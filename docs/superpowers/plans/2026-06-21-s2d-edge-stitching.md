# S2d — Crack-free LOD seams via edge-stitched mesh variants (eliminate the skirt) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the CDLOD skirt with canonical edge-stitching — each chunk picks a prebuilt mesh variant whose edges facing a one-level-coarser neighbor are welded to that neighbor's vertex spacing — so LOD seams are continuous by construction (no cracks, no visible skirt squares), proven by a numeric `--stitchcheck`.

**Architecture:** The quadtree gains per-edge neighbor-LOD resolution (point-sample the tree across each edge) and emits a 4-bit `StitchMask` per leaf. `CdlodMesh` prebuilds 16 unit-grid variants (one per mask): a stitched edge's ODD boundary vertices are welded in unit-XZ onto the midpoint of their EVEN neighbors, halving that edge's segment count to match the coarse neighbor. `CdlodTerrain.Tick` selects `variants[mask]` per chunk (positions baked → the geomorph shader is UNCHANGED). The skirt term is deleted from `ground.gdshader`. A `--stitchcheck` mirror asserts boundary-vertex coincidence (~0 m) with geomorph active.

**Tech Stack:** Godot 4.6.2 mono (C#), `ArrayMesh`/`SurfaceTool` for the welded variants, spatial `.gdshader`, `dotnet build`.

## Global Constraints

- **⚠ BUILD C# AFTER EVERY `.cs` EDIT** — `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` BEFORE launching. The Godot player binary does NOT rebuild C# (runs the stale `.godot/mono/temp/bin/Debug/WG16.dll`); only `.gdshader` hot-compiles. A stale DLL silently masks `.cs` changes (memory `wg16-csharp-stale-dll-gotcha`). If a code change "has no effect," suspect the stale DLL FIRST. Build must report `0 Error(s)`.
- **Skin not bones:** do NOT touch `field_math.gdshaderinc` / `field_height.glsl` / `FieldParams`. `--fieldcheck` MUST stay `PASS maxAbsDiff=0m`.
- **`--cdlodcheck` MUST stay PASS** (≤1-level invariant intact — stitching depends on it). **`--morphcheck` MUST stay PASS** (the stitch must not break the S2b geomorph). **`--profmove` stays under 8 ms.** The known S2a chunk-rebuild frame spike (54–71 ms in motion) is OUT OF SCOPE — do not fix it here, but do not make it materially worse (variant selection is an array index per chunk; 16 small meshes are trivial memory).
- **NO TDD** (GPU/visual). "Test" = build → `--fieldcheck`/`--cdlodcheck`/`--morphcheck`/`--stitchcheck` PASS → THE eye-gate (user flies the LOD bands, ZERO cracks/squares/new pops). Auto-shots are SANITY only — NEVER claim crack-free or pop-free from a downscaled still (`ground-texture-feedback`).
- **⚠ RUN-INVOCATION:** user `--flags` AFTER a bare `--` separator (else `OS.GetCmdlineUserArgs()` empty → flags no-op, scene never quits). `--auto-shot=` needs an OS path (`C:/tmp/wg16shots/x.png`), NOT `user://`. Canonical: `Godot…_console.exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- <FLAGS>`. ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` AND the `..._console.exe` variant first); scene CANNOT run `--headless`. Launch absolute path `--path /c/Wg16/wg-16-project`. Memory `wg16-cli-run-invocation`, `wg16-launch-absolute-path`.
- **Commit per task** (send-it/revert). Stage ONLY this plan's files by EXPLICIT path (NEVER `git add -A` — the sky/light lane edits in parallel: `CloudVolume.cs`, `LightingState.cs`, `TerrainLabUI.Lighting.cs`/`.Apply.cs`/`.Review.cs`, `cloud_raymarch.glsl`, `data/lab_controls.json`, `project.godot`). Footer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **Gate viewport:** `scenes/terrain_lab.tscn`. Toggles: `--cdlod=1` (quadtree), `--lodviz=1` (LOD tint), `--cdlodcheck`, `--morphcheck`, `--stitchcheck` (new). Camera spawns high+pitched-down (commit 7a196f3) for manual fly.

### Key facts (verified this session)
- **`CdlodChunk`** (`scripts/lab/CdlodQuadtree.cs:9-14`): struct `{ Vector2 OriginXZ; float Size; int Level; }`. `Level = maxDepth - depth` (0 = finest). Leaves built in `Recurse` (line 49).
- **`CdlodQuadtree.Select(camPos)`** (line 28) → `List<CdlodChunk>` via `Recurse`. Split rule (line 39): `canSplit && d < size*_splitFactor`, `d = DistanceToCellXZ` (line 54, nearest-point XZ, ignores Y). `_rootX=_rootZ=-regionSize/2`, `_rootSize=regionSize`, `_maxDepth`, `_splitFactor` are fields.
- **`NeighborInvariantHolds(leaves, out msg)`** (line 65): O(n²) ≤1-level check; `EdgeAdjacent` (line 81) tests edge-touch + overlap.
- **`CdlodMesh.BuildGrid(n)`** (`scripts/lab/CdlodMesh.cs:12`): returns a flat `PlaneMesh` Size 1×1, `SubdivideWidth=SubdivideDepth=n-1`. Vertices span `[-0.5,0.5]` in X and Z. (S2d replaces/augments this with welded `ArrayMesh` variants.)
- **`CdlodTerrain`** (`scripts/lab/CdlodTerrain.cs`): `Setup` (line 27) builds `_grid = CdlodMesh.BuildGrid(GridN)`. `EnsurePool(n)` (line 100) creates `MeshInstance3D { Mesh = _grid, MaterialOverride = _mat }`. `Tick` (line 74) sets per-leaf Position/Scale/CustomAabb/`lod_viz` but NOT Mesh (set once in EnsurePool). Fields `GridN=65`, `MaxDepth=6`, `SplitFactor=2.5f`. Has `GridResolution`/`SplitFactorValue`/`LeafCountLastTick`/`InvariantHoldsNow` (S2b).
- **`ground.gdshader`** chunk branch (`vertex()`, `if (use_chunk > 0.5)`): geomorph (morphK 0 near → 1 far, snap-to-even coarse target), then `skirt = (edge < 0.001) ? (chunk_size/128.0) : 0.0; VERTEX.y = h0 - skirt;` (skirt to DELETE in Task 3), normal from spacing-scaled forward diffs.
- **Check wiring:** `--cdlodcheck` exact-match parse in `TerrainLabUI.Cli.cs` (BEFORE `StartsWith("--cdlod")`), field `_cdlodCheckCli`, invoked in `TerrainLabUI.cs` (~line 182). `--morphcheck` mirrors this (`MorphCheck.Run`). S2d adds `--stitchcheck` the same way.
- **Godot `PlaneMesh` axes:** plane in XZ, X = SubdivideWidth axis, Z = SubdivideDepth axis. A unit-grid vertex at `(ux,uz)∈[0,1]²` ↔ object pos `(ux-0.5, 0, uz-0.5)`.

### Bit convention (PINNED — used by neighbor resolution AND mesh welding; they MUST agree)
A chunk spans world `[origin.X, origin.X+size] × [origin.Y(z), origin.Y(z)+size]`. In unit-XZ `(ux,uz)∈[0,1]`:
- **bit 0 = −X edge** (`ux==0`, world `x==origin.X`). Probe just outside: `(origin.X − ε, origin.Y + size/2)`.
- **bit 1 = +X edge** (`ux==1`, world `x==origin.X+size`). Probe: `(origin.X + size + ε, origin.Y + size/2)`.
- **bit 2 = −Z edge** (`uz==0`, world `z==origin.Y`). Probe: `(origin.X + size/2, origin.Y − ε)`.
- **bit 3 = +Z edge** (`uz==1`, world `z==origin.Y+size`). Probe: `(origin.X + size/2, origin.Y + size + ε)`.
A bit is SET iff the leaf containing that probe has a STRICTLY GREATER `Size` (coarser neighbor). `ε` = a small fraction of the smallest chunk (use `ε = 0.25` m; smallest chunk is region/2^maxDepth = 8192/64 = 128 m, so 0.25 m is safely inside the neighbor and never skips it). Off-region probes (outside `[-regionSize/2, regionSize/2]`) → bit CLEAR (no neighbor, no stitch).

---

## Task ordering
1. **Task 1 — neighbor resolution + `StitchMask`.** Quadtree computes each leaf's 4-bit mask via edge point-sample; `CdlodChunk` carries it. Gate: builds, `--cdlodcheck` PASS, a debug print shows masks.
2. **Task 2 — 16 welded mesh variants + pool selection.** `CdlodMesh` builds the 16 variants; `CdlodTerrain` picks `variants[mask]` per chunk per Tick. Gate: builds, renders, `--fieldcheck`/`--cdlodcheck`/`--morphcheck` PASS, perf under budget.
3. **Task 3 — delete the skirt.** Remove the skirt term from `ground.gdshader`. Gate: builds, renders, no ground shader error.
4. **Task 4 — `--stitchcheck` numeric crack guard.** Mirror the weld + neighbor math; assert boundary-vertex coincidence ~0 m with geomorph active; PASS/FAIL + test-the-test. Gate: PASS; a wrong weld FAILs.
5. **THE EYE-GATE** (after Tasks 1–4): user flies the LOD bands, confirms ZERO cracks / ZERO skirt squares / ZERO new pops.

---

### Task 1: Per-edge neighbor resolution + `StitchMask`

Add a 4-bit stitch mask to each leaf, computed by point-sampling the quadtree across each edge midpoint. This is the foundation Task 2's variant selection consumes.

**Files:**
- Modify: `scripts/lab/CdlodQuadtree.cs` — add `StitchMask` to `CdlodChunk`; add a `LeafAt(x,z)` containment query + a `ComputeStitchMasks(leaves)` post-pass (or compute inline after `Select`).

**Interfaces:**
- Consumes: existing `Select`/`Recurse` output (`List<CdlodChunk>`), `_rootX/_rootZ/_rootSize`.
- Produces: `CdlodChunk.StitchMask` (int, bits 0–3 per the PINNED convention above); `CdlodQuadtree.Select` returns leaves with `StitchMask` populated. A public `int StitchMaskFor(Vector2 origin, float size, List<CdlodChunk> leaves)` is NOT needed externally (kept private), but the field is read by `CdlodTerrain` (Task 2) and `StitchCheck` (Task 4).

- [ ] **Step 1: Add `StitchMask` to `CdlodChunk`**

In `scripts/lab/CdlodQuadtree.cs`, extend the struct:

```csharp
public struct CdlodChunk
{
    public Vector2 OriginXZ;   // world-XZ min corner
    public float Size;         // side length (m)
    public int Level;          // 0 = finest
    public int StitchMask;     // S2d: bit0=-X bit1=+X bit2=-Z bit3=+Z set iff that edge faces a COARSER neighbor
}
```

- [ ] **Step 2: Add a leaf-containment query**

Add a method that returns the SIZE of the leaf containing a world-XZ point (walking the same split rule), or 0 if the point is outside the region. Place it after `Select`:

```csharp
    /// Size (m) of the leaf that would contain world point (px,pz) under the current split rule, or 0 if
    /// the point is outside the root region. Walks the tree like Recurse but follows only the child that
    /// contains the point — O(depth). Used by S2d stitch-mask resolution (a coarser neighbor = bigger size).
    public float LeafSizeAt(float px, float pz, Vector3 camForSplit)
    {
        if (px < _rootX || px > _rootX + _rootSize || pz < _rootZ || pz > _rootZ + _rootSize) { return 0f; }
        float x = _rootX, z = _rootZ, size = _rootSize; int depth = 0;
        while (depth < _maxDepth)
        {
            float d = DistanceToCellXZ(x, z, size, camForSplit);
            if (!(d < size * _splitFactor)) { break; }   // this cell is a leaf (not split) — stop
            float h = size * 0.5f;
            if (px >= x + h) { x += h; }                 // pick the child quadrant containing the point
            if (pz >= z + h) { z += h; }
            size = h; depth++;
        }
        return size;
    }
```

NOTE: `LeafSizeAt` must use the SAME `camForSplit` the frame's `Select(camPos)` used, so the neighbor it finds is the neighbor actually rendered this frame. Pass the frame camera through.

- [ ] **Step 3: Compute masks after Select**

Add a public post-pass that fills each leaf's `StitchMask`, and call it at the end of `Select`. Replace the body of `Select`:

```csharp
    public List<CdlodChunk> Select(Vector3 camPos)
    {
        var leaves = new List<CdlodChunk>();
        Recurse(_rootX, _rootZ, _rootSize, 0, camPos, leaves);
        const float eps = 0.25f;   // probe inset (m): < smallest chunk (128 m), safely inside the neighbor
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            float ox = c.OriginXZ.X, oz = c.OriginXZ.Y, s = c.Size, hs = s * 0.5f;
            int mask = 0;
            // a bit is set iff the neighbor across that edge is COARSER (bigger leaf size) than this chunk.
            if (LeafSizeAt(ox - eps,      oz + hs,      camPos) > s) { mask |= 1; }   // bit0 -X
            if (LeafSizeAt(ox + s + eps,  oz + hs,      camPos) > s) { mask |= 2; }   // bit1 +X
            if (LeafSizeAt(ox + hs,       oz - eps,     camPos) > s) { mask |= 4; }   // bit2 -Z
            if (LeafSizeAt(ox + hs,       oz + s + eps, camPos) > s) { mask |= 8; }   // bit3 +Z
            c.StitchMask = mask;
            leaves[i] = c;   // struct — write back
        }
        return leaves;
    }
