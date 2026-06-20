# Sun Disc Polish (Stage 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the flat white sun dot into a believable, fully-tunable sun — limb-darkened disc + corona + warm atmospheric halo + horizon reddening/growth + soft optical-depth cloud occlusion — all in `cloud_sky.gdshader`.

**Architecture:** All sun rendering stays in the `sky()` shader (so clouds keep occluding it). The flat disc + single `pow` glow is replaced by a `sun_layers(rd)` function building four HDR pieces from `cosSun` (view·sun) and `sunElev` (`LIGHT0_DIRECTION.y`). New uniforms are set on the sky `ShaderMaterial` via `CloudVolume` setters, exposed as Light-tab `scenef` controls routed through `ApplySceneFloat`, and folded into the 6 moods + sun presets.

**Tech Stack:** Godot 4.6 `shader_type sky` GLSL; C# (CloudVolume owns `_skyMat`); JSON registry (`lab_controls.json`, `lighting_moods.json`, `cloud_presets.json`).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-06-19-sun-disc-polish-design.md`. Stage 1 of the Sun & Light arc.
- **Full-range tunable:** physical defaults, every piece a knob (realistic → stylized → fantasy). No hardcoded look.
- All sun rendering stays inside `sky()` in `shaders/cloud_sky.gdshader`. Clouds composite OVER the sun via the existing premultiplied over-composite `bg*(1-a) + cloud.rgb`.
- Keep `sun_disc_energy` decoupled from `LIGHT0_ENERGY` (overcast dims the directional light but NOT the visible disc).
- Stay LOCAL to the sun: do NOT recolor the broad sky gradient (`sky_top`/`sky_horizon`) — that's Stage 2.
- New sky-shader uniforms: add to `cloud_sky.gdshader` + a `CloudVolume.SetX()` setter (sets `_skyMat`) + an `ApplySceneFloat` case (`scripts/lab/TerrainLabUI.Apply.cs`) + a `lab_controls.json` row (tab `Light`, type `scenef`) + a `Set(...)` line in `SyncLightControlsToScene` (Apply.cs).
- **Verification is windowed visual/numeric** (no shader unit tests). Launch absolute path (memory `wg16-launch-absolute-path`):
  `"<Godot>" --path C:/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- <args>`
  Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- Build check: `dotnet build WG16.csproj -c Debug -v quiet 2>&1 | grep -E "Build succeeded|error CS"`.
- Commit after each task (project uses git; `experiment/presentation` is HEAD). If `git` reports not a repo, skip commits and note it.

---

### Task 1: Extract sun into `sun_layers(rd)` (pure refactor, no visual change)

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (the `LIGHT0_ENABLED` block in `sky()`, ~lines 50–57)

**Interfaces:**
- Produces: `vec3 sun_layers(vec3 rd)` — returns the sun's additive HDR contribution for view dir `rd`; `vec3(0)` if the sun is disabled. Later tasks rebuild its body.

- [ ] **Step 1: Add the function above `sky()`** (after `dir_to_uv`, before `void sky()`):

```glsl
// All sun rendering lives here so clouds composite OVER it. Built from cosSun (view·sun) and
// sunElev (LIGHT0_DIRECTION.y). Pieces are layered in later tasks; this is the current look verbatim.
vec3 sun_layers(vec3 rd) {
    if (!LIGHT0_ENABLED) { return vec3(0.0); }
    float cosSun = dot(rd, LIGHT0_DIRECTION);
    float disc = smoothstep(0.9994, 0.9998, cosSun);          // ~0.6° disc
    float glow = pow(max(cosSun, 0.0), 220.0) * 0.5;          // halo
    return LIGHT0_COLOR * sun_disc_energy * (disc * 12.0 + glow);
}
```

- [ ] **Step 2: Replace the inline sun block in `sky()`** — delete the old `if (LIGHT0_ENABLED){...}` block and use:

```glsl
    bg += sun_layers(rd);
```

- [ ] **Step 3: Build**

Run: `dotnet build WG16.csproj -c Debug -v quiet 2>&1 | grep -E "Build succeeded|error CS"`
Expected: `Build succeeded.`

