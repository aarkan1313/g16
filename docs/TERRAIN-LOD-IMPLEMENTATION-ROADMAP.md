# WG16 Terrain LOD — Implementation Roadmap (T1, T2, T3)

**Date:** 2026-06-21  
**Status:** T1 in-progress (S2b stage). T2/T3 scoped but not yet started.  
**Scope:** This roadmap maps the three-stage terrain LOD build (T1–T3) from the post-mortem-first design spec
(`docs/superpowers/specs/2026-06-18-terrain-lod-roadmap-design.md`), the T1 geomorph design
(`docs/superpowers/specs/2026-06-21-s2b-geomorph-design.md`), and sequenced implementation.

---

## Overview: The WG1–15 Graveyard Problem

Every prior WG attempt (WG1–15) **died at terrain LOD** — specifically at **elevation pops** (vertices
snapping to new heights when a chunk coarsens) and **quality pops** (detail quality jumping at LOD transitions).
The clipmap topology was the recurring victim.

**WG16's post-mortem-first approach:** The failure was missing *continuous* LOD, NOT the topology choice.
**Solution: CDLOD (continuous quadtree + per-vertex geomorph).** A vertex morphs its sample position between
LODs by a continuous factor, so elevation transitions are C0-continuous — never a snap. Surface detail fades
on a shared distance curve — no quality jump.

**The non-negotiable rule:** Each T stage has a **live-eye gate** proving pop-free in motion **before the
next stage proceeds.** Infra-first-build-later is the exact WG1–15 trap; T1 proves the technique works on
the fixed region BEFORE T2's tiling infra or T3's streaming.

---

## Three Stages (Each with Eye-Gate)

### T1 — Pop-free continuous LOD on the fixed 8 km region (THE GATE)

**Proof:** geomorph + detail cross-fade work on the current 2048² fixed mesh region. No elevation/quality
pops in motion, by the user's eye flying the LOD transitions.

**Stages (S1–S2d):**
- **S1 — Quadtree skeleton** ✅ **COMPLETE (2026-06-21).**
  - Quadtree hierarchy, per-frame LOD select by camera distance.
  - ≤1-level neighbor constraint (limits morph + stitch complexity).
  - Mechanical per-chunk validity check (`--cdlodcheck`): invariant holds every frame.
  - Commits: e3595e7…fb981e6.

- **S2a — Chunks render** ✅ **COMPLETE (2026-06-21).**
  - Quadtree leaves → pooled `MeshInstance3D` chunks (position + scale per chunk).
  - Per-chunk custom AABB (tight vertical bounds) for shadow cascade.
  - Skirt (border vertex drop) → crack prevention baseline.
  - Perf: 5.6 ms avg, 4.4× under budget. Shadow redraw drops with vertex count.
  - Commits: e3595e7…fb981e6.

