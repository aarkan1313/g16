# Erosion Arc 1 — Sim Core + Drainage Substrate (the river fix)

Date: 2026-06-23. Status: SPEC (pillars-driven). Parent: `2026-06-23-erosion-hydrology-master-design.md` (Arc 1).
Builds on the race-free pipe-model sim already built (was "E1": `shaders/erosion_sim.glsl`, `scripts/erosion/*`,
`scenes/erosion_lab.tscn`). This spec covers **Arc 1 phase 1** (sim core + substrate, live-lab-judged). Phases
2 (bake) and 3 (per-chunk synth) are named in the master and get their own specs later.

## Why this exists (the gap the review found)

The built pipe-model sim is race-free (sawtooth fixed, roughness 0.0045) and DOES erode — but a logical review
of the kernel found it will **not** form convincing dendritic rivers as-is. Incision scales with **local water
depth/velocity** (`cap = capacity·slope·speed·clamp(w·40,0,1)`), which in a rain-everywhere sheet stays roughly
uniform → diffuse hillslope erosion, soft gullies, NOT branching rivers. The `clamp(w·40,…)` saturates almost
immediately, so it only gates truly-dry cells; it gives no super-linear "big rivers cut far more than small
ones" feedback.

**Real dendritic networks come from one mechanism:** erosion power scales super-linearly with **accumulated
upstream drainage area** A (the stream-power law E ∝ Aᵐ·Sⁿ). Big flow → deeper channel → captures more flow →
branching network (positive feedback). Local depth/velocity is a weak proxy for A. Arc 1 adds real A.

## What Arc 1 phase 1 delivers

1. **Flow accumulation** — a per-cell field A = how much upstream water drains through this cell. Feeds the
   incision law so channels concentrate and branch.
2. **Stream-power incision** — `erode ∝ Aᵐ·Sⁿ` (m≈0.5, n≈1 as starting exponents, tunable) replacing the
   local-only capacity, so rivers carve where drainage concentrates.
3. **Lake/basin handling** — depressions fill to a flat `water_level` (so endorheic basins read as lakes, not
   black pits, and flow routes through/over them) — at least a simple fill; full priority-flood is optional.
4. **The drainage substrate outputs** (the master spec's contract) as first-class, debug-viewable fields:
   `height` (carved), `flow_accum` (A), `channel_mask` (thresholded A + a coarse stream order), `water_level`,
   `sediment`/`material`. These are what Arc 2 (static water) and Arc 3 (live water) consume.
5. **Live lab proof** — the existing erosion lab, judged in motion: dendritic valleys + rivers that drain
   logically, converge, tunable; the `flow_accum`/`channel_mask` debug views show real branching networks.

## The flow-accumulation method (the one real new algorithm)

Flow accumulation on a GPU, race-free, is the crux. Two viable methods (pick in the plan):

- **A) Iterative accumulation (recommended — fits the existing iterated sim):** each step, every cell pushes its
  water (1 + received) to its steepest-descent (or weighted multi-) downhill neighbor; over many iterations A
  converges to true upstream area. Race-free via the SAME flux/gather split already used (compute per-cell
  downhill weights → gather inflow). Cheap per step, converges as the sim runs, naturally couples with the
  water already flowing. No sort, no global pass.
- **B) Single-pass topological accumulation:** sort cells by height, accumulate downhill in order. Exact in one
  pass but needs a GPU sort / serial order — awkward on the local RD, and the sim is iterative anyway.

Recommendation: **A (iterative)** — it reuses the pipe-model's existing race-free gather pattern, converges
alongside the erosion, and is the standard GPU approach. The incision law reads the converging A each step.

## Architecture (extends the existing files — no new system)

- **`shaders/erosion_sim.glsl`** — add: a flow-accumulation pass pair (downhill-weight compute → gather, like
  water 1→2) writing an `accum` buffer; rewrite the erode capacity to stream-power on `accum`; a basin-fill
  contribution to `water_level`; write `channel_mask` from `accum`. Keep the race-freeness rule.
- **`scripts/erosion/ErosionSim.cs`** — add the `accum`, `water_level`, `channel_mask` SSBOs (+ bindings);
  expose them via `ReadDebug`. Extend `Step`'s phase order with the accumulation passes.
- **`scripts/erosion/ErosionParams.cs`** — add stream-power exponents (m, n), accumulation rate, channel
  threshold; keep std430 packing in sync.
- **`scripts/erosion/ErosionLab.cs`** — extend the D-key debug cycle to include `flow_accum` and `channel_mask`
  (the views that prove rivers form). Keep the colour-ramp lit view.
- **Bones untouched:** `field_math.gdshaderinc` / `field_height.glsl` not edited; `--fieldcheck` stays 0 m.

## Gate (the real one — the graveyard test)

1. **Mechanical:** `--erosioncheck` PASS (finite, bounded, roughness < 0.02 — no return of the sawtooth); a new
   assertion that `flow_accum` has real dynamic range (channels >> hillslopes, not uniform) — i.e. the network
   exists numerically before it's judged visually.
2. **USER eye-gate in motion:** valleys are **dendritic** (branching tributaries), rivers **drain downhill**
   into them, the `channel_mask`/`flow_accum` views show a **branching river network** (not uniform speckle),
   it **converges** to a stable good-looking state, and it's **tunable**. This is the "do we get rivers"
   question answered for real.
3. **STOP criterion:** if dendritic rivers can't be reached after fair effort, surface it — don't grind. (The
   substrate + both water tiers depend on a good drainage network, so a bad Arc 1 stops the whole system —
   better to know here.)

## NOT in Arc 1 phase 1 (boundaries)

- NOT the bake (Arc 1 phase 2) or per-chunk synth (phase 3) — phase 1 produces the substrate live in the lab.
- NOT static water rendering (Arc 2) or live water (Arc 3) — they consume the substrate later.
- NOT wiring erosion into the live CDLOD terrain (still standalone lab).
- NOT touching the base field.

## Self-review notes

- **Scope:** one phase (sim core + substrate), single plan. Bake/synth/water are later arcs/phases.
- **Placeholders:** none — the new algorithm (flow accumulation, method A) is named with a concrete race-free
  approach; the incision law (Aᵐ·Sⁿ) + starting exponents are given; the substrate outputs are the master
  spec's named fields.
- **Consistency:** race-freeness rule preserved; substrate contract matches the master; bones untouched.
- **Ambiguity resolved:** river fix = add real flow-accumulation stream-power (not tune local flow); method =
  iterative gather (reuses the existing pattern); gate = dendritic network visible in flow/channel debug views
  + converges, judged live.
