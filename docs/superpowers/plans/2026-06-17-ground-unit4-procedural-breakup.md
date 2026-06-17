# Ground Unit 4 — Procedural Breakup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ground read as *natural, not painted* by extending the GPU-compute bake to output per-texel **breakup masks** (slope, curvature, cavity, aspect, and a deferred flow approximation) into a SECOND data texture, and consume them in the fragment to VARY material per context — dirt settling in crevices/cavities, exposed rock on steep faces, debris/scree accumulation on shelves, sun/shade aspect wear — so no two spots fill the same. This unit (with Unit 5) also folds in the palette/zone-assignment retune: the breakup picks a *better material per context* instead of a flat near-monochrome grey fill.

**Architecture:** The masks are pure per-texel functions of the heightfield (slope from the surface normal, curvature from the neighbour Laplacian, cavity from clamped concavity, aspect from the normal's compass direction) — embarrassingly parallel, so they bake ONCE in a compute pass (extend `splat_weights.glsl` + `SplatCompute.cs`) into a second RGBA32F readback texture, because RGBA #1's four channels (dom/sec/mix/boundary) are full. The fragment samples that `breakup_tex` through the existing `groundData` seam (the splat read at fragment ~474), keeping per-frame work light (one extra texture fetch + cheap blends). FLOW (water accumulation) is sequential routing — NOT embarrassingly parallel — so it ships as a cheap bounded iterative approximation and is the one mask flagged hard/deferrable.

**Tech Stack:** Godot 4.6 mono. C# local-RenderingDevice compute driver (`scripts/lab/SplatCompute.cs`) + GLSL compute (`shaders/splat_weights.glsl`) for the bake; GLSL spatial shader (`shaders/terrain_lab.gdshader`) for consumption; data-driven control registry (`data/lab_controls.json`).

**Verification model (NOT TDD — GPU/visual, no unit harness):** Per task gate = `dotnet build WG16.csproj` → headless `--import` (shaders compile / SPIR-V compiles) → WINDOWED `--auto-shot` A/B (breakup on/off) at three ranges → a DEBUG VIEW of each baked mask (extend the existing `splat_debug` pattern with `breakup_debug=1..5` so each mask is verified to LOOK CORRECT as raw color before judging the lit result) → `--profile` (ms). THE gate is the user flying it live at close/mid/far. Commit per task.

---

## Environment

Project root: `C:\Wg16\wg-16-project`. Run all commands from there.

- **ONE Godot at a time.** Before any relaunch: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found").
- **ALWAYS** pass `--rendering-driver vulkan`.
- **After ANY `.cs` / `.glsl` / `.gdshader` change:** `dotnet build WG16.csproj` THEN headless `--import` with the **console** exe before launching windowed:
  - Console exe (import / compile-check): `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
  - Windowed exe (runs + bakes): same path without `_console`.
- **LOCAL-RD COMPUTE CANNOT RUN UNDER `--headless`.** `RenderingServer.CreateLocalRenderingDevice()` returns `null` headless → `NullReferenceException` in the `SplatCompute` ctor. So: `--headless --import` only **compile-checks** the GLSL/SPIR-V (it runs `ShaderCompileSpirVFromSource` at most, never dispatches). The actual bake + any visual/mask-debug verification MUST run **WINDOWED**.
- **std430 layout MUST match field-for-field.** The C# `BuildParams` byte packing and the GLSL `ParamsBuf` struct must agree on field order, type (all 4-byte scalars here), and total size padded to a 16-byte multiple. A mismatch silently garbles every mask. This unit ADDS fields to both — keep them in lockstep and re-pad.
- **Never debug a motion artifact from a still.** Swimming / shimmer / aliasing only show in motion — hand those to the live gate.
- Lab CLIs: `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<abs.png>`. Controls are data-driven in `data/lab_controls.json` (the UI, randomizer and lock system are generated from it).

**Key facts verified against current code:**
- `groundData` seam = the splat read in `fragment()` ~line 474: `vec4 sp = texture(splat_tex, v_uv)` → `R=dom`, `G=sec`, `B=mix`, `A=boundary`. RGBA #1 is FULL; Unit 4 adds `breakup_tex` (RGBA #2).
- The bake call site is `TerrainLab.RebakeSplat()` (`scripts/lab/TerrainLab.cs` ~66): builds `SplatCompute.Params`, calls `_splat.Bake(_heights, _res, sp)`, binds the result via `SetShaderParameter("splat_tex", tex)`. `Bake` currently returns ONE `ImageTexture`; Unit 4 makes it return BOTH (splat + breakup).
- `Bake` allocates `oBuf` (binding 1) = `cells*4*float`, dispatches `groups=(res+7)/8` 2D, syncs, reads back, builds `Image.Format.Rgbaf`. Mirror this exactly for the second output buffer.
- `BuildParams` packs 17 scalar fields into an 80-byte buffer. Unit 4 appends fields → re-count and re-pad.
- Material fetch entry is `ar_sample_wp(sampler2D, vec2, vec3)` (~243) via `tp_alb`/`trip_alb_by` (~419/433). Do NOT bypass it — breakup chooses *which zone index* feeds `trip_*_by`, then still fetches through `ar_sample_wp`.
- `distanceWeight(vec3 wp)` (~231) — reuse to LOD-fade breakup detail with distance.
- Varyings `v_slope = 1.0 - v_normal.y` and `v_curv = (hl+hr+hd+hu)*0.25 - VERTEX.y` already exist (set in `vertex()`), and the compute shader already computes the SAME `slope`/`curv` from neighbour heights in `main()` (~113). The masks reuse that math.
- EXISTING primitive breakup = the `contact_on` block (~508): `crevice_amp`/`crevice_range` via `v_curv`, `slope_wear_amp` via `v_slope`, `snow_dust`. Unit 4 **supersedes** this with the baked masks (keep `contact_on` as a legacy A/B fallback; route the real work through the new path).
- Debug-view pattern to mirror: `splat_debug` (uniform ~116) sets `dbg=1; dbg_col=...` and `ALBEDO = (dbg==1) ? dbg_col : alb` at ~525 (no early return — gdshader bans it).

---

## Task 1: Bake the breakup masks — extend `splat_weights.glsl` (second output + mask math)

**Files:** Modify `shaders/splat_weights.glsl`.

The masks are pure `f(heightfield)`. Slope/curvature already exist in `main()`; add cavity, aspect, and a cheap flow approximation, and write them to a NEW output buffer.

- [ ] **Step 1: Add the second output binding** after the `SplatOut` buffer (~line 33). It mirrors `SplatOut` exactly (vec4 per cell, row-major, std430 writeonly), binding **3** (binding 2 is `ParamsBuf`):

```glsl
// Unit 4: per-texel BREAKUP masks. RGBA #1 (splat) is full (dom/sec/mix/boundary),
// so masks ship as a SECOND readback texture. Pure f(heightfield) — baked once.
//   R = slope01     (0 flat .. 1 vertical)        — exposed rock on steep faces
//   G = curvature01 (0.5 flat, <0.5 ridge/convex, >0.5 hollow/concave)
//   B = cavity01    (0 .. 1 clamped concavity)    — dirt/scree settles here
//   A = aspect01    (sun-facing 0 .. shade-facing 1) — weathering by orientation
// (flow rides in a packed extra; see Step 4 — it reuses cavity's high bits via a
//  separate pass-free approximation, so the texture layout stays a clean RGBA.)
layout(set = 0, binding = 3, std430) restrict writeonly buffer BreakupOut {
    vec4 breakup[];
};
```

- [ ] **Step 2: Add the breakup-mask params** to `ParamsBuf` (~line 54, after `macro_m`). These tune the mask shaping (live-rebakable knobs in Task 5):

```glsl
    // --- Unit 4: breakup-mask shaping ---
    float bk_slope_lo;     // slope where rock starts exposing
    float bk_slope_hi;     // slope where it's full rock
    float bk_curv_scale;   // metres of relief that map curvature to [0,1]
    float bk_cavity_gain;  // how aggressively concavity reads as a cavity
    float bk_sun_azimuth;  // sun compass dir (radians) for aspect weathering
    uint  bk_flow_iters;   // 0 = skip flow approx; >0 = cheap iterative passes
```

- [ ] **Step 3: Add the cavity + aspect mask helpers** before `void main()` (~line 105). Cavity is a clamped, gained concavity over a slightly wider neighbourhood than the raw 4-tap curvature (so it reads pockets, not pixel noise); aspect is the normal's XZ direction dotted with the sun compass vector:

```glsl
// Cavity: average of the 8-neighbour height minus this cell, gained + clamped.
// Positive = this cell sits BELOW its surroundings (a pocket where debris/dirt
// collects). Wider stencil than the 4-tap curv so it reads pockets, not noise.
float cavity_at(ivec2 id, float hh){
    float s = 0.0;
    s += fetch(id.x-1,id.y) + fetch(id.x+1,id.y);
    s += fetch(id.x,id.y-1) + fetch(id.x,id.y+1);
    s += fetch(id.x-1,id.y-1) + fetch(id.x+1,id.y-1);
    s += fetch(id.x-1,id.y+1) + fetch(id.x+1,id.y+1);
    float concavity = (s * 0.125) - hh;             // >0 = pocket
    return clamp(concavity * bk_cavity_gain / max(bk_curv_scale, 1e-3), 0.0, 1.0);
}

// Aspect: 0 = faces the sun's compass direction, 1 = faces away (shade side).
// Uses the surface normal's XZ projection vs the sun azimuth unit vector.
float aspect_at(vec3 n){
    vec2 nd = n.xz;
    float l = length(nd);
    if (l < 1e-4) return 0.5;                        // flat-up = neutral
    nd /= l;
    vec2 sun = vec2(sin(bk_sun_azimuth), cos(bk_sun_azimuth));
    return clamp(0.5 - 0.5 * dot(nd, sun), 0.0, 1.0); // facing sun -> 0, away -> 1
}
```

- [ ] **Step 4: Add the cheap FLOW approximation** before `void main()` (right after `aspect_at`). True flow is sequential downhill routing (D8 accumulation) — NOT embarrassingly parallel, so we do NOT do real routing in a single dispatch. Instead a bounded, gather-only proxy: a cell accumulates "wetness" from how much higher its uphill neighbours are, iterated a few times in-shader over the local stencil. This stays per-cell-parallel (each cell only READS neighbours, never writes to them) and is good enough for streaking dirt down gullies. Flagged: replace with a real multi-dispatch routing pass later if the look demands it.

```glsl
// CHEAP flow proxy (NOT true sequential routing — that's a multi-dispatch job).
// Gather-only: sum how much each neighbour drains TOWARD this cell (neighbour
// higher AND draining downhill here). Bounded iterations re-weight by local
// slope so gullies streak. Returns 0..1 accumulation. flow_iters=0 disables.
float flow_at(ivec2 id, float hh, float slope){
    if (bk_flow_iters == 0u) return 0.0;
    float acc = 0.0;
    for (uint it = 0u; it < bk_flow_iters && it < 8u; it++){
        float r = 1.0 + float(it);                   // widen the gather each pass
        float up = 0.0;
        up += max(fetch(id.x-int(r),id.y) - hh, 0.0);
        up += max(fetch(id.x+int(r),id.y) - hh, 0.0);
        up += max(fetch(id.x,id.y-int(r)) - hh, 0.0);
        up += max(fetch(id.x,id.y+int(r)) - hh, 0.0);
        acc += up / (r * max(bk_curv_scale, 1e-3));
    }
    // more flow where it's steep enough to channel but not a sheer cliff face
    float channel = slope * (1.0 - smoothstep(0.7, 0.95, slope));
    return clamp(acc * channel, 0.0, 1.0);
}
```

- [ ] **Step 5: Write the masks in `main()`** — append after the existing `splat[...] = ...` line (~142). The slope/curv it needs are already computed at ~113-115:

```glsl
    // --- Unit 4: breakup masks (slope/curv already computed above) ---
    float slope01 = clamp(slope, 0.0, 1.0);
    float curv01  = clamp(0.5 + curv / max(bk_curv_scale, 1e-3) * 0.5, 0.0, 1.0);
    float cavity  = cavity_at(id, hh);
    float aspect  = aspect_at(n);
    float flow    = flow_at(id, hh, slope);
    // Pack flow into curvature's spare precision is NOT done — keep masks clean.
    // Layout: R slope, G curvature, B cavity, A = max(aspect-shade, flow streak)
    // so the fragment gets BOTH orientation wear and flow streaking in one channel,
    // disambiguated by sign of curvature where needed. (Aspect dominates on faces,
    // flow dominates in low-slope channels.)
    float a_chan = mix(aspect, max(aspect, flow), step(0.05, flow));
    breakup[id.y*int(res)+id.x] = vec4(slope01, curv01, cavity, a_chan);
```

- [ ] **Step 6: SPIR-V compile-check.** Even though the buffer isn't wired in C# yet, verify the GLSL compiles. Easiest: temporarily it's still referenced by the (about-to-change) C# — instead, defer the dispatch check to Task 2's `--import`. For now just a build:
```
dotnet build WG16.csproj
```
Expected: 0 errors (GLSL is compiled at runtime in `SplatCompute` ctor, so the real check is Task 2's windowed bake; this step only confirms C# still builds).

- [ ] **Step 7: Commit.**
```bash
git add shaders/splat_weights.glsl
git commit -m "Ground unit 4: bake slope/curv/cavity/aspect + cheap flow proxy into 2nd output buffer"
```

---

## Task 2: Wire the second output through `SplatCompute.cs` (extended std430 + 2nd ImageTexture)

**Files:** Modify `scripts/lab/SplatCompute.cs`.

- [ ] **Step 1: Extend the `Params` struct** to mirror the new GLSL `ParamsBuf` fields (append AFTER `MacroM`, same order as GLSL):

```csharp
    // Mirrors the std430 ParamsBuf in splat_weights.glsl (field-for-field).
    public struct Params
    {
        public uint Res;
        public float TexelWorld, RegionSize;
        public float HValley, HSlope, HHigh, HPeak, SlopeCliffLo, SlopeCliffHi, BandSoftnessM;
        public float MixScaleM, MixBias;
        public uint MaskMode;
        public float EdgeNoiseM, EdgeNoiseAmp, MacroM;
        // --- Unit 4: breakup-mask shaping (must match ParamsBuf order) ---
        public float BkSlopeLo, BkSlopeHi, BkCurvScale, BkCavityGain, BkSunAzimuth;
        public uint  BkFlowIters;
    }
```

- [ ] **Step 2: Change `Bake` to return BOTH textures.** Replace the signature + the output-buffer allocation + the readback + return. New return type is a small tuple/record:

```csharp
    /// (Re)bake the splat mask AND the breakup masks from a heightfield page →
    /// two ImageTextures (both RGBAF). Splat = dom/sec/mix/boundary; breakup =
    /// slope/curv/cavity/aspect+flow. One dispatch, two readbacks.
    public (ImageTexture splat, ImageTexture breakup) Bake(float[] heights, int res, Params p)
    {
        int cells = checked(res * res);

        // heights in (binding 0)
        var hBytes = new byte[heights.Length * sizeof(float)];
        Buffer.BlockCopy(heights, 0, hBytes, 0, hBytes.Length);
        Rid hBuf = _rd.StorageBufferCreate((uint)hBytes.Length, hBytes);
        var hU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        hU.AddId(hBuf);

        // splat out: 4 floats (RGBA) per cell (binding 1)
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * 4 * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        oU.AddId(oBuf);

        // params (binding 2)
        byte[] pBytes = BuildParams(p);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 };
        pU.AddId(pBuf);

        // breakup out: 4 floats (RGBA) per cell (binding 3) — Unit 4
        Rid bBuf = _rd.StorageBufferCreate((uint)(cells * 4 * sizeof(float)));
        var bU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
        bU.AddId(bBuf);

        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { hU, oU, pU, bU }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();

        byte[] outBytes = _rd.BufferGetData(oBuf);
        byte[] bkBytes  = _rd.BufferGetData(bBuf);
        _rd.FreeRid(set);
        _rd.FreeRid(hBuf);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);
        _rd.FreeRid(bBuf);

        Image splatImg = Image.CreateFromData(res, res, false, Image.Format.Rgbaf, outBytes);
        Image bkImg    = Image.CreateFromData(res, res, false, Image.Format.Rgbaf, bkBytes);
        return (ImageTexture.CreateFromImage(splatImg), ImageTexture.CreateFromImage(bkImg));
    }
