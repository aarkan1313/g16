# WG16 Infinite Terrain — Design (Analytic CDLOD, hybrid render+data)

Date: 2026-06-21. Status: SPEC (brainstormed with the user; approved section-by-section).
Supersedes the framing of `2026-06-18-terrain-lod-roadmap-design.md` (see "Relationship to the
2026-06-18 spec" below — that doc's CDLOD mechanism is kept; its T1 *premise* is corrected).
Companions: `docs/ROADMAP-northstar.md` (the feature north star), memory
`terrain-clipmap-killed-wg1-15` (the post-mortem this is built around), `docs/performance.md`
(the 8 ms budget + the ~3.8 ms current mesh floor).

---

## 0. Why this arc, and why now

WG16's stated build order is: **infinite world → erosion + water → biomes → ground textures.**
This spec is the **infinite world** arc — the foundation the other three stand on.

The dominant design fact: **"infinite" is not primarily a rendering problem, it is a *unit-of-work*
problem.** Erosion carves chunks; water flows across chunks; biomes are fields sampled per-chunk;
textures are applied per-chunk. So the #1 job of this arc is to **define the chunk** — the stable
world-XZ unit every later arc plugs into. LOD is the *rendering tactic* that makes infinite chunks
affordable; it is not the goal. Get the chunk contract right and the next three arcs inherit a home.
Get it wrong and it is rebuilt four times.

### The premise correction (why this is not the 2026-06-18 spec verbatim)

The 2026-06-18 roadmap gated **"T1 = pop-free continuous LOD on the current fixed 8 km region,
standalone, before any tiling."** During this brainstorm the user observed the decisive flaw in that
premise: **the current region is a single baked heightmap on a uniform-subdivision mesh — it has no
LOD, so it cannot pop.** Proving "pop-free LOD" on a region that has no LOD means *adding* LOD to a
region that does not need it, purely to prove the LOD we just added does not pop — a gate with no
user-facing payoff, on a configuration we immediately discard once tiles arrive.

The post-mortem's *real* lesson is not "gate on the fixed region." It is **"prove the pop-free
technique on a small, controlled scope before building streaming infra on top of it."** That lesson is
preserved exactly — relocated into **S2** (quadtree + geomorph on one finite region, no streaming),
where LOD pops can actually occur and are therefore worth gating against. The mechanism (CDLOD,
quadtree + per-vertex geomorph, NOT clipmap) is unchanged from 2026-06-18.

---

## 1. Approach decision — Analytic CDLOD with a hybrid render/data model (Approach C)

The fork is **where `field_height` runs in the render path.** Today it runs *only* in a compute shader
(`field_height.glsl`), dispatched once via `FieldCompute.ProducePage` with a **CPU readback**, baked
into a single 2048² `Rf` texture; `ground.gdshader` displaces by sampling **that texture**. So the
2026-06-18 spec's "free geomorph by re-sampling the analytic height" assumption is only half-true: the
analytic field exists, but on the **bake side**, not in the render path.

Three families were weighed against the pillars (**quality = performance = AAA-ish = long-term-best,
regardless of time cost**) and against the user's roadmap order (erosion/water are next, and both are
*data* operations on the terrain, not shader effects):

| Approach | Render path | Data for erosion/water? | Pop risk | Verdict |
|---|---|---|---|---|
| **A — Analytic-only** | live `field_height` in vertex shader | **none** (height computed and discarded each frame) | none (same fn at every LOD) | Beautiful + simplest, but **strands erosion/water** — no height grid to carve. Fails *long-term-best*. |
| **B — Texture-cached** | bake a height texture per chunk per LOD; sample + lerp | yes (textures exist as data) | **reintroduces the stored-heightmap resample pop** the post-mortem named; per-chunk readback multiplies the sync-stall caveat | Fails *quality* (pops) + *performance* (stalls). The clipmap's data problem in a quadtree costume. |
| **C — Hybrid (CHOSEN)** | live `field_height` in vertex shader (**render**); compute bake retained as a lazy per-chunk **data** grid | yes (lazy, carvable) | none (render morph target is the *identical* analytic function — pop is structurally impossible, not tuned away) | **Only path where the render is pop-free AND erosion/water have carvable data**, from one shared field function. Most moving parts; the pillars explicitly waive time cost. |

