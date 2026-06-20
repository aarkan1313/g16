# Spec: Lighting Decouple + Time-of-Day Driver (Stage 2 of the Sun & Light arc)

Date: 2026-06-20. Status: DESIGN (awaiting user review). Owner: TBD.
Parent: `specs/2026-06-20-sun-light-system-architecture.md` (the 3-axis vision). Stage 1 (sun disc)
is built; this is Stage 2.
Touches: NEW `scripts/lab/LightingComposer.cs`, NEW `scripts/lab/LightingState.cs` (3 state structs);
`scripts/lab/TerrainLabUI.Moods.cs` (ApplyMood → composer), `TerrainLabUI.Apply.cs` (routing),
`TerrainLabUI.Cli.cs` (`--time`/`--weather`/`--grade`); NEW `data/{time_presets,weather_presets,
grade_presets}.json`; `data/lab_controls.json` (Light tab); `data/lighting_moods.json` (split, kept as
combo presets).

## Why

Today `ApplyMood` (`TerrainLabUI.Moods.cs`) writes the entire scene lighting from one bundled mood
(`lighting_moods.json`): sun + sky + fog + ambient + tonemap/grade as a frozen unit. So you can't have
"golden-hour sun under a storm" or "midday geometry with a desaturated moody grade." The user wants the
real-world combinatorial range: **any weather, at any time, under any grade.** Stage 2 splits the bundle
into three independent axes behind one composer, and builds the **Time** axis as a physical day driver
(the other two axes are re-homed from the moods and deepened in later stages).

## Architecture — one composer, three pure-data states

```
LightingState.cs (pure data; no Godot scene writes)
  struct TimeState   — `time_of_day` (h) + arc params (sunrise_h, sunset_h, peak_elev, az_start, az_end).
                       Sky/sun-color/ambient come from the ATMOSPHERE, not stored colors.
  struct AtmosphereState — physical params: rayleigh/mie scatter+absorption, ozone, planet+atmos radii,
                       turbidity, ground albedo, sun intensity. (Knobs; physical defaults = Earth.)
  struct WeatherState— cloud preset id/values + fog {color, density, aerial, height, heightd, sun_scatter}.
  struct GradeState  — exposure, white, glow, contrast, saturation, (brightness), grade_tint.

AtmosphereCompute.cs (GPU compute on a local RenderingDevice — FieldCompute/CloudNoiseCompute pattern)
  Bake(sunDir, AtmosphereState) → transmittance LUT (256×64) + sky-view LUT (~200×100) [+ multiscatter
  32×32]; exposes them as textures for the sky material + a CPU read of sun-transmittance & sky-ambient
  for the composer. Re-bake only when sunDir or params change.

LightingComposer.cs (the ONLY writer of scene lighting; replaces ApplyMood's body)
  Apply(env, sun, cloud, godrays, atmosphere):
    1. TIME → sun.GlobalTransform (elev+az from the analytic arc at time_of_day);
              AtmosphereCompute.Bake(sunDir, atmoState); bind LUTs to cloud_sky.gdshader;
              sun.LightColor = transmittance(sunDir) (warm/red near horizon), sun.LightEnergy scales by it;
              env.AmbientLightEnergy/SkyContribution from the sky-view integral.
              (Stage-1 sun-disc reddening keys off the same sun elevation → consistent.)
    2. WEATHER → cloud knobs (CloudVolume) + env.Fog* .
    3. GRADE → env.TonemapExposure/White, AdjustmentContrast/Saturation/Brightness, Glow* .
```

### Time axis — the physical day model

- **Sun POSITION = analytic arc** (cheap CPU math), tunable, no keyframes:
  - `elev(t) = peak_elev * sin(π · clamp((t − sunrise)/(sunset − sunrise), 0, 1))` (0 at sunrise/sunset,
    peak at midday). Below `sunrise`/above `sunset` → elev ≤ 0 (night; Stage 3 handles the look).
  - `azimuth(t) = lerp(az_start, az_end, (t − sunrise)/(sunset − sunrise))` (e.g. 90°→270°, E→W).
  - Feeds the existing `OrientSun(sun)` (it already takes `_sunAngle`/`_sunAzimuth`).
