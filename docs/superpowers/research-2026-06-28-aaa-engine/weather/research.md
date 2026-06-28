# Weather — Research (deepen the existing mockup)

Today "weather" in WG16 is two things: (1) the **Weather axis** = a lighting-mood selector (a cloud
preset + depth fog, static, `weather_presets.json` has a single "Clear" entry), and (2) a separate
**weather-lab** sibling (40 C# files) that was never scaffolded into WG16. The user wants **dynamic
weather** — real transitions, precipitation, wetness, wind — feeding flora wind, biome rain-shadow,
and Phase-C snow-on-ground. This deepens (1) and harvests (2).

## How AAA does dynamic weather (techniques + sources)

- **A weather STATE machine / field, not just presets.** A current weather state (clear→cloudy→rain→
  storm) with **smooth transitions** + a 2D weather field over the world (coverage/type), advected by
  wind. The hard part isn't toggling effects — it's **transitioning the world smoothly between
  degrees of wetness/coverage**, which is "far more difficult than turning rain on and off."
  ([PC Gamer][1], [fxguide][2])
- **GPU particle precipitation, camera-anchored.** Rain/snow/hail as GPU particles linked to the
  camera (only simulate a volume around the player), wind-driven. ([fxguide][2])
- **Screen-space raindrops** — drops on the "lens"/near-plane drawn in view space; cheap, big
  immersion. ([fxguide][2])
- **Wetness response** — the world darkens, gains specular, puddles form in cavities as wetness
  rises; a global `wetness` scalar (0→1) drives material params + a puddle mask in low-slope/low-flow
  areas. The expensive, convincing part. ([wetness mods][3])
- **Wind as a first-class field** — a wind vector (direction + strength) drives clouds, flora sway,
  particles, and biome rain-shadow. Already implicitly needed by clouds + flora + biomes.

## What WG16 already has to build on (the fit is strong)

- **Volumetric clouds + coverage/type field** (`CloudWeather.cs`, `CloudLayers.cs`) — the weather
  state already drives cloud coverage; extend it to a *dynamic* coverage that transitions over time.
- **The decoupled Time × Weather × Grade axes** — weather is already an axis; make it *dynamic*
  (a clock-driven state machine) instead of a static preset pick.
- **Depth fog + aerial perspective** (AT-2) — already weather-responsive; tie to the state.
- **Overcast → sun/ambient dimming** (`CloudVolume.Overcast()`) — already wired; the wetness/storm
  states extend this.
- **The weather-lab (40 files)** — a research bed to harvest the easing/transition "brain" the user
  mentioned (the lab's design: C# easing brain → GPU renderers, rain first slice).

## How dynamic weather maps onto WG16

- **A `WeatherState` clock + state machine** (C#, cheap): current + target weather, transition
  easing, per-biome weather tendencies (deserts rarely rain; from `biomes.json`). Drives the existing
  cloud coverage, fog, overcast dimming, and a new global `wetness` + `wind` vector.
- **Precipitation** = camera-anchored GPU particles (tier-3, only when active) + screen-space drops;
  gated to the active weather state (≈0 when clear — the always-off-when-not-needed model).
- **Wetness** = a global scalar → terrain/flora material params + a puddle mask (low-slope/low-flow
  cavities, derivable from the drainage channel_mask already planned). Phase-C "wetness response."
- **Wind** = a field consumed by clouds (drift), flora (sway), particles (drive), biomes
  (rain-shadow). One source of truth, many consumers — the connective-tissue pattern again.
- **Snow-on-ground** (Phase C) = wetness's cold sibling: accumulation by temperature (biome) +
  aspect, a height/coverage delta on the terrain surface.

## Budget / "not always on" (`02`, `04`)

Weather is cheap when clear (no particles, wetness=0). Cost appears during precipitation: GPU
particles (camera volume only) + wetness material branch + screen drops. Tier-scaled particle
density + draw distance. The state machine + wind field are near-free CPU. Fits comfortably — the
spend is bounded to active-weather moments and the local camera volume.

## Risks / open questions
- **Smooth wetness transition** is the genuine hard part (not effect on/off) — needs eased material
  blends + puddle fill/drain over time, eye-gated in motion.
- **Cloud↔weather coupling** — dynamic coverage must drive the existing cloud raymarch without
  re-baking noise per frame (use the existing coverage field, animate its params).
- **The "huge dome" cloud repetition** (ROADMAP banked) interacts with dynamic weather over the
  infinite world — weather should vary by region (per-biome tendencies) so it's not globally uniform.
- **Determinism** — for the hybrid world, regional weather tendencies are per-biome data; the live
  state is a runtime clock (not cached) — clarify what's deterministic vs live.
- **Harvest vs rebuild the weather-lab** — audit the 40 files; take the easing/transition brain, don't
  blindly scaffold a parallel system (avoid the design debt of two weather systems).

## Recommendation
Deepen in Phase C (the ROADMAP's home for precipitation/wetness/snow) but **build the wind field +
dynamic-coverage state machine earlier** (Phase B) because flora (wind) and biomes (rain-shadow)
already need wind. Sequence: wind field → dynamic coverage/transitions → precipitation particles →
wetness response → snow-on-ground. Each behind the weather module toggle, default to current static
look until gated.

## Sources
[1]: https://www.pcgamer.com/how-developers-make-perfect-rain-in-games/
[2]: https://www.fxguide.com/fxfeatured/game-environments-partb/
[3]: https://researchhub.blog/photorealistic-rain-5-sse-wetness-effect-mods
