# WG16 Terrain LOD — Roadmap & Design (CDLOD)

> **⚠ SUPERSEDED (framing) by `2026-06-21-infinite-terrain-cdlod-design.md`.** That spec keeps this doc's
> CDLOD *mechanism* (quadtree + per-vertex geomorph + edge-stitch + ≤1-level neighbor, clipmap rejected) but
> corrects the staging: the "T1 = standalone pop-free LOD on the fixed region" gate here is folded into its
> **S2** (a baked region has no LOD, so it can't pop → a standalone fixed-region gate was payoff-free). LIVE
> STAGES: S1 (perf go/no-go) → S2 (quadtree+geomorph+stitch, DONE) → S3 (streaming infinite, spec+plan written
> 2026-06-22) → S4 (floating-origin, folded into S3). Read this doc for the mechanism/post-mortem rationale;
> read the 2026-06-21 spec + `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md` for the live status/plan.

Date: 2026-06-18. Status: SPEC (planned arc — not yet scheduled for build). Brainstormed with the user.
Companion: `docs/performance.md` (the floor measurement), `docs/ROADMAP.md` (where this arc slots in),
memory `terrain-clipmap-killed-wg1-15` (the post-mortem this spec is built around).

## Why this exists

The terrain is a **single 2048² PlaneMesh = ~4.19M verts / 8.4M tris, NO LOD** (`TerrainLab.cs:40-45`),
processed at full cost every frame regardless of distance AND drawn again ×4 in the sun's PSSM shadow
cascades. That is the ~3.8 ms perf "floor" measured in the 2026-06-18 perf pass — the single biggest
non-cloud cost, and it only grows as the world fills in. LOD is the lever that has never been pulled.

It is also a **keystone**: WG16's vision is infinite/streaming (erosion E4 "true-infinite, needs chunks
/streaming WG16 doesn't have yet"; world-editing height-delta; flora LOD/impostors), and every world
system is per-region world-XZ (splat bake, breakup masks, erosion per-chunk skeleton). A terrain
chunk/LOD foundation is what those arcs are waiting on.

## The post-mortem (the design anchor)

WG16 is the 16th attempt; **every WG1–15 fell apart at the terrain clipmap.** The user's post-mortem:
the killer was **elevation pops + quality pops** — as the camera moved and a region dropped to a
coarser LOD, vertices SNAPPED to new heights (elevation pop) and surface detail quality JUMPED
(quality pop). **The failure was missing *continuous* LOD, NOT the topology.** Therefore:

> **The non-negotiable requirement of this entire roadmap is POP-FREE CONTINUOUS LOD, proven in
> motion before anything else is built on top.** Topology choice is secondary to nailing this.

Two mechanisms deliver it:
1. **Geomorphing** — each vertex smoothly morphs its sample position/height between its LOD and the
   next-coarser LOD by a continuous morph factor (the camera-distance fraction within the LOD band),
   so elevation transitions are C0-continuous — never a snap. Natural fit: WG16 already displaces in
   the vertex shader from the procedural heightfield (`height_at` in `terrain_lab.gdshader`), so the
   morph is "sample height at the morphed XZ" — no new data, just a lerp of the sample grid.
2. **Detail cross-fade** — surface quality must blend across distance, never jump. WG16 ALREADY has
   this seam: `distanceWeight` / `ar_far_m` (the anti-repetition distance fade, ground Unit 1). Extend
   it so ALL detail (anti-repetition now; future parallax/normal/breakup) cross-fades on the same
   distance curve → no quality pop.

## Approach decision — CDLOD (chunked quadtree + geomorphing)

Three feasible topologies, weighed against the failure modes + WG16's procedural heightfield + the
per-region world systems + the future streaming need:

| | Pop-free? | Integration w/ WG16 world systems | Godot 4 feasibility | Verdict |
|---|---|---|---|---|
| **Clipmap + geomorph** (Losasso-Hoppe) | yes (with geomorph) | POOR — moving rings fight stable world-XZ data (edit/erosion/collision/scatter) | good | the cursed WG1-15 path; rings are the wrong unit here |
| **CDLOD: quadtree + geomorph** | **yes — its design goal** | **GOOD — stable world-XZ tiles map 1:1 to splat/breakup/erosion bakes, edit deltas, collision, scatter** | good | **CHOSEN** |
| **GPU tessellation / mesh shaders** | yes-ish (tess-factor popping to tame) | neutral | POOR — Godot 4 tess/mesh-shader support immature | not now |

**Chosen: CDLOD.** It is the AAA-standard for heightfield terrain and exists specifically to kill LOD
pops (continuous per-vertex morphing). Stable world-XZ tiles are the keystone the per-region systems
need. The only thing "above" it is Nanite-class virtualized micro-geometry, which Godot 4 lacks — so
CDLOD is the best *feasible* AAA choice. (Clipmap is rejected not because clipmaps are wrong, but
because moving rings are the wrong unit for WG16's stable-world-data systems — and the word is cursed.)

## Architecture (when built)

- **Quadtree over world XZ.** Recursive tiles; each frame select a per-tile LOD by camera distance,
  neighbors constrained to ≤1 level apart (the morph + stitch only has to bridge one level).
- **Vertex-shader geomorph.** Per vertex, a continuous `morphK ∈ [0,1]` from the distance fraction
  within the LOD band; the vertex's grid position lerps toward the coarser-grid position, and height
  is sampled (`height_at`) at the morphed XZ → C0-continuous elevation, no snap. (The procedural
  heightfield guarantees the same continuous height function at every LOD — no resampling mismatch.)
- **Crack prevention** between adjacent LOD tiles: edge-vertex stitching (snap shared edge to the
  coarser neighbor) or skirts. Geomorph + ≤1-level constraint keeps this simple.
- **Detail cross-fade** on the shared `distanceWeight` curve (no quality pop) — Unit 1 already does
  this for anti-repetition; later units (parallax, breakup) ride the same seam.
- **Shadow pass uses the LOD'd mesh** → the ×4 PSSM redraw drops with the vertex count, for free.
- **Per-tile frustum culling** (one mesh can't be culled; tiles can).

## Staged build — each stage EYE-GATED for ZERO pops in motion before the next

- **T1 — pop-free continuous LOD on the CURRENT fixed 8 km region. THE GATE.** Prove geomorph +
  detail cross-fade read with NO elevation/quality pops in motion. Fixes the 3.8 ms floor AND the
  shadow redraw. No streaming, no infinite — just the technique, proven. (Mirrors the erosion arc's
  "prove great on one region FIRST" pattern; this is the anti-WG1-15 discipline — do not build infra
  before the core technique is proven pop-free.)
- **T2 — stable world tiles.** Chunk the region into world-XZ tiles (the unit streaming/editing/
  erosion need): per-tile LOD select + edge stitch + frustum culling. Re-point the per-region bakes
  (splat/breakup) to per-tile.
- **T3 — streaming.** Load/unload tiles around the camera → true-infinite. The foundation erosion E4,
  world-editing, and flora consume. Biggest infra; only after T1+T2 are solid.

## Integration (what later arcs plug into)
- Tiles are world-XZ units → splat bake, breakup masks (Unit 4), erosion per-chunk skeleton (E3),
  world-editing height-delta, collision (near tiles only), flora scatter — all become per-tile.
- World-editing edits a tile's height-delta → invalidate that tile's splat/breakup/scatter → re-bake
  just that tile (already the ROADMAP's stated integration contract, now scoped to tiles).

## Risks / failure-mode guards (the WG1-15 ghosts)
- **Pops (THE killer)** — geomorph must be airtight; the T1 gate is "no elevation/quality pop in
  motion," judged by the user's eye (never a still). Numeric backstop: morphK is C0-continuous across
  the band; assert no discontinuity at LOD boundaries.
- **Cracks at LOD seams** — ≤1-level neighbor constraint + edge stitch/skirt.
- **Building infra before the core** — the exact WG1-15 trap. T1 (technique, fixed region) MUST be
  eye-approved pop-free before T2/T3 (tiles/streaming) start.
- **Heightfield consistency** — morphed vertices sample the SAME procedural `height_at`, so there is
  no LOD-to-LOD height resampling mismatch (a stored-heightmap clipmap's classic pop source).

## Verification
- **In-motion eye-gate** (the gate): fly low + fast across LOD bands; ZERO elevation/quality pop.
- `--profile` before/after: the ~3.8 ms floor + shadow-pass cost drop with vertex count.
- Keep the existing anti-repetition `distanceWeight` cross-fade as the quality-LOD seam (don't regress
  the approved Unit 1 look).

## NOT in scope (YAGNI for now)
- GPU tessellation / mesh shaders (Godot immaturity), Nanite-class virtual geometry (unavailable),
  and streaming itself until T1+T2 prove out. No clipmap.
