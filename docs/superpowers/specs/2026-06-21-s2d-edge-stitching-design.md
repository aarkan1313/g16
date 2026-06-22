# WG16 S2d — Crack-free LOD seams via edge-stitched mesh variants (eliminate the skirt)

Date: 2026-06-21. Status: SPEC (brainstormed; decisions to pillars + AAA + long-term-best per the user's
standing directive for this arc — "AAA / pillars"). Parent: T1 (pop-free continuous LOD on the fixed 8 km
region). Follows: S2b (per-vertex geomorph — pop fixed + `--morphcheck` proven; commits 25098d8…dd07e45).
Precedes: S2c (proxy shadows — PAUSED while the sky/light lane does a shadows fix; resume after).

## Why this arc (the graveyard tail)
S2b made LOD transitions pop-free (geomorph) and we shrank the S2a skirt to stop it fencing every chunk —
but a skirt is still a skirt: the dropped border ring stays faintly visible at grazing angles (the residual
"squares" the user flagged), and on the largest distant chunks the skirt is tens of metres deep. A visible
crack-backstop is not the AAA bar. **S2d removes the skirt entirely and makes seams continuous by
construction** via the canonical CDLOD technique: edge-stitched mesh variants chosen per chunk by neighbor
LOD. This is the long-term-best crack solution and the one T2 (world tiles) will build on.

The skirt=0 A/B during S2b showed the geomorph + the ≤1-level invariant already leave **no cracks** on the
fixed region in the tested cases — so the skirt was a backstop, not load-bearing. S2d replaces "backstop"
with "correct": stitched edges that coincide vertex-for-vertex with the coarser neighbor, proven numerically.

## Decisions (to pillars + AAA + long-term-best)

### 1. Per-edge neighbor-LOD resolution in the quadtree (the new core)
Chunks today carry only `OriginXZ`, `Size`, `Level` — nothing about neighbors. Stitching needs, per chunk,
**which of its 4 edges face a one-level-COARSER neighbor** (the only edges that can crack; same-level edges
already share vertices, and a finer neighbor is the finer chunk's problem, not this one's).

- **Mechanism — tree point-sample (NOT a geometric overlap scan).** For a leaf at `(origin, size, level)`,
  sample a point just OUTSIDE the midpoint of each edge (e.g. the −Z edge probe = `origin + (size*0.5, −ε)`),
  and walk the SAME quadtree to find which leaf contains that probe; compare its level to this leaf's. The
  ≤1-level invariant guarantees the neighbor is same-level or exactly-one-coarser. A coarser neighbor sets
  that edge's stitch bit. O(n·log depth), exact, reuses the tree we already own. Chosen over an O(n²)/spatial-
  hash edge-overlap test because point-containment has no epsilon-matching fragility and is the resolution
  T2 will reuse for cross-tile neighbors.
- **Output — a 4-bit `stitchMask` per leaf.** Bit per edge in a FIXED, documented convention tied to Godot
  `PlaneMesh` XZ axes (pinned in the plan; e.g. bit0 = −X, bit1 = +X, bit2 = −Z, bit3 = +Z). `CdlodChunk`
  gains a `StitchMask` field (or the select returns it alongside).
- **World-region boundary edges have no neighbor.** Those edges get NO stitch (the region edge isn't a
  same-region LOD seam). If a visible crack ever appears at the outer region rim, a single-edge skirt-of-last-
  resort can be re-added there only — but on the fixed 8 km region the rim is far off the play area, so YAGNI:
  do not pre-build it. Note it for T2 (tiles will stitch across tile boundaries instead).

### 2. Prebuilt stitched mesh variants (16), positions baked — shader untouched
- **16 variants.** 4 edges × {stitch, no-stitch} = 2⁴ = 16 unit-grid meshes, built once at setup, indexed by
  `stitchMask`. Memory is negligible (16 × a 65² grid; shared by all instances of that mask).
