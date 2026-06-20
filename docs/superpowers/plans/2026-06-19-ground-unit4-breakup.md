# Ground Unit 4 — Procedural Breakup Implementation Plan (compositing-core pipeline)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **This SUPERSEDES `plans/2026-06-17-ground-unit4-procedural-breakup.md`** (which predates the compositing core and conflicts with it: it added the breakup buffer at compute binding **3** — now occupied by the weightmaps — assumed 16 `ParamsBuf` fields, and rewrote the *old* `dom/sec` splat blend, which no longer exists as the default path). Reuse that doc only for its **mask math** (cavity/aspect/flow helpers are unchanged); take the *integration* from here.

**Goal:** Make the ground read **natural, not uniform**, by baking per-texel **breakup masks** (slope, curvature, cavity, aspect, cheap flow) into a third compute output, then in the fragment **modulating the 7 role weights *before* the top-2 selection** (boost the rock role on steep faces, the soil role in cavities/gullies) plus light post-blend debris/wear tints — so material varies by context within a region. This is the per-area variety the user flagged missing after the blend shipped.

**Architecture:** The masks are pure `f(heightfield)` → baked once in the existing `SplatCompute` dispatch as a **third readback texture** (`breakup_tex`, RGBA8 or RGBAF), alongside the splat index map and the two role weightmaps. The fragment samples it in the **weightmap branch** (`splat_blend_mode==1`, the shipped default) and **adds mask-driven boosts to the `sw[7]` role weights before `top-2` selection** — so the existing height-interlock blend (Phase A) then renders the breakup naturally (rock pokes through via the interlock, no special-casing). Post-blend, cheap debris-grit and aspect/flow wear tints modulate `alb`/`rgh`. This is cleaner than the old plan's "re-point the dominant zone index" — it composes with the weightmap system instead of bypassing it. Everything behind `breakup_on` (default on) + live knobs; mask SHAPE knobs rebake, LOOK knobs are live.

**Tech Stack:** Godot 4.6 mono; GLSL compute (`shaders/splat_weights.glsl`) + C# local-RD driver (`scripts/lab/SplatCompute.cs`, `scripts/lab/TerrainLab.cs`) for the bake; GLSL spatial shader (`shaders/terrain_lab.gdshader`) for consumption; data-driven controls (`data/lab_controls.json`) + `scripts/lab/TerrainLabUI.Apply.cs` for `field` rebake wiring.

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = long-term-best, regardless of time cost. **Hard 8 ms** in-motion budget — breakup is a few extra fetches + cheap blends; bake cost is one-time. Profile with `--profmove`.
- **The user's eye is the only gate for look.** Mechanical checks gate correctness/cost; the user flying it at close/mid/far is the gate. No TDD — GPU/visual. Per-task cycle: `dotnet build` → headless `--import` (compile) → windowed `--auto-shot`/mask-debug A/B → `--profmove`. Each mask gets a **debug view** so it's verified correct as raw color before the lit result is judged.
- **Behind toggles, defaulting to the current look** where it changes anything judged; `breakup_on` defaults on but is fully A/B-able.
- **⚠ Shared tree:** do NOT touch `GodRays*`/`shaders/godray*`, and do NOT touch the **scene WorldEnvironment / SDFGI / GI proxy / lighting** (the light/sun chat owns those — see NEEDS_REVIEW 0b). Stage with `git add -p`/by path; never `git add -A`.
- **Find seams by NAME, not line number.** Every `lab_controls.json` `param` must name a real uniform; `field` rows must have a `SetTerrainField` case in `TerrainLabUI.Apply.cs` or they silently no-op (lab-registry gotcha).

## Current-state facts (verified 2026-06-19, post-compositing-core)

