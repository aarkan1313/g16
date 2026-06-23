# Erosion-Free Infinite Water — Sea + Significant Lakes + Thin-Channel Rivers — Design

Date: 2026-06-23. Status: DESIGN (brainstormed, user-approved section-by-section). Lane: terrain shape + water.
Supersedes the erosion-as-terrain-author approach (the pipe-model AND the structure-first drainage carve both
hit the graveyard STOP — the eye-gate stayed "bad" after ~8 attempts). Reuses the refactored, tile-coherent
hydrology infra (`scripts/hydrology/*`) and the master spec's substrate contract.

## The reframe (why this is different from everything that failed)

**Water becomes a RENDERING LAYER, not a terrain-shaping process. We drop erosion entirely.** Every painful part
of this session came from erosion needing to modify terrain SHAPE coherently across the whole landscape (wide
valley carves, seams, terracing, polish). That requirement is gone. The terrain is the existing base field —
which never drew complaints; it read smooth and natural in every shot — left UNTOUCHED, with one tiny exception
(a thin channel groove under river lines, via the reserved carve-delta slot, never the base field generator).

This is how much AAA open-world water actually works: terrain is authored independently; water is a separate
layer that finds the low spots. The hard, repeatedly-failed problem ("make carved valleys look natural
everywhere") is replaced by a tractable, well-solved one ("render believable water on the terrain we have").

## Pillars + the user's hard constraint

- **AAA + best-long-term.** The water must look AAA (reflections, depth, motion, foam), not placeholder.
- **Graveyard discipline.** Each tier proven great before the next; base-field bones untouched (`--fieldcheck`
  0m); eye-gate the REAL shader in motion; STOP a tier if it can't be made great after fair effort. Risk surface
  is far smaller than erosion (sea = a constant; rivers = a thin local groove, not landscape-wide carving).
- **LIMITED water bodies (user's explicit concern).** An infinite world must NOT be pockmarked with ponds —
  most games have few, deliberate water bodies. Lake (and river) quantity is a FIRST-CLASS, CAPPED, tunable
  budget, not an emergent flood. (Recall: a naive fill flagged 2602 lake cells in one region — unacceptable.)
- **Modular + tunable.** Three independently-toggleable tiers; every shape + look value a uniform/param.
- **Additive / cannot break CDLOD.** Water is a sibling layer that reads the substrate; it never modifies the
  working terrain chunk geometry or calls CDLOD internals.

## The three water tiers (toggleable, increasing difficulty)

### Tier 1 — Sea level (free, bulletproof, infinite-trivial)
A global `sea_level` constant. Water exists wherever terrain dips below it → coastlines, seas, islands, the bulk
of the world's water. No drainage graph needed; works even where the graph isn't built (robust fallback). One
shared surface following the camera — zero coupling to terrain chunks.

### Tier 2 — Significant lakes (the user's "limited amounts", controlled)
Basins from the deterministic depression-fill, but passed through a SIGNIFICANCE FILTER + DENSITY CAP so only
deliberate water bodies survive:
- **min surface area** (kills tiny-pocket speckle), AND
- **min spill depth** (kills shallow puddles), AND
- **min inflow drainage area** (a lake should be fed), THEN
- **rank by significance, cap to `lake_density_per_km2`** (keep only the top N per area).
Tunable from "rare big lakes" → "more ponds". Deterministic + tile-coherent: the SAME basins qualify at the SAME
level regardless of chunking → no seams, no popping across chunk boundaries.

### Tier 3 — Significant rivers (thin local channel, not landscape erosion)
Keep the EXISTING tile-coherent drainage graph (MFD routing + Chaikin-smoothed centerlines — these were never
the problem; gates verified clean dendritic networks). Gate to channels above a drainage-area threshold (no
rivulet herringbone). For each significant reach, stamp a NARROW channel groove (a few cells wide, short smooth
blend) under the centerline ONLY — the surrounding terrain stays the pristine base field. Rivers drain into the
sea/lake levels. This is ~20x smaller risk surface than the wide-valley carve that failed: thin isolated ribbons
touch ~2-5% of cells with almost nothing to seam against.

## Architecture / data flow

```
base field (+halo, per chunk) ──► DrainageGraph (HAVE IT — tile-coherent, determinism gate 0.985)
                                        │  basins, LakeDepth, Area, smoothed river centerlines
                                        ▼
                                  WaterBodies (CPU, deterministic): significance filter + density cap
                                  │ ranked significant LAKES (basin, level, bounds) + significant RIVER reaches
                         ┌──────────────┴───────────────┐
                         ▼                               ▼
                 ChannelCarve (GPU)               WaterSurface (mesh + AAA shader)
                 thin groove under river          sea plane @ sea_level + lake surfaces + river strips;
                 centerlines → carve-delta        depth/transparency from terrain-height vs water-level
                         │                               │
                         └──────► per chunk: terrain (base + groove delta) + ADDITIVE water sibling mesh
```
Tier 1 (sea) needs no drainage — pure constant, always available.

## Components (units, each one job + clear interface)

- **`WaterParams.cs` (new)** — all knobs: `SeaLevel`; lake `MinArea`/`MinDepth`/`MinInflow`/`DensityPerKm2`;
  river `MinArea`, channel `Width`/`Depth`/`BlendDist`; + shader look params. std430 where GPU-bound. Tunable.
- **`WaterBodies.cs` (new, pure C#, deterministic)** — drainage graph + WaterParams → `{ List<Lake> lakes (id,
  surfaceLevel, bounds, significance), List<RiverReach> rivers (centerline pts, width, flowDir) }`. Owns the
  significance filter + density cap (the "limited amounts" logic, applied once, tile-coherently).
- **`ChannelCarve.cs` (GPU, rework of ValleyCarve scope)** — significant river reaches + base height → base
  height + thin channel-delta under centerlines only. NOT wide valleys.
- **`WaterSurface.cs` + `shaders/water_surface.gdshader` (rework of WaterRenderer + shader)** — sea/lake/river
  surfaces + the AAA shader (below). One shader, modular uniforms, used by all three.
- **`WaterChunk.cs` (new, for streaming)** — subscribes to terrain chunk stream-in/out; builds/frees the
  additive water sibling mesh per chunk. NEVER calls CDLOD internals. Sea is a separate global camera-follow plane.
- **Reuses:** `HydrologyPipeline` (produces the substrate the WaterBodies consumes), `DrainageGraph`,
  `CoarseField`, `HydrologyChecks` (extended with the new gates).

## The AAA water shader (one shader, all four qualities, modular uniforms)

- **Reflections** — screen-space reflections of sky + shoreline (planar fallback for the flat sea). Toggle uniform.
- **Depth & transparency** — terrain-height vs water-surface → clear-shallow→opaque-deep color ramp + refraction
  offset. Reads as volume, not a sheet.
- **Surface motion** — scrolling normal maps; rivers scroll ALONG flow direction (from drainage), sea = layered
  swell, ripples near shore. Tunable speed/scale.
- **Shoreline & foam** — soft depth-fade at the waterline, foam band at water-land contact, wet-darkening on
  adjacent terrain. Kills the mesh-intersection look.

## Testing — lens-first, budget-gated (the repeatedly-bitten part is first-class)

1. **The lens is in the plan from day one:** lab renders the REAL water shader + sky + low close-up camera. NEVER
   eye-gate water through a debug colour-ramp again. Every milestone ends with a real-render eye-gate in motion.
2. **Mechanical gates (numeric, before eye-gate):**
   - **Lake-budget gate** — significant-lake count within `DensityPerKm2` across a large region (proves "limited
     amounts" numerically; must be far below the old 2602-cell flood).
   - **Coherence gate** — extend `--determinismcheck`: lakes/rivers/levels agree across a chunk overlap (no seams).
   - **`--fieldcheck` 0m** — base field untouched (groove is delta-only).
   - **Finite/no-NaN water levels.**
3. **Toggle-staged eye-gates (de-risk integration):** sea-only (AAA on existing terrain) → +lakes (count looks
   deliberate, not pocky) → +rivers. Sign off each before the next.
4. **STOP discipline:** if a tier's LOOK can't reach AAA after fair effort, stop that tier + surface it; sea+lakes
   are low-risk, rivers are a tiny thin-carve.

## NOT doing (boundaries)

- NOT erosion / landscape-wide carving (the failed approach — explicitly dropped).
- NOT modifying the base-field generator or CDLOD terrain geometry (water is additive; groove via reserved delta).
- NOT a global live fluid sim (that was Arc 3 of the old roadmap; out of scope here — this is static-water render).
- NOT filling every basin (significance filter + cap is the whole point).
- NOT a new terrain LOD/streaming system (water subscribes to the existing one).

## Self-review notes

- **Scope:** one implementation plan's worth — WaterParams + WaterBodies + ChannelCarve + WaterSurface/shader +
  WaterChunk, reusing the hydrology infra. Three tiers staged sea→lakes→rivers (also the de-risk order).
- **Placeholders:** none vague — significance filter has concrete criteria (area/depth/inflow + per-km² cap);
  channel carve is "thin groove under centerline" with width/depth/blend params; shader lists the 4 concrete
  qualities; gates are concrete (budget count, coherence, fieldcheck).
- **Consistency:** "water is an additive layer over untouched terrain" honored everywhere; "limited amounts"
  enforced in WaterBodies + the budget gate; tile-coherence reused from the existing determinism guarantee;
  bones-untouched repeated in pillars + components + tests + boundaries.
- **Ambiguity resolved:** water = sea level + significant lakes + thin-channel rivers (not erosion); infinite =
  per-chunk additive on the proven tile-coherent drainage + a global sea plane; lakes = significance-filtered +
  density-capped (user's explicit concern); rivers = thin local groove on the existing good centerlines, gated by
  drainage area; AAA = all 4 shader qualities, modular/tunable; build order sea→lakes→rivers, eye-gated per tier.
