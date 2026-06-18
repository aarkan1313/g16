# Per-deck phase / albedo / tint (cloud roadmap #1)

**Date:** 2026-06-18
**Goal:** Make cumulus and cirrus read as *different kinds* of cloud by lighting each
deck with its own phase, albedo, sun-absorption and color tint — and thereby make the
multi-deck height structure visible. Top "looks real + makes layers visible" lever per
the cloud-look audit.

## Key insight
WG16 decks are **altitude-separated** (cumulus ~1500 m, cirrus ~5500 m) and do not
overlap. So at any ray sample exactly one deck is active. We do NOT need the audit's
general overlapping-deck recipe (carry N densities, accumulate N luminances under a
shared transmittance T). Instead: identify the **containing** deck at each sample and
light that sample with its material. Cheaper, simpler, same visual goal.

## Per-deck material fields (new)
Added to each layer, after the existing 12 density fields:

| idx | field       | cumulus | cirrus | effect |
|-----|-------------|---------|--------|--------|
| 12  | phase_g     | ~0.85   | ~0.35  | forward silver-lining sharpness |
| 13  | phase_iso   | ~0.15   | ~0.6   | isotropic blend — cirrus evenly lit, cumulus directional |
| 14  | albedo      | ~1.0    | ~1.25  | cirrus thin & bright |
| 15  | sun_absorb  | ~1.2    | ~0.4   | cumulus dark cores + bright edges (=3D form); cirrus uniform |
| 16  | tint_r      | warm    | cool   | subtle per-deck color (cumulus warmer, cirrus cooler/whiter) |
| 17  | tint_g      |         |        | |
| 18  | tint_b      | warm    | cool   | |
| 19  | (reserved)  | 0       | 0      | future per-deck knob |

Layer stride: **12 → 20 floats** (3 → 5 vec4 per layer). `layers[24]` → `layers[40]`.
`LF(i,f)` stride `*3` → `*5` in **both** raymarch and shadow shaders (shadow ignores
fields 12–19; density math fields 0–11 unchanged → coupling preserved).

## Shader changes
- `density_all(p, windOff, out float opac, out int activeLayer)` — returns the deck
  containing `p` (the last/only band that contained it). Density math untouched.
- Raymarch lighting loop:
  - phase from active deck: `mix(hg(cosA, phase_g), hg(cosA, -0.25), phase_iso)`.
  - sun optical-depth absorption scaled by active deck `sun_absorb` (applied at the
    receiving sample, not inside the light march — the march still sums all decks so
    cirrus above shadows cumulus below).
  - luminance multiplied by active deck `albedo` and `tint_rgb`.
- `cloud_shadow.glsl`: only the `LF` stride changes; density identical.

## Plumbing
- `CloudLayers.Stride` 12 → 20; `CloudLayer` record + `Pack` + JSON loader gain the 7
  new fields (`phase_g, phase_iso, albedo, sun_absorb, tint_r, tint_g, tint_b`).
- `CloudVolume.BuildParams` / `BuildShadowParams` Vec4Array now 40 floats.
- Layer-0-from-knobs path fills the new fields with cumulus defaults.
- `data/cloud_layers.json` seed: cumulus deck + cirrus deck get distinct values.

## Toggle
`--perdeck=0/1` CLI flag + knob. 0 = current global phase/albedo (A); 1 = per-deck (B).
Judge in motion by toggling.

## Verification
1. Build clean.
2. `--shadowcheck --coverage=0.5` must still PASS (density math unchanged → coupling
   regression guard).
3. Launch with both decks visible (coverage ~0.7), toggle `--perdeck` to A/B.

## Honesty flag
Per-deck lighting differentiates decks; it does NOT fix base flatness. If the underlying
lighting still reads flat/dull when toggled, the audit's "lighting is done" claim is
wrong and we revisit multi-scatter / occluded ambient itself.
