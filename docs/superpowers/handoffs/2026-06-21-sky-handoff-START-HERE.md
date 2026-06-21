# Sky lane — HANDOFF / START HERE (2026-06-21)

Cold-start continuity for the WG16 sky/light lane. You're the implementor; the user flies + gates the look.

## Read first
1. **This file** (state + the one next action + how to run).
2. **`handoffs/2026-06-21-sky-lane-next-steps.md`** — the full next-steps + the galaxy/nebula **failure analysis**
   + fresh directions + remaining roadmap. (The detail behind this handoff.)
3. `docs/DECISIONS.md` / `docs/ROADMAP.md` (#6 Celestial) / `docs/NEEDS_REVIEW.md` (10, 10b) — newest first.
4. Memory `sun-light-arc` (loaded each session) — the arc summary.

## Where the lane stands
**Built + gated (done):** Stage 3 night (moon/moonlight/stars) · #2 Clouds (CO-1..CO-4) · #3 GPU atmosphere
(AT-1 sky / AT-2 aerial / AT-3 cloud-light, default-on) · #4 Stage 4 (auto cycle + fantasy presets).

**This session's outcome:**
- **C1 procedural galaxy/nebula → REJECTED** ("it doesn't work"). Built + iterated live many rounds; killed it.
  Night sky = **moon + tuned starfield** (stars −60% count / −70% slower blink = PASS). Galaxy/nebula code parked
  (off at `mw_brightness` 0 / `neb_count` 0, **zero per-frame cost**), deletable.
  **WHY it failed (the key lesson):** it used **fbm/ridged 3D noise — the same noise the clouds use — so it always
  read as clouds, not space.** Localizing/sharpening/ridging never escaped that.
- **C2 meteors / shooting stars → PASS, default-on.** `cloud_sky.gdshader` `meteors()` — 2 TIME-hashed channels,
  occasional ~1 s streaks with a glowing head + trail + per-meteor color variety; gated night×horizon; free when
  idle. 7 Night-tab knobs (`meteors *`) + `--meteordebug`.

## THE next action (user's call: "start over on the nebulae and galaxy")
**Galaxy/Nebula v2 — a FRESH design from scratch.** Do NOT iterate the rejected procedural-noise approach.
- **Start with a brainstorm** (superpowers:brainstorming) → spec → plan → build → gate.
- **First decide a reference look** (agree on a specific target image / direction) so we don't iterate blind like
  C1 did. Candidate directions (in the next-steps doc): painted/authored galaxy-nebula **texture** sampled as a sky
  layer · a **structured non-noise generator** (spiral arms / sharp emission + embedded stars) · scattered
  **distant-galaxy billboards** · or **drop it** (moon+stars+meteors may be enough).
- Reusable infra: the render-thread bake seam (`AtmosphereCompute` `_mw*` / `night_sky_bake.glsl` / `Texture2Drd`);
  the orphaned night-sky preset picker (`TerrainLabUI.NightSkyPresets.cs` + `data/night_sky_presets.json` +
  `--nspreset`); the parked `galaxy_color`/`nebula_color` in `cloud_sky.gdshader` (replace/delete when v2 lands).

**After / alongside:** meteor presets (optional, small) · C2 planets · #5 shadow & lighting pass · #7 perf pass (last).

## Run / coordination
- One Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always `--rendering-driver vulkan`,
  absolute `--path /c/Wg16/wg-16-project`. Build: `dotnet build WG16.csproj`. Exe:
  `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`.
- **Night review = key 2** ("night sky — stars + moon + meteors"); drives night directly (robust to the
  ground-strip `Apply.cs` change). `--review=2` runs it headlessly; `--meteordebug` forces a meteor for capture.
  For shots, crop to the sky region (lab panel + terrain fill the rest of the frame).
- **Shared branch `experiment/presentation` with the GROUND chat** — they STRIPPED the material/surfacing system
  to a placeholder 2026-06-21 (deleted Splat/Height/GroundReview; it churned/broke the shared C# build + `Apply.cs`
  mid-session — sky work is shader-isolated so it survived). **Stay in sky/light + cloud files; `git add` paths
  explicitly, NEVER `-A`.** Commit-by-default; push when asked.

## Git
Pushed: `origin/experiment/presentation` at **b26a4c2**, **0 unpushed**. Today's sky commits: `a43515a … b26a4c2`
(C1 build+iterate, C1 close to moon+stars, C2 meteors, docs).