- [ ] **Step 4: Verify the look is unchanged**

Run (clear sky so the bare disc shows):
```
"<Godot>" --path C:/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --clouds=0 --godrays=0 --lookatsun --auto-shot=C:/tmp/sun/t1.png
```
Expected: a windowed run that quits after ~1.5s and writes `t1.png`; the sun looks identical to the pre-refactor dot (a pale disc upper-left, faint glow). No shader compile errors in stdout.

- [ ] **Step 5: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_sky.gdshader
git -C /c/Wg16/wg-16-project commit -m "refactor(sun): extract sun_layers() in cloud_sky (no visual change)"
```

---

### Task 2: Limb-darkened disc + `sun_size`

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (uniforms block; `sun_layers`)
- Modify: `scripts/lab/CloudVolume.cs` (add setters near `SetSunDiscEnergy`, ~line 445)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (`ApplySceneFloat` switch ~line 105; `SyncLightControlsToScene` ~line 146)
- Modify: `data/lab_controls.json` (Light tab rows)

**Interfaces:**
- Consumes: `sun_layers(vec3)` from Task 1.
- Produces: `CloudVolume.SetSunSize(float deg)`, `CloudVolume.SetSunLimb(float)`; sky uniforms `sun_size`, `sun_limb`.

- [ ] **Step 1: Add uniforms** to `cloud_sky.gdshader` (near `sun_disc_energy`):

```glsl
uniform float sun_size = 0.6;    // angular disc radius in DEGREES (visible sun size)
uniform float sun_limb = 0.55;   // limb darkening: 0 = flat disc, 1 = strong center→rim falloff
```

- [ ] **Step 2: Rebuild the disc in `sun_layers`** with limb darkening (replace the `disc`/`glow`/return lines, keep `glow` for now):

```glsl
    float cosSun = dot(rd, LIGHT0_DIRECTION);
    float discEdge = cos(radians(sun_size));                       // cosSun at the disc rim
    float mu = clamp((cosSun - discEdge) / max(1.0 - discEdge, 1e-6), 0.0, 1.0);  // 0 rim → 1 center
    float discMask = smoothstep(0.0, 0.04, mu);                    // soft rim
    float limb = mix(1.0, mix(0.45, 1.0, sqrt(mu)), sun_limb);     // center-bright → dimmer warm rim
    float disc = discMask * limb;
    float glow = pow(max(cosSun, 0.0), 220.0) * 0.5;               // (replaced in Task 3)
    return LIGHT0_COLOR * sun_disc_energy * (disc * 12.0 + glow);
```

- [ ] **Step 3: Add CloudVolume setters** (after `SetSunDiscEnergy`):

```csharp
public void SetSunSize(float deg) { _skyMat?.SetShaderParameter("sun_size", Mathf.Clamp(deg, 0.05f, 8f)); }
public void SetSunLimb(float v)   { _skyMat?.SetShaderParameter("sun_limb", Mathf.Clamp(v, 0f, 1f)); }
```

- [ ] **Step 4: Route in `ApplySceneFloat`** (add cases in the switch):

```csharp
            case "sun_size":  _cloud?.SetSunSize(v); break;
            case "sun_limb":  _cloud?.SetSunLimb(v); break;
```

- [ ] **Step 5: Add Light-tab controls** to `data/lab_controls.json` (after the `sun_disc` row):

```json
    { "id": "sun_size", "label": "sun size (deg)", "tab": "Light", "type": "scenef", "scene": "sun_size", "min": 0.1, "max": 4.0, "default": 0.6, "rand": false },
    { "id": "sun_limb", "label": "sun limb darken", "tab": "Light", "type": "scenef", "scene": "sun_limb", "min": 0.0, "max": 1.0, "default": 0.55, "rand": false },
