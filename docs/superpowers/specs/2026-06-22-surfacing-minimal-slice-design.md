# GROUND — Minimal Surfacing Slice ("readable landforms, to judge erosion")

Date: 2026-06-22. Status: SPEC (brainstormed; user-approved scope). Lane: Terrain (surface/skin).

## Why this exists (and why it is NOT the full surfacing arc)

Terrain geometry is done through S3 (pop-free infinite streaming), but the surface is the deliberate
**placeholder height/slope colour ramp** (`shaders/ground.gdshader`) — it reads as a smooth, untextured
blob. The user's next move is **erosion** (carve valleys/drainage), but erosion can't be eye-judged on a
colour-ramp blob: you can't see whether a valley reads as a valley. So we build **just enough surface** that
**landforms (valleys, ridges, slopes) read clearly in motion**, then PAUSE and go to erosion to judge it.

This is explicitly **NOT** the authoritative full surfacing arc
(`2026-06-21-ground-material-system-reset-design.md`, the 6-unit texture-array system). That arc stays
paused. This slice is a small, mostly-shader step with one job: legibility for judging erosion.

**User constraint (verbatim intent):** don't waste time on infra we'll throw away. Shaders/logic we keep
using later are fine to invest in; use the REAL texture library (`assets/materials/`), not procedural fakes.

## The bar (definition of "enough")

**Readable landforms — slope/depth legible.** In motion at close/mid/far: valleys, ridges, and slope
changes read as 3D shape (not a smooth blob); a few sensible materials by slope/height; real tiling texture
with real normal maps; **no 4 m grid blockiness**; no mip "fuzz". NOT AAA beauty, NOT close-up relief depth.

## Scope

- **In:** ~5 real materials from `assets/materials/`, placed per-pixel by height + slope, sampled with
  mipmaps + triplanar-on-steep + real normal maps, blended with per-pixel-soft boundaries, into the
  existing lighting. Live-tunable thresholds/material choice so "readable" can be dialled in motion.
- **Out (the paused full arc — do NOT build):** texture-array (`sampler2DArray`) system, material
  manifest/library build step, the per-pixel placement *engine* (data-driven rules/biomes), POM / relief
  depth (Unit 5), many-material/biome scaling, lighting re-tune (Unit 6), histogram anti-tiling re-host.
  (Simple tiling is accepted; obvious repetition is tolerable at this bar — it's a legibility slice.)

## Keep vs dispose (the user's "no throwaway infra" guardrail)

- **KEEP / reusable:** the placement-by-height/slope + triplanar + height-aware soft-boundary **blend
  logic** — this is conceptually the same job the full arc's Unit 2 (placement) + Unit 4 (blend) do, so it
  ports to the array engine later (only the texture *fetch* swaps: fixed `sampler2D` → `sampler2DArray`).
  Using the real material library is also kept work (those are the full arc's materials too).
- **DISPOSE (tiny, intentional, ~10 lines):** the fixed per-material `sampler2D` bindings (vs arrays). This
  is the only deliberately-disposable surface area, and it is unavoidable for a non-array slice.
- **Transitional:** the full arc plans a *new* `shaders/ground.gdshader` for the array system anyway, built
  alongside then flipped. So editing the current shader now is not wasted — it's the path being improved,
  and its blend logic informs the real engine.

## Architecture — one shader + thin C#

**Components:**

1. **Material set (data, ~5 roles).** Picked from `assets/materials/` (737 PBR sets, each
   albedo/normal/ao/roughness). Starter pick (tunable live; final choice during the eye-gate):
   - low/flat → sand or gravel (e.g. `03_coarse_sand`)
   - valley/mid grass → `01_tussock_grass`
   - mid earth/scree → `02_muddy_with_stones` or `01_fine_scree`
   - slope rock → `01_weathered_grey_bedrock`
   - peak snow → `01_fresh_powder`
   Each material binds albedo + normal + roughness (ao optional). 5 is a soft cap (keeps sampler count sane).

2. **Binding (C#, ~30 lines, in/near `TerrainLab.cs`).** Load each chosen set's textures and push them as
   `sampler2D` uniforms to the shared ground material. Material choice + the height/slope thresholds + tiling
   scale are exposed as tunables (a small Surface tab section or CLI overrides) so the look is dialled in
   motion. No manifest, no array build, no library system.

3. **Placement (shader — both the `use_chunk` and `use_analytic` branches of `ground.gdshader`).** Reuse the
   existing `v_h` (height) and `v_normal` (slope). Per fragment, compute a weight per material from a height
   band and a slope band, with **per-pixel soft transitions** (`smoothstep`, widened by `fwidth` so the
   boundary is screen-resolution soft) → no low-res field, **no grid to snap to** by construction.

4. **Sampling (shader).** World-XZ UV tiling at a per-material scale. **Mipmaps + anisotropy verified**
   (assert the "fuzz = no mipmaps" gotcha on import). **Triplanar** on steep faces (slope > threshold) so
   cliffs don't stretch. Sample the **real normal map** and combine with the geometric `v_normal` — this is
   what makes slope/depth read 3D (the legibility lever).

5. **Blend.** Blend the contributing materials by their normalized placement **weights** (NOT real-height —
   that's the full arc's Unit 4; no height array here), with the `fwidth`-soft boundary, → one
   `{albedo, normal, roughness}` fed to the existing BRDF/lighting. Keep it to the few materials whose weight
   is non-negligible (cheap).

**Data flow:** `assets/materials → C# bind → ground.gdshader uniforms`; per fragment
`v_h, v_normal → weights → tri/normal sample → blend → ALBEDO/NORMAL/ROUGHNESS → existing light()`.

## Perf posture

Fragment-side. ~5 materials × (albedo+normal+roughness) fetches, weight-gated so near-zero-weight materials
are skipped; triplanar only on steep fragments. Profile `--profmove` against the 8 ms budget; the terrain
geometry cost is the separate (done) scale arc. If fetches are too many, drop to top-2-by-weight blend.

## Eye-gate (definition of done)

Behind the existing analytic/chunk path (default can flip once it reads better than the ramp). Fly
close/mid/far in motion: **landforms read — valleys/ridges/slopes legible as 3D shape; materials placed
sensibly by height/slope; no grid facets; no mip fuzz; triplanar clean on cliffs.** User's live eye is the
only gate (never judged from a downscaled still). On PASS: **STOP — do not push toward AAA. Move to erosion.**

## Relationship to existing docs

- **Subordinate to** `2026-06-21-ground-material-system-reset-design.md` (the full arc, paused). This slice
  is a legibility stop-gap; the full arc supersedes it later (build-alongside-then-flip, per that spec's
  build sequence). The placement/blend logic here informs the full arc's Unit 2/4.
- Bones untouched: `field_height.glsl` / `field_math.gdshaderinc` and the CDLOD geometry are not touched
  (skin-not-bones). `--fieldcheck` must stay 0 m.

## Self-review notes

- **Scope:** one small slice, single plan. Full arc explicitly OUT.
- **Placeholders:** none — materials, binding, placement, sampling, blend, gate all specified; starter
  material pick named (final dialled live).
- **Consistency:** "no throwaway infra" honored — disposable surface = fixed sampler bindings only; blend
  logic + library usage are kept work. No baked splat / no array system (those are the paused arc).
- **Ambiguity resolved:** "enough" = readable landforms (slope/depth legible) in motion, NOT AAA; ~5 fixed
  `sampler2D` materials (not arrays); placement = per-pixel height/slope bands with `fwidth`-soft boundaries
  (not a baked field); edit the CURRENT `ground.gdshader` (not a parallel shader). Erosion follows on PASS.
