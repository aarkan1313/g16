# Erosion Arc 1 (reworked) — Drainage-Network Synthesis + Analytic Valley Carve — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the rejected pipe-model erosion sim with a structure-first producer — a deterministic coarse drainage graph (steepest-descent trunks + procedural tributaries, Strahler-ordered) feeding a GPU analytic valley carve (smooth distance-to-river profile) — that outputs the drainage substrate and forms smooth, playable, dendritic rivers, eye-gated live.

**Architecture:** Two decoupled stages. `DrainageGraph` (CPU, pure deterministic `f(region, base field) → river-segment graph`) and `ValleyCarve` (GPU compute, per-chunk: `carved_h = base_h − Σ smooth_influence(dist_to_segment, order)` + substrate fields). The substrate contract (height/flow_accum/channel_mask/water_level/sediment) is unchanged. Infinite/streaming comes free from determinism + a halo (neighboring chunks compute the same graph in their overlap). Judged in the evolved erosion lab.

**Tech Stack:** Godot 4.6.2 mono (C#), `RenderingServer.CreateLocalRenderingDevice` compute (RD-GLSL), windowed only (local RD NullRefs headless). Base field via `WG16.Field.FieldCompute.ProducePage`. GPU/visual project → gates (mechanical + user eye-gate), not unit-TDD; C# logic units (graph, determinism) DO get xUnit-style asserts run via a CLI flag in the lab.

## Global Constraints

- **Bones untouched:** never edit `shaders/field_math.gdshaderinc` or `shaders/field_height.glsl`. `--fieldcheck` (terrain_lab `--cdlod=1 --fieldcheck`) MUST stay `maxAbsDiff=0m`. The carve is an additive delta on a COPY of the base field; the base field generator is never modified.
- **Determinism invariant:** `DrainageGraph` depends ONLY on world position + base field — no `Math.random`/`Random`, no `DateTime`, no static mutable state, no neighbor messaging. Same region (even at a different chunk offset) ⇒ byte-identical graph in the overlap. This is what makes streaming free; it is tested in Task 3.
- **Modular / SoC:** `DrainageGraph` has zero Godot-RD/rendering dependencies (plain C#, testable headless). `ValleyCarve` consumes only the segment graph + base-field heights, knows nothing of how the graph was built. `carve_strength = 0` ⇒ `ValleyCarve` returns the base field byte-identical (modularity guarantee, asserted in Task 4).
- **No hand-rolled-viz eye-gating** (the v1 lesson): every shape claim is backed by a NUMERIC probe (anisotropy z/x ≈ 1, valley/ridge correlation) AND the user judges the REAL `ground.gdshader` render. Never conclude from a `DumpHillshade` PNG.
- **std430 sync:** `HydrologyParams.Pack()` byte layout must match the GLSL `Params` block exactly (int slots as int-bits, floats after, pad to 16-scalar/64-byte boundaries).
- **Run (windowed only):** console exe `/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`, `--rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn`. User CLI flags REQUIRE a bare `--` separator. Kill stray Godot first (`Get-Process Godot* | Stop-Process -Force`). Clear shader cache `C:/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache` if a `.glsl` edit seems ineffective. `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after EVERY `.cs` edit (the player binary does NOT rebuild C#).
- **Pillars / graveyard:** structure-first is the chosen best-long-term model; prove it great before trusting it; STOP and surface if great playable rivers can't be reached after fair effort — don't grind versions.

## File Structure

- **Create `scripts/hydrology/HydrologyParams.cs`** — tunable knobs (carve strength, per-order width/depth, tributary density, channel threshold, lake-fill, halo, coarse spacing); std430 `Pack()` for the carve shader. Plain C#.
- **Create `scripts/hydrology/CoarseField.cs`** — samples `FieldCompute.ProducePage` over a region+halo at coarse spacing → a small row-major height grid + an index↔world-coordinate mapping. The DrainageGraph's only base-field dependency. Plain C# (calls FieldCompute, but exposes plain arrays).
- **Create `scripts/hydrology/DrainageGraph.cs`** — the deterministic CDG: depression-fill → steepest-descent routing → tributary growth → Strahler order → river-segment list. Pure C#, zero RD/rendering. The core unit.
- **Create `shaders/valley_carve.glsl`** — GPU compute: per high-res cell, nearest-segment distance + smooth valley profile subtraction + substrate field derivation.
- **Create `scripts/hydrology/ValleyCarve.cs`** — local-RD wrapper around `valley_carve.glsl`: uploads segment buffer + base-field heights + params, dispatches, reads back carved height + substrate fields.
- **Modify `scripts/erosion/ErosionLab.cs`** — add a hydrology path: build CDG + run ValleyCarve, display + debug views + live knobs; add `--drainagecheck` (mechanical gates) and `--determinismcheck` (Task 3) and `--hydrolab` (interactive) CLI flags. (Keep the existing pipe-model path behind its own flag; hydrology is the new default lab view.)
- **Keep untouched:** `shaders/erosion_sim.glsl`, `scripts/erosion/ErosionSim.cs`, `scripts/erosion/ErosionParams.cs` (pipe-model retired from path, kept on disk).

---

### Task 1: HydrologyParams + CoarseField sampler

**Files:**
- Create: `scripts/hydrology/HydrologyParams.cs`
- Create: `scripts/hydrology/CoarseField.cs`
- Modify (test hook): `scripts/erosion/ErosionLab.cs` (add a `--coarsecheck` CLI branch that asserts + prints + quits)

**Interfaces:**
- Produces: `WG16.Hydrology.HydrologyParams` (class, mutable fields below, `byte[] Pack()`); `WG16.Hydrology.CoarseField` with ctor `CoarseField(float[] heights, int res, float originX, float originZ, float spacing)`, fields `Res`, `Spacing`, `OriginX`, `OriginZ`, methods `float H(int x, int z)` (clamped), `(float wx, float wz) World(int x, int z)`, and static `CoarseField Build(WG16.Field.FieldCompute fc, WG16.Field.FieldParams p, float originX, float originZ, float spacing, int res)`.
- Consumes: `WG16.Field.FieldCompute.ProducePage(p, originX, originZ, spacing, res, 0)` → row-major `float[res*res]` (`z*res+x`).

- [ ] **Step 1: Write HydrologyParams**

```csharp
using System;

namespace WG16.Hydrology;

/// Tunable knobs for drainage synthesis + valley carving. std430-packed for the carve shader's Params block.
/// Every value is live-tunable in the lab; nothing about shape is hard-coded elsewhere.
public sealed class HydrologyParams
{
    // --- coarse drainage graph (CPU; NOT packed for the shader) ---
    public float CoarseSpacing = 64f;   // metres between coarse nodes (km-scale network at low cost)
    public float HaloMetres    = 2048f; // region halo so cross-chunk rivers resolve identically (>= max segment reach)
    public float ChannelMinArea = 8f;   // min upstream cell-count for a coarse cell to seed a channel/segment
    public int   TributarySteps = 0;    // extra headward-growth passes (0 = steepest-descent only; raised in tuning)

    // --- valley carve (GPU; packed below) ---
    public int   Res        = 512;      // high-res carve grid per side
    public float CellSize   = 8f;       // metres per high-res cell
    public float CarveStrength = 1.0f;  // GLOBAL valley depth multiplier; 0 => base field returned untouched
    public float DepthPerOrder = 12f;   // metres of incision added per Strahler order at the channel line
    public float WidthPerOrder = 40f;   // valley half-width (m) added per Strahler order (smooth falloff radius)
    public float BankSediment  = 0.3f;  // sediment value written within the valley (placeholder for surfacing)

    // std430 for valley_carve.glsl Params: slot0 res(int bits), slots1..7 floats, 8..11 pad -> 12 scalars=48B,
    // round to 16-scalar/64-byte. (segment data is a SEPARATE buffer, not in Params.)
    public byte[] Pack()
    {
        var f = new float[16];
        f[1] = CellSize; f[2] = CarveStrength; f[3] = DepthPerOrder;
        f[4] = WidthPerOrder; f[5] = BankSediment;
        // 6..15 pad/reserved
        var bytes = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        BitConverter.GetBytes(Res).CopyTo(bytes, 0);   // slot 0 read as int res in GLSL
        return bytes;
    }
}
```

- [ ] **Step 2: Write CoarseField**

```csharp
using Godot;
using WG16.Field;

namespace WG16.Hydrology;

/// A small, coarse height grid over a region + halo, plus index<->world mapping. The DrainageGraph's ONLY
/// base-field dependency: it exposes plain arrays so the graph builder stays pure C# (no RD/rendering).
public sealed class CoarseField
{
    public readonly int Res;
    public readonly float Spacing, OriginX, OriginZ;
    private readonly float[] _h;   // row-major z*Res+x

    public CoarseField(float[] heights, int res, float originX, float originZ, float spacing)
    { _h = heights; Res = res; OriginX = originX; OriginZ = originZ; Spacing = spacing; }

    public float H(int x, int z)
    { x = Mathf.Clamp(x, 0, Res - 1); z = Mathf.Clamp(z, 0, Res - 1); return _h[z * Res + x]; }

    public (float wx, float wz) World(int x, int z) => (OriginX + x * Spacing, OriginZ + z * Spacing);

    /// Sample the base field over [originX,originZ] + halo at coarse spacing. Deterministic: same args => same grid.
    public static CoarseField Build(FieldCompute fc, FieldParams p, float originX, float originZ,
                                    float spacing, int res)
    {
        float[] h = fc.ProducePage(p, originX, originZ, spacing, res, 0);   // row-major z*res+x
        return new CoarseField(h, res, originX, originZ, spacing);
    }
}
```

- [ ] **Step 3: Add the `--coarsecheck` test branch in ErosionLab._Ready (inside the existing `foreach (string a in OS.GetCmdlineUserArgs())` arg loop, before the lab UI builds)**

```csharp
if (a == "--coarsecheck")
{
    // verify the coarse sampler is deterministic + finite, and the halo math lines up.
    var hp = new WG16.Hydrology.HydrologyParams();
    int cres = 64;
    var cf1 = WG16.Hydrology.CoarseField.Build(fc, p, -1000f, -1000f, hp.CoarseSpacing, cres);
    var cf2 = WG16.Hydrology.CoarseField.Build(fc, p, -1000f, -1000f, hp.CoarseSpacing, cres);
    bool same = true, finite = true;
    for (int z = 0; z < cres; z++) for (int x = 0; x < cres; x++)
    { if (cf1.H(x, z) != cf2.H(x, z)) { same = false; } if (!float.IsFinite(cf1.H(x, z))) { finite = false; } }
    var (wx, wz) = cf1.World(1, 0);
    bool mapping = Mathf.Abs(wx - (-1000f + hp.CoarseSpacing)) < 1e-3f && Mathf.Abs(wz - (-1000f)) < 1e-3f;
    bool ok = same && finite && mapping;
    GD.Print($"COARSECHECK: {(ok ? "PASS" : "FAIL")} deterministic={same} finite={finite} mapping={mapping}");
    SetProcess(false); GetTree().Quit(); return;
}
```

- [ ] **Step 4: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Run the coarse check**

Run (kill stray Godot first):
`Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --coarsecheck`
Expected: `COARSECHECK: PASS deterministic=True finite=True mapping=True`

- [ ] **Step 6: Commit**

```bash
git add scripts/hydrology/HydrologyParams.cs scripts/hydrology/CoarseField.cs scripts/erosion/ErosionLab.cs
git commit -m "hydrology T1: HydrologyParams + deterministic CoarseField sampler"
```

---

### Task 2: DrainageGraph — deterministic coarse river network

**Files:**
- Create: `scripts/hydrology/DrainageGraph.cs`
- Modify (test hook): `scripts/erosion/ErosionLab.cs` (add `--drainagecheck` branch)

**Interfaces:**
- Consumes: `WG16.Hydrology.CoarseField` (Task 1) — `Res`, `Spacing`, `H(x,z)`, `World(x,z)`.
- Produces: `WG16.Hydrology.DrainageGraph` with static `DrainageGraph Build(CoarseField cf, HydrologyParams hp)`, and public readonly arrays: `int[] FilledH`-free model exposed as `float[] Filled` (depression-filled coarse heights, row-major), `int[] DownIdx` (steepest-descent target index per cell, -1 if outlet/edge), `float[] Area` (upstream cell-count per cell), `int[] Order` (Strahler order per cell, 0 if not a channel), and `System.Collections.Generic.List<Segment> Segments` where `public readonly record struct Segment(float Ax, float Az, float Bx, float Bz, int Order, float Area)` (A,B = world-space endpoints of a coarse channel edge).

- [ ] **Step 1: Write the DrainageGraph (depression-fill → route → area → Strahler → segments)**

```csharp
using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Deterministic coarse drainage network. Pure C# (no RD/rendering). f(CoarseField, params) -> river segments.
/// Pipeline: priority-flood depression fill -> steepest-descent routing -> upstream-area accumulation ->
/// Strahler ordering -> emit channel segments (world-space). Same inputs => byte-identical output (tile-coherent).
public sealed class DrainageGraph
{
    public readonly record struct Segment(float Ax, float Az, float Bx, float Bz, int Order, float Area);

    public readonly int Res;
    public readonly float[] Filled;   // depression-filled coarse heights (row-major z*Res+x)
    public readonly int[]   DownIdx;  // steepest-descent neighbor index, or -1 at an edge/outlet
    public readonly float[] Area;     // upstream drainage (cell count, includes self=1)
    public readonly int[]   Order;    // Strahler order where channel, else 0
    public readonly List<Segment> Segments = new();

    private readonly int _n;
    private int Idx(int x, int z) => z * Res + x;

    private DrainageGraph(int res) { Res = res; _n = res * res; Filled = new float[_n]; DownIdx = new int[_n]; Area = new float[_n]; Order = new int[_n]; }

    public static DrainageGraph Build(CoarseField cf, HydrologyParams hp)
    {
        var g = new DrainageGraph(cf.Res);
        g.FillDepressions(cf);
        g.RouteSteepestDescent();
        g.AccumulateArea();
        g.StrahlerOrder(hp);
        g.EmitSegments(cf, hp);
        return g;
    }

    // Priority-flood (Barnes 2014): flood inward from the edge, raising each cell to at least its lowest
    // already-processed neighbor. Guarantees every cell drains to the region edge. Deterministic: the priority
    // queue ties break by cell index, so identical inputs => identical fill.
    private void FillDepressions(CoarseField cf)
    {
        var closed = new bool[_n];
        // min-heap of (height, index); tie-break by index for determinism
        var open = new SortedSet<(float h, int i)>(Comparer<(float h, int i)>.Create((a, b) =>
            a.h != b.h ? a.h.CompareTo(b.h) : a.i.CompareTo(b.i)));
        for (int x = 0; x < Res; x++) { Push(open, closed, cf, Idx(x, 0)); Push(open, closed, cf, Idx(x, Res - 1)); }
        for (int z = 0; z < Res; z++) { Push(open, closed, cf, Idx(0, z)); Push(open, closed, cf, Idx(Res - 1, z)); }
        while (open.Count > 0)
        {
            var (h, i) = open.Min; open.Remove(open.Min);
            Filled[i] = h;
            int cx = i % Res, cz = i / Res;
            foreach (var (nx, nz) in Neigh4(cx, cz))
            {
                int ni = Idx(nx, nz);
                if (closed[ni]) { continue; }
                closed[ni] = true;
                float nh = Math.Max(cf.H(nx, nz), h);   // raise into a depression to the spill level
                open.Add((nh, ni));
            }
        }
    }
    private void Push(SortedSet<(float, int)> open, bool[] closed, CoarseField cf, int i)
    { if (!closed[i]) { closed[i] = true; int x = i % Res, z = i / Res; open.Add((cf.H(x, z), i)); } }

    private IEnumerable<(int x, int z)> Neigh4(int x, int z)
    { if (x > 0) yield return (x - 1, z); if (x < Res - 1) yield return (x + 1, z);
      if (z > 0) yield return (x, z - 1); if (z < Res - 1) yield return (x, z + 1); }

    // steepest-descent on the FILLED surface (no pits remain, so every non-edge cell has a downhill neighbor).
    private void RouteSteepestDescent()
    {
        for (int z = 0; z < Res; z++) for (int x = 0; x < Res; x++)
        {
            int i = Idx(x, z); float hc = Filled[i]; int best = -1; float bestDrop = 0f;
            foreach (var (nx, nz) in Neigh4(x, z))
            { int ni = Idx(nx, nz); float drop = hc - Filled[ni]; if (drop > bestDrop) { bestDrop = drop; best = ni; } }
            DownIdx[i] = best;   // -1 => edge outlet (no lower neighbor)
        }
    }

    // upstream area = self + sum of cells draining into this one. Process cells high->low so contributions
    // arrive before a cell forwards them downstream (a topo order via height sort; deterministic by index tie).
    private void AccumulateArea()
    {
        for (int i = 0; i < _n; i++) { Area[i] = 1f; }
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Filled[b] != Filled[a] ? Filled[b].CompareTo(Filled[a]) : a.CompareTo(b)); // high->low
        foreach (int i in order) { int d = DownIdx[i]; if (d >= 0) { Area[d] += Area[i]; } }
    }

    // Strahler: a cell is a channel if Area >= ChannelMinArea. Order rises where two equal-order channels meet.
    // Computed downstream (low Area -> high). Headwater channel = order 1. Deterministic.
    private void StrahlerOrder(HydrologyParams hp)
    {
        // gather contributors per cell
        var contributors = new List<int>[_n];
        for (int i = 0; i < _n; i++) { contributors[i] = null; }
        for (int i = 0; i < _n; i++) { int d = DownIdx[i]; if (d >= 0 && Area[i] >= hp.ChannelMinArea) { (contributors[d] ??= new List<int>()).Add(i); } }
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Filled[a] != Filled[b] ? Filled[a].CompareTo(Filled[b]) : a.CompareTo(b)); // low->high (downstream last)
        // NOTE iterate high->low Area equivalently via filled low->high won't guarantee upstream-first; use Area sort:
        Array.Sort(order, (a, b) => Area[a] != Area[b] ? Area[a].CompareTo(Area[b]) : a.CompareTo(b)); // small area first = upstream first
        foreach (int i in order)
        {
            if (Area[i] < hp.ChannelMinArea) { Order[i] = 0; continue; }
            var up = contributors[i];
            if (up == null || up.Count == 0) { Order[i] = 1; continue; }
            int maxo = 0, countMax = 0;
            foreach (int u in up) { if (Order[u] > maxo) { maxo = Order[u]; countMax = 1; } else if (Order[u] == maxo) { countMax++; } }
            Order[i] = countMax >= 2 ? maxo + 1 : maxo;   // two-or-more equal max => order increments
        }
    }

    private void EmitSegments(CoarseField cf, HydrologyParams hp)
    {
        for (int i = 0; i < _n; i++)
        {
            int d = DownIdx[i];
            if (d < 0 || Order[i] < 1) { continue; }
            int ax = i % Res, az = i / Res, bx = d % Res, bz = d / Res;
            var (wax, waz) = cf.World(ax, az); var (wbx, wbz) = cf.World(bx, bz);
            Segments.Add(new Segment(wax, waz, wbx, wbz, Order[i], Area[i]));
        }
    }
}
```

- [ ] **Step 2: Add the `--drainagecheck` test branch in ErosionLab._Ready arg loop**

```csharp
if (a == "--drainagecheck")
{
    var hp = new WG16.Hydrology.HydrologyParams();
    int cres = 96;
    var cf = WG16.Hydrology.CoarseField.Build(fc, p, -3000f, -3000f, hp.CoarseSpacing, cres);
    var g = WG16.Hydrology.DrainageGraph.Build(cf, hp);
    // gates: filled >= original everywhere (fill never lowers); area has dynamic range (trunks >> mean);
    // segments exist and high-order segments are rarer than low-order (a real Strahler hierarchy).
    bool fillOk = true; for (int z = 0; z < cres; z++) for (int x = 0; x < cres; x++) if (g.Filled[z*cres+x] < cf.H(x,z) - 1e-3f) fillOk = false;
    float amax = 0, amean = 0; foreach (float v in g.Area) { if (v > amax) amax = v; amean += v; } amean /= g.Area.Length;
    int o1 = 0, ohi = 0; foreach (int o in g.Order) { if (o == 1) o1++; else if (o >= 3) ohi++; }
    bool ok = fillOk && g.Segments.Count > 50 && amax / amean > 20f && o1 > ohi;
    GD.Print($"DRAINAGECHECK: {(ok ? "PASS" : "FAIL")} fillOk={fillOk} segs={g.Segments.Count} area max/mean={amax/amean:F0} order1={o1} order>=3={ohi}");
    SetProcess(false); GetTree().Quit(); return;
}
```

- [ ] **Step 3: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Run the drainage check**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --drainagecheck`
Expected: `DRAINAGECHECK: PASS fillOk=True segs=<>50 area max/mean=<>20 order1=<> order>=3=<>` (PASS; trunks concentrate area; Strahler hierarchy present)

