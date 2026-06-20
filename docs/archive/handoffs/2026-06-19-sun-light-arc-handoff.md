# Handoff: Sun & Light arc — Stage 1 (Sun Disc Polish) ready to implement

Date: 2026-06-19. Branch: `experiment/presentation`. Status: **designed + planned, NOT started.**

## Start here

- **Spec:** `docs/superpowers/specs/2026-06-19-sun-disc-polish-design.md` (APPROVED by user).
- **Plan:** `docs/superpowers/plans/2026-06-19-sun-disc-polish.md` — 6 tasks, TDD-style steps adapted to this
  project's verification (build + windowed auto-shot + visual/numeric; there is NO shader unit-test harness).
- To execute: use `superpowers:subagent-driven-development` (fresh subagent per task, review between) or
  `superpowers:executing-plans`. Implement task-by-task; commit after each (project uses git).

## Context — how we got here

Just finished a long god-ray debugging arc (all resolved + documented in `docs/godray-system-overview.md`
and memory `godray-emission-vs-albedo-rootcause`). The user then wanted to move to **sun & light**. We
reviewed the lighting system (strong foundation: per-mood sun + 4-cascade shadows, SDFGI/SSIL/SSAO, AgX,
aerial+height fog, color grade) and found the **visible sun disc is a flat white dot** (`cloud_sky.gdshader`:
tight `smoothstep` disc + a single `pow(cosSun,220)` glow). Brainstormed → this spec.

## The arc (user wants all of it; we staged it)

- **Stage 1 — Sun disc polish (THIS spec/plan).** Limb-darkened disc + corona + warm atmospheric halo +
  horizon reddening/growth + soft optical-depth cloud occlusion, all in `sky()`, all tunable. Self-contained.
- **Stage 2 — Time-of-day driver (next spec).** One `time_of_day` parameter moves the sun and drives sun
  color/energy + the WHOLE-SKY gradient + fog/aerial + ambient together (this is the "overall light feel").
  The 6 moods fold in — **open design fork:** moods become time-of-day keyframes vs. a separate style axis.
  Stage 1 deliberately kept the halo LOCAL to the sun so it doesn't pre-empt this.
- **Stage 3 — Night & celestial.** Moon(s) + phases + moonlight + night ambient/GI, star field, fantasy
  options (blood moon, colored/multiple suns). Biggest; its own spec.

Art-direction target for the whole arc (user, 2026-06-19): **full-range tunable** — physical default,
every knob exposed (realistic → stylized → fantasy). Same data-driven ethos as the rest of WG16.

## Key technical facts (verified during brainstorming)

- Sun is drawn in `shaders/cloud_sky.gdshader` `sky()`. Clouds composite OVER it via premultiplied
  over-composite `bg*(1-a) + cloud.rgb` (so soft alpha occlusion is already partly there).
- Sky-shader uniforms are owned by `CloudVolume._skyMat` → add a `CloudVolume.SetX()` setter (precedent:
  `SetSunDiscEnergy`, CloudVolume.cs:445).
- Light-tab controls route as `scenef` → `ApplySceneFloat(scene, v)` switch in
  `scripts/lab/TerrainLabUI.Apply.cs` (~line 99); add a `case` that calls the CloudVolume setter; add a
  `lab_controls.json` row (tab `Light`); add a `Set(...)` line in `SyncLightControlsToScene` so the slider
  reflects state. Moods set sun values in `TerrainLabUI.Moods.cs` `ApplyMood`.
- `LIGHT0_DIRECTION` in the sky shader points TOWARD the sun (the existing disc resolves at `cosSun≈1`), so
  `LIGHT0_DIRECTION.y` is sun elevation (1=overhead, ~0=horizon). **Verify the sign in Task 4** — if
  reddening triggers at high sun, flip to `-LIGHT0_DIRECTION.y`.
- Keep `sun_disc_energy` decoupled from `LIGHT0_ENERGY` (overcast dims the directional light, not the disc).

## Verify (windowed; no unit tests for shaders)

Godot: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
```
"<Godot>" --path C:/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- \
  --clouds=0 --godrays=0 --lookatsun --auto-shot=C:/tmp/sun/x.png
```
Clear-sky `--clouds=0 --lookatsun` shows the bare disc. Low sun = `--preset=5` (sun ~14°); high sun = a
high-sun mood. Auto-shot saves at window res; crop to native pixels to judge (memory `wg16-mipmap-fuzz`).

## Not in scope / parked

- Whole-sky atmospheric color, sun travel, time-of-day → Stage 2.
- Moon/stars/night → Stage 3.
- Distant-sky/horizon-gradient rework → separate roadmap item.
- **Clouds read too nebulous/fuzzy** (user flagged 2026-06-19) → an end-of-cloud-work pass
  (density falloff / detail erosion / edge crispness), tracked separately from this arc.

See also memories: [[godray-emission-vs-albedo-rootcause]], [[wg16-launch-absolute-path]],
[[wg16-mipmap-fuzz-gotcha]], [[std430-packing-helper]].
