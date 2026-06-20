# Spec: Night & Celestial (Stage 3 of the Sun & Light arc)

Date: 2026-06-20. Status: DESIGN (brainstorm complete; spec for review). Owner: Sun/Light lane.
Parent: `specs/2026-06-20-sun-light-system-architecture.md` (3-axis + Celestial). Builds on Stage 1 (sun
disc + surface) and Stage 2 (decouple + time-of-day), both eye-gated. First piece of the "finish the full
sky system" push (ROADMAP ☀️🌙).

## Why

Stage 2 only models daylight: `DriveTime()` clamps the sun arc to `[sunrise, sunset]` (so outside day
hours the sun sits at the horizon, never below) and the day color script clamps to its dawn/dusk
endpoints. There is **no night**. Stage 3 adds the night half of the day: the sun drops below the
horizon, the sky darkens, and the **celestial layer** appears — a **moon** (textured, phased, casting
cool moonlight) and a **star field + Milky Way**. Per the user: **fully tunable across the range**
(dark-and-scary ↔ moonlit-and-bright ↔ middle), physically-grounded defaults, **presets + super-tunable**
like the sun surface — no hardcoded look.

## Scope

IN: extend the Time model to a full 24 h (sun elevation goes negative at night); a **darkening night sky**
(night gradient + tunable darkness + ambient floor); a **moon** (disc reusing the sun-surface system for
craters/maria, a tunable **phase** with terminator, a soft halo); **moonlight** (a 2nd directional light,
cool, shadow-casting, gated to night); a **star field + Milky Way** (procedural, twinkle, density,
rotation, night-fade); **celestial presets** + Light-tab knobs + CLI. OUT: the GPU-compute physical
atmosphere (its own later stage — night here uses the extended keyframed color script, same as Stage 2's
daylight); auto day/night clock + fantasy/multiple bodies (Stage 4); cloud changes (Clouds-overhaul
stage). Eclipses, real ephemeris/constellations (procedural only, user's call).

## Architecture — one composer, extended Time + a Celestial block

```
LightingState.cs
  TimeState (extend): TimeOfDay 0..24; arc gives sun elev that is NEGATIVE at night; + night fields
    (night_darkness, night_ambient_floor).
  CelestialState (NEW, pure data): moon { phase 0..1, arc (elev/az offset or own arc params),
    disc size, limb, surface{cells,contrast,spots,...} reusing sun-surface knobs, halo, color };
    moonlight { color, energy }; stars { brightness, density, twinkle, rotation_speed, fade };
    milkyway { brightness, width, tilt }.

TerrainLabUI.Lighting.cs
  DriveTime(hour 0..24): sun elev/az over the FULL day (negative at night); compute a `nightFactor`
    (0 day → 1 deep night, from sun elevation); position the moon; sample the extended day+night color
    script; ComposeLighting().
  ComposeLighting(): existing sun/sky/grade writes + NEW: moon node transform/color/energy (moonlight
    gated by nightFactor × moon-up × phase), night ambient floor, push moon + star/milkyway params to
    cloud_sky.gdshader.

cloud_sky.gdshader
  background(): darken toward night colors by nightFactor (extended script supplies night sky_top/
    horizon/ground).
  moon_layers(rd): a disc at moonDir — limb-darkened, the SAME procedural surface as the sun
    (sun_fbm reused) for maria/craters, a PHASE terminator (darken the disc by the lit-fraction from
    the phase angle), cool tint, halo. Composited like the sun (clouds occlude per-pixel, same as the
    sun fix).
  stars(rd): hash-based points on the dome (density/brightness/twinkle via TIME), rotated by a
    time-driven angle, faded in by nightFactor. Milky Way: a great-circle band shaped by sun_fbm,
    tunable width/tilt/brightness, rotates + fades with the stars.

scenes/*.tscn: add a Moon DirectionalLight3D (shadow-casting, default low energy/off in day).
```

### Time model (full 24 h)

- **Sun elevation continuous, negative at night.** Replace the clamped day-only `f` with a continuous
  arc: `noon=(sunrise+sunset)/2`, `halfDay=(sunset-sunrise)/2`; for the day window elevation peaks at
  noon and is 0 at sunrise/sunset; past the window it goes negative to a tunable nadir at solar midnight
  (a mirrored dip). Azimuth continues E→W→(around). Feeds the existing `OrientSun`.
- **nightFactor** = `smoothstep` of `-sunElev` (≈0 while the sun is up, ramping to 1 once it's well
  below the horizon) — the master "how night is it" lever that drives sky darkening, moonlight gate,
  star/Milky-Way fade.
- **Color script extended through 24 h:** `time_presets.json` `day_script` gains night anchors (e.g.
  20/22/0/2/4 h: deep-blue→near-black sky, near-zero sun energy). `night_darkness` scales the night
  colors (dark-scary ↔ brighter); `night_ambient_floor` sets the minimum ambient (can be ~0).

### Moon

- **Position:** its own tunable arc; default roughly anti-solar (up at night). Knobs for elev/az offset
  so the moon isn't rigidly opposite the sun (decoupled, per the tunable ethos).
- **Disc + surface:** `moon_layers()` mirrors `sun_layers()` — limb darkening + the reused procedural
  surface (`sun_fbm`) for maria/crater mottling, its own size/limb/surface knobs, cool default color.
- **Phase:** a `moon_phase` knob 0 (new) → 0.5 (half) → 1 (full). Render the terminator by darkening the
  disc where the surface normal faces away from the (phase-derived) sun direction — a smooth half-sphere
  shadow across the disc. (Phase also scales moonlight energy.)
- **Halo:** soft, like the sun halo but cool/dimmer. Clouds occlude the moon per-pixel (same mechanism
  as the sun-occlusion fix).

### Stars + Milky Way (procedural)

- **Stars:** quantize `rd` to a grid, hash per cell → sparse bright points; `star_density`/`star_bright`
  knobs; **twinkle** = per-star `TIME` phase; **rotation** = rotate `rd` about a tunable celestial axis by
  `time × star_rotation`; **fade** = `nightFactor × star_fade`. Procedural, not real constellations.
- **Milky Way:** a great-circle band (distance of `rd` from a tunable galactic plane) modulated by
  `sun_fbm` for cloudy structure; `mw_brightness/width/tilt`; rotates + fades with the stars.

### Controls + presets

- Light tab: `time of day` range extended to **0–24**; night: `night darkness`, `night ambient floor`;
  moon: `moon phase`, `moon elev/az`, `moon size`, `moon limb`, moon surface knobs, `moonlight energy`,
  `moonlight color`, `moon halo`; stars: `star brightness/density/twinkle/rotation`, `milky way
  brightness/width/tilt`. All registry-routed (warn on missing id).
- **Celestial presets** `data/celestial_presets.json` (+ picker + `--celestial=N`), mirroring sun
  presets: e.g. `full_moon_clear`, `new_moon_dark`, `crescent`, `bright_moonlit`, `deep_scary`,
  `exotic` — establishing the same per-feature preset pattern. Plus `--time` (0–24) to scrub.

## Build order (sub-phases — each its own eye-gate; build one past the last pass)

1. **3a — Night sky + sun-below-horizon:** 24 h arc, nightFactor, darkening sky + ambient floor + night
   color anchors. Gate: believable dusk→night→dawn, no pops, tunable dark↔dim.
2. **3b — Moon disc:** `moon_layers()` (disc + surface + phase terminator + halo), position knobs. Gate:
   reads as a believable phased moon; phase terminator correct; clouds occlude it.
3. **3c — Moonlight:** Moon DirectionalLight3D, cool, shadow-casting, gated by nightFactor×phase. Gate:
   terrain lit coolly at night under a full moon; new-moon stays dark; no fighting the sun at dusk.
4. **3d — Stars + Milky Way:** procedural field + band, twinkle/rotation/fade, presets. Gate: believable
   night sky, stars fade across dusk/dawn, no shimmer/aliasing, Milky Way reads subtle.

## Acceptance

- Scrubbing `time of day` 0–24 gives a believable full day: sun rises, peaks, sets, sky darkens to
  night, moon + stars appear, then dawn — smooth, no pops.
- Night is **tunable across the range** (near-black scary ↔ bright moonlit) via `night_darkness` /
  `night_ambient_floor` / moonlight / star knobs; physical default looks good with no tuning.
- The moon reads as a believable phased, textured body; the phase terminator tracks `moon_phase`;
  moonlight is cool and casts shadows; clouds occlude sun AND moon per-pixel.
- Stars/Milky Way are procedural, twinkle, rotate with time, and fade in/out across dusk/dawn.
- Celestial presets apply (disc/phase/stars) and compose with any time/weather/grade.
- Build clean; no new RenderingDevice errors; in-motion cost confirmed (`--profmove`) — moonlight is a
  2nd directional light, so gate it off in day; stars/moon are sky-shader pixels (cheap).

## Risks

1. **Two directional lights at dusk** — sun + moon both up near the horizon could double-light or fight.
   Cross-fade by elevation (sun dominant when up, moon ramps as sun sets); only one casts strong shadows
   at a time. Verify the dusk transition in motion.
2. **Moon phase terminator vs the procedural surface** — the terminator (lit fraction) must multiply
   cleanly over the surface mottle without a hard seam; soft smoothstep, judged at several phases.
3. **Star aliasing/shimmer** — hash-point stars can crawl/sparkle badly in motion (the project's "never
   judge a motion artifact from a still" rule). Size points sub-pixel-aware + temporal-stable twinkle;
   judge flying + rotating.
4. **Shadow cost at night** — the moon shadow map adds cost; gate moonlight (and its shadows) off in
   full day, and consider reusing the same shadow budget as the sun (only one strong caster at a time).
5. **Night sky vs the deferred atmosphere** — Stage 3 darkens via the keyframed script; when the GPU
   atmosphere lands it should *replace* the night gradient too. Keep the night colors in the same script
   structure so the atmosphere stage can supersede them cleanly (don't hardcode night in the shader).
6. **`time_of_day` range change** — extending 0–24 must not break the Stage-2 daylight reproductions or
   the 6 moods; verify the moods + daylight scrub still match after the range/arc change.
