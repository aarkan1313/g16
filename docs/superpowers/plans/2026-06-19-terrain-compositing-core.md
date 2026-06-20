# Terrain Compositing Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill the terrain's two visibly-broken layers — **blocky** material boundaries and a **flat/painted** surface — by replacing the nearest-index splat blend with a smooth **weight-blended, height-interlocked, organically-broken** compositing layer (Phase A) and adding real **parallax-occlusion relief + AO-driven BRDF** (Phase B), per `docs/superpowers/specs/2026-06-19-terrain-material-system-design.md`.

**Architecture:** The GPU splat bake (`SplatCompute` + `splat_weights.glsl`) already computes 7 smooth per-texel role weights but then *collapses* them to two integer indices written to one `filter_nearest` texture — that collapse + nearest filtering is the source of the blockiness. Phase A bakes the 7 weights into two `filter_linear` Rgba8 **weightmaps** (UE-Landscape style), and the fragment shader samples them smoothly, picks the **top-2** roles per pixel, and blends them by a **derived height channel** (the new material-height interface, derived from inverted-roughness first; real height maps are a documented seam) interlocked within a soft band, with a **world-space breakup** that folds in the existing `splat_warp` stopgap. Phase B adds **parallax-occlusion mapping** on the dominant triplanar plane (LOD-gated to the near band) using that same height channel, binds material **AO** into the custom `light()` BRDF, and strengthens the tangent-normal application. Everything sits behind toggles that **default to the current look**; the live look is gated by the user flying it at close/mid/far.

**Tech Stack:** Godot 4.6.2 mono; C# orchestration (`scripts/lab/TerrainLab.cs`, `scripts/lab/SplatCompute.cs`, `scripts/lab/TerrainLabUI.Apply.cs`); GLSL compute (`shaders/splat_weights.glsl`) + GLSL spatial shader (`shaders/terrain_lab.gdshader`); data-driven controls (`data/lab_controls.json`).

## Global Constraints

- **PILLARS (every choice):** quality = performance = AAA-ish = long-term-best, regardless of time cost. Lead with the correct option, not the cheap shortcut. "It works" is not the bar.
- **Hard perf target: 8 ms total frame** (world generator, long-term). This layer's budget rules: **top-2 blend only** · **POM LOD-gated near band only** · **all placement/weight masks baked once** (compute, never per-frame). Profile **in motion** with `--profmove` — static `--profile` hides SDFGI/shadow/cloud motion cost.
- **The user's eye is the only gate for look.** Mechanical checks (build/import/auto-shot/profile) gate *correctness and cost*, never *look*. Every phase ends with a live eye-gate at **close / mid / far**, in motion. Never judge a relief/blend/motion artifact from a still.
- **Behind toggles, defaulting to the current look.** Every new path ships OFF (or in legacy mode) so startup is pixel-identical to the approved look until the user approves the flip. `git checkout .` is the undo.
- **⚠ Shared working tree — god rays.** Another chat owns god rays in this same tree. Do **NOT** touch `GodRays*`, `shaders/godray*`, or god-ray rows in `data/lab_controls.json`/`data/cloud_presets.json`. This plan touches `terrain_lab.gdshader`, `splat_weights.glsl`, `SplatCompute.cs`, `TerrainLab.cs`, `TerrainLabUI.Apply.cs`, `data/lab_controls.json` — all shared except the two `splat*`/`Splat*` files; commit only this plan's hunks, and `git add -p` the shared files (never `git add -A` while the tree is intermingled).
- **Find seams by NAME/content, not line number** (other chats shift lines). Every `data/lab_controls.json` `"param"` row MUST name a real, existing shader uniform or it silently no-ops (lab-registry gotcha).
- **Custom-`light()` caveat:** `AO`/`AO_LIGHT_AFFECT` only reach the engine's ambient/GI under a custom `light()` — to darken the DIRECT term, AO must be carried on a varying and multiplied inside `light()` (Phase B Task B1).

## Environment & Godot 4.6 gotchas

- **ONE Godot at a time.** Before launching: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found"). A stale window shows OLD output.
- **ALWAYS** pass `--rendering-driver vulkan`.
- **Local-RD compute (`SplatCompute`) cannot run under `--headless`** — it returns null → NullRef. `--headless --import` only **compile-checks** shaders. Bakes/renders must be **windowed**.
- Windowed exe: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`. Console build (for `--auto-shot`/`--profile`/`--profmove`, still windowed): same path with `_console.exe`.
- After ANY `.cs`/shader/texture change: `dotnet build WG16.csproj` → then headless import:
  `"...Godot..._console.exe" --headless --path /c/Wg16/wg-16-project --import`
- Lab CLIs: `--auto-shot=<ABSOLUTE.png>` (launch, wait ~1.5 s, save PNG, quit), `--profile[=secs]`, `--profmove` (orbit during profile), `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--groundrules=1` (rule placement on), `--proxyres=N`.
- Launch the lab (absolute path — `.` opens the launcher):
  `"...Godot...exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`
- `_res = 2048` (`data/field_params.json` `heightmap_res`), `region_size = 8192`, `texel_world = 4`. At 2048² a single F32 RGBA texture is 64 MB — weightmaps **must be Rgba8** (32 MB for two), which is the AAA-standard weightmap precision anyway.

---

# PHASE A — Blend / compositing quality (de-block at the source)

Goal of the phase: material boundaries read as smooth, organic, relief-driven transitions instead of axis-aligned stair-stepped patches. Top-2 per pixel, smooth weights, height interlock, world-space breakup. Defaults keep the current (legacy index) look until the eye-gate.

## Task A1: Bake the 7 role weights into two linear Rgba8 weightmaps

The bake already computes a normalized `float w[7]` (both `role_weights` and `zone_weights`). Stop throwing it away: pack it into two Rgba8 textures sampled `filter_linear`, in addition to the existing legacy `splat_tex` (kept for A/B). No fragment change yet — this task only produces + binds the maps.

**Files:**
- Modify: `shaders/splat_weights.glsl` (add two output buffers; pack `w[7]` at the end of `main()`)
- Modify: `scripts/lab/SplatCompute.cs` (two extra output buffers; return the two new textures)
- Modify: `scripts/lab/TerrainLab.cs` (`RebakeSplat` — bind the two new textures)
- Modify: `shaders/terrain_lab.gdshader` (declare the two new samplers so the bind target exists)

