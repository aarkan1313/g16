# Erosion E1 — Coupled Sim Core + Live Lab (pipe-model hydraulic)

Date: 2026-06-22. Status: SPEC (brainstormed; pillars-driven, user-delegated). Lane: terrain shape.
Parent arc: `2026-06-17-erosion-arc-design.md` (E1 unit). This refreshes E1 for the post-S3 world and
locks the model choice the arc doc deferred.

## Why this exists (the scar)

Erosion is WG16's graveyard: WG15 shipped ~19 erosion versions + 5 water systems, all judged bad. The
diagnosed root cause was a **stack of independently-tuned solvers** (valley_carve → stream_power → hydraulic
→ thermal → alluvial) chained in a bake with **no unifying drainage model** — they fought each other (incision
cut, alluvium refilled, thermal slumped), frozen at an arbitrary balance → incoherent geometry, valleys that
"grew then shrank," dips that didn't drain. **We must not repeat the fighting-solver stack.**

WG16 dropped that entire stack and kept only the base field. E1 re-introduces erosion **differently**: one
coherent coupled model, proven great on a region FIRST, before any bake/stream infra.

## Mandate (pillars)

- **AAA + best-long-term, regardless of cost.** Lead with the most-correct model, not the easiest.
- **Sim quality FIRST.** Prove the coupled sim looks GREAT on one region, watched live in motion, before E2
  bake / E3–E4 streaming. If it can't be made good after a fair effort, that's a real STOP — don't grind 19
  versions like WG15.
- **Ship-correct formulation now.** Even though E1 only runs the lab, build the sim as the model it will SHIP
  as in the infinite world (a coherent COARSE DRAINAGE field that bakes tiny + conditions per-chunk detail),
  so E2/E3 EXTEND it, not re-derive a second erosion implementation. No throwaway lab sim.

## What changed since the parent arc (2026-06-17) — and what didn't

- **CHANGED:** the chunk/streaming system now EXISTS (S3 done 2026-06-22). The arc doc repeatedly says E3/E4
  "need a chunk system WG16 doesn't have yet" — no longer true; the runway for the later units is now real.
- **CHANGED:** terrain is no longer a single re-uploadable `float[]` heightmap mesh; CDLOD chunks sample the
  field live per-vertex via `analytic_h` in `ground.gdshader`. So E1's old seam ("erode the float[], re-upload
  to TerrainLab") doesn't fit the live terrain. → **E1 runs in its own standalone lab** (own scene + mesh),
  NOT wired into the CDLOD terrain. Wiring eroded output into streaming is E3/E4.
- **UNCHANGED:** the build order (sim quality before infra), the anti-WG15 mandate, base-field-untouched
  (skin-not-bones — erosion is a transform DOWNSTREAM of the settled field), eye-gate-in-motion.

## The model — pipe-model hydraulic erosion (the pillars choice)

**One coupled GPU-compute loop**, iterated on a local RenderingDevice. Every step works from ONE shared
water+velocity state, so incision / transport / thermal cannot fight (the WG15 fix, by construction). This is
the Mei et al. "Fast Hydraulic Erosion Simulation" family — the AAA-standard coherent field model.

Per iteration, per cell (all GPU-parallel):
1. **Water input** — add rain (uniform or noise-masked) and/or fixed sources.
2. **Flux / outflow** — virtual "pipes" to the 4 neighbors; outflow flux driven by the height+water gradient
   (shallow-water-ish), clamped so a cell can't drain more water than it holds (stability).
3. **Water update + velocity** — apply net flux to water depth; derive per-cell flow velocity from the fluxes.
4. **Erosion / deposition** — sediment transport CAPACITY = f(velocity, slope); if capacity > carried
   sediment → erode bedrock into suspension (stream-power, capped per step); else deposit. One rule, from the
   same flow state.
