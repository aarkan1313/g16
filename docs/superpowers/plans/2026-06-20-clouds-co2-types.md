# Clouds CO-2 — Types (stratus shape-mode + cirrus 2D layer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two new cloud TYPES — a **stratus** shape-mode (flat overcast sheet) inside the volumetric raymarch, and **cirrus** as a cheap 2D wind-streaked layer in the sky shader — while leaving cumulus byte-for-byte unchanged, everything behind default-off/neutral toggles.

**Architecture:** Stratus reuses the per-layer buffer's reserved field 22 (`ShapeMode`, no stride growth — reserved in CO-1) and a small branch in `layer_density` (cellGate→sheet + reduced detail erosion), mirrored byte-identically across raymarch + shadow + shadow-check. Cirrus is a separate `cirrus_layer(rd)` in `cloud_sky.gdshader` (anisotropic wind-stretched `fbm3`, gated by elevation, day/night-faded) composited over sun/moon/stars and UNDER the volumetric cumulus dome, driven by a `CirrusState` push from `CloudVolume`. No new compute, no FieldCompute.

**Tech Stack:** Godot 4.6 mono (C#), GLSL compute (raymarch/shadow) + a `shader_type sky` shader (cirrus), `Std430Writer` buffer packing, data-driven lab UI.

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = long-term-best — lead with the better option, no shortcut for speed. (HANDOFF §2)
- **Discipline / default-off:** CO-2 is built **ahead of the CO-1 eye-gate at the user's explicit direction.** Everything ships **default-off / neutral so the approved look is untouched** and the work is opt-in + banked for the eye. cumulus (ShapeMode=0, cirrus_on=false) MUST be byte-unchanged. Do NOT flip any default on.
- **Shadow lockstep (the recurring CO risk):** the stratus density branch MUST be byte-identical across `cloud_raymarch.glsl`, `cloud_shadow.glsl`, AND `cloud_shadow_check.glsl` (all three carry `layer_density`). Verify with `--shadowcheck`. ShapeMode is field 22 = `LF(i,22)` = `layers[i*6+5].z` (the slot reserved in CO-1; stride stays 24, `layers[48]`, `LF *6`).
- **No TDD — GPU/visual.** Per-task verification = `dotnet build WG16.csproj` → headless `--import` → `--auto-shot` A/B + `--shadowcheck` + `--profmove`; the look-gate is the user flying `scenes/review.tscn`. Never judge motion from a still.
- **Coordination:** stay in sky/light files; `lab_controls.json` + `TerrainLabUI` core are shared → additive only, `git add` your paths explicitly (never `-A`). Commit by default; push only when asked. The ground chat edits the same branch.
- **Run (one Godot at a time, kill strays first):** `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` then `"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`. `--import` uses the `_console.exe`. Local-RD compute can't run `--headless`.
- Benign `not a valid texture` / `us is null` radiance-rebake lines are cosmetic — don't chase.

**Paths:** Godot windowed `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe` (+ `_console.exe` for `--import`); project `C:\Wg16\wg-16-project`.

---

## File Structure

| File | Responsibility | Change |
|------|---------------|--------|
| `scripts/lab/CloudLayers.cs` | Layer record + schema + packing | Add `ShapeMode` field; `Build` default 0; `Pack` writes index 22 (was reserved 0f). Stride unchanged (24). |
| `shaders/cloud_raymarch.glsl` | Sky cloud raymarch | `layer_density` reads ShapeMode → stratus sheet branch (cellGate→1, reduced detail). |
| `shaders/cloud_shadow.glsl` | Ground cloud-shadow | Byte-identical stratus branch. |
| `shaders/cloud_shadow_check.glsl` | Shadow diagnostic | Byte-identical stratus branch. |
| `shaders/cloud_sky.gdshader` | Sky composite | New cirrus uniforms + `cirrus_layer(rd)` + composite (over sun/moon, under cumulus dome). |
| `scripts/lab/CloudVolume.cs` | Cloud driver | `_shapeMode` + layer-0 override + SetKnob case; `CirrusState` fields + `SetCirrus*` setters pushing cirrus uniforms. |
| `scripts/lab/TerrainLabUI.Cli.cs` + `TerrainLabUI.cs` | CLI | `--stratus` + `--cirrus` startup overrides. |
| `data/lab_controls.json` | Clouds-tab controls | stratus knob + cirrus toggle/knobs (additive). |
| `scripts/lab/TerrainLabUI.Review.cs` | Review presets | Extend key 6 to cycle cumulus→stratus→cirrus states. |
| docs | Bank | DECISIONS / ROADMAP / NEEDS_REVIEW. |

---

## Background — exact current state

- Per-layer fields (stride 24, 6 vec4): 0-11 density, 12-18 lighting, **19 ProfileBottom · 20 ProfileTop · 21 Anvil · 22 reserved(→ShapeMode) · 23 reserved**. `LF(i,f)=layers[i*6+(f>>2)][f&3]`.
- `layer_density` (all 3 shaders, byte-identical) computes `shape`, then `cellGate` (clump fragmentation), then `shape *= type_gradient * height_profile`, then detail erosion. **Stratus = suppress cell fragmentation + reduce detail erosion** so the deck reads as a connected sheet.
- `cloud_sky.gdshader` `sky()`: `bg = background(rd); bg += stars; bg += sun; bg += moon;` then over-composites the cumulus dome (`outColor = bg*(1-c.a) + c.rgb`). Helpers available to reuse: `fbm3(vec3)`, `vnoise3(vec3)`, `dir_to_uv(vec3)`, `night_factor` uniform, `LIGHT0_DIRECTION/COLOR`.

---

## Task 1: Stratus shape-mode (field 22 + 3 shaders + knob, cumulus byte-unchanged)

**Files:** `scripts/lab/CloudLayers.cs`, `shaders/cloud_raymarch.glsl`, `shaders/cloud_shadow.glsl`, `shaders/cloud_shadow_check.glsl`, `scripts/lab/CloudVolume.cs`, `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Cli.cs`, `scripts/lab/TerrainLabUI.cs`

**Interfaces:**
- Produces: `CloudLayer.ShapeMode` (float, after `Anvil`, before `NoiseId`); `Pack` writes it at index 22. `layer_density(..., float shapeMode)` trailing param in all 3 shaders, fed `LF(i,22)`. `CloudVolume.SetKnob("shape_mode", v)` → `_shapeMode` drives layer 0. CLI `--stratus[=v]`.
- ShapeMode 0 = cumulus (today, exact); 1 = stratus sheet. Default 0 everywhere → no-op.

- [ ] **Step 1: Add `ShapeMode` to the record + Build + Pack (CloudLayers.cs)**

Record (add after `Anvil`):
```csharp
    float ProfileBottom, float ProfileTop, float Anvil, float ShapeMode,
    int NoiseId, bool Enabled);
```
`Build` (add to the profile line):
```csharp
            F("profile_bottom", 0f), F("profile_top", 1f), F("anvil", 0f), F("shape_mode", 0f),
```
`Pack` (replace the `packed[o + 22] = 0f;` reserved line):
```csharp
            packed[o + 21] = L.Anvil;         packed[o + 22] = L.ShapeMode;   // 22 = stratus shape-mode (CO-2)
            packed[o + 23] = 0f;              // reserved
```
Update the `Stride` comment to note 22 = ShapeMode (stride stays 24).

- [ ] **Step 2: Stratus branch in the RAYMARCH `layer_density`**

In `shaders/cloud_raymarch.glsl`, add a trailing param to the signature:
```glsl
                    float pBottom, float pTop, float anvil, float shapeMode){
```
Replace the cellGate apply + detail-erosion strength to blend toward a sheet when `shapeMode` is high. Change:
```glsl
    float cellGate = smoothstep(mix(0.80, 0.42, coverage), mix(1.0, 0.78, coverage), cell);
    shape *= cellGate;
```
to:
```glsl
    float cellGate = smoothstep(mix(0.80, 0.42, coverage), mix(1.0, 0.78, coverage), cell);
    cellGate = mix(cellGate, 1.0, shapeMode);   // stratus: don't fragment into clumps → a connected sheet
    shape *= cellGate;
```
and in the detail-erosion block change:
```glsl
        float erodeAmt = mix(0.45, 0.95, h) * ldetail * edgeBoost;
```
to:
```glsl
        float erodeAmt = mix(0.45, 0.95, h) * ldetail * edgeBoost * (1.0 - 0.7 * shapeMode);   // stratus: smoother (less cauliflower)
```
Then pass `LF(i,22)` in `density_all`'s call (both the value already passes 19/20/21 — append):
```glsl
            LF(i,19), LF(i,20), LF(i,21), LF(i,22));
```

- [ ] **Step 3: Mirror the stratus branch byte-identically into SHADOW + SHADOW-CHECK**

In `shaders/cloud_shadow.glsl` and `shaders/cloud_shadow_check.glsl`: add the same `float shapeMode` trailing param; add the same `cellGate = mix(cellGate, 1.0, shapeMode);` line after their `shape *= cellGate;`; multiply their `erodeAmt` by `(1.0 - 0.7 * shapeMode)`; and append `LF(i,22)` to their `density_all` `layer_density` call. (The shadow/check shaders' detail constants differ from the raymarch — only the `* (1.0 - 0.7*shapeMode)` factor and the cellGate line are added; do not otherwise touch them.)

- [ ] **Step 4: CloudVolume `_shapeMode` + knob + layer-0 override**

In `scripts/lab/CloudVolume.cs`, add near `_anvil`:
```csharp
    private float _shapeMode = 0f;   // CO-2: 0 cumulus (default) .. 1 stratus sheet (drives layer 0)
```
Add a `SetKnob` case (after `anvil`):
```csharp
            case "shape_mode":      _shapeMode = Mathf.Clamp(v, 0f, 1f); break;            // CO-2 cumulus↔stratus
```
In `PackLayers`, add `ShapeMode = _shapeMode` to the layer-0 `with {}` (next to `Anvil = av`):
```csharp
            ProfileBottom = pb, ProfileTop = pt, Anvil = av, ShapeMode = _shapeMode, NoiseId = 0, Enabled = true });
```

- [ ] **Step 5: lab_controls.json knob + `--stratus` CLI**

In `data/lab_controls.json`, after `cloud_anvil`:
```json
    { "id": "cloud_shape_mode", "label": "type: cumulus↔stratus (CO-2)", "tab": "Clouds", "type": "cloudf", "cloud": "shape_mode", "min": 0.0, "max": 1.0, "default": 0.0, "rand": false },
```
In `scripts/lab/TerrainLabUI.Cli.cs` add a field + parse (mirror `--perdeck`):
```csharp
    private float _stratusCli = -1f;   // --stratus[=v]: stratus shape-mode (0..1); bare flag = 1
```
```csharp
            else if (a.StartsWith("--stratus")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=')+1) : "1"; float.TryParse(s, out _stratusCli); }
```
In `scripts/lab/TerrainLabUI.cs` (cloud CLI block, after `--cloudprofile`):
```csharp
        if (_stratusCli >= 0f) { _cloud.SetKnob("shape_mode", _stratusCli); GD.Print($"[stratus] shape_mode={_stratusCli}"); }
```

- [ ] **Step 6: Build + import**
```
dotnet build WG16.csproj    # 0 errors
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import   # no glsl: errors
```

- [ ] **Step 7: Verify cumulus byte-unchanged (ShapeMode default 0 = no-op)**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5 --auto-shot=/c/tmp/co2_t1_cumulus.png
```
Diff terrain region vs a CO-1-era baseline shot — terrain must be pixel-identical (sky differs only by drift). The packed buffer's index 22 went from constant 0f to `ShapeMode` (default 0f) → identical bytes.

- [ ] **Step 8: Verify stratus makes a sheet + shadow lockstep holds**
```
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.6 --stratus=1 --auto-shot=/c/tmp/co2_t1_stratus.png
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.6 --stratus=1 --shadowcheck --auto-shot=/c/tmp/co2_t1_stratuscheck.png
```
Read `co2_t1_stratus.png`: clouds read as a flatter connected overcast sheet (not broken cumulus clumps). `--shadowcheck` PASS (r > 0.6) with the stratus field — the shadow still tracks the (now sheet-like) cloud overhead. Record r in the commit.

- [ ] **Step 9: Commit**
```bash
git add scripts/lab/CloudLayers.cs shaders/cloud_raymarch.glsl shaders/cloud_shadow.glsl shaders/cloud_shadow_check.glsl scripts/lab/CloudVolume.cs data/lab_controls.json scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "clouds(CO-2): stratus shape-mode (field 22, cellGate->sheet, default cumulus)

ShapeMode per-layer field 22 (reserved in CO-1, no stride growth); layer_density
blends cellGate->1 + reduces detail erosion for a connected overcast sheet,
byte-identical across raymarch+shadow+check. Default 0 = cumulus byte-unchanged.
Clouds-tab 'type: cumulus<->stratus' knob + --stratus CLI. --shadowcheck PASS.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Cirrus 2D layer in the sky shader (default off = no-op)

**Files:** `shaders/cloud_sky.gdshader`

**Interfaces:**
- Produces: cirrus uniforms (`cirrus_on`, `cirrus_coverage`, `cirrus_density`, `cirrus_wind_deg`, `cirrus_speed`, `cirrus_scale`, `cirrus_sharpness`, `cirrus_color`); `vec4 cirrus_layer(vec3 rd)`; composite in `sky()` after `moon_layers`, before the dome. `cirrus_on=false` → returns `vec4(0)` → sky byte-unchanged.

- [ ] **Step 1: Add cirrus uniforms**

After the stars/milky-way uniforms (around line 63) add:
```glsl
// --- CIRRUS (CO-2): cheap 2D wind-streaked high layer, composited under the cumulus dome. default off. ---
uniform bool  cirrus_on = false;
uniform float cirrus_coverage : hint_range(0.0, 1.0) = 0.5;   // how much sky has streaks
uniform float cirrus_density : hint_range(0.0, 1.0) = 0.6;    // opacity of the streaks
uniform float cirrus_wind_deg = 30.0;                          // streak direction (degrees)
uniform float cirrus_speed = 0.002;                            // drift along the wind
uniform float cirrus_scale = 1.4;                              // streak frequency
uniform float cirrus_sharpness : hint_range(0.0, 1.0) = 0.5;  // soft wisps ↔ crisp filaments
uniform vec3  cirrus_color : source_color = vec3(0.92, 0.94, 1.0);
```

- [ ] **Step 2: Add `cirrus_layer(rd)` (after `stars_layer`, before `sky`)**
```glsl
// CIRRUS (CO-2): anisotropic wind-stretched fbm3 → high thin filaments. Streaks run ALONG the wind
// (cross-wind varies faster). Gated by elevation (fades at the horizon), tinted warm near the sun,
// dimmed at night. Cheap: one fbm3 tap. Returns premultiply-friendly (rgb, alpha).
vec4 cirrus_layer(vec3 rd){
    if (!cirrus_on || rd.y < 0.02) { return vec4(0.0); }
    float ang = radians(cirrus_wind_deg);
    vec2 w = vec2(cos(ang), sin(ang));
    vec2 hv = rd.xz / max(rd.y, 0.15);                 // project the view onto the sky plane
    float along = dot(hv, w);
    float cross = dot(hv, vec2(-w.y, w.x));
    vec3 sp = vec3(along * cirrus_scale + TIME * cirrus_speed, cross * cirrus_scale * 6.0, 0.0);
    float n = fbm3(sp);
    float streak = smoothstep(1.0 - cirrus_coverage, 1.0, n);
    streak = pow(streak, mix(1.0, 4.0, cirrus_sharpness));
    float band = smoothstep(0.02, 0.18, rd.y) * (1.0 - 0.3 * smoothstep(0.85, 1.0, rd.y));
    float a = clamp(streak * band * cirrus_density, 0.0, 1.0);
    // sun tint: warm the streaks near the sun direction (forward scatter)
    float toSun = max(dot(normalize(rd), LIGHT0_DIRECTION), 0.0);
    vec3 col = mix(cirrus_color, sun_redden_color, 0.4 * pow(toSun, 8.0)) * mix(0.6, 1.2, LIGHT0_COLOR.r);
    a *= mix(0.12, 1.0, 1.0 - night_factor);           // mostly fade out at night
    return vec4(col, a);
}
```

- [ ] **Step 3: Composite cirrus in `sky()` (over sun/moon, under the cumulus dome)**

Change:
```glsl
    bg += stars_layer(rd);     // behind the sun/moon/clouds (they composite over)
    bg += sun_layers(rd);
    bg += moon_layers(rd);

    vec3 outColor = bg;
```
to:
```glsl
    bg += stars_layer(rd);     // behind the sun/moon/clouds (they composite over)
    bg += sun_layers(rd);
    bg += moon_layers(rd);

    vec4 ci = cirrus_layer(rd);              // CO-2: high thin layer, OVER sun/moon, UNDER cumulus
    bg = bg * (1.0 - ci.a) + ci.rgb * ci.a;

    vec3 outColor = bg;
```

- [ ] **Step 4: Import (compile-check the sky shader)**
```
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import
```
Expected: no compile error for `cloud_sky.gdshader` (a `.gdshader` error surfaces at scene load; if uncertain, launch the scene — Step 5).

- [ ] **Step 5: Verify default-off no-op**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.4 --auto-shot=/c/tmp/co2_t2_cirrus_off.png
```
`cirrus_on` defaults false → the shot must match the pre-Task-2 sky (cirrus returns vec4(0); the composite is `bg*1 + 0`). No exceptions; sky shader compiles.

- [ ] **Step 6: Commit**
```bash
git add shaders/cloud_sky.gdshader
git commit -m "clouds(CO-2): cirrus 2D wind-streaked sky layer (default off)

cirrus_layer(rd): anisotropic wind-stretched fbm3 high filaments, elevation-gated,
sun-tinted, night-faded; composited over sun/moon and under the cumulus dome.
cirrus_on=false (default) -> vec4(0) -> sky byte-unchanged. Uniforms only; driver
push next task.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Cirrus driver (CloudVolume push) + Clouds-tab knobs + CLI + review wiring

**Files:** `scripts/lab/CloudVolume.cs`, `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Cli.cs`, `scripts/lab/TerrainLabUI.cs`, `scripts/lab/TerrainLabUI.Review.cs`

**Interfaces:**
- Consumes: the cirrus uniforms (Task 2).
- Produces: `CloudVolume` setters `SetCirrusOn(bool)`, `SetCirrus(string knob, float)` (or individual `SetCirrus*`); `SetKnobBool("cirrus_on", ...)` + `SetKnob("cirrus_*", ...)` cases routing to `_skyMat.SetShaderParameter`. CLI `--cirrus[=cov]`. Review key 6 cycles cumulus → stratus → cirrus.

- [ ] **Step 1: CloudVolume cirrus setters**

In `scripts/lab/CloudVolume.cs`, add near the other `_skyMat?.SetShaderParameter` setters (e.g. after the stars setters):
```csharp
    // --- CIRRUS (CO-2): 2D sky-layer uniforms (default off) ---
    public void SetCirrusOn(bool on)        { _skyMat?.SetShaderParameter("cirrus_on", on); }
    public void SetCirrus(string knob, float v)
    {
        switch (knob)
        {
            case "cirrus_coverage":  _skyMat?.SetShaderParameter("cirrus_coverage", Mathf.Clamp(v, 0f, 1f)); break;
            case "cirrus_density":   _skyMat?.SetShaderParameter("cirrus_density", Mathf.Clamp(v, 0f, 1f)); break;
            case "cirrus_wind_deg":  _skyMat?.SetShaderParameter("cirrus_wind_deg", v); break;
            case "cirrus_speed":     _skyMat?.SetShaderParameter("cirrus_speed", v); break;
            case "cirrus_scale":     _skyMat?.SetShaderParameter("cirrus_scale", Mathf.Max(0.1f, v)); break;
            case "cirrus_sharpness": _skyMat?.SetShaderParameter("cirrus_sharpness", Mathf.Clamp(v, 0f, 1f)); break;
        }
    }
```

- [ ] **Step 2: Route cirrus knobs through the cloud appliers (TerrainLabUI.Clouds.cs)**

In `scripts/lab/TerrainLabUI.Clouds.cs`, in `ApplyCloudBool` add (before the `SetKnobBool` fallback):
```csharp
        if (knob == "cirrus_on") { _cloud?.SetCirrusOn(on); return; }
```
and in `ApplyCloudFloat` add (before the `_cloud?.SetKnob(knob, v)` fallback):
```csharp
        if (knob.StartsWith("cirrus_")) { _cloud?.SetCirrus(knob, v); return; }
```

- [ ] **Step 3: lab_controls.json cirrus controls**

After `cloud_shape_mode`:
```json
    { "id": "cloud_cirrus_on", "label": "cirrus layer (CO-2)", "tab": "Clouds", "type": "cloud", "cloud": "cirrus_on", "default": false, "rand": false },
    { "id": "cloud_cirrus_cov", "label": "  cirrus: coverage", "tab": "Clouds", "type": "cloudf", "cloud": "cirrus_coverage", "min": 0.0, "max": 1.0, "default": 0.5, "rand": false },
    { "id": "cloud_cirrus_den", "label": "  cirrus: density", "tab": "Clouds", "type": "cloudf", "cloud": "cirrus_density", "min": 0.0, "max": 1.0, "default": 0.6, "rand": false },
    { "id": "cloud_cirrus_wind", "label": "  cirrus: wind dir", "tab": "Clouds", "type": "cloudf", "cloud": "cirrus_wind_deg", "min": 0.0, "max": 360.0, "default": 30.0, "rand": false },
    { "id": "cloud_cirrus_scale", "label": "  cirrus: scale", "tab": "Clouds", "type": "cloudf", "cloud": "cirrus_scale", "min": 0.3, "max": 4.0, "default": 1.4, "rand": false },
    { "id": "cloud_cirrus_sharp", "label": "  cirrus: sharpness", "tab": "Clouds", "type": "cloudf", "cloud": "cirrus_sharpness", "min": 0.0, "max": 1.0, "default": 0.5, "rand": false },
```

- [ ] **Step 4: `--cirrus` CLI**

In `scripts/lab/TerrainLabUI.Cli.cs`:
```csharp
    private float _cirrusCli = -1f;   // --cirrus[=cov]: enable cirrus + set coverage; bare flag = 0.5
```
```csharp
            else if (a.StartsWith("--cirrus")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=')+1) : "0.5"; float.TryParse(s, out _cirrusCli); }
```
In `scripts/lab/TerrainLabUI.cs` (cloud CLI block, after `--stratus`):
```csharp
        if (_cirrusCli >= 0f) { _cloud.SetCirrusOn(true); _cloud.SetCirrus("cirrus_coverage", _cirrusCli); GD.Print($"[cirrus] on cov={_cirrusCli}"); }
```

- [ ] **Step 5: Review key 6 — cycle cumulus → stratus → cirrus**

In `scripts/lab/TerrainLabUI.Review.cs`, replace the case-6 body so repeat presses cycle a small state machine (mirroring the existing `_co1ProfileOn` toggle but 3-state). Add a field next to `_co1ProfileOn`:
```csharp
    private int _co2Type;   // 0 cumulus+profile A/B (CO-1) · 1 stratus · 2 cirrus (review key 6 cycles)
```
Replace case 6 with:
```csharp
            case 6: // Clouds CO-1/CO-2 types — press 6 to cycle cumulus(profile A/B) → stratus → cirrus
                if (_lastPreset != 6) { ApplyMood(5); Set("cloud_enabled", true); Set("cloud_coverage", 0.55f); LookUpAtClouds(); _co2Type = 0; _co1ProfileOn = false; }
                else { _co2Type = (_co2Type + 1) % 3; }
                // reset all type levers, then enable the selected one
                Set("cloud_shape_mode", _co2Type == 1 ? 1.0f : 0.0f);
                Set("cloud_cirrus_on", _co2Type == 2);
                Set("cloud_profile_on", _co2Type == 0 && _co1ProfileOn);
                if (_co2Type == 0) { _co1ProfileOn = !_co1ProfileOn; Set("cloud_profile_on", _co1ProfileOn); }
                title = _co2Type switch {
                    1 => "6 · Clouds CO-2: STRATUS sheet  (press 6 → cirrus)",
                    2 => "6 · Clouds CO-2: CIRRUS layer  (press 6 → cumulus)",
                    _ => $"6 · Clouds CO-1: cumulus vertical profile {(_co1ProfileOn ? "ON" : "OFF")}  (press 6 → stratus)"
                };
                judge = _co2Type switch {
                    1 => "Stratus: a flat connected overcast SHEET (not cumulus clumps)? Tune Clouds 'type: cumulus↔stratus' + coverage/density.",
                    2 => "Cirrus: believable high WIND-STREAKED filaments, thin/semi-transparent, fading at the horizon, warm near the sun? Tune Clouds 'cirrus: *'. Cumulus still composites over it.",
                    _ => "Cumulus vertical profile (CO-1): ON reads as a 3D volume, OFF = approved slab. Press 6 again to cycle to stratus."
                };
                break;
```
(The `_co2Type==0` block double-sets `cloud_profile_on`; keep the second set as the source of truth — the first line resets it, the toggle flips it. Verify the banner shows the right ON/OFF.)

- [ ] **Step 6: Build + import**
```
dotnet build WG16.csproj    # 0 errors
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import
```

- [ ] **Step 7: Self-check cirrus appears + day/night fade**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.3 --cirrus=0.6 --auto-shot=/c/tmp/co2_t3_cirrus_day.png
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.3 --cirrus=0.6 --time=23 --auto-shot=/c/tmp/co2_t3_cirrus_night.png
```
Read both: day shows wind-streaked cirrus filaments; night shows them mostly faded. Sky-region diff vs the `cirrus_off` baseline confirms a structured change.

- [ ] **Step 8: Commit**
```bash
git add scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Clouds.cs data/lab_controls.json scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Review.cs
git commit -m "clouds(CO-2): cirrus driver + Clouds-tab knobs + CLI + review cycle

CloudVolume.SetCirrusOn/SetCirrus push the cirrus uniforms; cirrus_*/cirrus_on
route via the cloud appliers; Clouds-tab toggle+knobs; --cirrus CLI. Review key 6
now cycles cumulus(profile A/B) -> stratus -> cirrus for the gate. All default-off.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Bank CO-2 for the eye-gate

**Files:** `docs/DECISIONS.md`, `docs/ROADMAP.md`, `docs/NEEDS_REVIEW.md`

- [ ] **Step 1:** Prepend a DECISIONS entry: CO-2 built (stratus shape-mode field 22 lockstep across 3 shaders; cirrus 2D layer in cloud_sky); default-off/neutral so cumulus byte-unchanged; mechanically verified (cumulus no-op, `--shadowcheck` PASS, cirrus appears + night-fades); built ahead of CO-1's gate at user direction; awaiting the live eye.
- [ ] **Step 2:** ROADMAP #2: mark CO-2 BUILT (default-off, awaiting gate); CO-3 next after CO-1+CO-2 pass.
- [ ] **Step 3:** NEEDS_REVIEW: extend item 9 (or add 9b) — how to see stratus (`--stratus=1` / key 6) + cirrus (`--cirrus=0.6` / key 6), what to judge, that cumulus is untouched.
- [ ] **Step 4:** Commit docs (stage only the 3 doc paths).

---

## Self-Review (against the CO-2 spec)

**Spec coverage:** "stratus shape-mode in the raymarch" → Task 1 (cellGate→sheet + reduced detail, field 22). "cirrus as a separate 2D striated layer in cloud_sky.gdshader" → Task 2 (`cirrus_layer`, wind-stretched fbm3). "cumulus preserved" → ShapeMode/cirrus default 0/off, no-op verified (T1.7, T2.5). "CirrusState push (SetCirrus*)" → Task 3 Step 1. "Clouds-tab gains the new controls" → T1.5 + T3.3. "cirrus fades day↔night + tints at sunset" → T2.2 (`night_factor` fade + `sun_redden_color` near-sun tint). Gate criteria (believable cirrus lines, stratus sheet, cumulus unchanged) → Task 4 / review key 6. Shadow lockstep risk → T1.3 + `--shadowcheck` (T1.8). Cirrus banding risk → multi-octave `fbm3` + animated drift + sharpness control. Perf (cirrus ~free) → one `fbm3` tap; `--profmove` advisable at the gate. ✅

**Out of scope (correctly excluded):** weather-axis tie-in; unified all-volumetric cirrus; CO-3 anti-repetition; CO-4 presets.

**Placeholder scan:** all steps show full code/commands. ✅

**Type consistency:** `ShapeMode` (record/Build/Pack index 22 / `LF(i,22)` / `_shapeMode` / knob `shape_mode`) consistent across T1. Cirrus uniform names (`cirrus_on/coverage/density/wind_deg/speed/scale/sharpness/color`) identical in shader (T2.1), setters (T3.1), JSON `cloud` keys (T3.3), routing (T3.2). Stride stays 24 / `layers[48]` / `LF *6` (unchanged from CO-1). ✅
