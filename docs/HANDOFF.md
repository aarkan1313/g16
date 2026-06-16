# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-16. **Refresh the Current State block at the end of each session.**

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
| `scenes/terrain_lab.tscn` | **The look lab (active work).** Same base field + on-screen panel | dropdowns: 7 zone materials · mask mode · blend mode · preset save/load · RMB+WASD fly |
| `scenes/material_board.tscn` | The material JUDGING loop (already used) | 1 = pass · 3 = fail · ← undo · RMB+WASD inspect |
| `scenes/lab_experiment.tscn` | Sandbox copy of the base lab (scratch) | same as lab.tscn |

## 6. Current State — REFRESH EVERY SESSION

> ### ⮕ START HERE (2026-06-16)
> **Status:** base field done (proven, no bake). Texturing is the active lane, via a
> **look lab that is BUILT but NOT YET FLOWN by the user.**
>
> **The texturing story so far:**
> - Porting WG15's splat shader failed at eye level (blocky border-jitter + screen-space
>   grain). Reverted to the clean height/slope color ramp. Kept MSAA.
> - Found a huge PBR material library under `D:\assets` (~1,015 folders, **738 distinct**
>   after collapsing seed/variant copies). Built a **judging loop** and the user judged all
>   738: **108 accepted** (recorded in `data/material_verdicts.json` →
>   `data/material_library.json`). The keepers are heavily alpine/volcanic/tundra/rock,
>   which fits the mountainous terrain.
> - **Lesson:** most library textures are AI-generated with stamped flora (leaves/ferns)
>   that tiles badly. Keep surface-only ground materials. **Flora is a FUTURE procedural
>   decoration pass — never bake it into ground textures.**
>
> **Ready to fly:** `scenes/terrain_lab.tscn` — 7 zone materials (valley→peak), 6 mask
> modes (height / height+slope / noise-broken / curvature / steep-cliff / noise-biome),
> 5 blend modes (flat → full-stack PBR), preset save/load. Presets are the embryo of the
> biome system. Compiles clean, imported.
>
> **NEXT ACTION:** launch the look lab with the user, explore material/mask/blend combos,
> save good ones as presets (= biomes). If the user wants more variety, add mask/shader
> modes — each is one `case` in `shaders/terrain_lab.gdshader` (`zone_weights` for masks,
> the `blend_mode` branches for shading). The base field is settled; don't touch it unless
> asked.

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
- Latest commit: `b9e4e20` — "Material judging pipeline + terrain look lab".
