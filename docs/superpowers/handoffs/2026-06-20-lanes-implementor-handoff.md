# Implementor Handoff — the two lanes (Ground/Texture + Sun/Light)

Date: 2026-06-20. **You are taking over as the implementor for both active lanes.** This doc gets you
productive cold. It does not duplicate orientation — read the docs in §0 first.

---

## 0. Orient (read in this order, then come back)
1. `docs/HANDOFF.md` — env, how to run, gotchas, posture. **Non-negotiable; read §2 + §4.**
2. `docs/ROADMAP.md` — the authoritative path: **3 phases** (finish lanes → make it a world →
   climate/elements) + **the discipline rule**.
3. `docs/NEEDS_REVIEW.md` — the eye-gate queue + the **review-scene guide** (keys 1-9).
4. `docs/DECISIONS.md` — the why behind every choice (newest first). Don't re-litigate settled ones.

## 1. Where things stand (the one-paragraph version)
Both lanes are **PAUSED at a combined eye-gate.** Everything built is committed on
`experiment/presentation`, **default-off / approved-look**, so it's all opt-in and reversible. The
lanes went three phases past their last gate (the mistake we're correcting) — so **do not build new
depth until the eye-gate batch is judged.** Backup tag: `backup-doc-reset-2026-06-20`.

## 2. ⚠ THE DISCIPLINE RULE (this is why the reset happened — do not break it)
> **A lane builds at most ONE phase ahead of the last PASSED eye-gate.** When the gate queue holds
> work the user can't yet see, STOP and bank. **Designing ahead (spec/plan) is fine and encouraged;
> *building* ahead is the trap.** Everything behind a toggle defaulting to the approved look. Thin
> docs: a `ROADMAP`/`DECISIONS` line, not a plan-per-lane. The user's live eye is the only look-gate.

## 3. YOUR FIRST JOB — run the combined eye-gate session
Nothing new gets built until this is done. **Drive it for the user** (you set up each item; the user
flies + judges — don't make them click).
- **Tool:** `scenes/review.tscn` — number keys **1-9** jump to each gate item (toggles/mood/time/camera
  from the approved baseline + an on-screen "what to judge" banner). Mapping + per-item criteria +
  caveats are in `NEEDS_REVIEW.md`.
- **Order (upstream → downstream so judgments aren't contaminated):** light (GI decision → sun disc →
  time-of-day) → BRDF clouds-off → ground (GM1/2/3-A) → clouds → god rays → whole-scene AA.
- **After each verdict:** record it in `NEEDS_REVIEW.md` (approved / failed / tweak) **and** a
  `DECISIONS.md` line. Approval unblocks the *next* phase of that lane (and only that one).
- Run windowed, ONE Godot at a time (`HANDOFF.md` §4). AA is not wired — judge it separately.

## 4. Lane A — GROUND / TEXTURE (full scope; finish before Phase B)
Lane roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md` (the 7-layer stack + GM sequence).
- **Built, awaiting the gate (NEEDS_REVIEW 1c):** GM1 palette · GM2 surface-height (Poisson
  normal→height) · GM3-A within-area variation. Specs `specs/2026-06-20-ground-gm2-*`,
  `specs/2026-06-20-ground-gm3-*`. All default-off.
- **Then, one phase past the gate at a time:** GM3 B/C (true material patches via texture arrays, if A
  isn't enough) → GM5 detail (rock/pebble/debris scatter, decals, wetness) → G3 placement realism
  (snow-on-shade, green-in-drainage) → macro color/value.
- **The big one — Terrain depth & hydrology (erosion + water-flow + surface height):** owes a **fresh
  brainstorm → spec → plan → review** (user's explicit call). **Crux:** WG15's erosion failed because
  it wasn't informed by water — so the **water flow/hydrology MODEL co-designs with erosion** (one
  field: carved terrain + flow + sediment together; visible water *rendering* defers to Phase B/C).
  Base to build on: `specs/2026-06-17-erosion-arc-design.md` (already diagnoses the water-coupling root
  cause). Pair it with the **material surface-height / POM** write-up (the "we didn't have height setup"
  fix — GM2 was built fast). **Build gated behind the GM eye-gate** — don't carve terrain before the
  material batch is judged.
- **Key code:** `shaders/terrain_lab.gdshader` (the surface), `scripts/lab/TerrainLab.cs`,
  `scripts/lab/SplatCompute.cs` + `shaders/splat_weights.glsl` (placement bake),
  `scripts/lab/HeightCompute.cs` + `shaders/height_from_normal.glsl` (GM2), `data/ground_palette.json`,
  `data/lab_controls.json` (controls). The compositing-core blend (`splat_blend_mode`) is APPROVED.

## 5. Lane B — SUN & LIGHT (full sky system; finish before Phase B)
Lane roadmap: `specs/2026-06-20-sun-light-system-architecture.md` (3-axis Time × Weather × Grade + celestial).
- **Built, awaiting the gate (NEEDS_REVIEW 3b/3c):** Stage 1 sun disc (`sun_layers()` in
  `shaders/cloud_sky.gdshader`) · Stage 2 decouple (`ComposeLighting` = one writer +
  Time/Weather/Grade(+SunDisc) states) + analytic sun arc + keyframed day color (`time_of_day`). Spec
  `specs/2026-06-20-lighting-decouple-and-time-axis-design.md`.
- **Then, each gated, one phase ahead max:** Stage 3 night + moon (disc/phases/cool moonlight) + star
  field → the **GPU-compute physical atmosphere** → Stage 4 auto day/night cycle + fantasy/exotic.
- **⚠ Atmosphere gotcha (banked so you don't hit the wall):** the Hillaire LUTs must be built on the
  **CloudVolume render-thread `Texture2Drd` seam (`RenderingServer.CallOnRenderThread`), NOT the
  FieldCompute local-RD pattern** — a local-RD texture **cannot be sampled by a material** (see
  `scripts/lab/CloudNoiseCompute.cs` + memory `compute-to-material-callonrenderthread`). Re-spec the
  atmosphere as its own stage; **build it only after Stage 1+2 daylight passes its eye-gate.**
- **Key code:** `shaders/cloud_sky.gdshader` (sky + sun), `scripts/lab/CloudVolume.cs`,
  `scripts/lab/TerrainLabUI.Lighting.cs` (`ComposeLighting`), `TerrainLabUI.Moods.cs`,
  `TerrainLabUI.Apply.cs`/`.Process.cs` (routing + overcast), `scripts/lab/LightingState.cs`,
  `data/lighting_moods.json`.

## 6. Build / verify / run (cheat sheet)
- Build: `dotnet build WG16.csproj`. After a new `.cs`/texture: build → headless `--import` (compile-
  check; **local-RD compute won't run headless** — bake windowed).
- Self-check: `-- --auto-shot=<path>` (PNG), `--profmove` (in-motion profile — always), `--shadowcheck`.
- Programmatic lab control (for review setups etc.): `_byId[id]` → `SetWidgetValue(c, v)` routes to
  shader/scene/cloud; `ApplyMood(int)`, `DriveTime(float)`, `ApplyCloudPreset(int)`. Ids live in
  `data/lab_controls.json`. A `param` naming a missing uniform **silently no-ops** — verify ids.
- Run windowed (ONE Godot at a time; kill strays first):
  `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`

## 7. Don't
- Don't build past the eye-gate (§2). Don't touch the base-field math (settled). Don't add a clipmap or
  mesh-LOD outside the CDLOD roadmap (clipmap killed WG1-15). Don't write 20-page plans (the sprawl we
  just archived). Don't port WG15's erosion solver stack (the graveyard) — one coupled water-driven model.
