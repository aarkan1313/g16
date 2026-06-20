# GM3-A — Within-area variation (material-property modulation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (sequential, shared tree, eye-gated). Steps use checkbox (`- [ ]`).

**Goal:** Make a single uniform ground area read varied by modulating the composited material's **roughness, normal-detail strength, and albedo value/sat** with a fragment-side, multi-scale, domain-warped **world-position** noise field — the cheap, near-free core of GM3 (Approach A), the real within-area-variety fix Unit 4 couldn't deliver.

**Architecture:** A new `groundVariation(wp)` helper produces modulation factors from a 2-octave warped world-XZ noise field, distance-faded. Applied **once, post-composite** (after the weightmap blend + macro_color + contact), modulating `alb`/`rgh`/`nrm` just before the `ALBEDO`/`ROUGHNESS`/`NORMAL` output — continuous, decoupled from the top-2 math (the banked Unit-4 lesson). **No new samplers, no new texture fetches.** Behind `variation_on` (default on, gentle defaults), fully tunable, additive on top of (not replacing) `macro_color`.

**Tech Stack:** Godot 4.6 GLSL spatial shader (`shaders/terrain_lab.gdshader`); data-driven controls (`data/lab_controls.json`).

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = best-long-term. **Near-free** (one noise eval + a few mul/clamp on already-fetched values); profile `--profmove`, expect < 0.3 ms. Spec: `specs/2026-06-20-ground-gm3-within-area-variation-design.md`.
- **The user's live eye is the only gate for look** (no TDD; GPU/visual). Mechanical checks gate correctness/cost.
- **Default to a gentle, A/B-able look:** `variation_on` default on but modest amounts (additive on top of the approved macro_color); confirm no jarring change before the user's gate.
- **⚠ Shared tree:** stage by path; never `git add -A` (another chat owns `GodRays*`/sun/cloud edits in `terrain_lab.gdshader`'s neighbours — touch only the variation seam).

## Current-state facts (verified 2026-06-20)

- Weightmap branch composites `alb = mix(trip_alb_by(d0..),trip_alb_by(d1..),m)`, `rgh = mix(trip_rgh_by..)`, `nrm = mix(trip_nrm_by..)*2-1` (`shaders/terrain_lab.gdshader` ~828-835). No within-role variation.
- Output: `ALBEDO = (dbg==1)?dbg_col:alb` (~921); `ROUGHNESS = clamp(rgh,rough_floor,1.0)` (~930); normal perturbs the geometric normal by `nrm.x`/`nrm.y` (~943) → `nrm.xy` IS the detail-strength. So modulating `alb`/`rgh`/`nrm.xy` before these lines is safe and correct.
- `macro_color(alb,wp)` (Color tab, default on) already drifts albedo hue/value/sat — GM3-A **adds** roughness+normal+a second value scale, does NOT replace it.
- Reusable: `vnoise(vec2,wl)`, `distanceWeight(wp)`, the domain-warp idiom.

## Environment

- ONE Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (+ `..._console.exe`). Always `--rendering-driver vulkan`.
- Console exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`.
- Build `dotnet build WG16.csproj`. Launch `"<exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`.

---

## Task 1: `groundVariation()` helper + uniforms + application — `shaders/terrain_lab.gdshader`

**Files:** Modify `shaders/terrain_lab.gdshader`.

**Interfaces produced:** `uniform` block `variation_*`; `struct GroundVar`; `GroundVar groundVariation(vec3 wp)`; an application block modulating `alb`/`rgh`/`nrm` pre-output.

- [ ] **Step 1: Add uniforms** (near the `macro_*` uniforms, Color group):
```glsl
// --- GM3-A: within-area variation (multi-scale world-pos material-property modulation) -------
uniform bool  variation_on = true;
uniform float variation_amt   : hint_range(0.0, 2.0) = 1.0;     // master
uniform float var_val   : hint_range(0.0, 0.5)  = 0.12;   // ± brightness (drier patches lighter)
uniform float var_sat   : hint_range(0.0, 0.5)  = 0.10;   // ± saturation
uniform float var_rough : hint_range(0.0, 0.6)  = 0.15;   // ± roughness (the surface-character lever)
uniform float var_nrm   : hint_range(0.0, 1.0)  = 0.25;   // ± normal-detail strength
uniform float var_scale_macro : hint_range(15.0, 160.0) = 55.0;  // m, large character patches
uniform float var_scale_meso  : hint_range(4.0, 40.0)   = 12.0;  // m, medium breakup
uniform float var_far_keep : hint_range(0.0, 1.0) = 0.3;  // fraction of variation kept at distance
```
- [ ] **Step 2: Add the helper** before `fragment()` (after `groundBreakup`/`macro_color` defs):
```glsl
// GM3-A: coherent ground-character patches. +field = drier (lighter+rougher), -field = compacted
// (darker+smoother) — physically coherent; nrm uses a decorrelated field so bumpiness varies
// independently. Domain-warped 2 octaves, world-XZ, distance-faded. No texture fetches.
struct GroundVar { float val; float sat; float rough; float nrmAmp; };
GroundVar groundVariation(vec3 wp){
    GroundVar g; g.val = 0.0; g.sat = 0.0; g.rough = 0.0; g.nrmAmp = 0.0;
    if (!variation_on) return g;
    vec2 w = wp.xz;
    vec2 warp = vec2(vnoise(w + vec2(31.0,17.0), var_scale_macro*0.6),
                     vnoise(w + vec2(7.0,53.0),  var_scale_macro*0.6)) - 0.5;
    w += warp * var_scale_macro * 0.5;
    float nMacro = vnoise(w, max(var_scale_macro,1.0)) - 0.5;
    float nMeso  = vnoise(w + vec2(91.0,11.0), max(var_scale_meso,1.0)) - 0.5;
    float n = nMacro * 0.7 + nMeso * 0.3;                 // ~[-0.5,0.5] coherent character field
    float nB = vnoise(w + vec2(211.0,143.0), max(var_scale_meso*1.7,1.0)) - 0.5;  // decorrelated bumpiness
    float fade = mix(var_far_keep, 1.0, 1.0 - distanceWeight(wp));
    float amt = variation_amt * fade;
    g.val    = n  * var_val   * 2.0 * amt;
    g.sat    = n  * var_sat   * 2.0 * amt;
    g.rough  = n  * var_rough * 2.0 * amt;
    g.nrmAmp = nB * var_nrm   * 2.0 * amt;
    return g;
}
```
- [ ] **Step 3: Apply it pre-output.** Insert right before the `// splat debug view overrides` / `ALBEDO =` line (~920), where `alb`/`rgh`/`nrm` are final:
```glsl
    // GM3-A: within-area variation — modulate the composited surface character.
    {
        GroundVar gv = groundVariation(wp);
        alb *= (1.0 + gv.val);                                   // value
        float luma = dot(alb, vec3(0.2126,0.7152,0.0722));
        alb = mix(vec3(luma), alb, clamp(1.0 + gv.sat, 0.0, 2.0)); // saturation
        rgh = clamp(rgh + gv.rough, 0.0, 1.0);                   // roughness (lighting-visible)
        nrm.xy *= (1.0 + gv.nrmAmp);                             // detail-strength (consumed at NORMAL)
    }
```
- [ ] **Step 4: Verify** `dotnet build WG16.csproj` (0 errors) → windowed auto-shot + profmove:
```bash
cd /c/Wg16/wg-16-project && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
EXE="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$EXE" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1 --profmove --profile=3 --auto-shot=C:/tmp/gm3a.png 2>&1 | grep -iE "PROFILE:|error.*shader|spir|groundVariation" | head -6
```
**Expected:** no shader compile error; `PROFILE: avg … ms` within budget (delta vs off should be tiny). Shot renders.
- [ ] **Step 5: Commit** `git add shaders/terrain_lab.gdshader && git commit -m "GM3-A T1: within-area variation (multi-scale world-pos value/sat/rough/normal modulation)"`

---

## Task 2: Controls — `data/lab_controls.json`

**Files:** Modify `data/lab_controls.json`.

- [ ] **Step 1: Add Color-tab rows** (all live `param`, no rebake) after the `macro_*` rows:
```json
    { "id": "variation_on", "label": "within-area variation", "tab": "Color", "type": "toggle", "param": "variation_on", "default": true, "rand": false },
    { "id": "variation_amt", "label": "variation amt", "tab": "Color", "type": "slider", "param": "variation_amt", "min": 0, "max": 2, "default": 1.0, "rand": true },
    { "id": "var_rough", "label": "var roughness", "tab": "Color", "type": "slider", "param": "var_rough", "min": 0, "max": 0.6, "default": 0.15, "rand": true },
    { "id": "var_val", "label": "var value", "tab": "Color", "type": "slider", "param": "var_val", "min": 0, "max": 0.5, "default": 0.12, "rand": true },
    { "id": "var_sat", "label": "var sat", "tab": "Color", "type": "slider", "param": "var_sat", "min": 0, "max": 0.5, "default": 0.10, "rand": true },
    { "id": "var_nrm", "label": "var relief", "tab": "Color", "type": "slider", "param": "var_nrm", "min": 0, "max": 1, "default": 0.25, "rand": true },
    { "id": "var_scale_macro", "label": "var scale macro m", "tab": "Color", "type": "slider", "param": "var_scale_macro", "min": 15, "max": 160, "default": 55, "rand": true },
    { "id": "var_scale_meso", "label": "var scale meso m", "tab": "Color", "type": "slider", "param": "var_scale_meso", "min": 4, "max": 40, "default": 12, "rand": true },
    { "id": "var_far_keep", "label": "var far keep", "tab": "Color", "type": "slider", "param": "var_far_keep", "min": 0, "max": 1, "default": 0.3, "rand": false }
```
- [ ] **Step 2: Validate** `python -c "import json;json.load(open('data/lab_controls.json'));print('ok')"`.
- [ ] **Step 3: Verify** build → launch; Color tab shows the rows; flipping `within-area variation` toggles live. `--profmove` A/B on/off → record ms delta (expect ~0).
- [ ] **Step 4: Commit** `git add data/lab_controls.json && git commit -m "GM3-A T2: within-area variation controls (Color tab)"`

---

## Task 3: Live eye-gate (USER)

- [ ] Fly **close/mid** on a uniform slope; A/B `within-area variation`. Judge: does the area stop reading uniform — patches of drier/lighter+rougher vs damper/darker+smoother, organic blobs, no tiling/squares, gradual, no motion shimmer? Far unchanged-ish (`var far keep`).
- [ ] Tune `var roughness` (the key lever) / `var value` / `var relief` / the macro/meso scales. Decide vs `macro_color` (keep both, or fold). 
- [ ] **Verdict → does Approach A satisfy "variety within an area"?** If yes → GM3 done, update docs. If "nice but I want actually different *materials* in patches" → greenlight Approach B/C (true material patches via `sampler2DArray`; see the GM3 spec).

---

## Self-Review notes

- **Spec coverage:** implements GM3 Approach A (the spec's recommended core) — fragment-side multi-scale warped world-pos field modulating value/sat/rough/normal, post-composite, distance-faded, near-free, toggle. B/C explicitly deferred to the eye-result.
- **Placeholders:** none; all GLSL/JSON concrete, integration seam verified (lines ~828-835 composite, ~920-943 output).
- **Type consistency:** `GroundVar {val,sat,rough,nrmAmp}` defined Task 1 Step 2, consumed Step 3; `nrm.xy` matches the normal consumption at ~943.
- **Risk/undo:** additive, fragment-side, `variation_on` toggle + `git checkout`; no samplers/bake/C# touched; defaults gentle to protect the approved look until the eye-gate.
- **Honesty:** correlated val+rough (drier=lighter+rougher) is physically coherent; if the user wants different *materials* (not just character), that's B/C, gated on Task 3.