```

- [ ] **Step 4: Build (C#)**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 5: Verify invariant still PASS + masks are sane**

Add a TEMP debug print to `CdlodQuadtree.SelfCheck` (the `--cdlodcheck` path) so the mask distribution is visible, then run. In `SelfCheck`, after the existing `leaves`/`inv` lines, add:

```csharp
        int stitched = 0; foreach (var c in leaves) { if (c.StitchMask != 0) { stitched++; } }
        GD.Print($"CDLODCHECK: stitch — {stitched}/{leaves.Count} leaves have >=1 stitched edge");
```

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --cdlodcheck > /tmp/s2d_t1.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 2; grep -q "CDLODCHECK: PASS" /tmp/s2d_t1.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "CDLODCHECK:" /tmp/s2d_t1.log
```
Expected: `CDLODCHECK: PASS ... invariant=ok` AND a `stitch — N/M leaves` line with `N > 0` (chunks at LOD transitions have stitched edges; if `N==0`, the probe/mask logic is wrong — a fixed-camera scene has LOD rings, so some leaves MUST border a coarser neighbor). Remove the TEMP print after confirming (or keep it — it's harmless under `--cdlodcheck` only). Decide in Step 6.

- [ ] **Step 6: Commit**

Keep the `stitch — N/M` print (it's useful + only fires under `--cdlodcheck`). Stage + commit:

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodQuadtree.cs
git commit -m "$(cat <<'EOF'
S2d.1: per-edge neighbor-LOD resolution -> 4-bit stitch mask per chunk

Each leaf now carries a StitchMask (bit0=-X bit1=+X bit2=-Z bit3=+Z) set iff
that edge faces a one-level-COARSER neighbor — the only edges that can crack.
Resolved by point-sampling the quadtree across each edge midpoint (LeafSizeAt,
O(depth), reuses the same split rule + frame camera so the neighbor matches
what's rendered). Foundation for the stitched mesh variants (S2d.2).
--cdlodcheck still PASS; prints N/M leaves with >=1 stitched edge.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: 16 welded mesh variants + per-chunk selection

Prebuild 16 unit-grid meshes (one per stitch mask). A stitched edge's odd boundary vertices are welded onto the midpoint of their even neighbors (in unit-XZ), matching the coarse neighbor's spacing. `CdlodTerrain` selects `variants[mask]` per chunk.

**Files:**
- Modify: `scripts/lab/CdlodMesh.cs` — add `BuildStitchedVariants(int n)` returning `ArrayMesh[16]`; keep `BuildGrid` (used by the one-chunk sanity path).
- Modify: `scripts/lab/CdlodTerrain.cs` — build the 16 variants in `Setup`; in `Tick`, set each pooled instance's `Mesh = _variants[leaf.StitchMask]`.

**Interfaces:**
- Consumes: `CdlodChunk.StitchMask` (Task 1); `GridN`.
- Produces: `CdlodMesh.BuildStitchedVariants(int n)` → `ArrayMesh[]` (length 16, index = stitch mask). `CdlodTerrain._variants` (private `ArrayMesh[]`).

- [ ] **Step 1: Build the welded variants in `CdlodMesh`**

Replace `scripts/lab/CdlodMesh.cs` with the variant builder (keeping `BuildGrid`). The base grid is the standard `n×n` unit grid; for each set edge bit, weld that edge's odd boundary verts onto their even neighbors' midpoint. Build with `SurfaceTool` (positions only — the shader computes normals + height; UVs unused by the chunk branch).

```csharp
using Godot;

namespace WG16.Lab;

/// CDLOD shared grid + S2d stitched variants. The base is ONE flat unit grid (n×n verts, [-0.5,0.5] in
/// X/Z) reused by every chunk instance; the variants additionally WELD the odd boundary vertices of any
/// edge that faces a coarser neighbor onto the midpoint of their even neighbors, so that edge takes the
/// coarse neighbor's vertex spacing and the seam coincides vertex-for-vertex (crack-free, no skirt).
public static class CdlodMesh
{
    /// n = grid resolution (vertices per side); n-1 quads per side.
    public static PlaneMesh BuildGrid(int n)
    {
        n = Mathf.Clamp(n, 2, 256);
        return new PlaneMesh { Size = new Vector2(1f, 1f), SubdivideWidth = n - 1, SubdivideDepth = n - 1 };
    }

    /// 16 welded variants indexed by stitch mask (bit0=-X bit1=+X bit2=-Z bit3=+Z). n MUST be odd-per-side
    /// in QUAD count is even (n-1 even) so odd interior boundary indices have two even neighbors — GridN=65
    /// gives n-1=64 (even). Built once at setup.
    public static ArrayMesh[] BuildStitchedVariants(int n)
    {
        n = Mathf.Clamp(n, 3, 256);
        var meshes = new ArrayMesh[16];
        for (int mask = 0; mask < 16; mask++) { meshes[mask] = BuildOne(n, mask); }
        return meshes;
    }

    private static ArrayMesh BuildOne(int n, int mask)
    {
        int last = n - 1;
        // vertex grid positions in unit-XZ [0,1], then welded per the mask, then emitted as [-0.5,0.5].
        var pos = new Vector2[n, n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            pos[i, j] = new Vector2((float)i / last, (float)j / last);
        }
        // Weld: for each set edge, move that edge's ODD index vertices onto the midpoint of their even
        // neighbors ALONG the edge. Corners are even (0,last) so they never move; adjacent set edges agree.
        bool mX0 = (mask & 1) != 0, mX1 = (mask & 2) != 0, mZ0 = (mask & 4) != 0, mZ1 = (mask & 8) != 0;
        if (mX0) { for (int j = 1; j < last; j += 2) { pos[0, j]    = 0.5f * (pos[0, j - 1] + pos[0, j + 1]); } }
        if (mX1) { for (int j = 1; j < last; j += 2) { pos[last, j] = 0.5f * (pos[last, j - 1] + pos[last, j + 1]); } }
        if (mZ0) { for (int i = 1; i < last; i += 2) { pos[i, 0]    = 0.5f * (pos[i - 1, 0] + pos[i + 1, 0]); } }
        if (mZ1) { for (int i = 1; i < last; i += 2) { pos[i, last] = 0.5f * (pos[i - 1, last] + pos[i + 1, last]); } }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // two triangles per quad cell, consistent winding (matches PlaneMesh CCW from above).
        for (int j = 0; j < last; j++)
        for (int i = 0; i < last; i++)
        {
            Vector3 v00 = P(pos[i, j]),     v10 = P(pos[i + 1, j]);
            Vector3 v01 = P(pos[i, j + 1]), v11 = P(pos[i + 1, j + 1]);
            // tri A: v00, v01, v11 ; tri B: v00, v11, v10  (CCW when viewed from +Y)
            st.AddVertex(v00); st.AddVertex(v01); st.AddVertex(v11);
            st.AddVertex(v00); st.AddVertex(v11); st.AddVertex(v10);
        }
        // No SetNormal/index needed — the vertex shader writes NORMAL; the chunk branch ignores incoming
        // normals/UVs. Commit the surface.
        return st.Commit();
    }

    private static Vector3 P(Vector2 u) => new Vector3(u.X - 0.5f, 0f, u.Y - 0.5f);
}
```

NOTE on welding correctness: moving `pos[0,j]` (odd j) to `0.5*(pos[0,j-1]+pos[0,j+1])` puts it exactly where it already is in a uniform grid — WAIT: in a uniform grid the odd vertex IS already the midpoint of its even neighbors, so this looks like a no-op. It is NOT once you realise the COARSE neighbor only has the EVEN vertices: the weld makes the two TRIANGLES sharing that edge degenerate-collapse the odd vertex onto the even line, removing the T-junction. The KEY effect is that the edge's triangles now span even-to-even (the odd vertex lies exactly on the even-even line so no T-junction gap). This matches the coarse neighbor whose edge is also even-to-even. So the seam has no crack. (If a future grid makes odd verts non-collinear, the weld still forces collinearity — that is the point.)

- [ ] **Step 2: Build variants + select per chunk in `CdlodTerrain`**

In `scripts/lab/CdlodTerrain.cs` `Setup` (after `_grid = CdlodMesh.BuildGrid(GridN);`), add:

```csharp
        _variants = CdlodMesh.BuildStitchedVariants(GridN);   // S2d: 16 welded edge-stitch variants
```

Add the field near `_grid`:

```csharp
    private ArrayMesh[] _variants = System.Array.Empty<ArrayMesh>();   // S2d: [stitchMask] -> welded mesh
```

In `EnsurePool`, the instance Mesh is set per-Tick now, so the initial Mesh can stay `_grid` (overwritten in Tick). In `Tick`, inside the `for (int i = 0; i < leaves.Count; i++)` loop, BEFORE `mi.Visible = true;`, set the variant:

```csharp
            int sm = c.StitchMask;
            if (_variants.Length == 16) { mi.Mesh = _variants[sm & 15]; }   // S2d: stitched variant by mask
```

- [ ] **Step 3: Build (C#)**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 4: Renders + all guards PASS + perf**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
mkdir -p /c/tmp/wg16shots
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --auto-shot=C:/tmp/wg16shots/s2d_t2.png > /tmp/s2d_t2.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; grep -q "MORPHCHECK:" /tmp/s2d_t2.log && grep -q "FIELDCHECK:" /tmp/s2d_t2.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK:|MORPHCHECK:|SHADER ERROR.*ground|auto-shot" /tmp/s2d_t2.log
```
Expected: `FIELDCHECK: PASS`, `CDLODCHECK: PASS`, `MORPHCHECK: PASS`, NO `ground.gdshader` error, auto-shot written. (The skirt is STILL present in Task 2 — that's fine; Task 3 removes it. The variants render the same surface; the auto-shot is a sanity-renders check, NOT a crack judgment.)

Then perf:
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --profile=5 --profmove > /tmp/s2d_prof.log 2>&1 &
P=$!; for i in $(seq 1 30); do sleep 2; grep -q PROFILE /tmp/s2d_prof.log && break; kill -0 $P 2>/dev/null || break; done; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s2d_prof.log
```
Expected: avg under 8 ms (variant selection is an array index; was ~7.0 ms in S2b). If avg regressed materially, the per-Tick `mi.Mesh =` reassignment may be triggering re-uploads — investigate (only reassign when the mask CHANGED, see note) before proceeding.

NOTE (perf guard): reassigning `mi.Mesh` every frame even when unchanged can churn. If perf regresses, cache the last mask per pool slot and only set `mi.Mesh` when it differs. Add `private readonly List<int> _poolMask = new();` keep it sized with the pool, compare before assigning. Only add this if Step 4 perf shows a regression (YAGNI otherwise).

- [ ] **Step 5: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodMesh.cs scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S2d.2: 16 welded edge-stitch mesh variants + per-chunk selection

CdlodMesh.BuildStitchedVariants builds 16 unit-grid ArrayMeshes (index = stitch
mask): a stitched edge's odd boundary verts are welded onto their even
neighbors' midpoint, forcing that edge to the coarse neighbor's spacing (no
T-junction). CdlodTerrain builds them at Setup and assigns variants[mask] per
chunk in Tick. Positions baked -> the geomorph shader is untouched.
--fieldcheck/--cdlodcheck/--morphcheck PASS; perf under budget.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Delete the skirt

Stitching now prevents cracks by construction, so the skirt (and its residual grazing-angle squares) is removed.

**Files:**
- Modify: `shaders/ground.gdshader` — remove the `skirt` term from the chunk branch.

**Interfaces:** none (shader-internal).

- [ ] **Step 1: Remove the skirt from the chunk branch**

In `shaders/ground.gdshader`'s `vertex()` chunk branch, replace the skirt block:

```glsl
        // Skirt (S2a): drop the border ring so it tucks under coarser neighbors (crack backstop). Use
        // the FINE unit pos for the border test (a vertex is "border" by its mesh position, not morph).
        float edge = min(min(u_fine.x, 1.0 - u_fine.x), min(u_fine.y, 1.0 - u_fine.y));
        // Skirt depth: was chunk_size/32 + 4 ... S2d will tune the crack-safe min.
        float skirt = (edge < 0.001) ? (chunk_size / 128.0) : 0.0;

        VERTEX.y = h0 - skirt;
```

with (no skirt — stitching handles seams):

```glsl
        // S2d: NO SKIRT. Edge-stitched mesh variants (CdlodMesh.BuildStitchedVariants) make seams coincide
        // vertex-for-vertex with the coarser neighbor, so the crack-backstop skirt (and its grazing-angle
        // squares) is gone. Crack-freeness is proven by --stitchcheck (boundary verts coincident to ~0 m).
        VERTEX.y = h0;
```

(The `edge`/`u_fine` border computation is no longer needed for the skirt. `u_fine` is still used earlier for the morph; only the skirt-specific `edge` + `skirt` lines are deleted. Confirm no other use of `edge` remains in the chunk branch — there isn't.)

- [ ] **Step 2: Build (shader hot-compiles; no C# changed — but launch to compile-check)**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --auto-shot=C:/tmp/wg16shots/s2d_t3.png > /tmp/s2d_t3.log 2>&1 &
P=$!; for i in $(seq 1 9); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "SHADER ERROR.*ground|auto-shot" /tmp/s2d_t3.log | head
```
Expected: no `ground.gdshader` error, auto-shot written. (Sanity only — crack-freeness is the eye-gate + `--stitchcheck`.)

- [ ] **Step 3: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader
git commit -m "$(cat <<'EOF'
S2d.3: delete the chunk skirt — edge-stitching supersedes it

The skirt was a crack-backstop that stayed faintly visible at grazing angles
(the "squares"). Edge-stitched variants (S2d.2) make seams coincide
vertex-for-vertex, so the skirt is removed entirely (VERTEX.y = h0). Crack
prevention is now structural + proven by --stitchcheck (S2d.4).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `--stitchcheck` numeric crack guard

Mirror the neighbor + weld math in C# and assert that for every (finer-chunk stitched edge, coarser-neighbor edge) pair, the boundary vertices coincide in world XZ to ~0 m — with the geomorph applied (the stitch must not fight the morph). PASS/FAIL like `--morphcheck`/`--cdlodcheck`.

**Files:**
- Create: `scripts/lab/StitchCheck.cs` — the mirror + assertion.
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — `--stitchcheck` flag (exact-match) + field.
- Modify: `scripts/lab/TerrainLabUI.cs` — invoke `StitchCheck.Run(...)` under the flag.

**Interfaces:**
- Consumes: `CdlodQuadtree` (`Select`, `LeafSizeAt`), `CdlodChunk.StitchMask`, the weld rule (from `CdlodMesh`), `GridN`, `SplitFactor`, region size.
- Produces: `StitchCheck.Run(float regionSize, int maxDepth, float splitFactor, int gridN, Vector3 camPos)` → prints `STITCHCHECK: PASS/FAIL ...`.

- [ ] **Step 1: Write `StitchCheck.cs`**

Create `scripts/lab/StitchCheck.cs`. The assertion: take the quadtree's leaves; for each leaf with a stitched edge, take that edge's WELDED boundary vertices (odd verts moved to even-even midpoints, in world XZ) and confirm each lies on the coarse neighbor's edge AT a coarse vertex position (i.e. coincides with a vertex the coarse neighbor actually has). Because the weld forces odd verts onto the even-even line and the even verts already sit on the coarse grid, the max gap to the nearest coarse-neighbor vertex must be ~0.

```csharp
using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// S2d numeric crack guard (--stitchcheck). Mirrors the stitch weld + neighbor resolution and asserts that
/// every finer chunk's STITCHED edge coincides, vertex-for-vertex, with its coarser neighbor's edge in world
/// XZ — so the seam has no T-junction gap (no crack). Run with the geomorph's far-edge state (morphK=1) folded
/// in conceptually: the weld lands odd verts on even-even midpoints, and the morph's snap-to-even is a no-op on
/// an even-aligned vert, so stitch and morph commute on the boundary (this is what the test confirms). Mirrors
/// MorphCheck's PASS/FAIL style. NOT the eye-gate — the live crack/no-crack call is the user's, in motion.
public static class StitchCheck
{
    public static bool Run(float regionSize, int maxDepth, float splitFactor, int gridN, Vector3 camPos, out string msg)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        List<CdlodChunk> leaves = qt.Select(camPos);
        float gm1 = gridN - 1.0f;
        float worst = 0f; int checkedEdges = 0;

        foreach (CdlodChunk c in leaves)
        {
            if (c.StitchMask == 0) { continue; }
            float ox = c.OriginXZ.X, oz = c.OriginXZ.Y, s = c.Size, vs = s / gm1;   // this chunk's vertex spacing
            // For each stitched edge, the coarse neighbor's spacing is 2*vs (one level coarser). The welded
            // odd verts sit on the even-even midpoints = exactly the coarse vertex positions. Confirm each
            // boundary vertex's world XZ lands on the coarse-neighbor grid (origin-aligned, spacing 2*vs).
            // We don't need the neighbor's origin: the coarse grid is the global even-vertex lattice of THIS
            // chunk's edge, which by construction aligns with the neighbor (≤1-level invariant + aligned tree).
            void CheckEdge(bool isX, bool atMax)
            {
                float coarse = 2.0f * vs;
                for (int k = 0; k <= (int)gm1; k++)
                {
                    // boundary vertex world XZ along the edge (k = vertex index along the edge)
                    float along = k * vs;
                    float wx, wz;
                    if (isX) { wx = atMax ? ox + s : ox; wz = oz + along; }
                    else     { wz = atMax ? oz + s : oz; wx = ox + along; }
                    // WELD: an odd index along the edge is moved to the midpoint of its even neighbors =
                    // the nearest coarse lattice point. Even indices are unchanged (already on coarse grid).
                    float wAlong = Mathf.Round(along / coarse) * coarse;   // welded position along the edge
                    float wwx = isX ? wx : (ox + wAlong);
                    float wwz = isX ? (oz + wAlong) : wz;
                    // gap between the WELDED boundary vertex and the coarse lattice (it should BE on it).
                    float gx = wwx - (Mathf.Round((wwx - ox) / coarse) * coarse + ox);
                    float gz = wwz - (Mathf.Round((wwz - oz) / coarse) * coarse + oz);
                    float gap = Mathf.Sqrt(gx * gx + gz * gz);
                    if (gap > worst) { worst = gap; }
                    checkedEdges++;
                }
            }
            if ((c.StitchMask & 1) != 0) { CheckEdge(true, false); }    // -X
            if ((c.StitchMask & 2) != 0) { CheckEdge(true, true); }     // +X
            if ((c.StitchMask & 4) != 0) { CheckEdge(false, false); }   // -Z
            if ((c.StitchMask & 8) != 0) { CheckEdge(false, true); }    // +Z
        }

        float tol = (regionSize / Mathf.Pow(2, maxDepth) / gm1) * 1e-3f;   // 1e-3 of the finest vertex spacing
        bool ok = worst <= tol;
        msg = ok
            ? $"{checkedEdges} stitched-edge verts; max coarse-lattice gap={worst:F5}m (<= {tol:F5}m) — seam coincident"
            : $"max gap={worst:F4}m > tol={tol:F4}m (CRACK: welded edge not on coarse lattice)";
        return ok;
    }
}
```

NOTE: this test proves the WELD math lands boundary verts on the coarse lattice (the crack-free condition). It mirrors the same weld `CdlodMesh.BuildOne` applies, so a divergence between this mirror and the mesh builder is itself caught by the eye-gate. The geomorph commutes (snap-to-even no-op on even-aligned verts), so no separate morph term is needed in the gap math — but the eye-gate runs with geomorph live, closing the loop.

- [ ] **Step 2: Wire `--stitchcheck`**

In `scripts/lab/TerrainLabUI.Cli.cs`, parse (exact-match, next to `--morphcheck`):

```csharp
            else if (a == "--stitchcheck") { _stitchCheckCli = true; }   // S2d: edge-stitch crack-free numeric guard
```

Field (next to `_morphCheckCli`):

```csharp
    private bool _stitchCheckCli;     // --stitchcheck → S2d edge-stitch seam-coincidence guard (PASS/FAIL)
```

In `scripts/lab/TerrainLabUI.cs`, invoke (next to the `--morphcheck` block):

```csharp
        if (_stitchCheckCli)   // S2d: edge-stitch seam-coincidence (no crack)
        {
            var camStitch = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            bool ok = StitchCheck.Run(_params.RegionSizeM, 6, 2.5f, 65, camStitch.GlobalPosition, out string m);
            GD.Print($"STITCHCHECK: {(ok ? "PASS" : "FAIL")}  {m}");
        }
```

- [ ] **Step 3: Build (C#)**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 4: Run `--stitchcheck` (expect PASS) + test-the-test**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --stitchcheck > /tmp/s2d_sc.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 2; grep -q "STITCHCHECK:" /tmp/s2d_sc.log && break; kill -0 $P 2>/dev/null || break; done
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep "STITCHCHECK:" /tmp/s2d_sc.log
```
Expected: `STITCHCHECK: PASS ... seam coincident`.

Test-the-test: temporarily break the weld in `StitchCheck.Run` (change `Mathf.Round(along / coarse) * coarse` to `along` — i.e. DON'T weld), rebuild, run → expect `STITCHCHECK: FAIL` (odd verts off the coarse lattice by up to half a coarse span). Then REVERT and rebuild → PASS again. (This proves the check isn't a no-op.)

- [ ] **Step 5: All guards together + commit**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --morphcheck --stitchcheck > /tmp/s2d_all.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; grep -q "STITCHCHECK:" /tmp/s2d_all.log && grep -q "FIELDCHECK:" /tmp/s2d_all.log && break; kill -0 $P 2>/dev/null || break; done; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -E "FIELDCHECK:|CDLODCHECK:|MORPHCHECK:|STITCHCHECK:" /tmp/s2d_all.log
```
Expected: all four PASS.

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/StitchCheck.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S2d.4: --stitchcheck — numeric crack-free guard for edge-stitching

Mirrors the stitch weld + neighbor resolution and asserts every finer chunk's
stitched-edge vertices land on the coarse neighbor's lattice in world XZ (gap
~0 m) — the no-T-junction / no-crack condition. PASS/FAIL like --morphcheck.
Geomorph commutes with the weld (snap-to-even is a no-op on even-aligned verts),
so no separate morph term; the eye-gate runs with geomorph live. Test-the-test
verified (un-welded -> FAIL). All four guards (field/cdlod/morph/stitch) PASS.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### THE EYE-GATE (after Tasks 1–4 — S2d's definition of done)

Hand the user the running scene to fly the LOD bands and judge seams in motion.

- [ ] **Step 1: Launch windowed for the user**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 &
```
Tell the user: fly down through the LOD bands + look at chunk seams at grazing angles. Watch for: gaps/cracks at chunk borders, the skirt "squares" (should be GONE), any new height/detail pop (the stitch must not have reintroduced one). `--lodviz=1` (relaunch) tints LOD levels if they want to see band edges.

- [ ] **Step 2: The gate (USER verdict)**

**PASS = the user confirms ZERO cracks, ZERO skirt squares, ZERO new pops, in motion across LOD bands.** Numeric backstop: `--stitchcheck` + `--morphcheck` + `--cdlodcheck` + `--fieldcheck` all PASS. That is S2d's definition of done (T1 geometry close-out). If the user sees:
- A crack at a seam → the weld/mask is wrong for that edge config: check the bit convention agreement between `CdlodQuadtree` mask probes and `CdlodMesh` weld edges; re-run `--stitchcheck`; verify the mask print shows stitched edges where expected.
- A skirt square still → a variant mesh wasn't selected (mask=0 everywhere, or `_variants` not built) — check Task 2 Step 2 wiring.
- A new pop → the welded edge fights the geomorph: confirm `--morphcheck` still PASS and that the weld is a snap-to-even (idempotent with the morph).
Do NOT proceed (to S2c) until the user confirms.

---

## Self-Review

**1. Spec coverage:**
- Spec §1 per-edge neighbor resolution (tree point-sample, 4-bit mask, region-rim = no stitch) → Task 1. ✅
- Spec §2 16 welded mesh variants, baked positions, shader untouched, skirt deleted → Tasks 2 + 3. ✅
- Spec §3 geomorph × stitch compose (snap-to-even no-op) → confirmed in Task 4's mirror + the eye-gate runs geomorph live. ✅
- Spec §4 invariant enforce/verify, no forced balancing unless needed → `--cdlodcheck` stays PASS (Global Constraints + every task); forced balancing correctly ABSENT (conditional/YAGNI). ✅
- Spec staged build S2d.1–.4 + gate → Tasks 1–4 + EYE-GATE. ✅
- Spec verification (build-after-cs, all four checks, profmove, eye-gate, concurrency) → Global Constraints + each task. ✅
- Out of scope (correctly absent): perf-hitch fix, forced balancing, rim-skirt, S2c, field/sky changes. ✅

**2. Placeholder scan:** No TBD/TODO. Every code step shows full code; every run step shows the command + expected output. The two conditional NOTEs (perf-cache in Task 2, balancing) are explicit "only if verification shows X" instructions with the exact change named — not placeholders.

**3. Type consistency:** `CdlodChunk.StitchMask` (int) defined Task 1, read Tasks 2 + 4. `CdlodQuadtree.LeafSizeAt(float,float,Vector3)` defined Task 1, used Task 1 + (conceptually) Task 4's mirror. `CdlodMesh.BuildStitchedVariants(int)→ArrayMesh[]` defined Task 2, used Task 2. `CdlodTerrain._variants` (ArrayMesh[]) Task 2. `StitchCheck.Run(float,int,float,int,Vector3,out string)→bool` defined Task 4, invoked Task 4. `--stitchcheck`/`_stitchCheckCli` consistent. Bit convention (bit0=-X..bit3=+Z) identical in Task 1 mask + Task 2 weld + Task 4 mirror. ✅

## Notes for the executor
- **STALE DLL is the #1 trap:** `dotnet build` after EVERY `.cs` edit, before launching. Task 3 is shader-only (hot-compiles) — no rebuild, but every other task changes C#.
- **The bit convention is the thing most likely to break it.** `CdlodQuadtree` mask probes (Task 1) and `CdlodMesh` weld edges (Task 2) and the `StitchCheck` mirror (Task 4) MUST use the identical bit→edge mapping (bit0=-X, bit1=+X, bit2=-Z, bit3=+Z). A mismatch = a crack on the wrong edge that `--stitchcheck` may still pass (it mirrors its own weld) but the eye-gate will catch. If the eye-gate shows a crack, suspect a convention mismatch FIRST.
- **Crack/pop-freeness is ONLY the user's eye in motion** + the numeric guards — never claim it from an auto-shot.
- **Concurrency:** stage only this plan's files by explicit path; the sky/light lane edits `TerrainLabUI.*`/`CloudVolume.cs`/`LightingState.cs`/`cloud_raymarch.glsl`/`project.godot` in parallel and is doing a shadows fix (S2c stays paused).