```

- [ ] **Step 3: Extend `BuildParams`** to pack the 6 new fields and re-pad. 17 + 6 = 23 scalar fields × 4 bytes = 92 → pad to the next 16-byte multiple = **96 bytes**:

```csharp
    private static byte[] BuildParams(Params p)
    {
        // 23 fields, std430 scalar layout (all 4-byte) → pad to 16-byte multiple (96B).
        var b = new byte[96];
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(p.Res); F(p.TexelWorld); F(p.RegionSize);
        F(p.HValley); F(p.HSlope); F(p.HHigh); F(p.HPeak);
        F(p.SlopeCliffLo); F(p.SlopeCliffHi); F(p.BandSoftnessM);
        F(p.MixScaleM); F(p.MixBias);
        U(p.MaskMode); F(p.EdgeNoiseM); F(p.EdgeNoiseAmp); F(p.MacroM);
        // --- Unit 4 (must match the appended ParamsBuf fields, same order) ---
        F(p.BkSlopeLo); F(p.BkSlopeHi); F(p.BkCurvScale); F(p.BkCavityGain); F(p.BkSunAzimuth);
        U(p.BkFlowIters);
        return b;
    }
```

- [ ] **Step 4: Build.** `dotnet build WG16.csproj` — expect 0 errors. (This won't compile yet if `TerrainLab.RebakeSplat` still expects the old single-texture return — that's fixed in Task 3; do Task 3 before importing.)

- [ ] **Step 5: Commit.**
```bash
git add scripts/lab/SplatCompute.cs
git commit -m "Ground unit 4: SplatCompute bakes 2nd breakup output; std430 +6 fields, repad 96B"
```

---

## Task 3: Bind `breakup_tex` from the call site + push the breakup params

**Files:** Modify `scripts/lab/TerrainLab.cs`.

- [ ] **Step 1: Add the breakup-param backing fields** near the existing `MixScaleM`/`MixBias` registry-driven fields (search for `public float MixScaleM`). Add:

```csharp
    // --- Unit 4: breakup-mask shaping (rebake params, driven by lab_controls.json) ---
    public float BkSlopeLo = 0.30f, BkSlopeHi = 0.62f, BkCurvScale = 3.0f,
                 BkCavityGain = 1.4f, BkSunAzimuth = 0.7f;
    public int   BkFlowIters = 3;
