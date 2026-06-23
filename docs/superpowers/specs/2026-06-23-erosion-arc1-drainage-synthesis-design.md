# Erosion Arc 1 (reworked) — Drainage-Network Synthesis + Analytic Valley Carving — Design

Date: 2026-06-23. Status: DESIGN (brainstormed, user-approved section-by-section). Lane: terrain shape + water.
Supersedes the **producer** of Arc 1 in `2026-06-23-erosion-hydrology-master-design.md` — the drainage SUBSTRATE
CONTRACT (height, flow_accum, channel_mask, water_level, sediment) is UNCHANGED; only HOW Arc 1 produces it
changes. Supersedes the river-forming approach of `2026-06-23-erosion-arc1-substrate-design.md` (the pipe-model
flow-accumulation + stream-power path).

## Why this rework (the pipe-model verdict)

The race-free pipe-model hydraulic sim (E1 → Arc-1 v1) was built, made mechanically sound, and given real
flow-accumulation + stream-power incision. The LIVE eye-gate (user, ~23k steps, real `ground.gdshader` render)
rejected it: **terraced/stair-stepped surface + thin thread-rivers on top of unsmoothed terrain — not a playable
landscape, not nice rivers.** Root analysis: the per-cell pipe-model is a CONVERGENCE-TO-EQUILIBRIUM physical
sim — excellent at MICRO erosion detail (talus, gully texture), structurally poor at MACRO playable structure.
Terracing is inherent to discrete per-cell erode/deposit at scale; thread-rivers come from carving where the
thin water film currently is, not shaping a broad valley AROUND a river. Heavy smoothing to remove terracing
destroys the rivers. This is a wrong-tool conclusion, not a tuning gap → per the graveyard discipline, STOP and
change approach (decided with the user).

(Method note from v1, carried as a hard rule: NEVER eye-gate from a hand-rolled visualization. v1 nearly STOPped
on a "vertical combing" artifact that a numeric anisotropy probe proved was in the HILLSHADE DUMP, not the data.
Judge the DATA numerically + the user judges the REAL shader render.)

## The new idea: structure first, physics (optionally) for detail

Don't simulate water and hope rivers emerge. **Generate the river network as an explicit deterministic
structure, then build smooth valleys analytically AROUND it.** Production terrain tools get clean rivers this
way (structure → carve; physics only for fine detail). This fixes BOTH rejected symptoms by construction:
terracing is impossible (smooth analytic carve), and rivers sit at the floor of broad valleys the carve created
(not threads on top). The substrate contract is produced directly from the structure.

## Pillars + cross-cutting requirements (user-reaffirmed)

- **AAA / best-long-term, not a patch.** Structure-first is the correct model for playable macro hydrology.
- **Graveyard discipline.** Prove great before trusting it; bones untouched; eye-gate in motion; STOP if
  structure-first can't make great playable rivers after fair effort.
- **Modular.** Each unit swappable/disableable. `carve_strength = 0` ⇒ base field returned byte-untouched.
  The pipe-model is RETIRED from the Arc-1 path but KEPT on disk as an optional later fine-detail pass.
- **Separation of concerns.** `DrainageGraph` knows nothing about GPU/rendering. `ValleyCarve` knows nothing
  about how the graph was built. The substrate contract is the ONLY coupling between stages and to other arcs.
- **Tunable.** All shape knobs live in `HydrologyParams`, exposed live in the lab. Nothing hard-coded.

## World scope (user decision: full infinite/streaming now, made tractable by determinism)

Build INFINITE-NATIVE with cross-chunk continuity from the start — but continuity is achieved by CONSTRUCTION,
not iteration: the network is a **pure deterministic function of world position + base field**, computed per
chunk over a **halo**. Neighboring chunks compute the SAME coarse graph over their overlapping halo and agree on
every river crossing their shared edge — no messaging, no global state, no seams. Streaming "just works" the
moment a single region works, because a region is one tile's worth of the same function. The region eye-gate and
the streaming integration are therefore the SAME code; the only added machinery for streaming is wiring the
chunk system to call it per chunk (the S3 reserved carve-delta slot).

