# HANDOFF — S2b Geomorph (the pop-free terrain gate) · START HERE

**Date:** 2026-06-21  
**Branch:** `experiment/presentation`  
**Lane:** Terrain LOD (T1 — pop-free continuous LOD on the fixed region). This is the **graveyard arc** —
terrain LOD pops killed WG1–15 fifteen times. Discipline is high on purpose.

---

## TL;DR — what to do next

The S2b **spec is committed** (548e206) and the **implementation plan is written + self-reviewed** (on disk
at `docs/superpowers/plans/2026-06-21-s2b-geomorph.md`, NOT yet committed). The user has approved the design
and said **"send it."** The next action is to **execute the plan**.

> **Execute with `superpowers:subagent-driven-development` (or `executing-plans`).** The user's stated
> preference: **send-it per task** (commit per task, eye-gate each step independently) — not batch-then-review.

**Task order (from the plan):**
1. **Task 1 — geomorph shader + wiring** (the pop-killer). Commit when it builds, `--fieldcheck`/`--cdlodcheck` PASS, renders, perf under budget.
2. **Task 2 — test harness** (`TerrainTestPaths` + 3 paths + keys + `--testpath=N` + report). Commit when it drives + reports.
3. **Task 3 — reserved detail-fade curve** (trivial; unused). Commit.
4. **THE EYE-GATE** (after Tasks 1-2): the **user flies the 3 test paths in `terrain_lab.tscn` and confirms ZERO pops in motion across LOD bands.** That is T1's definition of done.

**Also:** commit the two new uncommitted docs (`docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md`, the S2b plan,
and the `docs/ROADMAP.md` edit) — they're staged work from this session, see "Loose ends" below.

---

## Where this sits in the big picture

WG16's terrain LOD is a **3-stage build (T1 → T2 → T3)**, each eye-gated before the next:

- **T1 — pop-free continuous LOD on the fixed 8 km region** ← WE ARE HERE
  - **S1 (quadtree skeleton)** ✅ DONE (commits e3595e7…fb981e6)
  - **S2a (chunks render)** ✅ DONE — 5.6 ms, 4.4× under budget (commits e3595e7…fb981e6)
  - **S2b (geomorph + test harness)** 🔨 IN PROGRESS ← THIS HANDOFF
  - S2c (proxy shadows) — blocked on S2b eye-gate
  - S2d (skirt/stitch finalize + atlas lock) — tail of T1
- **T2 — stable world tiles** — blocked on T1 eye-gate
- **T3 — streaming / infinite** — blocked on T2 eye-gate

Full roadmap: `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md` (written this session).

**Why it matters:** WG1–15 all died at terrain LOD pops (elevation pops + quality pops). WG16's answer is
**CDLOD with per-vertex geomorph** — NOT clipmap (the cursed path). T1 proves the technique works on the
fixed region BEFORE any tiling/streaming infra. Building infra before the core is the exact WG1–15 trap.

---

## What S2b is (the design, already approved)

