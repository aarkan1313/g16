# Kickoff prompt — GROUND / TEXTURE lane implementor

Paste the block below into a fresh chat to start the Ground/Texture lane. (The Sun/Light lane is paused
at the Night & Celestial spec; see `ROADMAP.md` ☀️🌙.)

---

You're taking over as the IMPLEMENTOR for the WG16 GROUND / TEXTURE lane. I'll be in the loop to fly and
judge visuals — you drive the setup, I give verdicts.

First, read these in order (authoritative; don't skip):
- docs/superpowers/handoffs/2026-06-20-lanes-implementor-handoff.md (read ALL; focus §4 = your lane)
- docs/HANDOFF.md (§2 posture, §4 how-to-run + gotchas; note the 2026-06-20 "commit by default" convention)
- docs/ROADMAP.md (the 3-phase path + the discipline rule; Phase A ▸ GROUND/TEXTURE section)
- docs/NEEDS_REVIEW.md (the eye-gate queue — your item is 1c; + the review-scene guide, keys 1–9)
- docs/DECISIONS.md (newest first — don't re-litigate settled calls)
- specs/2026-06-20-ground-roadmap-to-aaa-design.md (your lane roadmap: the 7-layer stack + GM sequence)
- specs/2026-06-20-ground-gm2-real-height-maps-design.md and
  specs/2026-06-20-ground-gm3-within-area-variation-design.md (the built-but-ungated GM work)

THE DISCIPLINE RULE is non-negotiable: build AT MOST ONE phase ahead of the last PASSED eye-gate.
Designing ahead (spec/plan) is fine; BUILDING ahead is the trap that caused the reset. My live eye is the
only look-gate. Everything stays behind a toggle defaulting to the approved look. Thin docs.

YOUR FIRST JOB — drive the GROUND eye-gate with me, via scenes/review.tscn (one Godot at a time,
--rendering-driver vulkan; kill strays first). The LIGHT lane is now SETTLED (all its gates passed), so
ground is judged next under the settled light. Your presets are keys **3** (GM1 palette — press 3 again to
A/B palettes), **4** (GM2 real height + POM), **5** (GM3-A within-area variation). These three are the
combined ground batch (NEEDS_REVIEW **1c**) — built, committed, all default-off/approved-era look, NONE
user-approved yet. Gate them TOGETHER (they share the surface): fly close/mid on a VARIED region
(cliffs/peaks too, not just the warm basin) under neutral light. NOTE: the review presets no longer move
the camera (a recent change) — they only set toggles; YOU fly to a real cliff/slope. Record verdicts in
NEEDS_REVIEW.md + a one-line DECISIONS.md entry.

CURRENT LIGHT BASELINE you'll be judging ground under (set by the Sun/Light lane, don't change these):
- SDFGI OFF + GI proxy OFF by default → sharp detail-mesh shadows (no SDFGI cascade box). Toggle `GI
  (SDFGI)` on the LIGHT tab / `GI/shadow proxy` on the Debug tab only to A/B; leave them off for ground.
- The everyday sun now has a textured surface (sun preset `realistic_midday`, default on) and clouds-off
  renders the cloud-shader sky. These are approved; judge ground, not the sky.

AFTER the gate: act on results, then continue the ground lane IN ORDER, one gated phase at a time —
GM3 B/C true material patches (texture arrays, if GM3-A isn't enough) → GM5 detail (rock/pebble/debris
scatter, decals, wetness) → G3 placement realism (snow-on-shade, green-in-drainage) → macro color/value.
⚠ The BIG one — terrain depth & hydrology (erosion + water-flow + surface height) — owes a fresh
brainstorm → spec → plan → review (user's explicit call). CRUX: WG15's erosion failed because it wasn't
informed by water, so the water-flow/hydrology MODEL co-designs with erosion (one field: carved terrain +
flow + sediment together; visible water rendering defers to Phase C). Base: specs are in the active set;
start from why the old erosion failed. Build it ONLY after the material batch is judged.

COORDINATION: the SUN/LIGHT lane is PAUSED at its Night & Celestial spec (built nothing new; daylight
shipped). Stay in YOUR lane's files — shaders/terrain_lab.gdshader, scripts/lab/TerrainLab.cs,
scripts/lab/SplatCompute.cs + shaders/splat_weights.glsl, scripts/lab/HeightCompute.cs +
shaders/height_from_normal.glsl, data/ground_palette.json. Do NOT edit the sky/cloud/lighting files
(cloud_sky.gdshader, CloudVolume.cs, TerrainLabUI.Lighting/.Moods, LightingState.cs, sun/celestial
presets — other lane). Both lanes share experiment/presentation + lab_controls.json / TerrainLabUI core;
keep edits there small + additive and tell me if you must touch them. Convention: commit finished work to
experiment/presentation as you go (don't ask each time); push only when asked.

Build/run cheat sheet: `dotnet build WG16.csproj`; after a new .cs/texture → headless `--import`
(compile-check; local-RD compute won't run headless — bake windowed). Self-check: `-- --auto-shot=<path>`,
`--profmove` (always profile in motion). Launch (one Godot at a time):
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`.

Start by reading the docs above, then tell me your understanding of the ground/texture lane's current
state and propose how we run the ground review (keys 3/4/5). Then we go.