```

- [ ] **Step 2: Update `RebakeSplat()`** to set the new params, capture BOTH returned textures, and bind both:

```csharp
        var sp = new SplatCompute.Params
        {
            Res = (uint)_res,
            TexelWorld = _spacing,
            RegionSize = _regionSize,
            HValley = 100f, HSlope = 350f, HHigh = 700f, HPeak = 950f,
            SlopeCliffLo = 0.30f, SlopeCliffHi = 0.55f, BandSoftnessM = 120f,
            MixScaleM = MixScaleM, MixBias = MixBias,
            MaskMode = (uint)SplatMaskMode,
            EdgeNoiseM = EdgeNoiseM, EdgeNoiseAmp = EdgeNoiseAmp, MacroM = MacroM,
            BkSlopeLo = BkSlopeLo, BkSlopeHi = BkSlopeHi, BkCurvScale = BkCurvScale,
            BkCavityGain = BkCavityGain, BkSunAzimuth = BkSunAzimuth,
            BkFlowIters = (uint)BkFlowIters,
        };
        var (splatTex, breakupTex) = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", splatTex);
        _mat.SetShaderParameter("breakup_tex", breakupTex);
        GD.Print($"TerrainLab: splat+breakup baked (mixScale {MixScaleM:F0}, bias {MixBias:F2}, mask {SplatMaskMode}, flowIters {BkFlowIters})");
