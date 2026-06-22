# S3 — Streaming infinite terrain (folded floating-origin) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unpin the S2 quadtree root so it roams with the camera → infinite streamed world, with floating-origin folded in (snapped camera-relative render space, no float drift), the field still sampling true world XZ (bit-identical across snaps → no shimmer), all S2 geometry guarantees preserved, and tight per-chunk shadow AABBs everywhere via an async GPU height-range (no stall).

**Architecture:** `CdlodTerrain.Tick` computes `renderOrigin = snap(cameraWorldXZ, coarseChunkSize)`, positions every chunk at `chunkWorldXZ − renderOrigin` (bounded coords), and pushes a `render_origin` uniform so `ground.gdshader` reconstructs true world XZ before `field_height`. The quadtree root roams (3×3 root-cell window on the camera). Chunks are born with a generous amplitude-envelope AABB (no pop-in); an isolated, tunable async module (`ChunkAabbProvider`, render-thread RD via `CallOnRenderThread`) tightens each AABB a few frames later. Births/deaths are churn-budgeted; off-window chunks cull.

**Tech Stack:** Godot 4.6.2 mono (C#), spatial `.gdshader`, `RenderingServer.CallOnRenderThread` + the main render-thread `RenderingDevice` for the async height-range, `dotnet build`.

## Global Constraints

- **⚠ BUILD C# AFTER EVERY `.cs` EDIT** — `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` BEFORE launching. Player binary does NOT rebuild C# (stale DLL; memory `wg16-csharp-stale-dll-gotcha`); only `.gdshader` hot-compiles. Build = `0 Error(s)`.
- **Skin not bones:** do NOT change `field_math.gdshaderinc` / `field_height.glsl` / `FieldParams` math. `--fieldcheck` MUST stay `PASS maxAbsDiff=0m`.
- **S2 GUARDS = REGRESSION BACKSTOP — all stay PASS:** `--cdlodcheck`, `--morphcheck`, `--stitchcheck`. S3 changes WHERE chunks are + WHAT XZ the field samples, NOT how they morph/stitch/displace.
- **`--profmove` under 8 ms.** Churn budget is the lever. Known S2 rebuild spike is bounded-not-fixed (logged perf item, not a blocker unless it breaks the eye-gate).
- **NO TDD** (GPU/visual). "Test" = build → all `--*check` PASS → `--profmove` → eye-gate (1-min flight + far-out teleport). Auto-shots SANITY only (`ground-texture-feedback`).
- **⚠ RUN-INVOCATION:** user `--flags` AFTER a bare `--`. `--auto-shot=` an OS path. ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` + `..._console.exe` first). Scene CANNOT `--headless`. Absolute `--path /c/Wg16/wg-16-project` (memory `wg16-cli-run-invocation`, `wg16-launch-absolute-path`).
- **Commit per task.** Stage ONLY this plan's files by EXPLICIT path (NEVER `git add -A` — sky/light lane edits `TerrainLabUI.Lighting.cs`/`.Apply.cs`/`.Process.cs`/`LightingState.cs`/`CloudVolume.cs`/`project.godot` in parallel; they recently extracted a `LightingComposer`). Footer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **Gate viewport:** `scenes/terrain_lab.tscn` / `review.tscn`. Toggles `--cdlod=1`, `--lodviz=1`.

### Key facts (verified 2026-06-22)
- **`CdlodTerrain.Setup`** (CdlodTerrain.cs:38): builds `_grid`, `_variants`, `_qt = new CdlodQuadtree(-region/2,-region/2,region,MaxDepth,SplitFactor)`. Fields `GridN=65`, `MaxDepth=6`, `SplitFactor=2.5f`, `_regionSize`, `_minH/_maxH` (global height range — used for the generous AABB), `_heights/_hRes` (baked — its AABB use is retired).
- **`CdlodTerrain.Tick(camPos)`** (CdlodTerrain.cs:86): `leaves=_qt.Select(camPos)`; per leaf `mi.Position=(OriginXZ.X+half,0,OriginXZ.Y+half)` WORLD, `mi.Scale=(Size,1,Size)`, `CustomAabb` (Y from `ChunkHeightRange`+margin `max(8,Size/(GridN-1)*1.5)`), `lod_viz`, `mi.Mesh=_variants[mask]` (mask-cached `_poolMask`). `EnsurePool(n)` grows `_pool`+`_poolMask`. Called from TerrainLabUI.Process.cs:130 `_terrain.CdlodTick(camPos)`.
- **`ChunkHeightRange(originXZ,size)`** (CdlodTerrain.cs:~50): samples `_heights` (baked, current region). Task 2 replaces it with the generous global-amplitude bound; Task 5's async module tightens.
- **`CdlodQuadtree`** (CdlodQuadtree.cs): ctor `(rootOriginX,rootOriginZ,rootSize,maxDepth,splitFactor)`; `Select(camPos)`→`Recurse(...)`+fills `StitchMask`; `LeafSizeAt(px,pz,cam)`; `NeighborInvariantHolds`. Leaf `Level=maxDepth-depth`. Split `d<size*_splitFactor`.
- **`ground.gdshader` chunk branch:** `wxz=(MODEL_MATRIX*vec4(VERTEX,1)).xz`; `chunk_size=length(MODEL_MATRIX[0].xyz)`; morph `vworld_fine=(MODEL_MATRIX*vec4(VERTEX,1)).xyz` + `distance(cam_world.xz,vworld_fine.xz)`; samples `analytic_h(wxz)`. Uniforms present: `cam_world`, `grid_n`, `split_factor`.
- **`cam_world`** pushed TRUE-world every frame (TerrainLabUI.Process.cs:129); the sky lane reads it — keep it TRUE.
- **NO CPU field evaluator exists** — field is GPU-compute (`field_height.glsl`→`FieldCompute.ProducePage`, which does `Submit();Sync();BufferGetData()` — blocking) + the GLSL include. Hand-porting forbidden (skin-not-bones). → the AABB tighten uses the render-thread RD async (Task 5).
- **`FieldCompute`** (FieldCompute.cs): local `RenderingDevice` `_rd`; `Dispatch` ends `Submit();Sync()` (blocking — no cross-frame fence). Memory `compute-to-material-callonrenderthread` is the render-thread-RD pattern Task 5 follows; memory `headless-no-local-rendering-device` (local RD NullRefs headless → Task 5 runs windowed only).
- **Check wiring:** `--morphcheck`/`--stitchcheck` parse (exact-match) in `TerrainLabUI.Cli.cs`, fields `_morphCheckCli`/`_stitchCheckCli`, invoked in `TerrainLabUI.cs`. `--streamcheck` (Task 4) mirrors.

---

## Task ordering
1. **Task 1 — render-origin plumbing (pure coordinate refactor; pixel-identical).**
2. **Task 2 — roaming root + generous AABB (chunks STREAM infinitely; shadows slightly loose far-out, fixed in Task 5).**
3. **Task 3 — churn budget + frustum culling.**
4. **Task 4 — `--streamcheck`.**
5. **Task 5 — async GPU AABB tightening (the big, isolated, tunable module — tight shadows everywhere).**
6. **THE EYE-GATE.**

(Tasks 1-4 deliver a working infinite world; Task 5 is the AAA shadow-quality layer on top, with the generous AABB as the always-safe fallback if Task 5 hits GPU-threading trouble.)

---

### Task 1: Render-origin plumbing (snapped camera-relative space) — pixel-identical

Move chunk rendering into snapped camera-relative space without changing what's drawn. Chunk position subtracts `renderOrigin`; the shader adds it back before sampling the field — net zero visual change, coords become bounded. Gate: pixel-identical + all guards PASS.

**Files:**
- Modify: `shaders/ground.gdshader` — add `uniform vec3 render_origin;`; reconstruct true world XZ for the field sample AND the morph distance.
- Modify: `scripts/lab/CdlodTerrain.cs` — `Tick`: compute `renderOrigin`, position chunks relative, push `render_origin`; add `_coarseSnap`/`_renderOrigin`.

**Interfaces:**
- Produces (shader): `uniform vec3 render_origin;` consumed by the chunk branch.
- Produces (C#): `CdlodTerrain._renderOrigin` (Vector3) + `RenderOrigin` getter; pushed to `_mat` as `render_origin` in `Tick`.

- [ ] **Step 1: Add `render_origin` + world-XZ reconstruction to `ground.gdshader`**

Add near `cam_world`:
```glsl
uniform vec3 render_origin = vec3(0.0);   // S3: world-XZ offset of the camera-relative render space; add to
                                          // render-relative pos to recover TRUE world XZ for field_height.
```
In the chunk branch, the morph distance currently uses render-relative `vworld_fine` against true-world `cam_world` — inconsistent once chunks go relative. Find:
```glsl
        vec3 vworld_fine = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // pre-displacement world pos (y≈0)
        float d = distance(cam_world.xz, vworld_fine.xz);
```
Replace with:
```glsl
        vec3 vworld_fine = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;          // render-relative pre-displacement pos
        vec2 vtrue_fine = vworld_fine.xz + render_origin.xz;               // S3: TRUE world XZ (cam_world is true-world)
        float d = distance(cam_world.xz, vtrue_fine);                       // morph distance in a CONSISTENT (true) frame
```
Find the morphed-sample line:
```glsl
        vec2 wxz = (MODEL_MATRIX * vec4(u_morph.x - 0.5, 0.0, u_morph.y - 0.5, 1.0)).xz;  // morphed world XZ
```
Replace with:
```glsl
        vec2 wxz = (MODEL_MATRIX * vec4(u_morph.x - 0.5, 0.0, u_morph.y - 0.5, 1.0)).xz + render_origin.xz;  // S3: TRUE morphed world XZ
```
NOTE: `chunk_size = length(MODEL_MATRIX[0].xyz)` is a scale (unaffected by translation) — leave it. Normal taps use `wxz` (now true) — correct. Skirt/stitch use `u_fine` (unit) — unaffected.

- [ ] **Step 2: Compute + apply `renderOrigin` in `CdlodTerrain`**

Add fields near the other private fields:
```csharp
    // S3: snapped camera-relative render space (floating-origin folded in). renderOrigin = camera XZ snapped
    // DOWN to _coarseSnap so render-relative coords stay bounded (no float drift) AND the field samples
    // bit-identical TRUE world XZ across snaps (relative + snapped origin == true world).
    private float _coarseSnap = 8192f;   // set in Setup to the root size (largest chunk grid → snap-invariant)
    private Vector3 _renderOrigin = Vector3.Zero;
    public Vector3 RenderOrigin => _renderOrigin;
```
In `Setup`, after `_qt = …`, add:
```csharp
        _coarseSnap = _regionSize;   // S3: snap renderOrigin to the coarsest chunk grid (root size)
```
Rewrite `Tick`'s top + per-leaf position (keep the rest of the loop body unchanged):
```csharp
    public void Tick(Vector3 camPos)
    {
        if (!_enabled) { return; }
        if (!IsInsideTree()) { return; }
        // S3: snapped camera-relative render origin (folded floating-origin).
        _renderOrigin = new Vector3(
            Mathf.Floor(camPos.X / _coarseSnap) * _coarseSnap, 0f,
            Mathf.Floor(camPos.Z / _coarseSnap) * _coarseSnap);
        _mat?.SetShaderParameter("render_origin", _renderOrigin);
        List<CdlodChunk> leaves = _qt.Select(camPos);   // still TRUE-world select (root fixed until Task 2)
        _lastLeaves = leaves;
        EnsurePool(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            MeshInstance3D mi = _pool[i];
            float half = c.Size * 0.5f;
            // S3: RENDER-RELATIVE position (true center − renderOrigin); shader adds render_origin back.
            mi.Position = new Vector3(c.OriginXZ.X + half - _renderOrigin.X, 0f, c.OriginXZ.Y + half - _renderOrigin.Z);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);
            var (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size);
            float m = Mathf.Max(8f, c.Size / (GridN - 1) * 1.5f);
            mi.CustomAabb = new Aabb(new Vector3(-0.5f, lo - m, -0.5f),
                                     new Vector3(1f, (hi - lo) + 2f * m, 1f));
            mi.SetInstanceShaderParameter("lod_viz", _lodViz ? (float)c.Level : -1.0f);
            if (_variants.Length == 16)
            {
                int sm = c.StitchMask & 15;
                if (_poolMask[i] != sm) { mi.Mesh = _variants[sm]; _poolMask[i] = sm; }
            }
            mi.Visible = true;
        }
        for (int i = leaves.Count; i < _pool.Count; i++) { _pool[i].Visible = false; }
    }