## Architecture — two stages

```
base field (coarse, chunk + HALO) ──► DrainageGraph (CPU, deterministic, tile-coherent)
                                            │  river-segment graph: nodes(pos, elevation, Strahler order,
                                            │  upstream-area), edges = segments
                                            ▼
base field (full-res chunk)        ──► ValleyCarve (GPU compute)
                                            │
                                            ▼
   carved height + flow_accum + channel_mask + water_level + sediment   (the SUBSTRATE — contract unchanged)
                                            │
                                            ▼
              terrain display  +  (Arc 2) static water  +  collision  +  (later) biomes/surfacing
```

### Stage 1 — `DrainageGraph` (CDG): deterministic coarse river network

Pure deterministic `f(region origin+extent+halo, base field) → segment graph`. No RNG state, no neighbor
messaging, no global mutable state. Pipeline:
1. **Coarse sample** the base field over chunk + halo at ~km-scale node spacing (low res → everything here is
   cheap, unlike the per-cell sim).
2. **Deterministic depression-fill** at coarse res so flow always reaches the region edge — NO pit
   fragmentation (the thing that diffused v1's network). Cheap at low res (priority-flood or iterative fill).
   Endorheic basins that genuinely don't drain become LAKES (feed `water_level`), not broken flow.
3. **Steepest-descent routing** on the filled coarse field → trunk rivers in the base field's natural valleys
   (base-field COHERENCE — the hybrid's "seeded from your world" half).
4. **Headward tributary growth** — recursively branch tributaries toward un-drained uplands by deterministic
   branching rules (the hybrid's "procedural refinement" half — controllable density/branching).
5. **Horton-Strahler ordering** + upstream drainage area per segment → drives valley width/depth and
   flow_accum.

Output: a compact river-segment graph. Internals invisible to downstream — consumers see only the graph.

### Stage 2 — `ValleyCarve` (GPU compute, per chunk): smooth valleys + substrate

Input: the CDG segment graph (uploaded as a buffer) + full-res base-field height for the chunk +
`HydrologyParams`. Per high-res cell:
- **`carved_h = base_h − Σ_segments influence(d_i, order_i)`** where `d_i` = distance to segment i and
  `influence` is a SMOOTH, monotonic falloff (deep at the river line, easing to zero at valley-width distance),
  depth & width scaling by Strahler order × `carve_strength`. Smooth analytic function of a continuous distance
  field ⇒ **C¹-smooth everywhere ⇒ terracing impossible by construction.** Broad deep valleys around high-order
  trunks, gentle around small streams.
- **Monotonic-descent enforcement** along each trunk: the carved river floor never rises downstream (no uphill
  water).
- **Substrate derivation (own-cell writes, no race):** `flow_accum` from CDG upstream-area along the nearest
  segment, falling off away from channels; `channel_mask` from segment order where carved; `water_level` from
  the depression-fill surface (lakes) + river-surface elevation along trunks; `sediment` seeded along banks
  (low-order placeholder for later surfacing).

Swappable/disableable: `carve_strength = 0` returns the base field untouched (modularity guarantee).

### Tunable knobs — `HydrologyParams` (std430, lab-exposed live)

`carve_strength` (global valley depth multiplier), per-order width & depth scaling, tributary density, channel
threshold (min order/area to be a channel), lake-fill aggressiveness, halo width. One struct, packed like
`ErosionParams`; every value live-tunable in the lab.

### `HydrologyLab` (evolves `erosion_lab.tscn`)

The eye-gate harness: build the CDG for a large region, run `ValleyCarve`, display carved terrain + debug
overlays (river graph, flow_accum, channel_mask, water_level) + live knob tuning. Reuses the existing lab
scaffolding (mesh, ground shader display, debug-field overlay, CLI overrides, the `--fieldcheck`/anisotropy/
valley-ridge diagnostics built in v1).

## Edge cases & failure modes

- **Endorheic basins (no outlet):** depression-fill → lake (`water_level`), not broken flow. A lake is a
  feature.
- **Flat regions (no clear descent):** steepest-descent degenerates → fall back to a tiny deterministic tilt +
  tributary growth so flats get sparse drainage, never NaN/stall.
- **Halo too small (segment longer than halo):** would cause a cross-chunk seam. Halo sized ≥ max segment
  reach; the determinism test catches violations.
- **River exits then re-enters a chunk:** handled — CDG is computed over the halo, not the bare chunk.
- **Carve pushing height below a downstream river (uphill water):** monotonic-descent enforcement along trunks.

## Testing — gates, not unit-TDD (GPU/visual project)

1. **Determinism test (the key invariant, mechanical):** compute `DrainageGraph` for one region at two chunk
   offsets → IDENTICAL graph in the overlap. Automated, must pass. This is what makes streaming free.
2. **`--fieldcheck`** stays `maxAbsDiff = 0m` (bones untouched; carve is an additive delta, base field math
   never edited).
3. **Anti-terracing probe (mechanical, from v1's lesson):** carved-height 2nd-difference stays low AND isotropic
   (z/x ≈ 1) — guards against any terracing/striping regression numerically, not by eye.
4. **Drainage sanity (valley/ridge discriminator):** high flow_accum correlates with LOW carved height (rivers
   in valleys, not on ridges).
5. **USER eye-gate (the real gate):** large region, REAL shader render, in motion — smooth playable valleys,
   natural dendritic rivers draining logically, lakes in basins, tunable carve. The pipe-model's failure was
   only caught here; this gate is decisive.

**STOP criterion:** if structure-first carving can't produce great-looking playable rivers after fair effort,
STOP and surface — graveyard discipline, no grinding versions.

## What this keeps / retires / doesn't touch

- **Keeps:** the substrate contract (unchanged); the master spec's arc order (Arc 2 static water / Arc 3 live
  water consume the same substrate, unaffected); the lab scaffolding + diagnostics built in v1; the S3 chunk
  contract's reserved carve-delta slot (where `ValleyCarve` output lands).
- **Retires from the Arc-1 path (keeps on disk):** `shaders/erosion_sim.glsl` + the pipe-model. Available later
  as an OPTIONAL fine-detail erosion pass layered on the already-good macro terrain (what it's actually good
  at), behind a toggle, NOT in the Arc-1 critical path.
- **Does NOT touch:** base-field generation math (`field_math.gdshaderinc` / `field_height.glsl` — bones); the
  chunk/streaming system internals (Arc 1 plugs into the reserved slot); Arc 2/3.

## Self-review notes

- **Scope:** single implementation plan's worth — two stages (CDG, ValleyCarve) + params + lab, all in the
  erosion/hydrology files. Infinite/streaming is folded in via determinism (same code as region), not a
  separate subsystem to architect.
- **Placeholders:** none vague — depression-fill is "priority-flood or iterative fill at coarse res" (concrete,
  bounded); `sediment` is an explicit low-order placeholder feeding later surfacing (bounded, named).
- **Consistency:** substrate contract honored end-to-end; modular/SoC/tunable/pillars written as explicit
  constraints; determinism invariant stated once and tested once; bones-untouched repeated in design + tests +
  boundaries.
- **Ambiguity resolved:** "different approach" = retire pipe-model as producer, structure-first instead (not
  patch the sim); "infinite from the start" = infinite-native by determinism, region-first validation is the
  SAME code; "hybrid" source = steepest-descent trunks (base-field coherence) + procedural headward tributaries
  (control); carve = smooth distance-to-river analytic profile (anti-terracing by construction); carve strength
  = tunable+modular (`carve_strength`, 0 ⇒ untouched).