- **Sky COLOR + sun TINT + AMBIENT = a GPU-compute ATMOSPHERE** (the AAA/GPU "better option", replacing
  keyframed color stops). A physically-based scattering model (Hillaire "Scalable and Production-Ready Sky
  and Atmosphere") computed on a **local RenderingDevice** (the `FieldCompute`/`CloudNoiseCompute`
  pattern): a **transmittance LUT** (256×64, optical depth through the atmosphere) and a **sky-view LUT**
  (~200×100, sky radiance by view direction for the current sun), optional **multi-scatter LUT** (32×32).
  Re-baked when the sun direction changes (cheap; ~sub-ms; per-frame only while auto-cycling in Stage 4).
  `cloud_sky.gdshader` samples the sky-view LUT by `EYEDIR` for the sky gradient (replacing
  `background()`), the transmittance LUT toward the sun for the **sun light color** (`LightColor`) and the
  Stage-1 sun-disc tint, and the sky-view integral for **ambient**. Sun **energy** scales by the
  transmittance (dimmer/redder near the horizon). Physical params are knobs: planet/atmosphere radii,
  Rayleigh + Mie scattering/absorption coeffs, ozone, turbidity, ground albedo.
- Net: one `time_of_day` knob moves the sun AND the atmosphere produces a physically-correct sky, sun
  color, and ambient for that sun elevation — real dawn→noon→sunset, and a foundation for night (Stage 3)
  + aerial perspective. The `TimeKey` color-script is dropped in favor of this.

### Decoupling — split the 6 moods, keep them as combo presets

The 6 `lighting_moods.json` entries are split by concern (exact keys confirmed from the file):
- → **Time** (color-script anchors / arc): `sun_angle`(→seeds arc peak/sample), `sun_az`, `sun_energy`,
  `sun_color`, `sky_top/horizon/ground`, `ambient`, `ambient_sky`, `shadow_soft`.
- → **Weather**: `fog_color/density/aerial/height/heightd/sun_scatter` (+ a matching cloud preset).
- → **Grade**: `exposure`, `white`, `glow`, `contrast`, `saturation`.
A named "mood" survives as a **combo preset** (one pick sets a Time hour + a Weather + a Grade), so
existing one-click looks still work — but each axis is now independently overridable.

## Controls (Light tab, registry-driven)

- `time_of_day` (`scenef`, e.g. 5.0–19.0) — the master scrub. Routes to `LightingComposer`.
- Time arc knobs: `day_sunrise`, `day_sunset`, `day_peak_elev`, `day_az_start`, `day_az_end`.
- Weather preset picker + the existing cloud/fog knobs (re-homed under Weather).
- Grade preset picker + the existing exposure/white/contrast/saturation/glow knobs (re-homed under Grade).
- Existing `sun_*` Stage-1 disc knobs stay (now driven by Time for color/energy, overridable).
- CLI: `--time=<h>`, `--weather=<id>`, `--grade=<id>` for windowed verification.

## Scope

IN: `LightingComposer` + the state structs; the analytic sun-arc Time driver; the **GPU-compute
atmosphere** (`AtmosphereCompute` LUTs + `cloud_sky.gdshader` sampling) as the sky renderer producing
sky color + sun tint/energy + ambient; the 3-way split of the moods into preset files (+ combo shim);
the Light-tab controls + CLI; re-homing clouds/fog (Weather) and tonemap/grade (Grade) behind the axes
WITHOUT changing their internals. OUT: night/celestial bodies (Stage 3 — but the atmosphere is built so
it *extends* there); auto-advancing clock + fantasy (Stage 4); new cloud/fog/grade *features* (just
re-homing what exists).

## Acceptance

- Scrubbing `time_of_day` across the day moves the sun along a believable arc AND shifts sun color/energy
  + sky gradient + ambient cohesively (dawn warm-low → noon white-high → dusk warm-low), smooth, no pops.
- Weather and Grade are independently selectable at ANY time (e.g. golden-hour sun + overcast clouds +
  desaturated grade all at once) — the combinatorial range works.
- The 6 original moods are reproducible as Time×Weather×Grade combos (no authored look lost); a combo
  preset still sets all three in one pick.
- Stage-1 sun disc reddening tracks the Time-driven sun elevation automatically.
- Build clean; `--shadowcheck` unaffected; default spawn look matches a sensible default time/weather/grade.

## Risks

1. **`ApplyMood` is load-bearing** (spawn default, preset apply, mood re-apply after cloud attach,
   `SyncLightControlsToScene`). Route ALL of these through `LightingComposer` or the old + new paths will
   fight. Migrate callers carefully; keep one writer.
2. **Sun azimuth/elevation convention** — `OrientSun` + `PushSunToCloud` use a specific +Basis.Z "to sun"
   convention (cloud shadow check depends on it). The arc must produce the same convention; verify with
   `--shadowcheck` after.
3. **Overcast/storm coupling** — `OvercastDirty()` scales sun/ambient from a base captured at mood apply;
   re-home that base into the composer so overcast still scales correctly from the Time/Weather state.
4. **Slider sync** — `SyncLightControlsToScene` reflects sliders from the scene; point it at the new state
   so the Light tab shows the composed values.
5. **Scope creep into night** — keep below-horizon handling trivial (dark) in Stage 2; the night look is
   Stage 3 or the two will entangle.
