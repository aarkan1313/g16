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

## Current Status (updated 2026-06-22)

> **⚠ STAGE NAMING SUPERSEDED.** The newer, user-approved spec `2026-06-21-infinite-terrain-cdlod-design.md`
> renames the stages **S1 (perf go/no-go) → S2 (quadtree+geomorph+stitch on a finite region) → S3 (streaming
> infinite) → S4 (floating-origin)** and corrects the old "T1 = standalone fixed-region gate" premise (a baked
> region has no LOD, so it can't pop → that gate was payoff-free; folded into S2). **The old T1/T2/T3 framing
> below is retained for history but is NOT the live map.** Live map = S1✅ S2✅ S3🔨(spec+plan, not built) S4(folded into S3).

### ✅ Complete
- **S1 — analytic-field perf go/no-go.** Measured: live `field_height` per-vertex = 34 ms full-mesh (4.3× over),
  but S2's LOD vertex-cut brings it to **~6.4 ms static** → the go/no-go is effectively PASSED (the quadtree
  closed the gap). Render is live-analytic. (Details: infinite-terrain spec §10.)
- **S1 quadtree skeleton + S2a chunks render.** Quadtree hierarchy, ≤1-level invariant (`--cdlodcheck`), pooled
  mesh chunks, per-chunk tight AABB, baseline crack prevention. Commits e3595e7…fb981e6.
- **S2b — per-vertex geomorph (POP-FREE).** Commits 25098d8…dd07e45. Each vertex morphs its grid XZ toward the
  coarse grid sampling the SAME `field_height` → elevation pop structurally impossible. **The pop bug was a
  morphK sign-inversion** (cc9638a fixed it). Proven by `--morphcheck` (far-edge verts coincide with coarse
  grid to 0.0 m). Test harness `TerrainTestPaths` (keys 5/6/7, `--testpath=N`). User eye-gate: PASS.
- **S2d — edge-stitched mesh variants (CRACK-FREE), skirt eliminated.** Commits 575939e…4804d98. 16 welded
  INDEXED variants (odd boundary verts collapse onto even → coarse spacing → no T-junction); skirt deleted.
  Proven by `--stitchcheck` (welded verts on the coarse neighbor's lattice to 0.0 m). Two bugs found+fixed in
  verification: vertex-soup→indexed (perf), inverted winding (the "see-through"). User eye-gate: soft-pass.
- **Shadow integration (cross-lane).** The sky lane's CSM shadow pass (d116612) is now in-build. Terrain-side:
  the per-chunk AABB margin was scaled to the geomorph displacement (96b88a9) to kill grid-aligned shadow
  ACNE. Shadow params (bias/penumbra/soft/dist) made lab-tunable + survive recompose (7fa6aab); review key 4
  cycles shadow presets (ad4a0f3); penumbra reverted to the physically-correct ~0.53° default (2990249). The
  residual penumbra/PCF stipple on coarse geometry is logged + deferred (fixed by surfacing, not shadow tuning;
  memory `cdlod-shadow-acne-vs-penumbra-stipple`).

**T1 GEOMETRY (pop-free + crack-free continuous LOD) is COMPLETE.**

### ✅ DONE — S3 (streaming infinite, folded floating-origin) — built + eye-gated 2026-06-22
- **Spec:** `docs/superpowers/specs/2026-06-22-s3-streaming-infinite-design.md` (194c1a8).
- **Plan:** `docs/superpowers/plans/2026-06-22-s3-streaming-infinite.md` (cb6a0b5) — 5 tasks + eye-gate.
- **Shape (built):** quadtree root roams with the camera (3×3 root-cell window) → infinite. Floating-origin
  FOLDED in (snapped camera-relative render space; subsumes the old "S4"). Per-chunk AABB born generous,
  tightened by an ASYNC GPU height-range on the render-thread RD (Task 5). Identity-keyed pool + churn budget.
- **Snap-pop ROOT-CAUSED + FIXED (commit 22408ed).** The in-flight "whole terrain reshapes every ~8 km" pop was
  the renderOrigin snap — but NOT via the field-XZ reconstruction the first root-cause claimed (that round-trips
  exactly; rebuilding wxz from a per-chunk true-origin uniform was byte-identical, a no-op). REAL cause:
  floating-origin was half-wired — chunks rendered at worldXZ−renderOrigin but the CAMERA stayed at true world,
  so it drew terrain offset by renderOrigin and that offset jumped 8192 m at each snap. Fix: co-locate the
  camera in the chunks' render frame (Process derives trueCam, ticks CDLOD, sets camN.Position = trueCam −
  newOrigin); every world-space consumer still gets the TRUE pos (sky lane unchanged). Shader + field untouched.
  Memory `cdlod-renderorigin-snap-pop`.
