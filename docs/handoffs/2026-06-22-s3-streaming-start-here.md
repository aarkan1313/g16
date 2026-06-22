# HANDOFF — S3 Streaming Infinite Terrain · ✅ DONE (superseded)

> **✅ S3 IS BUILT, EYE-GATED, AND COMMITTED (2026-06-22).** This "start here" is kept for history.
> For current terrain status go to **`docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md`** (S3 entry) and
> **`docs/ROADMAP.md`**. The in-flight snap-pop + two residual flashes are fixed; perf pass done; all 7
> guards PASS; user flew ~49 km / ~17 snaps with no pop/flash (memory `cdlod-renderorigin-snap-pop`).
> **NEXT ARC after S3 = E1 erosion** (spec `docs/superpowers/specs/2026-06-17-erosion-arc-design.md`, not
> yet planned) — though surfacing-vs-erosion-first is an open user call. The rest of this doc is the
> pre-build plan, retained as a record.

**Date:** 2026-06-22  
**Branch:** `experiment/presentation`  
**Lane:** Terrain (infinite-terrain CDLOD). The **graveyard arc** — terrain LOD pops killed WG1–15. Discipline is high.

---

## TL;DR — what to do next

**[SUPERSEDED — S3 is now DONE; see the banner above. Original text follows.]**
**S2 is DONE (pop-free + crack-free continuous LOD). The S3 spec + plan are written and committed but NOT
built. The next action is to EXECUTE the S3 plan.**