**Interfaces:**
- Consumes: existing `float w[7]` computed in `splat_weights.glsl` `main()`; existing `SplatCompute.Bake` readback pattern.
- Produces:
  - `splat_weights.glsl`: two `std430 writeonly buffer` outputs at bindings 3 (`wa[]`) and 4 (`wb[]`), each one `uint` per texel (`packUnorm4x8`): `wa` = roles {0,1,2,3}, `wb` = roles {4,5,6,0-spare}.
  - `SplatCompute.cs`: `public readonly record struct BakeResult(ImageTexture Splat, ImageTexture WeightsA, ImageTexture WeightsB);` and `public BakeResult Bake(float[] heights, int res, Params p)`.
  - `terrain_lab.gdshader`: `uniform sampler2D splat_wa`, `uniform sampler2D splat_wb` (both `filter_linear, repeat_disable, hint_default_black`).

- [ ] **Step 1: Add the two output buffers to `splat_weights.glsl`.** After the `SplatOut` buffer block (binding 1), add:
```glsl
// Phase A: 7 smooth role weights packed into two Rgba8 weightmaps (filter_linear in
// the fragment) — the de-blocked replacement for the nearest-index splat path.
// wa = roles {0,1,2,3}; wb = roles {4,5,6, spare}. One packUnorm4x8 uint per texel.
layout(set = 0, binding = 3, std430) restrict writeonly buffer WOutA { uint wa[]; };
layout(set = 0, binding = 4, std430) restrict writeonly buffer WOutB { uint wb[]; };
```

- [ ] **Step 2: Pack `w[7]` at the end of `main()` in `splat_weights.glsl`.** Immediately before the existing final `splat[...] = vec4(...)` line, add (the `w` array is already normalized above):
```glsl
    // Phase A weightmaps (independent of the legacy dom/sec packing below).
    int wi = id.y*int(res)+id.x;
    wa[wi] = packUnorm4x8(vec4(w[0], w[1], w[2], w[3]));
    wb[wi] = packUnorm4x8(vec4(w[4], w[5], w[6], 0.0));
```

- [ ] **Step 3: Add the two output buffers + readback to `SplatCompute.Bake`.** In `scripts/lab/SplatCompute.cs`, after the `oBuf`/`oU` (binding 1) block, add two uint output buffers (4 bytes/cell each):
```csharp
        // Phase A weightmaps: one packed uint (Rgba8) per cell (bindings 3, 4)
        Rid waBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(uint)));
        var waU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
        waU.AddId(waBuf);
        Rid wbBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(uint)));
        var wbU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 };
        wbU.AddId(wbBuf);
```
Add them to the uniform set (change the `UniformSetCreate` array):
```csharp
        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { hU, oU, pU, waU, wbU }, _shader, 0);
```

- [ ] **Step 4: Read back + build the two Rgba8 textures, change the return type.** In `SplatCompute.cs`, replace the readback/free/return tail of `Bake` (from `byte[] outBytes = _rd.BufferGetData(oBuf);` through the `return ImageTexture...;`) with:
```csharp
        byte[] outBytes = _rd.BufferGetData(oBuf);
        byte[] waBytes = _rd.BufferGetData(waBuf);   // packed Rgba8, little-endian: R=w0,G=w1,B=w2,A=w3
        byte[] wbBytes = _rd.BufferGetData(wbBuf);
        _rd.FreeRid(set);
        _rd.FreeRid(hBuf);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);
        _rd.FreeRid(waBuf);
        _rd.FreeRid(wbBuf);

        Image splatImg = Image.CreateFromData(res, res, false, Image.Format.Rgbaf, outBytes);
        Image waImg = Image.CreateFromData(res, res, false, Image.Format.Rgba8, waBytes);
        Image wbImg = Image.CreateFromData(res, res, false, Image.Format.Rgba8, wbBytes);
        return new BakeResult(
            ImageTexture.CreateFromImage(splatImg),
            ImageTexture.CreateFromImage(waImg),
            ImageTexture.CreateFromImage(wbImg));
```
And change the method signature + add the struct. Change `public ImageTexture Bake(...)` to `public BakeResult Bake(...)`, and add above it:
```csharp
    /// Result of one bake: the legacy index map (splat_tex) + the two Phase-A weightmaps.
    public readonly record struct BakeResult(ImageTexture Splat, ImageTexture WeightsA, ImageTexture WeightsB);
```

- [ ] **Step 5: Bind the new textures in `TerrainLab.RebakeSplat`.** In `scripts/lab/TerrainLab.cs`, replace the `var tex = _splat.Bake(...); _mat.SetShaderParameter("splat_tex", tex);` lines with:
```csharp
        var baked = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", baked.Splat);
        _mat.SetShaderParameter("splat_wa", baked.WeightsA);
        _mat.SetShaderParameter("splat_wb", baked.WeightsB);
```

- [ ] **Step 6: Declare the samplers in `terrain_lab.gdshader`.** Next to the existing `uniform sampler2D splat_tex : filter_nearest, ...;` declaration, add:
```glsl
// Phase A weightmaps (filter_linear → smooth de-blocked weights). Roles packed:
// splat_wa = {0,1,2,3}, splat_wb = {4,5,6, spare}. Read by splat_blend_mode==1.
uniform sampler2D splat_wa : filter_linear, repeat_disable, hint_default_black;
uniform sampler2D splat_wb : filter_linear, repeat_disable, hint_default_black;
```

