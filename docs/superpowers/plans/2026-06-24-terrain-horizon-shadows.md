# Terrain Heightfield Horizon Shadows — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Long-range terrain self-shadows — per terrain pixel, march a cheap macro height toward the sun and occlude the direct light where the terrain rises above the sun ray (a ridge shadowing a distant valley at low sun).

**Architecture:** A shared cheap macro-height function in `field_math.gdshaderinc`; a self-contained `horizon_shadow()` march in `ground.gdshader` that returns sun visibility; injected by writing `AO = vis; AO_LIGHT_AFFECT = 1.0` (occludes direct light, no `light()`/BRDF rewrite). Sun-elevation gated (free at high sun); all knobs are uniforms; `hz_on=false` ⇒ byte-identical to today.

**Tech Stack:** GDShader (spatial) + RD-GLSL include, Godot 4.6.2 mono, C#, data-driven lab JSON.

## Global Constraints

- **Perf is a HARD gate.** The frame is mesh-bound (~27 ms). Run `--profmove` at a LOW sun (march active) before vs after; the march must not meaningfully regress the frame. If it does, the levers are `hz_steps` / `hz_sun_gate` / `hz_maxdist`, and **default-OFF is the fallback**. Measured with `--profmove`, never eyeballed.
- **Modular + tunable (pillars):** shared `field_macro_height` (one macro definition), self-contained `horizon_shadow`, uniform knobs. `hz_on=false` must be byte-identical to the current render.
- **Build after every `.cs` edit:** `dotnet build C:/Wg16/wg-16-project/WG16.csproj` (stale-DLL gotcha). Shaders hot-compile but the player reads the user shader cache — clear `app_userdata/"WG16 base field"/shader_cache` if a shader edit seems to no-op. `field_math.gdshaderinc` is ALSO spliced into the compute shaders (FieldCompute/ChunkAabbProvider) — a syntax error there breaks the field bake, so re-run `--fieldcheck` after editing it.
- **Launch (windowed):** kill ALL Godot first; absolute path + bare `--` separator:
  `"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --time=17 --cam=0,600,0,-12,0`
  (`--time=17` = low evening sun, the regime where horizon shadows exist.)
- **Validation is drift-free** (the SSIL lesson): A/B in ONE launch via the live key, or a frozen capture — never compare two launches while the day cycle runs.
- `sun_dir_to` (unit dir TO the sun) is ALREADY pushed every Compose by `LightingComposer.PushTerrainFill` (unconditional — independent of the fill toggle). The march reuses it.

---

### Task 1: Shared cheap macro-height (`field_macro_height`)

Add the large-scale-only height eval to the shared field include. It is exactly `field_height`'s `base` term (continent + uplift), WITHOUT the per-octave hill/ridge FBMs — ~3–5× cheaper, which is what makes the per-pixel march affordable.

**Files:**
- Modify: `shaders/field_math.gdshaderinc` (add a function after `field_height`, ~end of file).

**Interfaces:**
- Consumes: existing `continent(vec2,uint,float,FieldP)`, `uplift(vec2,uint,FieldP)`, `FieldP`.
- Produces: `float field_macro_height(vec2 world_xz, uint seed, FieldP fp)` — used by Task 2.

- [ ] **Step 1: Add the function.** Append to `shaders/field_math.gdshaderinc` (after `field_height`):

```glsl
// Large-scale macro height ONLY (continent + uplift base) — NO hills/ridges. This is field_height's `base`
// term in isolation: ~3-5x cheaper than a full field_height, used by the per-pixel horizon-shadow march
// (long shadows come from big relief, so the macro shape suffices). Shared so there is ONE macro definition.
float field_macro_height(vec2 world_xz, uint seed, FieldP fp) {
    float cont = continent(world_xz, seed, fp.spacing, fp);
    Uplift up = uplift(world_xz, seed, fp);
    return (cont - fp.macro_pivot) * fp.macro_amp + up.amount * fp.uplift_weight * fp.macro_amp;
}
```

- [ ] **Step 2: Confirm the field bake still compiles (the include is spliced into the compute path).** Kill Godot, run the field self-check (now exit-coded):

```
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --fieldcheck
```
Expected console: `FIELDCHECK: PASS …` and `SELFCHECKS: ALL PASS — exit 0`. (Proves the new function didn't break `field_height.glsl`'s splice/compile.)

- [ ] **Step 3: Commit.**

```bash
git add shaders/field_math.gdshaderinc
git commit -m "feat(horizon-shadows): shared cheap field_macro_height (continent+uplift base, no hills/ridges)"
```

---

### Task 2: `horizon_shadow()` march + uniforms + AO injection (default OFF)