- **The weld (the stitch math).** In a variant, for each edge whose bit is set, every ODD boundary vertex
  (grid index 1,3,…,63 along that edge) is moved IN UNIT-XZ to the midpoint of its two EVEN neighbors along
  the edge. That makes the stitched edge have exactly the coarser neighbor's vertex spacing (half the
  segments) and lie on the same line → when the shader displaces both edges from the SAME `field_height` at
  the SAME world XZ, the two edges coincide vertex-for-vertex. Interior vertices are unchanged. Only the
  boundary ring of a stitched edge moves; corners (shared by two edges) resolve consistently (a corner vertex
  is even-indexed at both ends, so it never moves).
- **Why baked-into-mesh, not a shader edge-snap.** Keeps the already-subtle geomorph vertex shader clean (no
  extra per-vertex branch, no new per-instance uniform), and baked positions are trivially, statically
  correct with zero GPU cost. The shader's chunk branch is UNCHANGED by S2d.
- **Skirt deleted.** The `skirt` term in `ground.gdshader`'s chunk branch is removed (stitching supersedes
  it). This also removes the residual grazing-angle squares.

### 3. Geomorph × stitch compose cleanly (verified, not assumed)
The geomorph (S2b) morphs interior vertex XZ toward the coarse (even) grid by `morphK`; stitching welds
stitched-edge odd verts onto even positions in the baked mesh. These must not fight:
- The geomorph's coarse target is `floor(g*0.5+0.5)*2` — snapping to the nearest EVEN index. A welded edge
  vertex already sits AT an even-aligned position, so the morph's snap-to-even is a **no-op on it** (snapping
  an even position to even returns itself). Therefore a stitched edge vertex morphs to itself → stitch and
  morph commute on the boundary.
- Interior vertices are untouched by stitching, so the morph is unchanged there.
- At the far band edge (`morphK=1`) the whole chunk is on the coarse grid INCLUDING its edges, so the LOD
  hand-off and the stitch agree. This composition is a stated property the verification must CONFIRM (the
  `--stitchcheck` boundary-coincidence test, run with geomorph active), not merely an argument.

### 4. Invariant: enforce-by-construction, verify, don't over-engineer
Stitching is only sound if edge-adjacent leaves never differ by >1 level (else no single-stitch variant can
bridge a 2-level jump). The distance split rule (`d < size·splitFactor`) with a monotonic-in-distance metric
produces a restricted (balanced) quadtree by construction. S2d does NOT add a forced-balancing pass UNLESS
verification shows the rule alone is insufficient (YAGNI — don't build balancing the metric already gives).
- **Verify:** `--cdlodcheck` already asserts the ≤1-level invariant; S2d keeps it green and additionally
  exercises it across a sweep of camera positions (reuse the S2b test paths) so the guarantee is empirical
  across motion, not one frame.
- If (and only if) a >1-level adjacency is ever found, add a minimal restricted-quadtree balance pass and
  re-verify. Documented as a conditional, not pre-built.

## Staged build (commit per step — send-it/revert)
- **S2d.1 — neighbor resolution + stitchMask.** Quadtree computes each leaf's 4-bit stitch mask via edge
  point-sample; `CdlodChunk` carries it. Commit when `--cdlodcheck` still PASS and a debug print shows masks.
- **S2d.2 — 16 stitched mesh variants + pool selection.** `CdlodMesh` builds the 16 welded variants;
  `CdlodTerrain` picks `variants[mask]` per chunk per Tick. Commit when chunks render with stitched edges.
- **S2d.3 — delete the skirt.** Remove the skirt term from `ground.gdshader`'s chunk branch. Commit when it
  builds + renders (cracks now prevented by stitching, not skirt).
- **S2d.4 — `--stitchcheck` numeric crack guard.** Assert for every (finer-chunk stitched edge, coarser
  neighbor edge) pair that boundary vertices coincide in world XZ to ~0 m, with geomorph active. PASS/FAIL
  like `--morphcheck`. Test-the-test: a deliberately-wrong weld must FAIL it.
- **THE GATE (after S2d.1–S2d.4):** the user free-flies the LOD bands and confirms **ZERO cracks, ZERO
  skirt squares, ZERO new pops** in motion. Numeric backstop: `--stitchcheck` + `--morphcheck` + `--cdlodcheck`
  all PASS. That is S2d's definition of done (and effectively T1's geometry close-out; S2c shadows remains).

