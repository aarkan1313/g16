# Terrain surface anti-aliasing — kill the camera-fixed shading rings

Date: 2026-06-23
Status: approved (design)
Shader: `shaders/ground.gdshader` (textured branch, `use_textures`); CDLOD chunk + analytic paths share it.

## Problem

On flat / grazing-angle terrain, a pattern of concentric "rings" of dots sits at fixed camera distances and
reads as a screen-locked **overlay** (it does not slide with the terrain). It looks identical regardless of which
material is on the ground.

### Root cause — isolated empirically (live H-stepper, colored modes)

A `diag_mode` stepper rendered the textured branch with one channel at a time on a flat base:

| mode | content | rings? |
|------|---------|--------|
| 1 (red) | flat albedo, ROUGHNESS=1 (matte), geometric normal | **clean** |
| 3 (blue) | flat albedo, REAL roughness, geometric normal | **clean** |
| 2 (green) | REAL albedo, matte, geometric normal | **rings** |
| 4 (yellow) | flat albedo, matte, normal-map perturbation | **rings** |
| 0 | full (real albedo + roughness + geo normal) | rings |

**Two independent per-texel high-frequency channels alias:**

1. **Normal-map perturbation** (`apply_nrm`, mode 4): a tangent-space normal map bends the sun-lit normal per
   pixel; at distance/grazing those normal texels alias into a high-contrast lit↔shadowed moiré. Normal aliasing
   is intrinsically worse than albedo aliasing (a tiny normal wobble flips lit↔shadowed) and mips cannot fix it
   (averaging normal texels ≠ averaging the lighting response). The current `apply_nrm` is an explicit "cheap,
   tangent-free" hack with no variance/mip-aware roughness compensation — not AAA.
2. **Albedo speckle** (mode 2): high-contrast bright specks (e.g. `02_muddy_with_stones`, `03_coarse_sand`) tile
   every `mat_tiling` (12 m) and minify into a color moiré whose beat frequency is set by the pixel grid → fixed
   camera-distance bands.

Roughness (mode 3) and geometry/lighting (mode 1) are clean — NOT the cause.

Why earlier single-channel attempts only *reduced* it: each killed one channel while the other remained. The
artifact persists near as well as far, so a far-only distance fade is insufficient on its own.

## Fix — two AAA techniques, both in-shader, no asset reimport

### 1. Normal channel: proper tangent-space normals + in-shader Toksvig

- **Proper tangent basis.** Replace the tangent-free `apply_nrm` (which rotated the map's xy straight into world
  XZ) with a stable world-aligned tangent frame derived from the geometric normal `v_normal` (the mesh carries no
  tangents). For near-flat terrain a world-XZ-aligned basis is correct and stable across chunks; build
  `T = normalize(cross(vec3(0,0,1), N))`, `B = cross(N,T)`, and transform the sampled tangent-space normal into
  world space through `(T,B,N)`.
- **Toksvig roughness boost.** The mip-filtered normal shortens (`|n̄| < 1`) where sub-pixel normal variance is
  high. Toksvig: `gloss = 1 / (1 + k·(1/|n̄| − 1))`, and convert to a roughness FLOOR
  `rough = max(rough, sqrt(1 − gloss))` (or equivalent). Distant/variance-heavy pixels get rougher → specular
  flicker and the lit↔shadowed moiré collapse → rings gone. Near pixels keep full detail. `k` is a tunable
  uniform. `|n̄|` comes from the length of the *unnormalized* mip-filtered tangent normal (sample the normal map
  without forcing unit length, take `length(n.xyz*2-1)` mapped appropriately) — implementable from the existing
  normal-map sampler.
- **Perturbation roll-off.** Scale the perturbation strength by the shared `detail_fade(dist)` so far normals
  collapse to the smooth geometric normal regardless of Toksvig (belt-and-suspenders; cheap).

### 2. Albedo channel: distance detail-fade to mean/macro

- Blend each albedo sample toward its texture **mean** (`textureLod(tex, uv, BIG)` = top mip = average color)
  across the shared detail band. Tune the band to start close enough and finish before the moiré-resonance
  distance so the speckle **fully** dissolves (not just halves). Near terrain keeps full detail.
- Same treatment already wired for roughness samples (harmless — roughness wasn't a cause but fading it to mean
  far out is correct and free).

### 3. Shared detail band (single seam — no stacked rings)

- One `detail_fade(dist)` curve (the reserved seam already in the shader) drives BOTH the albedo fade and the
  normal/Toksvig roll-off, so all detail dissolves together on one band. `dist = distance(cam_world, wpos)` with
  `wpos` in true world space (already computed). Tunable near/far uniforms.

## Components / files

- `shaders/ground.gdshader` — textured `fragment()` branch:
  - new `apply_nrm_tangent()` (proper tangent-space) replacing `apply_nrm`,
  - Toksvig helper + roughness floor,
  - albedo/roughness `mix(sample, mean, dis)` fade (mostly already present),
  - shared `dis = detail_dissolve(dist)` gated by `detail_fade_on` (live G toggle).
  - new tunable uniforms: `toksvig_k`, reuse `detail_fade_near/far`, keep `detail_fade_on`.
- `scripts/lab/TerrainLabUI.Process.cs` — keep the live debug toggles already added: **G** (detail-fade A/B),
  **H** (colored diag-mode stepper) for eye-gating. No other C# changes.
- No texture asset reimport. The `mat*_nrm` / `mat*_rgh` bindings in `TerrainLab.LoadGroundMaterials` stay.

## Data flow

vertex: `v_normal` (heightfield gradient), `v_surf_xz`/`v_h` (true world). fragment: sample albedo/rough/normal
at `uv = v_surf_xz / mat_tiling` → blend bases by height/slope weights → Toksvig roughens by normal variance →
proper tangent normal → albedo+rough faded to mean by `dis` → `ALBEDO/ROUGHNESS/NORMAL`.

## Testing / eye-gate

- **Live H-stepper** (colored modes) stays in for channel-by-channel eye-gating: after the fix, modes 0/2/4 must
  be ring-free near→far; modes 1/3 already clean.
- **G toggle** A/Bs the whole detail-fade live.
- Final: fly near→far, rings gone at all distances; close-up retains texture/relief; perf within budget
  (extra cost is a few taps + cheap math, target <0.1 ms).
- **No regression:** vertex path untouched → `--morphcheck`, `--stitchcheck`, `--streamcheck`, `--fieldcheck`
  still PASS.

## Out of scope (YAGNI)

- Explicit per-pixel mip-LOD override from world distance (trilinear+aniso already on; the fade/Toksvig dominate
  — add only if a residual mip-band survives the fade).
- Offline texture reprocessing / Godot roughness-from-normal reimport (chosen the in-shader route).
- Wiring the unused `ao.png` or the larger material library — separate surfacing work.

## Debug-method note

Eye-gate was done LIVE via the colored H-stepper (one channel per mode), not from downscaled auto-shots — the
auto-shots and my reads drifted and produced a false "it's the normal map only" conclusion mid-investigation.
The colored live stepper is the trustworthy gate; keep it.