```

- [ ] **Step 6: Build + verify**

Run: `dotnet build WG16.csproj -c Debug -v quiet 2>&1 | grep -E "Build succeeded|error CS"` → `Build succeeded.`
Run: `"<Godot>" ... -- --clouds=0 --godrays=0 --lookatsun --auto-shot=C:/tmp/sun/t2.png`
Expected: the disc now has a center-bright → rim-darker falloff (not a flat fill). Optional numeric check (PowerShell `GetPixel`): the disc-center pixel is brighter than a disc-rim pixel. The "sun size (deg)" slider visibly grows/shrinks the disc.

- [ ] **Step 7: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "feat(sun): limb-darkened disc + sun_size/sun_limb controls"
```

---

### Task 3: Corona + atmospheric halo (replace the single `pow` glow)

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (uniforms; `sun_layers`)
- Modify: `scripts/lab/CloudVolume.cs`; `scripts/lab/TerrainLabUI.Apply.cs`; `data/lab_controls.json`

**Interfaces:**
- Produces: `CloudVolume.SetSunCorona(float size, float energy)`, `CloudVolume.SetSunHalo(float size, float energy)`; uniforms `sun_corona_size`, `sun_corona_energy`, `sun_halo_size`, `sun_halo_energy`.

- [ ] **Step 1: Add uniforms** to `cloud_sky.gdshader`:

```glsl
uniform float sun_corona_size : hint_range(40.0, 6000.0) = 1200.0;  // tight inner bloom (higher = tighter)
uniform float sun_corona_energy = 2.0;
uniform float sun_halo_size : hint_range(8.0, 400.0) = 90.0;        // wide soft glow (lower = wider)
uniform float sun_halo_energy = 0.4;
```

- [ ] **Step 2: Replace the `glow` line** in `sun_layers` with a two-scale corona + halo:

```glsl
    float corona = pow(max(cosSun, 0.0), sun_corona_size) * sun_corona_energy;  // searing inner ring
    float halo   = pow(max(cosSun, 0.0), sun_halo_size)   * sun_halo_energy;    // wide warm forward-scatter
    return LIGHT0_COLOR * sun_disc_energy * (disc * 12.0 + corona + halo);
```
(Delete the old `float glow = ...` line.)

- [ ] **Step 3: CloudVolume setters:**

```csharp
public void SetSunCorona(float size, float energy) {
    _skyMat?.SetShaderParameter("sun_corona_size", Mathf.Clamp(size, 40f, 6000f));
    _skyMat?.SetShaderParameter("sun_corona_energy", Mathf.Max(energy, 0f));
}
public void SetSunHalo(float size, float energy) {
    _skyMat?.SetShaderParameter("sun_halo_size", Mathf.Clamp(size, 8f, 400f));
    _skyMat?.SetShaderParameter("sun_halo_energy", Mathf.Max(energy, 0f));
}
```

- [ ] **Step 4: Route in `ApplySceneFloat`** (each slider sets one field; read the other from a cached value or re-pass current — simplest: split into 4 cases each setting its own uniform directly via new single-field setters). Add single-field setters to CloudVolume instead:

```csharp
public void SetSunCoronaSize(float v)   { _skyMat?.SetShaderParameter("sun_corona_size", Mathf.Clamp(v, 40f, 6000f)); }
public void SetSunCoronaEnergy(float v) { _skyMat?.SetShaderParameter("sun_corona_energy", Mathf.Max(v, 0f)); }
public void SetSunHaloSize(float v)     { _skyMat?.SetShaderParameter("sun_halo_size", Mathf.Clamp(v, 8f, 400f)); }
public void SetSunHaloEnergy(float v)   { _skyMat?.SetShaderParameter("sun_halo_energy", Mathf.Max(v, 0f)); }
```
And in `ApplySceneFloat`:
```csharp
            case "sun_corona_size":   _cloud?.SetSunCoronaSize(v); break;
            case "sun_corona_energy": _cloud?.SetSunCoronaEnergy(v); break;
            case "sun_halo_size":     _cloud?.SetSunHaloSize(v); break;
            case "sun_halo_energy":   _cloud?.SetSunHaloEnergy(v); break;
```
(Remove the two-arg `SetSunCorona`/`SetSunHalo` from Step 3 — the single-field setters replace them.)

