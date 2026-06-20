# GM2 — Real per-material height maps — Design

Date: 2026-06-20. GROUND roadmap (`specs/2026-06-20-ground-roadmap-to-aaa-design.md`) phase **GM2**.
Takes ground from "painted" to "real 3D surface": **real height data sharpens the (approved)
heightblend interlock and revives POM relief** — the #2 AAA lever after the palette, and core
"masks/shaders/blending" work (height drives the blend boundary + parallax).

> **Status: DESIGN — awaiting user review.** ⚠ Unlike GM1/GM3-A (additive, free, toggled), **GM2
> changes a look that is already APPROVED** (the compositing-core interlock) and revives POM — so it
> **must be eye-gated**; it should not ship default-on blind. Build is the natural next *eye-gated*
> session.

## Problem

The single height seam `material_height_uv()` (`terrain_lab.gdshader:402`) currently returns
`1 - roughness` (the `height_from_rough` hack) — "too flat" (POM was deferred for exactly this;
NEEDS_REVIEW 0). It feeds BOTH `interlock_blend` (the approved material boundary) and `pom_offset`
(relief). **The 108-material library has no height maps** (`copy_materials.py` copies only
albedo/normal/rough/ao; verified the source folders have no height/displacement). So GM2 must
*produce* height. (Note: a separate uncurated ComfyUI experiment generated ~1.3k AI `*_height*`
PNGs under `D:/assets/animators/…` — NOT a clean source; ignore for now.)

## Goal

A genuine per-material mesoscale height field for each active role material, sampled at the one
seam, so: (1) the interlock boundary is driven by real relief (rock genuinely protrudes through
sand in crevices), and (2) POM reads real surface depth → can come off the shelf. Behind toggles,
eye-gated, defaulting to the current approved look until approved.

## Approaches

**Approach 0 — source height maps (checked, ABSENT).** Would've been ideal (copy + bind). The
curated library has none. Skip.

**Approach A — better proxy, NO new bake (cheap interim).** Replace `1 - roughness` with a
**combined proxy**: AO (mesoscale cavities — already bound as `z*_ao`) as the low/mid-frequency
base, plus a high-frequency term from roughness, e.g. `h ≈ mix(ao, 1-rough, k)` tuned per use.
AO genuinely encodes occlusion/cavities (a far better height proxy than inverted roughness on
rocky materials). **Buildable now, zero new infra, verifiable**, but it's still a proxy (AO≠height;
flat-but-relieved surfaces read flat) and it **changes the approved interlock → eye-gate required.**
Good cheap win for the *blend boundary*; **not enough for true POM depth.**

**Approach B — derive REAL height from the normal map (RECOMMENDED, AAA).** The normal map is the
derivative of height, so integrating it recovers a real mesoscale height field. Bake **once at
load** for the active role materials (7, or 14 with GM3 variants — small set), write `z*_hgt`
textures, sample them in `material_height_uv`. Method (no FFT in Godot): a **GPU-compute Poisson
solve** — `∇²h = ∂x(nx/nz) + ∂y(ny/nz)`, solved by Jacobi/multigrid relaxation (N iterations on a
local RenderingDevice, like `SplatCompute`/`CloudNoiseCompute`), normalized to [0,1]. Real height →
**genuine interlock relief AND real POM depth.** This is the AAA-correct answer.

**Approach C — full displacement (tessellation/mesh).** True geometric displacement needs
tessellation (Godot lacks it) or the CDLOD mesh (GM6). Out of scope; POM (B) is the stand-in until then.

**Recommendation:** build **B** (real derived height — the thing POM was waiting for). Optionally
land **A** first as a one-line interim to improve the interlock cheaply while B is built. Both are
eye-gated (they touch the approved blend).

## Design (Approach B)

**Bake (`HeightCompute.cs` + `shaders/height_from_normal.glsl`, mirroring `SplatCompute`):**
- Input: a role material's normal map (CPU-load its `normal.png` → upload, or sample the already-
  loaded `z*_nrm`). Output: an R8/R16 height texture per material.
- Compute: decode normal → slope `(nx/nz, ny/nz)`; build the divergence; **Jacobi-relax** the
  Poisson equation for K iterations (tileable: wrap edges, since materials tile); normalize to [0,1];
  optional high-pass to keep it mesoscale (remove low-freq drift that would bias the interlock).
- Run **once at load** for the ≤14 active materials (cost: a few small dispatches; not per-frame).
- Driver loads result into new `uniform sampler2D z*_hgt` (repeat_enable, mipmaps).

**Fragment seam (one function):**
```glsl
uniform bool height_from_maps = false;     // GM2 master (default off until eye-gate)
uniform sampler2D z0_hgt; … z6_hgt;         // baked real height (R)
float material_height_uv(sampler2D rgh_t, sampler2D alb_t, sampler2D hgt_t, vec2 uv, vec2 dx, vec2 dy){
    if (height_from_maps) return clamp(textureGrad(hgt_t, uv, dx, dy).r, 0.0, 1.0);
    if (height_from_rough) return clamp(1.0 - textureGrad(rgh_t, uv, dx, dy).r, 0.0, 1.0);
    return clamp(dot(textureGrad(alb_t, uv, dx, dy).rgb, vec3(0.299,0.587,0.114)), 0.0, 1.0);
}
```
`height_by`/`material_height`/`pom_offset` thread the `z*_hgt` sampler through (mechanical). The
interlock and POM then consume real height with **no other change** — `height_from_maps` off
reproduces today exactly (safe default).

**Sampler budget:** +7 (`z*_hgt`, single-channel) on top of ~28. Check against Godot's limit; if
tight, GM2 motivates the **`sampler2DArray`** move (shared with GM3-B/C and GM6) — flag, don't
block.

## Perf
Bake: one-time at load (small). Runtime: POM already exists (LOD-gated, off by default); the
interlock gains one `textureGrad` per role on the boundary (negligible — replaces the roughness
fetch). Profile `--profmove`; the cost is POM-on (already characterized), not the height source.

## The gate (user, live)
- **Interlock:** with `height_from_maps` on, does the rock-in-crevices boundary read as real relief
  vs the current softer derived blend? A/B `height_from_maps`. No new seams/artifacts.
- **POM:** flip `pom_on` — does relief now actually read (the thing that was "too flat")? Tune
  `pom_scale_m`/steps. **This is POM's revival gate** — if it reads, POM comes off the shelf.
- Default both off until approved (protects the shipped look).

## Decision points for review
1. **B (real derived height) now**, or **A (AO proxy) first** as a cheap interim? (Rec: B; A optional one-liner.)
2. **Normal-integration solver:** Jacobi (simple, more iters) vs multigrid (faster, more code). Rec: start Jacobi, profile.
3. **Sampler budget:** add 7 `z*_hgt` samplers, or take the `sampler2DArray` refactor now (helps GM3-B/C + GM6 too)?

## Self-review
- **Scope:** single phase (real height at the one seam + bake); displacement/tessellation (C) and
  the array refactor explicitly deferred.
- **Placeholders:** none; the seam, bake approach, and integration are concrete and verified against
  `material_height_uv`/`pom_offset`/`interlock_blend`.
- **Consistency:** honors the roadmap GM2 ("real per-material height maps; flat→deep"); the bake
  mirrors the proven `SplatCompute`/`CloudNoiseCompute` local-RD pattern; default-off protects the approved look.
- **Honesty:** Approach A is a proxy (won't give true POM depth); B is the real fix but bigger and
  eye-gated because it alters the approved interlock.
