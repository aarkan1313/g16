# Erosion — Arc Design (coupled drainage erosion: live sim → bake → streamed synthesis)

Date: 2026-06-17 · Status: awaiting user review · Lane: terrain shape (a transform on the base heightfield + a new sim unit)

## Why (and why this is the project's scar tissue)

Erosion is WG15's graveyard: **~19 erosion versions + 5 abandoned water systems, all judged
bad** (per `docs/2026-06-15-wg16-base-field-design.md`). WG15's own post-mortem verdict was
*"the architecture was sound; the erosion CONTENT was the problem."* WG16 was created by
deliberately **dropping the entire bake/erosion/water stack** and keeping only the proven
base field. So re-introducing erosion is high-risk and must be done differently, not as a
port.

**The user's specific WG15 symptom (live recollection):** baked terrain where ditches/
valleys/dips "didn't do logical things" — valleys grew then shrank, elevation reversals,
dips that didn't drain. **Diagnosed root cause** (from the WG15 record + the symptom): WG15
ran a *stack of independently-tuned solvers* — `valley_carve → stream_power → hydraulic →
thermal → alluvial` — chained in a bake, with **no unifying drainage model**, frozen at an
arbitrary iteration balance. Stream-power cut channels, alluvial deposition partly filled
them, thermal slumped walls into them; the solvers *fought each other* and the frozen result
was incoherent geometry. Water was a *separate* system that never informed where erosion
should cut, so channels didn't follow real drainage. **That stacking-without-coherence is
the thing we must NOT repeat.**

## The mandate (user, this session)

- **AAA + best-long-term, regardless of cost** (the pillars).
- **Must work procedurally / infinitely** — eventually. The user explicitly wants the
  "really cool" coupled sim AND infinite range, reconciled via precompute/bake/streaming.
- **Bakes must NOT take crazy room** — small on disk.
- **Sim quality FIRST.** Build order (user's call): prove the coupled erosion sim looks
  GREAT on the *current single region*, watched live, before any bake/stream/infinite/chunk
  work. The procedural/infinite layers depend on a chunk system WG16 does not have yet and
  are explicitly LATER.

## The core idea (how "awesome coupled sim" + "infinite" + "small bakes" reconcile)

One **coupled erosion+water simulation core**, consumed three ways. The reconciliation of
the apparent contradiction (huge eroded worlds can't fit on disk) is: **you do NOT bake the
terrain — you bake the low-frequency DRAINAGE SKELETON (tiny), and synthesize the
high-frequency detail procedurally at runtime, conditioned on it.**

- Erosion's *expensive, non-local, coherence-critical* part = the **large-scale drainage
  network** (where rivers/valleys run, flow accumulation, coarse eroded shape). It is
  **low-frequency** → bakes at COARSE resolution → **small** (MBs, not TBs).
- Erosion's *detail* (gully texture, bank roughness, rills) is **local** → synthesized
  **per-chunk at runtime, conditioned on the coarse baked drainage** → infinite, cheap,
  seamless. ("A major valley runs SW through here" → carve fine tributaries procedurally.)
- "Huge range / not boring" comes from the coupled sim producing genuinely varied drainage
  (not repetitive noise) + the procedural detail layer. Small bakes + big variety, together.

This mirrors WG16's proven DNA (clouds): a heavy compute produces a field; cheap consumers
sample it. And it fixes the WG15 root cause by construction: **one coherent coupled
drainage model**, not a stack of fighting solvers.

## Three consumers of the one sim core (all eventually; sim-core FIRST)

1. **Live erosion lab** — the coupled sim running interactively on a region, watched in
   MOTION (valleys cutting), tuned to the user's eye, NO real-time budget pressure (it's a
   tool, runs as fast as it runs). This is where the creative quality is judged. **FIRST.**
2. **Bake** — freeze the coupled sim's output as a compact **drainage skeleton** (coarse
   height delta + flow + sediment/material maps). Small on disk. LATER.
3. **Streamed semi-procedural** — runtime chunks synthesize full-res detail conditioned on
   the baked skeleton (and/or a coarse global pre-solve for true-infinite). LATER, and
   depends on a chunk/streaming system WG16 doesn't have yet.

## Architecture — units (decomposed; build order = de-risk the graveyard first)

```
base heightfield  ──►  EROSION SIM CORE (GPU compute, coupled water+sediment+slope)
(FieldCompute float[])      │  iterates on a local RD; reads base, writes eroded
                            │  height + flow + sediment fields
                            ▼
                  ┌─────────┴───────────┬──────────────────────────┐
                  ▼                     ▼                          ▼
           LIVE LAB (1)            BAKE (2)               STREAMED SYNTH (3)
        watch+tune, no budget   freeze coarse drainage   per-chunk detail from
                                skeleton (small)         the baked skeleton (infinite)
```

