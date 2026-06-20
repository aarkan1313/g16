# Ground Unit 3 — Surface Depth (Parallax) Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: superpowers:executing-plans — read it and check the box before starting.
> - [ ] I have read superpowers:executing-plans and will follow the per-task review/commit gates.

**Goal:** Add real, up-close surface relief to the WG16 ground via parallax-occlusion mapping (POM) and make normal/roughness/AO genuinely drive the BRDF, killing the flat/plasticky look — without tessellation (Godot 4.6 has none).

**Architecture:** POM is a bounded ray-march in the dominant triplanar plane's 2D space, executed per-fragment inside `terrain_lab.gdshader` BEFORE `ar_sample_wp`, offsetting the UV by the parallax displacement. Height is derived from a material proxy (inverted roughness / albedo luminance — the same relief signal the existing height-blend already trusts), with a documented real-height-map upgrade path. POM step count and contribution are LOD-gated by `distanceWeight(wp)` so they ramp to zero by `ar_far_m`; the BRDF fix wires a real per-fragment tangent-space normal, roughness, and a newly-bound AO map into `NORMAL`/`ROUGHNESS`/`AO`.

**Tech Stack:** Godot 4.6.2 mono, C# orchestration (`scripts/lab/TerrainLab.cs`, `TerrainLabUI.cs`), GLSL spatial shader (`shaders/terrain_lab.gdshader`), data-driven controls (`data/lab_controls.json`). POM stays in the spatial fragment shader (per-fragment work — no compute pre-pass is justified here; see Self-Review PERF).

> **Cross-cutting (all ground units):** find seam functions by NAME/content, not line number (other units shift them). Registry `param` rows silently no-op if the uniform doesn't exist — every `param` here names a real new uniform. **Custom-`light()` caveat (this unit relies on it):** `AO_LIGHT_AFFECT` does NOT reach the direct term under a custom `light()` — AO must be multiplied inside `light()` via a varying (handled in Task 1).

---

## Environment & Godot 4.6 gotchas

- **ONE Godot at a time.** Before launching: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found").
- **ALWAYS** pass `--rendering-driver vulkan`. Local-`RenderingDevice` compute (SplatCompute) **cannot run under `--headless`**; the auto-shot/profile runs are windowed Vulkan, not headless.
- After ANY change: `dotnet build WG16.csproj` then headless import:
  `"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --headless --import` (import only — it does not render).
- Console exe path (for `--auto-shot` / `--profile`, windowed): same exe **without** `--headless`, with `--rendering-driver vulkan`.
- Lab CLIs: `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<ABSOLUTE.png>`.
- **Never judge a relief/motion artifact from a still** — POM swims under camera motion if step count is too low or height-scale too high; the real gate is the user flying it live, close range.
- Controls are data-driven in `data/lab_controls.json`. Add Unit-3 controls to a new **"Surface Depth"** group (reuse the `Detail` tab; `slider`/`toggle` with `"param"` mapping straight to the uniform).
- POM is the most perf-sensitive unit in the arc — `--profile` every task and watch ms at close range where step count is highest.

---

## Task 1 — Bind a real AO map (BRDF foundation)

Currently `TerrainLab.SetZoneMaterial` binds only `alb`/`nrm`/`rgh`; the shader has no AO sampler and `LoadOr` would fall AO back to albedo anyway. Bind AO explicitly so the BRDF gets occlusion (most "flat" comes from AO never reaching `AO`).

**Files:** `shaders/terrain_lab.gdshader`, `scripts/lab/TerrainLab.cs`

