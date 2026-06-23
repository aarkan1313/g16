# Hydrology: World-Space Rivers & Gated Lakes — Design

**Date:** 2026-06-23 · **Branch:** `experiment/presentation` · **Lane:** terrain water
**Status:** design (brainstormed, approved section 1; sections 2-6 recorded from approved decisions)

This is the RESTART after the water arc was trashed (see
`docs/handoffs/2026-06-23-WATER-TRASHED-rivers-restart.md`). It replaces erosion-as-author and
global-sea-level with a **network-first hydrology pipeline**: generate the river/lake network as
world-space data, bake it to a per-region texture, carve a thin groove, and render flowing water
meshes — erosion is an OPTIONAL Phase-4 polish, never the author.

---

## 1. Goal

Visible, flowing **real rivers** across the infinite CDLOD terrain, with **limited, gated lakes**, on
the UNMODIFIED base field. AAA look (depth, foam, flow, reflections), modular and tunable, infinite-safe.

## 2. Core principle

**Water is a world-space layer the terrain QUERIES; it never authors the terrain.** The base field
stays pure-analytic (`field_height(world_xz, seed, spacing, fp)` in `shaders/field_math.gdshaderinc`)
and is never modified — `--fieldcheck` stays 0 m throughout. Rivers and lakes are an additive, bounded
layer baked into a per-region texture that the GPU (carve + water shader) and CPU (mesh gen) read
identically.

## 3. Why this, not the trashed approaches

| Trashed approach | Why it failed | This design |
|---|---|---|
| Pipe-model erosion authors terrain | terracing, thread-rivers, expensive, needs whole-watershed | erosion is optional Phase-4 polish on a known channel |
| Structure-first landscape-wide carve | disconnected gouges, artifacts everywhere | carve is THIN, bounded to the SDF, profile-blended |
| Global sea level | user dislikes it; floods mountains | no sea; rivers + gated lakes only |
| D8 routing | grid-axis faceting (right-angle staircases) | **MFD** (multiple-flow-direction), not D8/steepest-descent |
| Per-chunk river generation | rivers stop at chunk boundaries | **world-space `hash(regionCell)`** + coarse halo trace |

## 4. Architecture & data flow

Everything is keyed per **8192 m region** (the existing CDLOD region/snap unit, `region_size_m`), built
once on demand from `hash(regionCellX, regionCellZ)` and cached. Fully deterministic → infinite-safe,
tile-coherent by construction.

```
  base field (analytic_h)                        water-table noise + biome
        |                                                  |
        v                                                  v
  (1) Coarse drainage solve  --------------------->  (2) Lake gating
      low-res grid over region + HALO                    (4 gates)
      MFD flow, Strahler order, trace-to-edge
        |                                                  |
        v                                                  v
  (3) River splines (world-space, smoothed)           lake polygons
        |                                                  |
        +------------------+--------------------------------+
                           v
        (4) Bake REGION WATER TEXTURE (RGBA):
            R = signed distance-to-river (SDF, metres)
            G = bed-depth target (metres below local terrain)
            B = flow direction angle / Strahler order (packed)
            A = wet mask (rivers + lakes), 0..1
                           |
            +--------------+---------------+
            v                              v
  (5) TERRAIN CARVE (GPU, ground.gdshader vertex)   (6) WATER MESHES (CPU->GPU)
      h = analytic_h(wxz)                              river ribbons (flow-mapped)
          - carve_depth * profile(R, G)                + lake surfaces, draped on
      (one texture tap per vertex)                     the carved bed; modular
                                                       water_surface.gdshader
```

**One source of truth:** the region water texture. Carve, ribbon meshes, foam, and the future biome
"distance-to-water" map all sample the SAME texture. No parallel systems to drift.

**Continuity is structural:** a river crossing a region boundary is the same coarse-grid path on both
sides (shared halo), so the SDF agrees at the seam by construction — no stitching, no per-chunk graph.