- [ ] **Step 5: Light-tab controls** in `data/lab_controls.json`:

```json
    { "id": "sun_corona_size",   "label": "sun corona size", "tab": "Light", "type": "scenef", "scene": "sun_corona_size",   "min": 40,  "max": 6000, "default": 1200, "rand": false },
    { "id": "sun_corona_energy", "label": "sun corona",      "tab": "Light", "type": "scenef", "scene": "sun_corona_energy", "min": 0,   "max": 6,    "default": 2.0,  "rand": false },
    { "id": "sun_halo_size",     "label": "sun halo size",   "tab": "Light", "type": "scenef", "scene": "sun_halo_size",     "min": 8,   "max": 400,  "default": 90,   "rand": false },
    { "id": "sun_halo_energy",   "label": "sun halo",        "tab": "Light", "type": "scenef", "scene": "sun_halo_energy",   "min": 0,   "max": 2,    "default": 0.4,  "rand": false },
```

- [ ] **Step 6: Build + verify**

Build → `Build succeeded.` Capture `--clouds=0 --godrays=0 --lookatsun --auto-shot=C:/tmp/sun/t3.png`.
Expected: a tight intense **corona** hugging the disc plus a noticeably **wider soft halo** fading into the sky (vs the old single faint glow). The four sliders each change the look (corona size = tightness, halo size = spread). Engine glow should bloom the corona without washing the whole frame.

- [ ] **Step 7: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "feat(sun): corona + atmospheric halo (two-scale glow) + controls"
```

---

### Task 4: Horizon reddening + growth (sun elevation driven)

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (uniforms; `sun_layers`)
- Modify: `scripts/lab/CloudVolume.cs`; `scripts/lab/TerrainLabUI.Apply.cs`; `data/lab_controls.json`

**Interfaces:**
- Produces: `CloudVolume.SetSunRedden(float)`, `SetSunReddenOnset(float)`, `SetSunHorizonGrow(float)`; uniforms `sun_redden`, `sun_redden_onset`, `sun_horizon_grow`, `sun_redden_color`.

- [ ] **Step 1: Add uniforms:**

```glsl
uniform float sun_redden = 1.0;            // strength of warm→red shift at low sun
uniform float sun_redden_onset = 0.25;     // sun elevation (0..1, =LIGHT0_DIRECTION.y) where reddening begins
uniform float sun_horizon_grow = 0.6;      // extra disc/halo size factor at the horizon (0 = none)
uniform vec3  sun_redden_color : source_color = vec3(1.0, 0.42, 0.18);  // target low-sun color
```

- [ ] **Step 2: Apply reddening + growth in `sun_layers`.** At the top (after `cosSun`), compute the sunset factor and grow the angular sizes; tint the final color. NOTE verify the sign of `LIGHT0_DIRECTION.y` — the existing disc resolves at `cosSun≈1`, meaning `LIGHT0_DIRECTION` points toward the sun, so `LIGHT0_DIRECTION.y` is the sun elevation (1=overhead, ~0=horizon). Full function after this task:

```glsl
vec3 sun_layers(vec3 rd) {
    if (!LIGHT0_ENABLED) { return vec3(0.0); }
    float cosSun = dot(rd, LIGHT0_DIRECTION);
    float sunElev = clamp(LIGHT0_DIRECTION.y, 0.0, 1.0);
    float sunset = smoothstep(sun_redden_onset, 0.0, sunElev) * sun_redden;   // 0 high → 1 at horizon
    float grow = 1.0 + sunset * sun_horizon_grow;                             // disc/halo swell at horizon

    float discEdge = cos(radians(sun_size * grow));
    float mu = clamp((cosSun - discEdge) / max(1.0 - discEdge, 1e-6), 0.0, 1.0);
    float discMask = smoothstep(0.0, 0.04, mu);
    float limb = mix(1.0, mix(0.45, 1.0, sqrt(mu)), sun_limb);
    float disc = discMask * limb;

    float corona = pow(max(cosSun, 0.0), sun_corona_size) * sun_corona_energy;
    float halo   = pow(max(cosSun, 0.0), sun_halo_size / grow) * sun_halo_energy;  // halo widens at horizon

    vec3 sunCol = mix(LIGHT0_COLOR, sun_redden_color, sunset);                 // warm→red tint
    return sunCol * sun_disc_energy * (disc * 12.0 + corona + halo);
}
```

- [ ] **Step 3: CloudVolume setters:**

```csharp
public void SetSunRedden(float v)      { _skyMat?.SetShaderParameter("sun_redden", Mathf.Clamp(v, 0f, 2f)); }
public void SetSunReddenOnset(float v) { _skyMat?.SetShaderParameter("sun_redden_onset", Mathf.Clamp(v, 0.02f, 0.8f)); }
public void SetSunHorizonGrow(float v) { _skyMat?.SetShaderParameter("sun_horizon_grow", Mathf.Clamp(v, 0f, 3f)); }
```

- [ ] **Step 4: Route in `ApplySceneFloat`:**

```csharp
            case "sun_redden":       _cloud?.SetSunRedden(v); break;
            case "sun_redden_onset": _cloud?.SetSunReddenOnset(v); break;
            case "sun_horizon_grow": _cloud?.SetSunHorizonGrow(v); break;