- [ ] Add 7 AO samplers after the rgh block (~line 34) in `terrain_lab.gdshader`:
```glsl
uniform sampler2D z0_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool ao_on = true;   // Unit 3: apply baked material AO to the BRDF
```
- [ ] Add an AO fetch helper next to `s_rgh` (~line 285) — single-channel, triplanar, through `ar_sample_wp` so it shares anti-repetition + LOD:
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
- [ ] In `fragment()`, declare `float ao=1.0;` next to `alb/nrm/rgh` (~line 463) and accumulate AO. In the splat branch (~line 486) add after `rgh = mix(rD, rS, m);`:
```glsl
            ao = mix(ao_by(dom,wp,nr), ao_by(sec,wp,nr), m);
```
and in the 7-zone `ZONE` macro (~line 493) change it to also accumulate AO:
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
- [ ] Write `AO` near the final outputs (~line 526, after `ROUGHNESS`). **Audit M4:** this shader has a CUSTOM `light()`, and `AO`/`AO_LIGHT_AFFECT` only fold AO into the *built-in* lighting + the engine's ambient/GI path — a custom `light()` does NOT see `AO_LIGHT_AFFECT` for the DIRECT term. So writing `AO` here darkens **ambient/GI only**; to darken **direct** light we must pass AO into `light()` and multiply there (next bullet). Set both anyway for the ambient/GI contribution:
```glsl
    AO = ao_on ? clamp(ao, 0.0, 1.0) : 1.0;
    AO_LIGHT_AFFECT = 1.0;   // affects ENGINE ambient/GI; direct term handled in light()
```
- [ ] **Pass AO to `light()` for the direct term (M4 fix).** `light()` runs separately and can't read the `ao` local from `fragment()`, so carry it on a varying. Add `varying float v_ao;` with the other varyings (~line 134), set it in `fragment()` right after computing `ao` (e.g. `v_ao = ao_on ? clamp(ao,0.0,1.0) : 1.0;`), and in the custom `light()` multiply the direct contributions by it — change the `DIFFUSE_LIGHT +=` / `SPECULAR_LIGHT +=` lines to include `* v_ao`:
```glsl
    // in light(): material AO darkens the DIRECT sun term too (not just ambient/GI)
    DIFFUSE_LIGHT  += ... * v_ao;
    SPECULAR_LIGHT += ... * v_ao;
```
  (Apply `* v_ao` to the existing diffuse/specular accumulate expressions; don't rewrite the BRDF.)
- [ ] In `TerrainLab.cs` `SetZoneMaterial` (~line 90) add the AO bind:
```csharp
        _mat.SetShaderParameter($"z{zone}_ao", LoadOr(b, "ao"));
```
- [ ] **Verify:**
  - `dotnet build WG16.csproj` → Build succeeded, 0 errors.
  - `...console.exe --headless --import` → imports clean, no shader compile error.
  - `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` then windowed:
    `...console.exe --rendering-driver vulkan --cam=0,40,0,-20,0 --auto-shot=C:/tmp/u3_t1_ao_on.png`
    then toggle off (set `ao_on=false` default temporarily OR use a debug run) for `C:/tmp/u3_t1_ao_off.png`.
    **Expected:** AO-on shot has visibly deeper contact shadow in crevices/folds vs off; no crash; not uniformly darker (white-default materials read unchanged).
  - `...console.exe --rendering-driver vulkan --profile=5 --cam=0,18,0,-10,0` → record ms; AO adds ~3 single-channel taps, expect < 0.3 ms delta close range.
- [ ] **Commit:** `git add -A && git commit -m "Unit 3 task 1: bind material AO and feed it to the BRDF"`

---

## Task 2 — Height proxy + POM uniforms and controls

Define the height source and the POM knobs before the march, so the march code in Task 3 is self-contained. No dedicated height map exists in the 108-material library, so derive height per-plane from a material proxy.

**Files:** `shaders/terrain_lab.gdshader`, `data/lab_controls.json`

- [ ] Add POM uniforms after the `ar_*` block (~line 42) in `terrain_lab.gdshader`:
```glsl
// --- Surface depth / parallax-occlusion (Unit 3) -----------------------------
uniform bool pom_on = true;                                   // master toggle
uniform int pom_steps : hint_range(0, 64) = 24;               // max march steps (close range)
uniform float pom_scale_m : hint_range(0.0, 0.5) = 0.06;      // displacement depth, meters
uniform float pom_far_fade : hint_range(0.0, 1.0) = 0.6;      // distanceWeight at which POM hits 0
uniform bool pom_proxy_invrgh = true;                         // height = 1-roughness (true) or albedo luma
```
- [ ] Add the per-plane height proxy fetch near `s_rgh` (~line 288). Height is sampled through the SAME tiling path so the relief matches the lit texture. Inverted roughness = "rough grit protrudes" (matches the existing height-blend convention, lines 118-124):
```glsl
// Unit 3 height proxy in [0,1] for a single 2D plane uv. NO triplanar here — POM
// marches one plane (the dominant one) so it stays a true 2D heightfield.
// Real-height-map upgrade: bind z*_hgt and sample it here instead of the proxy.
float pom_height_at(sampler2D rgh_t, sampler2D alb_t, vec2 uv, vec2 dx, vec2 dy){
    if (pom_proxy_invrgh){
        float r = textureGrad(rgh_t, uv, dx, dy).r;
        return clamp(1.0 - r, 0.0, 1.0);          // matte/rough = high relief
    }
    vec3 a = textureGrad(alb_t, uv, dx, dy).rgb;
    return clamp(dot(a, vec3(0.299,0.587,0.114)), 0.0, 1.0);   // luma proxy
}
```
- [ ] Add Unit-3 controls to `data/lab_controls.json` in the `Detail` tab (after the `snow_dust_amp` row ~line 53):
```json
    { "id": "pom_on", "label": "surface depth (POM)", "tab": "Detail", "type": "toggle",
      "param": "pom_on", "default": true, "rand": false },
    { "id": "pom_steps", "label": "POM steps", "tab": "Detail", "type": "slider",
      "param": "pom_steps", "min": 0, "max": 64, "default": 24, "rand": false },
    { "id": "pom_scale_m", "label": "POM depth m", "tab": "Detail", "type": "slider",
      "param": "pom_scale_m", "min": 0, "max": 0.5, "default": 0.06, "rand": false },
    { "id": "pom_far_fade", "label": "POM fade @", "tab": "Detail", "type": "slider",
      "param": "pom_far_fade", "min": 0, "max": 1, "default": 0.6, "rand": false },
    { "id": "ao_on", "label": "material AO", "tab": "Detail", "type": "toggle",
      "param": "ao_on", "default": true, "rand": false },
```
- [ ] **Verify:**
  - `dotnet build WG16.csproj` → 0 errors.
  - `...console.exe --headless --import` → clean (uniforms compile; `pom_height_at` unused-yet is fine).
  - Launch windowed once (`--rendering-driver vulkan --cam=0,18,0,-10,0`) → the Detail tab shows the 5 new controls; no crash. (Nothing visual changes yet — march wired in Task 3.)
- [ ] **Commit:** `git add -A && git commit -m "Unit 3 task 2: POM uniforms, height proxy, and Detail-tab controls"`

---

## Task 3 — POM ray-march on the dominant plane, LOD-gated

The core. March a bounded ray in the dominant triplanar plane's 2D UV, find the height intersection, and offset the UV. Apply that offset to ALL material fetches at this fragment so albedo/normal/rough/AO all read the parallaxed position. POM runs on the **dominant plane only** (the one with the largest triplanar weight) — full per-plane POM triples cost for sub-pixel gain on the two minor planes (per pillars, the full path is noted in Self-Review; dominant-plane is the chosen quality/perf sweet spot).

**Files:** `shaders/terrain_lab.gdshader`

- [ ] Add the POM march function before `fragment()` (after `ar_sample_wp`, ~line 267). It picks the dominant plane from the triplanar weights, builds a view ray in that plane's 2D space, marches front-to-back, and refines with one linear interpolation between the last two steps (standard POM):
```glsl
// Unit 3: parallax-occlusion. Returns a WORLD-SPACE UV offset to add to wp.xz-style
// plane coords. Marches the DOMINANT triplanar plane only. dom: 0=x(zy),1=y(xz),2=z(xy).
// Returns the 2D shift to apply to that plane's uv (in tex_scale_m units already).
vec2 pom_offset(sampler2D rgh_t, sampler2D alb_t, vec3 wp, vec3 nr, vec3 vdir, out int dom){
    vec3 bw = tri_w(nr);
    dom = (bw.x >= bw.y && bw.x >= bw.z) ? 0 : (bw.y >= bw.z ? 1 : 2);

    // plane uv + the 2D view direction projected into that plane (the in-plane axes),
    // AND the DEPTH axis = the component along the plane's normal (audit H1: the parallax
    // depth divisor must be the axis PERPENDICULAR to the plane, not always vdir.y).
    //   dom 0 = zy plane (normal = x) → depth = vdir.x
    //   dom 1 = xz plane (normal = y) → depth = vdir.y
    //   dom 2 = xy plane (normal = z) → depth = vdir.z
    vec2 uv; vec2 vp; float vdepth;
    if (dom == 0){ uv = wp.zy / tex_scale_m; vp = vdir.zy; vdepth = vdir.x; }
    else if (dom == 1){ uv = wp.xz / tex_scale_m; vp = vdir.xz; vdepth = vdir.y; }
    else { uv = wp.xy / tex_scale_m; vp = vdir.xy; vdepth = vdir.z; }

    float far = distanceWeight(wp);
    // LOD gate: full steps near, ramp to 0 by pom_far_fade.
    float lod = 1.0 - smoothstep(0.0, max(pom_far_fade, 1e-3), far);
    int steps = int(float(pom_steps) * lod);
    if (!pom_on || steps < 1 || pom_scale_m <= 0.0) return vec2(0.0);

    vec2 dx = dFdx(uv), dy = dFdy(uv);
    // depth into the surface along THIS plane's normal (grazing → longer offset).
    // Correct per-plane (H1); was hardcoded vdir.y which only suited the ground plane.
    float vz = max(abs(vdepth), 0.05);
    vec2 maxShift = (vp / vz) * (pom_scale_m / tex_scale_m);

    float dStep = 1.0 / float(steps);
    vec2 dUV = maxShift * dStep;
    vec2 curUV = uv; float rayH = 1.0;
    float h = pom_height_at(rgh_t, alb_t, curUV, dx, dy);
    // march until the ray depth drops below the surface height
    for (int i = 0; i < 64; i++){
        if (i >= steps) break;
        if (rayH <= h) break;
        rayH -= dStep;
        curUV -= dUV;
        h = pom_height_at(rgh_t, alb_t, curUV, dx, dy);
    }
    // refine: linear blend between last two samples (where ray crossed surface)
    vec2 prevUV = curUV + dUV;
    float hPrev = pom_height_at(rgh_t, alb_t, prevUV, dx, dy);
    float after  = h - rayH;
    float before = hPrev - (rayH + dStep);
    float wgt = after / max(after - before, 1e-4);
    vec2 finalUV = mix(curUV, prevUV, wgt);
    vec2 off = finalUV - uv;
    // Single fade (audit M1): step-count already drops with lod for COST; do NOT also
    // multiply the offset by lod (that squared the falloff). Only soft-fade the offset
    // in the last sliver before the cutoff so the switch-off is seamless, not lod^2.
    float seam = smoothstep(0.0, 0.15, lod);   // ~0 only in the final 15% before cutoff
    return off * seam;
}
```
- [ ] In `fragment()` (~line 460) compute the offset once and apply it to `wp`. Right after `vec3 wp=v_world; vec3 nr=v_normal;`:
```glsl
    // Unit 3: parallax-occlusion. Use the DOMINANT zone's rough/albedo as the height
    // field; shift the world pos along the dominant plane so every later fetch parallaxes.
    if (pom_on){
        vec3 vdir = normalize(wp - cam_world);          // view ray, world space
        // Height-field zone selection. SPLAT path (the default): use the baked dominant
        // zone — correct. NON-splat 7-zone path: pick the zone by the same height/slope
        // bands the accumulate uses, so cliffs/peaks parallax with THEIR relief, not
        // valley grass (audit M2). Cheap height/slope classify (matches zone_weights order).
        int hz = 0;
        if (splat_on){ vec4 spd = texture(splat_tex, v_uv); hz = clamp(int(spd.r+0.5),0,6); }
        else {
            // approximate dominant zone from height + slope (h_valley/h_slope/h_high/h_peak,
            // slope_cliff_*). Mirrors the band logic so POM relief matches the lit material.
            if      (v_slope > slope_cliff_hi)            hz = 4;             // cliff
            else if (v_h > h_peak)                        hz = 6;             // peak/snow
            else if (v_h > h_high)                        hz = 5;             // high
            else if (v_slope > slope_cliff_lo)            hz = 3;             // slope→cliff
            else if (v_h > h_slope)                       hz = 2;             // slope
            else if (v_h > h_valley)                      hz = 1;             // valley→slope
            else                                          hz = 0;             // valley
        }
        int domPlane;
        vec2 off = vec2(0.0);
        // pick the height textures by zone (samplers can't be indexed)
        if      (hz==0) off = pom_offset(z0_rgh,z0_alb,wp,nr,vdir,domPlane);
        else if (hz==1) off = pom_offset(z1_rgh,z1_alb,wp,nr,vdir,domPlane);
        else if (hz==2) off = pom_offset(z2_rgh,z2_alb,wp,nr,vdir,domPlane);
        else if (hz==3) off = pom_offset(z3_rgh,z3_alb,wp,nr,vdir,domPlane);
        else if (hz==4) off = pom_offset(z4_rgh,z4_alb,wp,nr,vdir,domPlane);
        else if (hz==5) off = pom_offset(z5_rgh,z5_alb,wp,nr,vdir,domPlane);
        else            off = pom_offset(z6_rgh,z6_alb,wp,nr,vdir,domPlane);
        // map the 2D plane offset (in uv units) back to a world-space xz/xy/zy shift
        vec2 woff = off * tex_scale_m;
        if      (domPlane==0){ wp.z += woff.x; wp.y += woff.y; }
        else if (domPlane==1){ wp.x += woff.x; wp.z += woff.y; }
        else                 { wp.x += woff.x; wp.y += woff.y; }
    }
```
  (All subsequent zone fetches already use `wp`, so they automatically read the parallaxed position. The geometric normal `nr` is intentionally left un-shifted — POM moves texture lookups, not the surface normal basis.)
- [ ] **Verify:**
  - `dotnet build WG16.csproj` → 0 errors.
  - `...console.exe --headless --import` → shader compiles (watch for loop/`break` warnings; constant `64` upper bound is required — GLSL needs a constant loop bound).
  - `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`, then A/B close range:
    `...console.exe --rendering-driver vulkan --cam=0,6,0,-35,0 --auto-shot=C:/tmp/u3_t3_pom_on.png`
    then with `pom_on` default false (or via UI toggle run) `C:/tmp/u3_t3_pom_off.png`.
    **Expected:** POM-on shows gravel/rock reading as 3D with self-occlusion and motion parallax (judge live, not the still); silhouette edges unchanged (POM can't move them — expected caveat). No texture "swimming" at default 24 steps / 0.06 m.
  - `...console.exe --rendering-driver vulkan --profile=5 --cam=0,4,0,-30,0` (worst case — close, grazing) vs `--cam=0,400,0,-80,0` (far, POM gated off). **Expected:** close adds the bulk of POM cost; far should be ~0 delta vs `pom_on=false` (LOD gate working). Record both ms.
- [ ] **Commit:** `git add -A && git commit -m "Unit 3 task 3: dominant-plane parallax-occlusion march, LOD-gated by distanceWeight"`

---

## Task 4 — Verify normal/roughness drive the BRDF; expose POM debug

Confirm the existing normal/roughness path is actually applied (the spec flags a "plasticky" look), and make POM independently bisectable via the existing Debug toggles.

**Files:** `shaders/terrain_lab.gdshader`

- [ ] Audit the final-output block (~lines 525-534). Confirm:
  - `ROUGHNESS` already routes through `dbg_fullrough` and `rough_floor` (line 526) — leave as-is; note `rough_floor` default 0.5 means roughness IS reaching the BRDF.
  - `NORMAL` is only the detailed normal when `blend_mode>=1 && dbg_use_normalmap` (lines 529-533) — confirm `blend_mode` default is 3 (full stack), so the normal map IS applied by default. Document that `dbg_use_normalmap=false` (Debug tab) flattens it for bisection.
- [ ] Strengthen the normal-map application so relief reads (current `*0.8` y-only perturbation under-uses the tangent normal). Replace lines 529-533:
```glsl
    if (blend_mode>=1 && dbg_use_normalmap){
        // apply the tangent-space normal map across BOTH horizontal axes (was y-only),
        // so POM-revealed relief gets matching shading. Strength tied to nearness so far
        // ground stays calm (shares the POM LOD intent).
        float nrNear = 1.0 - distanceWeight(wp);
        vec3 wn = normalize(nr + vec3(nrm.x, 0.0, nrm.y) * mix(0.4, 1.0, nrNear));
        NORMAL = normalize((VIEW_MATRIX*vec4(wn,0.0)).xyz);
    } else {
        NORMAL = normalize((VIEW_MATRIX*vec4(nr,0.0)).xyz);
    }
```
- [ ] **Verify:**
  - `dotnet build WG16.csproj` → 0 errors; `...console.exe --headless --import` → clean.
  - `...console.exe --rendering-driver vulkan --cam=0,6,0,-35,0 --auto-shot=C:/tmp/u3_t4_normalmap_on.png` then a run with Debug `normal maps` off → `C:/tmp/u3_t4_normalmap_off.png`. **Expected:** on shows directional micro-shading consistent with POM relief; off goes flat (proves the map is driving the BRDF). Force-matte toggle (`dbg_fullrough`) kills specular as before.
  - `...console.exe --rendering-driver vulkan --profile=5 --cam=0,4,0,-30,0` → no meaningful change vs Task 3 (math-only edit). Record ms.
- [ ] **Commit:** `git add -A && git commit -m "Unit 3 task 4: distance-aware normal-map application; confirm rough/normal reach BRDF"`

---

## Self-Review notes

**Spec coverage.**
- POM as the tessellation substitute, LOD-gated OFF at distance via `distanceWeight` → Task 3 (`lod = 1 - smoothstep(0, pom_far_fade, far)`, steps and offset both scale to 0).
- Height derived from the material set (no dedicated height map in the 108-lib) → Task 2 `pom_height_at` (inverted-roughness default, albedo-luma alt), matching the existing height-blend relief convention (shader lines 118-124). Real-height-map upgrade path documented in `pom_height_at`'s comment (bind `z*_hgt`, sample there).
- normal/roughness/AO genuinely drive the BRDF → Task 1 (AO bind + `AO`/`AO_LIGHT_AFFECT`), Task 4 (normal-map strengthened, roughness path confirmed).
- POM offsets UV BEFORE the `ar_sample_wp` fetch → Task 3 shifts `wp` at the top of `fragment()`; all later zone fetches use `wp`, so the parallax is upstream of every `ar_sample_wp` call.
- Triplanar complication addressed → dominant-plane-only POM (Task 3), with the full per-plane path explicitly noted as the higher-cost option below.
- Locked interfaces used: `ar_sample_wp` (Task 1 AO fetch), `distanceWeight` (Tasks 3-4 gate), `cam_world` (Task 3 view ray), default render path zone-fetch order untouched (POM only edits `wp`).
- Debug toggles referenced: `dbg_use_normalmap` / `dbg_fullrough` (Task 4); new `pom_on`/`ao_on` toggles for live bisection (Task 2 controls).

**Audit fixes applied.** H1 — POM depth divisor is now per-plane (`vdir.x`/`.y`/`.z` by dominant plane), not always `vdir.y`; this stops swimming on cliffs (where the dominant plane is vertical). M4 — AO reaches the DIRECT light via `varying v_ao` multiplied inside the custom `light()` (`AO_LIGHT_AFFECT` alone only touches ambient/GI under a custom `light()`). M2 — non-splat path picks the POM height-field zone by height/slope bands (cliffs/peaks parallax with their own relief, not zone-0 grass). M1 — removed the doubled `lod` (was `lod²`); offset uses a thin seam-fade only. H2 — removed the dead `prevH`.

**Placeholder scan.** No TODO/placeholder code — every GLSL/C#/JSON block is concrete and complete. Loop uses a constant `64` upper bound with a `break` at `steps` (GLSL requires constant bounds). The `v_ao` varying + `light()` `* v_ao` edits are described in Task 1 as exact-location changes (apply to the existing accumulate lines, don't rewrite the BRDF).

**Interface consistency.** AO fetch reuses `ar_sample_wp` + `tri_w` (same as `tp_*`). POM uses `tri_w`, `distanceWeight`, `tex_scale_m`, and `cam_world` exactly as the rest of the shader does. Controls follow the `lab_controls.json` `slider`/`toggle` + `"param"` convention; AO bind uses the existing `LoadOr` fallback (white-default sampler hint means missing AO reads as 1.0 = no occlusion, safe).

**PERF.**
- **Step-count knob:** `pom_steps` (0-64, default 24). This is THE cost lever — each step is 1 texture tap. At 24 steps close-range POM adds ~24 taps to the dominant zone only (NOT ×3 planes, NOT ×7 zones), the single biggest reason to march one plane.
- **LOD gate:** steps AND the returned offset both scale by `lod = 1 - smoothstep(0, pom_far_fade, far)`, so by `far = pom_far_fade` (default 0.6 → ~0.6·`ar_far_m`) POM is fully off — zero added taps far away, and the offset fading to 0 means no visible seam where it switches off. Profile the far camera to confirm ~0 delta vs `pom_on=false`.
- **Silhouette/edge caveat:** POM cannot displace geometry, so object silhouettes and the terrain horizon stay flat — relief reads on surfaces facing the camera, not at grazing edges. Expected and acceptable (this is the universal POM limitation; tessellation would be the only fix and Godot 4.6 lacks it).
- **Swimming caveat:** too few steps or too-large `pom_scale_m` makes the texture "swim" under motion. Defaults (24 / 0.06 m) are conservative; judge live and raise steps before raising depth.
- **No compute pre-pass:** POM is inherently per-fragment view-dependent — a baked height field wouldn't remove the per-fragment march, only the proxy derivation (a single tap). Not worth a compute pass; kept in-shader per the "don't add compute speculatively" directive. A real authored height map (upgrade path) would replace the proxy tap with one map tap — same march cost.
- **Full-quality path (noted, not chosen):** per-plane POM (march all 3 triplanar planes, blend offsets by `tri_w`) removes the dominant-plane discontinuity on 45° triplanar blends, at ~3× the POM tap cost. If the user finds the dominant-plane seam objectionable on steep cliffs at close range, this is the documented escalation.
