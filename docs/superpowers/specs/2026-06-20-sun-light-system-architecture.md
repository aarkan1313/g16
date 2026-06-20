# Sun & Light System — Master Architecture & Roadmap

Date: 2026-06-20. Status: ARCHITECTURE (umbrella for the Sun & Light arc). Owner: TBD.
Companion: arc handoff `handoffs/2026-06-19-sun-light-arc-handoff.md`; ROADMAP.md ☀️ section.
Supersedes the 3-stage sketch in the arc handoff with a fuller decomposition (per user direction
2026-06-20: "any weather at any time and any mood at any time… it's a complex system — make a full
fleshed-out roadmap/plan/spec, everything needs focus and attention").

> **STATUS (2026-06-20) — `../../ROADMAP.md` is authoritative for cross-arc sequencing.** This is a
> **Phase A** lane: finish the FULL sky system before the world phase (user's call). **Built + eye-gate
> owed:** Stage 1 sun disc + Stage 2 decouple/time-of-day (daylight). **Then, each gated, one phase past
> the last gate:** Stage 3 night + moon/stars · the **GPU-compute atmosphere** — DEFERRED + must be
> re-spec'd around the **CloudVolume render-thread `Texture2Drd` seam, NOT FieldCompute** (a local-RD
> texture can't be sampled by a material) · Stage 4 auto-cycle + fantasy. The Stage-2 doc reconciled the
> earlier "atmosphere inside Stage 2" framing — Stage 2 ships keyframed color; atmosphere is its own stage.

## Vision

A **fully decoupled, data-driven sky & lighting system**: the final look at any instant is the
composition of **three orthogonal axes plus a celestial layer**, every parameter a knob, any
combination valid — exactly as in reality (any weather, at any time of day, under any artistic grade).

```
        ┌──────────── TIME axis ────────────┐   physical celestial state as f(time_of_day)
        │  sun position (elev+azimuth arc)   │   — sun travel, color temperature, sky gradient,
        │  sun color-temp + energy curve     │     ambient level/color, → drives Stage-1 sun disc
        │  sky gradient (day→dusk→night)     │     reddening automatically. Below horizon = night.
        │  ambient level + color             │
        └────────────────────────────────────┘
        ┌────────── WEATHER axis ───────────┐    atmosphere, independent of time
        │  cloud cover/type/density (clouds) │   — the existing cloud system, bound to a weather
        │  fog density/color/height/scatter  │     state; storminess; (precip hooks = future)
        └────────────────────────────────────┘
        ┌──────── MOOD / GRADE axis ────────┐    artistic treatment, independent of time+weather
        │  tonemap exposure/white            │   — the "look": exposure, contrast, saturation,
        │  contrast, saturation, color tint  │     grade tint, bloom character
        │  bloom/glow character              │
        └────────────────────────────────────┘
        ┌────────── ATMOSPHERE (GPU compute) ┐    the SKY renderer — physically-based scattering
        │  Hillaire-style dynamic LUTs:      │   — transmittance + multi-scatter + sky-view LUTs baked
        │  transmittance · multiscatter ·    │     on a local RenderingDevice (the cloud/field compute
        │  sky-view, re-baked when sun moves │     pattern), sampled in cloud_sky.gdshader. PRODUCES the
        │  → sky color, sun tint, ambient,   │     sky gradient + sun color/energy + ambient + aerial as
        │  aerial perspective, for any elev  │     a function of the TIME-driven sun direction. Replaces
        └────────────────────────────────────┘     keyframed color stops. Extends into night (Stage 3).

        ┌────────── CELESTIAL layer ────────┐    sky-body CONTENT, positioned by TIME
        │  sun disc (Stage 1 ✅)             │   — moon(s)+phases+moonlight, star field, fantasy
        │  moon(s), stars, fantasy bodies    │     (blood moon, colored/multiple suns/moons)
        └────────────────────────────────────┘
                         ▼
              LightingComposer  →  WorldEnvironment + Sun(DirectionalLight) + cloud_sky.gdshader
              (composes the 3 axes + celestial each frame into the actual scene state)
```

**Why decouple:** today a "mood" (`lighting_moods.json`) bundles all of this into one frozen look —
`Golden Hour` = a time (sun 12°, warm) + a grade (contrast 1.1, sat 1.2) + an atmosphere (fog). You
can't get "golden-hour sun under a storm" or "harsh-midday geometry with a moody desaturated grade."
Splitting into independent axes makes the full combinatorial range reachable and keeps each concern
small, testable, and tunable — the WG16 data-driven ethos.

## The decoupling (the core refactor)

Introduce three plain state structs, each loaded from its own JSON preset set, each a set of Light-tab
knobs, composed by a single **`LightingComposer`** that writes the scene every frame:

| Axis | Owns (from today's bundled mood) | Preset file |
|---|---|---|
| **TimeState** | `time_of_day` → sun_angle/az (arc), sun_energy, sun_color (temp), sky_top/horizon/ground, ambient, ambient_sky | `data/time_presets.json` (or computed from a physical model + a few anchors) |
| **WeatherState** | cloud_* (existing cloud presets), fog_color/density/aerial/height/heightd/sun_scatter | `data/weather_presets.json` (cloud_presets.json folds in) |
| **GradeState** | exposure, white, glow, contrast, saturation, (brightness), grade tint | `data/grade_presets.json` |

`LightingComposer.Apply()` = `TimeState` → sun transform/color/sky/ambient; `WeatherState` →
clouds+fog; `GradeState` → env tonemap/adjustments/glow. Replaces the monolithic `ApplyMood`. The 6
existing moods are **split** across the three preset files (each mood's sun/sky → a Time preset, its
fog → a Weather preset, its grade → a Grade preset) so nothing authored is lost and "pick a mood" still
works as "pick one Time + one Weather + one Grade" (a named combo can still set all three).

## Roadmap — stages (each eye-gated, each its own spec → plan)

- **Stage 1 — Sun disc polish ✅ BUILT (2026-06-19→20), eye-gate owed.** `sun_layers()` in
  `cloud_sky.gdshader`. (Spec/plan `2026-06-19-sun-disc-polish*`.)

- **Stage 2 — Decoupling + Time-of-day driver + GPU-compute atmosphere (daylight).** Build
  `LightingComposer` + the three state structs; split the 6 moods into Time/Weather/Grade presets;
  implement the **Time axis**: a `time_of_day` knob (e.g. 5.0–19.0 h) drives the sun's elevation+azimuth
  along a tunable analytic day arc. Build the **GPU-compute atmosphere** (Hillaire LUTs on a local RD) as
  the sky renderer — it produces the sky gradient + sun color/energy + ambient from the sun direction
  (replacing keyframed color stops). Stage-1's sun reddening + the atmosphere both key off the same sun
  elevation. Weather + Grade are independent knobs/presets. Below-horizon = dark (night = Stage 3).
  **Acceptance:** scrub `time_of_day` and the sun travels while the atmosphere shifts the sky/sun-color/
  ambient physically + cohesively (real dawn→noon→sunset); any Weather + any Grade independently; the 6
  old moods reproducible as Time×Weather×Grade combos. Detailed spec:
  `2026-06-20-lighting-decouple-and-time-axis-design.md`.

- **Stage 3 — Night & celestial.** Extend the Time axis through the full 24 h: sky darkens to night
  (deep blue → near-black gradient), night ambient/GI floor, sun off below horizon. Add the celestial
  content: **moon** (disc in `cloud_sky.gdshader` like the sun, with **phases**, cool **moonlight** as a
  second directional light + night ambient tint), and a **star field** (procedural, fading in as the sky
  darkens, rotating with time). **Acceptance:** a believable night at `time_of_day` ~22 h; the moon
  lights the terrain coolly; stars fade in/out across dusk/dawn; clouds still occlude sun AND moon.

- **Stage 4 — Auto day/night cycle + fantasy/exotic.** A running clock (play/pause + speed) advancing
  `time_of_day` so the world cycles; smooth dawn/dusk/golden-hour transitions verified in motion. Plus
  the **fantasy axis**: blood/colored moon, colored or multiple suns/moons, exotic sky palettes — all as
  tunable/togglable knobs layered on the celestial + time axes. **Acceptance:** time animates smoothly
  with no pops; exotic looks reachable from knobs without breaking the physical default.

## Cross-cutting principles

- **Full-range tunable:** physically-grounded defaults, every parameter a Light-tab knob (registry
  `scenef`/`scene`), pushable realistic → stylized → fantasy. No hardcoded look.
- **One composer, three states:** `LightingComposer` is the only writer of the scene's lighting; the
  three state structs are pure data. Keeps each axis isolated + testable, and the composition explicit.
- **Reuse, don't rebuild:** clouds (Weather), the sun disc (Stage 1), AgX tonemap + adjustments (Grade),
  fog/aerial all already exist — Stage 2 re-homes them behind the axes, it doesn't reimplement them.
- **Verify windowed, in motion** (no shader unit tests): `--time=<h>` (new) to scrub, `--weather=N`,
  `--grade=N`, plus the existing `--lookatsun`/`--auto-shot`. Judge cohesion in motion, never a still.
- **Perf:** the composer runs once per change (or per frame only while the clock auto-advances); the
  physical curves are cheap CPU math. Night adds a 2nd directional light (moon) — gate it off in day.

## Resolved design decisions

- **Time model:** sun POSITION = analytic arc (CPU, cheap, tunable sunrise/sunset/peak-elev/azimuth — not
  a bake). Sky COLOR/sun-tint/ambient = the **GPU-compute ATMOSPHERE** (physically-based, not keyframes —
  the AAA/GPU "better option" per the pillars). The Time axis feeds the sun direction; the atmosphere
  produces the look.
- **Atmosphere technique:** Hillaire-style dynamic scattering (transmittance LUT + sky-view LUT, optional
  multi-scatter LUT) computed on a **local RenderingDevice** (the `FieldCompute`/`CloudNoiseCompute`
  pattern), re-baked when the sun moves (cheap; per-frame only while auto-cycling in Stage 4). Sampled in
  `cloud_sky.gdshader` by view+sun direction. Gives correct daytime→sunset gradients, sun color via
  transmittance, sky-integral ambient, and aerial perspective — and extends to night (Stage 3).
- **Moods survive as combo presets** (one click sets a Time + Weather + Grade) on top of the independent
  axes.

## Open forks (resolve in the relevant stage's spec)

- Multi-scatter LUT now vs. later (quality vs. scope) — Stage 2 spec decides.
- Star-field + moon-phase technique (Stage 3 spec).