```
NOTE: AABB Y (lo/hi) is true-world height; the AABB is LOCAL to the (relative-positioned) instance and Y is unaffected by the XZ offset → still correct.

- [ ] **Step 3: Build**

`cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.

- [ ] **Step 4: Pixel-identical + ALL guards PASS (the refactor gate)**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
mkdir -p /c/tmp/wg16shots
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --stitchcheck --cam=0,400,1400,-12,0 --auto-shot=C:/tmp/wg16shots/s3_t1.png > /tmp/s3_t1.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; grep -q "STITCHCHECK:" /tmp/s3_t1.log && grep -q "auto-shot" /tmp/s3_t1.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK: (PASS|FAIL)|MORPHCHECK:|STITCHCHECK:|SHADER ERROR.*ground|auto-shot" /tmp/s3_t1.log
```
Expected: ALL four guards PASS, NO `ground.gdshader` error, shot looks SAME as pre-S3 (offset is exact). If terrain shifted/warped → the `render_origin.xz` reconstruction or morph-distance frame is wrong. **Eyeball the shot — must be pixel-equivalent to a pre-S3 shot.**

- [ ] **Step 5: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S3.1: snapped camera-relative render space (render_origin) — pixel-identical

Chunks render at worldXZ-renderOrigin (renderOrigin = camera XZ snapped to the
root size); ground.gdshader reconstructs true world XZ (+render_origin.xz)
before field_height AND for the geomorph camera-distance, so the surface is
unchanged (offset exact) but render-relative coords stay bounded — float32
never drifts (folds floating-origin into streaming). Root still fixed (roaming
is S3.2). Pure coordinate refactor: pixel-identical, all four guards PASS.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Roaming root + generous AABB (chunks stream infinitely)

Make the quadtree window follow the camera → chunks appear ahead / vanish behind (infinite). Source the AABB from the field's generous global-amplitude bound (works anywhere; Task 5 tightens it async).

**Files:**
- Modify: `scripts/lab/CdlodQuadtree.cs` — `SelectRoaming` (3×3 root-cell window on the camera) + extract `FillStitchMasks` + make `LeafSizeAt` roaming-aware.
- Modify: `scripts/lab/CdlodTerrain.cs` — call `SelectRoaming`; replace `ChunkHeightRange` with the generous global bound.

**Interfaces:**
- Produces: `CdlodQuadtree.SelectRoaming(Vector3 camPos)` → `List<CdlodChunk>` (leaves over a camera-centered window, `StitchMask` filled).
- Consumes: existing `Recurse`/`LeafSizeAt`/`NeighborInvariantHolds`.

- [ ] **Step 1: Extract `FillStitchMasks` + roaming-aware `LeafSizeAt` + `SelectRoaming`**

Refactor `Select` to share the mask fill:
```csharp
    public List<CdlodChunk> Select(Vector3 camPos)
    {
        var leaves = new List<CdlodChunk>();
        Recurse(_rootX, _rootZ, _rootSize, 0, camPos, leaves);
        FillStitchMasks(leaves, camPos);
        return leaves;
    }

    /// Fill each leaf's 4-bit edge-stitch mask (bit0=-X bit1=+X bit2=-Z bit3=+Z): a bit is set iff the
    /// neighbor across that edge midpoint is COARSER. Shared by Select + SelectRoaming.
    private void FillStitchMasks(List<CdlodChunk> leaves, Vector3 camPos)
    {
        const float eps = 0.25f;
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            float ox = c.OriginXZ.X, oz = c.OriginXZ.Y, s = c.Size, hs = s * 0.5f;
            int mask = 0;
            if (LeafSizeAt(ox - eps,     oz + hs,      camPos) > s) { mask |= 1; }
            if (LeafSizeAt(ox + s + eps, oz + hs,      camPos) > s) { mask |= 2; }
            if (LeafSizeAt(ox + hs,      oz - eps,     camPos) > s) { mask |= 4; }
            if (LeafSizeAt(ox + hs,      oz + s + eps, camPos) > s) { mask |= 8; }
            c.StitchMask = mask;
            leaves[i] = c;
        }
    }

    /// S3: select leaves over a 3x3 block of root-size cells centered on the camera's root cell (cell-aligned
    /// so a world point always falls in the same chunk → no shimmer). Root roams with the camera → infinite.
    public List<CdlodChunk> SelectRoaming(Vector3 camPos)
    {
        float cx = Mathf.Floor(camPos.X / _rootSize) * _rootSize;   // camera's root-cell origin
        float cz = Mathf.Floor(camPos.Z / _rootSize) * _rootSize;
        var leaves = new List<CdlodChunk>();
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)   // 3x3 cells → camera never near a window edge
        {
            Recurse(cx + dx * _rootSize, cz + dz * _rootSize, _rootSize, 0, camPos, leaves);
        }
        FillStitchMasks(leaves, camPos);
        return leaves;
    }