| Unit | What | Build phase |
|------|------|-------------|
| **E1 — Coupled sim core + live lab** | The GPU-compute coupled erosion (single unifying drainage-driven model: water flow → incision + sediment transport/deposition + thermal slope, stepped together, NOT a solver stack). Run live on the current region, iterable/watchable, full knob set, judged in motion. **Owns the "is the erosion actually good" question.** | **NOW (this arc's first plan)** |
| **E2 — Drainage skeleton bake** | Define + produce the compact baked representation (coarse height-delta + flow-accumulation + sediment/material channels). Sized for small disk. | LATER |
| **E3 — Semi-procedural detail synthesis** | Per-area fine detail conditioned on the baked skeleton (tributaries, bank/gully texture), seam-free. | LATER (needs chunk system) |
| **E4 — Coarse global pre-solve + streaming** | True-infinite: coarse drainage over a large/extending field + chunk streaming consuming it. | LATER (needs chunk system; biggest infra) |

Pre-req not owned here: the **chunk/streaming system** (WG16 is currently single-region).
E3/E4 depend on it. The user's sequencing: good sim first, procedural/chunk system later.

## Unit E1 — the coupled sim core (what "one coherent model" means concretely)

The anti-WG15: instead of chained independent solvers, **one loop** where each step computes
water flow from the current height, then applies incision + sediment transport/deposition +
thermal relaxation *from that same flow state*, so they can't fight (they share one truth per
step). Candidate model (to be chosen/justified in the E1 plan): a **shallow-water or
pipe-model** flow field + stream-power incision + capacity-based sediment transport +
talus/thermal slope — coupled, GPU-parallel per cell, iterated. Convergence/stability is the
central engineering concern (the symptom "valleys grow then shrink" = instability/overshoot
if done wrong): bounded per-step erosion, stable timestep, and **watch it converge live**.

Integration seam (verified): erosion is a transform on the base heightfield. `FieldCompute.
ProducePage(params) → float[]` (res², region meters, spacing m/texel) feeds the terrain via
an `Rf` heightmap texture (`TerrainLab.Build`). E1 reads that `float[]`, simulates on a local
RD (the SplatCompute/CloudNoiseCompute pattern — local-RD compute, runs WINDOWED not
headless), and outputs a modified height `float[]` (+ flow/sediment side textures for later
units + for coloring/wetness). The Presenter re-uploads the eroded height. **The base-field
math is NOT touched** — erosion is downstream of it (settled per project rule).

> **Honest flag:** WG16 was *defined* as "no bake stage." E1's live sim is a runtime
> transform (fine); E2's bake re-introduces a (small, drainage-only) bake. That's a
> deliberate, scoped reversal of the no-bake stance for erosion specifically, justified by
> the infinite-world requirement. Calling it out so it's a conscious decision, not drift.

## Performance posture

- **E1 sim:** offline/tool budget — runs on a local RD, iterates, no per-frame constraint.
  It can be slow; it's not in the render loop. (If the user wants live-while-flying erosion
  later, that's a separate ambition.)
- **Runtime (E3/E4):** the WHOLE point of the bake+synth split — runtime samples a small
  coarse field + cheap procedural detail, never runs the sim. Streaming cost is the chunk
  system's concern, not erosion's.
- **Bake size (E2):** coarse skeleton only (low-freq) → MBs. The explicit "no crazy room"
  guarantee comes from never baking full-res terrain.

## Testing / validation

- **E1 (the one that matters now):** mechanical = builds, sim runs on the region, outputs a
  valid heightfield, debug views of flow/sediment/height-delta confirm the *fields* are sane
  before judging the lit terrain. **THE gate: user watches it erode LIVE in motion** — do
  valleys cut LOGICALLY (drain downhill, dendritic networks, no reversals, no grow-then-
  shrink), does it CONVERGE to a stable good-looking state, is it tunable. This directly
  tests against the WG15 failure symptoms. Never judged from a single still.
- Later units judged when built.

## Risk / undo

Highest-risk arc WG16 has attempted (erosion = the graveyard). Mitigations: (1) **sim
quality proven FIRST on one region** before any infra — if the sim can't be made good, we
stop before building bake/stream (the expensive infra is worthless without a good sim);
(2) **one coherent model** (not the WG15 solver stack) attacks the diagnosed root cause;
(3) erosion is a transform DOWNSTREAM of the settled base field, behind a toggle — base
field untouched, `git checkout .` reverts, the un-eroded field is always the fallback;
(4) per-unit eye-gated, watched in motion (the WG15 bakes were judged too late/frozen).
If E1 can't reach "great" after a fair effort, that's a real stop point — surface it, don't
grind 19 versions like WG15.

## NOT doing (YAGNI / boundaries)

- NOT porting WG15's solvers (StreamPower/Thermal/Alluvial/Hydraulic/ValleyCarve as a stack)
  — that stack is the thing that failed. One coherent coupled model instead.
- NOT building the chunk/streaming system here (E3/E4 depend on it; separate future arc).
- NOT touching the base-field generation math (settled).
- NOT real-time-while-flying erosion (E1 is an offline tool); revisit only if asked.
- NOT coupling to the (future) world-editing or flora systems yet — though E2's flow/
  sediment fields are natural inputs to flora density + ground wetness later (note the seam).

## Build order

E1 (coupled sim core + live lab, on current region) — judge GREAT in motion — **then** E2
(skeleton bake) → E3 (semi-procedural detail; needs chunks) → E4 (coarse global + streaming;
needs chunks). Each its own plan, eye-gated. This doc's companion is the **E1 plan**
(`docs/superpowers/plans/2026-06-17-erosion-unit1-sim-core.md`); E2–E4 get plans when reached.
```
