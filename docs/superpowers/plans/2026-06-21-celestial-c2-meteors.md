# Celestial C2 — Meteors / Shooting Stars Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended — coupled shader↔C# work, the user gates the look live) or superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`). Spec: `specs/2026-06-21-celestial-c2-meteors-design.md`.

**Goal:** Add occasional, subtle procedural shooting stars to the night sky — thin head-bright/tail-fading streaks that cross the sky over ~1 s, rarely, gated to night + above-horizon, free when idle, tunable on the Night tab.

**Architecture:** Pure in-shader effect in `cloud_sky.gdshader` (a `meteors(rd)` function over a couple of TIME-hashed "channels"), composited into `stars_layer` next to `star_field`. Tunables flow the same way the C1 night-sky knobs did: `CloudVolume` setters → `_skyMat` uniforms, driven from `TerrainLabUI.ComposeLighting`, with `data/lab_controls.json` Night rows. No bake, no CPU per-frame, no new node.

**Tech Stack:** Godot 4.6 mono (C# + a spatial `.gdshader`), the existing `CloudVolume`/`TerrainLabUI` registry plumbing.

## Global Constraints

- **Cheap / free when idle:** ~zero per-frame cost when off or between streaks (early-outs bail before the streak math). No CPU work, no per-frame uniform churn, no new node.
- **Fits the night system:** lives in `cloud_sky.gdshader` alongside `star_field`, gated by the same `night_factor × above-horizon` fade — meteors only at night, never below the horizon, fade in/out at dusk.
- **Tunable + master toggle**, defaults dialled subtle (occasional & faint).
- **Stay in sky/light files:** `cloud_sky.gdshader`, `CloudVolume.cs`, `TerrainLabUI.{Lighting,Apply,Cli}.cs`, `LightingState.cs`, `data/lab_controls.json`. No terrain/ground/godray edits. `git add` paths explicitly, never `-A`.
- **No shader unit-test harness in this project** — verification is **build + windowed auto-shot capture** (`--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`), then the user's live eye-gate. One Godot at a time: kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **Look gates on the user's live eye** (review key 2 — "night sky — stars + moon"; meteors appear there automatically).

## File Structure

- `shaders/cloud_sky.gdshader` — NEW `meteor_channel()` + `meteors()` functions + 6 uniforms; one call added inside `stars_layer`. Owns the entire meteor visual.
- `scripts/lab/CloudVolume.cs` — `SetMeteorsOn(bool)` + `SetMeteors(rate, brightness, length, speed)` (the sole `_skyMat` writer).
- `scripts/lab/LightingState.cs` — meteor fields on `StarsState` (folded in, as the galaxy/nebula params were).
- `scripts/lab/TerrainLabUI.Lighting.cs` — push the meteor uniforms from `ComposeLighting`.
- `scripts/lab/TerrainLabUI.Apply.cs` — `ApplySceneFloat`/`ApplyScene` cases for the meteor knobs.
- `scripts/lab/TerrainLabUI.Cli.cs` — `--meteordebug` (forces a streak for headless capture).
- `data/lab_controls.json` — Night-tab rows (toggle + 4 sliders).

## Reference points (read before building)

- `shaders/cloud_sky.gdshader` `stars_layer()` (~line 392) + `star_field()` (~line 369) + `hash13` (~line 135). The composite return is `return (star + mw) * fade;` — meteors slot in there.
- `scripts/lab/CloudVolume.cs` `SetStars(...)` (~line 531) + `SetNightSkyBaked` — the setter pattern.
- `scripts/lab/TerrainLabUI.Lighting.cs` `ComposeLighting` star push (`_cloud.SetStars(...)`); `LightingState.cs` `StarsState` (~line 86); `TerrainLabUI.Apply.cs` `ApplySceneFloat`/`ApplyScene` switches; `data/lab_controls.json` Night `star_*` rows.

---

## Task 1: The meteor shader (the whole visual)

Adds the procedural meteors to `cloud_sky.gdshader` with hardcoded-default uniforms (so it renders + is gate-able before any C# wiring). A `meteor_debug` uniform forces a streak so a headless capture can confirm the geometry.

**Files:** Modify `shaders/cloud_sky.gdshader`.

**Interfaces:**
- Produces (shader uniforms consumed by Task 2): `bool meteors_on`, `float meteor_rate`, `float meteor_brightness`, `float meteor_length`, `float meteor_speed`, `bool meteor_debug`.
- Produces (GLSL): `vec3 meteors(vec3 rd)` — additive night-sky meteor color.

- [ ] **Step 1: Add the meteor uniforms.** In `shaders/cloud_sky.gdshader`, next to the `star_*` uniforms (the `uniform float star_brightness ...` block, ~line 63), add:

```glsl
// METEORS (Celestial C2): occasional shooting stars. Defaults subtle; gate-tunable. meteor_debug forces a
// streak (headless capture). meteor_rate = how often a channel fires; speed = sweep speed (shorter window).
uniform bool meteors_on = true;
uniform float meteor_rate : hint_range(0.0, 1.0) = 0.15;
uniform float meteor_brightness : hint_range(0.0, 3.0) = 0.8;
uniform float meteor_length : hint_range(0.0, 1.0) = 0.5;
uniform float meteor_speed : hint_range(0.0, 1.0) = 0.5;
uniform bool meteor_debug = false;
```

- [ ] **Step 2: Add the `meteor_channel` + `meteors` functions** immediately BEFORE `vec3 stars_layer(vec3 rd){` (~line 392):

```glsl
// One meteor channel. Cycles on `period` seconds; only OCCASIONALLY fires (hash vs a rate-derived gate).
// When it fires, a head sweeps an arc over a ~WINDOW-second window, drawing a thin head-bright/tail-fading
// streak. Pure TIME (stateless); two early-outs make it ~free when idle. meteor_debug pins it visible.
const int METEOR_CHANNELS = 2;
vec3 meteor_channel(vec3 rd, float seed){
    float window = mix(1.6, 0.7, clamp(meteor_speed, 0.0, 1.0));   // active seconds (faster → shorter)
    float period = mix(42.0, 8.0, clamp(meteor_rate, 0.0, 1.0));   // seconds between potential fires
    float t = TIME + seed * 53.0;
    float cyc = floor(t / period);
    float local = fract(t / period) * period;                     // seconds into this cycle
    if (!meteor_debug && local > window) { return vec3(0.0); }     // idle most of the cycle (early-out 1)
    float fireGate = mix(0.05, 0.9, clamp(meteor_rate, 0.0, 1.0)); // fraction of cycles that actually fire
    if (!meteor_debug && hash13(vec3(cyc, seed, 1.0)) > fireGate) { return vec3(0.0); }   // (early-out 2)
    float p = meteor_debug ? 0.45 : clamp(local / window, 0.0, 1.0);   // 0..1 sweep progress
    // hashed per-cycle params
    vec3 A = normalize(vec3(hash13(vec3(cyc, seed, 2.0)) * 2.0 - 1.0,
                            0.35 + 0.6 * hash13(vec3(cyc, seed, 3.0)),
                            hash13(vec3(cyc, seed, 4.0)) * 2.0 - 1.0));   // start dir, upper sky
    vec3 up = abs(A.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 tang = normalize(cross(A, up));
    vec3 bnor = cross(A, tang);
    float ad = hash13(vec3(cyc, seed, 5.0)) * 6.2831853;
    vec3 v = normalize(cos(ad) * tang + sin(ad) * bnor);          // random travel tangent at A
    float arc = radians(mix(18.0, 42.0, hash13(vec3(cyc, seed, 6.0))));
    vec3 H = normalize(A + v * tan(arc * p));                     // head at progress p
    vec3 hv = normalize(v - H * dot(v, H));                       // travel tangent at the head
    vec3 hw = cross(H, hv);
    vec3 rel = rd - H;
    float xa = dot(rel, hv);                                      // along-track (ahead of head = +)
    float ya = dot(rel, hw);                                      // cross-track
    float trail = radians(mix(2.0, 7.0, clamp(meteor_length, 0.0, 1.0)));
    float thick = radians(0.10);
    float crossT = smoothstep(thick, 0.0, abs(ya));              // thin
    float tail = smoothstep(-trail, 0.0, xa);                    // tail-end (0) → head (1): extent + gradient
    float headCut = smoothstep(0.012, 0.0, xa);                 // keep only BEHIND the head
    float life = sin(p * 3.14159265);                          // fade in/out over the window
    float bri = mix(0.4, 1.2, hash13(vec3(cyc, seed, 7.0))) * meteor_brightness;
    return vec3(0.85, 0.92, 1.0) * (crossT * tail * headCut * life * bri);   // faint cool white
}
vec3 meteors(vec3 rd){
    if (!meteors_on || rd.y < -0.02) { return vec3(0.0); }       // off / below horizon → free
    vec3 acc = vec3(0.0);
    for (int i = 0; i < METEOR_CHANNELS; i++) { acc += meteor_channel(rd, float(i) + 1.0); }
    return acc;
}
```

- [ ] **Step 3: Composite meteors into the night sky.** In `stars_layer`, change the final return (currently `return (star + mw) * fade;`, ~line 417) to:

```glsl
    return (star + meteors(rd) + mw) * fade;
```

- [ ] **Step 4: Build (C# unaffected, but confirm the project still builds).**

Run (from `/c/Wg16/wg-16-project`): `dotnet build WG16.csproj -v quiet -nologo`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 5: Headless capture with the streak forced — confirm a clean thin streak renders + no shader error.**

The `.gdshader` compiles at runtime (windowed). Drive review key 2 (night), then a windowed auto-shot. There's no `--meteordebug` CLI yet (Task 2), so for this task temporarily flip the uniform default `meteor_debug = false` → `true` in Step 1, capture, then set it back.

Run:
```bash
GODOT="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; sleep 1
"$GODOT" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --review=2 --auto-shot=/c/tmp/meteor.png 2>&1 | grep -iE "\[review\] 2|error.*shader|cloud_sky"
```
Then view `/c/tmp/meteor.png` (crop the sky region, right of the UI panel): expect **two thin bright streaks** with a bright head and a fading tail, against the moon+stars night. No shader-compile errors in the output. Set `meteor_debug` default back to `false`.

- [ ] **Step 6: Perf sanity (night, meteors on, no debug).**

Run:
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; sleep 1
"$GODOT" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --review=2 --profmove --profile=4 2>&1 | grep -iE "PROFILE:"
```
Expected: night `ms` unchanged vs pre-C2 (the early-outs make it ~free; a streak is a brief 2-channel burst). Record the number.

- [ ] **Step 7: Commit.**
```bash
git add shaders/cloud_sky.gdshader
git commit -m "celestial(C2-1): procedural meteors/shooting stars in cloud_sky (subtle, free when idle)"
```

---

## Task 2: Tunables — CloudVolume setters + Night-tab knobs + debug CLI

Exposes the 5 meteor params as Night-tab controls (routed through the registry like the `star_*`/`galaxy_*` knobs) + a `--meteordebug` CLI for deterministic capture. After this, the user can dial frequency/brightness/length/speed and toggle meteors live.

**Files:** Modify `scripts/lab/CloudVolume.cs`, `scripts/lab/LightingState.cs`, `scripts/lab/TerrainLabUI.Lighting.cs`, `scripts/lab/TerrainLabUI.Apply.cs`, `scripts/lab/TerrainLabUI.Cli.cs`, `data/lab_controls.json`.

**Interfaces:**
- Consumes (from Task 1): shader uniforms `meteors_on`, `meteor_rate`, `meteor_brightness`, `meteor_length`, `meteor_speed`, `meteor_debug`.
- Produces: `CloudVolume.SetMeteorsOn(bool)`, `CloudVolume.SetMeteors(float rate, float brightness, float length, float speed)`, `CloudVolume.SetMeteorDebug(bool)`; `StarsState` fields `MeteorsOn`, `MeteorRate`, `MeteorBrightness`, `MeteorLength`, `MeteorSpeed`.

- [ ] **Step 1: CloudVolume setters.** In `scripts/lab/CloudVolume.cs`, after `SetStars(...)` (~line 537), add:

```csharp
    // --- METEORS (Celestial C2): occasional shooting stars (in cloud_sky.gdshader). CloudVolume stays the SOLE _skyMat writer. ---
    public void SetMeteorsOn(bool on) { _skyMat?.SetShaderParameter("meteors_on", on); }
    public void SetMeteors(float rate, float brightness, float length, float speed)
    {
        _skyMat?.SetShaderParameter("meteor_rate", Mathf.Clamp(rate, 0f, 1f));
        _skyMat?.SetShaderParameter("meteor_brightness", Mathf.Max(brightness, 0f));
        _skyMat?.SetShaderParameter("meteor_length", Mathf.Clamp(length, 0f, 1f));
        _skyMat?.SetShaderParameter("meteor_speed", Mathf.Clamp(speed, 0f, 1f));
    }
    public void SetMeteorDebug(bool on) { _skyMat?.SetShaderParameter("meteor_debug", on); }
```

- [ ] **Step 2: State fields.** In `scripts/lab/LightingState.cs`, in `StarsState` (after the star fields, ~line 88), add:

```csharp
    // Meteors / shooting stars (Celestial C2) — live in cloud_sky.gdshader. Defaults subtle.
    public bool MeteorsOn = true;
    public float MeteorRate = 0.15f, MeteorBrightness = 0.8f, MeteorLength = 0.5f, MeteorSpeed = 0.5f;
```

- [ ] **Step 3: Push from ComposeLighting.** In `scripts/lab/TerrainLabUI.Lighting.cs`, in the night block where `_cloud.SetStars(...)` is called (~line 135), add right after it:

```csharp
            _cloud.SetMeteorsOn(_stars.MeteorsOn);
            _cloud.SetMeteors(_stars.MeteorRate, _stars.MeteorBrightness, _stars.MeteorLength, _stars.MeteorSpeed);
```

- [ ] **Step 4: Apply cases.** In `scripts/lab/TerrainLabUI.Apply.cs`, add to the `ApplySceneFloat` switch (next to the `star_*` cases, ~line 157):

```csharp
            case "meteor_rate":         _stars.MeteorRate = v; ComposeLighting(); break;
            case "meteor_brightness":   _stars.MeteorBrightness = v; ComposeLighting(); break;
            case "meteor_length":       _stars.MeteorLength = v; ComposeLighting(); break;
            case "meteor_speed":        _stars.MeteorSpeed = v; ComposeLighting(); break;
```
and to the `ApplyScene` (bool) switch (the `case "ssao":` / `case "fog":` block, ~line 106):
```csharp
            case "meteors_on": _stars.MeteorsOn = on; ComposeLighting(); break;
```

- [ ] **Step 5: Lab controls.** In `data/lab_controls.json`, add these rows in the Night tab (after the `star_*` rows):

```json
    { "id": "meteors_on", "label": "meteors (shooting stars)", "tab": "Night", "type": "scene", "scene": "meteors_on", "default": true, "rand": false },
    { "id": "meteor_rate", "label": "meteor rate", "tab": "Night", "type": "scenef", "scene": "meteor_rate", "min": 0.0, "max": 1.0, "default": 0.15, "rand": false },
    { "id": "meteor_brightness", "label": "meteor brightness", "tab": "Night", "type": "scenef", "scene": "meteor_brightness", "min": 0.0, "max": 3.0, "default": 0.8, "rand": false },
    { "id": "meteor_length", "label": "meteor length", "tab": "Night", "type": "scenef", "scene": "meteor_length", "min": 0.0, "max": 1.0, "default": 0.5, "rand": false },
    { "id": "meteor_speed", "label": "meteor speed", "tab": "Night", "type": "scenef", "scene": "meteor_speed", "min": 0.0, "max": 1.0, "default": 0.5, "rand": false },
```

- [ ] **Step 6: Debug CLI.** In `scripts/lab/TerrainLabUI.Cli.cs`, add a field next to `_reviewCli` (~line 58):

```csharp
    private bool _meteorDebugCli;        // --meteordebug → force a meteor streak (headless capture)
```
add the parse next to the `--review=` parse (~line 157):
```csharp
            else if (a == "--meteordebug") { _meteorDebugCli = true; }
```
and apply it after the cloud node is attached — in `scripts/lab/TerrainLabUI.cs` in `ApplyCliOverrides`/the CLI-apply block where `_reviewCli` is applied (~line 230), add BEFORE the `_reviewCli` line:
```csharp
        if (_meteorDebugCli) { _cloud?.SetMeteorDebug(true); }
```

- [ ] **Step 7: Build.**

Run: `dotnet build WG16.csproj -v quiet -nologo`
Expected: `Build succeeded.` / `0 Error(s)`.

- [ ] **Step 8: Verify the knobs + debug CLI drive the shader.** Headless capture with `--meteordebug` (deterministic streak, no shader edit needed now):
```bash
GODOT="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; sleep 1
"$GODOT" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --review=2 --meteordebug --auto-shot=/c/tmp/meteor2.png 2>&1 | grep -iE "\[review\] 2"
```
View `/c/tmp/meteor2.png` (crop sky): expect the forced streak(s) present. Then confirm the toggle path: the Night-tab `meteors (shooting stars)` control exists and routes (registry loads with no `unknown control id` warning in the launch output).

- [ ] **Step 9: Commit.**
```bash
git add scripts/lab/CloudVolume.cs scripts/lab/LightingState.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.Apply.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs data/lab_controls.json
git commit -m "celestial(C2-2): Night-tab meteor tunables (rate/brightness/length/speed + toggle) + --meteordebug"
```

---

## Task 3: USER LIVE LOOK EYE-GATE

Drive `scenes/review.tscn`, press **2** (night sky — stars + moon + now meteors). Fly the night and **watch for streaks** (occasional by design — `--meteordebug` or temporarily raising `meteor rate` makes them appear on demand for judging). **Judge:** streaks read as believable shooting stars (thin, head-bright, fading tail, fast), the right *rarity* (the "occasional & subtle" feel), fade cleanly, never below the horizon / never in daytime, tie with the night. Tune `meteor rate/brightness/length/speed` on the Night tab to taste. Record in DECISIONS + NEEDS_REVIEW + ROADMAP (#6 C2). PASS → keep default-on. (Perf already verified per-task.)

---

## Self-Review

**Spec coverage:** procedural in-shader `meteors()` in `stars_layer`, gated night×horizon → T1 ✓; channels/period/fire-gate/sweep/streak model → T1 Step 2 ✓; tunables (rate/brightness/length/speed + toggle) on the Night tab → T2 ✓; debug-seed for build-time capture → T1 (`meteor_debug` uniform) + T2 (`--meteordebug` CLI) ✓; cheap/free-when-idle (early-outs) → T1 Step 2 + perf step ✓; stay-in-sky-files + no bake/CPU → all tasks ✓; look gate → T3 ✓; out-of-scope (planets/showers/trails) → not in any task ✓.

**Placeholder scan:** All shader constants (period 8–42 s, window 0.7–1.6 s, arc 18–42°, trail 2–7°, thickness 0.10°, fire-gate 0.05–0.9, defaults) are concrete starting values explicitly gate-tunable at T3 — not "TBD". File paths + line anchors + setter/case/uniform names are exact; the `~line N` anchors are read-hints (the surrounding code is quoted), the edits match on quoted strings.

**Type consistency:** uniform names (`meteors_on`/`meteor_rate`/`meteor_brightness`/`meteor_length`/`meteor_speed`/`meteor_debug`) are identical across the shader (T1), the `CloudVolume` setters (T2-1), and the lab-control `scene` ids (T2-5). `StarsState` fields (`MeteorsOn`/`MeteorRate`/`MeteorBrightness`/`MeteorLength`/`MeteorSpeed`, T2-2) match the `ComposeLighting` push (T2-3) and the `ApplySceneFloat`/`ApplyScene` cases (T2-4). `SetMeteors(rate, brightness, length, speed)` arg order matches the push call.
