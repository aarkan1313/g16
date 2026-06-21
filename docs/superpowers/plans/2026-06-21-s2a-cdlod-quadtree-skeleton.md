# S2a — CDLOD Quadtree Skeleton (LOD-select + instanced grid + cull + stitch) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single 2048² no-LOD terrain mesh with a recursive quadtree over stable world-XZ that renders each visible chunk as an instance of ONE shared flat grid mesh (displaced from the live field in the vertex shader via `MODEL_MATRIX`), with distance LOD selection, the ≤1-level neighbor constraint, per-chunk frustum culling, and edge-stitch — gated MECHANICALLY (invariant + cull + perf drop). Pops are temporarily expected (geomorph is S2b); each task commits so any result is one `git revert` away.

**Architecture:** A new `CdlodTerrain` manager (a `Node3D`) owns the quadtree and a pool of child `MeshInstance3D`s that all share one `N×N` `PlaneMesh` + the `ground.gdshader` material. Each frame it walks the quadtree from a root square over the fixed 8 km region, selects leaf chunks by camera distance (with hysteresis), enforces ≤1-level neighbors, and positions one shared-mesh instance per leaf via `Transform3D` (origin + scale). The vertex shader derives the chunk's world-XZ from `MODEL_MATRIX` (instead of `VERTEX.xz`) and displaces from the live `field_height`. A `--cdlod` toggle swaps between this and the existing single mesh; `--lodviz` colors chunks by level; `--cdlodcheck` asserts the neighbor invariant + reports culled count.