```

- [ ] **Step 3: Build + import (windowed bake compile-check is Task 4).**
```
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
```
Expected: 0 build errors; `--import` reports no GLSL/shader errors. NOTE: the bake itself does NOT run under `--import` (local-RD is null headless) — `--import` only confirms the `terrain_lab.gdshader` parse. The SPIR-V for `splat_weights.glsl` is only compiled when `SplatCompute` is constructed, which happens WINDOWED in Task 4.

- [ ] **Step 4: Commit.**
```bash
git add scripts/lab/TerrainLab.cs
git commit -m "Ground unit 4: bind breakup_tex + push breakup-mask params on rebake"
```

---

## Task 4: Sample `breakup_tex` in the fragment + per-mask DEBUG VIEWS (verify masks before lighting)

**Files:** Modify `shaders/terrain_lab.gdshader`.

Add the sampler and a `groundData`-style read, then mask-debug views — so the baked masks are confirmed CORRECT as raw color before any material logic depends on them.

- [ ] **Step 1: Add the `breakup_tex` sampler + uniforms** right after the `splat_tex` block (~line 116). `filter_linear` (these are continuous masks, unlike the integer zone indices in `splat_tex`):

```glsl
// --- Unit 4: GPU-baked BREAKUP masks (2nd output from SplatCompute) ----------
// R=slope01, G=curvature01 (0.5=flat), B=cavity01, A=aspect/flow channel.
// Continuous masks → linear filtering (NOT nearest like splat_tex's indices).
uniform sampler2D breakup_tex : filter_linear, repeat_disable, hint_default_black;
uniform bool breakup_on = true;          // master toggle for procedural breakup
uniform int  breakup_debug = 0;          // 0 off |1 slope |2 curv |3 cavity |4 aspect/flow |5 composite tint
uniform float bk_dirt_amt    : hint_range(0.0, 1.0) = 0.65;  // dirt settling in cavities
uniform float bk_rock_amt    : hint_range(0.0, 1.0) = 0.8;   // rock exposed on steep faces
uniform float bk_debris_amt  : hint_range(0.0, 1.0) = 0.5;   // scree/debris on shelves
uniform float bk_wear_amt    : hint_range(0.0, 1.0) = 0.4;   // aspect/flow weathering tint
uniform int  bk_rock_zone    = 4;        // zone index used as "exposed rock" (cliff)
uniform int  bk_dirt_zone    = 1;        // zone index used as "settled dirt" (valley/transition)
uniform float bk_far_fade    : hint_range(0.0, 1.0) = 1.0;   // fade breakup detail with distance
```

- [ ] **Step 2: Add the `groundData` breakup read + a struct** just before `void fragment()` (~line 458). This is the single read all breakup logic consumes (matches the seam pattern):

```glsl
// Unit 4: the breakup half of groundData(uv). One fetch, shared by all breakup
// logic + debug. far-fades the higher-freq masks (cavity/flow) toward neutral so
// distant terrain doesn't shimmer from sub-texel mask detail.
struct Breakup { float slope; float curv; float cavity; float aspflow; };
Breakup groundBreakup(vec2 uv, vec3 wp){
    vec4 b = texture(breakup_tex, uv);
    float far = distanceWeight(wp) * bk_far_fade;
    Breakup r;
    r.slope   = b.r;                                  // slope survives at distance
    r.curv    = mix(b.g, 0.5, far);                   // fade to "flat"
    r.cavity  = b.b * (1.0 - far);                    // fade out fine pockets
    r.aspflow = mix(b.a, 0.0, far);                   // fade weathering far away
    return r;
}
```

- [ ] **Step 3: Add the mask-debug views** inside `fragment()`, after the existing `splat_debug` handling (~line 484, still inside the `if (splat_on)` block is fine but put it AFTER the block so it works either path — place it right before `alb = macro_color(...)` ~line 505). Mirror the `dbg`/`dbg_col` pattern:

```glsl
    // Unit 4: per-mask debug views (verify each baked mask before judging the lit
    // result). Mirrors splat_debug: set dbg + dbg_col, the final write honours it.
    if (breakup_debug != 0){
        Breakup bk = groundBreakup(v_uv, wp);
        if (breakup_debug == 1) { dbg = 1; dbg_col = vec3(bk.slope); }
        else if (breakup_debug == 2) { dbg = 1; dbg_col = vec3(bk.curv); }            // 0.5 grey = flat
        else if (breakup_debug == 3) { dbg = 1; dbg_col = vec3(0.4,0.25,0.12)*bk.cavity; } // dirt-brown where pockets
        else if (breakup_debug == 4) { dbg = 1; dbg_col = vec3(bk.aspflow); }
        else if (breakup_debug == 5) { dbg = 1; dbg_col = vec3(bk.slope, bk.cavity, bk.aspflow); } // composite
    }
