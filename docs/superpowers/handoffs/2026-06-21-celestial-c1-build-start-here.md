# Handoff — BUILD Celestial C1 (procedural fantasy night-sky). START HERE (post-compaction)

Date: 2026-06-21. You're the **IMPLEMENTOR, Sun/Light lane.** The user flies + judges the look; you build behind
toggles (default = approved look) and gate the look live via `scenes/review.tscn`. This session's job: **build C1 —
a procedural, tunable, fantasy night-sky** (replace the uniform Milky Way fog band). Spec + a full plan are written +
user-approved. **The build is mechanically verifiable now; the final "is it cool" LOOK gate is owed** (the user
couldn't gate visuals at handoff time).

## Read first (in order)
1. **The plan (authoritative build steps):** `docs/superpowers/plans/2026-06-21-celestial-c1-procedural-night-sky.md`
   — T1 galaxy bake → T2 nebulae → T3 starfield → T4 tunables → T5 presets → T6 user look-gate. Full GLSL + C#.
2. **The spec (why/scope/perf/risks):** `specs/2026-06-21-celestial-c1-procedural-night-sky-design.md`.
3. **Context:** `docs/DECISIONS.md` (newest first — the sky perf pass + C1 are the top entries) · `docs/ROADMAP.md` #6
   (Celestial C1→C2→C3) · `docs/NEEDS_REVIEW.md` 10 (the original galaxy verdict) + 11 (atmosphere arc, all PASS).
4. **The seam C1 evolves (read, it's the foundation):** `shaders/milkyway_bake.glsl` + the bake bits in
   `scripts/lab/AtmosphereCompute.cs` (`_mwTex`/`_mwRd`/`BakeMilkyWay`/`MwParams`/`SetMilkyWay`, EnsureSets `_mwSet`,
   `_ExitTree` frees) + `shaders/cloud_sky.gdshader` `stars_layer()` (the `night_sky`/`mw_baked` branch + the
   `hash13`/`vnoise3`/`fbm3` noise) — C1 generalizes all of this from "Milky Way band" to "full night sky."

## Where the lane stands (so you start cold-productive)
- **GPU atmosphere arc COMPLETE + default-on, all eye-gated 2026-06-21:** AT-1 physical sky · AT-2 aerial perspective
  (strength 0.4) · AT-3 physical cloud lighting. Atmosphere is **~free** at runtime (LUTs recompute on sun-change only).
- **Sky perf pass DONE (no-visuals window):** profiled the sky = **clouds ~1.8 ms (day) + night stars/galaxy/moon
  ~1.3 ms**; atmosphere ~0. Shipped, all **pixel-diff-verified look-neutral, default-on**: the **Milky Way bake**
  (`milkyway_bake.glsl` — 3 per-pixel fbm3 → 1 texture tap) + **cloud temporal stride 1→2** (converged image identical)
  + (earlier) AT-3 readback throttle + static-camera aerial skip. Net ~0.5-0.8 ms + much steadier frame times — NOT
  the 2-3× the user asked; the bulk lever is **aggressive cloud temporal amortization (stride 4-8)** which trades
  *motion* smear a static pixel-diff can't certify → **staged for the user's motion gate** (Clouds-tab `temporal
  frames` / `--temporal=N`, already wired). Banked for ROADMAP #7 perf pass.
- **C1 is next:** spec + plan written + approved ("send it"). **Build T1-T5, gate the look (T6) with the user.**
  Don't start C2/C3 (separate sub-phases). C1 is the user's flagged eyesore: *"just a fog band, a half-circle all
  the way across."* User direction: **NOT a realistic Milky Way — procedural cool FANTASY, tunable, fix it all
  (band + stars), keep it performant.**

## The build (execute the plan — key shape)
- **C1 evolves the bake**: `milkyway_bake.glsl` → `night_sky_bake.glsl` (galaxy core/dust/star-clouds/2-color gradient
  + ≤4 nebula clouds → `rgba16f` color); `AtmosphereCompute` mw-bake bits generalize (`SetNightSky(GalaxyParams)`,
  `SetNebulae(NebulaParams[])`, `NightSkyTexture`, re-bake on change); `cloud_sky.gdshader` `stars_layer()` samples it
  (1 tap) + a reworked LIVE point-star field. **Perf by design:** static structure baked → runtime = 1 sample
  (cheaper than today); re-bake only on a knob/preset change.
- **Verification per stage (no look judgment — that's T6):** build → night `--time=0` `--nsbaked=1` vs `--nsbaked=0`
  sky-region **pixel-diff within ~0.1** (proves baked==procedural; same method that verified the MW bake) →
  `--profmove --profile=3 --time=0` ≤ the pre-C1 night number.
- **Execution mode:** recommend **inline** (`executing-plans`) — coupled GPU bake↔shader work, the user gates the look;
  same as the atmosphere arc. (Subagent-driven is fine.)

## Verify-at-build / gotchas (called out in the plan + lessons from the arc)
- **Cross-node GPU texture sampling HARD-CRASHES the render device** (the AT-3 lesson) — that's WHY C1 uses the
  CPU/param + bake-to-`Texture2Drd` seam, NOT cross-node compute sampling. Keep the bake noise block **byte-identical**
  to `cloud_sky.gdshader`'s (copy verbatim) so baked==procedural; the pixel-diff is the gate.
- **Bake is on the render-thread RD** (`RenderingServer.GetRenderingDevice()`), RID assigned to a `Texture2Drd` ONCE,
  re-baked on `_mwDirty` (rename `_nsDirty`) on a knob change — never per-frame. CloudVolume is the SOLE `_skyMat` writer.
- **The pixel-diff "diff is the star twinkle":** point-stars animate (`sin(TIME*3)`) so bake-vs-proc night diff has a
  ~0.07-0.1 floor + a few high-max cloud/star-edge pixels — that's NOT a bake error (proven on the MW bake). Compare
  against a proc-vs-proc2 control if unsure.
- **Look constants are gate-tunable**, not final — the plan's noise scales / falloffs / default colors are a working
  v1; the user tunes them at T6.

## Run / coordination (full list: HANDOFF.md §4 + memory)
- One Godot at a time; kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always `--rendering-driver
  vulkan`, absolute `--path /c/Wg16/wg-16-project`. Build `dotnet build WG16.csproj` from the project dir (cwd resets
  between Bash calls — `cd` first). GLSL compiles at runtime (windowed), not at `--import`.
- Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`.
- Night view for review: `--time=0` (or 23) + `--celestial=1` (boosts the MW for the gate). Self-shot:
  `-- --time=0 --auto-shot=/c/tmp/x.png`. Perf: `--profmove --profile=3` prints `PROFILE: avg X fps (Y ms)`.
- **Shared branch `experiment/presentation` with a Ground/Texture chat** (they're doing a per-pixel ground material
  RESET — `ground-v2`). Stay in sky/light + cloud files; **do NOT edit** `terrain_lab.gdshader`, `TerrainLab*.cs`,
  ground compute, `GodRays*`, `shaders/godray*`. `git add` your paths explicitly, **never `-A`**. Commit-by-default;
  **push only when the user asks.**

## Git state at handoff
- HEAD ahead of origin: pushed last at `3961089`; **unpushed**: `7757422` (C1 spec), `89c6deb` (roadmap nebula note),
  `8e563bb` (C1 plan) — plus this handoff. Push when the user asks.
- Backup tags on origin: `backup-atmosphere-arc-complete-2026-06-21` (the clean sky restore point), `backup-pre-at3-cloudlight-2026-06-21`.

## After C1 passes
Per ROADMAP #6: **C2 celestial bodies** (planets · meteors/shooting stars · named-star realism · distant +
animated/parallax nebulae) → **C3 N suns + N moons** (needs a `ComposeLighting` luminary-abstraction refactor; feeds
the atmosphere LUTs — heaviest, last). Each its own spec → plan → gate.
