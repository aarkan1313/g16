# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-17 (volumetric clouds + matched shadows + cloud-presence suite built; not yet flown live). **Refresh the Current State block at the end of each session.**

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
2. [DECISIONS.md](DECISIONS.md) — every decision, newest first, with the *why*.
3. [TECH_STACK.md](TECH_STACK.md) — file inventory + the tool policy + modularity rules.
4. [README.md](../README.md) — controls, layout.

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

> ### ⮕ START HERE (2026-06-17)
> **Status:** base field proven (no bake); texturing + lighting "really good"; and this
> session built **VOLUMETRIC CLOUDS + matched ground shadows + a cloud-PRESENCE suite**
> (mood-tinted cloud color, overcast dimming, aerial perspective, reflections/GI, and
> god rays [default-OFF, needs tuning]). Whole cloud+shadow system ~2 ms/frame; presence
> suite added ~0 cost. **⚠ The user has NOT flown ANY of this session's work live** — it's
> all mechanically verified (build + headless A/B captures + profiling) but the look/motion
> judgment is the outstanding gate. Everything is committed; `git log` for the arc.
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
> Debug) + Presets. Has: **Randomize / Lock** (per-control), **FLAT BASELINE** (turns
> every visual contributor off to bisect artifacts), **MOOD presets** (Light tab — pick a
> vibe), **hero shots** (camera save/load), preset save/load.
>
> **The surface (shaders/terrain_lab.gdshader):** 7 height/slope zones; a **GPU-baked
> splat mask** (`SplatCompute.cs` + `splat_weights.glsl`) blending a dominant+secondary
> material per spot; **per-zone companion** dropdowns; **height-blend** transitions;
> macro color; contact/crevice shading; anti-tiling (IQ/hex — but **hex caused tile-seam
> squares; default is IQ**). Mipmaps fixed (see below). Splat path uses smooth plain
> triplanar (~18 samples) after a lag/fuzz fight.
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