```
Make `LeafSizeAt` walk the cell containing the probe (drop the fixed-root bounds early-out):
```csharp
    public float LeafSizeAt(float px, float pz, Vector3 camForSplit)
    {
        // S3: root the walk at the root-cell containing (px,pz) (the world is infinite now — any probe has a
        // containing cell). MUST use the SAME cell grid as SelectRoaming (floor to _rootSize) so neighbor
        // lookups across a cell seam are correct.
        float x = Mathf.Floor(px / _rootSize) * _rootSize;
        float z = Mathf.Floor(pz / _rootSize) * _rootSize;
        float size = _rootSize; int depth = 0;
        while (depth < _maxDepth)
        {
            float d = DistanceToCellXZ(x, z, size, camForSplit);
            if (!(d < size * _splitFactor)) { break; }
            float h = size * 0.5f;
            if (px >= x + h) { x += h; }
            if (pz >= z + h) { z += h; }
            size = h; depth++;
        }
        return size;
    }
```
(3×3 independent root-cell quadtrees, all distance-split from the same camera. ≤1-level holds within a cell; across seams adjacent leaves are at similar camera distance so pick compatible levels — `--cdlodcheck`/`--streamcheck` verify empirically. A >1-level seam → forced cross-seam balance, deferred unless the check finds one.)

- [ ] **Step 2: Roaming select + generous AABB in `CdlodTerrain`**

In `Tick`, change the select:
```csharp
        List<CdlodChunk> leaves = _qt.SelectRoaming(camPos);   // S3: roaming root → infinite streaming