5. **Sediment transport** — advect suspended sediment along the velocity field.
6. **Thermal/talus** — slope above a per-material rest angle slumps material downhill (relaxes oversteep
   walls coherently, NOT as a competing pass — it reads the same height).
7. **Evaporation** — water decays so the system converges to a stable state.

**Stability is the central engineering concern** (the "grow-then-shrink" symptom is overshoot done wrong):
bounded per-step erosion amount, CFL-respecting timestep, flux clamping, and **watch it converge live**.

**Why pipe-model over droplet/particle:** droplet erosion is stochastic, hard to make seamless/deterministic
across streamed chunks, and gives no clean coherent drainage field to decompose. Pipe-model is a deterministic
field sim whose water/flow output IS the low-frequency drainage skeleton the infinite-world ship-formulation
bakes tiny + conditions detail on. Pillars → pipe-model.

## Fields (the data — designed for the ship decomposition)

The sim maintains these per-cell fields (GPU storage buffers / textures on the local RD):
- `height` — bedrock elevation (seeded from the base field; this is what erosion transforms).
- `water` — water depth.
- `flux` — 4-direction outflow (the pipes).
- `sediment` — suspended sediment.
- `velocity` — derived flow speed (for capacity + coloring/wetness later).

The **coarse, low-frequency** part of `height-delta` + `flux/velocity` (flow accumulation) IS the drainage
skeleton E2 will bake. E1 produces it as a first-class output (debug-viewable), so the ship decomposition is
built-in from day one, not reverse-engineered.

## Architecture — units (files)

```
base field (FieldCompute float[]  ──►  ErosionSim (GPU compute, pipe-model, local RD, iterated)
seeds the height grid)                  maintains height/water/flux/sediment/velocity buffers
                                        │
                                        ▼
                                 ErosionLab scene — own mesh displaced by the eroded height,
                                 debug views (water/flow/sediment/delta), live knobs, step/run/reset
```

- **`shaders/erosion_sim.glsl`** (NEW) — the compute kernel(s). RD-GLSL, `// @@INCLUDE` splice pattern if it
  needs shared math. Likely a few entry points (flux, water+velocity, erode/deposit, transport, thermal) OR
  one kernel with phases — decided in the plan; the CONTRACT is "one shared state per full step."
- **`scripts/erosion/ErosionSim.cs`** (NEW) — local-RD dispatcher (mirrors `FieldCompute`/`CloudNoiseCompute`:
  `CreateLocalRenderingDevice`, SSBOs, params buffer, dispatch). Methods: `Seed(FieldParams)`, `Step(int n)`,
  `Reset()`, `ReadHeight() → float[]`, `ReadDebug(field) → float[]`, plus a params struct for the knobs.
- **`scripts/erosion/ErosionParams.cs`** (NEW) — the tunable knob set (rain rate, evaporation, capacity
  constant, erosion/deposition rates, talus angle, timestep, region res/size). Data; lab-tunable.
- **`scenes/erosion_lab.tscn`** (NEW) + **`scripts/erosion/ErosionLab.cs`** (NEW) — the standalone lab: a mesh
  displaced by the sim's current height, a camera, a small UI (step / run-N / reset / knob sliders / debug-view
  selector), seeded from the base field. Reuses the FlyCamera + the lab UI patterns. Runs WINDOWED (local RD).
- **(reference, untouched)** `scripts/field/FieldCompute.cs`, `shaders/field_math.gdshaderinc` — seed source;
  the base-field math is NOT edited.

Each file = one job (sim dispatch / params / kernel / lab harness), independently understandable.

## Integration seam

- **Seed:** `FieldCompute.ProducePage(params, origin, spacing, res)` → the initial `height` grid. (The lab
  picks a fixed region + res; no streaming.)
- **Run:** `ErosionSim.Step(n)` iterates the coupled loop on the local RD. NOT in the render loop — it's a
  tool; it can be slow. The lab can step-once, run-N, or run-continuous to watch convergence.
