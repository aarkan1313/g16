# Handoff — WG16 SUN & LIGHT lane, after the full sky system landed

Date: 2026-06-20 (continuation of `2026-06-20-sun-light-lane-next-handoff.md`). You're the **IMPLEMENTOR for
the Sun/Light (full sky) lane.** The user flies + judges visuals; you drive setup, build behind toggles, and
gate each phase live. Drop the short prompt (below) into a fresh chat.

## Read first (authoritative, in order)
1. `docs/HANDOFF.md` §2 posture (PILLARS: quality = performance = AAA = long-term-best) + §4 run/gotchas.
2. `docs/ROADMAP.md` ▸ `☀️🌙 FINISH THE FULL SKY SYSTEM` — the status box shows what's done + remaining.
3. `docs/DECISIONS.md` (newest first) — this session's clouds + Stage-4 history; don't re-litigate.
4. `docs/NEEDS_REVIEW.md` — eye-gate queue (esp. **item 10 = galaxy/Milky Way review, owed**).
5. Specs for remaining work: `specs/2026-06-20-gpu-atmosphere-design.md` (#3, the big one).

## THE DISCIPLINE RULE (non-negotiable — caused the WG1-15 reset)
Build **AT MOST ONE sub-phase ahead of the last PASSED eye-gate.** Everything behind a toggle defaulting to the
approved look. **The user's live eye is the only look-gate** — never judge a motion artifact from a still; drive
`scenes/review.tscn` for them (set toggles; they fly + judge). Thin docs: a ROADMAP line + a DECISIONS entry.
(This session built a few sub-phases ahead at the user's explicit direction — all default-off/opt-in + banked.)

## Where the lane stands — THE FULL SKY SYSTEM IS BUILT END-TO-END (on `experiment/presentation`)
All gated/soft-gated live; startup default look unchanged (new features opt-in):
- **Stage 1 sun disc · Stage 2 decouple + time-of-day · Stage 3 Night & Celestial** (moon/phases/moonlight/
  stars+MilkyWay, 6 celestial presets) — all PASSED earlier.
- **#2 Clouds overhaul:** CO-1 vertical profile (per-deck `height_profile`) · CO-2 stratus shape-mode +
  world-anchored varied cirrus · CO-3 macro variety (default-off) · CO-4 cloud preset library. The cloud
  per-layer GPU buffer is now **6 vec4 / 24 floats**: fields 19 ProfileBottom · 20 ProfileTop · 21 Anvil ·
  22 ShapeMode · 23 AntiRepeat — **byte-identical across `cloud_raymarch.glsl` / `cloud_shadow.glsl` /
  `cloud_shadow_check.glsl`** (the #1 recurring gotcha — keep them in lockstep; verify `--shadowcheck`).
- **#4 Stage 4:** ST4-1 auto day/night cycle (clock in `_Process`, `play day/night` + `cycle speed` +
  `--autotime`) · ST4-2 fantasy/exotic presets (`TerrainLabUI.FantasyPresets.cs` + `data/fantasy_presets.json`
  + `--fantasy=N`; a persistent `_skyTint` multiply in `ComposeLighting`).
- **Moon decoupled to its own arc** — phase-lag + own declination (`MoonState.DeclScale`) + azimuth offset
  (default 35°); full moon still ~anti-solar.
- **Review keys:** `scenes/review.tscn` — **6 = cloud types** (cumulus profile → stratus → cirrus) · **7 =
  fantasy skies** (cycle the 5 presets). (God rays + earlier items already passed.)

## REMAINING sky work (pick with the user)
- **#3 GPU atmosphere (AT-1)** — the last big item, now unblocked (#2 landed). Hillaire LUTs on the **CloudVolume
  `Texture2Drd` / `RenderingServer.CallOnRenderThread` seam, NOT FieldCompute** (a local-RD texture can't be
  sampled by a material — memory `compute-to-material-callonrenderthread`). Toggleable sky-color provider,
  **default OFF = approved keyframed look until it wins an A/B gate.** AT-1 sky color → AT-2 aerial → AT-3
  cloud-lighting. Spec `specs/2026-06-20-gpu-atmosphere-design.md`. Heaviest remaining build.
- **Galaxy / Milky Way review** (NEEDS_REVIEW 10, user-flagged) — a look-pass; likely a small spec→build if it
  needs real work. Lighter; good warm-up.
- **Soft-accepts owing fuller judgment once the weather axis exists:** CO-3 macro variety (default-off), CO-4
  cloud presets, ST4-2 fantasy. **Deferred (own gate):** pre-existing detail-erosion divergence between the
  raymarch (2-octave) and the shadow shaders (1-octave) — shadows cast from a slightly fuller cloud edge.

## Key code (this lane owns these)
`shaders/cloud_sky.gdshader` (sky + sun + moon + stars + **cirrus**) · `shaders/cloud_raymarch.glsl`
(+ `cloud_shadow.glsl` + `cloud_shadow_check.glsl` — keep `layer_density` byte-identical, 6 vec4/layer) ·
`scripts/lab/CloudVolume.cs` + `CloudLayers.cs` (deck system + packing) · `TerrainLabUI.Lighting.cs`
(`ComposeLighting` one-writer, `DriveTime`, the ST4-1 clock, moon arc, `_skyTint`) · `.Apply.cs` (control
routing) · `.FantasyPresets.cs` / `.CelestialPresets.cs` / `.SunPresets.cs` / `.Clouds.cs` (preset patterns) ·
`LightingState.cs` (TimeState/MoonState/StarsState) · `data/{cloud,celestial,sun,fantasy,time}_presets.json`,
`data/cloud_layers.json`, `data/lab_controls.json`.

## Run / gotchas
- One Godot at a time; always `--rendering-driver vulkan`; kill strays
  (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`). After new .cs/texture: `dotnet build WG16.csproj`
  → headless `--import` (the `_console.exe`). GLSL compute compiles at runtime (windowed), not at `--import`.
  Launch absolute: `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`.
- Self-check CLIs: `--auto-shot=<path>` (windowed quit-after-shot), `--profmove`, `--shadowcheck`,
  `--cloudprofile=b,t,a`, `--stratus[=v]`, `--cirrus[=cov]`, `--antirepeat[=v]`, `--autotime[=speed]`,
  `--fantasy=N`, `--celestial=N`, `--time=H`.
- Benign `not a valid texture` / `us is null` radiance-rebake lines on any lighting change (and continuously
  while the auto-cycle plays) are cosmetic — documented; don't chase.

## Coordination
Shared branch `experiment/presentation` with a **Ground/Texture chat**. Stay in sky/light files; do NOT edit
ground files (`terrain_lab.gdshader`, `TerrainLab.cs`, Splat/Height compute, `ground_palette.json`). Shared
files (`lab_controls.json`, `TerrainLabUI` core, `DECISIONS.md`/`ROADMAP.md`): small + additive, **`git add`
your paths explicitly, never `git add -A`** (their edits intermingle). Commit-by-default; **push only when the
user asks** (nothing pushed this session).

## Start by
Read the docs above, then ask the user: **#3 GPU atmosphere (AT-1)** or the **galaxy/Milky Way review** — then
`writing-plans` → build sub-phase 1 → drive the eye-gate via `scenes/review.tscn`. One phase past the last pass.
