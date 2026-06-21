# Handoff — BUILD AT-2 (GPU atmosphere · aerial perspective). START HERE (new session)

Date: 2026-06-20. You're the **IMPLEMENTOR, Sun/Light lane.** The user flies + judges; you build behind toggles
(default = the approved look) and gate live via `scenes/review.tscn`. This session's job: **build AT-2 aerial
perspective** — the spec + a full, no-placeholder plan are already written and user-approved ("send it"). Don't
re-design; execute the plan.

## Read first (in order)
1. **The plan (authoritative build steps):** `docs/superpowers/plans/2026-06-20-gpu-atmosphere-at2-aerial-perspective.md`
   — Task 1 (aerial froxel LUT) → Task 2 (screen-space composite + the live gate). Full GLSL + C#.
2. **The spec (why/scope/risks):** `specs/2026-06-20-gpu-atmosphere-at2-aerial-perspective-design.md`.
3. **Context:** `docs/HANDOFF.md` §2 posture + §4 run/gotchas · `docs/DECISIONS.md` (newest first — AT-1 history) ·
   parent spec `specs/2026-06-20-gpu-atmosphere-design.md` (AT-1→AT-2→AT-3).
4. **Templates to mirror (read, don't edit the godray ones):** `scripts/lab/AtmosphereCompute.cs` (the LUT seam) ·
   `shaders/atmosphere_skyview.glsl` (raymarch + shared block) · `scripts/lab/GodRaysScreen.cs` +
   `shaders/godray_screen.gdshader` (the clip-space fullscreen-quad screen pass + depth reconstruction).

## Where the lane stands (so you start cold-productive)
- **AT-1 (core sky color) is DONE + DEFAULT-ON** on `experiment/presentation`. Hillaire LUTs
  (transmittance→multi-scatter→sky-view) in a new `AtmosphereCompute` node on the CloudVolume
  `Texture2Drd`/`CallOnRenderThread` seam; `cloud_sky.gdshader` `background()` samples the sky-view LUT. Day sky
  soft-PASSED live ("looks good"); perf +0.2 ms; night hands off to the keyframed night; fantasy/mood `sky_tint`
  recolors the physical sky; horizon seam fixed (continuous below `dir.y=0`). Toggle `GPU atmosphere (AT-1)` /
  `--atmosphere=0`. Self-checks: `--atmoscheck`, `--shadowcheck` (both PASS). Review **key 8** cycles day presets
  (dawn→noon→golden→dusk), camera reframes on first press only. `--review=N` drives a review preset headlessly.
- **AT-2 is the next sub-phase** (one past AT-1's pass — disciplined). Build it, then drive the gate. **Do NOT
  start AT-3** (cloud-lighting) — separate sub-phase, after AT-2 passes.

## The build (execute the plan)
- **T1 — aerial froxel LUT** in `AtmosphereCompute` (`shaders/atmosphere_aerial.glsl` + a 3D texture + camera
  params + a per-frame camera-only recompute path + `--aerialcheck`). No composite yet → zero visual change. Gate:
  `--aerialcheck` PASS, `--atmoscheck`/`--shadowcheck` still PASS, no perf regression. Commit.
- **T2 — screen-space composite** (`AerialPerspective.cs` + `shaders/aerial_screen.gdshader`, mirror GodRaysScreen)
  + the fog handoff + toggle/CLI/control + readiness gate. **This is the gate:** `--auto-shot` A/B (off == built-in
  fog; on = physical distance haze, distant terrain warm at sunset / blue by day, near unaffected, **sky
  unchanged** — verify no double-count), `--profmove` (per-frame 32³ recompute + one fullscreen pass — log cost),
  then the **user's live A/B** via review key 8 + the `aerial perspective (AT-2)` toggle. Tune `aerial strength`
  live. Record verdict in DECISIONS + NEEDS_REVIEW.
- **Execution mode:** recommend **inline** (`executing-plans`) — it's coupled GPU seam + screen-pass work and the
  user gates live; same as AT-1. (Subagent-driven is fine if the user prefers.)

## Build-time things to VERIFY against the live engine (called out in the plan)
- **Texture2Drd vs Texture3Drd:** the aerial LUT is 3D. If `Texture2Drd` won't wrap a 3D RID, use `Texture3Drd`
  for `_aerialRd` + declare `sampler3D` in `aerial_screen.gdshader`. Confirm at first run.
- **Depth convention:** Godot 4 reversed-Z. The plan's sky gate (`d <= eps`) + reconstruction mirror
  `godray_screen.gdshader` — copy its exact depth lines if they differ. If the sky gets haze, the gate is inverted.
- **Std430 matrix:** write `inv_view_proj` as 4× `Vec4(float×4)` (column-major) into the param buffer; the existing
  3 LUT shaders read only the first vec4 of the (now larger) buffer — that's fine.
- **Pass ordering:** `AerialPerspective` RenderPriority 120 < GodRaysScreen 127 (aerial composites first; beams on top).

## Run / gotchas (full list: HANDOFF.md §4 + memory)
- One Godot at a time; kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always
  `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`. Build `dotnet build WG16.csproj` **from the
  project dir** (cwd resets between Bash calls — `cd` first or the build fails "Project file does not exist").
- GLSL compiles at runtime (windowed), NOT at `--import`. Local-RD compute can't run `--headless`; AtmosphereCompute
  uses the **render-thread** RD so it runs windowed. Benign first-frame "Texture binding 1/34 not valid" cloud
  radiance-rebake lines are cosmetic — not yours; the readiness gates avoid sampling unbound atmosphere textures.
- Self-serve shots: `-- --auto-shot=/c/tmp/x.png` (quits after ~1.5 s). Perf: `--profmove` (orbits) — read fps/ms
  from the window title.

## Coordination (shared branch `experiment/presentation` with a Ground/Texture chat)
- Stay in sky/light + your NEW files. **Do NOT edit** `terrain_lab.gdshader`, `TerrainLab.cs`, ground compute,
  `GodRays*`, `shaders/godray*`. Keep the **4 `atmosphere_*.glsl` shared blocks byte-identical** (edit together).
- `git add` your paths explicitly, **never `-A`**. Commit-by-default; **push only when the user asks** (nothing
  pushed this whole arc — confirm with the user before any push).

## Owed (fold into the AT-2 gate session)
- **AT-1 horizon flash:** the user never re-confirmed in motion whether the flash is gone after the fog/seam fixes.
  Ask them to check during the AT-2 gate; if it persists, root-cause the sky-view UV mapping (the `sqrt` el-warp
  near `dir.y=0` is the suspect).

## Key files (this lane)
NEW (yours): `scripts/lab/AtmosphereCompute.cs`, `shaders/atmosphere_{transmittance,multiscatter,skyview}.glsl`
(AT-1) → add `atmosphere_aerial.glsl`, `scripts/lab/AerialPerspective.cs`, `shaders/aerial_screen.gdshader` (AT-2).
EDIT (sky/light): `cloud_sky.gdshader`, `CloudVolume.cs`, `TerrainLabUI.{Lighting,Process,Apply,Clouds,Cli,Review}.cs`,
`TerrainLabUI.cs`, `data/lab_controls.json`. Thin docs: a ROADMAP line + a DECISIONS entry per gate.

## After AT-2 passes
Per the user's sequence: **AT-3 cloud-lighting** (light the volumetric clouds with the physical sky — touches the
cloud shaders), then the **Celestial expansion** (C1 galaxy redesign → C2 bodies → C3 N suns/moons; see
ROADMAP #6 + DECISIONS 2026-06-20). The galaxy was the user's original flagged eyesore, bundled after the atmosphere.