Add the march and inject it via `AO`/`AO_LIGHT_AFFECT`, behind `hz_on` (default false ⇒ no-op). All knobs are float/bool uniforms (so the generic lab `slider`/`toggle` routing drives them with no C# case).

**Files:**
- Modify: `shaders/ground.gdshader` (uniform block near the fill uniforms ~line 90; helper function before `fragment()`; AO injection at the fragment tail, after the existing `if (fill_on)` EMISSION block).

**Interfaces:**
- Consumes (Task 1): `field_macro_height(vec2,uint,FieldP)`; existing `analytic_seed` (int uniform), `ground_fieldp()`, `sun_dir_to` (vec3, pushed by C#), `v_surf_xz`/`v_h` varyings.
- Produces (uniform names for Task 3): `hz_on` (bool), `hz_steps` `hz_maxdist` `hz_stride0` `hz_growth` `hz_softness` `hz_strength` `hz_sun_gate` (all float).

- [ ] **Step 1: Add the uniform block.** In `shaders/ground.gdshader`, after the `bounce_strength` fill uniform:

```glsl
// ── Heightfield HORIZON SHADOWS (long-range terrain self-shadow) ─────────────────────────────
// Per-pixel macro-height march toward the sun: where the terrain rises above the sun ray, occlude the
// DIRECT light via AO + AO_LIGHT_AFFECT=1 (no light()/BRDF rewrite). hz_on=false => byte-identical. Cheap:
// marches field_macro_height (macro only), increasing stride, gated OFF at high sun (no long shadows there).
uniform bool  hz_on = false;
uniform float hz_steps    : hint_range(4.0, 48.0)   = 16.0;    // march steps (int() in the loop)
uniform float hz_maxdist  : hint_range(500.0, 16000.0) = 6000.0; // horizontal reach (m)
uniform float hz_stride0  : hint_range(4.0, 200.0)  = 30.0;    // first step length (m); stride grows from here
uniform float hz_growth   : hint_range(1.0, 2.0)    = 1.35;    // stride multiplier per step (LOD march)
uniform float hz_softness : hint_range(0.005, 0.30) = 0.06;    // penumbra width (angle units; higher = softer)
uniform float hz_strength : hint_range(0.0, 1.0)    = 1.0;     // final occlusion gain (0 = off, 1 = full)
uniform float hz_sun_gate : hint_range(0.05, 1.0)   = 0.5;     // skip march when sun_dir_to.y > this (sin elev; 0.5 = 30 deg)
```

- [ ] **Step 2: Add the march function.** In `ground.gdshader`, before `void fragment()` (e.g. after `apply_nrm_tangent`):

```glsl
// Returns sun VISIBILITY [0,1] (1 = full sun, 0 = fully occluded by distant terrain along the sun ray).
// Marches the macro height toward the sun with an increasing stride; soft penumbra from the running-max
// occluder angle. Gated: free when the sun is below the horizon or high in the sky (no long shadows there).
float horizon_shadow(vec2 surf_xz, float surf_h, vec3 sun_dir_to) {
    float sunUp = sun_dir_to.y;                       // sin(elevation)
    if (sunUp <= 0.02 || sunUp >= hz_sun_gate) { return 1.0; }   // below horizon / high sun → skip (free)
    vec2 hdir = sun_dir_to.xz;
    float hlen = length(hdir);
    if (hlen < 1e-4) { return 1.0; }
    hdir /= hlen;
    float tanSun = sunUp / hlen;                      // sun ray rise per horizontal metre (tan of elevation)
    float maxAng = -1e9;                              // running max occluder angle (rise/run) above surf
    float dist = hz_stride0;
    float stride = hz_stride0;
    int n = int(hz_steps);
    for (int i = 0; i < n; i++) {
        if (dist > hz_maxdist) { break; }
        vec2 p = surf_xz + hdir * dist;
        float h = field_macro_height(p, uint(analytic_seed), ground_fieldp());
        maxAng = max(maxAng, (h - surf_h) / dist);    // angle this occluder subtends above the surface point
        stride *= hz_growth;
        dist += stride;
    }
    // occluded where the tallest occluder angle exceeds the sun ray angle; soft over hz_softness band.
    float occ = smoothstep(0.0, hz_softness, maxAng - tanSun);   // 0 = lit, 1 = fully occluded
    return clamp(1.0 - occ * hz_strength, 0.0, 1.0);
}
```

- [ ] **Step 3: Inject via AO at the fragment tail.** In `ground.gdshader` `fragment()`, AFTER the existing `if (fill_on) { … EMISSION … }` block (very end of `fragment()`):

```glsl
    // Heightfield horizon shadow → occlude DIRECT light (AO_LIGHT_AFFECT=1 makes AO multiply direct light).
    // EMISSION (sky/bounce fill) is unaffected by AO, so a shadowed point still gets its sky fill (correct).
    if (hz_on) {
        AO = horizon_shadow(v_surf_xz, v_h, sun_dir_to);
        AO_LIGHT_AFFECT = 1.0;
    }
```

- [ ] **Step 4: Verify no-op when off + the march compiles.** Kill Godot, clear the shader cache (`rm -rf "/c/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache"`), launch (Global Constraints). Expected: no shader-compile errors; the scene looks EXACTLY as before (`hz_on` defaults false).

- [ ] **Step 5: Sanity-check the march turns on (do NOT commit this).** Temporarily set `uniform bool hz_on = true;`, relaunch at the low-sun vista. Expected: distant ridges/dune crests cast soft shadows into valleys that were NOT there before. Then revert the default to `false`.

- [ ] **Step 6: Commit.**

```bash
git add shaders/ground.gdshader
git commit -m "feat(horizon-shadows): horizon_shadow march + AO_LIGHT_AFFECT injection (hz_on default off)"
```

---

### Task 3: Lab controls (toggle + sliders) + live A/B key

Make it gate-tunable and add an instant in-scene A/B. All `hz_*` are float/bool uniforms → the generic `slider`/`toggle` control types route them straight to `_terrain.SetFloat/SetBool` (no C# case, like the relight sliders).

**Files:**
- Modify: `data/lab_controls.json` (Light tab: 1 toggle + 7 sliders).
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (live key for `hz_on` + its `_last*` field).

**Interfaces:**
- Consumes (Task 2): uniforms `hz_on` / `hz_steps` / `hz_maxdist` / `hz_stride0` / `hz_growth` / `hz_softness` / `hz_strength` / `hz_sun_gate`.
- Produces: Light-tab controls + a live key toggling `hz_on` via `_terrain.SetBool`.

- [ ] **Step 1: Add the controls to `data/lab_controls.json`.** After the `bounce_strength` slider in the Light tab, add (match the existing slider/toggle schema exactly):

```json
    { "id": "hz_on", "label": "horizon shadows", "tab": "Light", "type": "toggle", "param": "hz_on", "default": false, "rand": false },
    { "id": "hz_strength", "label": "horizon: strength", "tab": "Light", "type": "slider", "param": "hz_strength", "min": 0.0, "max": 1.0, "default": 1.0, "rand": false },
    { "id": "hz_steps", "label": "horizon: steps", "tab": "Light", "type": "slider", "param": "hz_steps", "min": 4.0, "max": 48.0, "default": 16.0, "rand": false },
    { "id": "hz_maxdist", "label": "horizon: max dist (m)", "tab": "Light", "type": "slider", "param": "hz_maxdist", "min": 500.0, "max": 16000.0, "default": 6000.0, "rand": false },
    { "id": "hz_stride0", "label": "horizon: first step (m)", "tab": "Light", "type": "slider", "param": "hz_stride0", "min": 4.0, "max": 200.0, "default": 30.0, "rand": false },
    { "id": "hz_growth", "label": "horizon: stride growth", "tab": "Light", "type": "slider", "param": "hz_growth", "min": 1.0, "max": 2.0, "default": 1.35, "rand": false },
    { "id": "hz_softness", "label": "horizon: softness", "tab": "Light", "type": "slider", "param": "hz_softness", "min": 0.005, "max": 0.30, "default": 0.06, "rand": false },
    { "id": "hz_sun_gate", "label": "horizon: sun gate", "tab": "Light", "type": "slider", "param": "hz_sun_gate", "min": 0.05, "max": 1.0, "default": 0.5, "rand": false },
```

- [ ] **Step 2: Add the live A/B key field.** In `scripts/lab/TerrainLabUI.Process.cs`, near the `_lastFillAb` field (~line 43):

```csharp
    private bool _lastHzKey;        // P A/Bs horizon shadows (hz_on)
    private bool _hzOn;             // mirror of hz_on for the live toggle
```

- [ ] **Step 3: Add the key handler.** In `TerrainLabUI.Process.cs`, after the `I` (indirect-fill) handler block, add (key `P` — confirm free via `grep "Key.P\b" scripts/lab`; if taken, use `Key.Y`'s neighbor — pick any unused letter):

```csharp
                // P: A/B horizon shadows (long-range terrain self-shadow). Best seen at a LOW sun.
                bool kP = Input.IsKeyPressed(Key.P);
                if (kP && !_lastHzKey) { _hzOn = !_hzOn; _terrain.SetBool("hz_on", _hzOn); GD.Print($"[dbg] (P) Horizon shadows = {_hzOn}"); }
                _lastHzKey = kP;
```

- [ ] **Step 4: Build (C# changed).** `dotnet build C:/Wg16/wg-16-project/WG16.csproj`. Expected: 0 errors.

- [ ] **Step 5: Verify live.** Relaunch at the low-sun vista. Toggle the `horizon shadows` checkbox (or press `P`) — distant ridge shadows pop in/out (console prints the state). Drag the `horizon: strength`/`steps`/`max dist`/`softness` sliders — the shadows respond. Confirm `hz_on` off = the prior look.

- [ ] **Step 6: Commit.**

```bash
git add data/lab_controls.json scripts/lab/TerrainLabUI.Process.cs
git commit -m "feat(horizon-shadows): Light-tab toggle + 7 sliders + P-key A/B"
```

---

### Task 4: Perf gate + numeric sanity + eye-gate, then set the default

The HARD gate. Measure cost with `--profmove` at a low sun; numerically confirm the shadow appears at low sun and the gate makes it free at high sun; user eye-gate; then decide the shipped default.

**Files:**
- Modify (only if perf passes and the eye-gate approves ON): `data/lab_controls.json` (`hz_on` default true) + `shaders/ground.gdshader` (`hz_on` default true).

**Interfaces:** consumes Tasks 1–3.

- [ ] **Step 1: Perf — low sun, march ACTIVE (worst case), before vs after.** Kill Godot. Baseline (off) then on, both `--profmove --profile=4`:

```
"/c/Godot/…/Godot…exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --time=17 --cam=0,600,0,-12,0 --profmove --profile=4
```
Run once as-is (hz_on off) → record `PROFILE: avg … ms`. Then temporarily set the `hz_on` JSON default true, relaunch the same command → record. **Gate:** the on-vs-off avg-ms delta must be small relative to the ~27 ms frame (target: < ~2 ms). If it exceeds budget, lower `hz_steps` (24→12) and/or `hz_maxdist`, or raise `hz_sun_gate`, and re-measure. Record the numbers.

- [ ] **Step 2: Numeric sanity — shadow appears at low sun, gate works at high sun.** Drift-free, frozen via two `--auto-shot` launches at the SAME `--time` (no cycle running by default, so the sun is static per launch):

```
# low sun: on vs off
"…exe" … --time=17 --cam=0,600,0,-12,0 --auto-shot=C:/tmp/hz/low_off.png         # hz_on default false
# (temporarily flip hz_on default true) → C:/tmp/hz/low_on.png
# high sun: on (gate should make it ~identical to off)
"…exe" … --time=12 --cam=0,600,0,-12,0 --auto-shot=C:/tmp/hz/high_on.png
```
Python diff (PIL, `C:/` paths): `low_on` vs `low_off` should change a MATERIAL fraction of pixels (new distant shadows); `high_on` vs a `high_off` should be ~0 (the sun-gate skipped the march). Record both.

- [ ] **Step 3: User eye-gate (the look authority).** Relaunch live at the low-sun vista; user flies and confirms: ridges cast soft shadows into distant valleys; soft not hard-edged; no banding/grid from the stride; no swimming in motion; high-sun look unchanged. Tune `softness`/`strength`/`steps` live to taste.

- [ ] **Step 4: Set the shipped default.** If perf (Step 1) AND eye-gate (Step 3) pass, set `hz_on` default **true** in BOTH `data/lab_controls.json` and `shaders/ground.gdshader`, with the user's tuned slider values baked as the JSON defaults. If perf fails or the look isn't worth it, leave default **false** (opt-in) — the feature still ships, gated. Commit:

```bash
git add data/lab_controls.json shaders/ground.gdshader
git commit -m "feat(horizon-shadows): finalize defaults after perf gate + eye-gate (on/off + tuned values)"
```

---

## Self-Review

**Spec coverage:**
- Unit 1 `field_macro_height` → Task 1. ✓
- Unit 2 `horizon_shadow` march (macro proxy, increasing stride, running-max angle, soft penumbra, sun gate) → Task 2 Step 2. ✓
- Unit 3 AO + AO_LIGHT_AFFECT integration (no BRDF rewrite; EMISSION fill unaffected) → Task 2 Step 3. ✓
- Unit 4 knobs + push (toggle + 7 sliders + live key; `sun_dir_to` reused) → Task 3. ✓
- Perf HARD gate (`--profmove`) + numeric sanity + eye-gate + default decision → Task 4. ✓
- `hz_on=false` byte-identical → Task 2 default-off + Step 4 verify. ✓
- Modular/tunable → shared macro fn (Task 1), self-contained march (Task 2), uniform knobs (Task 3). ✓

**Placeholder scan:** No TBD/TODO; every code step shows real code. The one runtime choice (final `hz_on` default) is explicitly decided by Task 4's perf+eye-gate, not left vague.

**Type consistency:** `field_macro_height(vec2,uint,FieldP)→float` declared (Task 1) and called (Task 2) identically. `hz_*` uniform names identical between Task 2 (declare) and Task 3 (control `param`). `hz_steps` is a FLOAT uniform (cast `int(hz_steps)` in the loop) so the generic float `slider` routing works — consistent across Tasks 2 & 3.