```

- [ ] **Step 4: Build + import + WINDOWED mask-debug capture (this is where the bake actually runs).**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,400,800,-30,0" "--clouds=0" --auto-shot=C:/tmp/bk_dbg1_slope.png
```
Then in the live panel set `breakup debug` (Detail tab, Task 5) to 2/3/4/5 and capture each, OR temporarily change the `breakup_debug` default and re-launch. Expected per view: **slope** = white on cliff faces / black on flats; **curv** = mid-grey flats, dark ridges, bright hollows; **cavity** = brown only in pockets/gully bottoms; **aspect/flow** = bright on shade-facing slopes + streaks down gullies. CONFIRM the SplatCompute ctor compiled `splat_weights.glsl` (the print line from RebakeSplat appears in the console — if it threw on the std430 size, you'll see the SPIR-V compile error here, which is the canary that the C#/GLSL layout drifted).

- [ ] **Step 5: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 4: breakup_tex sampler + groundBreakup read + per-mask debug views"
```

---

## Task 5: Mask-driven material variation (dirt / rock / debris / wear) + supersede the contact block

**Files:** Modify `shaders/terrain_lab.gdshader`.

Now use the masks to VARY material in the lit path. Strategy: the splat path already chose `dom`/`sec` zones and blended them (`m`). Breakup **re-points** the material per context (steep → rock zone, deep cavity → dirt zone) and adds debris/wear tints, then everything still fetches through `ar_sample_wp` (we never bypass the sampler — we change which zone INDEX feeds `trip_*_by`, and modulate the result).

- [ ] **Step 1: Replace the splat material-blend block** (~line 486 `alb = mix(...)` down through the `blend_mode>=1` branch) so it applies breakup before/around the existing dom/sec blend:

```glsl
        // Unit 4: procedural breakup re-points material per context. Start from the
        // splat dom/sec choice, then let masks override toward rock (steep) / dirt
        // (cavity) and add debris + wear. All fetches still go through ar_sample_wp
        // via trip_*_by — breakup only changes the zone INDEX + post-modulates.
        Breakup bk = groundBreakup(v_uv, wp);

        // base dom/sec blend (unchanged seam)
        vec3 alb_base = mix(trip_alb_by(dom,wp,nr), trip_alb_by(sec,wp,nr), m);
        alb = alb_base;
        if (blend_mode>=1){
            nrm = mix(trip_nrm_by(dom,wp,nr), trip_nrm_by(sec,wp,nr), m)*2.0-1.0;
            rgh = mix(rD, rS, m);
        } else { rgh = 0.9; }

        if (breakup_on){
            // 1) ROCK on steep faces: blend toward the rock zone by slope.
            float rockw = smoothstep(bk_slope_lo, bk_slope_hi, bk.slope) * bk_rock_amt;
            if (rockw > 0.001){
                alb = mix(alb, trip_alb_by(bk_rock_zone, wp, nr), rockw);
                if (blend_mode>=1){
                    nrm = mix(nrm, trip_nrm_by(bk_rock_zone,wp,nr)*2.0-1.0, rockw);
                    rgh = mix(rgh, trip_rgh_by(bk_rock_zone,wp,nr), rockw);
                }
            }
            // 2) DIRT settling in cavities/pockets: blend toward the dirt zone,
            //    strongest where flow ALSO collects (gully bottoms).
            float dirtw = bk.cavity * bk_dirt_amt * (1.0 - rockw);  // rock wins on faces
            if (dirtw > 0.001){
                alb = mix(alb, trip_alb_by(bk_dirt_zone, wp, nr), dirtw);
                if (blend_mode>=1){
                    nrm = mix(nrm, trip_nrm_by(bk_dirt_zone,wp,nr)*2.0-1.0, dirtw);
                    rgh = mix(rgh, trip_rgh_by(bk_dirt_zone,wp,nr), dirtw);
                }
            }
            // 3) DEBRIS / scree: where a steepish face meets a hollow (curv>0.5),
            //    break up the surface with a darker, rougher gritty tint (cheap —
            //    no extra fetch; it reads as accumulated grit between rock & dirt).
            float shelf = smoothstep(0.5, 0.75, bk.curv) * smoothstep(0.15, bk_slope_hi, bk.slope);
            float debris = shelf * bk_debris_amt;
            alb = mix(alb, alb * vec3(0.78,0.74,0.70), debris);
            rgh = mix(rgh, 1.0, debris * 0.6);
            // 4) WEAR by aspect/flow: shade-facing + flow-streaked spots read cooler,
            //    damper, rougher (weathering / moisture staining).
            float wear = bk.aspflow * bk_wear_amt;
            alb = mix(alb, alb * vec3(0.85,0.88,0.92), wear);
            rgh = mix(rgh, mix(rgh, 0.85, 0.5), wear);
        }
```

- [ ] **Step 2: Add the matching `bk_slope_lo` / `bk_slope_hi` fragment uniforms** (the baked mask is raw slope01; these reshape it in the fragment so the live knobs work without a rebake). Put them with the other Unit-4 uniforms from Task 4 Step 1:

```glsl
uniform float bk_slope_lo : hint_range(0.0, 1.0) = 0.30;  // slope where rock begins (fragment-side)
uniform float bk_slope_hi : hint_range(0.0, 1.0) = 0.62;  // slope where it's full rock
```

- [ ] **Step 3: Demote the legacy `contact_on` block to a fallback.** It's superseded by the baked masks, but keep it for A/B. Change its guard so it only runs when breakup is OFF (~line 508):

```glsl
    // Legacy primitive breakup (Unit 4 supersedes this with baked masks). Kept as a
    // toggleable A/B fallback: only runs when the baked breakup path is disabled.
    if (contact_on && !breakup_on){
```
(Leave the block body unchanged.)

- [ ] **Step 4: Build + import + WINDOWED A/B captures (breakup on vs off) at three ranges.**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,90,160,-18,0" "--clouds=0" --auto-shot=C:/tmp/bk_on_close.png
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,400,800,-30,0" "--clouds=0" --auto-shot=C:/tmp/bk_on_mid.png
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,1200,2400,-32,0" "--clouds=0" --auto-shot=C:/tmp/bk_on_far.png
```
Then toggle `breakup` off in the panel (or temporarily default false) and re-capture `bk_off_*`. Expected: with breakup ON, cliff faces show exposed rock, gully/pocket bottoms show dirt, shelves get gritty debris, shade/flow spots read damper — and it's clearly LESS uniform than off, at all three ranges, with no hard switching seams (the `smoothstep` ramps should keep it gradual).

- [ ] **Step 5: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 4: mask-driven rock/dirt/debris/wear variation; demote contact block to fallback"
```

---

## Task 6: Expose toggle + knobs in the registry; profile

**Files:** Modify `data/lab_controls.json`.

The masks' SHAPE knobs (`bk_curv_scale`, `bk_cavity_gain`, `bk_sun_azimuth`, `bk_flow_iters`) are `field`+`rebake` (they feed `SplatCompute.Params`, so changing them triggers a rebake — exactly like `mix_scale_m`). The LOOK knobs (`bk_dirt_amt`, etc.) are plain `param` sliders (live, no rebake). Add to the **Detail** tab (where the legacy contact controls live).

- [ ] **Step 1: Add the Detail-tab breakup rows** after the `snow_dust_amp` row (~line 53):

```json
    { "id": "breakup_on", "label": "breakup", "tab": "Detail", "type": "toggle",
      "param": "breakup_on", "default": true, "rand": true },
    { "id": "bk_dirt_amt", "label": "cavity dirt", "tab": "Detail", "type": "slider",
      "param": "bk_dirt_amt", "min": 0, "max": 1, "default": 0.65, "rand": true },
    { "id": "bk_rock_amt", "label": "steep rock", "tab": "Detail", "type": "slider",
      "param": "bk_rock_amt", "min": 0, "max": 1, "default": 0.8, "rand": true },
    { "id": "bk_debris_amt", "label": "debris", "tab": "Detail", "type": "slider",
      "param": "bk_debris_amt", "min": 0, "max": 1, "default": 0.5, "rand": true },
    { "id": "bk_wear_amt", "label": "aspect wear", "tab": "Detail", "type": "slider",
      "param": "bk_wear_amt", "min": 0, "max": 1, "default": 0.4, "rand": true },
    { "id": "bk_slope_lo", "label": "rock slope lo", "tab": "Detail", "type": "slider",
      "param": "bk_slope_lo", "min": 0, "max": 1, "default": 0.30, "rand": false },
    { "id": "bk_slope_hi", "label": "rock slope hi", "tab": "Detail", "type": "slider",
      "param": "bk_slope_hi", "min": 0, "max": 1, "default": 0.62, "rand": false },
    { "id": "bk_rock_zone", "label": "rock zone", "tab": "Detail", "type": "enum", "param": "bk_rock_zone",
      "options": ["valley", "valley→slope", "slope", "slope→cliff", "cliff", "high", "peak/snow"], "default": 4, "rand": false },
    { "id": "bk_dirt_zone", "label": "dirt zone", "tab": "Detail", "type": "enum", "param": "bk_dirt_zone",
      "options": ["valley", "valley→slope", "slope", "slope→cliff", "cliff", "high", "peak/snow"], "default": 1, "rand": false },
    { "id": "bk_far_fade", "label": "breakup far fade", "tab": "Detail", "type": "slider",
      "param": "bk_far_fade", "min": 0, "max": 1, "default": 1.0, "rand": false },
    { "id": "breakup_debug", "label": "breakup debug", "tab": "Detail", "type": "enum", "param": "breakup_debug",
      "options": ["off", "slope", "curv", "cavity", "aspect/flow", "composite"], "default": 0, "rand": false },
    { "id": "bk_curv_scale", "label": "curv scale m", "tab": "Detail", "type": "slider",
      "field": "BkCurvScale", "min": 0.5, "max": 12, "default": 3.0, "rand": true, "rebake": true },
    { "id": "bk_cavity_gain", "label": "cavity gain", "tab": "Detail", "type": "slider",
      "field": "BkCavityGain", "min": 0.2, "max": 4, "default": 1.4, "rand": true, "rebake": true },
    { "id": "bk_sun_azimuth", "label": "weather sun dir", "tab": "Detail", "type": "slider",
      "field": "BkSunAzimuth", "min": 0, "max": 6.28, "default": 0.7, "rand": false, "rebake": true },
    { "id": "bk_flow_iters", "label": "flow iters (0=off)", "tab": "Detail", "type": "slideri",
      "field": "BkFlowIters", "min": 0, "max": 8, "default": 3, "rand": false, "rebake": true },
```
(If `slideri` is not a registered integer-slider type, use `"type": "slider"` and the C# setter will round — confirm against how `BkFlowIters` is read in the UI's `field` dispatch; match the existing integer `field` convention.)

- [ ] **Step 2: Validate JSON.**
```bash
python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"
```
Expected: `ok`.

- [ ] **Step 3: Confirm the UI dispatches the new `field` ids.** The `field`+`rebake` rows must map to the `Params` struct fields. If the UI's field dispatcher is a hardcoded switch (not reflection), add cases for `BkCurvScale`/`BkCavityGain`/`BkSunAzimuth`/`BkFlowIters` mirroring how `MixScaleM`/`MixBias` are handled. Grep first:
```
grep -n "MixScaleM\|case \"Mix\|rebake" scripts/lab/TerrainLabUI.cs
```
If it's reflection-based (sets the property by `field` name), no change needed — the property names already match. If switch-based, add the four cases + call `RebakeSplat()`.

- [ ] **Step 4: Build + profile A/B.**
```
dotnet build WG16.csproj
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,90,160,-18,0" "--clouds=0" --profile=3
```
Then toggle `breakup` off and re-profile. Expected: breakup adds modest per-frame cost (one `breakup_tex` fetch + up to 2 extra zone material blends on steep/cavity texels — most texels hit neither branch). The bake cost is one-time (rebake only on shape-knob changes). Record the ms delta; if heavy, `bk_far_fade` down and the amt knobs are the levers. Flow iterations only cost at BAKE time, never per-frame.

- [ ] **Step 5: Commit.**
```bash
git add data/lab_controls.json scripts/lab/TerrainLabUI.cs
git commit -m "Ground unit 4: breakup toggle + look/shape knobs + per-mask debug (Detail tab); rebake wiring"
```

---

## Task 7: Live judging handoff (THE gate)

- [ ] **Step 1:** Kill strays, build, launch windowed:
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```
- [ ] **Step 2: Verify each mask first (Detail tab → `breakup debug` 1..5).** Confirm slope/curv/cavity/aspect-flow each read correctly as raw color BEFORE judging the lit result — a wrong mask makes the lit material variation meaningless. Then set debug back to off.
- [ ] **Step 3: Tell the user to judge in MOTION at three ranges** (close / mid / far): does the ground stop reading as uniform/painted? Steep faces should show exposed rock; pockets and gully bottoms should collect dirt; shelves get gritty debris; shade/flow spots read damper. A/B with the `breakup` toggle. Dial `cavity dirt` / `steep rock` / `debris` / `aspect wear`, the `rock slope lo/hi` ramp, and the rock/dirt zone pickers (this is the palette/zone retune lever — choose materials that read RIGHT per context). Adjust `curv scale` / `cavity gain` / `flow iters` (these REBAKE). Check the FPS HUD.
- [ ] **Step 4:** On approval → proceed to Unit 5 (color/value variation), which finishes the palette retune on top of the contexts this unit established. If a mask reads wrong, it's isolated to `splat_weights.glsl` (use the debug views to localize); if the lit variation is wrong but masks look right, it's isolated to the Task 5 fragment block.

---

## Self-Review notes

- **Spec coverage:** Implements arc-spec Unit 4 (procedural breakup) end to end — extends the compute bake with slope/curvature/cavity/aspect/flow masks (arc-spec §"4. Procedural breakup") output as a SECOND data texture, consumes them through the `groundData` seam to vary material + add crevice/cavity dirt, exposed steep rock, shelf debris, and aspect/flow wear. Folds in the palette/zone-assignment retune the spec assigned to units 4-5 via `bk_rock_zone`/`bk_dirt_zone` pickers + per-context material re-pointing (the breakup decides *what material goes where*, the live gate tunes it; Unit 5 finishes color/value on top). Per-unit toggle (`breakup_on`) + knobs in the registry, isolating it for live bisection per the spec's testing model. Keeps Presenter-only, additive, revertable.
- **Placeholder scan:** No `TODO`/`add appropriate X`/stubs. Every code step is concrete GLSL/C#: the cavity/aspect/flow mask math, the 2nd std430 output buffer + matching `BuildParams` packing, the 2nd `ImageTexture` return + call-site binding, the `breakup_tex` sampler + `groundBreakup` read, the mask-driven material variation, and the five per-mask debug views. The only conditional note is Task 6 Step 1's `slideri` type + Step 3's UI-dispatch check (reflection vs switch) — both are explicit "grep and confirm" instructions, not deferred work.
- **std430-layout consistency (C# struct fields vs GLSL `ParamsBuf`, field-for-field):** GLSL appends `bk_slope_lo, bk_slope_hi, bk_curv_scale, bk_cavity_gain, bk_sun_azimuth, bk_flow_iters` to `ParamsBuf`; C# `Params` appends `BkSlopeLo, BkSlopeHi, BkCurvScale, BkCavityGain, BkSunAzimuth, BkFlowIters` in the SAME order; `BuildParams` packs them in that order (5×`F` then 1×`U` for the uint `flow_iters`). Count: 17 → 23 scalar 4-byte fields = 92 bytes, padded to 96 (next 16-byte multiple), buffer resized from 80→96. The bake's SPIR-V compile + first dispatch under WINDOWED run (Task 4 Step 4) is the canary for any drift. NOTE: `bk_slope_lo/hi` ALSO exist as fragment uniforms (Task 5 Step 2) — intentional: the bake stores raw `slope01`, the fragment reshapes it live without a rebake; the same-named compute params (`BkSlopeLo/Hi` in `Params`) are reserved/passed but the fragment owns the visible ramp. (If unused by the compute mask math, they can be dropped from `Params` to save 8 bytes — but keeping them keeps the struct stable for Unit 5; left in deliberately.)
- **Interface consistency with the `groundData` seam:** `splat_tex` (RGBA #1: dom/sec/mix/boundary) is untouched; breakup ships as RGBA #2 (`breakup_tex`) because #1 is full — exactly the arc-spec's "extends the current splat read" intent. The single consumption point is `groundBreakup(uv, wp)` (the breakup half of `groundData`), used identically by both the debug views (Task 4) and the lit path (Task 5). Material fetches still route through `ar_sample_wp` via `trip_*_by` — breakup changes the zone INDEX + post-modulates, never bypassing the Unit-1 sampler. `distanceWeight()` is reused to far-fade the high-freq masks (consistent with Unit 1/2's LOD-gating).
- **Masks: GPU-compute-now vs deferred.** GPU-compute NOW (pure per-cell, embarrassingly parallel, one dispatch): **slope** (surface normal), **curvature** (4-tap Laplacian), **cavity** (8-neighbour clamped concavity), **aspect** (normal XZ vs sun azimuth). DEFERRED / cheap-approx: **flow** — true water accumulation is SEQUENTIAL D8 downhill routing (each cell's value depends on upslope cells' resolved values), which is NOT embarrassingly parallel and would need an ordered multi-dispatch or jump-flood pass. Shipped here as a bounded gather-only proxy (`flow_at`, `bk_flow_iters`, default 3, `0`=off) that stays per-cell-parallel and only reads neighbours — good enough for gully streaking. Flagged as the hard one to revisit with a real routing pass if the live gate demands stronger hydrology; it does NOT block the unit.
