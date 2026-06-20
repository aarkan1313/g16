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
  struct TimeState   — arc params (sunrise_h, sunset_h, peak_elev, az_start, az_end) + a list of
                       TimeKey color-script anchors {hour, sun_energy, sun_color, sky_top, sky_horizon,
                       sky_ground, ambient, ambient_sky}; plus `time_of_day` (current hour).
  struct WeatherState— cloud preset id/values + fog {color, density, aerial, height, heightd, sun_scatter}.
  struct GradeState  — exposure, white, glow, contrast, saturation, (brightness), grade_tint.

LightingComposer.cs (the ONLY writer of scene lighting; replaces ApplyMood's body)
  Apply(env, sun, cloud, godrays):
    1. TIME → sun.GlobalTransform (elev+az from the analytic arc at time_of_day),
              sun.LightEnergy + sun.LightColor (from the color script, interpolated by hour),
              sky_top/horizon/ground (color script → CloudVolume.SetSkyColors + ProceduralSky),
              env.AmbientLightEnergy + AmbientLightSkyContribution.
              (Stage-1 sun-disc reddening already keys off LIGHT0_DIRECTION.y → automatic.)
    2. WEATHER → cloud knobs (CloudVolume) + env.Fog* .
    3. GRADE → env.TonemapExposure/White, AdjustmentContrast/Saturation/Brightness, Glow* .
```

### Time axis — the physical day model

- **Sun POSITION = analytic arc** (cheap CPU math), tunable, no keyframes:
  - `elev(t) = peak_elev * sin(π · clamp((t − sunrise)/(sunset − sunrise), 0, 1))` (0 at sunrise/sunset,
    peak at midday). Below `sunrise`/above `sunset` → elev ≤ 0 (night; Stage 3 handles the look).
  - `azimuth(t) = lerp(az_start, az_end, (t − sunrise)/(sunset − sunrise))` (e.g. 90°→270°, E→W).
  - Feeds the existing `OrientSun(sun)` (it already takes `_sunAngle`/`_sunAzimuth`).
- **Sun COLOR/ENERGY + SKY gradient + AMBIENT = a "color script"** — a short list of `TimeKey` anchors
  (Dawn ~6 h, Golden ~8 h, Noon ~12 h, Golden ~16 h, Dusk ~18 h) each carrying sun_color/energy +
  sky_top/horizon/ground + ambient; `time_of_day` interpolates between the two surrounding anchors. This
  is the art-directable "look of the day," seeded from the 6 moods' existing sun/sky values. (Position is
  physics; the palette is keyframed — the cleanest hybrid, resolves the architecture doc's open fork.)
- Net: one `time_of_day` knob moves the sun AND shifts the whole daytime palette cohesively.

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

IN: `LightingComposer` + 3 state structs; the analytic sun-arc + color-script Time driver; the
3-way split of the moods into preset files (+ combo-preset shim); the Light-tab controls + CLI;
re-homing clouds/fog (Weather) and tonemap/grade (Grade) behind the axes WITHOUT changing their
internals. OUT: night look + celestial bodies (Stage 3); auto-advancing clock + fantasy (Stage 4); any
new cloud/fog/grade *features* (just re-homing what exists); a full physical Rayleigh sky (color-script
keyframes stand in for daylight).

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
