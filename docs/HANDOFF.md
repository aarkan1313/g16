# WG16 — Handoff (start here)

Last updated: 2026-06-16. Refresh the **Current State** block each session.

## Orient (read in order)
1. [README.md](../README.md) — what the project is, how to run, layout.
2. [TECH_STACK.md](TECH_STACK.md) — the inventory + the C#/Rust/GPU policy + modularity rules.
3. [DECISIONS.md](DECISIONS.md) — running log of every decision (newest first).
4. This doc's Current State block.

## Posture
"Low plans": short spec only when a feature is genuinely new, one-line DECISIONS
entry otherwise, **user's eye is the gate**, spike cheap and judge early. Build
piece by piece, each piece behind a live toggle so artifacts can be isolated.
Git is the safety net — bad result = `git checkout .`, not a hand-undo.

## Current State — refresh every session

> ### ⮕ START HERE (2026-06-16) — Texturing via a LOOK LAB; built, not yet flown.
> **The project:** WG16 = the proven WG15 5-layer base field (continent/uplift/
> hills/ridges/macro), generated live on the GPU, **no bake stage**. Displaced
> plane, fly/walk camera, hot-reload params. The base field is confirmed good.
> Branch `experiment/presentation`; clean baseline is `main`.
>
> **What just happened (the texturing arc):** Porting WG15's splat shader failed
> at eye level (blocky jitter + screen-space grain). Reverted. Then discovered a
> huge PBR material library under `D:\assets` (~1,015 folders, 738 distinct).
> Built a **material judging loop** (`scenes/material_board.tscn`): one material at
> a time, full-stack PBR, **1=pass / 3=fail**, persisted. User judged all 738 →
> **108 accepted** (after dedupe), recorded in `data/material_verdicts.json` +
> `data/material_library.json`. Heavily alpine/volcanic/tundra/rock.
>
> **What's ready to fly (NOT yet flown):** the **terrain look lab**
> (`scenes/terrain_lab.tscn`) — same base field + an on-screen panel to swap LIVE:
> 7 zone materials (valley→peak), 6 mask modes, 5 blend modes, + preset save/load.
> Presets are the embryo of the biome system. Compiles clean, imported.
>
> **NEXT:** launch `scenes/terrain_lab.tscn`, explore material/mask/blend combos,
> save good ones as presets (→ biomes). If gaps appear, add more mask/shader modes
> (each is one `case` in the swapper in `shaders/terrain_lab.gdshader`). Flora is a
> FUTURE procedural-decoration pass — never bake it into ground textures.

## Run it

One Godot process at a time (two contend for the GPU → grey-screen hang).
```
GODOT=".../Godot_v4.6.2-stable_mono_win64.exe"
"$GODOT" --path . --rendering-driver vulkan scenes/terrain_lab.tscn   # the look lab
"$GODOT" --path . --rendering-driver vulkan scenes/lab.tscn           # the plain base-field lab
"$GODOT" --path . --rendering-driver vulkan scenes/material_board.tscn # the material judging loop
```
Build C# first if scripts don't register: `dotnet build WG16.csproj`.

## Regenerate the material library (it's gitignored — 2.5 GB)

The texture files are NOT in git. To rebuild `assets/materials/` from `D:\assets`,
re-run the dedup/copy step (a Python walk that collapses seed/variant copies and
copies albedo/normal/roughness/ao per distinct material). The accepted set is in
`data/material_library.json`; the full verdicts (incl. fails) in
`data/material_verdicts.json`. After regenerating, run a headless import:
```
"$GODOT" --headless --path . --import
```

## Git
- `main` = clean M1 baseline. `experiment/presentation` = current work.
- Remote: github.com/aarkan1313/g16 (`main` pushed).
- `assets/materials/` and `.godot/` are gitignored.
