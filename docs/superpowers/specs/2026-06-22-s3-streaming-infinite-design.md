# WG16 S3 — Streaming infinite terrain (with folded floating-origin)

Date: 2026-06-22. Status: SPEC (brainstormed; decisions to pillars + roadmap per the user's standing
directive for this arc — "pillars and roadmap for all Qs"). Parent: `2026-06-21-infinite-terrain-cdlod-design.md`
(the §5 S3/S4 stages — THIS spec folds them into one arc, see "Relationship" below). Follows: S2 (quadtree
+ geomorph + edge-stitch on the fixed region — DONE: pop-free `--morphcheck`, crack-free `--stitchcheck`,
shadow-acne fixed). Precedes: erosion / water / biomes / surfacing (each its own later arc, plugging into the
§2 chunk contract — NOT this arc).

## Why this arc (the keystone)
The infinite-terrain spec's §0 names the dominant fact: **"infinite" is a unit-of-work problem, not a
rendering problem** — erosion carves chunks, water crosses chunks, biomes/textures apply per-chunk. S2 built
the chunk (quadtree + pop-free/crack-free LOD) but **rooted it over a fixed 8 km region**. S3 unpins that
root so chunks stream around a roaming camera → **the infinite world** the next four arcs all stand on. This
is the keystone: get streaming + the chunk contract right and erosion/water/biomes/surfacing inherit a home;
get it wrong and they are rebuilt repeatedly (the WG16 anti-pattern).