```

- [ ] **Step 5: Light-tab controls:**

```json
    { "id": "sun_redden",       "label": "sun horizon redden", "tab": "Light", "type": "scenef", "scene": "sun_redden",       "min": 0.0,  "max": 2.0, "default": 1.0,  "rand": false },
    { "id": "sun_redden_onset", "label": "redden onset (elev)","tab": "Light", "type": "scenef", "scene": "sun_redden_onset", "min": 0.05, "max": 0.8, "default": 0.25, "rand": false },
    { "id": "sun_horizon_grow", "label": "sun horizon grow",   "tab": "Light", "type": "scenef", "scene": "sun_horizon_grow", "min": 0.0,  "max": 3.0, "default": 0.6,  "rand": false },
```

- [ ] **Step 6: Build + verify (low sun vs high sun)**

Build → `Build succeeded.`
Low sun: `... -- --clouds=0 --godrays=0 --preset=5 --lookatsun --auto-shot=C:/tmp/sun/t4_low.png` (preset 5 sets sun_angle≈14°).
High sun: `... -- --clouds=0 --godrays=0 --mood=0 --lookatsun --auto-shot=C:/tmp/sun/t4_high.png` (or set sun height high; pick a high-sun mood).
Expected: low sun = disc + halo shifted warm/orange-red and slightly larger; high sun = neutral-white, unchanged size. The "sun horizon redden" slider scales the effect; "redden onset" moves the elevation where it kicks in. If reddening appears at HIGH sun instead, the `LIGHT0_DIRECTION.y` sign is inverted — use `-LIGHT0_DIRECTION.y` and re-verify.

- [ ] **Step 7: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "feat(sun): horizon reddening + growth (sun-elevation driven) + controls"
```

---

### Task 5: Soft optical-depth cloud occlusion (dim + redden through cloud)

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (`sun_layers` signature + `sky()` call; uniform)
- Modify: `scripts/lab/CloudVolume.cs`; `scripts/lab/TerrainLabUI.Apply.cs`; `data/lab_controls.json`

**Interfaces:**
- Consumes: cloud dome `cloud_rd_tex`, `dir_to_uv()` (existing).
- Produces: `CloudVolume.SetSunCloudRedden(float)`; uniform `sun_cloud_redden`. `sun_layers` gains an `aSun` parameter.

- [ ] **Step 1: Add uniform:**

```glsl
uniform float sun_cloud_redden = 0.8;   // how much thin cloud reddens (vs just dims) the transmitted sun
```

- [ ] **Step 2: Make `sun_layers` take the cloud optical depth at the sun and attenuate the disc/corona** (halo only partially, so it bleeds around edges). Change the signature and the final lines:

