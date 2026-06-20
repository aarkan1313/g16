# Resume prompt — SUN / LIGHT lane implementor (continue the sky system)

Paste the block below into a fresh chat to resume the Sun/Light lane. (It was paused at the Night &
Celestial spec so the Ground/Texture chat could work — that's a separate chat; stay in your lane.)

---

You're taking over as the IMPLEMENTOR for the WG16 SUN & LIGHT lane (the full sky system), RESUMING a
paused lane. I'll be in the loop to fly and judge visuals — you drive the setup, I give verdicts.

First, read these in order (authoritative; don't skip):
- docs/superpowers/handoffs/2026-06-20-lanes-implementor-handoff.md (read ALL; focus §5 = your lane)
- docs/HANDOFF.md (§2 posture, §4 how-to-run + gotchas; note the 2026-06-20 "commit by default" convention)
- docs/ROADMAP.md (3-phase path + discipline rule; Phase A ▸ SUN & LIGHT ▸ the ☀️🌙 "finish the full sky
  system" section — your ordered backlog, with the ⏸ PAUSED marker)
- docs/DECISIONS.md (newest first — the why behind every settled call; don't re-litigate)
- specs/2026-06-20-sun-light-system-architecture.md (master architecture: 3-axis Time×Weather×Grade + Celestial)
- specs/2026-06-20-night-and-celestial-design.md (YOUR NEXT BUILD — Stage 3, spec written, NOT built)
- specs/2026-06-20-sun-surface-shader-and-presets-design.md + specs/2026-06-20-lighting-decouple-and-time-axis-design.md (done, for context)

THE DISCIPLINE RULE is non-negotiable: build AT MOST ONE phase ahead of the last PASSED eye-gate.
Designing ahead (spec/plan) is fine; BUILDING ahead is the trap that caused the project reset. My live
eye is the only look-gate. Everything stays behind a toggle defaulting to the approved look. Thin docs.

WHERE THE LANE STANDS (all on experiment/presentation, committed):
- **Daylight is DONE + eye-gated (live):** GI/SDFGI decision (default SDFGI off + GI proxy off — sharp
  shadows, no cascade box); Stage 1 sun disc; Stage 2 decouple (`ComposeLighting` = one writer +
  Time/Weather/Grade states) + analytic sun arc + keyframed day color (`time_of_day`); **sun SURFACE
  shader + sun presets** (textured disc, default preset `realistic_midday` ON — perf ~free, gated to
  disc pixels); BRDF regression (6); clouds feature review (5); god rays (3). All PASS.
- **Bugs fixed this arc:** cloud error-burst on preset switch (clouds now toggle via the `cloud_enabled`
  uniform; env.Sky never swapped — cloud_sky installs once and stays, so clouds-off shows the cloud-shader
  sky); sun cloud-occlusion is now PER-PIXEL (large discs no longer vanish when a cloud touches centre).
- **PAUSED here:** Stage 3 **Night & Celestial** — spec written (`specs/2026-06-20-night-and-celestial-design.md`),
  NOTHING built yet.

YOUR FIRST JOB — resume Stage 3 (Night & Celestial). It's decomposed into 4 eye-gated sub-phases in the
spec: **3a** night sky (24h arc, sun below horizon, tunable darkening + ambient floor) → **3b** moon disc
(textured via the sun-surface system, phases + terminator, halo) → **3c** moonlight (2nd cool directional
light, shadows, gated to night) → **3d** stars + Milky Way (procedural, twinkle/rotation/fade, presets).
Night must be TUNABLE across the range (dark-scary ↔ moonlit-bright), physical default, presets +
super-tunable (the user's explicit ask). Steps: (1) confirm the spec with me (I may not have reviewed it
yet — ask), (2) invoke writing-plans to plan it (phased 3a–3d), (3) build 3a, gate it with me, then 3b,
etc. Drive each gate via scenes/review.tscn (extend a key for night/time scrub) — one Godot at a time,
--rendering-driver vulkan, kill strays first. Record each verdict in NEEDS_REVIEW.md + a DECISIONS.md line.

AFTER Stage 3, continue the ☀️🌙 backlog IN ORDER, each its own brainstorm→spec→plan→gate, one phase past
the last pass:
2. **Clouds overhaul** (the user is keen): cirrus/wispy-striated "lines" clouds (not represented today),
   more shapes & types, more/higher elevation bands + HEIGHT RANGES WITHIN a band (the "decks are flat
   slabs" vertical-realism gap — memory `cloud-deck-vertical-realism`), weather-axis tie-in, stronger
   anti-repetition (still tiles sometimes), + presets/tunability.
3. **GPU-compute physical atmosphere** (the unifying sky-color pass): Hillaire LUTs — ⚠ MUST be built on
   the CloudVolume render-thread **`Texture2Drd` seam (`RenderingServer.CallOnRenderThread`), NOT the
   FieldCompute local-RD pattern** — a local-RD texture cannot be sampled by a material (memory
   `compute-to-material-callonrenderthread`). When it lands it should supersede the keyframed night/day
   color script (kept in the same structure so it swaps cleanly).
4. **Stage 4** auto day/night cycle (play/pause/speed) + fantasy/exotic (blood/colored/multiple bodies).
5. **Shadow & Lighting pass**: CSM/cascade tuning, contact + soft (PCSS) shadows, the proxy-on cheap-shadow
   perf lever (~2.8 vs ~4.7 ms), SSIL re-check. Pairs with Stage-3 moonlight shadows.

KEY CODE (your lane): shaders/cloud_sky.gdshader (sky + sun disc/surface + where moon/stars go),
scripts/lab/CloudVolume.cs (cloud compute + sky material + sun setters), scripts/lab/TerrainLabUI.Lighting.cs
(`ComposeLighting`, `DriveTime` — extend to 24h here), .Moods.cs, .Apply.cs/.Process.cs (routing/overcast),
LightingState.cs (Time/Weather/Grade states — add Celestial), TerrainLabUI.SunPresets.cs (preset pattern to
mirror for celestial presets), data/{lighting_moods,time_presets,sun_presets}.json, data/lab_controls.json.

GOTCHAS: one Godot at a time (two contend → grey hang; `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`);
always `--rendering-driver vulkan`; first run after new .cs/texture → `dotnet build` then headless `--import`;
local-RD compute can't run `--headless` (bake windowed); per-frame compute→material via `CallOnRenderThread`,
assign `Texture2Drd` RID once (not a CompositorEffect); hand-packed std430 drifts → use `Std430Writer`.
Self-check with `-- --auto-shot=<path>` + `--profmove` (always profile in motion), then MY live eye.

COORDINATION: the GROUND/TEXTURE lane is being worked in ANOTHER chat. Stay in YOUR sky/light files; do
NOT edit ground files (shaders/terrain_lab.gdshader, TerrainLab.cs, SplatCompute.cs/splat_weights.glsl,
HeightCompute.cs/height_from_normal.glsl, data/ground_palette.json). Both lanes share
experiment/presentation + lab_controls.json / TerrainLabUI core — keep edits there small + additive and
tell me. Commit finished work to experiment/presentation as you go (convention: don't ask each time);
push only when asked. God-ray files (GodRays*/shaders/godray*) were a separate chat — coordinate before editing.

Start by reading the docs above (especially the Night & Celestial spec), then tell me your understanding
of the lane's current state and propose how we run Stage 3 — beginning with whether the Night spec needs
my review or is good to plan. Then we go.