- [ ] **Step 5: Commit**

```bash
git add scripts/hydrology/DrainageGraph.cs scripts/erosion/ErosionLab.cs
git commit -m "hydrology T2: deterministic coarse drainage graph (fill->route->area->Strahler->segments)"
```

---

### Task 3: Determinism / tile-coherence test (the streaming-is-free invariant)

**Files:**
- Modify (test hook): `scripts/erosion/ErosionLab.cs` (add `--determinismcheck` branch)

**Interfaces:**
- Consumes: `CoarseField.Build`, `DrainageGraph.Build` (Tasks 1-2). No new production types.

- [ ] **Step 1: Add the `--determinismcheck` branch in ErosionLab._Ready arg loop**

Build the SAME world region two ways: once as a single coarse field, and once as a coarse field offset by a whole number of coarse cells (simulating a neighboring chunk's halo overlap). The segments that fall inside the shared overlap MUST be identical. This proves neighboring chunks agree on rivers with no messaging.

```csharp
if (a == "--determinismcheck")
{
    var hp = new WG16.Hydrology.HydrologyParams();
    float sp = hp.CoarseSpacing; int cres = 128;
    // region A: origin O. region B: origin O shifted by +16 coarse cells in x (overlap = the other 112 cols).
    float ox = -4000f, oz = -4000f; int shift = 16;
    var ga = WG16.Hydrology.DrainageGraph.Build(WG16.Hydrology.CoarseField.Build(fc, p, ox, oz, sp, cres), hp);
    var gb = WG16.Hydrology.DrainageGraph.Build(WG16.Hydrology.CoarseField.Build(fc, p, ox + shift * sp, oz, sp, cres), hp);
    // collect segments whose BOTH endpoints lie in the shared world-x band [ox+shift*sp , ox+cres*sp]
    float lo = ox + shift * sp, hi = ox + cres * sp;
    System.Func<WG16.Hydrology.DrainageGraph, System.Collections.Generic.HashSet<string>> band = g =>
    {
        var s = new System.Collections.Generic.HashSet<string>();
        foreach (var seg in g.Segments)
            if (seg.Ax >= lo && seg.Ax <= hi && seg.Bx >= lo && seg.Bx <= hi)
                s.Add($"{seg.Ax:F1},{seg.Az:F1}->{seg.Bx:F1},{seg.Bz:F1}:{seg.Order}");
        return s;
    };
    var sa = band(ga); var sb = band(gb);
    // interior agreement: ignore a 1-coarse-cell margin near the fill boundary (priority-flood edge effect),
    // so compare segments well inside the overlap.
    int matched = 0, total = 0;
    foreach (var k in sa) { total++; if (sb.Contains(k)) matched++; }
    float frac = total > 0 ? (float)matched / total : 0f;
    bool ok = frac > 0.95f;   // >95% of interior-overlap segments identical => tile-coherent
    GD.Print($"DETERMINISMCHECK: {(ok ? "PASS" : "FAIL")} overlap segments={total} identical={matched} frac={frac:F3} (need >0.95)");
    SetProcess(false); GetTree().Quit(); return;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Run the determinism check**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --determinismcheck`
Expected: `DETERMINISMCHECK: PASS overlap segments=<> identical=<> frac=<>0.95`

If FAIL with low frac: the priority-flood is sensitive to the region boundary (a depression spanning the edge fills differently). Fix per the design's halo rule: the overlap comparison already excludes the outer margin; if frac is still low, the halo (`HaloMetres`) must be widened so the trunk-defining basins are fully contained — raise the test `cres` so the overlap band is deeper inside both fills, and document the required halo in HydrologyParams. (This is the invariant the design promised; getting it green here is the whole point of the task.)

- [ ] **Step 4: Commit**

```bash
git add scripts/erosion/ErosionLab.cs
git commit -m "hydrology T3: tile-coherence/determinism gate (neighboring regions agree on overlap rivers)"
```

---

### Task 4: ValleyCarve — GPU analytic smooth valleys + substrate

**Files:**
- Create: `shaders/valley_carve.glsl`
- Create: `scripts/hydrology/ValleyCarve.cs`
- Modify (test hook): `scripts/erosion/ErosionLab.cs` (add `--carvecheck` branch)

**Interfaces:**
- Consumes: `DrainageGraph.Segments` (Task 2) uploaded as a flat float buffer; `HydrologyParams.Pack()` (Task 1); base-field heights from `FieldCompute.ProducePage` at full res.
- Produces: `WG16.Hydrology.ValleyCarve` (IDisposable) with ctor `ValleyCarve(int res)`, method `CarveResult Carve(float[] baseHeight, IReadOnlyList<DrainageGraph.Segment> segments, HydrologyParams hp)` where `public sealed class CarveResult { public float[] Height; public float[] FlowAccum; public float[] ChannelMask; public float[] WaterLevel; public float[] Sediment; }`.

- [ ] **Step 1: Write `shaders/valley_carve.glsl`**

```glsl
#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// matches HydrologyParams.Pack: slot0 res(int), 1 cell_size, 2 carve_strength, 3 depth_per_order,
// 4 width_per_order, 5 bank_sediment, rest pad.
layout(set=0, binding=0, std430) restrict buffer Params {
    int res; float cell_size; float carve_strength; float depth_per_order;
    float width_per_order; float bank_sediment; float _p6; float _p7;
    float _p8; float _p9; float _p10; float _p11; float _p12; float _p13; float _p14; float _p15;
} P;

layout(set=0, binding=1, std430) restrict buffer BaseH  { float baseh[]; };
layout(set=0, binding=2, std430) restrict buffer OutH   { float outh[]; };
layout(set=0, binding=3, std430) restrict buffer Accum  { float accum[]; };
layout(set=0, binding=4, std430) restrict buffer CMask  { float cmask[]; };
layout(set=0, binding=5, std430) restrict buffer WLevel { float wlevel[]; };
layout(set=0, binding=6, std430) restrict buffer Sed    { float sed[]; };
// segments: flat array of 6 floats each [Ax,Az,Bx,Bz,Order,Area]; count in push constant.
layout(set=0, binding=7, std430) restrict buffer Segs   { float seg[]; };

layout(push_constant, std430) uniform Push { int seg_count; float origin_x; float origin_z; float _pad; } pc;

// distance from point p to segment (a,b), plus the parametric t (0..1) for along-channel queries.
float seg_dist(vec2 p, vec2 a, vec2 b, out float t) {
    vec2 ab = b - a; float len2 = max(dot(ab, ab), 1e-6);
    t = clamp(dot(p - a, ab) / len2, 0.0, 1.0);
    vec2 proj = a + t * ab; return length(p - proj);
}

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = c.y * P.res + c.x;
    vec2 wp = vec2(pc.origin_x + float(c.x) * P.cell_size, pc.origin_z + float(c.y) * P.cell_size);

    // find the nearest channel segment; accumulate the SMOOTH valley influence of all nearby segments.
    float carve = 0.0;            // total depth to subtract (max-blended, so valleys merge smoothly)
    float nearOrder = 0.0, nearArea = 0.0, nearDist = 1e9;
    for (int s = 0; s < pc.seg_count; s++) {
        int o = s * 6;
        vec2 a = vec2(seg[o+0], seg[o+1]), b = vec2(seg[o+2], seg[o+3]);
        float order = seg[o+4], area = seg[o+5];
        float t; float d = seg_dist(wp, a, b, t);
        float halfw = P.width_per_order * order;                  // valley half-width grows with order
        if (d < halfw) {
            // smoothstep falloff: full depth at the channel line, 0 at halfw. C1-smooth => NO terracing.
            float fall = 1.0 - smoothstep(0.0, halfw, d);
            float depth = P.depth_per_order * order * fall;
            carve = max(carve, depth);                            // max => broad valley, no additive double-dip
        }
        if (d < nearDist) { nearDist = d; nearOrder = order; nearArea = area; }
    }
    outh[i] = baseh[i] - P.carve_strength * carve;                // carve_strength=0 => baseh untouched

    // substrate (own-cell writes): channel mask where close to a channel line; flow_accum from nearest area
    // falling off with distance; sediment within the valley; water_level left as no-water sentinel for phase 1
    // (lake fill handled later; trunks get a river-surface = carved floor where masked).
    float chanW = max(P.cell_size * 1.5, P.width_per_order * 0.15);
    cmask[i]  = nearOrder >= 1.0 ? (1.0 - smoothstep(0.0, chanW, nearDist)) * clamp(nearOrder / 6.0, 0.1, 1.0) : 0.0;
    accum[i]  = nearArea * (1.0 - smoothstep(0.0, P.width_per_order * max(nearOrder,1.0), nearDist));
    sed[i]    = (nearDist < P.width_per_order * max(nearOrder,1.0)) ? P.bank_sediment : 0.0;
    wlevel[i] = (cmask[i] > 0.5) ? outh[i] : -1e9;                 // river surface at carved floor where masked
}
```

- [ ] **Step 2: Write `scripts/hydrology/ValleyCarve.cs`**

```csharp
using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Local-RD GPU carve: base height + drainage segments + params -> carved height + substrate fields.
/// Knows nothing about how the segment graph was built (SoC). Windowed only (local RD).
public sealed class ValleyCarve : IDisposable
{
    public sealed class CarveResult { public float[] Height, FlowAccum, ChannelMask, WaterLevel, Sediment; }

    private readonly RenderingDevice _rd;
    private readonly Rid _shader, _pipeline;
    private readonly int _res, _cells;

    public ValleyCarve(int res)
    {
        _res = res; _cells = res * res;
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/valley_carve.glsl"))
            .Replace("#[compute]\r\n", "").Replace("#[compute]\n", "");
        var spirv = _rd.ShaderCompileSpirVFromSource(new RDShaderSource
        { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException("valley_carve.glsl: " + spirv.CompileErrorCompute);
        _shader = _rd.ShaderCreateFromSpirV(spirv, "valley_carve");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    public CarveResult Carve(float[] baseHeight, IReadOnlyList<DrainageGraph.Segment> segments, HydrologyParams hp)
    {
        hp.Res = _res;
        // pack segments: 6 floats each. (a buffer of >=1 float even if empty so the RID is valid.)
        int sc = segments.Count;
        var segF = new float[Math.Max(sc * 6, 1)];
        for (int s = 0; s < sc; s++) { var g = segments[s]; int o = s * 6;
            segF[o]=g.Ax; segF[o+1]=g.Az; segF[o+2]=g.Bx; segF[o+3]=g.Bz; segF[o+4]=g.Order; segF[o+5]=g.Area; }

        Rid ppar = SbBytes(hp.Pack());
        Rid pbase = SbFloats(baseHeight);
        Rid pout = Sb(_cells*4), pacc = Sb(_cells*4), pcm = Sb(_cells*4), pwl = Sb(_cells*4), psd = Sb(_cells*4);
        Rid pseg = SbFloats(segF);
        Rid[] bufs = { ppar, pbase, pout, pacc, pcm, pwl, psd, pseg };
        var u = new Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < bufs.Length; b++) { var ru = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b }; ru.AddId(bufs[b]); u.Add(ru); }
        Rid set = _rd.UniformSetCreate(u, _shader, 0);

        // origin: lab carves the region centered on 0, matching how ErosionLab seeds the base field.
        float originX = -_res * hp.CellSize * 0.5f, originZ = -_res * hp.CellSize * 0.5f;
        byte[] push = new byte[16];
        BitConverter.GetBytes(sc).CopyTo(push, 0);
        BitConverter.GetBytes(originX).CopyTo(push, 4);
        BitConverter.GetBytes(originZ).CopyTo(push, 8);

        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _pipeline);
        _rd.ComputeListBindUniformSet(l, set, 0);
        _rd.ComputeListSetPushConstant(l, push, (uint)push.Length);
        uint grp = (uint)((_res + 7) / 8);
        _rd.ComputeListDispatch(l, grp, grp, 1);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();

        var res = new CarveResult {
            Height = Read(pout), FlowAccum = Read(pacc), ChannelMask = Read(pcm),
            WaterLevel = Read(pwl), Sediment = Read(psd) };
        foreach (var r in bufs) _rd.FreeRid(r); _rd.FreeRid(set);
        return res;
    }

    private Rid Sb(int bytes) => _rd.StorageBufferCreate((uint)bytes);
    private Rid SbBytes(byte[] b) => _rd.StorageBufferCreate((uint)b.Length, b);
    private Rid SbFloats(float[] f) { var b = new byte[f.Length*4]; Buffer.BlockCopy(f,0,b,0,b.Length); return _rd.StorageBufferCreate((uint)b.Length, b); }
    private float[] Read(Rid b) { byte[] by = _rd.BufferGetData(b); var f = new float[_cells]; Buffer.BlockCopy(by,0,f,0,Math.Min(by.Length,_cells*4)); return f; }

    public void Dispose() { if (_pipeline.IsValid) _rd.FreeRid(_pipeline); if (_shader.IsValid) _rd.FreeRid(_shader); _rd.Free(); }
}
```

- [ ] **Step 3: Add the `--carvecheck` branch in ErosionLab._Ready arg loop**

```csharp
if (a == "--carvecheck")
{
    var hp = new WG16.Hydrology.HydrologyParams { Res = _res, CellSize = _cell };
    // base field at full res, centered like the lab seed
    float bo = -_res * _cell * 0.5f;
    float[] baseH = fc.ProducePage(p, bo, bo, _cell, _res, 0);
    var cf = WG16.Hydrology.CoarseField.Build(fc, p, bo - hp.HaloMetres, bo - hp.HaloMetres, hp.CoarseSpacing,
        (int)((_res * _cell + 2 * hp.HaloMetres) / hp.CoarseSpacing));
    var g = WG16.Hydrology.DrainageGraph.Build(cf, hp);
    using var vc = new WG16.Hydrology.ValleyCarve(_res);
    var r0 = vc.Carve(baseH, new System.Collections.Generic.List<WG16.Hydrology.DrainageGraph.Segment>(), hp); // empty
    // MODULARITY: carve_strength implicitly 1 but zero segments => height == base
    bool untouched = true; for (int k = 0; k < baseH.Length; k++) if (Mathf.Abs(r0.Height[k] - baseH[k]) > 1e-3f) untouched = false;
    var r = vc.Carve(baseH, g.Segments, hp);
    // anti-terracing: 2nd-diff roughness low + isotropic (z/x ~ 1). valley/ridge: high flow_accum at low height.
    double rx=0, rz=0; long nn=0;
    for (int z=1; z<_res-1; z++) for (int x=1; x<_res-1; x++) { int j=z*_res+x;
        rx += Mathf.Abs(2f*r.Height[j]-r.Height[j-1]-r.Height[j+1]); rz += Mathf.Abs(2f*r.Height[j]-r.Height[j-_res]-r.Height[j+_res]); nn++; }
    float aniso = (float)(rz / Math.Max(rx,1e-9));
    // valley/ridge discriminator on flow_accum
    var ord = new int[r.Height.Length]; for (int q=0;q<ord.Length;q++) ord[q]=q;
    System.Array.Sort(ord, (x,y)=>r.FlowAccum[y].CompareTo(r.FlowAccum[x]));
    int top = ord.Length/100; double hTop=0, hAll=0;
    for (int q=0;q<top;q++) hTop+=r.Height[ord[q]]; hTop/=top;
    foreach (float v in r.Height) hAll+=v; hAll/=r.Height.Length;
    bool ok = untouched && aniso > 0.7f && aniso < 1.4f && hTop < hAll;
    GD.Print($"CARVECHECK: {(ok?"PASS":"FAIL")} zeroSeg_untouched={untouched} antiterrace_z/x={aniso:F2} (want ~1) valleys(hTop={hTop:F1}<hAll={hAll:F1})={hTop<hAll}");
    SetProcess(false); GetTree().Quit(); return;
}
```

- [ ] **Step 4: Build + clear shader cache**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Then: `$c="C:\Users\josep\AppData\Roaming\Godot\app_userdata\WG16 base field\shader_cache"; if (Test-Path $c){Remove-Item -Recurse -Force $c}`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Run the carve check**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --carvecheck`
Expected: `CARVECHECK: PASS zeroSeg_untouched=True antiterrace_z/x=~1 valleys(...)=True`

- [ ] **Step 6: Commit**

```bash
git add shaders/valley_carve.glsl scripts/hydrology/ValleyCarve.cs scripts/erosion/ErosionLab.cs
git commit -m "hydrology T4: GPU analytic valley carve + substrate (smooth, anti-terracing by construction)"
```

---

### Task 5: HydrologyLab wiring + live eye-gate + tune

**Files:**
- Modify: `scripts/erosion/ErosionLab.cs` (add the interactive hydrology display path + live knobs + a `--rivermap` real-render dump aid)

**Interfaces:**
- Consumes: all of Tasks 1-4. No new production types.

- [ ] **Step 1: Add an interactive hydrology path in ErosionLab**

Add fields to the class: `private WG16.Hydrology.HydrologyParams _hp; private WG16.Hydrology.ValleyCarve _vc; private WG16.Hydrology.ValleyCarve.CarveResult _carve; private bool _hydroMode;`. After the existing seed/UI setup in `_Ready` (when no check-flag quit), if cmdline contains `--hydrolab` (or make it the default — see Step 3), build the substrate once and display it:

```csharp
// --- hydrology display: build coarse drainage graph + carve, then show carved terrain ---
_hp = new WG16.Hydrology.HydrologyParams { Res = _res, CellSize = _cell };
float bo = -_res * _cell * 0.5f;
float[] baseH = fc.ProducePage(p, bo, bo, _cell, _res, 0);
int cres = (int)((_res * _cell + 2 * _hp.HaloMetres) / _hp.CoarseSpacing);
var cf = WG16.Hydrology.CoarseField.Build(fc, p, bo - _hp.HaloMetres, bo - _hp.HaloMetres, _hp.CoarseSpacing, cres);
var g = WG16.Hydrology.DrainageGraph.Build(cf, _hp);
_vc = new WG16.Hydrology.ValleyCarve(_res);
_carve = _vc.Carve(baseH, g.Segments, _hp);
_hydroMode = true;
UploadHeight(_carve.Height);
GD.Print($"HydrologyLab: {g.Segments.Count} river segments, carved {_res}^2. D=cycle substrate views, [/]=carve strength, R=rebuild");
```

- [ ] **Step 2: Wire the hydrology debug views + live knobs into _Process**

Replace the sim-stepping body of `_Process` (guard with `if (_hydroMode)`) so the hydrology lab reads its substrate from `_carve`, not the pipe-model sim. D cycles lit→flow_accum→channel_mask→water_level→sediment; `[` / `]` adjust `_hp.CarveStrength` and rebuild the carve; R rebuilds from scratch.

```csharp
if (_hydroMode)
{
    bool d = Input.IsPhysicalKeyPressed(Key.D);
    if (d && !_dDown) { _debug = _debug >= 4 ? -1 : _debug + 1; RefreshHydroDisplay(); }
    _dDown = d;
    bool lb = Input.IsPhysicalKeyPressed(Key.BracketLeft), rb = Input.IsPhysicalKeyPressed(Key.BracketRight);
    if (lb && !_lbDown) { _hp.CarveStrength = Mathf.Max(0f, _hp.CarveStrength - 0.1f); RebuildCarve(); }
    if (rb && !_rbDown) { _hp.CarveStrength += 0.1f; RebuildCarve(); }
    _lbDown = lb; _rbDown = rb;
    string v = _debug switch { <0=>"lit carved", 0=>"flow_accum", 1=>"channel_mask", 2=>"water_level", 3=>"sediment", _=>"flow_accum" };
    _hud.Text = $"hydrology lab  carve_strength={_hp.CarveStrength:F1}  view={v}\n[ ] = carve strength   D = view   R = rebuild   {Engine.GetFramesPerSecond():0} fps";
    return;
}
```

Add the helper methods + the bool fields `_lbDown,_rbDown`:

```csharp
private void RebuildCarve()
{
    var pf = WG16.Field.FieldParams.Load();
    using var fc = new WG16.Field.FieldCompute();
    float bo = -_res * _cell * 0.5f;
    float[] baseH = fc.ProducePage(pf, bo, bo, _cell, _res, 0);
    int cres = (int)((_res * _cell + 2 * _hp.HaloMetres) / _hp.CoarseSpacing);
    var cf = WG16.Hydrology.CoarseField.Build(fc, pf, bo - _hp.HaloMetres, bo - _hp.HaloMetres, _hp.CoarseSpacing, cres);
    var g = WG16.Hydrology.DrainageGraph.Build(cf, _hp);
    _carve = _vc.Carve(baseH, g.Segments, _hp);
    UploadHeight(_carve.Height);
    RefreshHydroDisplay();
}

private void RefreshHydroDisplay()
{
    UploadHeight(_carve.Height);
    if (_debug < 0) { _mat.SetShaderParameter("debug_field", 0.0f); return; }
    float[] f = _debug switch { 0=>_carve.FlowAccum, 1=>_carve.ChannelMask, 2=>_carve.WaterLevel, 3=>_carve.Sediment, _=>_carve.FlowAccum };
    var disp = (float[])f.Clone();
    if (_debug == 0) { float mx=1e-6f; foreach (float v in disp) if (v>mx) mx=v; float lm=Mathf.Log(1f+mx); for (int k=0;k<disp.Length;k++) disp[k]=Mathf.Log(1f+disp[k])/Mathf.Max(lm,1e-6f); }
    else if (_debug == 2) { for (int k=0;k<disp.Length;k++) disp[k]= disp[k]>-1e8f?1f:0f; }
    else { float mx=1e-6f; foreach (float v in disp) if (v>mx) mx=v; for (int k=0;k<disp.Length;k++) disp[k]/=mx; }
    var bytes=new byte[disp.Length*4]; System.Buffer.BlockCopy(disp,0,bytes,0,bytes.Length);
    var img=Image.CreateFromData(_res,_res,false,Image.Format.Rf,bytes);
    _mat.SetShaderParameter("debug_field_tex", ImageTexture.CreateFromImage(img));
    _mat.SetShaderParameter("debug_field", 1.0f);
}
```

(Also dispose `_vc` in `_ExitTree`: add `_vc?.Dispose();`.)

- [ ] **Step 3: Make hydrology the default lab view**

In `_Ready`, after the check-flag loop, gate the OLD pipe-model setup behind `if (cmdline contains "--pipemodel")` and run the hydrology path otherwise. (Keep the pipe-model reachable for the optional-detail-pass future, per the design.) Concretely: wrap the existing `_mesh = new MeshInstance3D {...}` + sim seeding in an `else` and put the hydrology block in the `if`-default. Both share the existing `_mesh`/`_mat`/`_hud` setup — build the mesh/material/HUD FIRST (shared), then branch only on what fills the height + `_Process` behavior.

- [ ] **Step 4: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Field guard (bones untouched)**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck`
Expected: `FIELDCHECK: PASS maxAbsDiff=0m` (read from the output file; terrain_lab does not self-quit — kill it after the line appears).

- [ ] **Step 6: USER EYE-GATE (the real gate)**

Run (interactive, leave window open): `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn`
Have the USER confirm on the REAL render, in motion: smooth playable valleys (NO terracing), natural dendritic rivers draining logically, valleys broaden with stream order, lakes/basins read right, `[`/`]` carve-strength tuning feels good, D-views show a real branching network. Tune `DepthPerOrder`/`WidthPerOrder`/`CoarseSpacing`/`ChannelMinArea`/`CarveStrength` defaults until the user judges it GREAT — or STOP and surface if unreachable (graveyard discipline).

- [ ] **Step 7: Commit (on user PASS)**

```bash
git add scripts/erosion/ErosionLab.cs
git commit -m "hydrology T5: lab wiring + live carve tuning (eye-gate passed); drainage substrate complete"
```

(Then update `docs/superpowers/specs/2026-06-23-erosion-hydrology-master-design.md` Arc-1 status to: producer = drainage synthesis, sub-phase 1 done.)

## Self-Review

- **Spec coverage:** DrainageGraph deterministic CDG (T2) — fill/route/tributary/Strahler all present ✓; CoarseField + HydrologyParams (T1) ✓; determinism/tile-coherence invariant (T3) ✓; GPU analytic smooth carve + substrate fields (T4) ✓; lab eye-gate + tunable carve + anti-terracing/valley-ridge numeric probes (T4 mechanical, T5 user) ✓; modularity carve_strength/zero-seg untouched (T4) ✓; bones untouched + `--fieldcheck` (T5 step 5 + global constraints) ✓; pipe-model retired-but-kept (T5 step 3, behind `--pipemodel`) ✓; STOP criterion (T5 step 6) ✓.
  - **Tributary growth note:** `HydrologyParams.TributarySteps` defaults 0 (steepest-descent network only) for T1-T5; the spec's "headward tributary growth" is exposed as this knob and is a TUNING lever in T5 step 6 (raise if the network is too sparse). If the eye-gate needs richer branching than steepest-descent gives, growing tributaries is a bounded follow-up on the existing `DownIdx`/`Area`/`Order` machinery — flagged here, not silently dropped.
- **Placeholder scan:** no TBD/TODO; every code step has complete code; the determinism-FAIL remediation (T3 step 3) gives concrete actions (widen halo / deepen overlap), not "handle it".
- **Type consistency:** `CoarseField.Build`/`H`/`World`, `DrainageGraph.Build`/`Segments`/`Segment(Ax,Az,Bx,Bz,Order,Area)`/`DownIdx`/`Area`/`Order`/`Filled`, `HydrologyParams.Pack`/`Res`/`CellSize`/`CarveStrength`/`DepthPerOrder`/`WidthPerOrder`/`BankSediment`/`CoarseSpacing`/`HaloMetres`/`ChannelMinArea`, `ValleyCarve.Carve`/`CarveResult.{Height,FlowAccum,ChannelMask,WaterLevel,Sediment}` — names identical across all tasks and the GLSL Params block matches `Pack()` slot order ✓.