```glsl
vec3 sun_layers(vec3 rd, float aSun) {
    if (!LIGHT0_ENABLED) { return vec3(0.0); }
    // ... (cosSun, sunElev, sunset, grow, disc, corona, halo, sunCol exactly as Task 4) ...
    // Soft optical depth: thin cloud over the sun dims AND reddens the disc/corona; thick cloud kills them.
    // The halo spans a wider cone, so only partially occlude it (it bleeds around the cloud edge).
    vec3 ext = mix(vec3(1.0), sun_redden_color, clamp(aSun * sun_cloud_redden, 0.0, 1.0));  // warm extinction
    float discT  = (1.0 - clamp(aSun, 0.0, 1.0));            // disc/corona transmittance
    float haloT  = (1.0 - clamp(aSun, 0.0, 1.0) * 0.5);     // halo bleeds (half occlusion)
    vec3 core = sunCol * ext * (disc * 12.0 + corona) * discT;
    vec3 glow = sunCol * halo * haloT;
    return sun_disc_energy * (core + glow);
}
```

- [ ] **Step 3: Sample the dome at the sun direction in `sky()` and pass it.** Before `bg += sun_layers(...)`:

```glsl
    float aSun = 0.0;
    if (cloud_enabled && LIGHT0_DIRECTION.y > 0.0) {
        aSun = clamp(texture(cloud_rd_tex, dir_to_uv(LIGHT0_DIRECTION)).a, 0.0, 1.0);
    }
    bg += sun_layers(rd, aSun);
```
NOTE: the sun is now pre-attenuated by `aSun` inside `bg`. The existing per-pixel over-composite `bg*(1-a)+cloud.rgb` then applies the dome's own alpha for the cloud directly in front. Verify there's no visible double-darkening of the disc when a cloud crosses it (A/B vs `--clouds=0`); if the disc is too dark, drop the `discT` factor (let the over-composite alone occlude) and keep only the `ext` reddening.

- [ ] **Step 4: CloudVolume setter + route + control:**

```csharp
public void SetSunCloudRedden(float v) { _skyMat?.SetShaderParameter("sun_cloud_redden", Mathf.Clamp(v, 0f, 2f)); }
```
`ApplySceneFloat`:
```csharp
            case "sun_cloud_redden": _cloud?.SetSunCloudRedden(v); break;
```
`data/lab_controls.json`:
```json
    { "id": "sun_cloud_redden", "label": "sun cloud redden", "tab": "Light", "type": "scenef", "scene": "sun_cloud_redden", "min": 0.0, "max": 2.0, "default": 0.8, "rand": false },
```

- [ ] **Step 5: Build + verify (cloud crossing the sun)**

Build → `Build succeeded.`
Run with broken cloud over a low sun: `... -- --preset=5 --godrays=0 --coverage=0.5 --lookatsun --auto-shot=C:/tmp/sun/t5.png` and a clear A/B `--clouds=0 ...`.
Expected: where a cloud crosses the sun, the disc dims AND warms (reddens), fully gone under thick cloud; the halo still glows around the cloud's edge (rim). No hard double-dark ring on the disc. The "sun cloud redden" slider scales the warmth.

- [ ] **Step 6: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "feat(sun): soft optical-depth cloud occlusion (dim + redden) + control"
```

---

### Task 6: Mood + preset wiring, and slider sync

**Files:**
- Modify: `data/lighting_moods.json` (per-mood sun values)
- Modify: `scripts/lab/TerrainLabUI.Moods.cs` (`ApplyMood` — push new sun uniforms from mood JSON)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (`SyncLightControlsToScene` — reflect new sliders)
- Modify: `data/cloud_presets.json` (1–2 sun-showcase presets)

**Interfaces:**
- Consumes: all `CloudVolume.SetSun*` setters from Tasks 2–5.

- [ ] **Step 1: Push new sun uniforms per-mood in `ApplyMood`** (after the existing `sun.LightAngularDistance = ...` line). Use the mood JSON with physical defaults so existing moods are unchanged unless they add keys:

```csharp
        if (_cloud != null) {
            _cloud.SetSunSize(F(m, "sun_size", 0.6f));
            _cloud.SetSunLimb(F(m, "sun_limb", 0.55f));
            _cloud.SetSunCoronaSize(F(m, "sun_corona_size", 1200f));
            _cloud.SetSunCoronaEnergy(F(m, "sun_corona_energy", 2.0f));
            _cloud.SetSunHaloSize(F(m, "sun_halo_size", 90f));
            _cloud.SetSunHaloEnergy(F(m, "sun_halo_energy", 0.4f));
            _cloud.SetSunRedden(F(m, "sun_redden", 1.0f));
            _cloud.SetSunReddenOnset(F(m, "sun_redden_onset", 0.25f));
            _cloud.SetSunHorizonGrow(F(m, "sun_horizon_grow", 0.6f));
            _cloud.SetSunCloudRedden(F(m, "sun_cloud_redden", 0.8f));
        }
