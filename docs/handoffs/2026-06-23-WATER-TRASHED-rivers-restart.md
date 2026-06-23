# HANDOFF — Water arc TRASHED, restart on REAL RIVERS · START HERE

**Date:** 2026-06-23 (end of a long session) · **Branch:** `experiment/presentation` · **Lane:** terrain water.
This SUPERSEDES `2026-06-23-erosion-hydrology-start-here.md`. Read this first.

## ⛔ USER VERDICT: trash the water work, start over. They want REAL RIVERS.

Direct quote: *"I dont like it. lets trash water and start over, i dont like the sea level thing, im not seeing
aboveground water still, i want real rivers and stuff."*

So: **the erosion-free WATER arc is rejected.** Do NOT resume it. Specifically rejected:
- **The global SEA-LEVEL approach** — user dislikes "the sea level thing." A flat ocean plane flooding terrain
  below a Y value is NOT what they want for this (mountain/high-terrain) world.
- **The infinite sea render** (`InfiniteWater.cs` + `infinite_water.gdshader`) — never read right aboveground.
  Long debugging of "hard to see / clear / upside-down" (a view-space NORMAL bug + transparency/depth) — the
  user still wasn't seeing convincing aboveground water and lost confidence in the approach.
- The lake-as-water-plane + significance-cap machinery — over-engineered for what they want.

**What the user ACTUALLY wants: real rivers (and "stuff") — visible, flowing water across the terrain.** Not a
sea level. Rivers are the thing. (Earlier in the session they also liked the IDEA of lakes/tarns, but the
headline ask now is RIVERS.)

## The whole arc's history (so you don't repeat it)

The terrain-water problem has now killed MANY approaches this session, all eye-gate-rejected by the user:
1. **Pipe-model hydraulic erosion** (stream-power sim) → terraced/thread-rivers. Rejected. (`shaders/erosion_sim.glsl`, `scripts/erosion/`.)
2. **Structure-first drainage carve** (coarse graph → analytic valley carve; MFD + Chaikin + Wyvill blend +
   erosion polish; deep-researched twice) → still "bad"/artifact-ridden. Rejected. (`scripts/hydrology/{DrainageGraph,ValleyCarve,...}`, `shaders/valley_carve.glsl`.)
3. **Erosion-FREE water** (sea level + significant lakes + thin rivers + WaterCarve bowls; CoarseWorldWater map;
   infinite sea on CDLOD) → user dislikes sea-level, didn't see aboveground water. REJECTED (this handoff).

Memories with the full forensics: `clean-river-terrain-pipeline`, `river-valley-carving-method`,
`erosion-free-water-arc`, `erosion-arc1-rivers-built`, `erosion-e1-pipemodel-race`. The graveyard discipline
(`wg16-pillars`, `terrain-clipmap-killed-wg1-15`) is in FULL force: this is the project's hardest, most-failed
lane. **Before building anything, BRAINSTORM the river approach fresh with the user — do not assume.**

## What's committed (all on experiment/presentation) — keep or trash, user's call

- **Drainage graph infra (REUSABLE, probably keep):** `scripts/hydrology/{CoarseField,DrainageGraph,WaterBodies,
  CoarseWorldWater,HydrologyParams,HydrologyPipeline}.cs`. The drainage network itself (where rivers GO) was
  always sound + tile-coherent (gates passed); the FAILURES were always the CARVE/RENDER, not the routing. A
  fresh river approach can likely reuse `DrainageGraph`/`CoarseWorldWater` for river PATHS.
- **Rejected render/carve (candidates to trash):** `InfiniteWater.cs`, `shaders/infinite_water.gdshader`,
  `WaterCarve.cs`/`shaders/water_carve.glsl`, `WaterRenderer.cs`, `water_surface.gdshader`, `ValleyCarve.cs`/
  `valley_carve.glsl`, `scripts/erosion/*` + `erosion_sim.glsl`. Lab: `HydrologyLab.cs`. Scene change:
  `terrain_lab.tscn` got an `InfiniteWater` node (id 4) — REMOVE it if trashing the sea.
- Specs/plans: `specs/2026-06-23-erosion-free-water-design.md` (+ revision), `plans/2026-06-23-erosion-free-water.md`.
- **`--fieldcheck` is 0m throughout** — the base field / CDLOD terrain was NEVER damaged. That lane is safe.

## Hard-won lessons the next session MUST honor

1. **The LENS keeps killing us.** We judged water through: a debug colour-ramp, hand-rolled hillshades,
   single-angle captures, a sea level set high "for visibility," and a one-giant-basin lab region. EVERY time it
   misled. For rivers: judge through a REAL water render, in motion, in the actual terrain, from NORMAL play
   viewpoints — and accept the user is the only eye-gate that counts.
2. **The terrain base field is GOOD** (smooth, natural, never complained about). The problem has ALWAYS been
   adding water/carving to it. Bias toward approaches that touch the base field as LITTLE as possible.
3. **Real rivers need a CHANNEL** (water on a bare slope just sheets off). The unresolved question: how does a
   river get its channel without the landscape-wide carve that failed? Options not yet tried well: a *thin*
   river-only carve (started, not eye-gated), rivers as flowing-water RIBBON MESHES that hug the terrain
   surface (no carve at all — a flow-mapped strip following drainage centerlines), or splatted river textures.
   **Ribbon-mesh rivers that follow drainage centerlines on the UNMODIFIED terrain may be the untried winner** —
   no carve, no sea level, just visible flowing water where rivers go.
4. **STOP grinding.** Per the pillars + the user's repeated "bad": brainstorm the new river approach, get ONE
   approach agreed, build the MINIMUM to eye-gate it in the real world, and if the user says bad, STOP and
   re-brainstorm — don't iterate 8 times.

## Recommended first move for the next session

**Brainstorm "real rivers, no sea, minimal terrain touch" with the user.** Lead candidate to propose: rivers as
**flow-mapped ribbon meshes following the existing (good, tile-coherent) drainage centerlines, laid on the
unmodified terrain**, with a nice flowing-water shader — possibly + small lakes/tarns at basin lows. No global
sea, no landscape carve. Get the user's buy-in on the APPROACH before any code. Reuse `DrainageGraph`/
`CoarseWorldWater` for the river paths only.

Branch is clean (rejected uncommitted shader edit reverted; Godot killed). Nothing is mid-flight.
