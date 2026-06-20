# Handoff — Terrain Material System (start here, new chat)

Date: 2026-06-19. Continues the GROUND work. Read this, then the spec, then start.

## The one-liner
The terrain ground was "only frameworked, no real work done" (user, live). We diagnosed the real gaps
and **approved a design to build the terrain material pipeline as a proper AAA system**. Your job:
write the implementation plan from the spec, then build it — eye-gated, perf-bound to 8 ms.

## Read in this order
1. **The spec (the agreed design):** `docs/superpowers/specs/2026-06-19-terrain-material-system-design.md`
2. `docs/HANDOFF.md` (§6 START HERE) · `docs/ROADMAP.md` (GROUND arc) · `docs/DECISIONS.md` (newest)
3. `docs/NEEDS_REVIEW.md` — the live eye-gate queue (G1 ✅, GI-proxy ✅ already cleared).

## What was decided (don't re-litigate)
- Build the **weight-blended material pipeline** (the standard AAA setup) as a real system, **not tweaks**.
- **Build order: compositing-core first** — (1) **Blend/compositing quality** (the gap: smooth weights +
  **height-map interlocking** + organic breakup → kills the **blocky** look) + (2) **Surface relief**
  (parallax-occlusion + real normals → kills the **flat/painted** look). Then breakup (Unit 4 plan),
  then color (Unit 5 plan). Placement (G1) feeds it.
- **Two ceiling seams designed-for, deferred:** real **height maps** (start by *deriving* height from
  albedo-luma/roughness) and **RVT/virtual-texture caching** (the infinite-world perf answer; needs
  chunks; build the pipeline first). See spec.
- **Perf rules (hard 8 ms target):** top-2 blend · **POM LOD-gated near only** · masks **baked once** ·
  profile **in motion** with `--profmove`.

## Done this session (built; mostly UNCOMMITTED in the working tree — see warning)
- **G1 rule-based placement** — APPROVED ("basics work"). `role_weights` in `splat_weights.glsl`;
  `rule placement` toggle / `--groundrules=1`; tunable breakpoint sliders (Splat tab).
- **GI/shadow proxy** — perf win, ~2× flying fps; **default ON at 512²** (256² caused an SDFGI dark-blob,
  fixed by going finer; user-verified clean). Toggle `GI/shadow proxy (perf)` (Debug) / `--giproxy=` /
  `--proxyres=`. Tag `backup-pre-gi-proxy-2026-06-19`.
- **`--profmove`** profiling harness (orbit during `--profile`) — the only honest way to measure flying.
- **Contrast palette** set as default (Shots.cs `ZoneDefaultMaterialIndex`) — but the user's verdict was
  "it's not the picks, it's the artifacting/blending" → palette is downstream of the blend work.
- **`splat_warp`** sliders (Splat tab) — a *stopgap* boundary de-block; fold into the real Blend layer,
  don't treat as the fix.

## ⚠ Tree warning (important)
Another chat is **actively building god rays in the same working tree** (HEAD = god-ray WIP; dirty
files include `GodRaysScreen.cs`, `godray_screen.gdshader`, `cloud_presets.json`, and SHARED files
`TerrainLabUI.Cli.cs` / `.Process.cs` / `lab_controls.json`). **Do NOT touch god-ray files**
(`GodRays*`, `shaders/godray*`) — that's their thread. My session's later code changes (GI-proxy 512²,
palette, splat-warp, proxyres, profmove) are **uncommitted and intermingled** with theirs in the shared
files — verify they're present/committed before building on them. Docs (this handoff, the spec,
HANDOFF/ROADMAP/DECISIONS/NEEDS_REVIEW) were committed separately.

## First moves (new chat)
1. Read the spec; sanity-check it still matches the live look (launch windowed, `--groundrules=1 --clouds=0`).
2. Invoke **writing-plans** → implementation plan for the **compositing core** (Blend quality + Relief)
   from the spec. Treat Unit 3 surface-depth plan as the relief input; the Blend-quality layer is new.
3. Build it as a real system (not knobs), eye-gated at close/mid/far, profiled in motion. Run the lab:
   `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0`

## Environment (unchanged)
Godot 4.6 mono. Windowed exe (live/profile) + `_console` exe (`--headless --import`, compile-check only;
local-RD compute needs windowed). ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`).
Always `--rendering-driver vulkan`. Never judge a motion artifact from a still.
