# Handoff — CDLOD S2a render fix (2026-06-21)

Branch: `experiment/presentation` (HEAD `e6020e1`). **Nothing committed this session** — all
changes are in the working tree. The repo had substantial uncommitted WIP before this session
(see "Pre-existing WIP" below); my fixes are layered on top.

## TL;DR
S2a CDLOD (quadtree chunked terrain) had two bugs; both fixed (C# only — shader/scene untouched).
1. **Chunks rendered nothing** → manager node was never entering the SceneTree.
2. **Dark smeary "blob" shadows on the terrain** → over-tall per-chunk shadow AABBs.
Verified by screenshots at multiple cameras + sun angles. User accepted the result; the only
remaining darkening is normal directional shadow that tracks the sun (not a bug).

Repro that now works:
`<godot> --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --lodviz=1`
(Godot used this session: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/.../Godot_v4.6.2-stable_mono_win64.exe`)

---

## ⚠ CRITICAL WORKFLOW GOTCHA (cost ~30 min this session)
**Launching the Godot player binary does NOT rebuild C#.** It runs the pre-built
`.godot/mono/temp/bin/Debug/WG16.dll` as-is. A stale DLL silently masks every `.cs` change.
`.gdshader` edits DO hot-compile at runtime — which misleads you into thinking C# spikes are live.

**Always, after any `.cs` edit:**
`"/c/Program Files/dotnet/dotnet" build WG16.csproj -c Debug -v quiet`
…then launch. If a code change "has no effect," suspect the stale DLL FIRST.

Other run rules (confirmed): scene CANNOT run `--headless` (FieldCompute local RD NullRefs);
user `--flags` MUST come after a bare `--`; `--auto-shot=` needs an OS path (e.g. `C:/tmp/wg16shots/x.png`),
not `user://`; ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`).

---

## Bug 1 — chunks rendered nothing (empty view, ~240 fps)
**Root cause:** `TerrainLab.Build()` runs from `TerrainLabUI._Ready()` while the tree is mid-setup.
A direct `AddChild(_cdlod)` failed ("Parent node is busy setting up children, add_child() failed")
and left the `CdlodTerrain` manager **orphaned** — so its ~340 chunk MeshInstance3D children never
entered a viewport and never rasterized. (Material/AABB/MODEL_MATRIX were all red herrings; even a
StandardMaterial showed nothing. The cloud/atmosphere sibling nodes a few lines away already worked
around the exact same issue with `CallDeferred`.)

**Fix** (`scripts/lab/TerrainLab.cs`, in `Build()`):
- `(GetParent() ?? (Node)this).CallDeferred(Node.MethodName.AddChild, _cdlod);` (was a direct AddChild)
- `Setup()` still called immediately (needs no in-tree state).
- Defensive guard added in `CdlodTerrain.Tick()`: `if (!IsInsideTree()) { return; }`.

## Bug 2 — dark smeary shadow blobs on the surface
**Root cause:** every chunk set `CustomAabb` to the **full region height** (~835 m) over its small
footprint. Godot fits each directional shadow cascade's depth range to caster AABBs, so 340 chunks
each claiming the full height range inflated the shadow-map depth range → precision loss → large soft
self-shadow patches, worst at low sun.

**Diagnosis (elimination — why the obvious fixes all failed):**
- `--shadow=0` removed them → directional shadow pass.
- Single full mesh of the SAME field = clean → chunk-specific.
- `--time=12` (noon) removed them → depth precision, not relief shadows.
- **Tight chunk AABB removed them → confirmed.**
- Did NOT help (ruled out): SSAO off, clouds/godrays/aerial off, shadow_bias↑, shadow_normal_bias↑,
  max_distance 8000→2000, shadow_blur→0, GridN 65→257, flat NORMAL.

**Fix** (`scripts/lab/CdlodTerrain.cs`): compute a tight per-chunk vertical AABB from the baked
heightmap. New `ChunkHeightRange(originXZ, size)` samples the chunk footprint min/max (~32² taps cap
so coarse chunks stay cheap), 8 m margin. `Setup()` now takes `float[] heights` + uses `p.HeightmapRes`;
`TerrainLab.Build()` passes `heights`.

**Residual (NOT a bug):** the largest far chunks at extreme straight-down overhead still lose some
precision (they legitimately span a tall range). Not visible at normal play angles. User confirmed
the remaining sun-tracking darkening is correct shadow behavior.

---

## Files changed this session (working tree, uncommitted)
- `scripts/lab/CdlodTerrain.cs` (UNTRACKED file) — `Setup` signature (+heights/res), `ChunkHeightRange`,
  tight per-chunk AABB in `Tick`, `IsInsideTree` guard. (The CDLOD manager itself was pre-existing WIP.)
- `scripts/lab/TerrainLab.cs` — deferred AddChild + pass `heights` to `Setup`.
All diagnostic instrumentation was removed. `shaders/ground.gdshader` and `scenes/terrain_lab.tscn`
are at their pre-session state (I reverted every spike; verified byte-identical).

## Pre-existing WIP (NOT mine — already in working tree at session start)
`project.godot`, `shaders/cloud_sky.gdshader`, the S2a scaffolding in `shaders/ground.gdshader`
(chunk branch, `use_chunk`, `lod_viz` tint), `scripts/lab/TerrainLabUI.{Cli,Process}.cs` CDLOD flags,
`TerrainLab.cs` `SetCdlod`/`CdlodTick`, and untracked CDLOD files (`CdlodMesh.cs`, `CdlodQuadtree.cs`,
`CdlodTerrain.cs`) + many `.uid` files. Don't attribute these to the fix; commit deliberately.

## Open follow-ups (S2b — flagged, NOT done; not bugs)
1. **Chunk normal epsilon mismatch:** `ground.gdshader` chunk branch uses `e = analytic_spacing` (4 m)
   for the forward-difference normal, but chunk vertices are 16 m+ apart (chunkSize/64). Normal is
   sampled finer than the geometry → shading accuracy. Should use the chunk's real vertex spacing
   (or analytic derivatives — see the S1 audit recommendation).
2. **Dead cloud-shadow wiring:** `cloud_shadow_tex` / `cloud_shadow_on` / `cloud_shadow_region` are
   still pushed to the ground material (`TerrainLabUI.cs:94`, `GodRaysScreen` uses them) but the
   stripped `ground.gdshader` no longer samples them. Harmless no-op; clean up or re-wire in S2b.
3. **LOD geomorph** is still S2b (S2a is LOD-select + cull only); cracks at LOD seams not yet handled.

## How to re-verify
Build, then auto-shot at a normal play angle (was blobby) and overhead:
`-- --cdlod=1 --cam=600,500,600,-30,40 --auto-shot=C:/tmp/wg16shots/chk.png`  → should be clean.
`-- --cdlod=1 --lodviz=1` interactive → LOD rings visible (user confirmed), terrain displaced + colored.