```
Replace `ChunkHeightRange` body with the generous global-amplitude bound (Task 5 tightens it async):
```csharp
    /// Generous vertical bound from the field's global height envelope. S3: works for a streamed chunk
    /// ANYWHERE (no baked region needed) — born with this loose-but-safe AABB so it renders + shadows with no
    /// pop-in; the async ChunkAabbProvider (Task 5) tightens it a few frames later for cascade precision.
    private (float lo, float hi) ChunkHeightRange(Vector2 originXZ, float size)
    {
        return (_minH, _maxH);   // global field amplitude envelope (set in Setup)
    }
```
NOTE: `_minH/_maxH` are the region's measured min/max (set in `Setup` from the baked heights). For a truly infinite field these are a reasonable amplitude envelope (the field's amplitude is bounded by `FieldParams`); if far-out terrain exceeds them, Task 5's tighten corrects per-chunk anyway, and the generous bound only needs to be a safe *outer* bound for the interim — widen with a margin if needed:
```csharp
        return (_minH - 200f, _maxH + 200f);   // + safety margin for far-out amplitude beyond the sampled region
```
(Use the margined version.)

- [ ] **Step 3: Build**

`cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.

- [ ] **Step 4: Chunks stream (terrain 20 km out) + guards PASS**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --stitchcheck --cam=20000,400,20000,-12,0 --auto-shot=C:/tmp/wg16shots/s3_t2_far.png > /tmp/s3_t2.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; grep -q "STITCHCHECK:" /tmp/s3_t2.log && grep -q "auto-shot" /tmp/s3_t2.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK: (PASS|FAIL)|MORPHCHECK:|STITCHCHECK:|auto-shot" /tmp/s3_t2.log
```
Expected: guards PASS; the shot at `(20000,400,20000)` — 20 km out, far OUTSIDE the old 8 km region — shows TERRAIN (was blank pre-S3). **The "infinite" proof in a still** (sanity; the flight is the real gate). Shadows may be slightly loose (generous AABB) — Task 5 fixes; here just confirm continuous terrain + no holes + guards green.

- [ ] **Step 5: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodQuadtree.cs scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S3.2: roaming quadtree root + generous AABB — chunks stream infinitely

SelectRoaming roots a 3x3 block of root-size quadtree cells on the camera's
cell (cell-aligned → world addresses grid-stable, no shimmer) → chunks appear
ahead / vanish behind → infinite. FillStitchMasks extracted + LeafSizeAt walks
the roaming cell. AABB from the field's generous global-amplitude envelope
(works anywhere; retires the baked-heightmap dep) — chunks render+shadow with
no pop-in; Task 5 tightens async. All four guards PASS; terrain renders 20 km
out (was blank pre-S3).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Churn budget + frustum culling

**Files:** Modify `scripts/lab/CdlodTerrain.cs` — `MaxChunkOps` budget in `EnsurePool`; guard the `Tick` loop to the smaller of leaf-count/pool-size.

**Interfaces:** Produces `CdlodTerrain.MaxChunkOps` (int, tunable). Consumes `_pool`/`EnsurePool`.

- [ ] **Step 1: Births-per-frame budget**

Add field:
```csharp
    public int MaxChunkOps = 24;   // S3: max pooled-chunk births per frame (amortize streaming churn; tunable)
