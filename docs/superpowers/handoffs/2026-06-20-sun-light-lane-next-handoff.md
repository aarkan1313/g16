# Handoff — WG16 SUN & LIGHT lane, continue after Stage 3

Date: 2026-06-20. You're the **IMPLEMENTOR for the Sun/Light (full sky) lane.** Stage 3 (Night &
Celestial) is COMPLETE + eye-gated; the next three items are SPEC'd and ready to build. I (user) fly +
judge visuals; you drive setup, build behind toggles, and gate with me. Drop this prompt into a fresh chat.

## Read first (authoritative, in order)
1. `docs/HANDOFF.md` — §2 posture (PILLARS: quality = performance = AAA = long-term-best), §4 how-to-run + gotchas, commit-by-default convention.
2. `docs/ROADMAP.md` — Phase A ▸ ☀️🌙 FINISH THE FULL SKY SYSTEM (items #2/#3/#4 now SPEC'd; build order + deferrals).
3. `docs/DECISIONS.md` (newest first) — Stage 3 history; don't re-litigate.
4. `docs/NEEDS_REVIEW.md` — eye-gate queue; **item 8 = the two owed Stage-3 visual checks**.
5. The three specs (below).

## THE DISCIPLINE RULE (non-negotiable — caused the WG1-15 reset)
Build **AT MOST ONE sub-phase ahead of the last PASSED eye-gate.** Designing ahead (spec/plan) is fine and
done; **building ahead is the trap.** Everything behind a toggle defaulting to the approved look. Thin docs
(a ROADMAP/DECISIONS line, not plan-sprawl). **The user's live eye is the only look-gate** — never judge a
motion artifact from a still; drive `scenes/review.tscn` for the user (set it up; they fly + judge).

## Where the lane stands (Stage 3 = DONE)
- **Built + gated live (all PASS):** 3a night sky (24h arc, sun-below-horizon, tunable darkness/floor) ·
  3b moon (textured/cratered, phases + terminator, cool halo, cloud-occluded) · 3c moonlight (2nd cool
  shadow-casting directional, gated night×moon-up×phase) · 3d stars + Milky Way (3D-noise, twinkle/rotation/
  fade). All on a **Night lab tab** (~24 knobs) + **6 celestial presets** (`--celestial=N`). Also: fog-wash
  night-grade, `L` inspection light, sun-disc limb polish, live moon/moonlight color pickers, MoonLight
  deferred-add startup fix. Spec `specs/2026-06-20-night-and-celestial-design.md`, plan
  `plans/2026-06-20-night-and-celestial.md`.
- **Owed (await user eye, not blocking):** NEEDS_REVIEW item 8 — celestial-preset dropdown + color pickers
  live-check (built + self-checked, never live-gated).

## NEXT — three SPEC'd items (each: writing-plans → build sub-phase 1 → eye-gate → next)
- **#2 Clouds overhaul** `specs/2026-06-20-clouds-overhaul-design.md` — CO-1 vertical realism (height
  profile in decks) → CO-2 types (**cirrus as a 2D layer in cloud_sky.gdshader** + stratus shape-mode;
  cumulus preserved) → CO-3 anti-repeat/horizon → CO-4 presets. **Weather-axis tie-in DEFERRED.**
- **#3 GPU atmosphere** `specs/2026-06-20-gpu-atmosphere-design.md` — Hillaire LUTs on the **CloudVolume
  `Texture2Drd`/`CallOnRenderThread` seam, NOT FieldCompute** (memory `compute-to-material-callonrenderthread`).
  Toggle **default OFF = approved keyframed look until it wins an A/B gate.** AT-1 sky color → AT-2 aerial →
  **AT-3 cloud-lighting (do AFTER #2 lands — touches the raymarch).**
- **#4 Stage 4** `specs/2026-06-20-stage4-cycle-and-fantasy-design.md` — ST4-1 auto day/night cycle
  (**buildable now, no deps**) → ST4-2 fantasy/exotic cross-system presets. **Multiple suns/moons DEFERRED.**

**Suggested order:** ask the user. #4 ST4-1 (auto cycle) is the smallest/no-dep quick win; #2 is the
richest; #3 is deepest and #3-AT3 needs #2 first. Don't build #3-AT3 before #2.

## Key code (this lane owns these)
`shaders/cloud_sky.gdshader` (sky + sun + moon + stars; cirrus 2D goes here) · `shaders/cloud_raymarch.glsl`
(+ `cloud_shadow.glsl` — keep fields 0-11 byte-identical) · `scripts/lab/CloudVolume.cs` +
`scripts/lab/CloudLayers.cs` (deck system; `SetLayers` = preset-author seam) · `scripts/lab/
TerrainLabUI.Lighting.cs` (ComposeLighting one-writer, DriveTime, EnsureMoonLight) · `.Apply.cs` (control
routing incl. `scenecolor`) · `.CelestialPresets.cs` / `.SunPresets.cs` (preset pattern to mirror) ·
`LightingState.cs` (TimeState/MoonState/StarsState) · `data/{time,celestial,sun,cloud}_presets.json`,
`data/cloud_layers.json`, `data/lab_controls.json` (Night tab).

## Gotchas / run
- One Godot at a time; always `--rendering-driver vulkan`; kill strays
  (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`). After new .cs/texture: `dotnet build WG16.csproj`
  → headless `--import`. Local-RD compute can't run `--headless` (bake windowed).
- Per-frame compute→material via `CallOnRenderThread`, assign the `Texture2Drd` RID ONCE (the #3 wall).
- Run: `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`. Self-check
  `-- --auto-shot=<path>` + `--profmove`. Review keys / `--nightgate=1` night states in `data/review_night.json`.
- Benign radiance-rebake log lines (`not a valid texture`/`us is null`) on any lighting change are cosmetic
  (documented in CloudVolume + DECISIONS) — NOT a bug; don't chase.

## Coordination (a Ground/Texture chat shares this branch)
- Shared branch `experiment/presentation`; commit-by-default (clean scoped commits), **push only when the
  user asks.** Stay in sky/light files; **do NOT edit ground files** (`terrain_lab.gdshader`, `TerrainLab.cs`,
  Splat/Height compute, `ground_palette.json`). Shared files (`lab_controls.json`, `TerrainLabUI` core,
  `DECISIONS.md`/`ROADMAP.md`): small + additive, stage only YOUR files when committing (the other chat's
  edits intermingle — `git add` your paths explicitly, never `git add -A`).

## Start by
Reading the docs above, then ask the user: eye-check the owed Stage-3 visuals (NEEDS_REVIEW 8) first, or
pick #2 / #3 / #4 to build — then `writing-plans` → sub-phase 1 → drive the eye-gate. One phase past the
last pass. Go.