- **Two residual streaming flashes FIXED (19666ad + 5fbdd50):** (1) retire-hole — fast flight could hide a parent
  the frame its budget-deferred children were due → 1-2 frame hole; now a BOUNDED retire-grace (RetireGrace=2
  unseen frames) bridges it without leaking. (2) coarse-probe AABB undershoot — the 7×7 shadow-AABB probe could
  miss a peak on big far chunks → too-short AABB → cull; margin now padded by half the probe spacing.
- **Perf (5fbdd50):** the old "rebuild spike" + an interim retire-skip LEAK are gone. --profmove (terrain-only,
  sky/GI off): avg 21.2→14.6 ms, worst 149.5→22.2 ms, max active 3954→498 (≈ leaf count). No spike even on a
  12 km orbit crossing snaps; CDLOD worst-frame (22 ms) is now below the no-LOD full-mesh worst (38.9 ms).
- **Gates:** all 7 guards PASS (`--fieldcheck` 0 m / `--cdlodcheck` / `--morphcheck` / `--stitchcheck` /
  `--streamcheck` / `--popcheck` 0 m,0° / `--snapdiff`). Origin=0 pre/post byte-identical (no sky regression).
  USER eye-gate: flew to ~49 km out across ~17 snaps, popmeter worst Δh=0.00 m; no pop; no flash at normal speed.
- **The chunk is now the streamed, infinite, pop-free, crack-free unit the next arcs plug into.**

### ✅ Minimal surfacing slice — DONE 2026-06-22 (readable landforms, to judge erosion)
- Spec `specs/2026-06-22-surfacing-minimal-slice-design.md`, plan `plans/2026-06-22-surfacing-minimal-slice.md`.
- A deliberately small, mostly-shader pass (NOT the full surfacing arc): 5 real materials from
  `assets/materials/` (sand/grass/earth/rock/snow) placed PER-PIXEL by height+slope with `fwidth`-soft
  boundaries (no 4 m grid), triplanar on cliffs, real normal maps for the 3D read, blended into the existing
  lighting. Default-on (`use_textures`); `--textures=0` A/Bs back to the legacy colour ramp; thresholds +
  tiling + material roles are live tunables. Built in `ground.gdshader` + `TerrainLab.LoadGroundMaterials()`.
- **Gates:** all 7 guards PASS, `--fieldcheck` 0 m (bones untouched); `--profmove` textured == legacy ramp
  (4.6 ms avg — the blend is effectively free); user eye-gate: landforms read (light audit, OK to judge erosion).
- **Purpose:** make landforms legible so the NEXT arc (erosion) can be eye-judged. This slice is a stop-gap;
  the full texture-array surfacing arc (below) supersedes it later (build-alongside-then-flip).

### 🗺️ EROSION + HYDROLOGY — now ONE system (master roadmap `specs/2026-06-23-erosion-hydrology-master-design.md`)
Erosion and water are one **drainage substrate** (carved height + flow_accum + channel_mask + water_level +
sediment) consumed at two tiers: **static water** (always-on render) + **live water** (optional bounded GPU sim).
Three arcs, in order: **Arc 1 erosion+substrate** (← active; folds in E1 below, adds flow-accumulation so rivers
form) → **Arc 2 static water render** → **Arc 3 live water (toggle, last)**. Each its own spec→plan→build→eye-gate.

### 🔨 ARC 1 PHASE 1 — Erosion sim core (was "E1"): BUILT race-free, mechanically verified, NEEDS the river fix + eye-gate
- **Spec:** `specs/2026-06-22-erosion-e1-sim-core-design.md`. **Plan:** `plans/2026-06-22-erosion-e1-sim-core.md`.
- **Model (pillars choice):** pipe-model hydraulic erosion — ONE coupled GPU loop (water flux → velocity →
  stream-power incision + capacity sediment transport/deposition + thermal talus), fixing WG15's
  fighting-solver root cause by construction. Droplet erosion rejected (not ship-correct for streamed infinite).
