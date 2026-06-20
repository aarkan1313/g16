# Sun Surface Shader + Sun Presets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the sun disc a tunable procedural surface (subtle-realistic → living-star → fantasy) and a curated, eye-gated sun-preset set.

**Architecture:** Add a procedural surface to `sun_layers()` in `cloud_sky.gdshader` (sun-local world-anchored UVs + FBM/cellular noise → brightness/color modulation, animated churn), gated by a `sun_surface_on` uniform defaulting OFF (= today's flat disc). Expose the new uniforms as Light-tab controls routed through `CloudVolume` material-uniform setters (mirroring the existing `sun_*` disc knobs). Add `data/sun_presets.json` + a Light-tab picker (`TerrainLabUI.SunPresets.cs`) mirroring the cloud-preset pattern, disc-only and orthogonal to Time/Weather/Grade.

**Tech Stack:** Godot 4.6 mono, GDShader (sky shader), C# registry-driven lab UI.

## Global Constraints

- **No TDD / automated tests — GPU + visual.** Per project convention (`HANDOFF.md`, `ROADMAP.md`), the gate for look is the USER's live eye, never a still or a unit test. Per-task "verify" = `dotnet build WG16.csproj` (0 errors) → headless `--import` (shader compiles) → windowed `scenes/review.tscn` (`--rendering-driver vulkan`, ONE Godot at a time) for a visual check. Look-tuning of constants happens at the Task 4 eye-gate.
- **Behind a toggle, default = approved look.** `sun_surface_on` defaults OFF (flat disc) and `sun_presets.json` `active` stays neutral until the user approves; the default-flip is a single deliberate step in Task 4.
- **Stay in the Sun/Light lane files.** `shaders/cloud_sky.gdshader`, `scripts/lab/CloudVolume.cs`, `scripts/lab/TerrainLabUI.{Apply,Cli,Review}.cs`, NEW `scripts/lab/TerrainLabUI.SunPresets.cs`, `data/{lab_controls,sun_presets}.json`. Do NOT touch ground shader/material files.
- **Registry routing or it silently no-ops.** Every control id must have a matching `scene`/`cloud` target + a C# case; a `param`/`scene`/`cloud` naming a missing uniform silently no-ops (project gotcha) — verify ids.
- **Commit by default** (convention 2026-06-20): clean scoped commit per task. Co-author trailer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Build (one Godot at a time): `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`; kill strays first (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`).

---

## File Structure

- `shaders/cloud_sky.gdshader` — add 7 surface uniforms + noise helpers + surface modulation in `sun_layers()`.
- `scripts/lab/CloudVolume.cs` — add `SetSunSurface*` material-uniform setters + a `sun_surface_on` case in `SetKnobBool`.
- `scripts/lab/TerrainLabUI.Apply.cs` — route the new control ids (`ApplySceneFloat` cases + an `ApplyScene` bool case).
- `data/lab_controls.json` — Light-tab controls for the surface knobs.
- `data/sun_presets.json` — **NEW** preset definitions.
- `scripts/lab/TerrainLabUI.SunPresets.cs` — **NEW** loader + Light-tab dropdown + `ApplySunPreset` + the starter set application.
- `scripts/lab/TerrainLabUI.Cli.cs` — `--sunpreset=N`.
- `scripts/lab/TerrainLabUI.Review.cs` — extend review key 1 to cycle sun presets (A/B), mirroring key 3's palette cycle.

---

### Task 1: Procedural surface in `cloud_sky.gdshader` (default OFF)

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (uniforms after line 30; noise helpers before `sun_layers` at line 56; surface block inside `sun_layers` after `sunCol` is computed, ~line 72)

**Interfaces:**
- Produces (shader uniforms other tasks set): `sun_surface_on` (bool), `sun_surface_cells` (float), `sun_surface_contrast` (float), `sun_surface_spots` (float), `sun_surface_churn` (float), `sun_surface_warm` (float), `sun_surface_color` (vec3, source_color).

- [ ] **Step 1: Add the surface uniforms.** After the `sun_redden_color` uniform line (`shaders/cloud_sky.gdshader:30`), insert:

```glsl
// --- sun SURFACE (procedural granulation; gated, default off = flat disc) ---
uniform bool  sun_surface_on = false;
uniform float sun_surface_cells : hint_range(1.0, 40.0) = 9.0;     // granule frequency
uniform float sun_surface_contrast : hint_range(0.0, 1.0) = 0.0;   // 0 = flat (today), high = bold
uniform float sun_surface_spots : hint_range(0.0, 1.0) = 0.0;      // sunspot darkening
uniform float sun_surface_churn : hint_range(0.0, 1.0) = 0.15;     // animation speed (0 = static)
uniform float sun_surface_warm  : hint_range(0.0, 1.0) = 0.3;      // cell-vs-spot warm shift
uniform vec3  sun_surface_color : source_color = vec3(0.0);        // fantasy base override (black = off)
```

- [ ] **Step 2: Add noise helpers.** Immediately before `vec3 sun_layers(` (`shaders/cloud_sky.gdshader:56`), insert:

```glsl
float sun_hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float sun_vnoise(vec2 p){
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = sun_hash(i), b = sun_hash(i + vec2(1.0, 0.0));
    float c = sun_hash(i + vec2(0.0, 1.0)), d = sun_hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float sun_fbm(vec2 p){
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++){ s += a * sun_vnoise(p); p *= 2.02; a *= 0.5; }
    return s;   // ~0..1
}
```

- [ ] **Step 3: Add the surface modulation.** In `sun_layers()`, find the line `vec3 sunCol = mix(LIGHT0_COLOR, sun_redden_color, sunset);` (~line 72). Immediately AFTER it, insert:

```glsl
    if (sun_surface_on && discMask > 0.0) {
        // disc-local UVs: world-anchored tangent frame around the sun direction (no swimming).
        vec3 S = LIGHT0_DIRECTION;
        vec3 upv = abs(S.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
        vec3 T = normalize(cross(upv, S));
        vec3 Bt = cross(S, T);
        float rad = max(sin(radians(sun_size * grow)), 1e-4);
        vec2 pSurf = vec2(dot(rd, T), dot(rd, Bt)) / rad;          // ~unit-disc coords
        vec2 q = pSurf * sun_surface_cells + vec2(TIME * sun_surface_churn);
        float surf = sun_fbm(q);                                    // granulation, 0..1
        float spotField = sun_fbm(q * 0.5 + vec2(7.3, 2.1));
        float spot = smoothstep(0.55, 0.35, spotField);            // low field = dark spot
        float bright = mix(1.0 - sun_surface_contrast, 1.0 + sun_surface_contrast, surf);
        bright *= (1.0 - sun_surface_spots * spot);
        disc *= max(bright, 0.0);
        sunCol *= mix(vec3(1.0), sun_redden_color, sun_surface_warm * (1.0 - surf));  // warm cells
        if (dot(sun_surface_color, sun_surface_color) > 1e-4) {
            sunCol = mix(sunCol, sun_surface_color * (0.6 + 0.8 * surf), 0.85);        // fantasy override
        }
    }
```

- [ ] **Step 4: Verify it compiles + default-off is unchanged.** Run: `dotnet build WG16.csproj` (expect 0 errors), then headless import:
`"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import` (expect no `cloud_sky.gdshader` shader errors). Then windowed `scenes/review.tscn`, press `1` — the sun disc must look EXACTLY as before (surface off by default).

- [ ] **Step 5: Commit.** `git add shaders/cloud_sky.gdshader && git commit` — message: "Sun surface shader: procedural granulation in sun_layers (default off)".

---

### Task 2: Wire surface uniforms to CloudVolume setters + Light-tab controls

**Files:**
- Modify: `scripts/lab/CloudVolume.cs` (add setters near the existing `SetSunSize`/`SetSunLimb` setters; add a `sun_surface_on` case to `SetKnobBool`)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (`ApplySceneFloat` cases ~line 138; an `ApplyScene` bool case ~line 99)
- Modify: `data/lab_controls.json` (Light-tab controls, near the `sun_*` block ~line 161)

**Interfaces:**
- Consumes: the Task-1 shader uniforms.
- Produces (C# setters): `CloudVolume.SetSunSurfaceOn(bool)`, `SetSunSurfaceCells(float)`, `SetSunSurfaceContrast(float)`, `SetSunSurfaceSpots(float)`, `SetSunSurfaceChurn(float)`, `SetSunSurfaceWarm(float)`, `SetSunSurfaceColor(Color)`. Control ids: `sun_surface_on`, `sun_surface_cells`, `sun_surface_contrast`, `sun_surface_spots`, `sun_surface_churn`, `sun_surface_warm`.

- [ ] **Step 1: Add the CloudVolume setters.** In `scripts/lab/CloudVolume.cs`, immediately after the existing `SetSunHorizonGrow` setter (search `SetSunHorizonGrow`), add:

```csharp
public void SetSunSurfaceOn(bool on)     { _skyMat?.SetShaderParameter("sun_surface_on", on); }
public void SetSunSurfaceCells(float v)   { _skyMat?.SetShaderParameter("sun_surface_cells", v); }
public void SetSunSurfaceContrast(float v){ _skyMat?.SetShaderParameter("sun_surface_contrast", v); }
public void SetSunSurfaceSpots(float v)   { _skyMat?.SetShaderParameter("sun_surface_spots", v); }
public void SetSunSurfaceChurn(float v)   { _skyMat?.SetShaderParameter("sun_surface_churn", v); }
public void SetSunSurfaceWarm(float v)    { _skyMat?.SetShaderParameter("sun_surface_warm", v); }
public void SetSunSurfaceColor(Color c)   { _skyMat?.SetShaderParameter("sun_surface_color", new Vector3(c.R, c.G, c.B)); }
```

- [ ] **Step 2: Route the bool.** In `scripts/lab/TerrainLabUI.Apply.cs`, in the `ApplyScene` switch (the bool one, ~line 97), add a case:

```csharp
            case "sun_surface_on": _cloud?.SetSunSurfaceOn(on); break;
```

- [ ] **Step 3: Route the floats.** In `scripts/lab/TerrainLabUI.Apply.cs`, in the `ApplySceneFloat` switch, after the `case "sun_horizon_grow":` line (~line 137), add:

```csharp
            case "sun_surface_cells":    _cloud?.SetSunSurfaceCells(v); break;
            case "sun_surface_contrast": _cloud?.SetSunSurfaceContrast(v); break;
            case "sun_surface_spots":    _cloud?.SetSunSurfaceSpots(v); break;
            case "sun_surface_churn":    _cloud?.SetSunSurfaceChurn(v); break;
            case "sun_surface_warm":     _cloud?.SetSunSurfaceWarm(v); break;
```

- [ ] **Step 4: Add the Light-tab controls.** In `data/lab_controls.json`, after the `sun_horizon_grow` control entry (search `sun_horizon_grow`), add:

```json
    { "id": "sun_surface_on", "label": "sun surface", "tab": "Light", "type": "scene", "scene": "sun_surface_on", "default": false, "rand": false },
    { "id": "sun_surface_cells", "label": "sun surface cells", "tab": "Light", "type": "scenef", "scene": "sun_surface_cells", "min": 1.0, "max": 40.0, "default": 9.0, "rand": false },
    { "id": "sun_surface_contrast", "label": "sun surface contrast", "tab": "Light", "type": "scenef", "scene": "sun_surface_contrast", "min": 0.0, "max": 1.0, "default": 0.0, "rand": false },
    { "id": "sun_surface_spots", "label": "sun sunspots", "tab": "Light", "type": "scenef", "scene": "sun_surface_spots", "min": 0.0, "max": 1.0, "default": 0.0, "rand": false },
    { "id": "sun_surface_churn", "label": "sun surface churn", "tab": "Light", "type": "scenef", "scene": "sun_surface_churn", "min": 0.0, "max": 1.0, "default": 0.15, "rand": false },
    { "id": "sun_surface_warm", "label": "sun surface warm", "tab": "Light", "type": "scenef", "scene": "sun_surface_warm", "min": 0.0, "max": 1.0, "default": 0.3, "rand": false },
```

- [ ] **Step 5: Verify live.** `dotnet build` (0 errors) → headless `--import` → windowed `review.tscn`, press `1`, go to the **Light** tab, toggle **`sun surface`** ON and drag `sun surface contrast`/`cells`/`spots`/`churn` — the disc must visibly gain a moving granulated surface; `contrast 0` = flat. (Informal look — full judging is Task 4.) Confirm no registry "unknown control id" warnings in the log.

- [ ] **Step 6: Commit.** `git add scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json && git commit` — "Sun surface: CloudVolume setters + Light-tab controls".

---

### Task 3: Sun preset system (data + loader + picker + CLI)

**Files:**
- Create: `data/sun_presets.json`
- Create: `scripts/lab/TerrainLabUI.SunPresets.cs`
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (`--sunpreset=` parse + apply)

**Interfaces:**
- Consumes: the Task-2 control ids (applied via the registry `_byId` + `SetWidgetValue`) and `CloudVolume.SetSunSurfaceColor` (for the optional color override).
- Produces: `TerrainLabUI.LoadSunPresets()`, `TerrainLabUI.ApplySunPreset(int idx)`, `_sunPresets` list. CLI `--sunpreset=N`.

- [ ] **Step 1: Create the preset file** `data/sun_presets.json` with a neutral default (matches today's look so nothing changes until approved). Starter values are placeholders to be tuned at the Task-4 eye-gate:

```json
{
  "active": "neutral",
  "presets": {
    "neutral":        { "sun_surface_on": false, "sun_size": 0.6, "sun_limb": 0.55 },
    "realistic_midday": { "sun_surface_on": true, "sun_size": 0.8, "sun_limb": 0.7, "sun_surface_cells": 14.0, "sun_surface_contrast": 0.12, "sun_surface_spots": 0.1, "sun_surface_churn": 0.08, "sun_surface_warm": 0.2 },
    "golden_hour":    { "sun_surface_on": true, "sun_size": 1.4, "sun_limb": 0.5, "sun_surface_cells": 8.0, "sun_surface_contrast": 0.2, "sun_surface_spots": 0.1, "sun_surface_churn": 0.1, "sun_surface_warm": 0.6 },
    "living_star":    { "sun_surface_on": true, "sun_size": 1.2, "sun_limb": 0.6, "sun_surface_cells": 10.0, "sun_surface_contrast": 0.45, "sun_surface_spots": 0.25, "sun_surface_churn": 0.3, "sun_surface_warm": 0.4 },
    "blood_sun":      { "sun_surface_on": true, "sun_size": 1.6, "sun_limb": 0.4, "sun_surface_cells": 7.0, "sun_surface_contrast": 0.6, "sun_surface_spots": 0.5, "sun_surface_churn": 0.2, "sun_surface_warm": 0.9, "sun_surface_color": [0.7, 0.08, 0.04] },
    "alien":          { "sun_surface_on": true, "sun_size": 1.3, "sun_limb": 0.6, "sun_surface_cells": 12.0, "sun_surface_contrast": 0.5, "sun_surface_spots": 0.3, "sun_surface_churn": 0.25, "sun_surface_warm": 0.2, "sun_surface_color": [0.2, 0.8, 0.5] }
  }
}
```

- [ ] **Step 2: Create the loader + picker** `scripts/lab/TerrainLabUI.SunPresets.cs`, mirroring the cloud-preset path in `TerrainLabUI.Clouds.cs` (load list → Light-tab `OptionButton` → `ApplySunPreset`). Values route through the registry so they never silently no-op; the optional `sun_surface_color` array (no slider) is applied directly via the CloudVolume setter:

```csharp
using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    private readonly List<(string name, Godot.Collections.Dictionary values)> _sunPresets = new();

    private void LoadSunPresets()
    {
        if (_sunPresets.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/sun_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[sunpresets] data/sun_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys)
        {
            _sunPresets.Add((key.AsString(), pres[key].AsGodotDictionary()));
        }
        GD.Print($"[sunpresets] loaded {_sunPresets.Count}");
    }

    /// Build the Light-tab dropdown. Call from the Light-tab UI build (see Step 3 wiring).
    private void BuildSunPresetPicker(Container parent)
    {
        LoadSunPresets();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        pick.AddItem("— sun preset —", 0);
        for (int i = 0; i < _sunPresets.Count; i++) { pick.AddItem(_sunPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplySunPreset((int)idx - 1); } };
        parent.AddChild(pick);
    }

    public void ApplySunPreset(int idx)
    {
        if (idx < 0 || idx >= _sunPresets.Count) { return; }
        var vals = _sunPresets[idx].values;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "sun_surface_color")
            {
                var arr = vals[k].AsGodotArray();
                if (arr.Count >= 3) { _cloud?.SetSunSurfaceColor(new Color(arr[0].AsSingle(), arr[1].AsSingle(), arr[2].AsSingle())); }
                continue;
            }
            if (_byId.TryGetValue(id, out var c)) { SetWidgetValue(c, vals[k]); }
            else { GD.PushWarning($"[sunpresets] unknown control id '{id}' in preset '{_sunPresets[idx].name}'"); }
        }
        // a preset that omits sun_surface_color leaves the override as-is; reset it explicitly to off:
        if (!vals.ContainsKey("sun_surface_color")) { _cloud?.SetSunSurfaceColor(new Color(0, 0, 0)); }
        GD.Print($"[sunpresets] applied '{_sunPresets[idx].name}'");
    }
}
```

- [ ] **Step 3: Wire the picker into the Light tab.** In the Light-tab UI build (find where the Light tab is populated in `TerrainLabUI.Registry.cs` — the same place the mood picker is added via `_moodPick`), add a call `BuildSunPresetPicker(<the Light tab container>)`. Match the exact container variable used for the Light tab there.

- [ ] **Step 4: Add the CLI flag.** In `scripts/lab/TerrainLabUI.Cli.cs`, add a field near `_presetCli` (`private int _sunPresetCli = -1;`), parse it next to the `--preset=` handler (`else if (a.StartsWith("--sunpreset=")) { int.TryParse(a.Substring("--sunpreset=".Length), out _sunPresetCli); }`), and apply it where `--preset` is applied (`if (_sunPresetCli >= 0) { ApplySunPreset(_sunPresetCli); }`).

- [ ] **Step 5: Verify.** `dotnet build` (0 errors) → headless `--import` → windowed `review.tscn`: the Light tab shows a sun-preset dropdown; selecting `living_star` / `blood_sun` / `alien` visibly changes the disc; `--sunpreset=3` at launch applies one at startup. Check the log for no `[sunpresets] unknown control id` warnings.

- [ ] **Step 6: Commit.** `git add data/sun_presets.json scripts/lab/TerrainLabUI.SunPresets.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Registry.cs && git commit` — "Sun presets: data + loader + Light-tab picker + --sunpreset".

---

### Task 4: Review integration, eye-gate tuning, and default-flip

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Review.cs` (extend review key 1 to cycle sun presets)
- Modify: `data/sun_presets.json` (bake the user-approved tuned values; set `active`)
- Modify: `shaders/cloud_sky.gdshader` defaults + `data/lab_controls.json` (`sun_surface_on` default → true) — ONLY after approval
- Modify: `docs/NEEDS_REVIEW.md`, `docs/DECISIONS.md` (record verdicts)

**Interfaces:**
- Consumes: `ApplySunPreset(int)`, `_sunPresets` from Task 3.

- [ ] **Step 1: Extend review key 1 to cycle presets.** In `scripts/lab/TerrainLabUI.Review.cs` `case 1:`, after the existing sun knob sets, add a preset cycle mirroring `CyclePalette` (press 1 again to advance):

```csharp
                LoadSunPresets();
                if (_sunPresets.Count > 0) {
                    if (_lastPreset == 1) { _sunPresetIdx = (_sunPresetIdx + 1) % _sunPresets.Count; }
                    ApplySunPreset(_sunPresetIdx);
                }
```
Add the field `private int _sunPresetIdx;` near `_palIdx`, and update the title string to show the active preset name: `title = $"1 · Sun disc + surface  [{(_sunPresets.Count>0 ? _sunPresets[_sunPresetIdx].name : "?")}]  (press 1 to cycle)";`.

- [ ] **Step 2: Verify the cycle.** `dotnet build` → windowed `review.tscn`, press `1` repeatedly — it cycles neutral → realistic_midday → golden_hour → living_star → blood_sun → alien, disc updates each press. Fly close to the sun to judge the surface.

- [ ] **Step 3: EYE-GATE (user, live).** Drive the user through it: for each preset, fly close on the sun, judge surface believability, churn, no swimming/pole-spin (raise sun high via `time of day`/`sun height`), no clipping-to-white. Tune the `sun_surface_*` sliders live; record the values the user approves. **This is the gate — do not flip defaults until the user says a look is good.**

- [ ] **Step 4: Bake approved values.** Write the user-approved tuned numbers back into `data/sun_presets.json` (per preset). If the user approves a default surface look, set `sun_surface_on` default → `true` in BOTH `shaders/cloud_sky.gdshader` and `data/lab_controls.json`, and set `sun_presets.json` `"active"` to the approved default preset. If the user wants it opt-in only, leave defaults off.

- [ ] **Step 5: Record verdicts.** Add the per-preset verdict to `docs/NEEDS_REVIEW.md` (under a new "Sun surface + presets" item) and a one-line `docs/DECISIONS.md` entry. Note the surface closes the 3b "just a circle" polish note.

- [ ] **Step 6: Commit.** `git add -A && git commit` — "Sun surface + presets: eye-gated tuning + verdicts (3b polish closed)".

---

## Self-Review

**Spec coverage:** Part A surface shader → Task 1; uniforms full-range knobs → Tasks 1–2; animated churn → `sun_surface_churn` (Tasks 1–2); gating default-off → Task 1 + Task 4 flip; Part B preset system (json + picker + CLI) → Task 3; Part C starter set + eye-gate → Tasks 3–4; orthogonal-to-axes → presets set only `sun_*` ids (Task 3). Optional `sun_limb_flare` (spec stretch) intentionally omitted from v1 — re-add only if the eye-gate wants rim flares. Acceptance "no new RenderingDevice errors" — covered by the just-fixed cloud seam + per-task `--import`/windowed checks.

**Placeholder scan:** preset numeric values in Task 3 are explicit starting values, tuned at the Task-4 eye-gate (per the GPU/visual constraint) — not TODOs. No "add error handling"/"similar to" placeholders. Steps 3-3 and 3-4 reference exact existing patterns (`_moodPick`, `--preset=`) the implementer must match the local variable name for — flagged inline.

**Type consistency:** setter names (`SetSunSurfaceCells` etc.) and control ids (`sun_surface_cells` etc.) match across Tasks 1→2→3; `ApplySunPreset(int)`/`_sunPresets`/`_sunPresetIdx` consistent across Tasks 3→4.