```
Cap `EnsurePool`:
```csharp
    private void EnsurePool(int n)
    {
        int births = 0;
        while (_pool.Count < n && births < MaxChunkOps)   // S3: cap births/frame so a fast camera doesn't spike
        {
            var mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat };
            AddChild(mi);
            _pool.Add(mi);
            _poolMask.Add(-1);
            births++;
        }
    }
```
Guard the `Tick` loop (pool may be capped below leaf count this frame) — change the loop bound + tail hide:
```csharp
        int n = Mathf.Min(leaves.Count, _pool.Count);   // S3: budget may cap the pool below leaf count this frame
        for (int i = 0; i < n; i++)
        {
            // ... existing per-leaf body using leaves[i] / _pool[i] ...
        }
        for (int i = n; i < _pool.Count; i++) { _pool[i].Visible = false; }
```
(Uncovered leaves fill over the next frames; a fast camera sees the far field populate within a few frames — eye-gate confirms acceptable. Holes at speed → raise `MaxChunkOps`.)

- [ ] **Step 2: Build + perf under a moving camera**

`dotnet build … ` → `0 Error(s)`.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --profile=5 --profmove > /tmp/s3_t3_prof.log 2>&1 &
P=$!; for i in $(seq 1 30); do sleep 2; grep -q PROFILE /tmp/s3_t3_prof.log && break; kill -0 $P 2>/dev/null || break; done; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s3_t3_prof.log
```
Expected: avg under 8 ms in motion; budget should reduce the worst-frame spike vs un-budgeted streaming.

- [ ] **Step 3: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S3.3: churn budget (births/frame cap) + bounded streaming cost

EnsurePool caps new chunk instantiations per frame (MaxChunkOps=24); the Tick
loop is guarded to min(leafCount, poolCount) so uncovered leaves fill over the
next frames instead of spiking. Bounds streaming churn (the known S2 rebuild
spike is bounded-not-fixed). --profmove under budget in motion.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `--streamcheck` (mechanical streaming guard)

**Files:** Create `scripts/lab/StreamCheck.cs`; modify `scripts/lab/TerrainLabUI.Cli.cs` (flag+field) + `scripts/lab/TerrainLabUI.cs` (invoke).

**Interfaces:** `StreamCheck.Run(float regionSize, int maxDepth, float splitFactor, out string msg)` → prints `STREAMCHECK: PASS/FAIL`.

- [ ] **Step 1: Write `StreamCheck.cs`**

