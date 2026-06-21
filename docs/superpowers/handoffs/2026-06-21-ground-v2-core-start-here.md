# START HERE — Ground v2 (material-rendering reset), Core build

**For a fresh chat with zero context.** Your job: build the **core (units 1–4)** of the new ground
material-rendering system, following the plan. Read this top-to-bottom first, then read the spec, then the plan.

---

## 1. The one-paragraph why

WG16 is a procedural terrain generator (Godot 4.6 mono, C# + GPU compute). Its **sky** is genuinely
AAA (physical Hillaire atmosphere, volumetric clouds, decoupled lighting) because it got a focused,
holistic, staged build. Its **ground** never did — it was bolted on one "Unit" at a time, and on
2026-06-21 a live eye-gate proved the base is structurally broken: the blend hard-thresholds a **4 m
baked weight field**, so real height snaps material boundaries onto that grid → **blocky rectangular
facets**. The user's call: **start over on the ground, follow pillars, get it to parity with the sky.**
You are building that reset.

## 2. The decision (read these two, in order)

1. **Spec:** `docs/superpowers/specs/2026-06-21-ground-material-system-reset-design.md` — the architecture.
2. **Plan:** `docs/superpowers/plans/2026-06-21-ground-material-system-core.md` — the task-by-task build (Tasks 0–5). **This is what you execute.**

**The approach in one line:** materials in **texture arrays** + **per-pixel procedural placement** (no
baked splat → no blockiness, scales to many materials/biomes) → re-hosted anti-tiling → top-4
height-aware blend with `fwidth` AA.

## 3. The guardrail — DO NOT skip this (it's why WG1–15 died)

WG1–15 died from **repeated big-bang teardowns**. WG16 survived because its teardown was disciplined.
So this reset:
- **Does NOT touch the base-field geometry** (`field_height.glsl` / `FieldCompute`) — that's the *bones*,
  proven across 8 seeds. You reset the *skin* only.
- **Re-hosts, does not re-derive** the two keepers: the histogram anti-tiling (`terrain_lab.gdshader`
  `histo_sample_wp` ~L322–366) and the BRDF (`light()` ~L1015–1064).
- **Does NOT delete the old path.** The new `shaders/ground.gdshader` is built **alongside**
  `terrain_lab.gdshader`, swapped via a toggle, **default = the old look**. The old path is retired
  **only after** the user's live eye-gate says the new core is at parity-or-better (Task 5, gated). No
  deletion before that PASS.

## 4. How to run / build / verify (project convention — NOT pytest)

- **Build:** `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly` → `0 Error(s)`.
- **Godot:** windowed `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`; headless `..._console.exe`.
- **One Godot at a time** (two contend for the GPU → grey hang). Kill strays first:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always `--rendering-driver vulkan`, absolute
  `--path /c/Wg16/wg-16-project`.
- **Verify loop (per task):** build → headless `--import` → **mechanical CLI self-check** (the
  `--histcheck`/`--shadowcheck`/`--groundarraycheck` pattern: read back, print PASS/FAIL, quit) →
  `--auto-shot=<png>` visual sanity → **the user's live eye** for look (the only look-gate). **No TDD /
  pytest** — this is GPU/visual; that's a standing project rule.
- **Gotchas:** local-RD compute (the Poisson height bake) returns null under `--headless` → run windowed;
  hand-packed std430 drifts → use `Std430Writer`; per-frame compute→material goes via `CallOnRenderThread`
  (you won't need that for v2 — the arrays are static).

## 5. Current state (what already exists)

- **Spec + plan committed** (`616b3bc`, `82a10a4`). Nothing of the v2 *build* exists yet — you start at Task 0.
- **The diagnostic that proved the reset is needed is live:** `scenes/review.tscn` **key 3** steps the old
  "wrong-defaults" cluster (rough_floor/tex_scale/real-height/variation/palette) one lever at a time; the
  **real-height** step is where the blocky facets appear. A `fwidth` AA was added to the *old*
  `interlock_blend` (it's correct for the old path too, until retired). A `rough_floor` slider was added.
  These are committed and stay.
- **Review key 4** (old GM2) is **free to repurpose** — the plan's Task 5 wires it to the v2 parity A/B.
- The **material library** lives at `assets/materials/<name>/{albedo,normal,roughness,ao}.png` (gitignored,
  2.5 GB, re-derivable). The 108 accepted names are in `data/material_library.json`. **It has NO height
  maps** — Task 1 derives height via the Poisson bake (re-hosted).

## 6. ⚠ Concurrency — the user is editing the project live

The user is actively working the **sky lane (AT-3 cloud lighting)** in parallel — `CloudVolume.cs`,
`TerrainLabUI.Cli.cs`, `cloud_sky.gdshader`, etc. **Your work is on the ground** (`ground.gdshader`,
`GroundMaterialArrays.cs`, `TerrainLab.cs`, `ground_materials.json`, `lab_controls.json` Debug rows,
`Review.cs` case 4) — mostly disjoint. But: **stage only your own files when committing** (`git add
<specific paths>`, never `git add -A`), and if a file you need (`lab_controls.json`, `TerrainLabUI.Cli.cs`,
`Review.cs`) shows unrelated uncommitted changes, those are the user's WIP — add only your hunks (use a
patch), don't sweep theirs in. Work on `experiment/presentation`; commit per task; push only when asked.

## 7. Your first move

Execute **Task 0** of the plan (scaffold `ground.gdshader` + the old↔new material toggle), verify the swap
with the two auto-shots, commit. Then Task 1 (arrays), and so on. Drive the user's live eye only at the
parity gate (Task 5). Use `superpowers:executing-plans` (inline, recommended for the shader tasks) or
`superpowers:subagent-driven-development`.

**Definition of done for this handoff's scope:** the new core renders the terrain through per-pixel
procedural placement + array sampling + height-aware blend, the user flies the key-4 A/B, and it reads
**at parity-or-better with NO blocky facets.** Surface depth (Unit 5), lighting tune (Unit 6), detail
scatter, and biomes are downstream — not this build.
