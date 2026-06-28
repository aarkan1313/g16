# Weather — Spec (deepen existing; design-ahead, build gated)

Scope: turn the static Weather *axis* (a preset picker) into **dynamic weather** — a state machine +
wind field + precipitation + wetness + (Phase-C) snow — feeding flora wind, biome rain-shadow, and
the existing cloud/fog/overcast systems. Default to the current static look until gated. Wind +
state machine land in Phase B (flora/biomes need wind); precipitation/wetness/snow in Phase C.

## Architecture (extend the existing axis, harvest the weather-lab)
```
WeatherState (C# clock + state machine)
  current/target weather + eased transition  ─> cloud coverage (existing field, animate params)
  per-biome tendencies (biomes.json)          ─> fog + overcast dimming (existing seams)
  ─> wind vector (field)   ─> clouds drift · flora sway · particles · biome rain-shadow
  ─> wetness scalar (0..1) ─> material params + puddle mask (drainage channel_mask)
  ─> precipitation kind/intensity ─> GPU particles (camera volume) + screen-space drops
```
Harvest the weather-lab's easing/transition "brain" (audit its 40 files first) rather than scaffold
a parallel system — one weather system, not two.

## Units (each build → eye-gate)

### W1 · Wind field (Phase B — flora/biomes need it)
- A wind vector (direction + strength), slowly varying + gust noise; consumed by clouds (drift),
  flora (sway, `trees-flora` F5), biomes (rain-shadow, `biomes` BIO1). Data: `weather_params.json`.
- **Gate:** clouds + flora respond to wind coherently; wind drives biome rain-shadow sensibly.

### W2 · Dynamic coverage + transitions (Phase B)
- Replace the static preset pick with a clock-driven state machine: current→target weather with
  eased transitions, animating the existing cloud coverage/fog/overcast params (no per-frame noise
  re-bake). Per-biome tendencies from `biomes.json`.
- **Gate:** weather transitions smoothly over time (clear↔cloudy↔overcast), varies by region.

### W3 · Precipitation (Phase C)
- Camera-anchored GPU particles (rain/snow/hail), wind-driven, gated to active state (≈0 when clear);
  screen-space drops on the near plane. Tier-scaled density/distance.
- **Gate:** rain/snow reads convincing in motion, camera-anchored, cheap when clear.

### W4 · Wetness response (Phase C — the hard part)
- Global `wetness` scalar → terrain/flora material darken + specular; **puddle mask** in low-slope/
  low-flow cavities (from drainage channel_mask); **eased fill/drain over time** (the genuinely hard
  transition, not on/off).
- **Gate:** the world smoothly wets/dries in motion, puddles form/drain naturally.

### W5 · Snow-on-ground (Phase C — wetness's cold sibling)
- Accumulation by temperature (biome) + aspect; a height/coverage delta on the terrain surface;
  ties to BIO4 altitude caps.
- **Gate:** snow accumulates/melts plausibly by biome/altitude/aspect.

## Data contract (game-agnostic)
- `weather_params.json` — wind params, state-machine states + transition rules, precipitation/wetness/
  snow knobs. `weather_presets.json` — extend with dynamic states (not just static moods).
- Per-biome tendencies live in `biomes.json` (consumed here). `weather` module toggle (`04`).

## Dependencies & integration
- **Reads:** biome tendencies (per-region weather), drainage channel_mask (puddles), temperature
  (snow). **Writes:** wind field (→ flora, clouds, biome rain-shadow), wetness (→ materials), snow
  (→ terrain surface).
- **Coordinates with:** clouds/atmosphere (existing axis — extend, don't fork), flora (wind), biomes
  (wind in / tendencies out), shadow rebuild (overcast → softer shadows).

## Performance plan (`02`)
- State machine + wind = near-free CPU. Cost only during active precipitation: GPU particles (camera
  volume) + wetness branch + screen drops. Tier-scaled. ≈0 when clear (always-off-when-not-raining).

## STOP criterion
Phase B stops at W1+W2 (wind + smooth transitions — what flora/biomes need). Precipitation/wetness/
snow (W3–W5) are Phase C, gated individually. Do not build W4 wetness before drainage's channel_mask
exists (puddles read from it). Audit + harvest the weather-lab before building W2's transition brain.
