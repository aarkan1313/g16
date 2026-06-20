# Spec: Clouds Overhaul (Sky lane #2)

Date: 2026-06-20. Status: DESIGN (brainstorm complete, user-approved). Owner: Sun/Light lane.
Parent: ROADMAP ▸ ☀️🌙 FINISH THE FULL SKY SYSTEM ▸ #2. Follows Stage 3 (Night & Celestial, complete).

## Why

The cloud broad-review passed ("all good"), but the sky reads samey: the shape model is **cumulus-only**
and every deck is a **flat slab** (fixed altitude + thickness, no vertical density distribution — memory
`cloud-deck-vertical-realism`). There are no **cirrus/wispy** clouds, limited type variety, and visible
**tiling/repetition** in the dome. This pass adds cloud **types** (cirrus + stratus, cumulus preserved),
**vertical realism** (height profile within a deck), **anti-repetition/horizon** polish, and a richer
**preset** library — keeping the proven deck-raymarch and the amortized perf budget.

## Scope

IN: vertical density profile within decks; stratus shape-mode in the raymarch; **cirrus as a separate 2D
striated layer** in `cloud_sky.gdshader` (hybrid — user's call); anti-repetition (domain warp / macro
coverage variety) + horizon soft-spot; preset library + Clouds-tab knobs for the new params. OUT (deferred,
user's call): **weather-axis tie-in** (clear→overcast→storm driving coverage/type/decks — its own later
stage); a unified all-volumetric cirrus (hybrid 2D is enough; revisit only if the infinite world needs
cirrus depth); precipitation/visible water (Phase C of the master roadmap).

## Architecture — extend the deck system + a 2D cirrus layer

```
CloudLayer (scripts/lab/CloudLayers.cs) — add vertical-profile fields (packed to the GPU buffer):
  ProfileBottom, ProfileTop (height-fraction shaping: flat bottom → rounded top), Anvil (top spread),
  ShapeMode (0 cumulus = today · 1 stratus sheet). Stride grows; Pack() + the march read them in lockstep.

cloud_raymarch.glsl — height_profile(hFrac, layer): density *= a vertical shape so a deck reads as a 3D
  volume (flat base, rounded/anvil top) not a slab. ShapeMode switches cumulus↔stratus (sheet: high
  coverage, low detail, thin profile). Anti-repetition: domain-warp + a larger-scale coverage modulation
  so the base field stops obviously tiling. MUST stay byte-identical with the shadow shader (fields 0-11).

cloud_sky.gdshader — cirrus_layer(rd): wind-aligned striated fbm (stretched along a tunable wind dir),
  thin/semi-transparent, gated to a high altitude band, simple sun tint + day/night fade. Composited
  with the dome (behind cumulus that's nearer). Cheap (a few 2D fbm taps) — NOT volumetric.

scripts/lab/CloudVolume.cs — CirrusState push (SetCirrus*: coverage, density, wind dir/speed, sharpness,
  altitude, color); existing SetLayers stays the deck-stack authoring seam.

data/cloud_presets.json — richer library authoring deck stacks + cirrus + knobs (per-feature preset
  pattern, mirroring celestial/sun presets). Clouds-tab gains the new deck + cirrus controls (registry).
```

### Build order (sub-phases — each its own eye-gate; build one past the last pass)

1. **CO-1 — Vertical realism.** `height_profile()` in the march + new `CloudLayer` profile fields + Pack/
   shadow lockstep. Gate: cumulus decks read as 3D volumes (flat base, rounded/anvil tops), not flat slabs;
   the approved cumulus look reproduces at neutral profile values; cost unchanged.
2. **CO-2 — Types.** Stratus shape-mode (flat sheet) in the march; **cirrus 2D layer** (`cirrus_layer()` +
   `CirrusState` + setters + Clouds-tab knobs). Gate: believable wind-streaked cirrus "lines"; stratus
   overcast sheet; cumulus unchanged; cirrus fades correctly day↔night and tints at sunset.
3. **CO-3 — Anti-repetition + horizon.** Domain warp + macro coverage variety in the march; cirrus domain
   warp; horizon soft-spot. Gate: no obvious tiling flying over a wide area; distant sky/horizon reads
   clean; no shimmer in motion.
4. **CO-4 — Presets + tunability.** `cloud_presets.json` library (fair-weather, broken, overcast-stratus,
   cirrus-streaked, stormy-anvil, clear…) authoring deck stacks + cirrus; Clouds-tab knobs for all new
   params; mirror the celestial-preset picker. Gate: presets give distinct believable skies and compose
   with any time/weather/grade; super-tunable.

## Acceptance

- Decks read as 3D volumes (vertical profile), not slabs; cumulus baseline preserved at neutral values.
- Cirrus reads as believable high wind-streaked "lines"; stratus as an overcast sheet; both tunable.
- No obvious tiling/repetition flying over a wide area; horizon reads clean; no motion shimmer.
- A preset library spans clear→fair-weather→broken→overcast→cirrus-streaked→stormy, each composing with
  any time/weather/grade; all new params on the Clouds tab.
- In-motion cost stays within the amortized cloud budget (`--profmove`); cirrus is ~free; no new
  volumetric decks required.

## Risks

1. **Shadow/march field drift** — the vertical-profile fields must stay byte-identical between the raymarch
   and the cloud-shadow shader (fields 0-11 rule in CloudLayers). Add the profile fields in lockstep; verify
   `--shadowcheck`.
2. **Cirrus banding/tiling** — 2D striated fbm can band or repeat; use domain warp + multi-octave + a
   subtle animated drift; judge flying, not from a still.
3. **Vertical profile vs the lat-long dome** — the dome samples by direction; a tall cumulus must still
   read right near the horizon. Verify the profile holds at grazing angles.
4. **Perf creep** — more decks/detail/types add march cost. Stay amortized; cirrus stays 2D; profile is a
   multiply. `--profmove` each phase.
5. **Preset schema growth** — new fields must round-trip through `CloudLayers.Build` (one schema source) so
   file + preset loaders can't drift.