- `shaders/splat_weights.glsl` compute bindings in use: **0** Heights, **1** SplatOut (vec4 dom/sec/mix/boundary), **2** ParamsBuf, **3** WOutA (weights 0-3, packUnorm4x8), **4** WOutB (weights 4-6). → **Breakup output goes to binding 5.**
- `ParamsBuf` has **18** scalar 4-byte fields (… `macro_m`, then `rule_based` (uint), `curv_k`). `SplatCompute.BuildParams` allocates **80** bytes (18×4=72→80). Unit 4 appends **6** → **24 fields = 96 bytes → allocate 96.**
- `SplatCompute.Bake` returns `BakeResult(ImageTexture Splat, ImageTexture WeightsA, ImageTexture WeightsB)`. Unit 4 adds a 4th member `Breakup`.
- `TerrainLab.RebakeSplat()` builds `SplatCompute.Params`, calls `Bake`, binds `splat_tex`/`splat_wa`/`splat_wb`. Unit 4 binds `breakup_tex` and pushes the 6 breakup params.
- Fragment default path = the **weightmap branch** in `fragment()`: `if (splat_on){ if (splat_blend_mode==0){ legacy } else { float sw[7]; splat_weights7(sw, warped_uv); top-2 d0/d1; interlock_blend; mix d0/d1 } }`. **Unit 4 inserts weight modulation right after `splat_weights7(...)` and before the top-2 loop.**
- Shared helpers to reuse: `splat_weights7`, `material_height`, `interlock_blend`, `trip_alb_by/trip_nrm_by/trip_rgh_by`, `ao_by`, `distanceWeight`, `tri_w`, `vnoise`. Existing primitive breakup = the `contact_on` block (crevice/slope-wear/snow-dust) — demote to a fallback when `breakup_on`.
- `SetTerrainField(string field, float v)` lives in **`scripts/lab/TerrainLabUI.Apply.cs`** (handles `MixScaleM`/`MixBias`/`CurvK`/`HValley`/…). `field`+`rebake` rows route here. (The old plan said `TerrainLabUI.cs` — that's stale.)
- The mask math (cavity_at / aspect_at / flow_at) is copy-reusable verbatim from `plans/2026-06-17-ground-unit4-procedural-breakup.md` Task 1 Steps 3-4.

## Environment

- ONE Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (and `..._console.exe`). Always `--rendering-driver vulkan`. Local-RD compute can't run `--headless` (import only compile-checks; bakes are windowed).
- Console exe: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`; windowed = same without `_console`.
- Build: `dotnet build WG16.csproj`. Import: `"<console>" --headless --path /c/Wg16/wg-16-project --import`. Launch: `"<exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`.

---

## Task 1: Bake the breakup masks — `shaders/splat_weights.glsl` (3rd output at binding 5)

**Files:** Modify `shaders/splat_weights.glsl`.

**Interfaces produced:** compute output buffer `BreakupOut` at binding 5 (one `uint` per cell, `packUnorm4x8`: R slope01, G curv01, B cavity01, A wear = max(aspect, flow)); 6 new `ParamsBuf` fields.

> **PILLARS NOTE (2026-06-19, updated): pack to RGBA8, not RGBAF.** All four masks are `[0,1]` read through linear filtering — 32-bit float is 4× the VRAM/bandwidth for zero visible gain. **Match the weightmaps' existing idiom** (`WOutA`/`WOutB` already do `packUnorm4x8` → `uint` → read back as `Image.Format.Rgba8`): same 4 bytes/texel, no CPU float→u8 conversion, codebase-consistent. Steps below use that.
>
> **EROSION-READINESS NOTE:** these masks are pure `f(heightfield)` — when the erosion arc later reshapes the field, they auto-recompute against the new geomorphology (rock on new steep faces, soil in new gullies) with **zero rework**, and fire *stronger* (erosion produces the cavities/drainage they read). Unit 4 is the material-response layer erosion plugs into, NOT throwaway. The one seam erosion supersedes: `flow_at` is a gather-only wetness *proxy*, not real D8 routing — flag it in-code as "replace with erosion's real drainage when that lands."

- [ ] **Step 1: Add the 3rd output buffer** after `WOutB` (binding 4), mirroring the `WOutA`/`WOutB` `uint` pattern:
```glsl
// Unit 4: per-texel BREAKUP masks (3rd readback). R=slope01, G=curv01 (0.5=flat),
// B=cavity01, A=wear (max aspect-shade, flow-streak). Pure f(heightfield), baked once.
// packUnorm4x8 → RGBA8 (same idiom as WOutA/WOutB); masks are [0,1], no float precision needed.
layout(set = 0, binding = 5, std430) restrict writeonly buffer BreakupOut { uint breakup[]; };
```
- [ ] **Step 2: Append 6 fields to `ParamsBuf`** (after `curv_k`), in this exact order:
```glsl
    // --- Unit 4: breakup-mask shaping ---
    float bk_slope_lo;     // (reserved; fragment owns the visible ramp)
    float bk_slope_hi;
    float bk_curv_scale;   // metres of relief mapping curvature/cavity to [0,1]
    float bk_cavity_gain;  // how aggressively concavity reads as a cavity
    float bk_sun_azimuth;  // sun compass dir (radians) for aspect weathering
    uint  bk_flow_iters;   // 0 = skip flow proxy; >0 = cheap gather passes
```
- [ ] **Step 3: Add `cavity_at`, `aspect_at`, `flow_at`** before `main()` — copy verbatim from `plans/2026-06-17-ground-unit4-procedural-breakup.md` Task 1 Steps 3-4 (they reference `fetch`, `bk_cavity_gain`, `bk_curv_scale`, `bk_sun_azimuth`, `bk_flow_iters`, all now defined).
- [ ] **Step 4: Write the masks in `main()`** — after the existing weightmap pack (`wb[wi] = ...`), reusing the `slope`, `curv`, `hh`, `n`, `id` already computed:
```glsl
    float slope01 = clamp(slope, 0.0, 1.0);
    float curv01  = clamp(0.5 + curv / max(bk_curv_scale, 1e-3) * 0.5, 0.0, 1.0);
    float cavity  = cavity_at(id, hh);
    float aspect  = aspect_at(n);
    float flow    = flow_at(id, hh, slope);   // gather-only PROXY — erosion's real drainage replaces this later
    breakup[wi] = packUnorm4x8(vec4(slope01, curv01, cavity, max(aspect, flow)));
```
- [ ] **Step 5: Verify** `dotnet build WG16.csproj` (0 errors; GLSL compiles at runtime in Task 2's windowed bake).
- [ ] **Step 6: Commit** `git add shaders/splat_weights.glsl && git commit -m "Unit 4 T1: bake slope/curv/cavity/aspect/flow masks to compute binding 5"`

---

## Task 2: Wire the 3rd output through `scripts/lab/SplatCompute.cs`

**Files:** Modify `scripts/lab/SplatCompute.cs`.

**Interfaces:** `BakeResult` gains `ImageTexture Breakup`; `Params` gains the 6 Bk fields; `BuildParams` → 96 bytes.

- [ ] **Step 1: Extend `Params`** (append after `CurvK`):
```csharp
        public float BkSlopeLo, BkSlopeHi, BkCurvScale, BkCavityGain, BkSunAzimuth;
        public uint  BkFlowIters;
```
- [ ] **Step 2: Extend `BakeResult`** to `public readonly record struct BakeResult(ImageTexture Splat, ImageTexture WeightsA, ImageTexture WeightsB, ImageTexture Breakup);`
- [ ] **Step 3: Add the binding-5 output buffer** in `Bake` (after `wbBuf`), sized `cells*sizeof(uint)` (RGBA8 via `packUnorm4x8`, exactly like `wbBuf`), add `bU` to the `UniformSetCreate` array.
- [ ] **Step 4: Read back + build the texture** (mirror the `WeightsB` readback): `byte[] bkBytes = _rd.BufferGetData(bBuf);`, free `bBuf`, `Image bkImg = Image.CreateFromData(res,res,false,Image.Format.Rgba8,bkBytes);`, add `ImageTexture.CreateFromImage(bkImg)` as the 4th `BakeResult` member.
- [ ] **Step 5: Repad `BuildParams` to 96 bytes** (`new byte[96]`) and append, after `U(p.RuleBased); F(p.CurvK);`:
```csharp
        F(p.BkSlopeLo); F(p.BkSlopeHi); F(p.BkCurvScale); F(p.BkCavityGain); F(p.BkSunAzimuth);
        U(p.BkFlowIters);
```
- [ ] **Step 6: Verify** `dotnet build` (won't fully link until Task 3 updates the call site — expect the `RebakeSplat` deconstruction error; fix in Task 3 before import).
- [ ] **Step 7: Commit** `git add scripts/lab/SplatCompute.cs && git commit -m "Unit 4 T2: SplatCompute bakes breakup (binding 5); Params +6, BuildParams 96B"`

---

## Task 3: Bind `breakup_tex` + push params — `scripts/lab/TerrainLab.cs`

**Files:** Modify `scripts/lab/TerrainLab.cs`.

**Interfaces:** public fields `BkSlopeLo/BkSlopeHi/BkCurvScale/BkCavityGain/BkSunAzimuth` (float), `BkFlowIters` (int), consumed in `RebakeSplat`.

- [ ] **Step 1: Add backing fields** near the other rebake params:
```csharp
    public float BkSlopeLo = 0.30f, BkSlopeHi = 0.62f, BkCurvScale = 3.0f, BkCavityGain = 1.4f, BkSunAzimuth = 0.7f;
    public int   BkFlowIters = 3;
```
- [ ] **Step 2: In `RebakeSplat`**, set the 6 params on the `Params` initializer (`BkSlopeLo = BkSlopeLo, … BkFlowIters = (uint)BkFlowIters,`), capture the new return member, and bind: `_mat.SetShaderParameter("breakup_tex", baked.Breakup);`
- [ ] **Step 3: Verify** `dotnet build` (0 errors) → `"<console>" --headless --path /c/Wg16/wg-16-project --import` (clean; bake runs windowed in Task 4).
- [ ] **Step 4: Commit** `git add scripts/lab/TerrainLab.cs && git commit -m "Unit 4 T3: bind breakup_tex + push breakup params on rebake"`

---

## Task 4: Sample `breakup_tex` + per-mask debug views — `shaders/terrain_lab.gdshader`

**Files:** Modify `shaders/terrain_lab.gdshader`.

**Interfaces:** `uniform sampler2D breakup_tex`; `struct Breakup`; `Breakup groundBreakup(vec2 uv, vec3 wp)`; `breakup_on`, `breakup_debug`, mask-look uniforms.

- [ ] **Step 1: Add the sampler + uniforms** after the `splat_wa/splat_wb` block:
```glsl
// --- Unit 4: GPU-baked BREAKUP masks (3rd SplatCompute output) ---------------
uniform sampler2D breakup_tex : filter_linear, repeat_disable, hint_default_black;
uniform bool breakup_on = true;
uniform int  breakup_debug = 0;   // 0 off |1 slope |2 curv |3 cavity |4 wear |5 composite
uniform float bk_dirt_amt  : hint_range(0.0, 1.0) = 0.65;  // soil-role boost in cavities
uniform float bk_rock_amt  : hint_range(0.0, 1.0) = 0.8;   // rock-role boost on steep faces
uniform float bk_debris_amt: hint_range(0.0, 1.0) = 0.5;   // gritty shelf tint (post-blend)
uniform float bk_wear_amt  : hint_range(0.0, 1.0) = 0.4;   // aspect/flow weathering tint
uniform float bk_slope_lo  : hint_range(0.0, 1.0) = 0.30;  // fragment-side rock ramp
uniform float bk_slope_hi  : hint_range(0.0, 1.0) = 0.62;
uniform int   bk_rock_role = 4;   // role index used as "exposed rock"
uniform int   bk_dirt_role = 1;   // role index used as "settled soil"
uniform float bk_far_fade  : hint_range(0.0, 1.0) = 1.0;
```
- [ ] **Step 2: Add the read struct** before `fragment()`:
```glsl
struct Breakup { float slope; float curv; float cavity; float wear; };
Breakup groundBreakup(vec2 uv, vec3 wp){
    vec4 b = texture(breakup_tex, uv);
    float far = distanceWeight(wp) * bk_far_fade;
    Breakup r;
    r.slope  = b.r;                       // slope survives at distance
    r.curv   = mix(b.g, 0.5, far);        // fade to flat
    r.cavity = b.b * (1.0 - far);         // fade fine pockets far away
    r.wear   = mix(b.a, 0.0, far);
    return r;
}
```
- [ ] **Step 3: Add the per-mask debug views** in `fragment()` after the `splat_on` block, before `alb = macro_color(...)` (mirrors `splat_debug`'s `dbg`/`dbg_col`):
```glsl
    if (breakup_debug != 0){
        Breakup bkv = groundBreakup(v_uv, wp);
        if (breakup_debug == 1){ dbg=1; dbg_col=vec3(bkv.slope); }
        else if (breakup_debug == 2){ dbg=1; dbg_col=vec3(bkv.curv); }
        else if (breakup_debug == 3){ dbg=1; dbg_col=vec3(0.4,0.25,0.12)*bkv.cavity; }
        else if (breakup_debug == 4){ dbg=1; dbg_col=vec3(bkv.wear); }
        else if (breakup_debug == 5){ dbg=1; dbg_col=vec3(bkv.slope,bkv.cavity,bkv.wear); }
    }
```
- [ ] **Step 4: Verify** build → import → windowed mask-debug capture (set `breakup_debug` default 1..5 temporarily, or drive via the Task-6 control). **Expected:** slope=white on cliffs/black on flats; curv=mid-grey flats, dark ridges, bright hollows; cavity=brown only in pockets/gullies; wear=bright on shade/flow streaks. Confirms the bake compiled (the `splat baked` print appears; a std430 drift shows as a SPIR-V error here).
- [ ] **Step 5: Commit** `git add shaders/terrain_lab.gdshader && git commit -m "Unit 4 T4: breakup_tex sampler + groundBreakup + per-mask debug views"`

---

## Task 5: Weight modulation in the top-2 path + post-blend tints; demote contact block

**Files:** Modify `shaders/terrain_lab.gdshader`.

The clean integration: nudge the 7 role weights by the masks **before** the top-2 selection, then post-tint. Material still fetched via `trip_*_by` → `ar_sample_wp`.

- [ ] **Step 1: Modulate `sw[7]` before top-2.** In the weightmap branch, right after `float sw[7]; splat_weights7(sw, v_uv + swarp * (...));`, insert:
```glsl
            Breakup bk = groundBreakup(v_uv, wp);
            if (breakup_on){
                float rockw = smoothstep(bk_slope_lo, bk_slope_hi, bk.slope) * bk_rock_amt;
                float dirtw = bk.cavity * bk_dirt_amt * (1.0 - rockw);   // rock wins on faces
                sw[clamp(bk_rock_role,0,6)] += rockw;                    // expose rock on steep
                sw[clamp(bk_dirt_role,0,6)] += dirtw;                    // soil collects in pockets
            }
```
  (No renormalize needed — top-2 uses `m1/(m0+m1)`. The boosts push rock/soil into the top-2 where the masks say so; the existing interlock blend then renders them.)
- [ ] **Step 2: Post-blend debris + wear tints.** After the `alb = mix(...); … ao = mix(...);` block in the weightmap branch (before its closing `}`), add:
```glsl
            if (breakup_on){
                float shelf = smoothstep(0.5,0.75,bk.curv) * smoothstep(0.15,bk_slope_hi,bk.slope);
                float debris = shelf * bk_debris_amt;
                alb = mix(alb, alb*vec3(0.78,0.74,0.70), debris);       // gritty scree
                rgh = mix(rgh, 1.0, debris*0.6);
                float wear = bk.wear * bk_wear_amt;
                alb = mix(alb, alb*vec3(0.85,0.88,0.92), wear);         // shade/flow weathering
                rgh = mix(rgh, 0.85, wear*0.5);
            }
```
- [ ] **Step 3: Demote the legacy `contact_on` block** to a fallback: change its guard to `if (contact_on && !breakup_on){` (body unchanged).
- [ ] **Step 4: Verify** build → import → windowed A/B (`breakup_on` on vs off) at close/mid/far. **Expected:** steep faces show more exposed rock, pockets/gullies collect soil, shelves get gritty grit, shade/flow read damper — clearly less uniform than off, gradual (smoothstep), no hard seams. `--profmove` close: a few extra fetches + cheap blends; record ms, must stay within 8 ms.
- [ ] **Step 5: Commit** `git add shaders/terrain_lab.gdshader && git commit -m "Unit 4 T5: mask-driven weight modulation + debris/wear tints; demote contact block"`

---

## Task 6: Controls + rebake wiring — `data/lab_controls.json` + `TerrainLabUI.Apply.cs`

**Files:** Modify `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Apply.cs`.

- [ ] **Step 1: Add Detail-tab rows** (LOOK knobs = `param` live; SHAPE knobs = `field`+`rebake`; role pickers = `enum` with `options`). After the existing `detail_*`/`pom_*` rows:
```json
    { "id": "breakup_on", "label": "breakup", "tab": "Detail", "type": "toggle", "param": "breakup_on", "default": true, "rand": true },
    { "id": "bk_dirt_amt", "label": "cavity soil", "tab": "Detail", "type": "slider", "param": "bk_dirt_amt", "min": 0, "max": 1, "default": 0.65, "rand": true },
    { "id": "bk_rock_amt", "label": "steep rock", "tab": "Detail", "type": "slider", "param": "bk_rock_amt", "min": 0, "max": 1, "default": 0.8, "rand": true },
    { "id": "bk_debris_amt", "label": "debris", "tab": "Detail", "type": "slider", "param": "bk_debris_amt", "min": 0, "max": 1, "default": 0.5, "rand": true },
    { "id": "bk_wear_amt", "label": "aspect wear", "tab": "Detail", "type": "slider", "param": "bk_wear_amt", "min": 0, "max": 1, "default": 0.4, "rand": true },
    { "id": "bk_slope_lo", "label": "rock slope lo", "tab": "Detail", "type": "slider", "param": "bk_slope_lo", "min": 0, "max": 1, "default": 0.30, "rand": false },
    { "id": "bk_slope_hi", "label": "rock slope hi", "tab": "Detail", "type": "slider", "param": "bk_slope_hi", "min": 0, "max": 1, "default": 0.62, "rand": false },
    { "id": "bk_rock_role", "label": "rock role", "tab": "Detail", "type": "enum", "param": "bk_rock_role", "options": ["valley","valley→slope","slope","slope→cliff","cliff","high","peak/snow"], "default": 4, "rand": false },
    { "id": "bk_dirt_role", "label": "soil role", "tab": "Detail", "type": "enum", "param": "bk_dirt_role", "options": ["valley","valley→slope","slope","slope→cliff","cliff","high","peak/snow"], "default": 1, "rand": false },
    { "id": "bk_far_fade", "label": "breakup far fade", "tab": "Detail", "type": "slider", "param": "bk_far_fade", "min": 0, "max": 1, "default": 1.0, "rand": false },
    { "id": "breakup_debug", "label": "breakup debug", "tab": "Detail", "type": "enum", "param": "breakup_debug", "options": ["off","slope","curv","cavity","wear","composite"], "default": 0, "rand": false },
    { "id": "bk_curv_scale", "label": "curv scale m", "tab": "Detail", "type": "slider", "field": "BkCurvScale", "min": 0.5, "max": 12, "default": 3.0, "rand": true, "rebake": true },
    { "id": "bk_cavity_gain", "label": "cavity gain", "tab": "Detail", "type": "slider", "field": "BkCavityGain", "min": 0.2, "max": 4, "default": 1.4, "rand": true, "rebake": true },
    { "id": "bk_sun_azimuth", "label": "weather sun dir", "tab": "Detail", "type": "slider", "field": "BkSunAzimuth", "min": 0, "max": 6.28, "default": 0.7, "rand": false, "rebake": true },
    { "id": "bk_flow_iters", "label": "flow iters (0=off)", "tab": "Detail", "type": "slider", "field": "BkFlowIters", "min": 0, "max": 8, "default": 3, "rand": false, "rebake": true }
```
- [ ] **Step 2: Add `SetTerrainField` cases** in `scripts/lab/TerrainLabUI.Apply.cs` (the `if (field == "MixScaleM")…` chain):
```csharp
        else if (field == "BkCurvScale")  { _terrain.BkCurvScale = v; }
        else if (field == "BkCavityGain") { _terrain.BkCavityGain = v; }
        else if (field == "BkSunAzimuth") { _terrain.BkSunAzimuth = v; }
        else if (field == "BkFlowIters")  { _terrain.BkFlowIters = Mathf.RoundToInt(v); }
```
- [ ] **Step 3: Validate JSON** `python -c "import json;json.load(open('data/lab_controls.json'));print('ok')"`.
- [ ] **Step 4: Verify** build → import → launch; Detail tab shows the rows; flipping a `field` row triggers a rebake (console `splat baked` print). `--profmove` A/B breakup on/off → record ms delta.
- [ ] **Step 5: Commit** `git add -p data/lab_controls.json scripts/lab/TerrainLabUI.Apply.cs && git commit -m "Unit 4 T6: breakup controls + rebake wiring (Apply.cs)"`

---

## Task 7: Live eye-gate (USER)

- [ ] Launch windowed; Detail tab → `breakup debug` 1..5, confirm each mask reads correct as raw color; set back to off.
- [ ] Fly **close/mid/far** in motion; A/B the `breakup` toggle. Judge: does the ground stop reading uniform? Exposed rock on steep faces, soil in pockets/gullies, gritty shelves, damper shade/flow — gradual, no seams? Tune `cavity soil`/`steep rock`/`debris`/`aspect wear`, the `rock slope lo/hi` ramp, the rock/soil role pickers (the per-context palette lever), and the rebake knobs (`curv scale`/`cavity gain`/`flow iters`).
- [ ] On approval → update NEEDS_REVIEW/HANDOFF/ROADMAP; next is Unit 5 (macro color). If a mask reads wrong → isolated to `splat_weights.glsl` (debug views localize); if masks right but lit variation wrong → Task 5 fragment block.

---

## Self-Review notes

- **Spec coverage:** implements the spec's "Procedural breakup (Unit 4) — slope/curv/cavity masks → dirt-in-crevices, exposed edges, wear" on top of the shipped compositing core. Integration is the *new* clean approach (modulate the 7 role weights pre-top-2, so the Phase-A interlock renders it) rather than the old "re-point dom/sec index" — composes with, doesn't bypass, the weightmap system.
- **Conflicts with the 2026-06-17 plan resolved:** breakup output binding 3→**5** (weightmaps own 3/4); `ParamsBuf` 16→**18→24** fields, BuildParams **96B**; `BakeResult` 4th member; fragment integration targets the **weightmap branch** (`sw[7]`), not the removed default dom/sec block; `SetTerrainField` is in **Apply.cs**. Mask math (cavity/aspect/flow) reused verbatim.
- **Perf:** one `breakup_tex` fetch + (when masks fire) weight nudges that don't add fetches (top-2 still fetches 2 materials) + a couple of cheap post-blend `mix`es. Flow cost is bake-time only. Profile `--profmove`; `bk_far_fade` + amt knobs are the levers.
- **Placeholder scan:** no stubs; mask math referenced to the prior plan's exact code (copy verbatim), all other blocks concrete.
- **Risk/undo:** Presenter-only, additive, `breakup_on` toggle + `git checkout`. Does not touch lighting/GI/god-rays (other chats). Masks debug-viewable before trusting the lit result.
