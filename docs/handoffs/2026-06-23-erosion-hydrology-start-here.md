# HANDOFF — Erosion + Hydrology · START HERE (new chat)

**Date:** 2026-06-23 · **Branch:** `experiment/presentation` (pushed) · **Lane:** terrain shape + water.
The **graveyard arc** — erosion killed WG1–15. Discipline high: prove each arc great before the next; one
coherent coupled model (NOT a solver stack); base field untouched; eye-gate in motion; STOP if it can't be
made great (don't grind 19 versions).

## TL;DR — what to do next

**Execute the Arc 1 plan: make the erosion sim form real dendritic RIVERS (add flow-accumulation + stream-power),
then user eye-gate it live.**

- **Master roadmap (the durable plan):** `docs/superpowers/specs/2026-06-23-erosion-hydrology-master-design.md`.
- **Arc 1 spec:** `docs/superpowers/specs/2026-06-23-erosion-arc1-substrate-design.md`.
- **Arc 1 plan (execute this):** `docs/superpowers/plans/2026-06-23-erosion-arc1-substrate.md` (4 tasks).
- **Execute DIRECT/inline** (user's standing preference all session; subagent dispatch hit 529s before).

## The system (decided this session — don't re-litigate)

Erosion + water = **ONE drainage substrate, two water tiers.** The substrate = per-cell `height` (carved) +
`flow_accum` + `channel_mask` + `water_level` + `sediment`, produced by the erosion sim, consumed by:
- **Static water** (Arc 2): always-on cheap render of rivers/lakes from the substrate. The 99%-case water.
- **Live water** (Arc 3): optional, toggled, **camera-local bounded** GPU shallow-water seeded by the substrate,
  relaxing to static at its border. Cool but risky → built LAST, one-way (never re-bakes the world).

**Build order:** Arc 1 (erosion+substrate: sim → bake → synth) → Arc 2 (static water) → Arc 3 (live water).
Each its own spec→plan→build→eye-gate. We are at **Arc 1 phase 1** (sim core + substrate, live-lab-judged).

## State of the erosion sim (built, what's right, what's missing)

- **BUILT + race-free:** `shaders/erosion_sim.glsl` (pipe-model coupled loop: water flux→velocity→erode/
  deposit→thermal→transport, 7 phases), `scripts/erosion/{ErosionSim,ErosionParams,ErosionLab}.cs`,
  `scenes/erosion_lab.tscn`. Local-RD compute, windowed. Lab keys: space=run S=step R=reset D=debug-view.
- **The sawtooth was a GPU read-write RACE (fixed)** — erode+thermal read neighbor h and wrote own h same
  dispatch → grid oscillation. Restructured race-free (erode→dh delta; thermal=slump-flux+gather-apply,
  conservative; transport double-buffered). Memory `erosion-e1-pipemodel-race`. **Mechanical gate PASS**
  (`--erosioncheck`: finite, roughness 0.0045 vs >0.02 sawtooth signature).
- **THE GAP (why Arc 1 isn't done):** a logical review found incision scales with LOCAL water depth/velocity →
  diffuse hillslope erosion, **NOT dendritic rivers**. Real rivers need erosion ∝ **accumulated upstream
  drainage area** (stream-power E ∝ Aᵐ·Sⁿ). **Arc 1's job = add a flow-accumulation pass + stream-power
  incision** (the plan's T1–T2), then channel_mask + water_level (T3), then eye-gate (T4).
- **USER EYE-GATE PENDING** — the sim has NOT been judged "rivers look great" yet (user couldn't test). That
  judgment, after the river fix, is the Arc 1 phase-1 gate.

## Critical gotchas (each cost time)

1. **GPU race-freeness rule** — no phase reads neighbor h/s/accum while writing that buffer in the same
   dispatch. Split into compute-flux/weight (read-only) + gather-apply (write own index). A regular
   grid-aligned pattern = suspect a race FIRST, not tuning. (kernel header comment + `erosion-e1-pipemodel-race`.)
2. **Verify the VIEW before judging the data** — the first eye-gate looked worse partly because the lab's lit
   view was `use_textures=true` with no textures bound → black albedo. It's now the colour ramp; debug views
   (D key) false-colour water/sediment/flow. Use the flow_accum/channel_mask views to confirm rivers form.
3. **Stale shader cache** = `app_userdata/"WG16 base field"/shader_cache` (NOT project `.godot`). Clear it if a
   `.glsl` edit seems ineffective. C# needs `dotnet build` after edits.
4. **Local RD = windowed only** (NullRefs headless). `--erosioncheck` quits itself.
5. **std430:** ErosionParams.Pack() byte layout must match the GLSL `Params` block exactly when adding knobs.
6. **Bones untouched:** never edit `field_math.gdshaderinc`/`field_height.glsl`; `--fieldcheck` (terrain_lab)
   must stay `maxAbsDiff=0m`.

## Verification ladder (Arc 1)

1. `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → 0 errors.
2. `<godot_console> --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/erosion_lab.tscn -- --erosioncheck`
   → `EROSIONCHECK: PASS` (finite, roughness<0.02, + flow_accum dynamic range).
3. `<godot_console> ... scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck` → `FIELDCHECK: PASS maxAbsDiff=0m`.
4. USER eye-gate: launch `scenes/erosion_lab.tscn` (no flag), space=run, D→flow_accum/channel_mask. Confirm a
   DENDRITIC branching river network forms, valleys drain logically, converges, tunable. STOP if unreachable.

## Where the rest of the terrain stands (context)

- S1–S3 DONE: pop-free infinite CDLOD streaming (snap-pop fixed, perf pass done, eye-gated). Roadmap
  `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md`.
- Minimal surfacing slice DONE: terrain reads textured (5 materials by height/slope) so erosion is judgeable.
- The FULL surfacing arc (texture arrays/POM/biomes) is LAST, after erosion+water — consumes the substrate.
- Sky/light lane is a separate concurrent lane; stage ONLY erosion/terrain files by explicit path, never `-A`.

## References

- Master: `specs/2026-06-23-erosion-hydrology-master-design.md`
- Arc 1: `specs/2026-06-23-erosion-arc1-substrate-design.md` + `plans/2026-06-23-erosion-arc1-substrate.md`
- Parent arc (kept): `specs/2026-06-17-erosion-arc-design.md` (bake+synth split, anti-WG15).
- E1 origin (superseded scope): `specs/2026-06-22-erosion-e1-sim-core-design.md`.
- Memories: `erosion-e1-pipemodel-race`, `wg16-pillars`, `headless-no-local-rendering-device`,
  `wg16-csharp-stale-dll-gotcha`, `compute-to-material-callonrenderthread`, `terrain-clipmap-killed-wg1-15`.
