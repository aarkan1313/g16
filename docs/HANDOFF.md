# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-24 (terrain relight shipped + independent state-of-project audit run; §6 fully
refreshed). **Refresh §6 each session.**

Written so a fresh chat with zero context gets productive immediately.

---

## 1. What WG16 is (in three sentences)

A procedural terrain generator in **Godot 4.6 mono (C# + GPU compute)**. It takes the **proven
WG15 base field** — a 5-layer GPU heightfield (continent → uplift → hills → ridges → macro base)
the user confirmed looks good — and renders it on a displaced plane with **no bake stage**
(generated live). Everything since is about look: ground material, lighting/sun/weather, clouds.

The whole point: the previous project (WG15) churned for weeks on erosion/water and got torn down
repeatedly. WG16 keeps only the part that worked (the base field) and rebuilds outward slowly, one
judged piece at a time.

## 2. Posture (how to work here — the user cares about this)

- **PILLARS (the standard for every choice): quality = performance = AAA-ish = long-term-best —
  regardless of time cost.** All four weigh equally; none traded for delivery speed. Lead with the
  most-correct option, not the cheap shortcut. "It works" is not the bar.
- **The discipline rule (see `ROADMAP.md` — this is why we reset):** a lane builds **at most ONE
  phase ahead of the last PASSED eye-gate**; when the gate queue holds work the user can't yet see,
  STOP and bank — don't open new depth. **Thin docs:** a roadmap line + a `DECISIONS.md` entry, not
  a plan-per-lane; a spec only when a feature genuinely needs one.
- **The user's eye is the only gate for look.** Mechanical checks gate correctness/cost, never look.
  Spike cheap, put it in front of the user EARLY, behind live toggles defaulting to the approved
  look. **Never debug a motion artifact from a still — fly it.** When you DO drive a review, drive
  the changes yourself (the user judges; don't make them click).
- **Git is the undo.** `main` = clean baseline; work on `experiment/presentation`. Bad result =
  `git checkout .`, never a hand-revert.
- **Right tool** (see `TECH_STACK.md`): C# default, GPU compute for parallel per-cell math, Rust
  only for a measured serial hot path. Modular — swap a unit without rewriting neighbors.

## 3. Orient (read in this order)

1. This doc (esp. §6 Current State).
2. `ROADMAP.md` — the source of truth: the NOW eye-gate session, the two paused lanes, done/backlog.
3. `NEEDS_REVIEW.md` — the live eye-gate queue (how to see / judge / unblocks, per item).
4. `DECISIONS.md` — every decision, newest first, with the *why*.
5. `TECH_STACK.md` (tool policy + modularity) · `../README.md` (controls, layout).

Frozen history (superseded/not-yet-scheduled designs, all plans, old handoffs) lives in
`docs/archive/` — see `docs/archive/README.md`.

## 4. Environment & how to run

- **Project dir:** `C:\Wg16\wg-16-project`
- **Godot (windowed):** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`
  (`_console.exe` variant for headless `--import` / screenshots).
- **.NET:** `dotnet` 8 on PATH. Build: `dotnet build WG16.csproj`.

Run a scene (always `--rendering-driver vulkan`, absolute `--path`):
```
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn
```

**GOTCHAS (learned the hard way):**
- **One Godot at a time.** Two contend for the GPU → grey-screen hang. Kill first:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **Stale windows** show OLD output — if "nothing changed," suspect a dead window; kill all, relaunch one.
- **First run after a new .cs / texture:** `dotnet build` → headless `--import` before launching.
- **Self-serve screenshots:** the labs accept `-- --auto-shot=<path>` (launch, wait ~1.5 s, save PNG, quit).
- **Local-RD compute can't run under `--headless`** (`CreateLocalRenderingDevice()` → null). Bake WINDOWED.
- **Per-frame compute→material:** drive via `RenderingServer.CallOnRenderThread`, assign the
  `Texture2Drd` RID ONCE (a CompositorEffect races it). Hand-packed std430 drifts → use `Std430Writer`.

## 5. The scenes

| Scene | What it is | Key controls |
|-------|-----------|--------------|
| `scenes/review.tscn` | **THE VISUAL REVIEW SCENE** (for the eye-gate session). A copy of the look lab where **number keys 1-9 jump to each gate item** — sets toggles/mood/time/camera from the approved baseline + shows an on-screen "what to judge" banner. Full lab UI still present. | 1 sun disc · 2 time-of-day · 3 GM1 palette (3 again=cycle) · 4 GM2 height+POM · 5 GM3-A variation · 6 clouds · 7 god rays · 8 GI/SDFGI+proxy · 9 BRDF baseline. Guide in `NEEDS_REVIEW.md`. |
| `scenes/terrain_lab.tscn` | **The look lab (active work).** Base field + data-driven panel | Tabs: Zones · Surface · Color · Detail · Splat · **Light** · Clouds · Debug + Presets. Randomize/Lock, FLAT BASELINE, MOOD presets, hero shots. RMB/LMB+WASD fly |
| `scenes/lab.tscn` | Plain base-field lab (clean baseline) | 0–5 layer views · R reseed · G walk · F12 shot |
| `scenes/material_board.tscn` | Material judging loop | 1 = pass · 3 = fail · ← undo · RMB+WASD |
| `scenes/godray_test.tscn` | God-ray screen-space test harness | god-ray sliders/presets |

## 6. Current State — REFRESH EVERY SESSION

> **2026-06-24 — STATE OF THE PROJECT (post terrain-relight; independent state-of-project audit run).**
>
> **SHIPPED & signed off:**
> - **Sky / Atmosphere / Celestial — DONE/AAA (pillar effectively closed).** Volumetric clouds (CO-1..4:
>   vertical realism, types incl. cirrus, anti-repeat, presets); Sun & Light (Time×Weather×Grade decouple +
>   time-of-day driver, sun disc/surface + presets); Night (moon + phases, stars, moonlight); Celestial **C2**
>   (meteors, planets, named stars) + **C3** (N-suns / N-moons via a priority budgeter — "3 suns ≈ today's cost")
>   + **data-driven luminaries** (`data/luminaries.json` + Sky-bodies editor); GPU atmosphere **AT-1** (Hillaire
>   sky LUTs) / **AT-2** (froxel aerial perspective) / **AT-3** (physical cloud lighting) all default-on + eye-gated.
>   Galaxy/nebula KILLED (read fake). Only minor luminary look-tuning owed.
> - **Terrain CDLOD / infinite streaming — SHIPPED.** S1 perf gate → S2 quadtree + pop-free per-vertex geomorph
>   (`--morphcheck`; the pop bug was a morphK sign-inversion) + crack-free edge-stitch (16 welded variants, skirt
>   deleted) → **S3 infinite roaming root + folded floating-origin + async GPU AABB tighten**. All 7 mechanical
>   guards PASS (`--morphcheck/--stitchcheck/--streamcheck/--popcheck/--snapdiff/--cdlodcheck/--fieldcheck`).
>   **The WG1–15 terrain-LOD "graveyard" gate is PASSED.**
> - **Terrain RELIGHT — SHIPPED this session (eye-gate PASSED "good enough").** Analytic indirect fill (cool
>   sky hemisphere + warm sun→ground bounce) injected via `EMISSION` in `ground.gdshader`, pushed each Compose by
>   `LightingComposer`; env ambient dropped to low-neutral so the shader fill owns terrain fill. Defaults warmed
>   (`fill: sky` 0.40 / `fill: ground bounce` 2.40); `I` A/Bs it, `--fillab` is a drift-free frozen-time harness.
>   Also fixed: **aerial fade-to-black** (`(1-aer.a)*aerial_haze` path-radiance term, key `Y`/`--aerialhaze`) and
>   **SSIL@1.0 crushing the terrain** (disabled). The long "anti-sun shadows that appear when I turn" hunt
>   **resolved as CORRECT directional lighting, not a bug** — see `docs/handoffs/2026-06-23-terrain-anti-sun-darkness-line.md`.
>
> **DERAILED / open:**
> - **Water / rivers — the project GRAVEYARD.** 5 approaches rejected at the user's eye-gate (pipe-model erosion;
>   structure-first MFD carve; erosion-free sea+lakes). Restart **#6 = flow-mapped RIBBON-MESH rivers on
>   UNMODIFIED terrain** (reuse the good drainage *routing*; no carve, no sea) — **STARTED but UNCOMMITTED in the
>   working tree** (`scripts/hydrology/{RiverRibbonMesh,LakeMesh,WaterRenderer,RegionHeightGrid}.cs` + modified
>   `WorldWaterRegion.cs` + `shaders/water_surface.gdshader`). **Decide its fate (commit-as-WIP or stash) before
>   touching the tree, then BRAINSTORM the approach with the user before grinding** — STOP criterion in force.
>   Handoff: `docs/handoffs/2026-06-23-WATER-TRASHED-rivers-restart.md`.
> - **Erosion — ABANDONED** (folded into the water trash; the pipe-model sim was built + race-fixed but never
>   passed eye-gate). If ribbon rivers on unmodified terrain win, erosion-as-geometry likely stays shelved.
> - **Ground surfacing — STRIPPED to a minimal 5-material height/slope placeholder** (2026-06-21, afef3b1). The
>   full per-pixel / texture-array / placement system is DESIGNED, not built — correctly sequenced LAST (it
>   consumes the hydrology substrate's material/wetness output).
>
> **Known-open real problems:** short-range sun shadows on CDLOD terrain (distant hills cast ~nothing — relight
> spec sub-project #2, not started); terrain-mesh perf floor (~27.5 ms, mesh-bound; CDLOD bounded but didn't
> eliminate worst-case spikes); renderOrigin **snap-pop** every 8192 m (memory ambiguous — verify before trusting
> "fixed"); **60+ commits unpushed to origin**.
>
> **▶ NEXT (ranked, from the 2026-06-24 audit):** (1) resolve the uncommitted ribbon-river WIP, then brainstorm
> rivers with the user (ONE agreed approach, minimum to eye-gate); (2) relight **sub-project #2 = long-range cast
> shadows**; (3) push to origin; then pick the next PILLAR deliberately — with water blocked, **biomes** is the
> cleaner next lane (the terrain is already biome-ready: a manifest = a biome), full surfacing after hydrology.
>
> Prior 2026-06-20/21 state (both-lanes-paused; AT-2 gate; ground G-0) is SUPERSEDED — see `docs/AUDIT-2026-06-21.md`
> + git log for history.

## 7. The material library (gitignored — 2.5 GB)

`assets/materials/<name>/{albedo,normal,roughness,ao}.png` — 738 distinct PBR materials copied from
`D:\assets`. **NOT in git** (too big, re-derivable). The *choices* are: `data/material_verdicts.json`
(pass/fail per material) + `data/material_library.json` (the 108 accepted, what the lab loads).

If `assets/materials/` is missing (fresh clone):
```
python tools/copy_materials.py
"<godot_console.exe>" --headless --path . --import
```

## 8. Git

- Branches: `main` = clean baseline · `experiment/presentation` = current work (HEAD here).
- **Convention (2026-06-20): commit by default.** Commit finished/coherent units of work to
  `experiment/presentation` as you go — clean, scoped commits with good messages — without asking each
  time. Push only when explicitly asked; never touch `main`/force-push unless asked.
- Remote: `https://github.com/aarkan1313/g16.git` (`main` pushed; push others if asked).
- Gitignored: `assets/materials/` (2.5 GB), `.godot/`, build output.
- Restore tags worth knowing: `backup-pre-gi-proxy-2026-06-19`, `backup-clouds-skyshader-2026-06-17`,
  `backup-before-cloud-removal-2026-06-16` (+ branch `backup/clouds-system-2026-06-16`).
