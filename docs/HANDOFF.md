# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-16 (late — lighting/moods done, clouds cut). **Refresh the Current State block at the end of each session.**

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

> ### ⮕ START HERE (2026-06-16, late)
> **Status:** base field proven (no bake). The **look lab is built, flown, and the user
> says the look is "really good."** Texturing + lighting are both substantially done.
> The active question is pushing "really good → great" via the remaining levers.
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
> **Two big lessons banked this session (don't relearn the hard way):**
> 1. **The long "fuzziness" was textures imported WITHOUT mipmaps** → minification
>    aliasing that only shows in MOTION (invisible in stills). Fixed: all `.import` set
>    `mipmaps/generate=true`; `tools/copy_materials.py` now writes them so it can't recur.
>    **Never debug a motion artifact from a screenshot — fly it / get the user to judge.**
> 2. **Cloud shadows were CUT** — kept reading as square artifacts across 3 fix attempts.
>    Removed (reverted to Godot default lighting). Full impl preserved on tag
>    `backup-before-cloud-removal-2026-06-16` / branch `backup/clouds-system-2026-06-16`.
>    **To be rebuilt FRESH in a new session** (the user's call).
>
> **NEXT ACTIONS (pick with the user):**
> - **Rebuild cloud shadows fresh** (the immediate ask). The square came from value-noise
>   patches + GI washing out albedo-darkening; a fresh attempt should (a) use a real
>   texture or better noise, (b) attenuate the sun not albedo, (c) be judged in MOTION
>   early. Backup branch has the prior (flawed) version for reference, not reuse.
> - **Save biome presets** — bundle a favorite mood + materials + tuning into named
>   "biomes" (the original goal; preset save/load exists, may want to also snapshot mood).
> - **The remaining "great" levers** (researched, not built): a climate/moisture field
>   driving material+color+wetness together; erosion masks (flow/curvature/aspect);
>   real silhouette geometry (Godot has NO tessellation — scatter rock meshes); more
>   composition tooling. The base field is settled; don't touch its math unless asked.

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