- **Display:** the lab mesh's height comes from `ErosionSim.ReadHeight()` (re-upload to an `Rf` texture or
  rebuild verts) each visible step. Debug views colour by water/flow/sediment/delta.
- **Headless:** local RD compute runs WINDOWED only (memory `headless-no-local-rendering-device`); a numeric
  self-check (mass conservation / no-NaN / drains-downhill) can run windowed via `--auto-shot`-style quit.

## Performance posture

E1 is an OFFLINE TOOL — no per-frame budget. The sim runs as fast as it runs on the local RD; the lab displays
the latest state. (Runtime cost is E3/E4's concern — the whole point of the bake+synth split is the runtime
never runs the sim.) The only "perf" care in E1 is that the lab stays interactive enough to watch (step on a
timer / throttle iterations per displayed frame).

## Testing / validation (NO TDD — GPU/visual)

1. **Mechanical:** builds; sim runs on the region; outputs a valid finite heightfield (no NaN/Inf); a numeric
   self-check (e.g. `--erosioncheck`): water mass conservation within tolerance over a step, height stays
   finite, total height-delta is bounded. Debug views of water/flow/sediment render sane BEFORE judging lit
   terrain (verify the fields, not just the look).
2. **THE gate — user watches it erode LIVE in motion:** do valleys cut LOGICALLY (drain downhill, dendritic
   networks form, NO elevation reversals, NO grow-then-shrink), does it CONVERGE to a stable good-looking
   state, is it tunable to taste. This directly tests the WG15 failure symptoms. Never judged from one still.
3. **Stop criterion honored:** if it can't reach "great" after a fair tuning effort, surface it as a real stop
   — do not grind versions.

## Risk / undo

Highest-risk arc WG16 has attempted. Mitigations: (1) sim quality proven FIRST on one region — if it can't be
made good, we stop before building E2–E4 infra (worthless without a good sim); (2) ONE coherent coupled model
(pipe-model), attacking the diagnosed root cause; (3) standalone lab — the live CDLOD terrain + base field are
untouched, so nothing existing can regress (`git checkout .` reverts; `--fieldcheck` must stay 0 m as a guard
that the base field wasn't touched); (4) eye-gated in motion, watched converge (WG15 judged frozen bakes too
late).

## NOT doing (YAGNI / boundaries)

- NOT porting WG15's solver stack (the thing that failed). One coupled model.
- NOT wiring erosion into the live CDLOD terrain / streaming (E3/E4; needs the bake first).
- NOT the bake (E2) — E1 produces the coarse drainage field as a debug output, but baking/compacting it to
  disk is E2.
- NOT touching the base-field generation math (settled; skin-not-bones).
- NOT real-time-while-flying erosion (E1 is an offline tool).
- NOT droplet/particle erosion (rejected: not ship-correct for the streamed infinite target).

## Build order

E1 (this spec) → judge GREAT in motion → **then** E2 (drainage skeleton bake) → E3 (per-chunk semi-procedural
detail, on the S3 chunk system) → E4 (coarse global pre-solve + streaming). Each its own plan, eye-gated.

## Self-review notes

- **Scope:** one unit (E1 sim core + lab). E2–E4 explicitly OUT. Single implementation plan.
- **Placeholders:** none — model steps, fields, files, seam, gate all specified; the only "decided in the
  plan" is kernel granularity (one phased kernel vs several), a real implementation detail with a stated
  contract (one shared state per full step), not a deferred requirement.
- **Consistency:** pipe-model coherent-coupled is honored throughout; ship-correct (coarse drainage as
  first-class output) is in Fields + Why; standalone-lab seam is consistent with "base field untouched."
- **Ambiguity resolved:** model = pipe-model hydraulic (not droplet); E1 = standalone lab (not wired to CDLOD);
  "great" = valleys cut logically + converges + tunable, judged live in motion; coarse drainage is a built-in
  output so E2 extends rather than re-derives.