## 5. Components (new, under `scripts/hydrology/`)

Each unit is independently testable; the boundary is the region water texture.

| File | Responsibility | Depends on |
|---|---|---|
| `WaterParams.cs` | All knobs: gates, carve profile, water-table freq, render uniforms. Loaded from `data/water_params.json` with graceful key fallback (mirrors `FieldParams`). | — |
| `CoarseDrainage.cs` | Coarse low-res height grid over region+halo; priority-flood depression fill; **MFD** flow + flow accumulation; Strahler order. Pure C#, deterministic. | FieldParams (samples `field_height`) |
| `RiverTracer.cs` | Extract river polylines from accumulated flow (threshold by accum), Chaikin-smooth to splines, width from `sqrt(flow)*scale`. World-space. | CoarseDrainage |
| `WaterTable.cs` | New low-freq noise (`simplex(wx*f, wz*f)`) → moisture/water-table scalar; cheap biome class from (elevation, water-table). Shared by gating + future biome map. | FieldParams |
| `LakeGating.cs` | The 4-gate filter on basins from the fill: (a) water-table noise gate, (b) significance (min area+depth+inflow), (c) density cap per km², (d) biome gate. Emits lake polygons. Pure C#, deterministic, testable. | CoarseDrainage, WaterTable |
| `WaterTextureBaker.cs` | Rasterize splines + lake polygons → the RGBA region water texture (SDF + depth + flow + mask). | RiverTracer, LakeGating |
| `WorldWaterRegion.cs` | Owns ONE region end-to-end: runs the above, holds the cached texture + spline/lake lists, exposes `SurfaceAt`/`BedAt`/`IsWet`/`Texture`. The producer/facade. | all above |
| `RiverRibbonMesh.cs` | Spline → flow-mapped ribbon `ArrayMesh` (width from flow, flow-aligned UVs, draped onto carved bed via `BedAt`). | WorldWaterRegion |
| `LakeMesh.cs` | Lake polygon → triangulated flat surface at the lake water level. | WorldWaterRegion |
| `WaterRenderer.cs` | Lifecycle: spawn/despawn ribbon + lake meshes as CDLOD regions enter/leave; additive sibling Node3Ds (never touch terrain geom). Render-relative to `CdlodRenderOrigin`. | RiverRibbonMesh, LakeMesh |
| `HydrologyChecks.cs` | CLI self-checks (determinism, continuity, budget, fieldcheck-still-0). | all |

**Shader edits:**
- `shaders/ground.gdshader` — vertex gains the carve: sample region water texture at `wxz`, subtract
  `carve_depth * profile(R, G)`. ONE tap + a smoothstep profile. Guarded by a `water_carve_on` uniform
  (default off until a region texture is bound) so the base field is untouched when water is disabled.
- `shaders/water_surface.gdshader` — NEW, modular: depth tint (shallow→deep), flow-scrolled normals
  (flow dir from the mesh UVs), shoreline foam (from SDF distance), fresnel; SSR reflections as a
  later tunable. Used by both ribbons and lakes.

## 6. The carve profile (continuity-critical)

The single lever that made past carves look like "disconnected gouges" was subtracting depth from a
bumpy base. Here the carve is **toward a bed-depth target with a smooth compact-support profile**:

```
// in ground.gdshader vertex(), after h0 = analytic_h(wxz):
vec4 w = texture(water_tex, region_uv(wxz));   // R=dist(m), G=bedDepth(m), A=wetMask
float t = clamp(w.r / carve_width, 0.0, 1.0);   // 0 at centre .. 1 at bank
float profile = (1.0 - t*t);                    // smooth U/V cross-section, 0 at bank
profile *= profile;                             // Wyvill-ish compact support (C1 at edge)
h0 -= w.g * profile * w.a;                       // carve only where wet; depth from bake
```