- **Built (T1–T5):** `scenes/erosion_lab.tscn` + `scripts/erosion/{ErosionSim,ErosionParams,ErosionLab}.cs` +
  `shaders/erosion_sim.glsl`. Standalone lab seeded from `FieldCompute`, local-RD compute (windowed), own mesh.
  NOT wired into the live CDLOD terrain (E3/E4). Lab controls: space=run S=step R=reset D=debug-view (water/
  sediment/flow false-colour overlay added to `ground.gdshader`, default off). Built ship-correct (water/flow =
  the coarse drainage skeleton E2 will bake; no throwaway lab sim).
- **A real bug was found + fixed (NOT tuning):** the first run sawtoothed ("millions of cuts" at 10k steps) —
  root cause was a GPU READ-WRITE RACE (erode + thermal read neighbor h and wrote h[i] in the SAME parallel
  dispatch → grid-aligned oscillation) + non-conservative thermal. Restructured race-free (erode→dh delta;
  thermal = slump-flux + gather-apply, conservative; transport double-buffered) — the correct Mei structure.
  An audit then fixed a smaller phase-1 water race, dead thermal-cap code, and the lab's lit view (was
  use_textures=true with no textures bound → black albedo, which made the sawtooth read worse than it was; now
  the height/slope colour ramp). Memory `erosion-e1-pipemodel-race`.
- **Gate status:** `--erosioncheck` PASS (finite, bounded, **roughness 0.0045** vs the >0.02 sawtooth signature
  — mechanical proof the grid oscillation is gone). **USER EYE-GATE NOT YET DONE** (deferred — user couldn't
  test). The real "does erosion look great / valleys cut logically / converges" judgment is still OPEN; E1 is
  NOT signed off until that passes. **STOP criterion still in force** (graveyard arc; don't grind versions).
- **Base field untouched** (`--fieldcheck` 0 m); standalone so nothing existing can regress.
- **NEXT CONCRETE STEP (Arc 1 phase 1, the river fix):** the race-free sim erodes but the channel-forming
  feedback is WEAK — incision scales with LOCAL water depth/velocity, not ACCUMULATED upstream drainage area,
  so it gives diffuse hillslope erosion, not dendritic rivers. Add a **flow-accumulation pass** feeding a
  stream-power incision (E ∝ Aᵐ·Sⁿ) + lake/basin fill → `water_level` + `channel_mask`. THEN re-eye-gate.
  This is what makes the substrate the master spec's Arcs 2–3 consume. (Spec for Arc 1 to be written.)

### ⏸ Deferred / later
- **The async per-chunk DATA grid** (carvable height for erosion/water) — reserved-dormant; S3 builds only the
  AABB slice. Arc 1's bake phase wakes it (the drainage substrate's per-chunk delta).
- **Surfacing (Skyrim-look ground) — the FULL arc** — `specs/2026-06-21-ground-material-system-reset-design.md`
  (texture arrays, per-pixel placement engine, POM/relief). Consumes the substrate's sediment/material + wetness.
  Supersedes the minimal slice above; the residual shadow stipple resolves here. NOT next.
- **Erosion + hydrology — the rest of the master arc** (`specs/2026-06-23-erosion-hydrology-master-design.md`):
  Arc 1 phases 2–3 (bake the coarse substrate → per-chunk detail synthesis), **Arc 2 static water render**,
  **Arc 3 live water (toggle, last)**. Each its own spec→plan→build→eye-gate, in order.
- **Biomes, collision, flora, world-editing** — each its own later arc on the chunk contract (biomes consume
  the substrate's flow_accum as moisture; flora consumes flow_accum + sediment).
- **Old "S2c proxy shadows"** — largely subsumed (shadows are the sky lane's CSM now; terrain casts the LOD'd
  mesh). Not a separate live stage.
- **Chunk-rebuild frame spike** — FIXED in the S3 perf pass (5fbdd50); see the S3 entry above. No longer open.

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