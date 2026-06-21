# WG16 S2 — CDLOD Quadtree + Geomorph (pop-free continuous LOD) Design

Date: 2026-06-21. Status: SPEC (brainstormed; design decisions made to pillars + AAA + long-term-best
per the user's standing directive for this arc). Parent: `2026-06-21-infinite-terrain-cdlod-design.md`
(§2 chunk contract, §3 quadtree, §4 pop-free mechanisms — this spec is the *implementation* of that
technique). Follows: S1 (analytic field render, commits 121b583…57b5983) + S1.5 (cheaper normal, 170063c).
Precedes: S3 (streaming → infinite).

## Why this arc (and the number it must fix)
After S1+S1.5 the live analytic field renders the **full 2048² no-LOD mesh at ~25 ms in motion** — over
the 8 ms budget. The audit's verdict: the field is *affordable* once vertex count is sane; the quadtree is
what makes it sane. S2 is that quadtree — **and it is the graveyard arc.** WG1–15 died at terrain LOD on
two failure modes the user named explicitly: **heightmap pops** (vertices snapping to new heights at LOD
changes) and **quality pops** (surface detail jumping). The non-negotiable, per the post-mortem: **pop-free
continuous LOD, proven in motion by the user's eye, before any streaming infra.**

## The design decisions (all reasoned to pillars + AAA + long-term-best)

### 1. Mesh & spatial structure — recursive quadtree + ONE shared grid mesh, instanced
- **Quadtree over stable world-XZ.** Root = a large world square covering the current 8 km region.
  Each frame, walk from the root: if a node is too far from the camera relative to its size, keep it as one
  chunk; if close enough, split into 4 and recurse; stop at max depth (finest LOD). Output = leaf chunks,
  large-far / small-near, at roughly constant screen-space triangle density.
  - *Why quadtree, not concentric rings:* rings ARE the clipmap layout the graveyard memory explicitly
    forbids ("never unilaterally add a clipmap; moving rings were the cursed WG1-15 path"). The quadtree
    gives **stable world-XZ tiles** — data-pinnable for erosion/water/biomes (the parent spec's chunk
    contract), and the reason the parent spec chose quadtree over clipmap. Long-term-best + the guardrail
    both point here; the "rings are simpler" argument is convenience, which the pillars rank below
    correctness.
- **One shared grid mesh, instanced.** Every chunk at every LOD is the SAME flat `N×N` PlaneMesh resource
  (grid resolution a tunable, e.g. 32 or 64). A chunk renders that one mesh placed at its world-XZ with
  scale = chunk size; the **vertex shader displaces** each grid vertex from the live `field_height` at its
  world position (the S1 analytic path + the S1.5 cheap normal). All terrain variety = field displacement;
  the grid is flat and identical everywhere.
  - *Why:* AAA-standard CDLOD geometry (Strugar). Thousands of instances share ONE mesh → tiny memory, and
    "stream a chunk" = "add an instance at a transform" — the foundation S3 needs, async-trivially. Cleanest
    geomorph (uniform grid op, no per-chunk mesh data). The alternative (per-chunk generated meshes) adds
    mesh-gen + memory + streaming cost for CPU-side data the analytic render doesn't need — rejected on
    performance + long-term-best.
- **Scene integration.** The current single `TerrainLab` `MeshInstance3D` becomes a **manager**: it owns the
  quadtree, and each frame spawns/updates child `MeshInstance3D`s (one per visible chunk) sharing the grid
  mesh + the ground material, each with a transform + per-instance uniforms (chunk world origin, chunk
  scale, LOD level, morph params). Reads the existing `Camera` sibling (`/root/TerrainLabRoot/Camera`) for
  LOD selection. Preserves the `_giProxy` and the cloud-shadow / `cam_world` passthroughs the sky lane uses.

### 2. Geomorph — the heightmap-pop killer
- Per vertex, a continuous `morphK ∈ [0,1]` = the camera-distance fraction within this chunk's LOD band
  (0 at the band's near edge, →1 at the far edge where it's about to coarsen).
- As `morphK → 1`, each odd-indexed grid vertex slides its XZ toward its even-indexed (coarser-grid)
  neighbor's position; height is then sampled by `field_height` at the **morphed** XZ. By the moment the
  chunk actually switches LOD, its fine vertices have already continuously moved onto the coarse grid
  positions — the switch changes nothing visible. C0-continuous.
- **Why this is structurally pop-free (not tuned):** the morphed vertex samples the SAME analytic
  `field_height` the coarser chunk will sample. There is no second heightmap to disagree with — the fine
  surface morphs onto the EXACT height the coarse surface shows. The historical heightmap pop **cannot
  occur** here. (This is the payoff of the analytic render path from S1: it makes the graveyard's #1 killer
  impossible by construction.)
- **Detail cross-fade** (the quality-pop killer): all distance-varying surface detail blends over a band on
  ONE shared, named distance curve (so there's a single soft transition zone, not stacked rings). Today the
  placeholder has no such detail, so this is a **reserved, named seam** — implemented as the single curve
  from day one so future surfacing (textures, parallax) rides it instead of inventing a competing one.

### 3. Seams — crack-free
- **≤1-level neighbor constraint**, enforced during the quadtree walk: adjacent leaf chunks differ by at
  most one LOD level. This bounds every seam to a single-level bridge.
- **Edge-stitch (primary):** because the mismatch is always one level, the finer edge has exactly one extra
  vertex between each pair of coarse vertices; snap that vertex onto the average of its two coarse neighbors
  → the fine edge becomes collinear with the coarse edge. No gap.
- **Skirts (backstop):** each chunk drops a short vertical skirt around its border — invisible from above,
  plugs any sub-pixel hairline (e.g. mid-morph). Stitch is the real fix; skirt is insurance. AAA-standard.

## Staged build — each stage gated before the next (never big-bang; build behind a toggle)

A toggle keeps the current single-mesh path runnable for A/B until S2 passes its gates (like `--analytic`).

- **S2a — quadtree structure + LOD-select + cull + edge-stitch, fixed region.**
  Build: the quadtree walk, per-chunk instancing of the shared grid, distance LOD selection with hysteresis,
  ≤1-level neighbor constraint, per-tile frustum culling, edge-stitch.
  **Gate = MECHANICAL (not look):** a `--cdlodcheck`-style readback asserts the ≤1-level neighbor invariant
  holds, frustum culling drops off-screen chunks, and `--profmove` shows the vertex-count perf drop (toward
  budget). **Pops are TEMPORARILY EXPECTED here and are NOT eye-gated** — geomorph lands in S2b. (This stage
  proves the *skeleton* the historically-fatal pop work will sit on; isolating it is the anti-WG1-15
  discipline at fine grain.)
- **S2b — geomorph + detail cross-fade seam. THE GRAVEYARD GATE.**
  Build: per-vertex `morphK`, XZ morph toward the coarse grid, height at morphed XZ; the single named
  detail-distance curve.
  **Gate = the user's live eye, IN MOTION: ZERO elevation pops, ZERO quality pops, ZERO cracks** flying low
  and fast across LOD bands. Numeric backstop: assert `morphK` is C0-continuous across band boundaries. This
  is the arc's definition of done — one visible pop and we do NOT proceed; we fix the morph.
- **S2c — proxy-shadow win (audit Finding 2, ~21%), done CAREFULLY.**
  Build: route the PSSM shadow cast to the coarse `_giProxy` (or a cheap shadow tier) instead of the full
  detail mesh, so the heavy analytic field stops running ×~4 in the shadow cascades.
  **⚠ CAREFUL — it brushes the DONE, parallel-edited sky/light lane. Hard guardrails:**
  - Touches ONLY terrain geometry flags (`CastShadow`/`GIMode` on the terrain meshes + `_giProxy`) and a
    per-instance material override to pin the shadow-caster to a cheap height source. Does **NOT** modify the
    `Sun`, `LightingState`, `ComposeLighting`, PSSM config, the cloud-shadow map, god-rays, or anything the
    sky lane owns. The sun keeps casting exactly as it does; only *which terrain geometry* feeds the shadow
    pass changes.
  - **Its own eye-gate:** the user confirms shadows still look right (shape/quality/contact unchanged vs.
    before) AND nothing in the sky/light system shifted. If shadows look different → revert S2c (it's
    isolated); keep S2a/S2b.
  - **Concurrency:** stage only `TerrainLab`-side files; if the sky lane has touched anything
    shadow-adjacent, STOP and check with the user before proceeding.
  - *Note:* the big perf fix is the vertex-count drop (S2a/S2b) — the shadow pass gets cheaper for free as
    vertex count falls. S2c is an additional ~21% on top, not load-bearing for budget; hence it's last,
    behind the pop-free gate, and revertible.

## Verification (NO TDD — GPU/visual; gate viewport = `scenes/terrain_lab.tscn`)
- **Build clean** → `dotnet build … -clp:ErrorsOnly` → `0 Error(s)`.
- **`--fieldcheck` stays PASS** throughout (the field math is never touched — S2 only changes meshing/morph,
  never `field_math.gdshaderinc`). Skin not bones.
- **S2a mechanical:** a CLI readback (`--cdlodcheck`) prints PASS/FAIL for the ≤1-level neighbor invariant +
  `morphK` continuity (once S2b lands) + reports culled-chunk count; `--profmove` records the vertex-count
  perf drop vs. the 25 ms no-LOD baseline.
- **S2b eye-gate:** the user flies `terrain_lab.tscn` across LOD bands in motion — ZERO pops/cracks. Never
  claimed from a downscaled still (`ground-texture-feedback`).
- **A LOD-visualization overlay** (`--lodviz`: color chunks by level / wireframe) is built in S2a so the
  user can SEE the bands and watch the morph — without it, "is that a pop?" is unanswerable.
- **Run-invocation rule** applies (user `--flags` after a bare `--`; `--auto-shot` needs an OS path) — see
  [[wg16-cli-run-invocation]].
- **ONE Godot at a time**; the scene cannot run `--headless` (compute NullRefs); run windowed.

## Definition of done for S2
S2a mechanical gate (invariant + cull + perf drop) → S2b eye-gate (ZERO pops in motion, the graveyard
killer) → S2c (proxy-shadow, shadows-unchanged eye-gate). The current single-mesh path stays behind a toggle
until all gates pass. Then S3 (streaming → infinite) builds on the quadtree + shared-grid foundation.

## NOT in scope (YAGNI / deferred)
- Streaming / camera-following root / async gen / floating origin → S3 + S4 (own arcs). S2's quadtree is
  rooted over the FIXED 8 km region.
- Surfacing (textures/parallax replacing the height-color placeholder) → its own later arc; S2 only reserves
  the named detail-distance curve it will ride.
- Per-chunk baked height DATA (erosion/water/collision) → built when those arcs need it (the chunk
  contract's lazy data slot); S2 renders purely analytic.
- Any change to the field math or the sky/light system.
