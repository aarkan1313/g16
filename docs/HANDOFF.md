# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-18 (CLOUD-POLISH session: found the audit's "already fixed" lighting was buggy → fixed real root causes → finished cloud roadmap #1-#6, all behind toggles → awaiting user's feature-by-feature visual review; ground Unit 1 anti-repeat APPROVED; EROSION arc spec'd + E1 planned; library zips staged at c:\Wg16\_incoming awaiting integration). **Refresh the Current State block at the end of each session.**

This doc is written so a fresh chat with zero context can get productive immediately.

---

## 1. What WG16 is (in three sentences)

A procedural terrain generator in **Godot 4.6 mono (C# + GPU compute)**. It takes the
**proven WG15 base field** — a 5-layer GPU heightfield (continent → uplift → hills →
ridges → macro base) the user already confirmed looks good — and renders it on a
displaced plane with **no bake stage** (generated live). Everything since has been about
**texturing/material look**, which is the active work.

The whole point of this project: the previous one (WG15) churned for weeks on erosion/water
and got torn down repeatedly. WG16 deliberately keeps only the part that worked (the base
field) and rebuilds outward slowly, one judged piece at a time.

## 2. Posture (how to work here — the user cares about this)

- **PILLARS (the standard for every choice): quality = performance = AAA-ish =
  long-term-best — regardless of time cost.** All four weigh equally; none is traded for
  speed of delivery. Lead with the AAA/most-correct option, not the cheap shortcut. "It
  works" is not the bar. (Still gate the LOOK on the user's live eye — see below.)
- **Low plans.** No big upfront plans (they thrashed last project); no zero planning either.
  A short spec only when a feature is genuinely new; a one-line DECISIONS.md entry otherwise.
- **The user's eye is the only gate.** Mechanical checks don't decide look — the user does,
  flying it live. Spike things cheap and put them in front of the user EARLY, never build a
  full system to first-judgment.
- **Build piece by piece, behind live toggles**, so any artifact can be isolated instantly
  (a recurring failure was debugging from stationary screenshots that didn't show what the
  user saw in motion — don't do that; let the user toggle suspects live).
- **Git is the undo.** `main` is the clean baseline; work on `experiment/presentation`. Bad
  result = `git checkout .`, never a manual hand-revert.
- **Right tool for the job** (see TECH_STACK.md): C# default, GPU compute for parallel
  per-cell math, Rust only for a measured serial hot path. Nothing speculative. Everything
  modular.

## 3. Orient (read in this order)

1. This doc (esp. §6 Current State).
2. [ROADMAP.md](ROADMAP.md) — done / in-flight / queued across all threads (start here for "what now").
3. [DECISIONS.md](DECISIONS.md) — every decision, newest first, with the *why*.
4. [TECH_STACK.md](TECH_STACK.md) — file inventory + the tool policy + modularity rules.
5. [README.md](../README.md) — controls, layout.

## 4. Environment & how to run

- **Project dir:** `C:\Wg16\wg-16-project`
- **Godot (windowed):** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`
  - Add `_console.exe` instead for a headless/console build (used for `--import` and screenshots).
- **.NET:** `dotnet` 8 is on PATH. Build with `dotnet build WG16.csproj`.

Run a scene (always `--rendering-driver vulkan`):
```
"<godot.exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```

**GOTCHAS (learned the hard way — heed these):**
- **One Godot process at a time.** Two contend for the GPU → grey-screen hang that looks
  like a crash. Kill existing first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **Stale windows.** A leftover window from a prior launch shows OLD output — if the user
  says "nothing changed," suspect a dead window first. Kill all, relaunch one.
- **First run after adding a .cs / texture:** `dotnet build`, then a headless `--import`,
  before launching, or scripts/textures may not register.
- **Screenshots without a human:** the labs accept `-- --auto-shot=<path>` (launch, waits
  1.5s, saves a PNG, quits). Use it to verify renders yourself.

## 5. The scenes (what each lab is for)

| Scene | What it is | Key controls |
|-------|-----------|--------------|
| `scenes/lab.tscn` | The plain base-field lab (clean baseline look) | 0–5 layer views · R reseed · G walk · P polish · F12 shot |
| `scenes/terrain_lab.tscn` | **The look lab (active work).** Base field + a big data-driven panel | Tabs: Zones · Surface · Color · Detail · Splat · **Light** · Debug + Presets. Randomize/Lock, FLAT BASELINE, MOOD presets, hero shots. RMB/LMB+WASD fly |
| `scenes/material_board.tscn` | The material JUDGING loop (already used) | 1 = pass · 3 = fail · ← undo · RMB+WASD inspect |
| `scenes/lab_experiment.tscn` | Sandbox copy of the base lab (scratch) | same as lab.tscn |

## 6. Current State — REFRESH EVERY SESSION

> ### ⮕ START HERE (2026-06-19, refreshed — GROUND FOUNDATION pivot + PERF win)
> **PERF (2026-06-19, user push "code not settings"):** static `--profile` was hiding the flying cost.
> Added **`--profmove`** (orbit during profile) → found **SDFGI ≈12 ms IN MOTION** (≈0 static): it
> re-voxelizes the 4M-vert un-LOD'd mesh as the camera moves (shadows +~3 ms the same way); frame is
> geometry-bound (4K≈1440p). **Fix BUILT: GI/shadow PROXY** — a coarse 256² heightfield copy feeds
> SDFGI + casts shadows while the detail mesh renders the view. In-motion 1440p **clouds-off 55→131,
> clouds-on 49→101 fps**; GI retained. Toggle `GI/shadow proxy (perf)` (Debug) / `--giproxy=1`,
> **DEFAULT OFF** pending the user's eye-gate on GI/shadow fidelity → then default ON. Restore tag
> `backup-pre-gi-proxy-2026-06-19`. ALWAYS profile with `--profmove` now. Next mesh lever = CDLOD arc.
>

> **Status:** base field proven (no bake); LIGHTING "really good"; CLOUDS reworked + reviewed good.
> Recent: god-ray redesign (paused), performance pass (big wins), terrain-LOD roadmap spec'd.
> **This session: built ground Unit 2 (distance detail), then live judging exposed the real problem —
> the ground baseline is "random / drab" because material PLACEMENT + PALETTE were never designed.**
> So we **reordered the ground arc: build the placement+palette FOUNDATION first** (rule-based
> splatting), then resume the detail units on top. Unit 2 is **BUILT, default OFF, SHELVED** until the
> foundation reads good.
> **⮕ GROUND FOUNDATION G1 — BUILT + mechanically verified (2026-06-19); awaiting the USER's LIVE
> EYE-GATE (user couldn't do visual checks this session).** Spec:
> `docs/superpowers/specs/2026-06-19-ground-foundation-splatting-design.md`; plan:
> `docs/superpowers/plans/2026-06-19-ground-foundation-g1-rule-engine.md` (all 7 tasks done except the
> live gate). G1 = `role_weights()` rule engine in the splat bake (signal-driven placement by
> altitude/slope/signed-curvature; runner-up ROLE becomes the baked secondary; fragment now READS
> `splat.g` — it previously ignored it for a fixed `sec_zone[dom]` lookup). Behind a **`rule placement`
> toggle (Splat tab, default OFF = current look)** + `--groundrules=0/1`; placement breakpoints promoted
> to **live re-bake sliders** (valley/alpine/snow line, rock slope lo/hi, band soft, curve split).
> **Mechanical verify PASSED:** builds/imports/bake-runs; toggle+CLI work; perf NEUTRAL (baked-once:
> ~220 legacy vs ~233 rule fps); zone-debug A/B (`C:/tmp/g1_zones_{legacy,rule}.png`) proves placement
> meaningfully changed + coherent (rock confined to steep/convex, gentle ground stays green). **NOT
> judged: whether it reads good — that's the user's eye-gate (G1 plan Task 7), at close/mid/far.**
> ⚠ Pre-existing (NOT G1) startup noise seen clouds-off: `Texture (binding 1/24) not valid` /
> `Parameter "us" is null` — identical in legacy+rule runs, bake succeeds after; unrelated, flagged.
> **⮕ NEXT:** user flies G1 → on approval, write the **G2 plan** (curated palette: `ground_palette.json`
> + per-role dropdowns). Perf target: **160+ fps final**; baseline clouds-off 227/4.4 ms, on 156/6.4 ms.
>
> **PERFORMANCE PASS (2026-06-19; see `docs/performance.md`):** profiled + decomposed the frame, then
> landed CODE-efficiency wins (NOT quality cuts): branched triplanar (skip ~0 triplanar planes),
> anti-repetition on albedo-only (normal/rough use plain triplanar), `light()` `pow→`5-muls, cloud
> compute GC (cached uniform sets + alloc-free `Std430Writer` → no per-frame churn), 16-bit half
> noise/weather volumes, MSAA 4×→2×. **Baseline frame ~9.7→~5.6 ms, clouds-on ~12.4→~7 ms, zero look
> change.** Tried + REVERTED the "cheap sun light-march" (cache-resident → no gain, darkened clouds).
> ⚠ **AA REVIEW NEEDED (user, in motion):** MSAA is at safe 2×; off/FXAA/TAA are bigger but risk
> crawl/shimmer/cloud-ghosting — see performance.md. Also flagged: cloud temporal default 1→3 (validated).
>
> **TERRAIN LOD — SPEC'D, not scheduled (`docs/superpowers/specs/2026-06-18-terrain-lod-roadmap-design.md`,
> ROADMAP "⛰ TERRAIN LOD" arc):** the terrain is ONE 2048² PlaneMesh (4M verts, no LOD) = the ~3.8 ms
> "floor" + a ×4 shadow redraw — PARKED for its roadmap because **clipmap killed WG1-15** via ELEVATION
> + QUALITY POPS (memory `terrain-clipmap-killed-wg1-15`). Chosen approach **CDLOD (quadtree + per-vertex
> geomorph + detail cross-fade)** — pop-free is the gate; staged T1 (prove on fixed region) → T2 (tiles)
> → T3 (streaming). Do NOT add a clipmap or touch the mesh-LOD without that roadmap.
>
> **GOD RAYS — Component A built, PAUSED (other thread; `godray-redesign-spec.md`):** unified the two
> old god-ray knobs into one cloud-occluded VOLUMETRIC system — a FogVolume (`GodRays.cs` +
> `shaders/godray_fog.gdshader`) that samples the cloud shadow map by world-XZ so air glows through
> cloud gaps. Reads as a soft sun-ward glow (froxel fog can't do knife-edge shafts — Component B
> screen-space radial is the crisp layer, not built). WIP committed in the checkpoint tag. Default OFF.
>
> **CLOUDS — reviewed good (cloud-polish + review, 2026-06-18; `cloud-system-overview.md`,
> `cloud-next-steps.md`):** root-cause fixes (premult double-alpha, sun-extinction, weather, scale) made
> them read good; roadmap #1-#6 built behind toggles; coupling PROVEN (`--shadowcheck`). Review DONE:
> per-deck, presets, sun-disc bug, overcast-as-knob, dome-res (view-space NOT needed), temporal (3 fine),
> randomize. Memory: `cloud-lighting-model` (clouds dim ground / brighten sky — physical, not a bug),
> `cloud-look-real-rootcauses`, `std430-packing-helper`, `wg16-launch-absolute-path`.
>
> **⚠ STILL WANTS THE USER'S EYE (live in `scenes/terrain_lab.tscn`):**
> 1. **AA in motion** — MSAA off vs FXAA vs TAA vs the current safe 2× (perf vs crawl/shimmer/ghosting).
> 2. **H1 BRDF regression check** — clouds-off terrain vs the approved look (custom `light()` =
>    Burley+GGX replica; confirm no regression — note the perf pass touched the terrain shader, all
>    bit-near-identical, but an eye-confirm is owed).
> 3. **God rays** (when un-paused) — the soft volumetric base, then Component B for crisp shafts.
>
> **ACTIVE WORK = the GROUND rebuild, REORDERED 2026-06-19: FOUNDATION first, then detail.**
> Live judging proved the ground reads "random / drab" because material PLACEMENT (the baked
> splat's companion = `dominant−1` BY ARRAY INDEX, not by meaning) + PALETTE (a near-monochrome
> grey 7-zone subset) were never designed — the original 6-unit arc wrongly deferred those to the
> end as polish. **New order (foundation spec
> `docs/superpowers/specs/2026-06-19-ground-foundation-splatting-design.md`, supersedes the
> 2026-06-17 arc's ORDER):**
> - **FOUNDATION = rule-based splatting (the gate):** GPU-bake a real signal set (altitude, slope,
>   signed curvature, aspect→sun-exposure, moisture/flow proxy, cavity) + role placement rules +
>   curated palette, all baked-once (fragment stays flat → protects 160+ fps). Reuse the 7 slots as
>   ROLES; data-driven `data/ground_palette.json`. **G1** signals+rules (prove placement coherent w/
>   current palette) → **G2** curated palette (photoreal) → **G3** aspect+moisture rules.
> - **DETAIL (resume on the good foundation):** Unit 1 anti-repetition (`ar_sample_wp` bombing on
>   the `tp_*` splat path) BUILT + APPROVED. **Unit 2 distance-detail BUILT this session, default
>   OFF, SHELVED** (`detail_on` toggle / `--detail=1`; built with 2 deviations — perf pass preserved,
>   toggle default off — see its commits). Units 3 (surface-depth/POM), 5 (color/value tint), 6 ("and
>   more") still PLANNED (`plans/2026-06-17-ground-unit{3..6}-*.md`). Old Unit 4 (breakup masks) +
>   the palette half of old Unit 5 are now FOLDED INTO the foundation.
> Scope (user): make THIS region really good FIRST → chunks → infinite → biomes last. Each unit
> eye-gated live at close/mid/far; build behind a toggle defaulting to the current look.
>
> **PARALLEL (isolated chats, no project access — will return LIBRARY code to integrate
> here later, not review):** (a) procedural FLORA (trees/grass/forests); (b) WORLD EDITING /
> terrain deformation (brush + GPU-compute height-delta layer + undo). On return, both need
> integration: providers, scene wiring; world-editing edits invalidate splat + ground
> breakup masks + flora scatter → re-bake after edits.
>
> **EROSION arc (spec'd + first plan written this session — NOTHING built yet; see DECISIONS
> 2026-06-17 + `docs/superpowers/specs/2026-06-17-erosion-arc-design.md`):** re-introducing
> WG15's graveyard, done differently. WG15 failed on a STACK of fighting solvers with no
> drainage coherence (the user's symptom: valleys grew/shrank, elevation reversals); WG16
> redoes it as ONE coherent coupled drainage model, a transform DOWNSTREAM of the settled base
> field behind a pristine↔eroded toggle (base-field math untouched). Reconciles coupled sim +
> infinite + small bakes by **baking the low-freq drainage SKELETON (MBs) + synthesizing
> detail procedurally** (clouds DNA). **Build order (user's call): prove the sim GREAT on the
> current single region FIRST** — E1 (coupled droplet hydraulic + thermal sim on a local RD,
> watched cutting live; plan `docs/superpowers/plans/2026-06-17-erosion-unit1-sim-core.md`) is
> THE gate. E2 skeleton bake / E3 semi-procedural detail / E4 coarse global + streaming are
> LATER and need a chunk system WG16 doesn't have. ⚠ E2 is a scoped, deliberate reversal of
> the "no bake stage" stance, erosion-only. **STOP clause:** if E1 can't reach "great" after a
> fair effort, surface it — don't grind 19 versions like WG15. NOT started; needs E1 built +
> flown before anything downstream.
>
> **The cloud system (built this session — see DECISIONS 2026-06-17 + spec/plan):**
> - `shaders/cloud_noise_3d.glsl` + `CloudNoiseCompute.cs` — GPU-bake tileable Perlin-Worley
>   shape (96³) + Worley detail (32³) volumes once at load. `CloudWeather.cs` — 2D coverage/
>   type field. `CloudParams.cs` + `data/cloud_params.json` — knobs.
> - `shaders/cloud_raymarch.glsl` — raymarches the cloud shell (Beer+HG+powder+light cone)
>   into a lat-long texture. `shaders/cloud_shadow.glsl` — same field, top-down sun-march →
>   2D shadow map. Both run on the RENDER THREAD via `RenderingServer.CallOnRenderThread`
>   driven by `CloudVolume.cs` (NOT a CompositorEffect — that raced the Texture2Drd RID).
> - `shaders/cloud_sky.gdshader` — samples the cloud texture by EYEDIR. `terrain_lab.gdshader`
>   custom `light()` (faithful Burley+GGX) samples the shadow map in world XZ (sun-only
>   attenuation, inert when `cloud_shadow_on` false). Presence: overcast/aerial driven by
>   `CloudVolume.Overcast()` (CPU coverage proxy) in `TerrainLabUI.UpdateOvercast`.
> - **Clouds tab**: coverage/density/type/size/edge/detail/opacity/bright/ambient/altitude/
>   thickness/drift/HG/powder/sun-absorb + ground-shadow + god-rays + perf knobs. 5 presets.
>   **Per-tab Randomize+Lock** on every tab. **FPS HUD**. CLI: `--profile[=secs]`, `--clouds=`,
>   `--coverage=`, `--mood=`, `--godrays=`, `--ar=`, `--cloudsteps=`, `--clouddbg=`. Backup of
>   interim full-res cloud path: tag `backup-clouds-skyshader-2026-06-17`.
>
> **The cloud system (new — see DECISIONS 2026-06-17 + spec/plan in docs/superpowers):**
> - `shaders/cloud_noise_3d.glsl` + `CloudNoiseCompute.cs` — GPU-bake tileable Perlin-Worley
>   shape (96³) + Worley detail (32³) volumes once at load. `CloudWeather.cs` — 2D coverage/
>   type field. `CloudParams.cs` + `data/cloud_params.json` — knobs.
> - `shaders/cloud_raymarch.glsl` — raymarches the cloud shell (Beer+HG+powder+light cone)
>   into a lat-long texture. `shaders/cloud_shadow.glsl` — same field, top-down sun-march →
>   2D shadow map. Both run on the RENDER THREAD via `RenderingServer.CallOnRenderThread`
>   driven by `CloudVolume.cs` (NOT a CompositorEffect — that raced the Texture2Drd RID).
> - `shaders/cloud_sky.gdshader` — samples the cloud texture by EYEDIR. `terrain_lab.gdshader`
>   has a re-introduced custom `light()` sampling the shadow map in world XZ (sun-only
>   attenuation, inert when `cloud_shadow_on` false).
> - **Clouds tab** in the look lab: coverage/density/type/size/edge/detail/opacity/bright/
>   ambient/altitude/thickness/drift/HG/powder/sun-absorb + ground-shadow + perf knobs.
>   5 presets (Clear/Scattered/Broken/Overcast/Stormy). **Per-tab Randomize+Lock** added to
>   every tab. **FPS HUD** top-right. CLI: `--profile[=secs]`, `--clouds=0/1`, `--cloudsteps=`,
>   `--clouddbg=` (1 raw cloud tex). Backup of interim full-res path: tag
>   `backup-clouds-skyshader-2026-06-17`.
>

> **What the look lab now is (`scenes/terrain_lab.tscn`):** a full **data-driven** art-
> direction tool, not a slider farm. Every control is defined in `data/lab_controls.json`
> and built into a **TabContainer** (Zones · Surface · Color · Detail · Splat · Light ·
> **Clouds** · Debug) + Presets. Has: **Randomize / Lock** (per-control AND per-tab),
> **FLAT BASELINE** (turns every visual contributor off to bisect artifacts), **MOOD
> presets** (Light tab), **hero shots** (camera save/load), preset save/load, **FPS HUD**.
>
> **The surface (shaders/terrain_lab.gdshader):** 7 height/slope zones; a **GPU-baked
> splat mask** (`SplatCompute.cs` + `splat_weights.glsl`) blending a dominant+secondary
> material per spot; **per-zone companion** dropdowns; **height-blend** transitions;
> macro color; contact/crevice shading. **Anti-repetition (Unit 1, 2026-06-17): the splat
> triplanar `tp_*` now fetches through `ar_sample_wp` (stochastic bombing)** — replaced the
> plain `textureGrad` that had no anti-tiling (the legacy IQ/hex `tiled()` only fed the
> off-by-default non-splat path). Mipmaps fixed (see below). NOTE: this surface is the
> subject of the active GROUND-PRESENTATION ARC rebuild (see top of §6) — "functional but
> bad" until units 2-6 land.
>
> **The lighting (scene + `data/lighting_moods.json`):** soft sun shadows, SDFGI+SSIL GI,
> large-radius SSAO, aerial-perspective + height fog, **AgX tonemap**, built-in color
> grade. 6 curated **mood presets** (golden hour / overcast / midday / blue dawn / storm /
> alpine), each a complete coordinated look. A **default mood is applied on spawn** so
> startup == picking a preset. Sun disc size + shadow softness are decoupled + tunable.
>
> **Lessons banked (don't relearn the hard way; also in `~/.claude` memory):**
> 1. **Fuzziness was missing mipmaps** → motion-only aliasing. Fixed in `.import` +
>    `tools/copy_materials.py`. **Never debug a motion artifact from a still — fly it.**
> 2. **Local-RD compute can't run under `--headless`** (`CreateLocalRenderingDevice()`
>    returns null → NullRef in the Compute ctor). Verify compute bakes WINDOWED;
>    `--headless --import` only compile-checks shaders.
> 3. **Per-frame compute → material texture in Godot 4.6:** drive it via
>    `RenderingServer.CallOnRenderThread` from a plain node, create the output texture +
>    assign the `Texture2Drd` RID ONCE before any dispatch. A CompositorEffect raced the
>    RID ("binding not valid", Godot #118292) — avoid for compute-to-material.
> 4. The earlier cloud-cut / ground-only-shadow approaches are superseded by the
>    volumetric system. Old impls: branch `backup/clouds-system-2026-06-16`.
>
> **CLOUD PRESENCE suite (2026-06-17, after the cloud build):** clouds now affect the whole
> scene, all ~free (full suite 140 fps vs 139 clouds-only). Built + verified mechanically:
> mood/time-of-day cloud color (warm golden / grey storm), CPU coverage scalar
> (`CloudVolume.Overcast()`), overcast dims ambient+sun, aerial-perspective tinted to cloud
> sky, clouds in reflections/GI (sky radiance + `roughness_layers=7`). **God rays** built
> (gap-aligned FogVolume gated by the cloud-shadow map) but **DEFAULT OFF** — first-pass fog
> darkens the scene; needs live tuning (toggle "god rays (live-tune)" in Clouds tab). See
> memory `cloud-presence-research` + DECISIONS 2026-06-17.
>
> **NEXT ACTIONS (pick with the user):**
> - **Fly EVERYTHING live** — the big outstanding gate (user couldn't review this session).
>   Judge: shadow alignment under clouds; the coordinated mood look (warm clouds + overcast
>   + aerial together); then **enable + tune god rays** (Clouds tab toggle; tune fog density/
>   `LightVolumetricFogEnergy`/the gap bias in `cloud_godray_fog.gdshader` — currently
>   darkens the scene, needs your eye). Shadow offset? → `cloud_shadow.glsl` sun-march map.
> - **Snapshot clouds into mood presets** so each lighting mood gets a matching skyscape.
> - **Save biome presets** (the original goal; preset save/load exists, may snapshot mood+
>   clouds too).
> - **Remaining "great" levers** (researched, not built): climate/moisture field; erosion
>   masks; scatter rock meshes (Godot has NO tessellation). Base field settled — don't touch.

## 7. The material library (gitignored — 2.5 GB)

`assets/materials/<name>/{albedo,normal,roughness,ao}.png` holds 738 distinct PBR materials
copied from `D:\assets`. **It is NOT in git** (too big, re-derivable). The *choices* are:
`data/material_verdicts.json` (pass/fail/dropped per material) and `data/material_library.json`
(the 108 accepted, what the look lab loads).

If `assets/materials/` is missing (fresh clone), regenerate it:
```
python tools/copy_materials.py
"<godot_console.exe>" --headless --path . --import
```

## 8. Git

- Branches: `main` = clean M1 baseline · `experiment/presentation` = current work (HEAD here).
- Remote: `https://github.com/aarkan1313/g16.git` (`main` pushed; push others if asked).
- Gitignored: `assets/materials/` (2.5 GB), `.godot/`, build output.
- **Backup of the removed cloud system:** tag `backup-before-cloud-removal-2026-06-16`
  and branch `backup/clouds-system-2026-06-16` (full working impl, for a fresh rebuild).
- Latest commit at last handoff: `0c39c2d` — "Remove cloud-shadow system…". Many commits
  this session (lighting/moods/AgX, mipmap fix, data-driven panel, cleanup) — `git log`.
