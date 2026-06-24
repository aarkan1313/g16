# Terrain GI / Fill Relight Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the dead-blue ambient fill on shaded/anti-sun terrain with an analytic, physically-motivated indirect-light term (cool sky hemisphere + warm sun→ground bounce) injected via `EMISSION` in the terrain shader, so shaded dunes read as warm sand (photoreal desert).

**Architecture:** Direct sun stays Godot's `DirectionalLight`. Indirect/fill becomes the terrain shader's job (`ground.gdshader` `EMISSION`). `LightingComposer` pushes the irradiance uniforms each `Compose()` and stops the flat-blue env ambient. A C# `FillEnabled` flag drives a `fill_on` uniform so the live A/B key and the drift-free validation harness toggle the same thing.

**Tech Stack:** Godot 4.6.2 mono (Vulkan, Forward+), GDShader (spatial), C# (.NET). CDLOD infinite terrain.

## Global Constraints

- **PRESERVE existing improvements — never revert:** aerial haze fix (`Y` / `--aerialhaze`, the `(1-aer.a)*aerial_haze` term in `aerial_screen.gdshader`), SSIL disabled (`LightingComposer` tune block + scene `ssil_enabled=false`), and the diagnostics (`U`/`--aerialdbg` aerial isolation, `--ssil` probe). These live in the same files this plan edits — ADD, don't overwrite.
- **Build C# after every `.cs` edit:** `dotnet build C:/Wg16/wg-16-project/WG16.csproj` — launching the player does NOT rebuild C# (stale-DLL gotcha). Shaders hot-compile, but the player reads the user shader cache; clear `app_userdata/"WG16 base field"/shader_cache` if a shader edit seems to no-op.
- **Launch (windowed, to execute compute/render):** kill ALL Godot first, then absolute path with a bare `--` separator:
  `"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --cam=0,300,0,-20,0`
- **Validation is drift-free only:** never A/B across two launches while the day cycle moves the sun. Use the live A/B key (same frozen instant) or the frozen-time `--fillab` harness (Task 4). The user's eye on the LIVE scene is the final look authority; auto-shots are for numeric/structural checks only.
- **Linear space:** `EMISSION` and pushed colors are linear radiance (the framebuffer is pre-tonemap). Convert sRGB `Color`s with `.SrgbToLinear()` where noted.

---

### Task 1: Shader irradiance term (uniforms + EMISSION), default OFF

Adds the analytic indirect-fill term to the terrain shader behind a `fill_on` uniform that defaults **false**, so with no C# changes the render is unchanged (no regression). The term reads sensible uniform defaults so it is eyeball-testable before the C# push exists.

**Files:**
- Modify: `shaders/ground.gdshader` (uniform block near the other `uniform`s ~top; `EMISSION` at the fragment tail, after `ALBEDO`/`ROUGHNESS` are set in the live `else` branch ~line 429-431).