## What S2 already gives us (verified 2026-06-22, so S3 builds on fact not assumption)
- **Render is ALREADY live-analytic.** `ground.gdshader`'s chunk branch displaces from live `field_height`
  (`analytic_h`) in the vertex shader — a new chunk is a mesh patch, no bake, no readback. S3's core render
  assumption already holds. (The S1 perf go/no-go is effectively answered: S2's LOD vertex-cut brought the
  live field to ~6.4 ms static, vs S1's 34 ms full-mesh — the quadtree closed the 4.3× gap.)
- **The quadtree root is FIXED** (`new CdlodQuadtree(-region/2, -region/2, region, …)`). Unpinning it is the
  central change.
- **The per-chunk shadow AABB samples the BAKED heightmap** (`ChunkHeightRange` → `_heights`, current region
  only). A streamed chunk far out has no baked data — so the AABB needs an infinite-safe source (decided below).
- **`FieldCompute` is sync-blocking** (`Submit(); Sync(); BufferGetData()`). The lazy per-chunk *data* grid
  (chunk-contract §2.3) would need this async — but **no consumer exists yet** (erosion/collision unbuilt), so
  it stays dormant this arc (reserve the slot, don't build the async path — YAGNI, per the contract's own
  scope discipline).

## Decisions (to pillars + roadmap)

### 1. Snapped camera-relative render space — fold floating-origin in (no drift phase, ever)
The parent spec stages S3 (streaming, true world coords) then S4 (floating-origin) separately for debugging
isolation. **This arc folds them** (user's call): build precise coords from frame one so the "jitter far out"
phase never exists. Pillar-best — the most-correct infinite-world coordinate model, and it costs ~nothing
because `Tick` already rewrites every chunk transform each frame.

- **Each frame:** `renderOrigin = snap(cameraWorldXZ, coarseChunkSize)` — the camera's world XZ snapped DOWN
  to the coarsest-chunk grid. Every chunk is positioned at `chunkWorldXZ − renderOrigin` → render-relative
  coordinates stay **bounded to the camera's visible window** (a few km at most — the window extent), NOT
  unbounded, **regardless of how far the camera has flown**. Bounded few-km coords keep float32 precision
  ample (~sub-mm at km scale) where unbounded world coords (tens/hundreds of km) would jitter. The world
  conceptually slides under a camera that stays near the render origin.
- **The field still samples TRUE world XZ.** The shader reconstructs `wxz = renderRelativeXZ + render_origin`
  (uniform) before `field_height`. Because `render_origin` is snapped to chunk size, the reconstructed world
  XZ at any given surface point is **bit-identical frame-to-frame across a snap** → zero shimmer/swimming by
  construction (the same structural-correctness discipline as the geomorph: made impossible, not tuned).
- **The camera node keeps its TRUE world position** (it really moves). Only terrain *chunks* render relative
  to `renderOrigin`. So `cam_world` (used by the sky/cloud lane) is unaffected — no cross-lane ripple, no
  camera-at-origin decoupling. (This is why snapped-continuous beats full camera-at-origin: it doesn't disturb
  everything else.)
- **No separate S4.** Floating-origin is achieved by the snap-continuous render space; the parent's S4 is
  subsumed. The S4 *gate* (teleport far out → no jitter) is folded into this arc's eye-gate.

### 2. Roaming quadtree root
The quadtree covers a large power-of-two window centered on the camera (root extent follows `renderOrigin`),
instead of a fixed region root. Leaf selection is UNCHANGED — `Recurse`/`LeafSizeAt`/the stitch-mask logic
already operate in world XZ and are distance-based; they just run over a moving root extent. "Infinite" = the
window roams; per-frame the tree is bounded (finest LOD near camera, coarsest at the window edge) — you only
build what's near. Matches parent §5 ("the root recenters on the camera").

### 3. Per-chunk AABB from a CPU analytic eval (retire the baked dependency)
A streamed chunk's tight vertical shadow AABB (load-bearing — the 96b88a9 acne fix) is computed by evaluating
`field_height` at a **coarse CPU grid** (~5×5) over the chunk footprint on chunk birth → min/max height. Works
anywhere (infinite-safe), keeps the tight AABB that fixed shadow acne (pillar: don't regress quality), and
**retires the `_heights` baked-heightmap dependency entirely** (cleaner for infinite). Cost is bounded: only
on births, capped by the churn budget (§4). (The CPU `field_height` must match the shader's — reuse the same
field math the bake already mirrors; this is a read-only eval, NOT a change to the field — skin not bones.)

### 4. Churn budget (bound the cost streaming introduces)
Cap chunk births/deaths per frame (`MaxChunkOps`, tunable). A fast camera amortizes pool growth + AABB evals
across frames instead of spiking. This is also the lever that bounds the known S2 chunk-rebuild frame spike
(54–71 ms transients) under motion — S3 does not *fix* the rebuild cost, but the budget keeps it from
unbounded growth as the world streams. (If a profile shows the budget starving visible chunks, raise the cap;
it is a uniform/field, no rebuild — codebase convention.)

### 5. Frustum culling (free once chunks exist)
Drop chunks outside the camera frustum. Godot culls via the per-chunk AABB + visibility; S3 ensures off-window
chunks hide. Lands here per parent §5 (trivial once chunks exist).

### 6. Async data path — RESERVED, dormant (YAGNI)
The lazy per-chunk data grid + async `FieldCompute` (parent §2.3 / §5) is NOT built this arc — no consumer
needs it. The chunk contract reserves the slot; the async path is built when erosion/collision wake it. (Per
the contract's own scope discipline: "the async data path is a property of S3 *when streaming needs it*, not a
day-one clause" — and rendering streams fine without it, so nothing needs it now.)

## What stays UNCHANGED (S3 changes WHERE chunks are + WHAT XZ the field samples, not HOW they morph)
- The geomorph (`--morphcheck`), edge-stitch variants (`--stitchcheck`), and field math (`--fieldcheck`) are
  untouched. They operate in render-relative space; only the final field-sample XZ gains the `render_origin`
  offset. **All three guards MUST stay PASS** — that is the regression backstop.
- `ground.gdshader`'s geomorph/stitch/normal code: untouched except adding `uniform vec3 render_origin;` and
  the one-line `wxz += render_origin.xz` before `analytic_h`.

## Staged build (commit per step — send-it/revert)
- **S3.1 — render-origin plumbing.** `render_origin` uniform in the shader (world-XZ reconstruction);
  `CdlodTerrain.Tick` computes `renderOrigin = snap(cam, coarseChunkSize)` + positions chunks relative + pushes
  the uniform. At this step the root is still the fixed region (just rendered relative) → looks identical;
  `--fieldcheck`/`--cdlodcheck`/`--morphcheck`/`--stitchcheck` all still PASS (the offset is exact). Commit.
- **S3.2 — roaming root + analytic AABB.** Quadtree root follows `renderOrigin`; AABB from CPU `field_height`
  eval (retire `_heights`). Chunks now stream as the camera moves. Commit when chunks appear/vanish + guards PASS.
- **S3.3 — churn budget + frustum culling.** Cap births/deaths per frame; cull off-window chunks. Commit when
  `--profmove` holds under a moving camera + no visible starvation.
- **S3.4 — `--streamcheck`.** Scripted long traverse asserts: ≤1-level invariant throughout, budget never
  exceeded, no main-thread gen stall, field-continuity across a snap (fixed world point bit-identical
  before/after). PASS/FAIL + test-the-test (break snap-align → FAIL). Commit.
- **THE GATE (after S3.1–S3.4):** the eye-gate (below). This arc's definition of done.

## Verification (NO TDD — GPU/visual; gate viewport = `scenes/terrain_lab.tscn` / `review.tscn`)
- **⚠ Build C# after EVERY `.cs` edit** (`dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`) before
  launching — player binary does NOT rebuild C# (memory `wg16-csharp-stale-dll-gotcha`). Shaders hot-compile.
- **Build clean** → `0 Error(s)`. **`--fieldcheck` stays PASS** (field math untouched — skin not bones).
- **`--cdlodcheck` / `--morphcheck` / `--stitchcheck` ALL stay PASS** — S3 must not regress S2 geometry.
- **`--streamcheck` PASS** (new: invariant-along-traverse + budget + no-stall + snap field-continuity).
- **`--profmove`** under 8 ms in motion. Streaming churn is the new cost; the churn budget is the lever. The
  known S2 rebuild spike is bounded (not fixed) by the budget; if it dominates, that is the logged perf-pass
  item, not an S3 blocker (unless it breaks the eye-gate).
- **THE eye-gate (parent §5 gate = the arc's definition of done):** **fly one direction for a full minute —
  no chunk edge ever appears at the window boundary, no hitch, no shimmer at renderOrigin snaps, frame time
  holds. Infinite world achieved.** PLUS the folded S4 check: **fly/teleport ~tens of km out → no vertex
  jitter, terrain identical in character to near origin** (proves the folded floating-origin). Never claimed
  from a downscaled still (`ground-texture-feedback`); `--auto-shot` is sanity-only. Run rules: user `--flags`
  after a bare `--`; ONE Godot at a time; scene can't run `--headless` (memory `wg16-cli-run-invocation`).
- **Concurrency:** S3 is terrain-lane only (`CdlodTerrain.cs`, `CdlodQuadtree.cs`, `ground.gdshader`, a new
  `--streamcheck` in `TerrainLabUI.Cli.cs`/`.cs`). The sky/light lane edits its own files in parallel — stage
  ONLY S3 files by explicit path; NEVER `git add -A`.

## Definition of done
S3.1–S3.4 committed; the user flies a one-minute traverse + a far-out teleport and confirms **infinite world,
no edge, no hitch, no shimmer, no jitter**; `--fieldcheck` + `--cdlodcheck` + `--morphcheck` + `--stitchcheck`
+ `--streamcheck` all PASS; `--profmove` under budget. The chunk is now the streamed, infinite, pop-free,
crack-free unit of work the next arcs plug into.

## Relationship to the parent spec (2026-06-21-infinite-terrain-cdlod)
- **Kept:** the chunk contract (§2), the CDLOD mechanism, the pop-free/crack-free guarantees from S2, the
  "infinite = unit-of-work" framing, the toggle-not-big-bang + eye-gate discipline.
- **Folded:** parent S3 (streaming, true world coords) + S4 (floating-origin) → ONE arc with snapped
  camera-relative coords, so no float-drift phase ever exists (user's "fold" decision; pillar-best coord model).
- **Deferred (per parent + this spec):** the async data path (reserved-dormant, no consumer); erosion / water
  / biomes / surfacing (each its own arc on the chunk contract).

## NOT in scope (YAGNI / deferred)
- The async per-chunk data grid / async `FieldCompute` — reserved-dormant until a consumer (erosion/collision).
- Erosion, water, biomes, ground surfacing — each its own later arc on the §2 chunk contract.
- Fixing the S2 chunk-rebuild frame spike beyond bounding it with the churn budget — logged perf-pass item.
- Collision, flora scatter, world-editing — per-chunk integrations that follow streaming, not part of it.
- Any field-math or sky/light change.
