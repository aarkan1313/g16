# Baked-Ground Compute — Design Spec

Date: 2026-06-16 · Status: awaiting user review · Lane: texturing/look (Presenter-only)

## Why (the measured problem)

The look-lab fragment shader grew too heavy. With splat-mix + hex-tiling + triplanar:
each `s_alb/s_nrm/s_rgh` calls `tiled()` 3× (triplanar planes), and `hex_sample`
does 3 `textureGrad` each → **~27 samples per material × 2 materials ≈ 54 texture
fetches per pixel**, plus height-blend re-sampling. At 2560×1600 that is the measured
**lag**. Separately, hex-tiling's per-tile rotated lookups produce **visible
rectangular SQUARES** at hex/triplanar seams, and the over-blending adds to the
**fuzz**. Three symptoms, one root: too much per-fragment composition.

This is the tech-stack ladder's explicit trigger — *move work down the ladder when a
profiler says you must.* We've hit it for real (not speculatively).

## Decision (locked in brainstorming)

**Bake the expensive LOW-FREQUENCY decisions in a GPU-compute pass; keep crisp
high-frequency detail live per-fragment.** NOT a full virtual-texture color bake
(that caps detail at bake resolution and fights the crispness goal).

## What gets baked vs stays live

**Baked once (GPU compute → data texture(s)), low-frequency, no per-pixel sharpness needed:**
- Per-area **material choice**: dominant zone index + secondary zone index.
- **Blend weight** between them (already relief/curvature-aware in the bake).
- **Macro color tint** (the Lever-2 multi-octave drift) — bake the RGB multiplier.

These are smooth, large-scale fields — perfect for a modest-res baked texture and
exactly the costly part to compute per-fragment.

**Stays live per-fragment (high-frequency, must be crisp):**
- ONE tiled material lookup for the chosen material(s) — the actual albedo/normal/
  roughness sampling. Detail stays sharp because we still sample real textures.
- Anti-tiling via **cheap IQ-2tap** (2 samples), NOT hex (hex caused the squares and
  costs 3×). Plain/IQ only.
- Contact shading (Lever 4) — already cheap math on existing varyings, stays.

**Net per-pixel cost:** ~2 data-texture fetches + 1 tiled material lookup (≈2–6
samples) instead of ~54. Lag gone, squares gone (no hex), detail intact.

## Architecture

```
field heights ─> SplatCompute (EXTENDED) ─> ground-data texture(s):
                    RGBA #1: domZone, secZone, blendWeight, (spare)
                    RGBA #2: macro tint rgb (optional 2nd target)
                                        │  (baked once; rebake on relevant change)
zone materials + live knobs ───────────┤
                                        ▼
                 terrain_lab.gdshader (SIMPLIFIED fragment):
                   sample ground-data (1-2 fetches) -> pick materials + weight
                   ONE iq/plain tiled lookup per material, blend by baked weight
                   apply baked macro tint, contact shading -> out
```

- **Extend `SplatCompute` / `splat_weights.glsl`** (the unit already exists from
  Lever 1) to also compute + output the macro tint and a cleaner blend weight. Same
  local-RD + readback → ImageTexture pattern already proven. Possibly a 2nd output
  buffer/texture for tint.
- **Rewrite the fragment compositing** in `terrain_lab.gdshader`: delete the 7-zone
  per-fragment accumulate and the hex path from the hot loop; sample baked data,
  do the cheap lookups. Keep tile_mode {none, IQ} only (drop hex from the default;
  can leave it selectable but off).
- **Untouched:** Field unit, base field, Workbench, other scenes. Presenter-only.

## Resolution / cost

- Ground-data texture at the field res (2048²) or half (1024²) — it's low-frequency,
  so 1024² (8m/texel) likely fine and quarter the memory. Tunable; start 2048².
- Bake = one compute dispatch + readback (the existing ~field-time ballpark). On
  demand (rebake button) + when a material/zone/breakpoint changes. NOT per frame.

## What this fixes

- **Lag:** ~54 → ~6 samples/pixel. The dominant cost is gone.
- **Squares:** hex-tiling removed from the path (it caused them); IQ-2tap has no
  rotated-tile seams.
- **Fuzz:** fewer overlapping blends; single crisp tiled lookup. (If fuzz persists
  after this, it's isolated and we chase it separately — but this removes the
  over-blend contributors.)

## Risk / undo

Large change to the fragment shader + SplatCompute, but Presenter-only and behind the
existing splat toggle. `git checkout .` reverts. The base field is untouched. We keep
the old per-fragment path reachable (splat off) during A/B so the change is judgeable
against itself.

## Build order (incremental, eye-gated)

1. Extend the bake to output macro tint + clean blend weight (verify via debug view).
2. Rewrite fragment to sample baked data + ONE tiled lookup; drop hex from hot path.
3. Headless verify: sample-count down, renders clean, no squares in close-up.
4. Live: user flies it — confirm lag gone, squares gone, detail crisp. Tune.

## NOT doing (YAGNI)

- No camera-following detail tiles / full virtual texture (the bigger system; only if
  we later want whole-region crispness beyond one tiled lookup).
- No Heitz-Neyret LUT, no new material imports, no base-field changes.
- Deferred fuzziness from earlier remains deferred unless this incidentally fixes it.

## Open

- Whether to keep hex-tiling selectable at all (it's the square culprit). Lean: keep
  it in the registry but default OFF, so it's not in anyone's way.