**Interfaces:**
- Produces (uniform names consumed by Task 2's C# push): `fill_on` (bool), `sky_zenith` `sky_horizon` `sky_ground` (vec3 source_color), `sun_radiance` (vec3 source_color), `sun_dir_to` (vec3, unit, points toward the sun), `ground_albedo` (vec3 source_color), `sky_strength` (float), `bounce_strength` (float).

- [ ] **Step 1: Add the uniform block.** In `shaders/ground.gdshader`, near the existing uniforms, add:

```glsl
// ── Analytic indirect FILL (relight sub-project #1) ──────────────────────────────────────────
// Replaces the dead-blue flat ambient on shaded slopes with a cool sky hemisphere + a warm
// sun->ground bounce. Injected via EMISSION (unshaded indirect). LinearRGB. fill_on default false
// => byte-identical to the old render until LightingComposer pushes it on. See spec 2026-06-23.
uniform bool  fill_on = false;
uniform vec3  sky_zenith  : source_color = vec3(0.13, 0.22, 0.40);   // up-facing cool fill
uniform vec3  sky_horizon : source_color = vec3(0.40, 0.45, 0.50);   // grazing fill
uniform vec3  sky_ground  : source_color = vec3(0.22, 0.19, 0.16);   // down-facing (earth) fill
uniform vec3  sun_radiance : source_color = vec3(1.0, 0.93, 0.82);   // sun_color * sun_energy
uniform vec3  sun_dir_to = vec3(0.0, 1.0, 0.0);                       // unit dir TOWARD the sun
uniform vec3  ground_albedo : source_color = vec3(0.62, 0.50, 0.36); // representative sand albedo
uniform float sky_strength    : hint_range(0.0, 2.0) = 1.0;
uniform float bounce_strength : hint_range(0.0, 2.0) = 1.0;
```

- [ ] **Step 2: Add the EMISSION term at the fragment tail.** In the live `else` branch, immediately AFTER `ALBEDO = water_debug_tint(c, v_surf_xz);` and `ROUGHNESS = ground_rough;` (around line 429-431), add:

```glsl
        // Analytic indirect fill (see uniform block). World normal: v_normal (+Y up).
        if (fill_on) {
            vec3 nf = normalize(v_normal);
            float up = clamp(nf.y, -1.0, 1.0);
            // Cool sky hemisphere: zenith when up-facing, horizon at grazing, ground tint when down-facing.
            vec3 sky = (up >= 0.0) ? mix(sky_horizon, sky_zenith, up)
                                   : mix(sky_horizon, sky_ground, -up);
            float sky_vis = 0.5 + 0.5 * up;                  // 1 up-facing .. 0.5 vertical .. 0 down
            vec3 sky_fill = sky * sky_vis * sky_strength;
            // Warm bounce off sunlit ground: scales with sun elevation (lit-ground amount) and is
            // strongest on faces NOT pointing at the sky (down/sideways slopes see the lit sand).
            float sun_up = clamp(sun_dir_to.y, 0.0, 1.0);    // sun elevation -> how much ground is lit
            float bounce_vis = clamp(1.0 - up, 0.0, 1.0) * 0.5;
            vec3 bounce = sun_radiance * ground_albedo * sun_up * bounce_vis * bounce_strength;
            // Indirect light responds to surface albedo (warm bounce on sand = warm sand).
            EMISSION = (sky_fill + bounce) * ALBEDO;
        }
```

- [ ] **Step 3: Verify the shader compiles and is a no-op by default.** Kill Godot, then launch (see Global Constraints). Expected console: no shader-compile errors; the scene looks EXACTLY as before (fill_on defaults false). If a stale shader cache hides the change later, clear `app_userdata/"WG16 base field"/shader_cache`.

- [ ] **Step 4: Sanity-check the term turns on.** Temporarily (do NOT commit this) edit the uniform default to `uniform bool fill_on = true;`, relaunch, confirm shaded slopes pick up fill (warmer, less blue), then revert the default back to `false`.

- [ ] **Step 5: Commit.**

```bash
git add shaders/ground.gdshader
git commit -m "feat(relight): analytic indirect-fill EMISSION term in terrain shader (fill_on default off)"
```

---

### Task 2: LightingComposer pushes the fill + kills the blue env ambient

Wires the shader term to real lighting: pushes the irradiance uniforms each `Compose()` from the day-script sky/sun state, drops the flat-blue env ambient for terrain (no double-count), and removes the superseded warm-flat WIP. Adds the `Terrain` host accessor and `SetColor`/`SetVector3` on `TerrainLab`. A `FillEnabled` flag (default true) drives `fill_on` so later tasks can A/B it.

**Files:**
- Modify: `scripts/lab/TerrainLab.cs` (add `SetColor`, `SetVector3` near `SetFloat` ~line 243).
- Modify: `scripts/lab/LightingComposer.cs` (interface `ILightingHost` ~line 9; add `FillEnabled` field; add `PushTerrainFill()`; call it from `ApplyOvercastScaling()`; rework the ambient lines ~617-624).
- Modify: `scripts/lab/TerrainLabUI.cs` (or the partial implementing `ILightingHost`) to expose `Terrain`.

**Interfaces:**
- Consumes (from Task 1): the `ground.gdshader` uniforms `fill_on, sky_zenith, sky_horizon, sky_ground, sun_radiance, sun_dir_to, ground_albedo, sky_strength, bounce_strength`.
- Produces: `LightingComposer.FillEnabled` (bool, default true) — read when pushing `fill_on`, toggled by Tasks 3 & 4. `ILightingHost.Terrain` (`TerrainLab?`). `TerrainLab.SetColor(string, Color)`, `TerrainLab.SetVector3(string, Vector3)`.

- [ ] **Step 1: Add color/vector setters to `TerrainLab`.** After `public void SetBool(...)` (~line 245):

```csharp
    public void SetColor(string param, Color v) => _mat.SetShaderParameter(param, v);
    public void SetVector3(string param, Vector3 v) => _mat.SetShaderParameter(param, v);
```

- [ ] **Step 2: Expose `Terrain` on the host interface.** In `LightingComposer.cs`, add to `ILightingHost` (after `Atmosphere`):

```csharp
    TerrainLab? Terrain { get; }       // terrain material target for the indirect-fill uniforms (relight #1)
```

- [ ] **Step 3: Implement `Terrain` on the host.** In `TerrainLabUI` (the partial that holds `_terrain`), add the property (place near the other `ILightingHost` members):

```csharp
    public TerrainLab? Terrain => _terrain;
```

Run `dotnet build C:/Wg16/wg-16-project/WG16.csproj`. Expected: 0 errors (interface now satisfied).

- [ ] **Step 4: Add the `FillEnabled` flag.** In `LightingComposer.cs`, near the other public state (~line 40):

```csharp
    public bool FillEnabled { get; set; } = true;   // master A/B for the analytic indirect fill (relight #1)
```

- [ ] **Step 5: Rework the ambient block + push the fill.** In `ApplyOvercastScaling()`, REPLACE the WIP override lines (currently energy 0.9 / sky 0.4 / warm-white, ~622-624) so the terrain no longer gets a flat blue ambient and the shader fill owns it. Keep the lines ABOVE (`AmbientLightEnergy = BaseAmbient*lerp(...)`, `AmbientLightSkyContribution = Time.AmbientSky`) but override to a low NEUTRAL flat ambient (no blue), then call the push:

```csharp
        // Relight #1: the terrain's shaded-slope FILL is now owned by ground.gdshader's analytic
        // indirect term (cool sky hemisphere + warm ground bounce), pushed in PushTerrainFill().
        // So the env ambient is dropped to a low NEUTRAL flat base (no blue sky-contribution to
        // re-introduce the dead-blue slopes; other lit objects keep a small fill). Was the warm-flat WIP.
        env.AmbientLightEnergy = 0.12f;
        env.AmbientLightSkyContribution = 0.0f;          // 0 = flat AmbientLightColor, no blue sky pull
        env.AmbientLightColor = new Color(0.5f, 0.5f, 0.5f);
        PushTerrainFill();
```

- [ ] **Step 6: Implement `PushTerrainFill()`.** Add this method to `LightingComposer` (near `ApplyOvercastScaling`). It maps the day-script sky/sun state to the shader uniforms (sRGB→linear for colors):

```csharp
    /// Push the analytic indirect-fill uniforms to the terrain (relight #1). Cool sky hemisphere
    /// from the day-script sky colors + warm sun->ground bounce from the current sun. Called every
    /// Compose so the fill tracks time-of-day/mood through the one-writer.
    private void PushTerrainFill()
    {
        var t = _host.Terrain;
        if (t == null) { return; }
        var sun = SunNode;
        Vector3 toSun = sun.GlobalTransform.Basis.Z.Normalized();      // +Basis.Z points toward the sun
        Color sunRad = (Time.SunColor * Time.SunEnergy).SrgbToLinear();
        t.SetBool("fill_on", FillEnabled);
        t.SetColor("sky_zenith",  Time.SkyTop.SrgbToLinear());
        t.SetColor("sky_horizon", Time.SkyHorizon.SrgbToLinear());
        t.SetColor("sky_ground",  Time.SkyGround.SrgbToLinear());
        t.SetColor("sun_radiance", new Color(sunRad.R, sunRad.G, sunRad.B));
        t.SetVector3("sun_dir_to", toSun);
        // ground_albedo + strengths keep their shader defaults until the Task 3 sliders set them.
    }
```

- [ ] **Step 7: Build.** Run `dotnet build C:/Wg16/wg-16-project/WG16.csproj`. Expected: 0 errors.

- [ ] **Step 8: Live eye-gate (drift-free, single launch).** Kill Godot, relaunch (Global Constraints). Fly to a dune with the sun behind you and look at the shaded slope. Expected: the slope reads as warm/neutral sand with a subtle cool tint, NOT dead blue. (This is the user's look call.) If it's over-bright/washed or still blue, that's `bounce_strength`/`sky_strength`/ambient tuning in Task 3 — note it, don't fix here.

- [ ] **Step 9: Commit.**

```bash
git add scripts/lab/TerrainLab.cs scripts/lab/LightingComposer.cs scripts/lab/TerrainLabUI.cs
git commit -m "feat(relight): push analytic indirect-fill from LightingComposer; drop blue env ambient for terrain"
```

---

### Task 3: Lab controls — strength sliders + live A/B key

Makes the fill gate-tunable and adds an instant in-scene A/B so the look can be dialed and verified drift-free.

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (add an A/B key near the existing `U`/`Y` handlers ~line 236-245, and its `_last*`/state fields ~line 40-43).
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` or the relevant `ApplyXFloat` registry (where `aerial_strength` lives — see `case "aerial_strength"`), to route two new control ids.
- Modify: `data/lab_controls.json` (add `sky_strength`, `bounce_strength` sliders to the Light tab) — match the existing slider schema in that file.

**Interfaces:**
- Consumes: `LightingComposer.FillEnabled` (Task 2); terrain uniforms `sky_strength`, `bounce_strength` (Task 1).
- Produces: live key toggling `_lighting.FillEnabled`; control ids `sky_strength`/`bounce_strength` → `Terrain.SetFloat`.

- [ ] **Step 1: Add the A/B key state fields.** In `TerrainLabUI.Process.cs` near the other `_last*` fields (~line 40-43):

```csharp
    private bool _lastFillAb;   // toggles the analytic indirect fill (relight #1) for live A/B
```

- [ ] **Step 2: Add the key handler.** After the `Y` handler block (~line 245), add (key `I` is free — verified unused):

```csharp
                // I: A/B the analytic indirect FILL (relight #1). On = warm sky+bounce fill on shaded
                // slopes; Off = the raw low-ambient look (dead blue) for comparison.
                bool kI = Input.IsKeyPressed(Key.I);
                if (kI && !_lastFillAb) { _lighting.FillEnabled = !_lighting.FillEnabled; ComposeLighting(); GD.Print($"[dbg] (I) Indirect fill = {_lighting.FillEnabled}"); }
                _lastFillAb = kI;
```

(If `_lighting` is named differently in this partial, use the existing `LightingComposer` field name; confirm via the `_lighting.ExtraSunCount` usage in `TerrainLabUI.cs`.)

- [ ] **Step 3: Route the strength controls.** In the float-control registry that handles `"aerial_strength"` (grep `case "aerial_strength"`), add alongside it:

```csharp
            case "sky_strength":    _terrain?.SetFloat("sky_strength", v); return;
            case "bounce_strength": _terrain?.SetFloat("bounce_strength", v); return;
```

- [ ] **Step 4: Add the sliders to `data/lab_controls.json`.** In the Light tab's control array, add two entries matching the existing slider schema (copy the shape of the `aerial_strength` slider — same keys: id/label/min/max/default/tab). Use `min 0.0, max 2.0, default 1.0` for both `sky_strength` and `bounce_strength`. (Per the lab-registry note, a control whose id names no uniform silently no-ops, so the ids MUST match Task 1's uniforms exactly.)

- [ ] **Step 5: Build.** `dotnet build C:/Wg16/wg-16-project/WG16.csproj`. Expected: 0 errors.

- [ ] **Step 6: Verify live.** Relaunch. Press `I` — the shaded slope should pop between warm-fill and dead-blue (console prints the state). Drag the `sky_strength` / `bounce_strength` sliders on the Light tab — the fill responds. User dials to taste; eye-gate the midday case (no over-warm wash).

- [ ] **Step 7: Commit.**

```bash
git add scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Clouds.cs data/lab_controls.json
git commit -m "feat(relight): sky/bounce strength sliders + I-key A/B for the indirect fill"
```

---

### Task 4: Drift-free frozen-time validation harness (`--fillab`)

Adds an objective, day-cycle-proof A/B (the lesson from the SSIL mis-diagnosis): one launch freezes time, captures fill-ON, toggles `FillEnabled`, captures fill-OFF, quits — the two frames differ ONLY by the fill term. Plus a numeric helper to confirm the shaded-slope shift (blue→warm, luminance up).

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (parse `--fillab=<path>`; field).
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (the capture state machine, modeled on the existing `--godrayab` block ~line 348-360).

**Interfaces:**
- Consumes: `LightingComposer.FillEnabled` (Task 2), `ComposeLighting()`.
- Produces: `--fillab=<ospath>` → writes `<path>_fillon.png` and `<path>_filloff.png` at the same frozen instant, then quits.

- [ ] **Step 1: Parse the flag.** In `TerrainLabUI.Cli.cs`, near the other `--auto-shot`/`--godrayab` parsing, add a field and parse:

```csharp
    private string? _fillAbPath;   // --fillab=<ospath> → frozen-time fill ON/OFF A/B capture, then quit
    private int _fillAbStage; private double _fillAbT = -1.0; private int _fillAbFrames;
```
and in the arg loop:
```csharp
            else if (a.StartsWith("--fillab=")) { _fillAbPath = a.Substring("--fillab=".Length); _fillAbT = 0.0; }
```

- [ ] **Step 2: Add the capture state machine.** In `TerrainLabUI.Process.cs`, after the `--godrayab` block (~line 360), add (mirrors that block's freeze-and-toggle pattern):

```csharp
        if (_fillAbT >= 0.0 && _fillAbPath != null)
        {
            _fillAbT += delta;
            if (_fillAbStage == 0 && _fillAbT > 1.5)
            {
                Engine.TimeScale = 0.0;                       // FREEZE: sun/day-cycle stop -> pure A/B
                _lighting.FillEnabled = true; ComposeLighting();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_fillAbPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_fillAbPath + "_fillon.png");
                _lighting.FillEnabled = false; ComposeLighting();
                _fillAbStage = 1; _fillAbFrames = 0;
            }
            else if (_fillAbStage == 1)
            {
                _fillAbFrames++;
                if (_fillAbFrames > 3)                        // let the recompose flush
                {
                    GetViewport().GetTexture().GetImage().SavePng(_fillAbPath + "_filloff.png");
                    GD.Print($"TerrainLab: fillab -> {_fillAbPath}_fillon/_filloff.png");
                    _fillAbStage = 2; _fillAbT = -1.0;
                    GetTree().Quit();
                }
            }
        }
```

- [ ] **Step 3: Build.** `dotnet build C:/Wg16/wg-16-project/WG16.csproj`. Expected: 0 errors.

- [ ] **Step 4: Capture the drift-free A/B.** Kill Godot, run (single launch, frozen):

```
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --cam=0,300,0,-30,0 --fillab=C:/tmp/wg16fill/ab
```

- [ ] **Step 5: Numeric check (blue→warm on shaded slopes).** Run a pixel comparison of the two PNGs (Python+PIL, as used in diagnosis):

```python
from PIL import Image; import numpy as np
on=np.asarray(Image.open("C:/tmp/wg16fill/ab_fillon.png").convert("RGB")).astype(float)
off=np.asarray(Image.open("C:/tmp/wg16fill/ab_filloff.png").convert("RGB")).astype(float)
# shaded-slope mask: pixels that were dark+blue in OFF (B > R, low luma)
luma=off@[0.2126,0.7152,0.0722]; mask=(off[:,:,2]>off[:,:,0]+6)&(luma<90)
d=on-off
print("shaded px:",int(mask.sum()))
print("mean dR,dG,dB on shaded:",[round(float(d[:,:,c][mask].mean()),2) for c in range(3)])
print("mean luma shift:",round(float((d@[0.2126,0.7152,0.0722])[mask].mean()),2))
```
Expected: on shaded pixels, `dR >= dB` (warming, red rises at least as much as blue) and a POSITIVE luma shift (slopes lifted out of the dead-blue). Record the numbers in the commit message.

- [ ] **Step 6: User eye-gate.** Have the user fly the live scene (sun behind, look down) and confirm the shaded dunes read as warm sand. This is the final gate.

- [ ] **Step 7: Commit.**

```bash
git add scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Process.cs
git commit -m "test(relight): drift-free --fillab frozen-time A/B harness + numeric blue->warm check"
```

---

## Self-Review

**Spec coverage:**
- §Architecture (direct sun unchanged; indirect via EMISSION; env ambient dropped; LightingComposer pushes) → Tasks 1, 2. ✓
- §Irradiance model (sky hemisphere + ground bounce × albedo) → Task 1 Step 2. ✓
- §Integration / no double-count (drop env ambient, remove WIP) → Task 2 Step 5. ✓
- §Tuning (sky_strength/bounce_strength sliders, A/B key) → Task 3. ✓
- §Validation (frozen-time isolation, numeric, eye-gate) → Task 4. ✓
- §AO deferred / horizon-AO out of scope → not implemented (correct; deferred). ✓
- §Preserve aerial/SSIL/diagnostics → Global Constraints + additive edits. ✓
- §Sub-project #2 (long-range shadows) → explicitly NOT in this plan. ✓

**Placeholder scan:** No TBD/TODO; every code step shows real code. The one judgement call (exact `ground_albedo` source) is given a concrete default + noted as slider-overridable — not a placeholder.

**Type consistency:** `FillEnabled` (bool), `Terrain` (`TerrainLab?`), `SetColor(string,Color)`, `SetVector3(string,Vector3)`, uniform names identical between Task 1 (declare) and Tasks 2-3 (set). `fill_on` driven only via `FillEnabled`. Consistent across tasks.
