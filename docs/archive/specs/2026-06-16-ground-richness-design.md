# Ground Richness System — Design Spec

Date: 2026-06-16 · Status: awaiting user review · Lane: texturing/look (Presenter-only)

## Problem

The look-lab ground reads as **uniform, repetitive, flat, and lifeless**. Each of the
7 height/slope zones is a single material filled edge to edge; large-scale color
variation is weak; surfaces are evenly lit with no depth in creases. Anti-tiling
(hex / IQ-2tap) was added in a prior session but only addresses micro-repetition —
it cannot make a one-material slope *interesting*.

User wants all four problems addressed thoroughly. This is a genuinely new feature
(introduces a new GPU-compute unit), so it gets a short spec.

## Goal

Ground that reads natural, varied, and alive — surface-only (flora stays a future
pass). Four independent levers, each behind a live toggle/slider so the user's eye
judges each contribution in isolation.

## Decisions locked (from brainstorming)

- **Do all four levers**, not a subset.
- **Build Lever 1's material-mix mask as a GPU-compute splat pass NOW** (user call),
  not a deferred fragment→compute migration. A material-weight mask is embarrassingly-
  parallel per-cell math (pure function of height/slope/curvature/noise per texel) —
  squarely in the GPU-compute tier of the tech-stack ladder. Accepted tradeoff: mask
  parameter tweaks require a re-bake (not slider-live); mitigated by fast on-demand
  re-bake from the UI.
- Levers 2 (macro color) and 4 (contact/wear) stay **fragment-shader** — they are pure
  per-pixel functions of position/normal/curvature evaluated at render time, with no
  meaningful compute version (a bake would just store the same math at memory cost).
- Lever 3 (anti-tiling) is **already built** — folds in as-is.
- No flora, no new texture imports (work with the 108 accepted), base field untouched.

## The four levers

### Lever 1 — Multi-material mixing via a GPU-compute splat mask  *(the big one)*
**Kills:** "too uniform / one-note."
**What:** within each zone, blend a **dominant + secondary** material by a mid-scale
field, so "slope" = scree *with patches of* dirt/lichen, "high" = rock *with* snow in
pockets. The 7 zones remain the macro structure; each spot becomes a believable mix.
**GPU-compute pass (`SplatCompute` + `shaders/splat_weights.glsl`):** writes a
per-texel weight map — for each cell, the dominant zone + a secondary-mix factor —
as a **GPU texture** the terrain shader samples (no CPU round-trip). Inputs: the
heightfield + the same height/slope/curvature/noise logic currently in
`zone_weights()`, lifted into compute. Output: an `RG`(or `RGBA`) texture
(dominant index / mix amount [/ secondary index / extra]).
**Fragment side:** terrain shader samples the splat texture, picks dominant +
secondary material, blends them (2 samples, not 7). Mix strength slider; toggle off
(falls back to current single-material `zone_weights`).
**Companion material:** auto-derived from the adjacent zone for v1 (zero new UI). A
per-zone "secondary" dropdown can come later if the eye wants explicit control.

### Lever 2 — Macro color variation  *(fragment)*
**Kills:** "flat/lifeless," helps "repeated."
**What:** strengthen the existing weak macro tint (`macro_amp=0.14`, value-only) into
**multi-octave low-frequency color drift** — Far Cry 5's single biggest "doesn't look
tiled" trick. Two octaves (big + medium); gentle hue/saturation drift, not just
brightness; neutral-centered so it both lightens and darkens. Sliders: amount, scale.
Toggle off.

### Lever 3 — Anti-tiling  *(already built)*
**Kills:** "repeats." hex-tiling / IQ-2tap + `tile_mode` selector from prior session.
No new work; richness levers make it more effective (macro + mixing hide residual
tiling). Keep the toggle.

### Lever 4 — Contact & wear shading  *(fragment)*
**Kills:** "flat surfaces."
**What:** use already-computed `v_curv` (curvature) + slope to drive:
- **Crevice darkening:** concave folds get subtly darker (cheap AO-like term).
- **Slope wear:** steeper → more exposed rock / less soil (extend to roughness/color).
- Optional **snow dusting** on upward faces near the snow line.
Pure math on existing varyings — very cheap. Sliders for strength; toggle off.

## Architecture

```
field_params.json ─> FieldCompute (GPU) ─> heights ─┬─> LabTerrain mesh + heightmap tex
                                                     └─> SplatCompute (GPU) ─> splat weight TEXTURE
                                                                                      │
zone/mix/macro/contact uniforms ───────────────────────────────────────────────────┤
                                                                                      ▼
                                                              terrain_lab.gdshader (fragment) ─> screen
```

- **New unit:** `scripts/lab/SplatCompute.cs` + `shaders/splat_weights.glsl`, mirroring
  `FieldCompute.cs` exactly (local RenderingDevice, GLSL→SpirV, 8×8 dispatch groups).
  Difference: output is a **GPU texture** handed to the material, not a CPU `float[]`.
  One job: produce the splat weight map. Knows nothing about cameras/UI.
- **Modified:** `terrain_lab.gdshader` (sample splat tex in compositing; Lever 2 + 4
  math in fragment), `TerrainLab.cs` (own the splat texture, re-bake entry point,
  setters), `TerrainLabUI.cs` (toggles + sliders + a "rebake splat" button).
- **Untouched:** Field unit, base lab, Workbench, other scenes. Presenter-only;
  zero base-field risk. The modular boundary holds (adding a unit + wiring lines).

## Build order (incremental, eye-gated — spike cheap, judge early)

1. **Lever 2 (macro color)** — cheapest, high impact, recalibrates the eye. *(fragment)*
2. **Lever 4 (contact shading)** — also cheap fragment math; adds depth. *(fragment)*
3. **Lever 1 (splat compute + mixing)** — the big one; new GPU unit. Build the compute
   pass, verify the mask, then wire fragment mixing. *(GPU compute + fragment)*
4. Lever 3 already done — confirm it still composes.

Each step: build → headless A/B capture (on/off) self-verify → user flies it live →
adjust → next. Nothing merged until the eye signs off.

## Tunability / re-bake

- Levers 2, 4, 3: fully slider-live.
- Lever 1 mask: re-baked on demand. A **"rebake splat" button** + auto-rebake when a
  zone material or the relevant breakpoints change. Mix *strength* (fragment) stays
  live; only the *mask structure* needs a bake. Re-bake is one compute dispatch at
  2048² (~the field's ~390 ms ballpark) — fast enough for iterate-and-look.

## Verification

- Per lever: headless `--auto-shot` A/B (toggle on vs off, same camera) — I look first.
- Then live fly-through; user's eye is the gate.
- Splat compute: sanity-check the mask texture renders sane regions (visualize the
  weight map as debug color) before wiring it to materials.

## What we are NOT doing (YAGNI)

- No virtual texturing / RVT-style cache (the heaviest tier — only if per-frame cost
  forces amortization after all four levers).
- No Heitz-Neyret per-texture histogram LUT (108 materials → real pipeline, marginal
  gain over hex-tiling).
- No flora / decoration (future pass).
- No new material imports; no base-field changes.

## Risk / undo

Additive, behind toggles, Presenter-only. Bad result = `git checkout .`. The new
compute unit is isolated; if it misbehaves, Lever 1 toggles off and the other three
(fragment) are unaffected.

## Open / deferred

- Fuzziness from the prior session is **deferred** (noted in DECISIONS). Likely the
  same root family as repetition; the richness levers may incidentally improve it.
  Revisit after, with fresh eyes.
- Per-zone explicit secondary-material dropdowns: deferred to "if the eye wants it."