- **Plan:** `docs/superpowers/plans/2026-06-22-s3-streaming-infinite.md` (commit cb6a0b5) — 5 tasks + eye-gate.
- **Spec:** `docs/superpowers/specs/2026-06-22-s3-streaming-infinite-design.md` (commit 194c1a8).
- **Execute DIRECT, send-it per task** (the user's preference all session; subagent dispatch hit repeated
  529 overloads — use direct execution unless that's cleared).
- The user wants to build it as **one arc, modular + tunable** ("if it's tunable and modular I don't see why
  it wouldn't work"). Honor that: keep Task 5's async module isolated + dial-able.

---

## Where this sits (the LIVE map — read this, the old T1/T2/T3 is superseded)

The live terrain spec is **`2026-06-21-infinite-terrain-cdlod-design.md`** (stages **S1→S2→S3→S4**). It
SUPERSEDES the old `2026-06-18-terrain-lod-roadmap-design.md` T1/T2/T3 framing (which is annotated as such).
Do NOT navigate by T1/T2/T3.

- **S1 — analytic perf go/no-go** ✅ effectively PASSED (live field 34 ms full-mesh → ~6.4 ms with S2 LOD).
- **S2 — quadtree + per-vertex geomorph + edge-stitch on a finite region** ✅ DONE (this session):
  - **Geomorph (pop-free):** commits 25098d8…dd07e45. `--morphcheck` proves far-edge verts coincide with the
    coarse grid to 0.0 m. **The pop bug was a morphK SIGN INVERSION** (`morphK = 1.0 - morphK`) — removed in
    cc9638a. Test harness `TerrainTestPaths` (keys 5/6/7, `--testpath=N`).
  - **Edge-stitch (crack-free), skirt deleted:** commits 575939e…4804d98. 16 welded INDEXED mesh variants
    (odd boundary verts collapse onto even → coarse spacing → no T-junction). `--stitchcheck` proves it.
  - User eye-gated both. Shadows (sky lane's CSM, d116612) integrated; per-chunk AABB margin scaled for the
    geomorph displacement (96b88a9) to kill grid-aligned acne; shadow knobs lab-tunable (7fa6aab) + review
    key-4 presets (ad4a0f3); penumbra at the physically-correct ~0.53° default (2990249).
- **S3 — streaming infinite** 🔨 **SPEC + PLAN WRITTEN, NOT BUILT** ← YOU ARE HERE.
- **S4 — floating-origin** → FOLDED into S3 (no separate stage; snapped camera-relative coords = no drift phase).

After S3: the async data GRID, then **surfacing** (the LAST arc — fixes the "smooth mess" placeholder look
AND the residual shadow stipple), then erosion/water/biomes/collision/flora.

---

## What S3 is (the design, already approved)

**Unpin the S2 quadtree root so it roams with the camera → infinite world.** Five tasks:

1. **Render-origin plumbing (pixel-identical refactor).** `renderOrigin = snap(cameraXZ, rootSize)`; chunks
   render at `worldXZ − renderOrigin` (bounded coords, no float drift = **folds floating-origin in**); shader
   reconstructs true world XZ (`+ render_origin.xz`) before `field_height` AND the morph distance → surface
   unchanged. **Gate: pixel-identical + all 4 guards PASS.**
2. **Roaming root + generous AABB (WORKING INFINITE WORLD).** `SelectRoaming` = a 3×3 block of root-size
   quadtree cells centered on the camera's cell (cell-aligned → no shimmer). AABB from the field's generous
   global-amplitude bound (works anywhere; Task 5 tightens). Gate: terrain renders 20 km out + guards PASS.
3. **Churn budget + frustum culling.** `MaxChunkOps` caps births/frame; guard the Tick loop to
   `min(leafCount, poolCount)`. Gate: `--profmove` under budget in motion.
4. **`--streamcheck`.** Synthetic traverse asserts ≤1-level invariant ALONG the path + renderOrigin snap
   field-continuity. PASS/FAIL + test-the-test.
5. **Async GPU AABB tightening (THE BIG ONE).** `ChunkAabbProvider` — born-generous chunks get a tight AABB
   from an ASYNC GPU height-range on the **render-thread RD** (via `CallOnRenderThread`), landing a few frames
   later. Isolated, tunable (`ProbeRes`, `MaxRequestsPerFrame`), `TightenAabb` toggle. **Step 1 is a SPIKE**
   (prove non-blocking async match vs sync, ONE chunk, before wiring). If the spike fails → escalate (the arc
   still ships a working infinite world on Tasks 1–4 + generous AABB).

**Eye-gate (definition of done):** fly one direction a full minute (no edge/hitch/shimmer) + teleport tens of
km out (no jitter = folded floating-origin proven, tight shadows = Task 5 proven).

---

## Two blockers ALREADY RESOLVED during planning (don't re-discover them)

1. **There is NO CPU `field_height` evaluator.** The field is GPU-compute only (`field_height.glsl` →
   `FieldCompute.ProducePage`, which does a blocking `Submit();Sync();BufferGetData()`) + the GLSL include.
   **Hand-porting the field math to C# is FORBIDDEN** (skin-not-bones; parity is fragile). → the AABB uses an
   async GPU eval, NOT a CPU eval.
2. **`FieldCompute`'s LOCAL `RenderingDevice` `Submit()/Sync()` is BLOCKING** — Godot's local RD has no
   cross-frame fence (verified). → Task 5's async path MUST use the **main render-thread RD via
   `RenderingServer.CallOnRenderThread`** (memory `compute-to-material-callonrenderthread`; see how
   `CloudVolume`/`AtmosphereCompute` do it). Runs windowed only (local/main RD NullRefs headless).

---

## Critical gotchas (each has cost a session)

1. **⚠ STALE C# DLL** — `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after EVERY `.cs` edit,
   BEFORE launching. The player binary runs the stale DLL; a `.cs` change with "no effect" = stale DLL.
   Shaders DO hot-compile (misleads). Memory `wg16-csharp-stale-dll-gotcha`.
2. **Task 1 is a PURE REFACTOR — its gate is pixel-identical.** If the terrain shifts/warps, the
   `render_origin.xz` reconstruction or the morph-distance frame is wrong. Don't proceed to Task 2 until the
   shot equals a pre-S3 shot.
3. **`cam_world` stays TRUE-world** (the sky lane reads it). Only chunk POSITIONS go render-relative.
4. **The roaming-seam ≤1-level invariant is the main streaming risk** — `--cdlodcheck`/`--streamcheck` verify
   it; a >1-level jump at a 3×3 cell seam → a cross-seam balance pass (deferred unless the check finds one).
5. **Run-invocation:** user `--flags` AFTER a bare `--`; `--auto-shot=` an OS path (`C:/tmp/wg16shots/x.png`);
   ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` + `..._console.exe` first);
   scene CANNOT `--headless`; absolute `--path /c/Wg16/wg-16-project`. Memory `wg16-cli-run-invocation`,
   `wg16-launch-absolute-path`.
6. **Concurrency — the sky/light lane edits in parallel.** Stage ONLY this plan's files by EXPLICIT path;
   NEVER `git add -A`. They recently extracted a `LightingComposer` from `TerrainLabUI` and edit
   `TerrainLabUI.Lighting.cs`/`.Apply.cs`/`.Process.cs`/`LightingState.cs`/`CloudVolume.cs`/`project.godot`.
   (S3 touches `CdlodTerrain.cs`, `CdlodQuadtree.cs`, `ground.gdshader`, `FieldCompute.cs`, new
   `ChunkAabbProvider.cs`/`StreamCheck.cs`, + `--streamcheck` in `TerrainLabUI.Cli.cs`/`.cs`.)
7. **Pop/shimmer/jitter-freeness is ONLY the user's eye in motion** + the five mechanical guards — NEVER
   claimed from a downscaled still. Memory `ground-texture-feedback`.

---

## Verification ladder (NO TDD — GPU/visual)

1. **Build:** `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.
2. **All FIVE guards PASS** (the regression backstop): `--fieldcheck` (field untouched), `--cdlodcheck`
   (≤1-level), `--morphcheck` (pop-free), `--stitchcheck` (crack-free), `--streamcheck` (streaming, NEW).
3. **`--profmove`** under 8 ms in motion (churn budget is the lever; the known rebuild spike is bounded-not-fixed).
4. **THE eye-gate:** user flies a 1-min traverse + far-out teleport → infinite, no edge/hitch/shimmer/jitter,
   tight shadows.

Canonical launch (windowed for the eye-gate):
```
"<godot>.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1
```
(`review.tscn` instead = ReviewMode ON, so the 1-9 keys work incl. key-4 shadow presets.)

---

## State of the tree

- **Working tree:** clean (all session work committed). Only sky-lane files may show as modified (theirs).
- **Latest terrain commits:** `194c1a8` (S3 spec), `cb6a0b5` (S3 plan) on top of the S2 chain.
- **Backup tag:** `pre-shadow-tunable-backup` (16659e6) — a known-good point before the shadow-tunability work.
- **SDD ledger:** `.superpowers/sdd/progress.md` (has the full S2b/S2d/shadows/S3 history).

---

## Key files (for orientation)

- **Streaming target:** `scripts/lab/CdlodTerrain.cs` (`Tick`, `Setup`, `EnsurePool`, `ChunkHeightRange`),
  `scripts/lab/CdlodQuadtree.cs` (`Select`/`Recurse`/`LeafSizeAt` → add `SelectRoaming`/`FillStitchMasks`).
- **Shader:** `shaders/ground.gdshader` (chunk branch — add `render_origin`).
- **Async field:** `scripts/field/FieldCompute.cs` (add render-thread async height-range), new
  `scripts/lab/ChunkAabbProvider.cs`.
- **Checks:** new `scripts/lab/StreamCheck.cs`; existing `MorphCheck.cs`/`StitchCheck.cs` are the pattern.
- **Driver:** `TerrainLabUI.Process.cs:130` calls `_terrain.CdlodTick(camPos)` (the per-frame entry).

---

## References

- **S3 spec:** `docs/superpowers/specs/2026-06-22-s3-streaming-infinite-design.md`
- **S3 plan:** `docs/superpowers/plans/2026-06-22-s3-streaming-infinite.md`
- **Parent (live) spec:** `docs/superpowers/specs/2026-06-21-infinite-terrain-cdlod-design.md` (chunk contract §2, S1 result §10)
- **Implementation roadmap:** `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md` (status updated 2026-06-22)
- **Memories:** `cdlod-s2bd-built`, `cdlod-shadow-acne-vs-penumbra-stipple`, `cdlod-chunk-shadow-aabb`,
  `compute-to-material-callonrenderthread`, `headless-no-local-rendering-device`, `terrain-clipmap-killed-wg1-15`,
  `wg16-csharp-stale-dll-gotcha`, `wg16-cli-run-invocation`, `wg16-pillars`.
- **Shadow coordination:** `docs/handoffs/2026-06-21-shadow-terrain-coordination.md` (the sky lane owns shadow params).