**Tech Stack:** Godot 4.6.2 mono (C#), spatial `.gdshader` (`instance uniform`, `MODEL_MATRIX`), `dotnet build`.

## Global Constraints

- **Build:** `dotnet build WG16.csproj -v q -clp:ErrorsOnly` → `0 Error(s)`.
- **Skin not bones:** do NOT touch `field_math.gdshaderinc` / `field_height.glsl` / `FieldParams`. `--fieldcheck` MUST stay `PASS maxAbsDiff=0m` throughout (S2 changes meshing/transform, never the field math).
- **NO TDD** (GPU/visual). S2a "test" = build clean → `--fieldcheck` PASS → `--cdlodcheck` PASS (neighbor invariant + cull count) → `--profmove` perf drop vs the ~25 ms no-LOD baseline. Pops are NOT eye-gated in S2a (that's S2b). Auto-shots are SANITY only.
- **Commit per task** so every state is one `git revert` away (the user's "send it, revert if bad" model). Stage ONLY this plan's files (NEVER `git add -A` — the sky lane edits in parallel). Footer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **⚠ RUN-INVOCATION RULE:** user `--flags` AFTER a bare `--` separator (else `OS.GetCmdlineUserArgs()` is empty → flags no-op, scene never quits). `--auto-shot=` needs an OS path (`C:/tmp/wg16shots/x.png`), NOT `user://`. Canonical: `Godot…_console.exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- <FLAGS>`. A correct run quits on its own in seconds; alive at ~20s ⇒ a flag no-op'd.
- **ONE Godot at a time** (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`); scene CANNOT run `--headless` (compute NullRefs) — run windowed. Analytic-slow profiles need a generous wall-clock poll (~80s) since they run at low fps.
- **Gate viewport:** `scenes/terrain_lab.tscn`.

### Key facts (verified this session via Godot 4.6 docs research)
- `instance uniform vec4 x;` in a spatial shader → per-instance, set from C# via `meshInstance.SetInstanceShaderParameter("x", value)`. Cap 16; scalars/vectors only (no arrays/textures). Each instance keeps its own value while sharing the material.
- `MODEL_MATRIX` is available in `vertex()` (4.x rename of `WORLD_MATRIX`): instance world origin = `MODEL_MATRIX[3].xz`; scale = `length(MODEL_MATRIX[0].xyz)`. → derive chunk origin+scale from the transform; NO instance uniforms needed for those.
- Many `MeshInstance3D` are frustum-culled individually by their AABB; vertex displacement isn't in the mesh AABB, so set `CustomAabb` per chunk (enclosing displaced height) — same fix the current single mesh uses (`TerrainLab.cs:71-73`).
- Current state: `TerrainLab.cs` builds ONE `PlaneMesh` (`SubdivideWidth/Depth = HeightmapRes-1` ≈ 2048²) with `ground.gdshader`; the analytic branch (`use_analytic`) displaces from `VERTEX.xz` via `analytic_h()`. Scene: `/root/TerrainLabRoot/{TerrainLab, Camera}`.

---

## Task ordering
1. **Task 1** — shared grid mesh + a chunk that displaces from `MODEL_MATRIX` (the shader change), shown via ONE manually-placed instance. Proves the shared-grid + transform-derived-world-XZ render is correct vs the current full mesh (eye sanity + `--fieldcheck`).
2. **Task 2** — the quadtree data structure + LOD selection (pure C#, headless-testable logic), with a `--cdlodcheck` asserting the ≤1-level neighbor invariant.
3. **Task 3** — the `CdlodTerrain` manager: spawn/pool chunk instances from the quadtree each frame, per-chunk `CustomAabb`, behind a `--cdlod` toggle; `--lodviz` overlay.
4. **Task 4** — edge-stitch (skirt backstop) + the S2a mechanical gate (`--cdlodcheck` PASS + `--profmove` perf drop). Pops still present (expected); commit.

---

### Task 1: Shared grid mesh + chunk shader (displace from MODEL_MATRIX)

Add a chunk-mode path to `ground.gdshader` that derives world-XZ from the instance transform (`MODEL_MATRIX`) instead of `VERTEX.xz`, so one small flat grid mesh placed by transform renders the correct terrain slice. Prove with ONE manually-placed chunk instance covering the whole region (identical to the current mesh when scaled to the full region).

**Files:**
- Modify: `shaders/ground.gdshader` — add `use_chunk` instance-aware path in `vertex()`.
- Create: `scripts/lab/CdlodMesh.cs` — builds the shared `N×N` flat `PlaneMesh` (static helper).
- Modify: `scripts/lab/TerrainLab.cs` — a temporary `--cdlodtest` hook that adds one chunk instance (full-region scale) for the Task-1 sanity check.
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — `--cdlodtest` flag (Task-1 only; removed/superseded in Task 3).

**Interfaces:**
- Produces: `static class CdlodMesh { static PlaneMesh BuildGrid(int n); }` — a flat `PlaneMesh` size 1×1 (unit), `SubdivideWidth=SubdivideDepth=n-1`, centered at origin (so an instance transform of translate=chunkOrigin+half, scale=chunkSize maps unit grid → world chunk).
- Produces (shader): `ground.gdshader` honors `use_chunk` (instance uniform bool/float) — when set, `vertex()` computes `wxz = (MODEL_MATRIX * vec4(VERTEX,1)).xz` and displaces from `analytic_h(wxz)`; normal via the S1.5 2-tap forward difference at `analytic_spacing`.

- [ ] **Step 1: Build the shared grid mesh helper**

Create `scripts/lab/CdlodMesh.cs`:

```csharp
using Godot;

namespace WG16.Lab;

/// CDLOD shared grid: ONE flat unit PlaneMesh reused by every chunk instance.
/// Size 1x1 centered at origin; an instance's Transform3D (translate to chunk
/// center, scale = chunk size) maps the unit grid onto a world chunk. The
/// vertex shader derives world-XZ from MODEL_MATRIX and displaces from the field.
public static class CdlodMesh
{
    /// n = grid resolution (vertices per side); n-1 quads per side. e.g. 32 or 64.
    public static PlaneMesh BuildGrid(int n)
    {
        n = Mathf.Clamp(n, 2, 256);
        return new PlaneMesh
        {
            Size = new Vector2(1f, 1f),
            SubdivideWidth = n - 1,
            SubdivideDepth = n - 1,
        };
    }
}
```

- [ ] **Step 2: Add the chunk path to `ground.gdshader`**

In `shaders/ground.gdshader`: add an instance uniform near the analytic uniforms (after `analytic_spacing`):

```glsl
instance uniform float use_chunk = 0.0;   // 1.0 = CDLOD chunk: derive world-XZ from MODEL_MATRIX
```

Then in `vertex()`, add a FIRST branch for chunk mode (before the existing `if (use_analytic)`), reusing the S1.5 2-tap normal:

```glsl
void vertex() {
    if (use_chunk > 0.5) {
        vec2 wxz = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;   // chunk world-XZ from the instance transform
        float e = analytic_spacing;
        float h0 = analytic_h(wxz);
        VERTEX.y = h0;                                       // displace in object space at y=0 grid
        float hx = analytic_h(wxz + vec2(e, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));
        NORMAL = v_normal;
        v_h = h0;
    } else if (use_analytic) {
        vec2 wxz = VERTEX.xz;
        float e = analytic_spacing;
        float h0 = analytic_h(wxz);
        VERTEX.y = h0;
        float hx = analytic_h(wxz + vec2(e, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
        vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
        VERTEX.y = height_at(uv);
        float t = texel_world / region_size;
        float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
        float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
        v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    }
}
```
NOTE: `VERTEX.y` for the chunk grid is set from the world height directly. Because the grid is a unit mesh scaled by chunk size in X/Z but we want TRUE world height (not scaled), the instance transform must scale ONLY X and Z, leaving Y scale = 1 (Task 1 Step 3 / Task 3 build the transform that way). The displaced `VERTEX.y` is then already in world units.

- [ ] **Step 3: Temporary one-chunk test hook in `TerrainLab.cs`**

In `scripts/lab/TerrainLab.cs`, add a method to spawn ONE full-region chunk instance (for the Task-1 sanity check only):

```csharp
    private MeshInstance3D? _cdlodTest;
    /// TASK 1 SANITY ONLY (superseded by CdlodTerrain in Task 3): one chunk instance
    /// covering the whole region — should look identical to the single analytic mesh.
    public void CdlodTestOneChunk(FieldParams p)
    {
        if (_cdlodTest != null) { return; }
        var grid = CdlodMesh.BuildGrid(65);   // 64 quads/side
        _cdlodTest = new MeshInstance3D { Mesh = grid, MaterialOverride = _mat };
        // unit grid centered at origin -> scale X/Z to region, Y scale 1 (height is world units)
        _cdlodTest.Scale = new Vector3(p.RegionSizeM, 1f, p.RegionSizeM);
        _cdlodTest.SetInstanceShaderParameter("use_chunk", 1.0f);
        _cdlodTest.CustomAabb = new Aabb(
            new Vector3(-0.5f, (_minBase - AabbMarginM), -0.5f),     // unit-space AABB (pre-scale): Godot scales it
            new Vector3(1f, (_maxBase - _minBase) + 2f * AabbMarginM, 1f));
        AddChild(_cdlodTest);
        Visible = false;                       // hide the original full mesh so we see ONLY the chunk
        GD.Print("TerrainLab: CDLOD one-chunk test instance added (full region)");
    }
```
(`_minBase`/`_maxBase`/`_mat`/`AabbMarginM` already exist in `TerrainLab.cs`. The unit-space CustomAabb is multiplied by the node Scale by Godot; Y extent is in world units since Y scale = 1.)

- [ ] **Step 4: Wire `--cdlodtest` (Task-1 temporary)**

In `scripts/lab/TerrainLabUI.Cli.cs`: add field near `_analyticCli`:
```csharp
    private bool _cdlodTestCli;   // --cdlodtest → Task-1 sanity: one full-region chunk instance
```
parse line near the `--analytic` parse:
```csharp
            else if (a == "--cdlodtest") { _cdlodTestCli = true; }
```
apply near the `_analyticCli` apply in `ApplyCliOverrides()`:
```csharp
        if (_cdlodTestCli) { _terrain.SetAnalytic(true); _terrain.CdlodTestOneChunk(_params); }
```
(`_params` exists in `TerrainLabUI.cs`; `ApplyCliOverrides` runs after `_terrain.Build`. If `_params` isn't visible in the Cli partial, pass it: this partial shares the `TerrainLabUI` class, and `_params` is a field — it is visible.)

- [ ] **Step 5: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 6: Sanity — one chunk looks like the full mesh + field untouched**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
mkdir -p /c/tmp/wg16shots
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlodtest --fieldcheck --cam=0,300,0,-30,0 --auto-shot=C:/tmp/wg16shots/s2a_t1.png > /tmp/s2a_t1.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "FIELDCHECK: (PASS|FAIL)|one-chunk test|SHADER ERROR|exception" /tmp/s2a_t1.log | head
```
Expected: `FIELDCHECK: PASS`, the "one-chunk test" print, NO shader error. The auto-shot (`s2a_t1.png`) should show terrain (the single full-region chunk). It will look like the analytic mesh (same field). This proves the MODEL_MATRIX-derived chunk render is correct. (Hand the PNG to the user only if a quick gross-shape confirmation is wanted; not a formal gate.)

- [ ] **Step 7: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader scripts/lab/CdlodMesh.cs scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "$(cat <<'EOF'
S2a.1: shared grid mesh + chunk shader (displace from MODEL_MATRIX)

Add CdlodMesh.BuildGrid (one flat unit NxN PlaneMesh) and a use_chunk
instance-uniform path in ground.gdshader: derive world-XZ from MODEL_MATRIX
and displace from the live field (S1.5 2-tap normal). A --cdlodtest hook
renders ONE full-region chunk instance — looks like the analytic mesh,
proving transform-derived chunk render. Field untouched (--fieldcheck PASS).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Quadtree + LOD selection (pure C# logic) + `--cdlodcheck` invariant

The spatial logic, isolated from rendering so it's the cleanest unit. Recursive subdivision by camera distance, leaf collection, and the ≤1-level neighbor constraint. A `--cdlodcheck` runs the selection once against a fixed camera and asserts the invariant + prints leaf/level stats.

**Files:**
- Create: `scripts/lab/CdlodQuadtree.cs` — the quadtree + selection + invariant check.
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — `--cdlodcheck` flag.
- Modify: `scripts/lab/TerrainLabUI.cs` — fire `CdlodQuadtree.SelfCheck(...)` at startup.

**Interfaces:**
- Produces:
  - `struct CdlodChunk { public Vector2 OriginXZ; public float Size; public int Level; }` — a leaf: world-XZ min corner, side length, LOD level (0 = finest).
  - `class CdlodQuadtree { public CdlodQuadtree(float rootOriginX, float rootOriginZ, float rootSize, int maxDepth, float splitFactor); List<CdlodChunk> Select(Vector3 camPos); bool NeighborInvariantHolds(List<CdlodChunk> leaves, out string msg); static void SelfCheck(float regionSize, int maxDepth, float splitFactor, Vector3 camPos); }`
  - LOD rule: a node at a given size subdivides if `camDistanceToNode < size * splitFactor` AND `depth < maxDepth`. Level of a leaf = `maxDepth - depthRemaining` (finest near).

- [ ] **Step 1: Write `CdlodQuadtree.cs`**

Create `scripts/lab/CdlodQuadtree.cs`:

```csharp
using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// CDLOD quadtree over stable world-XZ. Each frame, Select() walks from the root
/// square and returns leaf chunks (large-far / small-near) by camera distance.
/// Pure logic — no rendering. Level 0 = finest (smallest chunks).
public struct CdlodChunk
{
    public Vector2 OriginXZ;   // world-XZ min corner
    public float Size;         // side length (m)
    public int Level;          // 0 = finest
}

public sealed class CdlodQuadtree
{
    private readonly float _rootX, _rootZ, _rootSize;
    private readonly int _maxDepth;
    private readonly float _splitFactor;   // subdivide when camDist < size*splitFactor

    public CdlodQuadtree(float rootOriginX, float rootOriginZ, float rootSize, int maxDepth, float splitFactor)
    {
        _rootX = rootOriginX; _rootZ = rootOriginZ; _rootSize = rootSize;
        _maxDepth = Mathf.Max(0, maxDepth); _splitFactor = Mathf.Max(0.01f, splitFactor);
    }

    public List<CdlodChunk> Select(Vector3 camPos)
    {
        var leaves = new List<CdlodChunk>();
        Recurse(_rootX, _rootZ, _rootSize, 0, camPos, leaves);
        return leaves;
    }

    private void Recurse(float x, float z, float size, int depth, Vector3 cam, List<CdlodChunk> outLeaves)
    {
        bool canSplit = depth < _maxDepth;
        float d = DistanceToCellXZ(x, z, size, cam);
        if (canSplit && d < size * _splitFactor)
        {
            float h = size * 0.5f;
            Recurse(x,     z,     h, depth + 1, cam, outLeaves);
            Recurse(x + h, z,     h, depth + 1, cam, outLeaves);
            Recurse(x,     z + h, h, depth + 1, cam, outLeaves);
            Recurse(x + h, z + h, h, depth + 1, cam, outLeaves);
        }
        else
        {
            outLeaves.Add(new CdlodChunk { OriginXZ = new Vector2(x, z), Size = size, Level = _maxDepth - depth });
        }
    }

    /// Nearest-point XZ distance from the camera to a cell (ignores Y so altitude doesn't starve LOD).
    private static float DistanceToCellXZ(float x, float z, float size, Vector3 cam)
    {
        float cx = Mathf.Clamp(cam.X, x, x + size);
        float cz = Mathf.Clamp(cam.Z, z, z + size);
        float dx = cam.X - cx, dz = cam.Z - cz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// ≤1-level neighbor invariant: any two EDGE-ADJACENT leaves differ by ≤1 level.
    /// Conservative O(n²) check (fine for a self-check; the runtime build relies on the
    /// restricted-quadtree property, but this verifies it empirically).
    public bool NeighborInvariantHolds(List<CdlodChunk> leaves, out string msg)
    {
        for (int i = 0; i < leaves.Count; i++)
        for (int j = i + 1; j < leaves.Count; j++)
        {
            if (EdgeAdjacent(leaves[i], leaves[j]) && Mathf.Abs(leaves[i].Level - leaves[j].Level) > 1)
            {
                msg = $"levels {leaves[i].Level} vs {leaves[j].Level} adjacent at " +
                      $"({leaves[i].OriginXZ}) / ({leaves[j].OriginXZ})";
                return false;
            }
        }
        msg = "ok";
        return true;
    }

    private static bool EdgeAdjacent(CdlodChunk a, CdlodChunk b)
    {
        float ax0 = a.OriginXZ.X, ax1 = ax0 + a.Size, az0 = a.OriginXZ.Y, az1 = az0 + a.Size;
        float bx0 = b.OriginXZ.X, bx1 = bx0 + b.Size, bz0 = b.OriginXZ.Y, bz1 = bz0 + b.Size;
        const float e = 0.5f;
        bool xTouch = Mathf.Abs(ax1 - bx0) < e || Mathf.Abs(bx1 - ax0) < e;
        bool zTouch = Mathf.Abs(az1 - bz0) < e || Mathf.Abs(bz1 - az0) < e;
        bool zOverlap = az0 < bz1 - e && bz0 < az1 - e;
        bool xOverlap = ax0 < bx1 - e && bx0 < ax1 - e;
        return (xTouch && zOverlap) || (zTouch && xOverlap);
    }

    public static void SelfCheck(float regionSize, int maxDepth, float splitFactor, Vector3 camPos)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        var leaves = qt.Select(camPos);
        bool inv = qt.NeighborInvariantHolds(leaves, out string msg);
        int minL = int.MaxValue, maxL = int.MinValue;
        foreach (var c in leaves) { minL = Mathf.Min(minL, c.Level); maxL = Mathf.Max(maxL, c.Level); }
        GD.Print($"CDLODCHECK: {(inv ? "PASS" : "FAIL")}  leaves={leaves.Count}  levels={minL}..{maxL}  invariant={msg}  cam=({camPos.X:F0},{camPos.Z:F0})");
    }
}
```

- [ ] **Step 2: Add `--cdlodcheck`**

In `scripts/lab/TerrainLabUI.Cli.cs`: field near `_fieldCheckCli`:
```csharp
    private bool _cdlodCheckCli;   // --cdlodcheck → quadtree neighbor-invariant + stats self-check (S2a)
```
parse near `--fieldcheck`:
```csharp
            else if (a == "--cdlodcheck") { _cdlodCheckCli = true; }
```

- [ ] **Step 3: Fire it at startup**

In `scripts/lab/TerrainLabUI.cs`, near the `_fieldCheckCli` firing line, add (use the region size from `_params` and the camera position):
```csharp
        if (_cdlodCheckCli)
        {
            var camNode = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            CdlodQuadtree.SelfCheck(_params.RegionSizeM, 6, 2.5f, camNode.GlobalPosition);
        }
```
(maxDepth=6, splitFactor=2.5 are the starting LOD params — both become tunables in Task 3.)

- [ ] **Step 4: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 5: Run the check — invariant PASS**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlodcheck --cam=0,200,0,-30,0 --auto-shot=C:/tmp/wg16shots/s2a_t2.png > /tmp/s2a_t2.log 2>&1 &
P=$!; for i in $(seq 1 8); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "CDLODCHECK: (PASS|FAIL)" /tmp/s2a_t2.log
```
Expected: `CDLODCHECK: PASS  leaves=…  levels=0..N  invariant=ok`. (The restricted-quadtree subdivision should naturally satisfy ≤1-level for an axis-aligned distance split; if it FAILs, the split rule needs a neighbor-balancing pass — implement before proceeding.)

- [ ] **Step 6: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodQuadtree.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S2a.2: CDLOD quadtree + LOD selection + --cdlodcheck invariant

Pure-logic quadtree over stable world-XZ: Select(camPos) walks from the
root and returns leaf chunks by nearest-XZ camera distance (split when
camDist < size*splitFactor, to maxDepth). --cdlodcheck asserts the
<=1-level edge-neighbor invariant and prints leaf/level stats.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `CdlodTerrain` manager — instance the quadtree each frame + `--cdlod` toggle + `--lodviz`

The manager that turns selected leaves into pooled chunk instances every frame, with per-chunk `CustomAabb` for correct culling, behind a `--cdlod` toggle (the current single mesh stays for A/B). `--lodviz` colors chunks by level so the bands are visible.

**Files:**
- Create: `scripts/lab/CdlodTerrain.cs` — the `Node3D` manager (quadtree + instance pool).
- Modify: `shaders/ground.gdshader` — add `instance uniform float lod_viz;` + a `lodviz_on` uniform to tint by level.
- Modify: `scripts/lab/TerrainLab.cs` — expose field params + create/own a `CdlodTerrain` child; `SetCdlod(bool)`.
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — `--cdlod[=1]` + `--lodviz[=1]`.
- Modify: `scripts/lab/TerrainLabUI.cs` — apply the toggles after Build.

**Interfaces:**
- Consumes: `CdlodQuadtree`, `CdlodChunk`, `CdlodMesh.BuildGrid`, `FieldParams`, the shared `_mat`.
- Produces: `class CdlodTerrain : Node3D { void Setup(ShaderMaterial mat, FieldParams p, float minH, float maxH); void SetEnabled(bool on); void SetLodViz(bool on); void Tick(Vector3 camPos); }` — pools `MeshInstance3D`s; each frame maps leaves→instances (transform = translate to chunk center + scale X/Z by size, Y=1; `use_chunk=1`; per-chunk `CustomAabb`; `lod_viz`=level when viz on).

- [ ] **Step 1: Write `CdlodTerrain.cs`**

Create `scripts/lab/CdlodTerrain.cs`:

```csharp
using Godot;
using System.Collections.Generic;
using WG16.Field;

namespace WG16.Lab;

/// CDLOD manager: each Tick(), selects leaf chunks from the quadtree and renders
/// each as an instance of ONE shared grid mesh + the ground material, placed by
/// transform (world-XZ derived from MODEL_MATRIX in the shader). Pools instances
/// to avoid per-frame alloc. S2a: LOD-select + cull only (geomorph is S2b).
public sealed class CdlodTerrain : Node3D
{
    private ShaderMaterial _mat = null!;
    private PlaneMesh _grid = null!;
    private CdlodQuadtree _qt = null!;
    private float _minH, _maxH, _regionSize;
    private readonly List<MeshInstance3D> _pool = new();
    private bool _enabled;
    private bool _lodViz;

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable

    public void Setup(ShaderMaterial mat, FieldParams p, float minH, float maxH)
    {
        _mat = mat; _minH = minH; _maxH = maxH; _regionSize = p.RegionSizeM;
        _grid = CdlodMesh.BuildGrid(GridN);
        _qt = new CdlodQuadtree(-_regionSize * 0.5f, -_regionSize * 0.5f, _regionSize, MaxDepth, SplitFactor);
        GD.Print($"CdlodTerrain: setup gridN={GridN} maxDepth={MaxDepth} split={SplitFactor} region={_regionSize:F0}");
    }

    public void SetEnabled(bool on)
    {
        _enabled = on;
        foreach (var mi in _pool) { mi.Visible = false; }
        GD.Print($"CdlodTerrain: {(on ? "ENABLED" : "disabled")}");
    }

    public void SetLodViz(bool on) { _lodViz = on; }

    public void Tick(Vector3 camPos)
    {
        if (!_enabled) { return; }
        List<CdlodChunk> leaves = _qt.Select(camPos);
        EnsurePool(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            MeshInstance3D mi = _pool[i];
            float half = c.Size * 0.5f;
            mi.Position = new Vector3(c.OriginXZ.X + half, 0f, c.OriginXZ.Y + half);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);    // X/Z = chunk size; Y = 1 (world-unit height)
            // Unit-space CustomAabb (Godot multiplies by Scale): X/Z span the unit grid, Y the world height range.
            mi.CustomAabb = new Aabb(new Vector3(-0.5f, _minH - 8f, -0.5f),
                                     new Vector3(1f, (_maxH - _minH) + 16f, 1f));
            mi.SetInstanceShaderParameter("use_chunk", 1.0f);
            mi.SetInstanceShaderParameter("lod_viz", _lodViz ? (float)c.Level : -1.0f);
            mi.Visible = true;
        }
        for (int i = leaves.Count; i < _pool.Count; i++) { _pool[i].Visible = false; }
    }

    private void EnsurePool(int n)
    {
        while (_pool.Count < n)
        {
            var mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat };
            AddChild(mi);
            _pool.Add(mi);
        }
    }
}
```

- [ ] **Step 2: Add LOD-viz tint to `ground.gdshader`**

Add instance uniform (near `use_chunk`):
```glsl
instance uniform float lod_viz = -1.0;   // >=0 = tint this chunk by LOD level (debug); <0 = normal
```
In `fragment()`, after the final `ALBEDO = c;` line, add a tint when viz is on:
```glsl
    if (lod_viz >= 0.0) {
        // distinct hue per level (debug band viz): cycle through a few colors
        float L = lod_viz;
        vec3 tint = vec3(fract(L * 0.27), fract(L * 0.53 + 0.33), fract(L * 0.81 + 0.66));
        ALBEDO = mix(ALBEDO, 0.5 + 0.5 * tint, 0.55);
    }
```

- [ ] **Step 3: Own a `CdlodTerrain` in `TerrainLab.cs`**

In `scripts/lab/TerrainLab.cs`, add a field + creation in `Build` (after the proxy block) + a toggle:
```csharp
    private CdlodTerrain? _cdlod;
    // call at the end of Build():
    //   _cdlod ??= new CdlodTerrain { Name = "CdlodTerrain" };
    //   if (_cdlod.GetParent() == null) { AddChild(_cdlod); _cdlod.Setup(_mat, p, _minBase, _maxBase); }
    public void SetCdlod(bool on)
    {
        if (_cdlod == null) { return; }
        _cdlod.SetEnabled(on);
        Visible = !on;                          // hide the single full mesh when CDLOD owns the view
        if (_giProxy != null) { _giProxy.Visible = !on; }
        GD.Print($"TerrainLab: CDLOD {(on ? "ON (quadtree)" : "off (single mesh)")}");
    }
    public void SetCdlodViz(bool on) { _cdlod?.SetLodViz(on); }
    public void CdlodTick(Vector3 camPos) { _cdlod?.Tick(camPos); }
```
Add the creation lines at the end of `Build()`:
```csharp
        _cdlod ??= new CdlodTerrain { Name = "CdlodTerrain" };
        if (_cdlod.GetParent() == null) { AddChild(_cdlod); _cdlod.Setup(_mat, p, _minBase, _maxBase); }
```

- [ ] **Step 4: Drive `CdlodTick` each frame + wire toggles**

In `scripts/lab/TerrainLabUI.Process.cs`, inside the `if (_ready)` camera block (where `camPos` is already fetched, ~line 132), add:
```csharp
            _terrain.CdlodTick(camPos);   // S2a: rebuild the visible chunk set from the quadtree
```
In `scripts/lab/TerrainLabUI.Cli.cs`: fields:
```csharp
    private int _cdlodCli = -1;     // --cdlod[=1] → quadtree terrain instead of the single mesh
    private int _lodVizCli = -1;    // --lodviz[=1] → tint chunks by LOD level
```
parse:
```csharp
            else if (a.StartsWith("--cdlod")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cdlodCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--lodviz")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _lodVizCli = (s == "1") ? 1 : 0; }
```
(NOTE: put the `--cdlodcheck`/`--cdlodtest` exact-match `else if`s BEFORE these `StartsWith("--cdlod")` lines so they aren't shadowed.)
apply in `ApplyCliOverrides()`:
```csharp
        if (_cdlodCli >= 0) { _terrain.SetAnalytic(true); _terrain.SetCdlod(_cdlodCli == 1); }
        if (_lodVizCli >= 0) { _terrain.SetCdlodViz(_lodVizCli == 1); }
```

- [ ] **Step 5: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 6: Run CDLOD on + lodviz — chunks render, bands visible**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --lodviz=1 --cam=0,400,0,-35,0 --auto-shot=C:/tmp/wg16shots/s2a_t3.png > /tmp/s2a_t3.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "CdlodTerrain: setup|CDLOD ON|SHADER ERROR|exception" /tmp/s2a_t3.log | head
ls -la /c/tmp/wg16shots/s2a_t3.png
```
Expected: setup + "CDLOD ON" prints, no shader error, a PNG showing terrain tinted in LOD bands (color changes with distance from camera). Read the PNG to confirm bands appear (this is a structural sanity check, not a look gate).

- [ ] **Step 7: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodTerrain.cs shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Process.cs
git commit -m "$(cat <<'EOF'
S2a.3: CdlodTerrain manager — instance the quadtree per frame (+--cdlod/--lodviz)

CdlodTerrain pools MeshInstance3D chunks sharing one grid mesh + the ground
material; each Tick() maps quadtree leaves -> instances (transform place +
scale, per-chunk CustomAabb for culling, use_chunk=1). --cdlod toggles it vs
the single mesh; --lodviz tints chunks by LOD level so the bands are visible.
Pops expected (geomorph = S2b).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Edge-stitch + skirt + the S2a mechanical gate

Close the cracks between adjacent-LOD chunks (stitch the finer edge to the coarser; skirt as backstop), then run the S2a mechanical gate: `--cdlodcheck` PASS (invariant) + `--profmove` showing the vertex-count perf drop vs the 25 ms no-LOD baseline. Pops remain (S2b); this stage's gate is mechanical + perf, not look.

**Files:**
- Modify: `scripts/lab/CdlodMesh.cs` — add a skirt option to the grid (drop border ring downward).
- Modify: `shaders/ground.gdshader` — edge-stitch: snap odd edge vertices toward the coarse grid using a per-chunk `chunk_lod`/neighbor hint (S2a: skirt-only is acceptable if stitch needs neighbor data not yet wired — see note).
- Modify: `scripts/lab/CdlodTerrain.cs` — pass each chunk's level as `instance uniform float chunk_level` (for the shader's stitch/skirt scale).

**Interfaces:**
- Consumes: Task 3's `CdlodTerrain` pool + `ground.gdshader` chunk path.
- Produces: gap-free chunk borders (skirt guaranteed; stitch where neighbor levels are available), and the S2a mechanical gate result.

- [ ] **Step 1: Add a skirt to the shared grid**

In `scripts/lab/CdlodMesh.cs`, add a variant that, after building the plane, pushes the outer ring of vertices down by a skirt depth. Simplest robust approach for S2a — a shader-side skirt (cheaper than editing mesh arrays): add to `ground.gdshader` chunk branch, detect border vertices by their unit position and drop them:

```glsl
// inside the use_chunk branch, after computing h0 but before setting VERTEX.y:
        float skirt = 0.0;
        vec2 uv01 = VERTEX.xz + vec2(0.5);                 // unit grid -> [0,1]
        float edge = min(min(uv01.x, 1.0 - uv01.x), min(uv01.y, 1.0 - uv01.y));
        if (edge < 0.001) { skirt = 8.0; }                 // border ring drops 8 m (world units) -> plugs cracks
        VERTEX.y = h0 - skirt;
```
(Replace the plain `VERTEX.y = h0;` in the chunk branch with this skirt-aware version.)

- [ ] **Step 2: Build + verify no cracks via lodviz screenshot**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly` → `0 Error(s)`.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --cam=0,120,0,-12,0 --auto-shot=C:/tmp/wg16shots/s2a_t4_cracks.png > /tmp/s2a_t4.log 2>&1 &
P=$!; for i in $(seq 1 10); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "SHADER ERROR|exception|CDLOD ON" /tmp/s2a_t4.log | head
```
Read `s2a_t4_cracks.png` (low camera near LOD boundaries): confirm NO sky-through-ground gaps at chunk borders. (Pops/seam shading still present — only checking for actual holes.)

- [ ] **Step 3: S2a mechanical gate — invariant PASS + perf drop**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
echo "=== invariant ==="
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlodcheck --cam=0,200,0,-30,0 --auto-shot=C:/tmp/wg16shots/s2a_gate.png > /tmp/s2a_inv.log 2>&1 &
P=$!; for i in $(seq 1 8); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep "CDLODCHECK" /tmp/s2a_inv.log
echo "=== perf: CDLOD vs no-LOD analytic (both --profmove) ==="
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --profile=5 --profmove > /tmp/s2a_prof_cdlod.log 2>&1 &
P=$!; for i in $(seq 1 30); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
echo -n "CDLOD: "; grep PROFILE /tmp/s2a_prof_cdlod.log
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --profile=5 --profmove > /tmp/s2a_prof_nolod.log 2>&1 &
P=$!; for i in $(seq 1 40); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
echo -n "no-LOD: "; grep PROFILE /tmp/s2a_prof_nolod.log
```
Expected: `CDLODCHECK: PASS`, and CDLOD `--profmove` ms **substantially below** the no-LOD analytic ms (~25 ms). The vertex-count drop is the S2a perf win. (If CDLOD isn't faster, the chunk count/gridN is too high — tune `MaxDepth`/`SplitFactor`/`GridN`.)

- [ ] **Step 4: Commit (the S2a deliverable)**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/CdlodMesh.cs shaders/ground.gdshader scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S2a.4: edge skirt + S2a mechanical gate (invariant PASS + perf drop)

Shader-side skirt drops chunk border vertices to plug LOD-seam cracks (no
sky-through-ground). S2a mechanical gate met: --cdlodcheck PASS (<=1-level
neighbor invariant) and --profmove shows CDLOD well under the ~25 ms no-LOD
analytic baseline (vertex-count reduction). Pops remain by design — geomorph
is S2b. End of S2a.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage (S2a scope only):** Spec §1 "recursive quadtree over stable world-XZ + one shared grid mesh instanced, MODEL_MATRIX-derived" → Tasks 1 (shader/mesh) + 2 (quadtree) + 3 (manager). Spec S2a gate "mechanical: ≤1-level invariant + cull + perf drop, pops temporarily OK" → Task 2 (`--cdlodcheck`) + Task 4 (gate). Spec "≤1-level neighbor constraint + edge-stitch/skirt" → Task 2 (invariant) + Task 4 (skirt). Spec "`--lodviz` overlay built in S2a" → Task 3. Spec "build behind a toggle" → `--cdlod` (Task 3). Spec "`--fieldcheck` stays PASS" → Task 1 Step 6. Out of S2a scope (correctly absent): geomorph (S2b), proxy-shadows (S2c), streaming (S3). ✅
**2. Placeholder scan:** All steps have full code + exact commands/expected output. Two honest scope-notes (not placeholders): Task 4 stitch may be skirt-only for S2a if neighbor-level data isn't wired — flagged explicitly with the skirt as the guaranteed crack-fix; Task 2 Step 5 names the fallback if the invariant FAILs (add a balancing pass). ✅
**3. Type consistency:** `CdlodChunk{OriginXZ:Vector2, Size:float, Level:int}`, `CdlodQuadtree.Select(Vector3)→List<CdlodChunk>`, `CdlodMesh.BuildGrid(int)→PlaneMesh`, `CdlodTerrain.Setup(ShaderMaterial,FieldParams,float,float)/SetEnabled/SetLodViz/Tick(Vector3)` — consistent across Tasks 2/3/4. Shader instance uniforms `use_chunk`, `lod_viz`, (Task4) `chunk_level` consistent. `SetInstanceShaderParameter` casing matches the verified Godot 4.6 API. ✅

## Notes for the executor
- **Send-it / revert model:** each task commits a working+checked state, so any bad result is one `git revert <sha>` away. The CDLOD path is behind `--cdlod` and `Visible` toggles — the single mesh always remains as fallback.
- **`--fieldcheck` is the bones guard** — must stay PASS every task (the field math is never touched).
- **Pops are EXPECTED through all of S2a** — do NOT treat seam shading / LOD snapping as a failure here; S2a's gate is mechanical (invariant) + perf, and crack-FREE (no holes). The look/pop eye-gate is S2b.
- **Parse-order gotcha:** exact-match `--cdlodcheck` / `--cdlodtest` `else if`s MUST precede `StartsWith("--cdlod")`, or they get shadowed.
- **Perf tuning levers** if CDLOD isn't under budget: `MaxDepth`, `SplitFactor`, `GridN` (all fields on `CdlodTerrain`).