`carve_width`, `carve_depth` scale (multiplies baked G), and the profile exponent are all
`WaterParams` knobs. Bed-depth target G is computed in the bake as a **monotonic-descending** profile
along each river (downstream never rises), so the channel is a continuous descending trench, not a
bumpy subtract.

## 7. Lake gating (all four gates active, AND-combined)

A basin becomes a lake only if ALL hold:
1. **Water-table gate** — `WaterTable.At(center) > table_threshold` (dry regions get few/none).
2. **Significance** — area ≥ `min_area_m2` AND depth ≥ `min_depth_m` AND inflow ≥ `min_inflow`.
3. **Density cap** — accept in descending-significance order until `lakes_per_km2 ≤ cap`; reject rest.
4. **Biome gate** — `biomeClass(elevation, water-table)` ∈ allowed set (wet-lowland yes; desert/peak no).

All thresholds are `WaterParams` knobs. This is "limited lakes by construction," tunable per the
"certain spaces / frequency / biomes" intent.

## 8. Scope — Milestone 1 (what we eye-gate first)

**Rivers + thin carve + gated lakes.** Specifically:
- Coarse MFD drainage + splines + region water texture bake (continuity-safe).
- Terrain carve (thin groove, profile-blended).
- River ribbon meshes + lake meshes with the modular water shader (depth/foam/flow now; SSR later).
- All four lake gates.

**Deferred (later milestones, NOT this build):** wetlands/marshes (Phase 2 extra), seasonal flow
(Phase 3), GPU hydraulic erosion polish / deltas / meanders (Phase 4), biome-driven flora/fauna
consumers of the distance-to-water map (separate lane).

## 9. Constraints & budgets

- **Base field untouched:** `--fieldcheck` maxAbsDiff = 0 m at every step. The carve is display-time
  subtraction in the vertex shader, never written back to the field.
- **GPU budget:** water system ≤ ~1-2 ms/frame on top of terrain+sky (one SDF tap in the terrain
  vertex shader; lightweight ribbon/lake shader; SSR is the tunable that can be dropped first).
- **Infinite-safe:** all per-region, `hash(regionCell)`-deterministic, cached; no global bake.
- **Determinism / continuity gates:** a river crossing a region seam is byte-identical on both sides
  (shared coarse+halo); `HydrologyChecks` proves it (`--watercontinuitycheck`), plus a determinism
  gate (same seed+cell → same texture) and a budget gate (lakes/km² ≤ cap).
- **CDLOD lane safety:** water meshes are additive siblings on the region lifecycle, render-relative to
  `CdlodRenderOrigin`; they never touch terrain geometry, the quadtree, or the base field.
- **Tile/region size:** 8192 m, matching `region_size_m` and the CDLOD snap unit.

## 10. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Carve looks like gouges again | bed-depth target is monotonic-descending + compact-support profile; eye-gate the DRY groove before adding water |
| Coarse grid too coarse → blocky rivers | Chaikin-smooth splines; SDF is continuous (hardware-filtered texture), independent of grid res |
| Region seam mismatch | shared coarse+halo solve → identical path both sides; `--watercontinuitycheck` is the gate |
| "Too much water" again | 4 AND-combined lake gates + density cap; rivers thresholded by flow-accum (only significant channels) |
| Perf | one tap in terrain vertex; ribbon meshes only for visible regions; SSR is droppable |
| Eye-gate fails | STOP-and-rebrainstorm after ONE rejection (pillars discipline); do not grind versions |

## 11. Eye-gate plan (the lens discipline)

Judge ONLY through the real render, in motion, in the actual CDLOD world, from normal play viewpoints —
never a hand-rolled viz or single still. Gate order: (1) dry carved channels look like natural valleys;
(2) river ribbons read as flowing water; (3) lakes are limited and sit naturally; (4) full look in
motion. One rejection at any gate → STOP and re-brainstorm that piece, don't iterate 8×.