**Chosen: C.** The pillars are decisive: they *name* the reasons to reject A (no long-term foundation
for the next arcs) and B (visible pops are not AAA; stalls are not performant), and explicitly waive
C's only downside ("regardless of time cost"). C is staged so its complexity is paid down incrementally
(S1–S4) and never built big-bang.

### C's one real risk, and how it is retired

Running the heavy `field_height` (domain warps + three fBM variants + ridges) **per-vertex, live,
every frame** for the whole visible quadtree could blow the 8 ms budget. This is *measurable*, not
arguable, so **S1 is built first specifically to measure it** (port the field to a shader include,
displace the current single mesh from it live, run `--profmove`). If S1 holds frame time → C is
green-lit on real evidence. If S1 blows the budget → we pivot the height-source (e.g. analytic morph
*math* with a sampled height *value*) **before** building the quadtree on a bad foundation. Cheap
failure, by design. (Pillar: measure performance, don't assume; never big-bang.)

---

## 2. The chunk contract (the keystone)

A **chunk** is a square region of world-XZ at a power-of-two size, addressed by integer coordinates.
It is the universal unit of work — subdivided by the quadtree, loaded/unloaded by streaming, and (in
later arcs) carved by erosion, crossed by water, colored by biomes, painted by textures.

Each chunk owns exactly three things:

1. **Identity** — `(level, x, z)`. Deterministic: the same world coordinate always belongs to the same
   chunk address, forever, regardless of camera. This is precisely what clipmap *rings* cannot provide
   — rings move, so stable world-data (erosion deltas, biome data, collision) cannot pin to them. Stable
   world-XZ chunks are why the post-mortem rejected rings and chose a quadtree.

2. **Render representation** — a mesh patch at a chosen vertex resolution, displaced **live in the
   vertex shader** by analytic `field_height` (Approach C's render path). Pop-free by construction;
   ~free to stream (allocate a patch — no bake, no readback).

3. **Data representation (lazy, optional)** — a baked height grid for this chunk, produced by the *same*
   `field_height` via the compute path, **generated only when a consumer needs it** (erosion, water,
   collision, scatter). Day one, nothing needs it, so it is never built — but the contract reserves the
   slot. Erosion later writes a **height-delta** into it. **This reserved slot is the entire reason C is
   chosen over A.**

**The one invariant** binding render and data: **effective height = analytic + Σ deltas; every consumer
reads the effective height, never raw analytic.** (Once erosion runs, `analytic + delta` is what is
*seen*, so collision/water/scatter must read the same value or they disagree with the rendered surface.
This is not pre-engineering for erosion — it is simply Approach C stated correctly.)

**Addressing unit ≠ render resolution.** The chunk is the world-XZ *addressing* unit. Each system picks
its own resolution within that addressing — terrain geometry, and later flora multimesh / splat, need
not share a subdivision. (Confirmed by the existing un-integrated flora code, which already assumes a
`chunk_size_m` distinct from its per-layer `cell_size_m` and carries its own independent LOD bands.)

**The single shared field function — the non-negotiable.** Render and data both displace from **one**
`field_height`, compiled from **one** source: `field_height.glsl` (compute, today) and a generated
`field_height.gdshaderinc` (the material). Same math, two compile targets — they cannot disagree because
they are the same function. **S1 proves this parity holds before anything is built on it.**

### Scope discipline on the contract (what this contract deliberately does NOT do)

The north-star roadmap is *informational* — a menu of *what* may come, not a contract to pre-engineer
*for*. Per the user's explicit steer, this contract does **not** pre-build speculative seams:
- `field_height` keeps its current signature `(world_xz, seed, spacing)`. It is **not** made
  biome-extensible now. If/when biome-specific landforms arrive (an arc away), that arc changes the
  signature. (YAGNI is part of long-term-best; pre-built seams are cruft.)
- The async data path (below) is a property of **S3**, not a day-one clause — it is built when
  streaming needs it, not before.

---

## 3. Quadtree & LOD selection (S2 structure)

**Structure.** A quadtree rooted at a large world-XZ square. Each frame, walk from the root: for a
node, if it is *too far* from the camera relative to its size, keep it as one coarse chunk; if *close
enough*, subdivide into four children and recurse; stop at a max depth (finest LOD). The frame's output
is a set of leaf chunks — large far away, small/dense near the camera — covering the visible world at
roughly **constant screen-space triangle density**. Standard CDLOD selection.

**Neighbor rule (≤1 level).** Adjacent leaf chunks differ by **at most one LOD level**, enforced during
the quadtree walk. This single constraint keeps the morph and the edge-stitch bounded to a *one-level*
bridge — never an arbitrary jump — which is what makes seams tractable (§4).

**LOD by distance, with hysteresis.** A chunk picks its level from camera distance to its nearest
point. Each boundary carries a small hysteresis band (must cross *past* it, not merely touch, to
switch) so a chunk hovering near a threshold does not flicker between levels. (Proven pattern in this
codebase — the flora code's `HysteresisM`.)

**Not in S2:** streaming (the S2 quadtree is still rooted over the *current finite region*; §5 S3 makes
the root follow the camera) and frustum culling (§5 S3 — trivial once chunks exist, but not S2's point).
S2's sole job: prove the quadtree selects sane LODs and the morph between them is invisible.

---

## 4. Pop-free geomorph + crack-free seams (the graveyard-killer)

Three distinct failure modes killed WG1–15; each gets its own mechanism.

### Enemy 1 — the elevation pop (vertices snapping to new heights)
When a chunk drops from LOD N to N+1 it has fewer vertices, so the drawn surface *changes shape* in one
frame — the snap.

**Weapon — per-vertex geomorph.** Each vertex carries a continuous `morphK ∈ [0,1]` from where the
camera sits *within* this chunk's LOD distance band (0 at the near edge, →1 at the far edge where it is
about to coarsen). As `morphK → 1`, each fine vertex **slides its XZ** toward its coarser-LOD
counterpart (odd-indexed vertices migrate onto the even/coarse grid); height is then sampled by
`field_height` **at the morphed XZ**. By the moment the LOD actually switches, the fine vertices have
*already continuously moved* onto the coarse positions — the switch changes nothing visible. C0-continuous.

**Why bulletproof in Approach C specifically:** the morphed vertex calls the *same analytic
`field_height`* the coarse chunk will call. There is no second heightmap to disagree with — the fine
surface morphs onto the *exact* height the coarse surface will show. The elevation pop is **structurally
impossible**, not tuned away. (This is the guarantee Approach B cannot make and the core reason C was
chosen.)

### Enemy 2 — the quality pop (surface detail jumping)
Even with geometry morphing, surface *treatment* (anti-repetition, normal/parallax/textures later) that
switches hard at a distance draws a visible line where near-detail becomes far-detail.

**Weapon — detail cross-fade on one shared distance curve.** All distance-varying detail blends over a
band, and *everything* rides the **same** named distance curve → one soft transition zone, not a stack
of visible rings at different radii. The current placeholder has no such detail yet, so for S2/S3 this
is a **reserved seam** — but reserved as a single named curve from day one, so when textures arrive
(arc 4) they ride it instead of inventing a competing one.

### Enemy 3 — the crack (gaps at chunk borders)
Adjacent chunks at different LODs share an edge; the finer chunk has an extra mid-edge vertex the
coarser lacks, so the fine edge dips while the coarse edge cuts straight — a gap (sky through ground).

**Weapon — edge-stitch, skirts as backstop.** By the ≤1-level rule (§3) the mismatch is always exactly
one level: the fine edge has exactly one extra vertex between each pair of coarse vertices. Snap that
vertex onto the average of its two coarse neighbors → the fine edge becomes collinear with the coarse
edge. No gap. As cheap insurance against residual hairlines (e.g. mid-morph), each chunk drops a short
vertical **skirt** around its border — invisible from above, plugs any sub-pixel crack. Stitch is the
real fix; skirt is insurance.

### Composition (the full per-vertex story)
Per vertex, per frame, in the vertex shader: compute `morphK` from camera distance → lerp XZ toward the
coarse grid → if an edge vertex, apply the stitch → sample `field_height` at the final XZ → displaced
position. One function, no stored render data, pop-free by construction.

### The gate (THE gate of the arc)
Fly low and fast across the LOD bands, judged by **the user's live eye in motion** — the only look-gate
(pillars + the `ground-texture-feedback` memory: never a downscaled still). Pass condition is absolute:
**zero elevation pop, zero quality pop, zero cracks**, crossing every band. Numeric backstop: assert
`morphK` is continuous across band boundaries (no discontinuity at the switch). One visible pop → do not
proceed to streaming; fix the morph first.

---

## 5. Streaming + floating origin (S3, S4)

**S3 — Streaming.** The quadtree root stops being pinned to the current 8 km square and **recenters on
the camera** each frame; new leaf chunks enter the view distance, old ones leave.
- **Render path is free** — a new chunk is a mesh patch displaced by live `field_height`; no bake, no
  readback, no stall. (Approach C's gift; the reason streaming is cheap here where it was murderous in
  WG1–15.)
- **Data path goes async** — the lazy data grid (§2.3) must NOT use today's `Submit(); Sync();
  BufferGetData()` synchronous stall, or every chunk needing data hitches the frame. S3 converts it to
  an async compute submission whose result is collected a frame or two later. Dormant until a consumer
  exists (erosion/collision unbuilt), but **built async so it never hitches when it wakes**.
- **Budgeted** — chunk births/deaths per frame are capped so a fast camera amortizes work across frames
  instead of spiking. The 8 ms in-motion budget is the ceiling; `--profmove` measures.
- **Per-tile frustum culling** lands here (free once chunks exist).
- **S3 gate:** fly one direction for a full minute — no edge ever appears, no hitch, frame time under
  budget. *Infinite world achieved.*

**S4 — Floating origin.** `field_height` hashes integer cells from float32 world-XZ; flying tens of km
out degrades float32 precision → vertex jitter. Fix: **rebase the world origin near the camera**
periodically so GPU-fed coordinates stay small. Isolated and last — a precision hardening pass, not a
feature; it addresses the audit's far-from-origin caveat.
- **S4 gate:** teleport ~100 km out — no jitter; terrain identical in character to near origin.

S3 ("does infinity *work*") and S4 ("does infinity *stay precise*") are separate stages so a streaming
bug is never tangled with a precision bug. One pillar at a time.

---

## 6. Verification, tooling, toggling (project convention — NO TDD; GPU/visual)

Each stage clears the same ladder, in order:
1. **Build clean** — `dotnet build WG16.csproj -v q -clp:ErrorsOnly` → `0 Error(s)`.
2. **Headless import** — `--headless --import` compile-checks shaders. (The scene cannot run headless —
   `FieldCompute`'s local RenderingDevice NullRefs without a GPU context; run windowed to execute.)
3. **Mechanical CLI self-check** — a `--*check` flag that reads back state, prints PASS/FAIL, quits:
   - **S1:** analytic-vs-baked height parity within epsilon.
   - **S2:** quadtree ≤1-level neighbor invariant + `morphK` continuity across bands.
   - **S3:** no chunk-gen stall on the frame thread + per-frame budget respected.
   - **S4:** heights match across an origin rebase.
4. **`--profmove`** — the 8 ms in-motion budget. **S1 is the go/no-go** (live `field_height` per-vertex);
   every later stage re-checks.
5. **The look-gate — the user's live eye, in motion** — the only authority on look and pops. **Never
   claimed from a downscaled auto-shot** (they hid the shredding last time, per `ground-texture-feedback`).
   `--auto-shot` is a *sanity* check ("did it render / did anything throw") only. One "pop"/"bad" → stop
   and question the approach, do not tune.

**Tooling built alongside (so the gates are possible, not afterthoughts):**
- **LOD-visualization overlay** (`--lodviz`) — color chunks by level / wireframe; makes "is that a pop"
  answerable and lets the user *watch* the morph. Built in S2.
- The mechanical checks above, each a CLI flag, added in the stage that needs it.
- Everything tunable stays a **uniform / JSON control** (codebase convention): LOD distances, morph band
  widths, skirt depth, chunk size, budget caps — no C# rebuild to tune the look.

**Toggle, never big-bang.** The new CDLOD terrain lives behind a switch so the current single-mesh
placeholder stays runnable for A/B comparison until CDLOD passes its gates. (Pillar: build behind a
toggle, judge by eye, never big-bang.)

### Staged-gate summary (the whole arc on one card)

| Stage | Builds | Hard gate (in motion: the user's eye + the mechanical check) |
|---|---|---|
| **S1** | Port `field_height` → `.gdshaderinc`; current single mesh displaces from the **live function** | Looks **identical** to the bake (parity) **AND** holds frame time (`--profmove`). **Go/no-go for Approach C.** |
| **S2** | Quadtree + per-vertex geomorph + edge-stitch/skirt, on the **fixed region**; `--lodviz` | **Zero** elevation pop, quality pop, or crack across every LOD band. *(The graveyard gate.)* |
| **S3** | Camera-following quadtree; chunks stream in/out; data path **async**; frustum culling | Fly one direction 1 min: **no edge, no hitch**, under budget. *(Infinite world achieved.)* |
| **S4** | Origin rebasing | Teleport ~100 km: **no jitter**. *(Infinity hardened.)* |

**Definition of done for the arc:** S1–S4 each pass their gate by the user's eye in motion. **S1 is also
a decision point:** if live `field_height` per-vertex blows the budget, stop and pivot the height-source
*before* building the quadtree. Only after S1–S4 pass do erosion / water / biomes / textures follow as
their own arcs, each plugging into the §2 chunk contract.

---

## 7. Guardrails carried from the project (do NOT skip)

- **The graveyard.** Terrain LOD / clipmap / infinite streaming killed WG1–15. **Use CDLOD, not
  clipmap.** The non-negotiable is **pop-free continuous LOD, proven in motion (S2) before any streaming
  infra (S3)** — the exact WG1–15 trap is building infra before the core technique is proven.
- **Skin not bones.** Do NOT alter the base field *generation* (`field_height`'s math /
  `FieldParams`) — it is the proven bones (8 seeds + the audit). CDLOD *samples* it; it is not rebuilt.
  (S1 *relocates* the function to a shared include without changing its math — parity is the gate.)
- **Pillars:** quality = performance = AAA-ish = best-long-term, regardless of time cost; lead with the
  most-correct option; one focused pillar at a time; build behind a toggle; never big-bang. Perf budget
  = **8 ms in-motion** (`--profmove`); the current single no-LOD mesh is ~3.8 ms of that floor — S2/S3
  pay it back.
- **The user's live eye is the ONLY look-gate, in motion, never a still.** When the user says "bad"/"pop"
  once, STOP tuning and question the approach.

## 8. Relationship to the 2026-06-18 spec
- **Kept:** the CDLOD mechanism (quadtree + per-vertex geomorph + detail cross-fade + ≤1-level
  neighbor + edge-stitch), the clipmap rejection, the "prove the technique on a small scope before
  infra" discipline.
- **Corrected:** the 2026-06-18 standalone "T1 on the fixed region" gate is folded into **S2** (the
  technique proven on one finite region *with real tiling*, where pops can occur), because the current
  baked region has no LOD and therefore cannot pop — making a standalone fixed-region LOD gate a
  payoff-free step on a discarded configuration.
- **Added (this spec):** the explicit render/data fork resolved as **Approach C**; the chunk contract
  with the effective-height invariant; **S1 as the analytic-parity + perf go/no-go**; the async-data
  requirement promoted to a first-class S3 deliverable; `--lodviz` + per-stage mechanical checks.

## 9. NOT in scope (YAGNI)
- Erosion, water, biomes, ground-texture surfacing — each its own later arc, plugging into the §2 chunk
  contract. Not braided into this arc.
- GPU tessellation / mesh shaders (Godot 4 immaturity), Nanite-class virtual geometry (unavailable).
- Biome-into-`field_height` signature extension (deferred to the biome arc, per the user's "what, not
  how" steer).
- No clipmap, ever, without a fresh post-mortem-first roadmap.

---

## 10. S1 result (measured 2026-06-21)

S1 built (commits 121b583 / a871c6b / 7e8e8e1): `field_height` extracted to one shared
`field_math.gdshaderinc` (params as a `FieldP` struct); the compute bake splices it (C# marker
replace), the spatial `ground.gdshader` `#include`s it. Two cross-language fixes were needed for the
shared file to compile in BOTH Godot's shading language and RD-GLSL: unsigned shift amounts
(`>> 16u`, Godot rejects `uint >> int`) and one-uniform-per-line (Godot rejects comma-separated
uniforms). Both are valid/portable GLSL, so the single-source goal holds.

**Parity (mechanical + eye):** `--fieldcheck` = `PASS maxAbsDiff=0m` (bit-exact; the refactor did not
change a single height). Gross-shape analytic-vs-baked confirmed equal by eye at altitude.

**Perf go/no-go (`--profile=5 --profmove`, full 2048² no-LOD mesh, RTX 5090 Laptop):**
| Path | Avg in motion | Worst | 8 ms budget |
|---|---|---|---|
| Baked texture (baseline) | **5.9 ms** | 7.2 ms | under |
| Analytic live field | **34.0 ms** | 39.3 ms | **~4.3× over** |

**Verdict — CONDITIONAL GO (decision deferred to the user).** This is the worst case by construction:
`field_height` (domain warps + 3 fBM variants + ridges) evaluated per-vertex ×5 taps over the ENTIRE
4.19M-vertex mesh, every frame, with NO LOD. S2's quadtree exists precisely to cut that vertex count
by 1–2 orders of magnitude (constant screen-space density → most far vertices vanish), so the relevant
question is not "is 34 ms acceptable" (it isn't) but "will the quadtree's vertex reduction bring the
live field under budget." Three honable paths, weighed against the pillars:
1. **Proceed to S2 as designed** — bet the quadtree's vertex cut closes the 4.3× gap. Pure-analytic
   render (no per-chunk height data needed for *rendering*), keeps Approach C's data slot for erosion.
2. **Hybrid height-source** — analytic for the morph MATH (pop-free) but sample a per-chunk baked
   height TEXTURE for the value (cheap), accepting more data management. Lower per-vertex cost, but
   reintroduces some of the texture-cache the design avoided.
3. **Reduce field cost** — cache expensive low-frequency terms (continent/uplift are low-freq; could be
   sampled coarsely) so the per-vertex field is cheaper without changing the look.

S1 did its job: it surfaced the cost cheaply, on one mesh, BEFORE any quadtree was built — the
anti-WG1-15 discipline working as intended.

### S1 perf audit (independent, 2026-06-21) — the 34 ms is genuine but ~85% reducible

A separate-chat audit (brief: `docs/superpowers/handoffs/2026-06-21-s1-perf-audit-brief.md`), normalized
against the matched 5.9 ms baked anchor and verified here (no-dead-octaves arithmetic re-checked
independently — all octaves weight≈1.0 at 4 m spacing, confirmed), found the 34 ms is real GPU vertex
work but **NOT irreducible** — two look-neutral wins, separable and additive:

| Lever | Win | Safe? |
|---|---|---|
| **Cheaper normal** — `ground.gdshader` does **5 `field_height` evals/vertex** (1 height + 4 normal taps); the field already computes analytic derivatives (`value_noise_d`, `slope_damped_fbm`'s `dsum`) → exact gradient in ~1 eval | ~54% (53.8→25.0 ms on the audit's hot run) | Yes — analytic gradient is *more* correct than 4-tap finite diff (no ±spacing smoothing) |
| **Proxy shadows** — main 4.19M-vert mesh casts into all 4 PSSM cascades (analytic field runs ×~5 total); the existing coarse `_giProxy` is built for this but inert by default | ~21% (additional) | Yes — terrain directional shadows are low-frequency; 512² proxy silhouette ≈ 2048² at cascade distances |

Combined ≈ **7.5 ms** on the full un-LOD'd mesh (audit Test D). **Bottom line: the pure-analytic field is
affordable; the hybrid sampled-height fallback is NOT needed on cost grounds** (reserve it only if S2's
LOD morph needs a sampled tier for *correctness*, not perf). The quadtree (S2) thus becomes **headroom**
(draw distance / detail / stability), not the sole thing standing between the field and budget.

**Sequencing decision (made by the implementer, pillar-reasoned):**
- **S1.5 (next, small): land the cheaper normal only.** S2-independent (per-vertex waste regardless of
  vertex count), the biggest single lever, and pillar-*best* (exact normal). Eye-gate the normal change.
  Caveat: "1 eval exact" needs derivatives threaded through `continent`/`uplift`/`ridged_fbm` (which
  don't accumulate gradients today) — non-trivial; the 2-tap (3-eval) interim is the fallback.
- **Finding 2 (proxy shadows) → folded into S2, NOT now.** Fixing the proxy's shared `use_analytic`
  needs a separate shadow material/override — and S2 rewrites the shadow story anyway ("shadow pass uses
  the LOD'd mesh"). Doing it now then reworking it for the quadtree is building throwaway infra. It
  belongs in the S2 shadow design.