```csharp
using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// S3 mechanical streaming guard (--streamcheck). Walks a synthetic straight-line camera traverse across many
/// root-window boundaries and asserts the streaming logic stays sound ALONG the path (not one frame):
///   (A) the <=1-level neighbor invariant holds at EVERY step (a roaming-window seam must not create a
///       >1-level jump = uncrackable).
///   (B) renderOrigin snap field-CONTINUITY: a fixed world point reconstructs to the SAME true XZ under any
///       snapped origin (relative + snapped origin == true) — the no-shimmer guarantee, numerically.
/// Drive+measure only; the live no-edge/no-hitch call is the user's eye-gate. Mirrors --morphcheck style.
public static class StreamCheck
{
    public static bool Run(float regionSize, int maxDepth, float splitFactor, out string msg)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        int steps = 200;
        float step = regionSize / 8f;
        bool invOk = true; string worstInv = "ok";
        for (int s = 0; s < steps; s++)
        {
            var cam = new Vector3(s * step, 400f, s * step * 0.5f);
            List<CdlodChunk> leaves = qt.SelectRoaming(cam);
            if (!qt.NeighborInvariantHolds(leaves, out string m)) { invOk = false; worstInv = $"step {s} cam=({cam.X:F0},{cam.Z:F0}): {m}"; break; }
        }

        float snap = regionSize;   // matches CdlodTerrain._coarseSnap = _regionSize
        float worldPt = 1234.5f;
        float maxDelta = 0f;
        foreach (float camX in new float[] { 0f, snap * 0.4f, snap * 0.9f, snap * 1.1f, snap * 2.3f })
        {
            float origin = Mathf.Floor(camX / snap) * snap;
            float relative = worldPt - origin;
            float reconstructed = relative + origin;
            maxDelta = Mathf.Max(maxDelta, Mathf.Abs(reconstructed - worldPt));
        }
        bool contOk = maxDelta < 1e-3f;

        bool ok = invOk && contOk;
        msg = ok
            ? $"{steps} traverse steps invariant=ok; snap field-continuity maxDelta={maxDelta:E2} (<1e-3) — no shimmer"
            : (!invOk ? $"invariant FAIL: {worstInv}" : $"snap field-continuity FAIL: maxDelta={maxDelta:E2}");
        return ok;
    }
}
```

- [ ] **Step 2: Wire `--streamcheck`**

`TerrainLabUI.Cli.cs` parse (next to `--stitchcheck`):
```csharp
            else if (a == "--streamcheck") { _streamCheckCli = true; }   // S3: streaming invariant + snap-continuity guard
```
Field (next to `_stitchCheckCli`):
```csharp
    private bool _streamCheckCli;     // --streamcheck → S3 streaming invariant-along-traverse + snap field-continuity
```
`TerrainLabUI.cs` invoke (next to `--stitchcheck`):
```csharp
        if (_streamCheckCli)   // S3: streaming invariant-along-traverse + renderOrigin snap field-continuity
        {
            bool ok = StreamCheck.Run(_params.RegionSizeM, 6, 2.5f, out string m);
            GD.Print($"STREAMCHECK: {(ok ? "PASS" : "FAIL")}  {m}");
        }
```

- [ ] **Step 3: Build + run (PASS) + test-the-test**