```

- [ ] **Step 2: Reflect new sliders in `SyncLightControlsToScene`** (so the Light tab shows current values). Since these live on the sky material, store the last-set values in CloudVolume and expose getters, OR (simpler) `Set(...)` them to the mood/default values right after `ApplyMood`. Add to the `Set(...)` block:

```csharp
        Set("sun_size", F(m, "sun_size", 0.6f)); Set("sun_limb", F(m, "sun_limb", 0.55f));
        Set("sun_corona_size", F(m, "sun_corona_size", 1200f)); Set("sun_corona_energy", F(m, "sun_corona_energy", 2.0f));
        Set("sun_halo_size", F(m, "sun_halo_size", 90f)); Set("sun_halo_energy", F(m, "sun_halo_energy", 0.4f));
        Set("sun_redden", F(m, "sun_redden", 1.0f)); Set("sun_redden_onset", F(m, "sun_redden_onset", 0.25f));
        Set("sun_horizon_grow", F(m, "sun_horizon_grow", 0.6f)); Set("sun_cloud_redden", F(m, "sun_cloud_redden", 0.8f));
```
(If `SyncLightControlsToScene` lacks the mood dict `m` in scope, read defaults instead of mood values — the goal is only that the sliders display a sane number.)

- [ ] **Step 3: Add a low-sun showcase to `data/cloud_presets.json`** (a "Golden Hour Sun" entry: low `sun_angle`, warm sun, clear-ish sky, e.g. `"sun_angle": 8.0, "sun_redden": 1.3, "sun_horizon_grow": 1.0, "cloud_coverage": 0.35`). Match the existing preset JSON shape (a `values` dict keyed by control ids).

- [ ] **Step 4: Build + verify across moods**

Build → `Build succeeded.`
Run `... -- --preset=5 --godrays=0 --lookatsun` and cycle the 6 moods (`--mood=0..5`); capture a couple.
Expected: each mood shows a coherent sun (high-sun moods neutral-white, low-sun/golden moods warm + grown); the new Light-tab sliders display values and still drive the look live; the Golden Hour Sun preset loads a warm low sun.

- [ ] **Step 5: Commit**

```bash
git -C /c/Wg16/wg-16-project add data/lighting_moods.json data/cloud_presets.json scripts/lab/TerrainLabUI.Moods.cs scripts/lab/TerrainLabUI.Apply.cs
git -C /c/Wg16/wg-16-project commit -m "feat(sun): per-mood sun values + Golden Hour preset + slider sync"
```

---

## Self-review notes (done)

- **Spec coverage:** disc/limb (T2), corona+halo (T3), horizon redden+grow (T4), soft optical-depth cloud redden (T5), controls+moods+presets (T2–T6), full-range-tunable defaults (every task), stays-in-`sky()` + local-to-sun (constraints). All spec sections map to a task.
- **Type consistency:** single-field setters (`SetSunCoronaSize` etc.) used consistently T3→T6; `sun_layers` gains its `aSun` param in T5 and all callers updated. `sun_redden_color` introduced T4, reused T5.
- **Risks carried from spec:** `LIGHT0_DIRECTION.y` sign (T4 Step 6 has the fix), double-occlusion (T5 Step 3 verify + fallback), glow/corona HDR interaction (T3 verify).