- **S2b — Geomorph + test harness** 🔨 **IN PROGRESS (2026-06-21).**
  - **S2b.1 — Per-vertex geomorph in the vertex shader.**
    - Each vertex computes its own `morphK ∈ [0,1]` from camera distance within the LOD band.
    - Vertex grid XZ lerps toward the coarser-grid position by `morphK`.
    - Height sampled at the **morphed** XZ (same `field_height` the coarser LOD samples).
    - **Why per-VERTEX not per-chunk:** shared edge vertices across chunk boundaries compute the same
      morphK from the same distance → morph continuous across seams (per-chunk would leave a pop at
      boundaries — the exact failure we're killing).
    - **Why structurally pop-free:** the morphed vertex samples the SAME analytic function, so there is
      no LOD-to-LOD height mismatch (stored-heightmap clipmaps' classic pop source).
    - Shader additions: `cam_world`, `grid_n`, `split_factor` uniforms; spacing-scaled normal epsilon.
    - Implementation plan: `docs/superpowers/plans/2026-06-21-s2b-geomorph.md` (geomorph math + wiring).

  - **S2b.2 — LOD-crossing test harness.**
    - `TerrainTestPaths` component: flies the camera along 3 scripted LOD-crossing paths.
    - **3 paths** (tunable JSON, `data/terrain_test_paths.json`):
      1. **Low-fast flythrough** — low altitude, high speed, horizontal across many LOD rings (worst-case
         pop stress, horizontal LOD-band crossings).
      2. **Vertical descend/ascend** — drop/rise through LOD bands over a fixed point (altitude-driven LOD).
      3. **Slow boundary-hover** — creep slowly at a single LOD boundary (isolate one transition).
    - **Two drive modes:**
      - **Keys (5/6/7):** start a path live in-scene, watch in motion, judge pops by eye (like the 1-9
        review presets).
      - **CLI (`--testpath=N`):** runs path N, prints per-path report (avg/worst/spike ms, chunk count
        range, ≤1-level invariant checked **along the path**), quits — repeatable regression testing.
    - **Measures:** avg/worst-frame/spike-over-budget ms per path; chunk count (min–max during flight);
      the ≤1-level invariant sampled at every frame (not just one static snapshot).
    - **Auto pop-detection:** DEFERRED. Reserves a per-frame hook; detection must be calibrated to a real
      pop the eye finds in the data AFTER the harness is built, not guessed now.
    - Implementation plan: `docs/superpowers/plans/2026-06-21-s2b-geomorph.md` (Task 2: test harness).

  - **S2b.3 — Reserved detail cross-fade curve.**
    - Defines the single `detail_fade(dist)` function + uniform (no quality detail exists yet in the
      placeholder to fade).
    - Future surfacing detail (textures, parallax, normal) will ride this seam → no quality pop.
    - YAGNI: trivial reservation, unused now.
    - Implementation plan: Task 3 (one commit).

  - **Eye-gate:** The user flies the 3 test paths in motion and confirms **ZERO elevation pops, ZERO
    cracks, ZERO quality pops** across LOD bands. Numeric backstop: `morphK` is C0-continuous across
    band boundaries (assert no discontinuity). That is T1's definition of done.

- **S2c — Proxy shadows** (fenced to terrain geometry flags only).
  - The ×4 PSSM shadow redraw drops with the LOD'd mesh vertex count.
  - Proxy setup deferred until S2b eye-gate passes (isolate geomorph testing).

- **S2d — Skirt/stitch finalization + invariant lock.**
  - Final crack-prevention hardening.
  - Committed atlas (single public T1 gate).

**Perf target:** ≤8 ms total (S2a is 5.6 ms; geomorph adds negligible vertex cost; S2c proxy saves ~0.5 ms).
**Field untouched:** `field_height` math stays intact (S2 is skin, not bones).

---

### T2 — Stable world tiles (after T1 eye-gate PASSES)

**Proof:** The fixed 8 km region chunks into world-XZ tiles, each with its own LOD select, edge stitch,
and frustum culling. The splat/breakup/erosion bakes are re-pointed to per-tile. No visible regression
from T1.

**Stages:**
- **T2.1 — World-XZ tile subdivision.**
  - Divide the 8 km × 8 km fixed region into N×N world-aligned tiles (e.g., 512 m each → 16×16 tiles).
  - Each tile owns its quadtree (independent LOD select by camera distance to that tile's center).
  - Tiles are the unit for streaming, baking, editing, collision, flora scatter.

- **T2.2 — Edge stitch + frustum culling.**
  - Shared edges between tiles stitch (or skirt). Per-tile frustum culling drops off-screen tiles.

- **T2.3 — Re-point per-region bakes to per-tile.**
  - Splat bake (material weights), breakup (anti-tiling masks), scatter providers → per-tile.
  - The splat/breakup atlas is re-subdivided per-tile or kept global + sampled per-tile.

- **T2.4 — Edit/delta integration.**
  - World-editing brush edits a tile's height delta; invalidates that tile's splat/breakup/scatter → re-bake.

**Eye-gate:** Same as T1 — fly the 3 test paths over the tile grid. ZERO pops, no visible regression.
**Perf:** Should be ~flat (tiling adds culling savings + per-tile quadtree overhead; net should wash).

---

### T3 — Streaming (after T2 eye-gate PASSES)

**Proof:** Tiles load/unload around the camera as it moves. Camera can roam the infinite heightfield.
Splat/breakup/scatter load with their tiles. No pops, no visible seams at tile boundaries.

**Stages:**
- **T3.1 — Tile load/unload.**
  - Stream manager loads tiles in a radius around the camera, unloads distant tiles.
  - Heightfield is infinite (field_height is pure world-space function; no resampling mismatch).
  - Async CPU→GPU upload (current FieldCompute is sync-blocking; needs async for streaming).

- **T3.2 — Floating-origin (optional, high-precision guard).**
  - World-XZ int-hashing for the procedural field can drift far from origin (float32 precision).
  - Floating-origin: shift the world around the camera so coordinates stay small → stable hash precision.
  - Implementation: world-relative camera + node transform re-centering. (Post-mortem feasibility TBD.)

- **T3.3 — Collision + physics.**
  - Per-tile collision mesh (convex hull or trimesh of the LOD'd geometry).
  - Load/unload with tile streaming.

- **T3.4 — Flora + editing integration.**
  - Flora scatter (grass, trees) loads with tiles; respects brush edits (height delta).
  - World-editing brush works on streamed tiles (invalidate + re-bake).

**Eye-gate:** Camera flies across the infinite world; no tile-boundary seams, no pops, no LOD discontinuities.
**Perf:** Streaming cost (IO + bake async) TBD after T2.

---

## Current Status (2026-06-21)

### ✅ Complete
- **S1 (quadtree skeleton):** commits e3595e7…fb981e6.
  - Quadtree hierarchy, ≤1-level neighbor constraint, mechanical per-frame select.
  - `--cdlodcheck` PASS every frame.
  - Build clean, no regressions.

- **S2a (chunks render):** commits e3595e7…fb981e6.
  - Quadtree leaves → pooled mesh chunks (position + scale per chunk).
  - Per-chunk custom AABB (tight vertical for shadow).
  - Skirt crack prevention.
  - **Perf: 5.6 ms avg, 4.4× under budget** (was 3.8 ms floor for the no-LOD 4.19M vert mesh; S2a is 5.6 ms
    with shadow redraw also dropping → net is a speedup once S2c proxy lands).
  - Mechanics gate: PASS.

### 🔨 In Progress
- **S2b.1 — Per-vertex geomorph:** spec written (2026-06-21, commit 548e206); implementation plan drafted.
  - Ready to execute (geomorph math, wiring, shader + C# changes scoped).
  - Next: write the plan, execute tasks (geomorph → test harness → reserved curve), eye-gate.

- **S2b.2 — Test harness:** spec written; plan drafted.
  - Next: implement `TerrainTestPaths` + JSON + keys + CLI.

- **S2b.3 — Reserved detail-fade curve:** spec written; trivial, tagged as tail.

- **S2b eye-gate:** After Tasks 1-2 complete, user flies the 3 paths in motion, confirms ZERO pops.

### ⏸ Blocked (on T1 eye-gate)
- **S2c — Proxy shadows:** fenced to terrain geom only, deferred until S2b passes.
- **S2d — Skirt/stitch finalization + atlas lock:** tail of T1.
- **T2 (tiles):** cannot start until T1 eye-gate passes.
- **T3 (streaming):** cannot start until T2 eye-gate passes.

---

## Integration with Other Arcs (Phase A/B/C)

### Phase A (finishing the two lanes)
- **Ground / Texture:** Independent of T1–T3. G-0 gate, then G-1 compositing (both Roadmap §Ground).
  Once Phase A is done, T1 can be the foundation T2/T3 build on.
- **Sun & Light:** Independent of T1–T3.

### Phase B (making it a world)
- **T1–T3 (terrain LOD):** Spine. Everything else depends on it.
  - **Biomes:** select palettes by climate field (runs on T2 stable tiles).
  - **Macro procedural variety:** per-tile content generation (biome-aware).
  - **Erosion at scale:** E2 bake → E3 per-chunk detail → E4 global (all consume T2 tiles).
  - **Flora:** scatter per-tile, respects tile streaming + edits (T3 dependency).
  - **World-editing:** brush edits a tile, re-bakes splat/breakup/scatter (T2+ dependency).

### Phase C (climate & elements)
- **Visible water:** renders on top of the LOD'd terrain (T1+).
- **Precipitation + wetness:** Weather axis, ground Unit 6 response (T1+).
- **Snow-on-ground:** aspect/temperature placement, sits on the terrain (T1+).

---

## Key Design Principles (from the post-mortem)

1. **Per-VERTEX geomorph, not per-chunk.** Seam continuity across chunk boundaries. WG1–15 lesson.
2. **Sample the SAME field at the morphed XZ.** No LOD-to-LOD height mismatch. Structurally pop-free.
3. **Eye-gate each T stage before the next proceeds.** Technique-first, infra-second. Anti-WG1–15 discipline.
4. **Stable world-XZ tiles.** NOT moving rings (clipmap). Plays well with per-region systems (bakes, edits, collision, scatter).
5. **Floating-origin deferred.** Can do T1–T3 without it; revisit if precision drifts.

---

## Next Actions (2026-06-21)

1. **S2b implementation plan reviewed + approved by user** → move to execution.
2. **Execute S2b.1 (geomorph shader + wiring)** → commit → verify (`--fieldcheck` PASS, perf under budget).
3. **Execute S2b.2 (test harness)** → commit → drive the 3 paths.
4. **S2b eye-gate:** user flies the 3 paths, confirms ZERO pops in motion.
5. **If PASS:** proceed to S2c/S2d (tail of T1), then T2.
6. **If FAIL:** diagnose the morph, iterate S2b.1 until pop-free.

---

## References

- **Spec (T1 design):** `docs/superpowers/specs/2026-06-18-terrain-lod-roadmap-design.md`
- **Spec (S2b geomorph):** `docs/superpowers/specs/2026-06-21-s2b-geomorph-design.md`
- **Plan (S2b implementation):** `docs/superpowers/plans/2026-06-21-s2b-geomorph.md`
- **Quadtree skeleton (S1):** commits e3595e7…fb981e6
- **Chunks render (S2a):** commits e3595e7…fb981e6
- **Memory post-mortem:** `C:\Users\josep\.claude\projects\c--Wg16\memory\terrain-clipmap-killed-wg1-15.md`
- **Audit findings:** `docs/AUDIT-2026-06-21.md` § Terrain LOD