- [ ] **Step 7: Verify (correctness + cost; no look change expected).**
  - `dotnet build WG16.csproj` → Build succeeded, 0 errors.
  - `"...Godot..._console.exe" --headless --path /c/Wg16/wg-16-project --import` → imports clean; `splat_weights.glsl` + `terrain_lab.gdshader` compile (no `packUnorm4x8`/binding errors).
  - `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`; then windowed (bake runs):
    `"...Godot..._console.exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1 --auto-shot=C:/tmp/A1_after.png`
    **Expected:** identical to current look (fragment doesn't read the weightmaps yet); console prints `splat baked`; no crash, no GPU validation error about bindings 3/4.
  - `"...Godot..._console.exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1 --profmove --profile=5` → record ms; expect ~0 delta (bake-only change, two extra bake-time readbacks, no per-frame cost).
- [ ] **Step 8: Commit** (stage hunks individually — shared tree):
```bash
git add shaders/splat_weights.glsl scripts/lab/SplatCompute.cs scripts/lab/TerrainLab.cs
git add -p shaders/terrain_lab.gdshader   # stage only the two new sampler declarations
git commit -m "Compositing core A1: bake 7 role weights into two linear Rgba8 weightmaps"
```

---

## Task A2: Smooth-weight top-2 blend path + derived height channel + interlock

Add the de-blocked render path behind `splat_blend_mode` (default 0 = legacy/current). When `==1`: sample the weightmaps linearly, pick the top-2 roles, derive a per-material **height channel**, and blend the two by a height-interlock within a soft band. This is the core de-block.

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (uniforms, height helpers, weight read, top-2 blend branch)

**Interfaces:**
- Consumes: `splat_wa`/`splat_wb` (Task A1); existing `tri_w`, `tex_scale_m`, `trip_alb_by/trip_nrm_by/trip_rgh_by`, `heightblend_on`.
- Produces (used by A3 + Phase B):
  - `float material_height_uv(sampler2D rgh_t, sampler2D alb_t, vec2 uv, vec2 dx, vec2 dy)` — derived height in [0,1] for one plane uv.
  - `float height_by(int z, vec2 uv, vec2 dx, vec2 dy)` — height_uv dispatched by zone index.
  - `float material_height(int z, vec3 wp, vec3 nr)` — dominant-plane triplanar height for zone z.
  - `void splat_weights7(out float w[7], vec2 uv)` — the 7 smooth weights at uv.
  - `float interlock_blend(float hA, float hB, float t, float sharp)` — relief-aware weight of B in [0,1].
  - uniforms `int splat_blend_mode`, `bool height_from_rough`, `float interlock_sharp`, `float interlock_height_amp`.

- [ ] **Step 1: Add uniforms.** Near the splat uniforms block in `terrain_lab.gdshader` (after the `splat_warp_*`/`mix_strength` lines), add:
```glsl
// Phase A: smooth weight-blend path. 0 = legacy index path (current look) | 1 = weightmaps.
uniform int splat_blend_mode = 0;
// Derived material-height channel (the new "height" interface). true = 1-roughness
// (rough grit protrudes) | false = albedo luma. Real height maps: bind z*_hgt, sample there.
uniform bool height_from_rough = true;
// Height-interlock band: smaller = sharper relief-driven boundary; larger = softer crossfade.
uniform float interlock_sharp : hint_range(0.05, 0.9) = 0.35;
// How strongly the height channel drives the boundary (0 = pure weight crossfade).
uniform float interlock_height_amp : hint_range(0.0, 1.0) = 1.0;
```

- [ ] **Step 2: Add the height-channel helpers.** Place after `s_rgh` (the per-plane fetch helpers), before `vertex()`:
```glsl
// Phase A/B SHARED material-height channel. Derived per-plane (no triplanar) so POM can
// march it as a true 2D field; inverted roughness matches the existing heightblend intent.
float material_height_uv(sampler2D rgh_t, sampler2D alb_t, vec2 uv, vec2 dx, vec2 dy){
    if (height_from_rough){ return clamp(1.0 - textureGrad(rgh_t, uv, dx, dy).r, 0.0, 1.0); }
    return clamp(dot(textureGrad(alb_t, uv, dx, dy).rgb, vec3(0.299,0.587,0.114)), 0.0, 1.0);
}
float height_by(int z, vec2 uv, vec2 dx, vec2 dy){
    if(z==0) return material_height_uv(z0_rgh,z0_alb,uv,dx,dy); if(z==1) return material_height_uv(z1_rgh,z1_alb,uv,dx,dy);
    if(z==2) return material_height_uv(z2_rgh,z2_alb,uv,dx,dy); if(z==3) return material_height_uv(z3_rgh,z3_alb,uv,dx,dy);
    if(z==4) return material_height_uv(z4_rgh,z4_alb,uv,dx,dy); if(z==5) return material_height_uv(z5_rgh,z5_alb,uv,dx,dy);
    return material_height_uv(z6_rgh,z6_alb,uv,dx,dy);
}
// Dominant-plane triplanar height for interlock (cheap: one plane, the relief boundary
// only needs a representative height per material at this fragment).
float material_height(int z, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec2 uv = (bw.x>=bw.y && bw.x>=bw.z) ? wp.zy/tex_scale_m : (bw.y>=bw.z ? wp.xz/tex_scale_m : wp.xy/tex_scale_m);
    return height_by(z, uv, dFdx(uv), dFdy(uv));
}
// 7 smooth role weights at uv (linear-filtered weightmaps).
void splat_weights7(out float w[7], vec2 uv){
    vec4 a = texture(splat_wa, uv);
    vec4 b = texture(splat_wb, uv);
    w[0]=a.r; w[1]=a.g; w[2]=a.b; w[3]=a.a; w[4]=b.r; w[5]=b.g; w[6]=b.b;
}
// Relief-aware weight of B in [0,1]. Standard UE/Unity heightlerp WITHOUT the legacy
// 0.5 wash (heightblend_w softened toward linear t; here the height genuinely drives the
// boundary, controlled by interlock_sharp). t = secondary's smooth weight fraction.
float interlock_blend(float hA, float hB, float t, float sharp){
    float a = hA + (1.0 - t);
    float b = hB + t;
    float ma = max(a, b) - max(sharp, 1e-3);
    float wa2 = max(a - ma, 0.0);
    float wb2 = max(b - ma, 0.0);
    return clamp(wb2 / max(wa2 + wb2, 1e-5), 0.0, 1.0);
}
```

- [ ] **Step 3: Add the top-2 weight-blend branch in `fragment()`.** Inside `if (splat_on){ ... }`, wrap the existing legacy body in `if (splat_blend_mode == 0)` and add the `else` weightmap branch. Concretely, find the existing splat body (`vec2 swarp = ...` through the `alb = mix(... ); if(blend_mode>=1){...} else { rgh = 0.9; }`) and restructure to:
```glsl
    if (splat_on){
        if (splat_blend_mode == 0){
            // ---- LEGACY index path (current approved look). UNCHANGED. ----
            // (existing splat_warp lookup + dom/sec index read + heightblend_w blend)
            // ... keep the entire existing legacy body here verbatim ...
        } else {
            // ---- Phase A: smooth weightmap top-2 blend (de-blocked) ----
            // (A3 will warp this uv for organic breakup; A2 uses v_uv directly.)
            float sw[7]; splat_weights7(sw, v_uv);
            int d0=0; float m0=-1.0; for(int i=0;i<7;i++){ if(sw[i]>m0){ m0=sw[i]; d0=i; } }
            int d1=d0; float m1=-1.0; for(int i=0;i<7;i++){ if(i!=d0 && sw[i]>m1){ m1=sw[i]; d1=i; } }
            float t = clamp(m1 / max(m0+m1, 1e-5), 0.0, 1.0);   // 0 deep in d0 .. ~0.5 at boundary

            float hD = material_height(d0, wp, nr) * interlock_height_amp;
            float hS = material_height(d1, wp, nr) * interlock_height_amp;
            float m = heightblend_on ? interlock_blend(hD, hS, t, interlock_sharp) : t;

            if (splat_debug == 1){ dbg = 1; dbg_col = mix(zone_color(d0),zone_color(d1),m); }
            else if (splat_debug == 2){ dbg = 1; dbg_col = vec3(m); }

            alb = mix(trip_alb_by(d0,wp,nr), trip_alb_by(d1,wp,nr), m);
            if (blend_mode>=1){
                nrm = mix(trip_nrm_by(d0,wp,nr), trip_nrm_by(d1,wp,nr), m)*2.0-1.0;
                rgh = mix(trip_rgh_by(d0,wp,nr), trip_rgh_by(d1,wp,nr), m);
            } else { rgh = 0.9; }
        }
    } else {
        // ... existing 7-zone accumulate ZONE(...) block UNCHANGED ...
    }
```
  (Do NOT delete the legacy body — move it verbatim into the `splat_blend_mode==0` arm so default startup is pixel-identical.)

- [ ] **Step 4: Add the two controls to `data/lab_controls.json`** (Splat tab; both name real new uniforms):
```json
    { "id": "splat_blend_mode", "label": "blend: legacy/weightmap", "tab": "Splat", "type": "enum",
      "param": "splat_blend_mode", "options": ["0 legacy index", "1 weightmap"], "default": 0, "rand": false },
    { "id": "interlock_sharp", "label": "interlock sharpness", "tab": "Splat", "type": "slider",
      "param": "interlock_sharp", "min": 0.05, "max": 0.9, "default": 0.35, "rand": false },
    { "id": "interlock_height_amp", "label": "interlock height drive", "tab": "Splat", "type": "slider",
      "param": "interlock_height_amp", "min": 0.0, "max": 1.0, "default": 1.0, "rand": false },
    { "id": "height_from_rough", "label": "height = 1-rough (vs luma)", "tab": "Splat", "type": "toggle",
      "param": "height_from_rough", "default": true, "rand": false }
```
  (`enum` with a `param` routes through `_terrain.SetInt` — see `TerrainLabUI.Apply.cs` `case "enum"`; the registry builds an `OptionButton` from `"options"` and selects `"default"` — verified against the existing `splat_debug`/`tile_mode` rows, which is why this uses `options`, NOT min/max.)

- [ ] **Step 5: Verify (correctness + A/B look capture for the eye-gate).**
  - `dotnet build WG16.csproj` → 0 errors; `--headless --import` → clean (new helpers compile; `interlock_blend`/`material_height` unused-by-legacy is fine).
  - `taskkill ...`; capture **mid-range A/B** at the same camera, legacy then weightmap:
    `... scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1 --cam=0,120,0,-25,0 --auto-shot=C:/tmp/A2_legacy.png` (default mode 0)
    then a second run with `splat_blend_mode` default temporarily 1 (or drive via UI) → `C:/tmp/A2_weightmap.png`.
    **Expected (mechanical):** mode-0 shot == current look; mode-1 shot renders without crash, boundaries visibly **less axis-aligned/stair-stepped** than mode-0; no NaN/black patches. (Look quality is the user's gate, not this check.)
  - `--profmove --profile=5` in mode 1 at close range (`--cam=0,8,0,-30,0`): expect a small delta vs legacy (interlock adds ~1 dominant-plane height tap per material; top-2 fetch count is the same as legacy dom/sec). Record ms; must stay within the 8 ms posture.
- [ ] **Step 6: Commit:**
```bash
git add -p shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Compositing core A2: smooth top-2 weightmap blend + derived height interlock (behind splat_blend_mode)"
```

---

## Task A3: Organic breakup (fold in `splat_warp`, add interlock detail noise)

Make boundaries flow organically instead of following the weightmap grid, and crinkle the height-interlock so the rock/grass line is irregular rather than smooth. This folds the `splat_warp` stopgap into the real Blend layer (weight-domain warp) and adds a high-freq breakup to the interlock heights.

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (warp the weightmap uv; add breakup noise to interlock heights; uniform)

**Interfaces:**
- Consumes: `splat_weights7`, `interlock_blend`, `material_height` (A2); existing `vnoise`, `splat_warp_m`, `splat_warp_wl`.
- Produces: uniform `float interlock_breakup` (high-freq boundary crinkle amount).

- [ ] **Step 1: Add the breakup uniform** near the A2 uniforms:
```glsl
// Phase A organic breakup: high-freq world-noise added to the interlock heights so the
// relief boundary is crinkled/irregular, not a smooth contour. 0 = off.
uniform float interlock_breakup : hint_range(0.0, 0.6) = 0.25;
```

- [ ] **Step 2: Warp the weightmap uv (reuse the existing splat_warp noise).** In the `splat_blend_mode==1` branch, replace `splat_weights7(sw, v_uv);` with the warped lookup (same 2-octave warp the legacy path uses, so the knob does one consistent thing across both paths):
```glsl
            vec2 swarp = (vec2(vnoise(wp.xz + vec2(19.0,57.0), splat_warp_wl),
                               vnoise(wp.xz + vec2(83.0,11.0), splat_warp_wl)) - 0.5)
                       + 0.5 * (vec2(vnoise(wp.xz + vec2(5.0,99.0), splat_warp_wl*0.4),
                                     vnoise(wp.xz + vec2(61.0,7.0), splat_warp_wl*0.4)) - 0.5);
            float sw[7]; splat_weights7(sw, v_uv + swarp * (splat_warp_m / region_size));
```

- [ ] **Step 3: Crinkle the interlock heights with breakup noise.** In the same branch, replace the `hD`/`hS` lines with a per-material height plus an opposite-signed high-freq breakup (so the boundary weaves between the two materials):
```glsl
            float bk = (vnoise(wp.xz + vec2(127.0,311.0), tex_scale_m*1.7) - 0.5) * interlock_breakup;
            float hD = clamp(material_height(d0, wp, nr) * interlock_height_amp + bk, 0.0, 1.0);
            float hS = clamp(material_height(d1, wp, nr) * interlock_height_amp - bk, 0.0, 1.0);
```

- [ ] **Step 4: Add the control to `data/lab_controls.json`** (Splat tab):
```json
    { "id": "interlock_breakup", "label": "interlock breakup", "tab": "Splat", "type": "slider",
      "param": "interlock_breakup", "min": 0.0, "max": 0.6, "default": 0.25, "rand": false }
```

- [ ] **Step 5: Verify:**
  - `dotnet build` → 0 errors; `--headless --import` → clean.
  - `taskkill ...`; mode-1 close + mid A/B with breakup at 0 vs 0.25 (drive `interlock_breakup` via UI or temp default), same camera:
    `--cam=0,10,0,-30,0 --auto-shot=C:/tmp/A3_breakup0.png` and `C:/tmp/A3_breakup25.png`.
    **Expected (mechanical):** breakup-on boundary is irregular/crinkled vs the smooth contour at 0; no swimming at the conservative default; no seams from the uv warp (warp is on a linear-filtered map, so it stays smooth).
  - `--profmove --profile=5` mode-1 close range → noise taps are cheap; expect negligible delta vs A2. Record ms.
- [ ] **Step 6: Commit:**
```bash
git add -p shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Compositing core A3: organic breakup — weight-domain warp + interlock detail noise"
```

---

## Phase A eye-gate (USER, live — the look gate)

- [ ] Launch windowed, clouds off, rule placement on:
  `"...Godot...exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`
- [ ] Drive `splat_blend_mode` 0↔1 live (Splat tab) and fly **close / mid / far**, in motion. Judge:
  - Does mode 1 read **less blocky** than mode 0 (no axis-aligned stair-stepped material patches, no grid-snapped boundaries)?
  - Do `interlock_sharp` / `interlock_height_amp` / `interlock_breakup` give a believable relief-driven, organic boundary (rock poking through gravel, gravel settling into grass) without speckle/swimming in motion?
  - Any top-2 swap seam where a third material would belong? (Note for the top-3 escalation if objectionable — see Self-Review.)
- [ ] **On approval:** flip `splat_blend_mode` default to `1` (and `Shots.cs`/preset defaults if applicable), update `docs/NEEDS_REVIEW.md` + the HANDOFF Current State, then proceed to Phase B. **If not approved:** tune the three knobs live with the user before flipping; do not advance.

---

# PHASE B — Surface relief (parallax + AO-driven BRDF)

Goal of the phase: the surface reads as genuinely 3D up close (self-occluding relief) instead of a painted texture, and AO/normal/roughness genuinely drive the BRDF. POM on the dominant plane only, LOD-gated to the near band, using the Phase-A height channel. Adapted from the shelved `docs/superpowers/plans/2026-06-17-ground-unit3-surface-depth.md` to the new top-2 path.

## Task B1: Bind material AO and feed it to the custom BRDF

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (7 AO samplers, `s_ao`/`ao_by`, accumulate AO, `varying v_ao`, multiply in `light()`)
- Modify: `scripts/lab/TerrainLab.cs` (`SetZoneMaterial` — bind AO)

**Interfaces:**
- Consumes: existing `ar_sample_wp`, `tri_w`, the splat/7-zone branches; `LoadOr`.
- Produces: `float s_ao(sampler2D,vec3,vec3)`, `float ao_by(int,vec3,vec3)`, `varying float v_ao`, uniform `bool ao_on`, 7 `z*_ao` samplers.

- [ ] **Step 1: Add 7 AO samplers + toggle** after the `z6_rgh` declaration in `terrain_lab.gdshader`:
```glsl
uniform sampler2D z0_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool ao_on = true;   // Phase B: apply baked material AO to the BRDF
```

- [ ] **Step 2: Add AO fetch helpers** next to `s_rgh`:
```glsl
float s_ao(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    return ar_sample_wp(t,wp.zy/tex_scale_m,wp).r*bw.x
         + ar_sample_wp(t,wp.xz/tex_scale_m,wp).r*bw.y
         + ar_sample_wp(t,wp.xy/tex_scale_m,wp).r*bw.z;
}
float ao_by(int z, vec3 wp, vec3 nr){
    if(z==0) return s_ao(z0_ao,wp,nr); if(z==1) return s_ao(z1_ao,wp,nr);
    if(z==2) return s_ao(z2_ao,wp,nr); if(z==3) return s_ao(z3_ao,wp,nr);
    if(z==4) return s_ao(z4_ao,wp,nr); if(z==5) return s_ao(z5_ao,wp,nr);
    return s_ao(z6_ao,wp,nr);
}
```

- [ ] **Step 3: Add the varying** with the other `varying` declarations: `varying float v_ao;`

- [ ] **Step 4: Accumulate AO in `fragment()`.** Declare `float ao=1.0;` next to `alb/nrm/rgh`. In the legacy splat arm (`splat_blend_mode==0`) after `rgh = mix(rD, rS, m);` add `ao = mix(ao_by(dom,wp,nr), ao_by(sec,wp,nr), m);`. In the weightmap arm after `rgh = mix(trip_rgh_by(d0,...),...);` add `ao = mix(ao_by(d0,wp,nr), ao_by(d1,wp,nr), m);`. Change the 7-zone `ZONE` macro to accumulate AO:
```glsl
        #define ZONE(i,A,N,R,O) alb+=w[i]*s_alb(A,wp,nr); if(blend_mode>=1){ nrm+=w[i]*(s_nrm(N,wp,nr)*2.0-1.0); rgh+=w[i]*s_rgh(R,wp,nr); ao+=w[i]*s_ao(O,wp,nr);} else { rgh+=w[i]*0.9; ao+=w[i]; }
        ZONE(0,z0_alb,z0_nrm,z0_rgh,z0_ao)
        ZONE(1,z1_alb,z1_nrm,z1_rgh,z1_ao)
        ZONE(2,z2_alb,z2_nrm,z2_rgh,z2_ao)
        ZONE(3,z3_alb,z3_nrm,z3_rgh,z3_ao)
        ZONE(4,z4_alb,z4_nrm,z4_rgh,z4_ao)
        ZONE(5,z5_alb,z5_nrm,z5_rgh,z5_ao)
        ZONE(6,z6_alb,z6_nrm,z6_rgh,z6_ao)
```

- [ ] **Step 5: Write `AO`/`AO_LIGHT_AFFECT` + set the varying.** After the `ROUGHNESS = ...` line in `fragment()`:
```glsl
    ao = ao_on ? clamp(ao, 0.0, 1.0) : 1.0;
    AO = ao;
    AO_LIGHT_AFFECT = 1.0;   // engine ambient/GI only under a custom light(); direct term handled below
    v_ao = ao;               // carry to light() for the DIRECT term (M4)
```

- [ ] **Step 6: Multiply AO into the direct term in `light()`.** Append `* v_ao` to the existing accumulate lines:
```glsl
    DIFFUSE_LIGHT += ALBEDO * radiance * burley * v_ao;
    SPECULAR_LIGHT += radiance * (D * Vis) * F * v_ao;
```

- [ ] **Step 7: Bind AO in `TerrainLab.SetZoneMaterial`.** After the `z{zone}_rgh` line:
```csharp
        _mat.SetShaderParameter($"z{zone}_ao", LoadOr(b, "ao"));
```

- [ ] **Step 8: Verify:**
  - `dotnet build` → 0 errors; `--headless --import` → clean.
  - `taskkill ...`; A/B with `ao_on` true vs false (UI or temp default), close range:
    `--cam=0,8,0,-25,0 --auto-shot=C:/tmp/B1_ao_on.png` / `C:/tmp/B1_ao_off.png`.
    **Expected:** AO-on has deeper contact darkening in crevices/folds; NOT uniformly darker (white-default AO reads unchanged); no crash.
  - `--profmove --profile=5 --cam=0,8,0,-25,0` → AO adds ~3 single-channel taps × top-2; expect < ~0.4 ms close. Record ms.
- [ ] **Step 9: Commit:**
```bash
git add scripts/lab/TerrainLab.cs
git add -p shaders/terrain_lab.gdshader
git commit -m "Compositing core B1: bind material AO and feed it to the custom BRDF (direct + ambient)"
```

---

## Task B2: POM uniforms + controls (height source reuses the Phase-A channel)

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (POM uniforms)
- Modify: `data/lab_controls.json` (Detail tab controls)

**Interfaces:**
- Consumes: `material_height_uv` (A2 — the POM height source; do NOT add a second proxy).
- Produces: uniforms `bool pom_on`, `int pom_steps`, `float pom_scale_m`, `float pom_far_fade`.

- [ ] **Step 1: Add POM uniforms** after the `ar_*`/detail uniform block:
```glsl
// --- Surface relief / parallax-occlusion (Phase B) ---------------------------
uniform bool pom_on = false;                                 // master toggle (default OFF until eye-gate)
uniform int pom_steps : hint_range(0, 64) = 24;              // max march steps (close range)
uniform float pom_scale_m : hint_range(0.0, 0.5) = 0.06;     // displacement depth, meters
uniform float pom_far_fade : hint_range(0.0, 1.0) = 0.6;     // distanceWeight at which POM hits 0
```

- [ ] **Step 2: Add controls to `data/lab_controls.json`** (Detail tab):
```json
    { "id": "pom_on", "label": "surface depth (POM)", "tab": "Detail", "type": "toggle",
      "param": "pom_on", "default": false, "rand": false },
    { "id": "pom_steps", "label": "POM steps", "tab": "Detail", "type": "slider",
      "param": "pom_steps", "min": 0, "max": 64, "default": 24, "rand": false },
    { "id": "pom_scale_m", "label": "POM depth m", "tab": "Detail", "type": "slider",
      "param": "pom_scale_m", "min": 0.0, "max": 0.5, "default": 0.06, "rand": false },
    { "id": "pom_far_fade", "label": "POM fade @", "tab": "Detail", "type": "slider",
      "param": "pom_far_fade", "min": 0.0, "max": 1.0, "default": 0.6, "rand": false },
    { "id": "ao_on", "label": "material AO", "tab": "Detail", "type": "toggle",
      "param": "ao_on", "default": true, "rand": false }
```

- [ ] **Step 3: Verify:**
  - `dotnet build` → 0 errors; `--headless --import` → clean (uniforms compile, unused-yet OK).
  - `taskkill ...`; launch once → Detail tab shows the 5 controls; no crash; nothing visual changes yet (march wired in B3).
- [ ] **Step 4: Commit:**
```bash
git add -p shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Compositing core B2: POM uniforms + Detail-tab controls (height reuses A2 channel)"
```

---

## Task B3: POM ray-march on the dominant plane, LOD-gated, zone from top-2

The core relief. March a bounded ray in the dominant triplanar plane using the Phase-A height channel; offset `wp` so every later fetch parallaxes. Zone selection: weightmap mode → argmax(weights); legacy mode → `splat.r`; non-splat → height/slope bands.

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (`pom_offset` march + apply at top of `fragment()`)

**Interfaces:**
- Consumes: `material_height_uv` (A2), `tri_w`, `distanceWeight`, `cam_world`, `tex_scale_m`, `splat_weights7` (A2), `splat_tex`.
- Produces: `vec2 pom_offset(sampler2D rgh_t, sampler2D alb_t, vec3 wp, vec3 nr, vec3 vdir, out int dom)`.

- [ ] **Step 1: Add `pom_offset`** before `fragment()` (after `ar_sample_wp`):
```glsl
// Phase B: parallax-occlusion. Marches the DOMINANT triplanar plane only, using the
// Phase-A material height channel. Returns the in-plane uv offset (caller maps to world).
// dom out: 0=zy(normal x), 1=xz(normal y), 2=xy(normal z).
vec2 pom_offset(sampler2D rgh_t, sampler2D alb_t, vec3 wp, vec3 nr, vec3 vdir, out int dom){
    vec3 bw = tri_w(nr);
    dom = (bw.x >= bw.y && bw.x >= bw.z) ? 0 : (bw.y >= bw.z ? 1 : 2);
    vec2 uv; vec2 vp; float vdepth;
    if (dom == 0){ uv = wp.zy/tex_scale_m; vp = vdir.zy; vdepth = vdir.x; }
    else if (dom == 1){ uv = wp.xz/tex_scale_m; vp = vdir.xz; vdepth = vdir.y; }
    else { uv = wp.xy/tex_scale_m; vp = vdir.xy; vdepth = vdir.z; }

    float far = distanceWeight(wp);
    float lod = 1.0 - smoothstep(0.0, max(pom_far_fade, 1e-3), far);
    int steps = int(float(pom_steps) * lod);
    if (!pom_on || steps < 1 || pom_scale_m <= 0.0) return vec2(0.0);

    vec2 dx = dFdx(uv), dy = dFdy(uv);
    float vz = max(abs(vdepth), 0.05);                       // grazing → longer offset
    vec2 maxShift = (vp / vz) * (pom_scale_m / tex_scale_m);
    float dStep = 1.0 / float(steps);
    vec2 dUV = maxShift * dStep;
    vec2 curUV = uv; float rayH = 1.0;
    float h = material_height_uv(rgh_t, alb_t, curUV, dx, dy);
    for (int i = 0; i < 64; i++){
        if (i >= steps) break;
        if (rayH <= h) break;
        rayH -= dStep; curUV -= dUV;
        h = material_height_uv(rgh_t, alb_t, curUV, dx, dy);
    }
    vec2 prevUV = curUV + dUV;
    float hPrev = material_height_uv(rgh_t, alb_t, prevUV, dx, dy);
    float after  = h - rayH;
    float before = hPrev - (rayH + dStep);
    float wgt = after / max(after - before, 1e-4);
    vec2 off = mix(curUV, prevUV, wgt) - uv;
    float seam = smoothstep(0.0, 0.15, lod);                // thin fade only in last 15% before cutoff
    return off * seam;
}
```

- [ ] **Step 2: Apply POM at the top of `fragment()`** — right after `vec3 wp=v_world; vec3 nr=v_normal;` and BEFORE `zone_weights(...)`/the splat branch (so all fetches read the parallaxed `wp`):
```glsl
    if (pom_on){
        vec3 vdir = normalize(wp - cam_world);
        int hz = 0;
        if (splat_on){
            if (splat_blend_mode == 1){
                float pw[7]; splat_weights7(pw, v_uv);
                float mx=-1.0; for(int i=0;i<7;i++){ if(pw[i]>mx){ mx=pw[i]; hz=i; } }
            } else { hz = clamp(int(texture(splat_tex, v_uv).r + 0.5), 0, 6); }
        } else {
            if      (v_slope > slope_cliff_hi) hz = 4;
            else if (v_h > h_peak)             hz = 6;
            else if (v_h > h_high)             hz = 5;
            else if (v_slope > slope_cliff_lo) hz = 3;
            else if (v_h > h_slope)            hz = 2;
            else if (v_h > h_valley)           hz = 1;
            else                               hz = 0;
        }
        int domPlane; vec2 off = vec2(0.0);
        if      (hz==0) off = pom_offset(z0_rgh,z0_alb,wp,nr,vdir,domPlane);
        else if (hz==1) off = pom_offset(z1_rgh,z1_alb,wp,nr,vdir,domPlane);
        else if (hz==2) off = pom_offset(z2_rgh,z2_alb,wp,nr,vdir,domPlane);
        else if (hz==3) off = pom_offset(z3_rgh,z3_alb,wp,nr,vdir,domPlane);
        else if (hz==4) off = pom_offset(z4_rgh,z4_alb,wp,nr,vdir,domPlane);
        else if (hz==5) off = pom_offset(z5_rgh,z5_alb,wp,nr,vdir,domPlane);
        else            off = pom_offset(z6_rgh,z6_alb,wp,nr,vdir,domPlane);
        vec2 woff = off * tex_scale_m;
        if      (domPlane==0){ wp.z += woff.x; wp.y += woff.y; }
        else if (domPlane==1){ wp.x += woff.x; wp.z += woff.y; }
        else                 { wp.x += woff.x; wp.y += woff.y; }
    }
```

- [ ] **Step 3: Verify:**
  - `dotnet build` → 0 errors; `--headless --import` → shader compiles (constant `64` loop bound + `break` at `steps` — GLSL requires the constant bound).
  - `taskkill ...`; A/B close, `pom_on` on vs off (UI/temp default):
    `--cam=0,5,0,-35,0 --auto-shot=C:/tmp/B3_pom_on.png` / `C:/tmp/B3_pom_off.png`.
    **Expected (mechanical):** POM-on shows apparent depth/self-occlusion on gravel/rock vs flat off; no texture "swimming" at 24 steps / 0.06 m; silhouette edges unchanged (POM can't move geometry — expected).
  - **Far gate:** `--profmove --profile=5 --cam=0,400,0,-80,0` with pom_on vs off → expect ~0 delta (LOD gate fully off far). Then **close worst case** `--profmove --profile=5 --cam=0,4,0,-30,0` → record the close-range POM cost; must keep the frame within the 8 ms posture (drop `pom_steps` if not).
- [ ] **Step 4: Commit:**
```bash
git add -p shaders/terrain_lab.gdshader
git commit -m "Compositing core B3: dominant-plane parallax-occlusion march, LOD-gated, zone from top-2"
```

---

## Task B4: Strengthen normal-map application; confirm rough/normal reach the BRDF

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (the final `NORMAL` block)

**Interfaces:**
- Consumes: `distanceWeight`, existing `nrm`/`nr`, `blend_mode`, `dbg_use_normalmap`.
- Produces: (none new — math-only).

- [ ] **Step 1: Confirm the rough/normal path.** Audit the final-output block: `ROUGHNESS` routes through `dbg_fullrough` + `rough_floor` (default 0.5 → roughness IS reaching the BRDF); `NORMAL` applies the tangent normal only when `blend_mode>=1 && dbg_use_normalmap` (default `blend_mode=3` → applied). No code change for this step; note `dbg_use_normalmap=false` flattens it for bisection.

- [ ] **Step 2: Strengthen the normal-map application** (current `*0.8`, y-only). Replace the `if (blend_mode>=1 && dbg_use_normalmap){ ... } else { ... }` NORMAL block with:
```glsl
    if (blend_mode>=1 && dbg_use_normalmap){
        // apply the tangent-space normal across BOTH horizontal axes (was y-only) so
        // POM-revealed relief gets matching shading; stronger near, calm far (shares POM LOD intent).
        float nrNear = 1.0 - distanceWeight(wp);
        vec3 wn = normalize(nr + vec3(nrm.x, 0.0, nrm.y) * mix(0.4, 1.0, nrNear));
        NORMAL = normalize((VIEW_MATRIX*vec4(wn,0.0)).xyz);
    } else {
        NORMAL = normalize((VIEW_MATRIX*vec4(nr,0.0)).xyz);
    }
```

- [ ] **Step 3: Verify:**
  - `dotnet build` → 0 errors; `--headless --import` → clean.
  - `taskkill ...`; A/B close, Debug `normal maps` on vs off:
    `--cam=0,6,0,-35,0 --auto-shot=C:/tmp/B4_nrm_on.png` / `C:/tmp/B4_nrm_off.png`.
    **Expected:** on shows directional micro-shading consistent with POM relief; off goes flat (proves the map drives the BRDF). `dbg_fullrough` still kills specular.
  - `--profmove --profile=5 --cam=0,6,0,-35,0` → math-only edit, ~0 delta vs B3. Record ms.
- [ ] **Step 4: Commit:**
```bash
git add -p shaders/terrain_lab.gdshader
git commit -m "Compositing core B4: distance-aware normal-map application; confirm rough/normal reach BRDF"
```

---

## Phase B eye-gate (USER, live — the look gate)

- [ ] Launch windowed (clouds off, rule placement on, Phase-A weightmap mode on if approved):
  `"...Godot...exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`
- [ ] Drive `pom_on` and `ao_on` (Detail tab) live and fly **close / mid / far**, in motion. Judge:
  - Up close: real relief/self-occlusion vs the old flat/painted look? Any **swimming** of the parallax in motion, or a fade **pop** at the near→mid boundary?
  - Does AO add believable contact darkening without going muddy? Do normal + roughness clearly drive the surface (use Debug `normal maps` / `force matte` to bisect)?
  - Far: unchanged (POM gated off)?
- [ ] **On approval:** flip `pom_on` default to `true` (and any preset/`Shots.cs` defaults), update `docs/NEEDS_REVIEW.md` + HANDOFF Current State. Then the compositing core is complete → next arc is **Procedural breakup (Unit 4)**, then **Macro color (Unit 5)** per the spec. **If not approved:** tune `pom_steps`/`pom_scale_m`/`pom_far_fade` live; if relief can't read well from derived height, surface the **real-height-map seam** decision (spec Ceiling seam #1) to the user rather than grinding.

---

## Self-Review notes

**Spec coverage.**
- *Blocky → smooth weights, de-block at the source* → A1 (bake 7 weights to linear weightmaps, stop collapsing to nearest indices) + A2 (linear sample → top-2). The nearest-index `filter_nearest` texture is bypassed in weightmap mode.
- *Height-map interlocking (real heightblend, replace the roughness-proxy hack)* → A2 `material_height`/`interlock_blend` (dedicated derived height channel, no 0.5 wash). Real-height-map upgrade documented in `material_height_uv` (bind `z*_hgt`).
- *Organic breakup* → A3 (weight-domain warp folding in `splat_warp`; high-freq interlock breakup noise).
- *Top-2 materials per pixel* → A2 top-2 select; top-3+ noted as the later lever in the eye-gate.
- *Surface relief: POM LOD-gated near, from the height channel* → B2/B3 (`pom_offset`, `lod = 1 - smoothstep(0, pom_far_fade, far)`, steps + offset scale to 0; reuses A2's `material_height_uv`).
- *normal/roughness/AO genuinely drive the BRDF* → B1 (AO bind + carried into `light()` for the direct term — the custom-`light()` caveat) + B4 (normal strengthened, roughness path confirmed).
- *Material-sample height channel (derived first, seam for real maps)* → A2 `material_height_uv` (the single derived-height interface shared by interlock + POM).
- *Perf rules: top-2 · POM near-only · masks baked once · profile in motion* → top-2 throughout; POM LOD gate (B3); weightmaps baked once in `SplatCompute` (A1); every verify uses `--profmove`.
- *Ceiling seams designed-for, deferred* → real height maps (the `material_height_uv` seam) + RVT (not in scope; this pipeline is exactly what RVT would cache later). Both explicitly NOT built here, matching the spec's "NOT doing now".

**Methodology deviation (intentional).** No xUnit/TDD failing-test cycle: this is GLSL look work and the project's hard rule is "the user's eye is the only gate; mechanical checks don't decide look." Each task's cycle is build → headless-import compile → windowed auto-shot A/B → `--profmove` profile, capped by a per-phase live eye-gate. This matches the established Unit-3 plan convention in this repo.

**Placeholder scan.** No TODO/placeholder code — every GLSL/C#/JSON block is concrete. Loop uses a constant `64` upper bound with `break` at `steps` (GLSL requires a constant bound). The "keep the legacy body verbatim" instruction in A2 Step 3 refers to existing committed code, not a placeholder.

**Type consistency.** `Bake` returns `BakeResult` (A1) consumed in `RebakeSplat` (A1). `material_height_uv`/`height_by`/`material_height`/`splat_weights7`/`interlock_blend` defined in A2, reused by name in A3 (breakup) and B3 (POM). `pom_offset` signature in B3 matches its call sites. `v_ao` declared, set in `fragment()`, read in `light()` (B1). Uniform names in `data/lab_controls.json` `param` rows (`splat_blend_mode`, `interlock_sharp`, `interlock_height_amp`, `height_from_rough`, `interlock_breakup`, `pom_on`, `pom_steps`, `pom_scale_m`, `pom_far_fade`, `ao_on`) each match a uniform declared in this plan (lab-registry gotcha guarded).

**Known limitations (escalation paths).** (1) Top-2 swap seam where a third role would belong — accepted per spec; top-3 blend is the documented later lever. (2) Dominant-plane-only POM has a discontinuity on ~45° triplanar blends; per-plane POM (3× cost) is the escalation if objectionable at close range. (3) POM cannot move silhouettes/horizon — universal POM limitation; tessellation is the only fix and Godot 4.6 lacks it. (4) Derived (inverted-roughness) height may not carry strong relief for some materials → the real-height-map seam (spec Ceiling #1) is the user-decision escalation.

**Risk / undo.** Presenter-only (base field/lighting/clouds/god-rays untouched); every path behind a toggle defaulting to the current look; `git checkout .` reverts. Shared-tree discipline: `git add -p` the shared files, never touch god-ray files.
```