# Night & Celestial (Stage 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Sun & Light system to the night half of the day — a darkening, tunable night sky (24 h sun arc), a phased textured moon, cool moonlight, and a procedural star field + Milky Way — all behind toggles defaulting to a physical look.

**Architecture:** Keep the Stage-2 one-writer composer (`ComposeLighting`). Stage 3 makes the Time axis continuous over 24 h (sun elevation goes negative at night), derives a master `nightFactor`, darkens via the existing keyframed color script (night anchors), and adds a pure-data `CelestialState` (moon/stars) pushed to `cloud_sky.gdshader`. No GPU atmosphere here — night colors live in the same script the atmosphere stage will later supersede.

**Tech Stack:** Godot 4.6 mono (C#), GLSL sky shader (`cloud_sky.gdshader`), JSON-driven presets/controls. No new RenderingDevice/compute resources (moon/stars are sky-shader pixels; moonlight is a 2nd DirectionalLight3D).

## Global Constraints

- **Discipline rule (non-negotiable):** build AT MOST ONE sub-phase ahead of the last PASSED eye-gate. This plan details **3a fully**; **3b/3c/3d are forward-design blocks** — flesh them into bite-sized steps only after the preceding gate PASSES.
- **The user's live eye is the only look-gate.** Each task's "verify" = `dotnet build` → headless `--import` (compile-check) → `--auto-shot` self-check → **user flies `scenes/review.tscn`**. No unit tests for look.
- **Everything behind a toggle defaulting to the approved/physical look.** Night defaults to physically dark-but-not-pure-black; presets cover the dark-scary ↔ moonlit-bright range.
- **Tunable across the range, physical default, presets + super-tunable** (user's explicit ask) — mirror the sun-preset pattern (`data/sun_presets.json` → `data/celestial_presets.json`).
- **Don't regress daylight or the 6 moods** — the `time_of_day` range/arc change must keep Stage-2 daylight scrub and `MoodToStates` reproductions identical in [sunrise, sunset].
- **Run discipline:** ONE Godot at a time; always `--rendering-driver vulkan`; kill strays (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`) before launching. After new `.cs`/texture: `dotnet build WG16.csproj` → headless `--import`.
- **Coordinate, don't collide:** sky/light files only. Do NOT edit ground files. Shared (`lab_controls.json`, review scene) — small + additive.
- **Commit by default** to `experiment/presentation` per finished sub-phase; push only when asked.

**Key files (verified):**
- `scripts/lab/LightingState.cs` — `TimeState`, `TimeKey`, `LightingPresets` (add night fields + `CelestialState`).
- `scripts/lab/TerrainLabUI.Lighting.cs` — `DriveTime`, `ComposeLighting`, `ApplyOvercastScaling`, `SampleDayScript`.
- `scripts/lab/TerrainLabUI.Apply.cs` — `ApplySceneFloat` (control routing), `OrientSun`, `PushSunToCloud`.
- `scripts/lab/CloudVolume.cs` — `_skyMat` uniform setters (`SetSun*`, `SetSkyColors`), shader install.
- `shaders/cloud_sky.gdshader` — `background()`, `sun_layers()`, `sky()`; add `moon_layers()`, `stars()`.
- `data/time_presets.json` — `day_script` anchors (add night anchors).
- `data/lab_controls.json` — Light-tab controls (extend `time_of_day` range; add night/celestial knobs).
- `data/celestial_presets.json` (NEW, 3b+), `scripts/lab/TerrainLabUI.CelestialPresets.cs` (NEW, 3b+).
- `scenes/review.tscn`, `scenes/terrain_lab.tscn` — Moon DirectionalLight3D (3c).

---

## SUB-PHASE 3a — Night sky + sun-below-horizon  ← BUILD THIS NOW

**Outcome:** Scrubbing `time of day` 0–24 gives a believable dusk→night→dawn: the sun drops below the horizon and cleanly disappears, the sky darkens to night colors, ambient drops to a tunable floor. Tunable dark-scary ↔ dim via `night_darkness` / `night_ambient_floor`. Physical default looks right with no tuning. No pops; daylight + the 6 moods unchanged.

### Task 3a.1: Continuous 24 h sun arc + nightFactor + night fields

**Files:**
- Modify: `scripts/lab/LightingState.cs` (`TimeState`: add night fields)
- Modify: `scripts/lab/TerrainLabUI.Lighting.cs` (`DriveTime`, `ComposeLighting`/`ApplyOvercastScaling`)

**Interfaces:**
- Produces: `TimeState.NightDarkness` (float, default 1.0), `TimeState.NightAmbientFloor` (float, default 0.02), `TimeState.NightNadir` (float deg, default = mirror of `PeakElev` when ≤0). Private field `_nightFactor` (0 day → 1 deep night) readable by later tasks (3b/3c gate moon/moonlight on it).
- Consumes: existing `_time`, `SampleDayScript`, `OrientSun`, `ApplyOvercastScaling`.

- [ ] **Step 1: Add night fields to `TimeState`** (`LightingState.cs`, after line 22's azimuth fields)

```csharp
    // NIGHT (Stage 3a): the night half of the 24 h arc. Defaults = physical (mirror dip, near-black floor).
    public float NightNadir = -60f;        // sun elevation at solar midnight (deg, negative). default mirrors PeakElev.
    public float NightDarkness = 1.0f;     // scales night sky+ambient: 1 = authored, <1 dark-scary, >1 moonlit-bright
    public float NightAmbientFloor = 0.02f;// minimum ambient at deep night (≈0 = scary; raise for moonlit)
```

- [ ] **Step 2: Add a `_nightFactor` field next to the composer state** (`TerrainLabUI.Lighting.cs`, after line 16 `_grade`)

```csharp
    private float _nightFactor = 0f;   // 0 = sun up (day), 1 = sun well below horizon (deep night). Set by DriveTime.
```

- [ ] **Step 3: Rework `DriveTime` for a continuous arc + nightFactor + night scaling** (`TerrainLabUI.Lighting.cs:110-121`)

Replace the body of `DriveTime` with the continuous arc (remove the `Clamp` on `f` so elevation goes negative past the day window) and compute `_nightFactor`, then scale the sampled night colors/ambient by `NightDarkness` and apply the ambient floor:

```csharp
    private void DriveTime(float hour)
    {
        _time.TimeOfDay = hour;
        float dayLen = Mathf.Max(_time.SunsetH - _time.SunriseH, 1e-3f);
        float f = (hour - _time.SunriseH) / dayLen;                  // 0 at sunrise, 1 at sunset; <0/>1 = night
        _time.SunAngle = _time.PeakElev * Mathf.Sin(Mathf.Pi * f);   // continuous: peak at noon, NEGATIVE at night
        _time.SunAzimuth = Mathf.Lerp(_time.AzStart, _time.AzEnd, f);// continues sweeping (extrapolates) at night

        // nightFactor: 0 while the sun is up, ramping to 1 once it is ~6° below the horizon (civil twilight).
        float belowDeg = Mathf.Max(-_time.SunAngle, 0f);
        float nf = Mathf.Clamp(belowDeg / 6f, 0f, 1f);
        _nightFactor = nf * nf * (3f - 2f * nf);                     // smoothstep

        TimeKey k = SampleDayScript(hour);
        // night scaling: NightDarkness multiplies sky+ambient by nightFactor (1 = authored night).
        float ns = Mathf.Lerp(1f, _time.NightDarkness, _nightFactor);
        _time.SunEnergy = k.SunEnergy;
        _time.SunColor = k.SunColor;
        _time.SkyTop = ScaleRgb(k.SkyTop, ns);
        _time.SkyHorizon = ScaleRgb(k.SkyHorizon, ns);
        _time.SkyGround = ScaleRgb(k.SkyGround, ns);
        _time.AmbientSky = k.AmbientSky;
        // ambient: scaled toward dark, but never below the night floor (keeps moonlit option alive).
        float amb = k.Ambient * ns;
        _time.Ambient = Mathf.Lerp(amb, Mathf.Max(amb, _time.NightAmbientFloor), _nightFactor);
        ComposeLighting();
    }

    private static Color ScaleRgb(Color c, float s) => new Color(c.R * s, c.G * s, c.B * s, c.A);
```

- [ ] **Step 4: Push `_nightFactor` to the sky shader** for the sun horizon-gate + future moon/star fade. In `ComposeLighting()` (`TerrainLabUI.Lighting.cs`, inside the `if (_cloud != null)` block ~line 68) add:

```csharp
            _cloud.SetNightFactor(_nightFactor);
```

- [ ] **Step 5: Add the `SetNightFactor` setter + uniform** in `CloudVolume.cs` (next to the other `SetSun*` setters ~line 466) and install it in the shader-install block (~line 134):

```csharp
    private float _nightFactor = 0f;
    public void SetNightFactor(float v) { _nightFactor = Mathf.Clamp(v, 0f, 1f); _skyMat?.SetShaderParameter("night_factor", _nightFactor); }
```
(In the install block where other params are set on `_skyMat`, add: `_skyMat.SetShaderParameter("night_factor", _nightFactor);`)

- [ ] **Step 6: Build + import**

Run: `dotnet build WG16.csproj` then headless `--import`.
Expected: clean build, no errors.

- [ ] **Step 7: Commit**

```bash
git add scripts/lab/LightingState.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/CloudVolume.cs
git commit -m "Stage 3a: continuous 24h sun arc + nightFactor + night scaling fields"
```

### Task 3a.2: Sun horizon-gate in the sky shader

**Files:**
- Modify: `shaders/cloud_sky.gdshader` (`sun_layers()` ~line 79-143; add `night_factor` uniform near line 18)

**Interfaces:**
- Consumes: `night_factor` uniform (Task 3a.1 Step 5), `LIGHT0_DIRECTION.y`.
- Produces: the visible sun disc/corona/halo fade to zero as the sun passes below the horizon (so night sky has no ghost sun).

- [ ] **Step 1: Declare the uniform** (`cloud_sky.gdshader`, near line 18 with the other sun uniforms)

```glsl
uniform float night_factor = 0.0;   // 0 day .. 1 deep night (drives sun horizon-gate; moon/star fade later)
```

- [ ] **Step 2: Gate the sun below the horizon.** At the end of `sun_layers()` (the `return` ~line 142), multiply by a smooth horizon gate so the disc/corona/halo vanish once the sun direction drops below the horizon:

```glsl
    float horizonGate = smoothstep(-0.04, 0.0, LIGHT0_DIRECTION.y);   // 1 above horizon, 0 once below
    return sun_disc_energy * (core + glow) * horizonGate;
```

- [ ] **Step 3: Build + import; self-check shot at night**

Run: `dotnet build WG16.csproj` → headless `--import` → windowed `... scenes/review.tscn -- --time=1 --auto-shot=c:/tmp/night3a.png`
Expected: a dark sky, NO sun disc/glow visible at hour 1.

- [ ] **Step 4: Commit**

```bash
git add shaders/cloud_sky.gdshader
git commit -m "Stage 3a: sun horizon-gate so the disc/halo vanish below the horizon"
```

### Task 3a.3: Night color anchors + controls + range extension

**Files:**
- Modify: `data/time_presets.json` (`day_script`: add night anchors)
- Modify: `data/lab_controls.json` (extend `time_of_day` range; add `night_darkness`, `night_ambient_floor`)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (`ApplySceneFloat`: route the two new ids)

**Interfaces:**
- Consumes: `LightingPresets.DayScript` (already parses any hour anchor — night anchors need no parser change), `DriveTime`/`_time` night fields.
- Produces: control ids `night_darkness`, `night_ambient_floor`; `time_of_day` slider spans 0–24.

- [ ] **Step 1: Add night anchors to `day_script`** in `data/time_presets.json` (append after the 18.5 h anchor; keep the same key structure so the future atmosphere stage can supersede them):

```json
    { "hour": 20.0, "sun_energy": 0.15, "sun_color": [0.55, 0.45, 0.55], "sky_top": [0.05, 0.07, 0.16], "sky_horizon": [0.18, 0.14, 0.22], "sky_ground": [0.07, 0.07, 0.10], "ambient": 0.14, "ambient_sky": 0.85 },
    { "hour": 22.0, "sun_energy": 0.0,  "sun_color": [0.40, 0.45, 0.65], "sky_top": [0.02, 0.03, 0.08], "sky_horizon": [0.05, 0.06, 0.12], "sky_ground": [0.03, 0.03, 0.05], "ambient": 0.06, "ambient_sky": 0.80 },
    { "hour": 0.0,  "sun_energy": 0.0,  "sun_color": [0.40, 0.45, 0.65], "sky_top": [0.015,0.02, 0.06], "sky_horizon": [0.04, 0.05, 0.10], "sky_ground": [0.02, 0.02, 0.04], "ambient": 0.05, "ambient_sky": 0.80 },
    { "hour": 2.0,  "sun_energy": 0.0,  "sun_color": [0.40, 0.45, 0.65], "sky_top": [0.02, 0.03, 0.08], "sky_horizon": [0.05, 0.06, 0.12], "sky_ground": [0.03, 0.03, 0.05], "ambient": 0.06, "ambient_sky": 0.80 },
    { "hour": 4.0,  "sun_energy": 0.10, "sun_color": [0.55, 0.50, 0.60], "sky_top": [0.05, 0.07, 0.16], "sky_horizon": [0.16, 0.13, 0.22], "sky_ground": [0.06, 0.06, 0.09], "ambient": 0.12, "ambient_sky": 0.85 }
```

> NOTE: `SampleDayScript` sorts by hour and clamps to the endpoints, so anchors at 0/2/4 (early) and 20/22 (late) interpolate correctly across the wrap because the list spans 0→22; the 0 h and 4 h anchors bracket pre-dawn, 20/22 bracket post-dusk. Verify the 4 h→5.5 h dawn transition and 18.5 h→20 h dusk transition are smooth in motion (gate criterion).

- [ ] **Step 2: Extend `time_of_day` range + add night controls** in `data/lab_controls.json` (change line 155's `max` to 24.0; add two entries on the Light tab after it):

```json
    { "id": "time_of_day", "label": "time of day (h)", "tab": "Light", "type": "scenef", "scene": "time_of_day", "min": 0.0, "max": 24.0, "default": 12.0, "rand": false },
    { "id": "night_darkness", "label": "night darkness", "tab": "Light", "type": "scenef", "scene": "night_darkness", "min": 0.0, "max": 2.0, "default": 1.0, "rand": false },
    { "id": "night_ambient_floor", "label": "night ambient floor", "tab": "Light", "type": "scenef", "scene": "night_ambient_floor", "min": 0.0, "max": 0.4, "default": 0.02, "rand": false }
```

- [ ] **Step 3: Route the two new ids** in `ApplySceneFloat` (`scripts/lab/TerrainLabUI.Apply.cs`, with the other `day_*` cases ~line 118-120):

```csharp
            case "night_darkness":      _time.NightDarkness = v; DriveTime(_time.TimeOfDay); break;
            case "night_ambient_floor": _time.NightAmbientFloor = v; DriveTime(_time.TimeOfDay); break;
```

- [ ] **Step 4: Build + import; dusk/night/dawn self-check shots**

Run: `dotnet build WG16.csproj` → headless `--import`, then windowed auto-shots at several hours:
`... scenes/review.tscn -- --time=18.5 --auto-shot=c:/tmp/3a_dusk.png`
`... scenes/review.tscn -- --time=23 --auto-shot=c:/tmp/3a_night.png`
`... scenes/review.tscn -- --time=5 --auto-shot=c:/tmp/3a_dawn.png`
Expected: dusk = warm low sun fading; night = dark, no sun; dawn = brightening. No console errors.

- [ ] **Step 5: Commit**

```bash
git add data/time_presets.json data/lab_controls.json scripts/lab/TerrainLabUI.Apply.cs
git commit -m "Stage 3a: night color anchors + night darkness/floor controls + 0-24h range"
```

### Task 3a.4: Harden the key-2 re-apply cloud/compute re-bind (banked correctness follow-up)

**Files:**
- Investigate then modify: the cloud/compute re-bind path touched by `DriveTime`/`ComposeLighting` → `PushSunToCloud`/`SetSun` and cloud uniform sets (`scripts/lab/CloudVolume.cs`, possibly `scripts/lab/TerrainLabUI.Process.cs`).

**Interfaces:** no API change — the fix guards a transient-invalid `cloud_rd_tex`/uniform-set during re-apply (the error noted in DECISIONS 2026-06-20). This lives in the exact path 3a reworked, so fix it here, once.

- [ ] **Step 1: Reproduce** with render-thread markers — launch `scenes/review.tscn`, press 2 twice, capture the RenderingDevice "invalid texture" error (use systematic-debugging skill; do NOT guess the fix).
- [ ] **Step 2: Root-cause** — confirm whether the error is a uniform-set rebuilt against a not-yet-valid `Texture2Drd` during re-apply (the same class as commit `63c59ff`), or a per-frame compute rebind racing the re-apply.
- [ ] **Step 3: Guard** the identified rebind (skip/defer the uniform-set when the RID is transiently invalid, mirroring the `63c59ff` "install once, never swap" approach). Write the minimal real fix once the root cause is confirmed.
- [ ] **Step 4: Verify** — press 2 ≥20× live; expect 0 mid-session RenderingDevice errors (a benign quit-teardown burst is acceptable, as documented).
- [ ] **Step 5: Commit**

```bash
git commit -am "Stage 3a: harden key-2 re-apply cloud rebind (zero mid-session RD errors)"
```

### 3a EYE-GATE (user flies — I drive)

- Set up `scenes/review.tscn`, windowed, `--rendering-driver vulkan`, strays killed.
- **What to judge:** scrub `time of day` 0→24 — believable dusk→night→dawn, sun cleanly drops below horizon (no ghost disc), no pops at the dawn/dusk anchor seams. `night_darkness` 0↔2 spans dark-scary↔dim; `night_ambient_floor` lifts the deep-night minimum. Daylight [6,18] + the 6 moods unchanged. `--profmove` cost unchanged (no new lights yet).
- **Record:** verdict → `NEEDS_REVIEW.md` + a `DECISIONS.md` line. PASS unblocks 3b ONLY.

---

## SUB-PHASE 3b — Moon disc  ← BUILDING NOW (3a passed)

**Outcome:** a believable, textured, phased moon hangs in the night sky — positioned (default anti-solar,
tunable), limb-darkened, maria/crater mottle (reuses `sun_fbm`), a phase terminator that tracks `moon_phase`
new→half→full, a cool halo, occluded per-pixel by clouds, gated to night. No moonlight on terrain yet (3c).

### Task 3b.1: CelestialState data + moon position plumbing
**Files:** `LightingState.cs` (+`CelestialState`/`MoonState`), `TerrainLabUI.Lighting.cs` (compute+push moon dir in `ComposeLighting`), `CloudVolume.cs` (+`SetMoon*` setters + uniforms), `cloud_sky.gdshader` (declare moon uniforms).
- `MoonState` (pure data): `Phase 0..1=1`, `ElevOffset=0`, `AzOffset=0`, `Size=1.2`, `Limb=0.6`, `HaloSize=60`, `HaloEnergy=0.5`, surface `Cells=8/Contrast=0.5/Spots=0.4/Churn=0`, `DiscEnergy=0.9`, `Color=(0.85,0.88,1.0)`.
- In `ComposeLighting`: `toSun = -sun.forward`; `antiSun=-toSun`; apply Elev/AzOffset → `moonDir`; push `SetMoon(moonDir,color,energy)` + phase/size/limb/halo/surface. Moon dir is a **custom uniform** (`moon_dir`), independent of any 2nd light (that's 3c).
- Shader: declare `uniform vec3 moon_dir; uniform float moon_size, moon_limb, moon_phase, moon_disc_energy, moon_halo_size, moon_halo_energy, moon_surf_cells, moon_surf_contrast, moon_surf_spots, moon_surf_churn; uniform vec3 moon_color;`
- Verify: build+import clean (no render yet).

### Task 3b.2: moon_layers() render — disc + surface + phase + halo + gates + occlusion
**Files:** `cloud_sky.gdshader` (+`moon_layers(rd)`, call in `sky()` after `sun_layers`).
- Mirror `sun_layers`: disc mask at `moon_dir`, limb darkening (`moon_limb`), de-foreshortened surface via `sun_fbm` (maria + craters using `moon_surf_*`), cool tint, soft halo.
- **Phase terminator:** disc-pixel normal `n=(p.x,p.y,ball)`; light dir from phase `angle=(1-moon_phase)*PI`, `L=vec3(sin(angle),0,cos(angle))`; `lit=smoothstep(-0.06,0.06,dot(n,L))`; multiply disc by `lit` (full=1 at phase 1, dark at phase 0, half-lit at 0.5).
- **Gates:** `horizonGate=smoothstep(-0.04,0,moon_dir.y)` × `night_factor` (moon only at night, only above horizon).
- **Cloud occlusion:** per-pixel `cloud_rd_tex` alpha at `rd` (same as the sun fix).
- Verify: auto-shots at `--time=23` with `--moonphase=0/0.5/1` (add a tiny CLI) — disc visible, phase terminator tracks.

### Task 3b.3: controls + routing + fold banked sun-disc polish
**Files:** `data/lab_controls.json` (moon knobs, Light tab), `TerrainLabUI.Apply.cs` (route `moon_*` ids), `CloudVolume.cs`/`TerrainLabUI.Lighting.cs` (sun-disc polish defaults: bump `sun_limb` + size/corona to the values the eye-gate liked).
- Add `--moonphase=`/`--moon=` CLI + a `review_night.json`-style hook (extend night gate states with a `moonphase` field).
- Verify: build+import; night-gate relaunch for the eye.

### 3b EYE-GATE
Believable phased, textured moon; terminator tracks `moon_phase` new→half→full; cool halo; clouds occlude it per-pixel; sits correctly anti-solar (and offsets move it). Sun-disc polish reads better too. PASS unblocks 3c.

## SUB-PHASE 3c — Moonlight  *(FORWARD DESIGN — detail after 3b PASSES)*

**Approach:** add a **Moon `DirectionalLight3D`** to `scenes/{review,terrain_lab}.tscn` (shadow-casting, default low/off in day). Compose its transform/color/energy in `ComposeLighting`, gated by `nightFactor × moon-up × phase`. Cool default tint. **Cross-fade with the sun by elevation** (sun dominant when up, moon ramps as sun sets) so they don't double-light or fight at dusk; only one strong shadow-caster at a time (reuse the sun shadow budget). `CelestialState.moonlight { color, energy }` + controls.

**Files:** `scenes/*.tscn` (Moon light node), `TerrainLabUI.Lighting.cs` (compose + cross-fade), `LightingState.cs` (moonlight fields), `data/lab_controls.json`, `TerrainLabUI.Apply.cs`.

**Gate:** terrain lit coolly under a full moon at night; new-moon stays dark; no fight with the sun at dusk; shadows correct; `--profmove` confirms cost gated off in day. PASS unblocks 3d.

## SUB-PHASE 3d — Stars + Milky Way  *(FORWARD DESIGN — detail after 3c PASSES)*

**Approach:** `stars(rd)` in `cloud_sky.gdshader` — hash-grid sparse bright points, `star_density/brightness`, per-star `TIME` twinkle, rotation about a tunable celestial axis by `TIME × star_rotation`, fade by `night_factor × star_fade`. Milky Way = great-circle band (distance of `rd` from a tunable galactic plane) modulated by `sun_fbm`, `mw_brightness/width/tilt`, rotates+fades with the stars. **Watch aliasing/shimmer in motion** (sub-pixel-aware sizing, temporal-stable twinkle — judge flying + rotating, never from a still). `CelestialState.stars/milkyway` + controls. Then author **`data/celestial_presets.json`** + `TerrainLabUI.CelestialPresets.cs` + picker + `--celestial=N` (mirror `SunPresets`): `full_moon_clear`, `new_moon_dark`, `crescent`, `bright_moonlit`, `deep_scary`, `exotic`.

**Files:** `cloud_sky.gdshader` (+`stars`/milkyway + uniforms), `CloudVolume.cs` (setters), `LightingState.cs` (star/mw fields), `data/celestial_presets.json` (NEW), `scripts/lab/TerrainLabUI.CelestialPresets.cs` (NEW), `data/lab_controls.json`, `TerrainLabUI.Apply.cs`, `TerrainLabUI.Cli.cs` (`--celestial=`).

**Gate:** believable night sky; stars fade across dusk/dawn; no shimmer/aliasing in motion; Milky Way reads subtle; presets apply and compose with any time/weather/grade. PASS completes Stage 3 → re-roadmap (Clouds overhaul next).

---

## Self-Review (3a)

- **Spec coverage (3a slice):** 24 h arc ✓ (Task 3a.1 Step 3), nightFactor ✓ (3a.1), darkening sky + night anchors ✓ (3a.3), ambient floor ✓ (3a.1/3a.3), tunable dark↔dim ✓ (`night_darkness`/`night_ambient_floor`), sun-below-horizon clean ✓ (3a.2), no-regress daylight/moods ✓ (gate criterion), banked key-2 fix ✓ (3a.4). 3b–3d intentionally forward-design per the discipline rule.
- **Placeholder scan:** 3a steps all carry real code/commands. 3b–3d are explicitly marked forward-design (architecture + files + gate), not buildable steps — by design, not placeholders.
- **Type consistency:** `NightDarkness`/`NightAmbientFloor`/`NightNadir` on `TimeState`; `_nightFactor` field + `SetNightFactor`/`night_factor` uniform consistent across C# and GLSL; `ScaleRgb` helper defined where used. Control ids (`night_darkness`, `night_ambient_floor`) match their `ApplySceneFloat` cases.