## Verification (NO TDD — GPU/visual; gate viewport = `scenes/terrain_lab.tscn`)
- **⚠ Build C# after EVERY `.cs` edit** (`dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`) before
  launching — the player binary does NOT rebuild C# (memory `wg16-csharp-stale-dll-gotcha`). Shaders hot-compile.
- **Build clean** → `0 Error(s)`. **`--fieldcheck` stays PASS** (field math untouched — skin not bones).
- **`--cdlodcheck` PASS** (≤1-level invariant intact). **`--morphcheck` PASS** (geomorph still pop-free —
  the stitch must not break it). **`--stitchcheck` PASS** (new: boundary vertices coincident → no crack).
- **`--profmove`** stays under 8 ms. Variant selection is an array index per chunk (cheap); 16 meshes add
  trivial memory. NOTE: the known S2a chunk-rebuild frame spike (54–71 ms in motion) is OUT OF SCOPE here —
  it is the pool-rebuild cost the S2b harness surfaced; logged for a dedicated perf pass ("performance will
  need to happen eventually" — user). S2d must not make it materially worse, but does not fix it.
- **THE eye-gate:** user flies the bands → ZERO cracks / squares / new pops. Never claimed from a downscaled
  still (`ground-texture-feedback`). Run rules: user `--flags` after a bare `--`; `--auto-shot` needs an OS
  path; ONE Godot at a time; scene can't run `--headless` (memory `wg16-cli-run-invocation`).
- **Concurrency:** the sky/light lane edits in parallel (`CloudVolume.cs`, `LightingState.cs`,
  `TerrainLabUI.Lighting.cs` / `.Apply.cs` / `.Review.cs`, `cloud_raymarch.gdshader`/`.glsl`, `data/lab_controls.json`,
  `project.godot`). S2d touches `CdlodMesh.cs`, `CdlodQuadtree.cs`, `CdlodTerrain.cs`, `ground.gdshader`, and
  adds a `--stitchcheck` flag (small edits to `TerrainLabUI.Cli.cs` / `TerrainLabUI.cs`). Stage ONLY S2d files
  by explicit path; NEVER `git add -A`. They are doing a shadows fix → S2c stays paused; S2d is shadow-free.

## Definition of done
S2d.1–S2d.4 committed; the **user confirms ZERO cracks, ZERO skirt squares, ZERO new pops** flying the LOD
bands; `--cdlodcheck` + `--morphcheck` + `--stitchcheck` + `--fieldcheck` all PASS; perf under 8 ms. The skirt
is gone. This closes T1's GEOMETRY (continuous, crack-free, pop-free LOD on the fixed region). Remaining T1
tail: S2c proxy shadows (paused on the sky lane's shadows fix). Then T2 (world tiles).

## NOT in scope (YAGNI / deferred)
- **The chunk-rebuild frame spike** (54–71 ms in motion, surfaced by the S2b harness) → a dedicated perf pass.
  Acknowledged as the known next-perf item; S2d must not worsen it but does not fix it.
- **Forced restricted-quadtree balancing** → only if verification shows the distance rule doesn't already
  guarantee ≤1-level (it should). Conditional, not pre-built.
- **Region-rim skirt-of-last-resort** → only if a rim crack is ever seen; off the play area on the fixed
  region. T2 stitches tile boundaries instead.
- **Proxy shadows (S2c)**, world tiles (T2), streaming (T3), any field-math or sky/light change.
- **Auto crack-detection beyond `--stitchcheck`'s vertex-coincidence proof** — the numeric coincidence test
  IS the proof; no image-diff detector.