### S2b.1 — Per-vertex geomorph (the pop-killer)
Each chunk vertex morphs its grid XZ toward the **coarse (even) grid** by a per-vertex `morphK ∈ [0,1]`
(camera distance across the chunk's LOD band), then samples the **same** `field_height` at the morphed XZ.

- **Why structurally pop-free:** the morphed vertex samples the SAME analytic function the coarser LOD
  samples → no stored second heightmap to disagree → the elevation pop *cannot* occur (it's not tuned away).
- **Why per-VERTEX, not per-chunk** (the load-bearing decision): adjacent chunks' shared edge vertices
  compute the same morphK from the same camera distance → morph is continuous **across chunk seams**.
  Per-chunk morphK would re-pop at boundaries — the exact failure being killed.
- **Shader additions:** `cam_world` (re-declared post-strip), `grid_n`, `split_factor` uniforms (fed from
  `CdlodTerrain` via `TerrainLab`); spacing-scaled normal epsilon (S2a review fix — was `analytic_spacing`=4 m
  but coarse chunk verts are 16 m+ apart).

### S2b.2 — LOD-crossing test harness (drive + measure)
`TerrainTestPaths` flies the camera along 3 scripted paths (`data/terrain_test_paths.json`), each targeting
a geomorph failure mode:
1. **Low-fast flythrough** — worst-case horizontal LOD-ring pop stress.
2. **Vertical descend/ascend** — altitude-driven LOD transitions.
3. **Slow boundary-hover** — isolate a single transition for close inspection.

Driven by **keys 5/6/7** (live, watch + judge) AND **`--testpath=N`** (run, print report, quit). Reports
`TESTPATH: <name> avg=… worst=… spikes=… chunks=lo-hi invariant=PASS/FAIL` — the invariant is checked
**along the path**, not one static frame. **Auto pop-detection is DEFERRED** (hook reserved but empty —
calibrate to a real pop the eye finds, don't guess now).

### S2b.3 — Reserved detail cross-fade curve (YAGNI)
Defines `detail_fade(dist)` + uniform, **unused** — the placeholder ground has no distance-varying surface
detail yet, so there's no quality-pop to fix. The seam exists so future surfacing detail rides one curve
(no stacked rings). Trivial.

---

## The gate (T1's definition of done)

**The user flies the 3 test paths in motion and confirms ZERO elevation pops, ZERO cracks, ZERO quality
pops across LOD bands.** Numeric backstop: morphK is C0-continuous across band boundaries (assert no
discontinuity). One visible pop → do NOT proceed; fix the morph (the morph window is the knob — see below).

---

## Critical gotchas (these have each cost a session)

1. **⚠ STALE C# DLL** — `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after EVERY `.cs` edit,
   BEFORE launching. The Godot player binary does NOT rebuild C#. A `.cs` change that "does nothing" is the
   stale DLL, not your logic. **Shaders DO hot-compile** (which misleads). Memory `wg16-csharp-stale-dll-gotcha`.
2. **The morph window is the most likely tuning knob at the gate.** The plan morphs over the far half of the
   band `[size*split_factor, 2*size*split_factor]`. If pops persist, that window is the lever — it MUST
   complete the morph by the coarsen distance `size*split_factor`. Shaders hot-compile → iterate fast.
3. **Pop-freeness is ONLY the user's eye in motion** — NEVER claim it from an auto-shot (the morph is
   near-invisible in a static frame). Memory `ground-texture-feedback`.
4. **Run-invocation:** user `--flags` need a bare `--` separator (else `OS.GetCmdlineUserArgs()` is empty →
   flags silently no-op, scene never quits). `--auto-shot=` needs an OS path (`C:/tmp/wg16shots/x.png`), NOT
   `user://`. ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` first). Scene
   CANNOT run `--headless`. Memory `wg16-cli-run-invocation`.
5. **Launch with absolute path:** `--path /c/Wg16/wg-16-project` (NOT `.` — shell cwd is the parent, `.`
   opens the Godot launcher). Memory `wg16-launch-absolute-path`.
6. **Field is bones, not skin — DON'T TOUCH IT.** `field_math.gdshaderinc` / `field_height.glsl` /
   `FieldParams`. `--fieldcheck` MUST stay `PASS maxAbsDiff=0m`.
7. **Concurrency — the sky lane edits in parallel.** Stage ONLY this plan's files. NEVER `git add -A`. The
   sky lane touches `TerrainLabUI.*`, `CloudVolume.cs`, `cloud_sky.gdshader`, `project.godot`,
   `NightBillboards.cs`. (Current uncommitted `cloud_sky.gdshader` + `project.godot` changes are likely the
   sky lane's — confirm before staging anything.)

---

## Verification ladder (NO TDD — GPU/visual)

1. **Build:** `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.
2. **Field guard:** `--fieldcheck` → `PASS maxAbsDiff=0m` (field untouched).
3. **Invariant guard:** `--cdlodcheck` → `PASS` (≤1-level invariant intact).
4. **Harness:** `--testpath=0/1/2` → report printed per path.
5. **Perf:** `--profmove` → under 8 ms (S2a was 5.6 ms; geomorph adds negligible vertex cost).
6. **THE eye-gate:** user flies the 3 paths in `terrain_lab.tscn` → ZERO pops.

Canonical launch:
```
"<godot>_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project \
  scenes/terrain_lab.tscn -- --cdlod=1 <FLAGS>
```
CDLOD toggles: `--cdlod=1` (quadtree), `--lodviz=1` (LOD tint), `--cdlodcheck`, key `1` (analytic↔baked).

---

## Loose ends from this session (commit these)

These are uncommitted docs from the planning/roadmap work just done — commit them (docs only, safe):
- `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md` (NEW — the full T1/T2/T3 implementation roadmap)
- `docs/superpowers/plans/2026-06-21-s2b-geomorph.md` (NEW — the S2b implementation plan, ready to execute)
- `docs/ROADMAP.md` (MODIFIED — Phase-B Terrain LOD section expanded with T1/T2/T3 + S2b status)

Suggested commit (docs only — does NOT touch the parallel sky-lane files):
```
git add docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md \
        docs/superpowers/plans/2026-06-21-s2b-geomorph.md \
        docs/ROADMAP.md
git commit -m "docs: S2b implementation plan + T1/T2/T3 terrain LOD roadmap"
```
**Do NOT** `git add` `cloud_sky.gdshader` or `project.godot` — those are likely the parallel sky lane's
uncommitted work; confirm with the user / sky-lane chat first.

The `*.cs.uid` / `*.import` untracked files are Godot-generated; leave them (or let the normal flow pick
them up — they're not this lane's concern).

---

## Key files (for orientation)

- **Geomorph target:** `shaders/ground.gdshader` (chunk vertex branch, `if (use_chunk > 0.5)`, ~lines 90-107).
- **Chunk management:** `scripts/lab/CdlodTerrain.cs` (`Tick(camPos)`, `GridN=65`, `MaxDepth=6`, `SplitFactor=2.5f`).
- **Quadtree:** `scripts/lab/CdlodQuadtree.cs` (split rule: subdivide when `camDist < size*splitFactor`).
- **Lab orchestration:** `scripts/lab/TerrainLab.cs` (owns `_mat`, `_cdlod`; `SetCameraWorld` pushes `cam_world`).
- **Key handlers:** `scripts/lab/TerrainLabUI.Process.cs` (key-1 toggle is the template for keys 5/6/7).
- **CLI:** `scripts/lab/TerrainLabUI.Cli.cs` (`ParseCli` flag table + `ApplyCliOverrides`).
- **NEW (Task 2):** `scripts/lab/TerrainTestPaths.cs` + `data/terrain_test_paths.json`.

---

## References

- **S2b spec:** `docs/superpowers/specs/2026-06-21-s2b-geomorph-design.md` (committed 548e206)
- **S2b plan:** `docs/superpowers/plans/2026-06-21-s2b-geomorph.md` (on disk, commit it)
- **T1 design (parent):** `docs/superpowers/specs/2026-06-18-terrain-lod-roadmap-design.md`
- **Implementation roadmap:** `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md`
- **Post-mortem memory:** `terrain-clipmap-killed-wg1-15.md`
- **S2a fixes memory:** `cdlod-chunk-shadow-aabb.md`
- **Project audit:** `docs/AUDIT-2026-06-21.md`
- **Main roadmap:** `docs/ROADMAP.md` (Phase B → Terrain LOD)
