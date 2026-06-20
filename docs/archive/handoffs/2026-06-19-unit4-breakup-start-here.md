# Handoff — Ground Unit 4 (Procedural Breakup), start here (new chat)

Date: 2026-06-19. The compositing core shipped this session; Unit 4 is the next material lever. Read this, then the plan, then build.

## The one-liner
The terrain **blend** is now good (weightmap top-2 + height interlock — APPROVED + shipped). The user's remaining ask: the ground doesn't have **enough variety within an area**. Unit 4 adds that — context-driven material breakup (rock on steep faces, soil in cavities/gullies, gritty shelves, shade/flow weathering). **Plan is written and fitted to the new pipeline; your job is to build it, eye-gated, ≤8 ms.**

## Read in this order
1. **The plan (ready to execute):** `docs/superpowers/plans/2026-06-19-ground-unit4-breakup.md` — 7 tasks, concrete code.
2. `docs/HANDOFF.md` §6 START HERE · `docs/NEEDS_REVIEW.md` (item 0 = what shipped; 0b = GI, NOT yours) · `docs/ROADMAP.md` (GROUND arc).
3. Only for mask math: the superseded `plans/2026-06-17-ground-unit4-procedural-breakup.md` (cavity/aspect/flow helpers copy verbatim).

## What shipped this session (don't redo)
- **Compositing core Phase A (blend) — APPROVED + DEFAULT.** Bake emits 7 smooth role weights → two linear Rgba8 weightmaps (`splat_wa`/`splat_wb`, compute bindings 3/4); fragment picks **top-2** roles, blends by a derived **material-height interlock** + organic breakup. `splat_blend_mode` default **1** (legacy index path behind mode 0). User: "it actually looks good."
- **AO** bound into the custom BRDF (`ao_on`, default on) — kept.
- **POM (relief) — DEFERRED**, built behind `pom_on` (default OFF). Derived-roughness height too flat; needs **real height maps** (a deferred arc). Seam (`material_height_uv`, `pom_offset`) is in place. Don't delete.

## Don't touch (other chats own these)
- **GI / SDFGI / scene WorldEnvironment / lighting** — the light/sun chat owns it. We investigated (NEEDS_REVIEW 0b): SDFGI costs ~2.4 ms in motion for a 0.002/255 visible change here (functioning, just redundant on open terrain) → recommended PARK off + re-evaluate when flora/erosion/night land. **Decision handed to that chat — do not change `sdfgi_enabled`/ambient/GI proxy from this thread.**
- **God rays** (`GodRays*`, `shaders/godray*`) — other chat.

## Unit 4 — the key integration facts (why the old plan was rewritten)
- Breakup output goes to compute **binding 5** (0 Heights,1 SplatOut,2 ParamsBuf,3 WOutA,4 WOutB already used).
- `ParamsBuf` is **18** fields now → append 6 → **24 = 96 bytes** in `BuildParams` (currently 80).
- `SplatCompute.Bake` returns `BakeResult(Splat, WeightsA, WeightsB)` → add 4th `Breakup`.
- Fragment default = the **weightmap branch** (`splat_blend_mode==1`): `float sw[7]; splat_weights7(...); top-2 d0/d1; interlock_blend`. **Unit 4 modulates `sw[7]` (boost rock role on steep, soil role in cavities) BEFORE the top-2 loop** — cleaner than the old "re-point dom/sec index", composes with the interlock. Plus cheap post-blend debris/wear tints.
- `SetTerrainField` (for `field`+`rebake` rows) is in **`scripts/lab/TerrainLabUI.Apply.cs`** (not `TerrainLabUI.cs`).

## First moves (new chat)
1. Read the plan. Sanity-launch: `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1` (blend is on by default; fly close/mid to re-baseline).
2. Execute the plan task-by-task (subagent-driven-development or executing-plans). Verify each task: build → import → windowed mask-debug / A/B → `--profmove`. **The look gate is the user flying it (Task 7).**
3. Commit per task with `git add -p`/by path (shared tree). Never `git add -A`.

## Environment (unchanged)
Godot 4.6 mono. Windowed exe bakes/renders; `_console --headless --import` compile-checks only (local-RD compute needs windowed). ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`). Always `--rendering-driver vulkan`. Project: `/c/Wg16/wg-16-project`. Never judge a motion artifact from a still.