`dotnet build …` → `0 Error(s)`.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --streamcheck > /tmp/s3_sc.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 2; grep -q "STREAMCHECK:" /tmp/s3_sc.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep "STREAMCHECK:" /tmp/s3_sc.log
```
Expected: `STREAMCHECK: PASS … invariant=ok … no shimmer`. Test-the-test: change `reconstructed = relative + origin;` → `+ 0.5f`, rebuild, run → `STREAMCHECK: FAIL … snap field-continuity`; REVERT + rebuild → PASS. (If the INVARIANT fails at a real step → a roaming-seam >1-level jump; escalate for a cross-seam balance pass.)

- [ ] **Step 4: All five guards + commit**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --stitchcheck --streamcheck > /tmp/s3_all.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; grep -q "STREAMCHECK:" /tmp/s3_all.log && grep -q "FIELDCHECK:" /tmp/s3_all.log && break; kill -0 $P 2>/dev/null || break; done; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK: (PASS|FAIL)|MORPHCHECK:|STITCHCHECK:|STREAMCHECK:" /tmp/s3_all.log
```
Expected: all five PASS.
```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/StreamCheck.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S3.4: --streamcheck — streaming invariant-along-traverse + snap field-continuity

200-step synthetic traverse across many root-window boundaries asserts (A) the
<=1-level invariant at EVERY step (roaming seams must not create a >1-level
jump = crack) and (B) renderOrigin snap field-continuity (fixed world point →
same true XZ under any snapped origin = no shimmer). PASS/FAIL like
--morphcheck; test-the-test verified. All five guards PASS.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Async GPU AABB tightening (the big, isolated, tunable module)

Tighten each chunk's generous AABB via an async GPU height-range on the render-thread RD — tight shadows everywhere, no stall. Self-contained module; if it misbehaves, the generous AABB (Task 2) is the safe fallback (disable the provider).

> **⚠ This is the largest, highest-risk task (first real async-data-path build). If the render-thread async proves infeasible/buggy after a genuine attempt, STOP and escalate — do NOT hand-port the field to CPU (skin-not-bones) and do NOT add a sync per-chunk readback (the stall the spec forbids). The arc still ships a working infinite world on Tasks 1-4 + generous AABB; this task is the quality layer.**

**Files:**
- Create: `scripts/lab/ChunkAabbProvider.cs` — the async height-range module (request queue, render-thread dispatch, collect, tighten callback).
- Modify: `scripts/field/FieldCompute.cs` — add a render-thread-safe async height-range (min/max over a coarse grid) that does NOT block (uses the main RD via CallOnRenderThread, collects next frames). DO NOT change the field math.
- Modify: `scripts/lab/CdlodTerrain.cs` — own a `ChunkAabbProvider`; on chunk birth queue a tighten request keyed by chunk address; apply the tightened range to the instance AABB when it lands. Tunable `AabbProbeRes` / `MaxAabbRequestsPerFrame`.

**Interfaces:**
- Produces: `ChunkAabbProvider` with `void Request(long key, Vector2 originXZ, float size)`, `bool TryTake(out long key, out float lo, out float hi)` (poll completed results), `int MaxRequestsPerFrame` + `int ProbeRes` (tunable). Render-thread dispatch internal.
- Consumes: the field shader/params (read-only), `RenderingServer.CallOnRenderThread`.

NOTE: the EXACT render-thread-RD mechanics (creating a main-RD compute pipeline for the field shader, dispatching a coarse grid, reducing to min/max, collecting without stall) must follow memory `compute-to-material-callonrenderthread` (the established render-thread compute pattern in this codebase — read it + an existing CallOnRenderThread user like `CloudVolume`/`AtmosphereCompute` before writing). Because the precise API sequence is codebase/Godot-version specific and not fully knowable from this plan, **Step 1 is a SPIKE**: stand up the smallest async height-range that returns a correct min/max for ONE chunk off the main thread, verified against a known value, BEFORE wiring it to streaming. If the spike can't achieve non-blocking collection, escalate per the warning above.

- [ ] **Step 1: SPIKE — one async height-range off the main thread**

Read memory `compute-to-material-callonrenderthread` + how `AtmosphereCompute`/`CloudVolume` use `RenderingServer.CallOnRenderThread` + the main `RenderingServer.GetRenderingDevice()`. Build a minimal `ChunkAabbProvider` that, given one chunk footprint, dispatches the field compute on the render-thread RD over a `ProbeRes×ProbeRes` grid, reduces to min/max, and makes the result collectable on a later frame (no `Sync()` stall on the main thread). Verify: request a chunk whose height range is known (compare to a synchronous `FieldCompute.ProducePage` min/max of the same footprint) — the async result must match within epsilon. Print a one-off `AABBSPIKE: async=<lo,hi> sync=<lo,hi> match=YES/NO`. **Gate: match=YES + no main-thread stall (frame time during the request is not spiked). If NO → escalate.**

(No commit yet — spike validates feasibility. If it works, proceed; the spike code becomes the module.)

- [ ] **Step 2: The provider module + tunables**

Finalize `ChunkAabbProvider`: a request queue (dedup by chunk key), `MaxRequestsPerFrame` dispatched per frame (throttle the GPU queue), a completed-results queue drained by `TryTake`. Tunable fields `ProbeRes` (default 7) + `MaxRequestsPerFrame` (default 8). Keep it fully self-contained (no streaming knowledge — it maps `key → (lo,hi)`).

- [ ] **Step 3: Wire into `CdlodTerrain` (birth → request; land → tighten)**

In `Setup`, create the provider (render-thread RD available once in-tree — defer if needed like `_cdlod`). In `Tick`: when a pool slot is first assigned a chunk address (or its address changes), `Request(key, originXZ, size)` (key = a stable hash of the chunk's world address + level). Each frame, drain `TryTake` and, for any live chunk matching the key, replace its `CustomAabb` Y with the tightened `(lo,hi)` + the existing margin. Until a chunk's tighten lands, it keeps the generous AABB (no pop-in). Add a toggle `bool TightenAabb = true` so the provider can be disabled (fallback to generous).

NOTE: chunk identity across frames — the pool reuses slots, so key the request by the chunk's WORLD address (`level,x,z`), not the slot index. Track `_poolKey[i]` like `_poolMask[i]`; re-request only when a slot's key changes.

- [ ] **Step 4: Build + verify tight shadows far-out + guards + perf**

`dotnet build …` → `0 Error(s)`. Then a far-out shot where the generous AABB previously gave loose shadows — after a moment (tighten lands) shadows should sharpen, no acne:
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --stitchcheck --streamcheck --cam=20000,400,20000,-20,0 --auto-shot=C:/tmp/wg16shots/s3_t5_tight.png > /tmp/s3_t5.log 2>&1 &
P=$!; for i in $(seq 1 14); do sleep 3; grep -q "auto-shot" /tmp/s3_t5.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK: (PASS|FAIL)|MORPHCHECK:|STITCHCHECK:|STREAMCHECK:|auto-shot" /tmp/s3_t5.log
```
Then `--profmove` (the async dispatch must not stall): expect under 8 ms, no per-birth spike from the AABB (it's off-thread + throttled). Inspect the shot: tight shadows, no acne, no holes.

- [ ] **Step 5: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/ChunkAabbProvider.cs scripts/field/FieldCompute.cs scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S3.5: async GPU AABB tightening — tight shadows everywhere, no stall

ChunkAabbProvider: an isolated, tunable module that tightens each streamed
chunk's generous AABB via an async GPU height-range on the render-thread RD
(CallOnRenderThread — local RD has no cross-frame fence). Chunks are born with
the generous bound (no pop-in); a coarse-grid (ProbeRes) min/max lands a few
frames later and tightens the AABB in place → cascade-precision shadows
anywhere, no per-birth stall. Requests keyed by chunk world address (_poolKey),
throttled (MaxRequestsPerFrame). Toggle TightenAabb=false falls back to the
generous AABB. First real slice of the async data-path (AABB only; the data
grid stays dormant). All five guards PASS; --profmove under budget.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### THE EYE-GATE (after Tasks 1–5 — the arc's definition of done)

- [ ] **Step 1: Launch windowed**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 &
```
Tell the user: **fly one direction for a full minute** (W + Shift; wheel = speed) — no chunk edge should appear at the horizon, no hitch as chunks stream, no shimmer/swimming as renderOrigin snaps. Then **fly tens of km out + look around** — terrain identical in character, NO vertex jitter (proves folded floating-origin), shadows tight (proves Task 5). `--lodviz=1` to watch the bands roam.

- [ ] **Step 2: The gate (USER verdict)**

**PASS = infinite (no edge), no hitch, no shimmer, no jitter far out, tight shadows, frame time holds.** Backstop: all five `--*check` PASS. If the user sees: world ends at a boundary → widen the 3×3 window; hitch → lower `MaxChunkOps` / coarsen `AabbProbeRes`; shimmer → snap reconstruction wrong (`--streamcheck` B should catch); jitter far out → snap increment too large; loose shadows far out → `TightenAabb`/provider not landing (Task 5). Do NOT call done until the user confirms.

---

## Self-Review

**1. Spec coverage:** §1 snapped camera-relative (Task 1) ✅ · §2 roaming root (Task 2) ✅ · §3 async GPU AABB + generous-now (Task 2 generous + Task 5 async) ✅ · §4 churn budget (Task 3) ✅ · §5 frustum culling (Task 3) ✅ · §6 async slice for AABB only / data grid dormant (Task 5 builds AABB slice only) ✅ · all S2 guards + --streamcheck (Global + Task 4 + every gate) ✅ · eye-gate 1-min flight + far teleport (EYE-GATE) ✅.

**2. Placeholder scan:** No TBD/TODO. Task 5 Step 1 is an explicit SPIKE with a concrete match-verification + escalation (not a placeholder — it's the honest way to build an API-uncertain GPU-threading piece; the precise render-thread sequence is deliberately discovered against the existing CallOnRenderThread users + memory, not guessed in the plan). Every other code step shows full code.

**3. Type consistency:** `SelectRoaming(Vector3)→List<CdlodChunk>`, `FillStitchMasks(List<CdlodChunk>,Vector3)`, `LeafSizeAt(float,float,Vector3)` consistent (Task 2 + Task 4 StreamCheck). `CdlodTerrain` new members `_coarseSnap`/`_renderOrigin`/`RenderOrigin`/`MaxChunkOps`/`_poolKey`/`TightenAabb` + the `ChunkAabbProvider` field consistent. `ChunkAabbProvider.Request(long,Vector2,float)`/`TryTake(out long,out float,out float)`/`ProbeRes`/`MaxRequestsPerFrame` consistent (Task 5 def + CdlodTerrain use). `render_origin` uniform (Task 1). `StreamCheck.Run(float,int,float,out string)→bool`. `--streamcheck`/`_streamCheckCli`. ✅

## Notes for the executor
- **STALE DLL #1 trap:** build after every `.cs` edit. Task 1 Step 1 is shader-only (hot-compiles).
- **Task 1 is a PURE REFACTOR — gate is pixel-identical.** Don't proceed to Task 2 until the shot equals a pre-S3 shot.
- **Tasks 1-4 deliver the working infinite world.** Task 5 is the AAA shadow layer; if its GPU-threading spike fails, the arc still ships on the generous AABB — escalate, don't hack.
- **cam_world stays TRUE-world** (sky lane reads it). Only chunk POSITIONS go render-relative.
- **Roaming-seam invariant is the main streaming risk** — `--cdlodcheck`/`--streamcheck` verify; a >1-level seam → cross-seam balance pass (deferred unless found).
- **Task 5 GPU threading:** follow memory `compute-to-material-callonrenderthread`; runs windowed only (local/main RD NullRefs headless per `headless-no-local-rendering-device`); NEVER hand-port the field (skin-not-bones) or add a sync per-chunk readback.
- **Concurrency:** stage only this plan's files by explicit path; sky/light lane edits its own (incl. the new `LightingComposer`) in parallel.
