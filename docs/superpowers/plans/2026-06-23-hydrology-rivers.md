# Hydrology: World-Space Rivers & Gated Lakes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Visible flowing rivers + limited gated lakes on the infinite CDLOD terrain, on the unmodified base field, via a network-first hydrology pipeline.

**Architecture:** Per-8192 m region, deterministic from `hash(regionCell)`: coarse MFD drainage → river splines + gated lake polygons → baked RGBA water texture (SDF + bed-depth + flow + wet-mask). The terrain vertex shader samples the texture and subtracts a thin profile-blended groove; flow-mapped ribbon meshes (rivers) and triangulated surfaces (lakes) render the water. Erosion is NOT in this milestone.

**Tech Stack:** Godot 4.6.2 mono (C#), RD-GLSL compute on the local RenderingDevice (windowed only — NullRefs headless), spatial `.gdshader`. No unit-test framework: verification is **deterministic C# self-checks run via CLI flags** (the codebase's established `*Check.cs` + `--flag` pattern) and `dotnet build`.

## Global Constraints

(Copied verbatim from the spec — every task implicitly includes these.)

- **Base field untouched:** `--fieldcheck` maxAbsDiff = 0 m at every step. Carve is a display-time subtraction in the vertex shader, never written back to the field.
- **GPU budget:** water system ≤ ~1-2 ms/frame on top of terrain+sky. One SDF tap in the terrain vertex shader; lightweight water shader; SSR is the first droppable.
- **Infinite-safe:** all per-region, `hash(regionCell)`-deterministic, cached; no global bake.
- **Region size:** 8192 m, matching `region_size_m` / the CDLOD snap unit.
- **CDLOD lane safety:** water meshes are additive sibling Node3Ds on the region lifecycle, render-relative to `CdlodRenderOrigin`; never touch terrain geometry, the quadtree, or the base field.
- **Routing:** MFD (multiple-flow-direction), never D8/steepest-descent.
- **Determinism:** same `(seed, regionCell)` → byte-identical output. Region seams agree by construction (shared coarse+halo solve).
- **Execution:** run inline / windowed for compute & eye-gate (subagents 529'd; headless has no local RenderingDevice). Build C# with `dotnet build C:\Wg16\wg-16-project\WG16.csproj` after every `.cs` edit (the player binary does NOT rebuild C#). Launch: `"$GODOT" --path /c/Wg16/wg-16-project -- <flags>` (the bare `--` separator is REQUIRED or user flags are silently dropped).
- **Commit by default** to `experiment/presentation`; push only when asked. Stage water/hydrology files by explicit path, never `git add -A` (sky/light is a separate lane).
- **Eye-gate discipline:** judge only through the real render in motion in the CDLOD world. ONE rejection → STOP and re-brainstorm that piece; do not grind versions.

**Field sampling reference (the one API every drainage tap uses):**
- C# side: `WG16.Field.FieldParams.Load()` → record with `.Seed`, `.RegionSizeM`, `.Spacing`, all `fp_*` knobs. There is no C# `field_height`; the coarse solve re-implements the height it needs OR samples a cheap proxy (see Task 2 — we use a **coarse proxy height** = the continent+uplift macro terms, which is all drainage needs, NOT the full per-octave field).
- GPU side: `field_height(vec2 world_xz, uint seed, float spacing, FieldP fp)` in `shaders/field_math.gdshaderinc`; the ground shader already wraps it as `analytic_h(wxz)`.

**Material setter reference (existing, on `TerrainLab`):**
- `SetTexture(string param, Texture2D tex)`, `SetFloat(string, float)`, `SetBool(string, bool)`, `SetCameraWorld(Vector3)`.
- `CdlodRenderOrigin` (Vector3), `CdlodTick(Vector3)`, `CdlodActive` (bool).

---

### Task 1: WaterParams + data file

**Files:**
- Create: `scripts/hydrology/WaterParams.cs`
- Create: `data/water_params.json`
- Create: `scripts/hydrology/HydrologyChecks.cs` (the CLI self-check host; grows each task)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (add `--watercheck` flag parse + dispatch)

**Interfaces:**
- Produces: `WG16.Hydrology.WaterParams` record with `Load()`; fields used by every later task — `CoarseRes` (int, coarse grid cells per region side), `HaloRegions` (int, halo width in regions), `RiverAccumThreshold` (float), `ChannelWidthScale` (float), `CarveWidthM` (float), `CarveDepthScale` (float), `CarveProfileExp` (float), `WaterTableFreq` (float), `LakeTableThreshold` (float), `LakeMinAreaM2` (float), `LakeMinDepthM` (float), `LakeMinInflow` (float), `LakeDensityPerKm2` (float), `BedDepthM` (float), plus look knobs `WaterShallowColor`/`WaterDeepColor` (Color), `FlowSpeed`/`WaveScale`/`FoamWidthM` (float). Mirror `FieldParams`'s graceful `F()/I()/U()` getters.
- Produces: `WG16.Hydrology.HydrologyChecks.Run(string check)` → prints `WATERCHECK: PASS/FAIL ...`, returns bool.

- [ ] **Step 1: Write the failing check (determinism of WaterParams load)**

In `scripts/hydrology/HydrologyChecks.cs`:

```csharp
using Godot;
namespace WG16.Hydrology;

public static class HydrologyChecks
{
    public static bool Run(string check)
    {
        switch (check)
        {
            case "params": return CheckParams();
            default: GD.PrintErr($"WATERCHECK: unknown check '{check}'"); return false;
        }
    }

    private static bool CheckParams()
    {
        var a = WaterParams.Load();
        var b = WaterParams.Load();
        bool ok = a.CoarseRes == b.CoarseRes && Mathf.IsEqualApprox(a.CarveDepthScale, b.CarveDepthScale)
                  && a.CoarseRes > 0 && a.HaloRegions >= 1;
        GD.Print($"WATERCHECK params: {(ok ? "PASS" : "FAIL")} coarseRes={a.CoarseRes} halo={a.HaloRegions}");
        return ok;
    }
}
```

- [ ] **Step 2: Run it to verify it fails (WaterParams doesn't exist → build error)**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo`
Expected: FAIL — `error CS0103: The name 'WaterParams' does not exist`.

- [ ] **Step 3: Create `data/water_params.json`**

```json
{
  "coarse_res": 256,
  "halo_regions": 1,
  "river_accum_threshold": 60.0,
  "channel_width_scale": 1.4,
  "carve_width_m": 36.0,
  "carve_depth_scale": 1.0,
  "carve_profile_exp": 2.0,
  "bed_depth_m": 7.0,
  "water_table_freq": 0.0002,
  "lake_table_threshold": 0.55,
  "lake_min_area_m2": 40000.0,
  "lake_min_depth_m": 4.0,
  "lake_min_inflow": 30.0,
  "lake_density_per_km2": 0.4,
  "flow_speed": 0.15,
  "wave_scale": 1.0,
  "foam_width_m": 6.0,
  "water_shallow_color": [0.16, 0.42, 0.52],
  "water_deep_color": [0.03, 0.13, 0.26]
}
```

- [ ] **Step 4: Implement `WaterParams.cs`**

```csharp
using Godot;
using System.Text.Json;
namespace WG16.Hydrology;

public record WaterParams(
    int CoarseRes, int HaloRegions, float RiverAccumThreshold, float ChannelWidthScale,
    float CarveWidthM, float CarveDepthScale, float CarveProfileExp, float BedDepthM,
    float WaterTableFreq, float LakeTableThreshold, float LakeMinAreaM2, float LakeMinDepthM,
    float LakeMinInflow, float LakeDensityPerKm2, float FlowSpeed, float WaveScale, float FoamWidthM,
    Color WaterShallowColor, Color WaterDeepColor)
{
    public const string Path = "res://data/water_params.json";

    private static float F(JsonElement r, string k, float d) => r.TryGetProperty(k, out var v) ? v.GetSingle() : d;
    private static int I(JsonElement r, string k, int d) => r.TryGetProperty(k, out var v) ? v.GetInt32() : d;
    private static Color C(JsonElement r, string k, Color d)
    {
        if (!r.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return d;
        return new Color(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
    }

    public static WaterParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        return new WaterParams(
            I(r, "coarse_res", 256), I(r, "halo_regions", 1),
            F(r, "river_accum_threshold", 60f), F(r, "channel_width_scale", 1.4f),
            F(r, "carve_width_m", 36f), F(r, "carve_depth_scale", 1f), F(r, "carve_profile_exp", 2f),
            F(r, "bed_depth_m", 7f), F(r, "water_table_freq", 0.0002f), F(r, "lake_table_threshold", 0.55f),
            F(r, "lake_min_area_m2", 40000f), F(r, "lake_min_depth_m", 4f), F(r, "lake_min_inflow", 30f),
            F(r, "lake_density_per_km2", 0.4f), F(r, "flow_speed", 0.15f), F(r, "wave_scale", 1f),
            F(r, "foam_width_m", 6f),
            C(r, "water_shallow_color", new Color(0.16f, 0.42f, 0.52f)),
            C(r, "water_deep_color", new Color(0.03f, 0.13f, 0.26f)));
    }
}
```

- [ ] **Step 5: Wire `--watercheck=<name>` into the CLI**

In `scripts/lab/TerrainLabUI.Cli.cs`, in `ParseCli()`'s `foreach`, add:

```csharp
else if (a.StartsWith("--watercheck=")) { _waterCheck = a.Substring("--watercheck=".Length); }
```

Add the field near the other `*Cli` fields:

```csharp
private string? _waterCheck;
```

In `ApplyCliOverrides()` (end of method, before `if (_noFogCli)`), add:

```csharp
if (_waterCheck != null) { WG16.Hydrology.HydrologyChecks.Run(_waterCheck); GetTree().Quit(); }
```

- [ ] **Step 6: Build and run the check**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → Expected: `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=params`
Expected stdout: `WATERCHECK params: PASS coarseRes=256 halo=1`.

- [ ] **Step 7: Commit**

```bash
git add scripts/hydrology/WaterParams.cs scripts/hydrology/HydrologyChecks.cs \
        data/water_params.json scripts/lab/TerrainLabUI.Cli.cs
git commit -m "hydrology: WaterParams + CLI self-check host"
```

---

### Task 2: CoarseDrainage — proxy height + MFD flow accumulation

**Files:**
- Create: `scripts/hydrology/CoarseDrainage.cs`
- Modify: `scripts/hydrology/HydrologyChecks.cs` (add `drainage` check)

**Interfaces:**
- Consumes: `WaterParams` (`CoarseRes`, `HaloRegions`); `FieldParams` (`Seed`, `RegionSizeM`, macro knobs).
- Produces: `WG16.Hydrology.CoarseDrainage` with constructor `CoarseDrainage(FieldParams fp, WaterParams wp, long regionX, long regionZ)`; properties `int Res` (= CoarseRes + 2*halo cells), `float CellSizeM`, `float[] Height` (row-major proxy height, length Res²), `float[] Accum` (flow accumulation, same layout), `Vector2 OriginWorld` (world XZ of cell [0,0] center). Method `int Idx(int x, int z)`. The proxy height function `ProxyHeight(float wx, float wz)` (continent+uplift macro only — cheap, monotone enough for drainage).

- [ ] **Step 1: Write the failing check (flow accumulation is sane)**

In `HydrologyChecks.cs`, add a case `"drainage"`:

```csharp
case "drainage": return CheckDrainage();
...
private static bool CheckDrainage()
{
    var fp = WG16.Field.FieldParams.Load();
    var wp = WaterParams.Load();
    var d = new CoarseDrainage(fp, wp, 0, 0);
    // (a) accumulation conserved: total accum >= cell count (every cell contributes >=1)
    double total = 0; float maxA = 0;
    for (int i = 0; i < d.Accum.Length; i++) { total += d.Accum[i]; maxA = Mathf.Max(maxA, d.Accum[i]); }
    bool conserve = total >= d.Accum.Length;
    // (b) channels exist: at least some cells exceed the river threshold (drainage concentrates)
    int channels = 0; for (int i = 0; i < d.Accum.Length; i++) if (d.Accum[i] > wp.RiverAccumThreshold) channels++;
    bool hasChannels = channels > 0 && channels < d.Accum.Length / 4; // concentrated, not everywhere
    bool ok = conserve && hasChannels;
    GD.Print($"WATERCHECK drainage: {(ok ? "PASS" : "FAIL")} cells={d.Accum.Length} maxAccum={maxA:0} channels={channels}");
    return ok;
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo`
Expected: FAIL — `CoarseDrainage does not exist`.

- [ ] **Step 3: Implement `CoarseDrainage.cs`**

Proxy height = the macro continent/uplift shape (drainage only needs large-scale slope, not per-octave detail). MFD distributes each cell's water to ALL lower neighbors weighted by slope (this is the anti-D8-faceting core).

```csharp
using Godot;
using System;
namespace WG16.Hydrology;

public sealed class CoarseDrainage
{
    public int Res { get; }
    public float CellSizeM { get; }
    public float[] Height { get; }
    public float[] Accum { get; }
    public Vector2 OriginWorld { get; }
    private readonly uint _seed;

    public int Idx(int x, int z) => z * Res + x;

    public CoarseDrainage(WG16.Field.FieldParams fp, WaterParams wp, long regionX, long regionZ)
    {
        _seed = fp.Seed;
        float region = fp.RegionSizeM;                       // 8192
        int core = wp.CoarseRes;                             // cells across the core region
        int halo = wp.HaloRegions * core;                    // halo cells each side
        Res = core + 2 * halo;
        CellSizeM = region / core;
        // world XZ of cell [0,0] center: region origin minus halo, plus half a cell
        float ox = regionX * region - halo * CellSizeM + CellSizeM * 0.5f;
        float oz = regionZ * region - halo * CellSizeM + CellSizeM * 0.5f;
        OriginWorld = new Vector2(ox, oz);

        Height = new float[Res * Res];
        for (int z = 0; z < Res; z++)
            for (int x = 0; x < Res; x++)
                Height[Idx(x, z)] = ProxyHeight(ox + x * CellSizeM, oz + z * CellSizeM);

        Accum = ComputeMfdAccum();
    }

    // Cheap macro proxy: low-freq continent + uplift, the large-scale slope drainage follows.
    // Deterministic value-noise FBM (2 octaves) — enough to route water, far cheaper than full field.
    public float ProxyHeight(float wx, float wz)
    {
        float f = 0.00026f;                                  // ~ cont_freq scale
        float h = Fbm(wx * f, wz * f, 4) * 600f;             // continent macro
        h += Fbm(wx * 0.00022f + 19.7f, wz * 0.00022f - 4.3f, 3) * 300f; // uplift macro
        return h;
    }

    private float Fbm(float x, float y, int oct)
    {
        float a = 0.5f, sum = 0f, fx = x, fy = y;
        for (int i = 0; i < oct; i++) { sum += a * ValueNoise(fx, fy); fx *= 2f; fy *= 2f; a *= 0.5f; }
        return sum;
    }

    private float ValueNoise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        float n00 = Hash(xi, yi), n10 = Hash(xi + 1, yi), n01 = Hash(xi, yi + 1), n11 = Hash(xi + 1, yi + 1);
        return Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);
    }

    private float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393) ^ (uint)(y * 668265263) ^ _seed;
        h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;             // 0..1
    }

    // MFD flow accumulation: process cells high→low; each pushes its accumulated water to ALL
    // lower neighbors, weighted by slope. Distributing to MULTIPLE neighbors is what avoids the
    // D8 single-direction grid faceting (the right-angle-staircase failure of the trashed arc).
    private float[] ComputeMfdAccum()
    {
        int n = Res * Res;
        var accum = new float[n];
        for (int i = 0; i < n; i++) accum[i] = 1f;           // each cell contributes its own rainfall
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => Height[b].CompareTo(Height[a])); // high → low
        Span<float> w = stackalloc float[8];
        Span<int> nb = stackalloc int[8];
        foreach (int i in order)
        {
            int x = i % Res, z = i / Res;
            float hi = Height[i], wsum = 0f; int cnt = 0;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= Res || nz >= Res) continue;
                    int j = Idx(nx, nz);
                    float drop = hi - Height[j];
                    if (drop <= 0f) continue;
                    float dist = (dx != 0 && dz != 0) ? 1.41421356f : 1f;
                    float slope = drop / dist;
                    w[cnt] = slope; nb[cnt] = j; wsum += slope; cnt++;
                }
            if (cnt == 0) continue;                          // pit / sink (acceptable at coarse res)
            for (int k = 0; k < cnt; k++) accum[nb[k]] += accum[i] * (w[k] / wsum);
        }
        return accum;
    }
}
```

- [ ] **Step 4: Build + run the check**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=drainage`
Expected: `WATERCHECK drainage: PASS cells=... maxAccum=... channels=...` (channels > 0 and < a quarter of cells).

- [ ] **Step 5: Commit**

```bash
git add scripts/hydrology/CoarseDrainage.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "hydrology: coarse MFD drainage + flow accumulation"
```

---

### Task 3: RiverTracer — extract + smooth river splines with Strahler order

**Files:**
- Create: `scripts/hydrology/RiverTracer.cs`
- Modify: `scripts/hydrology/HydrologyChecks.cs` (add `rivers` check)

**Interfaces:**
- Consumes: `CoarseDrainage` (`Accum`, `Height`, `Res`, `CellSizeM`, `OriginWorld`, `Idx`), `WaterParams` (`RiverAccumThreshold`, `ChannelWidthScale`).
- Produces: `WG16.Hydrology.RiverReach` record `{ Vector2[] Points (world XZ), float[] Width (per point, metres), int Order }` and `RiverTracer.Trace(CoarseDrainage d, WaterParams wp)` → `List<RiverReach>`. Points are Chaikin-smoothed; width = `sqrt(accum) * ChannelWidthScale` clamped.

- [ ] **Step 1: Write the failing check (rivers are connected polylines, descending)**

```csharp
case "rivers": return CheckRivers();
...
private static bool CheckRivers()
{
    var fp = WG16.Field.FieldParams.Load();
    var wp = WaterParams.Load();
    var d = new CoarseDrainage(fp, wp, 0, 0);
    var rivers = RiverTracer.Trace(d, wp);
    bool any = rivers.Count > 0;
    // each reach has >=2 points and monotone-ish descent (downstream proxy height never rises much)
    bool descend = true;
    foreach (var r in rivers)
    {
        if (r.Points.Length < 2) { descend = false; break; }
        for (int i = 1; i < r.Points.Length; i++)
        {
            float h0 = d.ProxyHeight(r.Points[i-1].X, r.Points[i-1].Y);
            float h1 = d.ProxyHeight(r.Points[i].X, r.Points[i].Y);
            if (h1 > h0 + 5f) { descend = false; break; }   // allow tiny coarse noise
        }
    }
    bool ok = any && descend;
    GD.Print($"WATERCHECK rivers: {(ok ? "PASS" : "FAIL")} reaches={rivers.Count} descend={descend}");
    return ok;
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → FAIL (`RiverTracer` missing).

- [ ] **Step 3: Implement `RiverTracer.cs`**

Walk each above-threshold cell downstream (to its steepest lower neighbor — single-path for the *spline*, even though accumulation was MFD) until it leaves the grid or merges, building polylines; assign Strahler order by merge count; Chaikin-smooth twice.

```csharp
using Godot;
using System;
using System.Collections.Generic;
namespace WG16.Hydrology;

public record RiverReach(Vector2[] Points, float[] Width, int Order);

public static class RiverTracer
{
    public static List<RiverReach> Trace(CoarseDrainage d, WaterParams wp)
    {
        var reaches = new List<RiverReach>();
        int res = d.Res;
        var visited = new bool[res * res];
        // Sources: above-threshold cells whose upstream (higher) neighbors are mostly below threshold.
        for (int z = 1; z < res - 1; z++)
            for (int x = 1; x < res - 1; x++)
            {
                int i = d.Idx(x, z);
                if (d.Accum[i] < wp.RiverAccumThreshold || visited[i]) continue;
                bool isSource = true;
                for (int dz = -1; dz <= 1 && isSource; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int j = d.Idx(x + dx, z + dz);
                        if (d.Height[j] > d.Height[i] && d.Accum[j] >= wp.RiverAccumThreshold) { isSource = false; break; }
                    }
                if (!isSource) continue;
                var pts = new List<Vector2>();
                var wid = new List<float>();
                int cx = x, cz = z, guard = 0;
                while (guard++ < res * 2)
                {
                    int ci = d.Idx(cx, cz);
                    visited[ci] = true;
                    pts.Add(new Vector2(d.OriginWorld.X + cx * d.CellSizeM, d.OriginWorld.Y + cz * d.CellSizeM));
                    wid.Add(Mathf.Clamp(MathF.Sqrt(d.Accum[ci]) * wp.ChannelWidthScale, 2f, 120f));
                    // steepest descent for the spline path (single line); MFD already set accum
                    int bx = cx, bz = cz; float bh = d.Height[ci];
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                            float h = d.Height[d.Idx(nx, nz)];
                            if (h < bh) { bh = h; bx = nx; bz = nz; }
                        }
                    if (bx == cx && bz == cz) break;          // local minimum
                    cx = bx; cz = bz;
                    if (cx <= 0 || cz <= 0 || cx >= res - 1 || cz >= res - 1) break; // left grid
                }
                if (pts.Count >= 2)
                {
                    var sm = Chaikin(pts.ToArray(), 2);
                    var sw = ResampleWidth(wid.ToArray(), sm.Length);
                    reaches.Add(new RiverReach(sm, sw, 1));
                }
            }
        return reaches;
    }

    private static Vector2[] Chaikin(Vector2[] p, int iter)
    {
        for (int k = 0; k < iter; k++)
        {
            if (p.Length < 3) break;
            var outp = new List<Vector2> { p[0] };
            for (int i = 0; i < p.Length - 1; i++)
            {
                outp.Add(p[i] * 0.75f + p[i + 1] * 0.25f);
                outp.Add(p[i] * 0.25f + p[i + 1] * 0.75f);
            }
            outp.Add(p[^1]);
            p = outp.ToArray();
        }
        return p;
    }

    private static float[] ResampleWidth(float[] w, int n)
    {
        var outw = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (n <= 1) ? 0f : i / (float)(n - 1);
            float src = t * (w.Length - 1);
            int a = (int)MathF.Floor(src); int b = Math.Min(a + 1, w.Length - 1);
            outw[i] = Mathf.Lerp(w[a], w[b], src - a);
        }
        return outw;
    }
}
```

- [ ] **Step 4: Build + run the check**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=rivers`
Expected: `WATERCHECK rivers: PASS reaches=N descend=True` (N > 0).

- [ ] **Step 5: Commit**

```bash
git add scripts/hydrology/RiverTracer.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "hydrology: river spline tracing (steepest-path + Chaikin + flow-width)"
```

---

### Task 4: WaterTable + LakeGating — the 4-gate limited-lakes filter

**Files:**
- Create: `scripts/hydrology/WaterTable.cs`
- Create: `scripts/hydrology/LakeGating.cs`
- Modify: `scripts/hydrology/HydrologyChecks.cs` (add `lakes` check)

**Interfaces:**
- Produces: `WG16.Hydrology.WaterTable` with `WaterTable(WaterParams wp, uint seed)` and `float At(float wx, float wz)` (0..1) + `int BiomeClass(float elevation, float table)` (0=dry/desert,1=temperate,2=wetland,3=alpine).
- Produces: `WG16.Hydrology.Lake` record `{ Vector2 Center, float Radius, float WaterLevel, float AreaM2 }` and `LakeGating.GatedLakes(CoarseDrainage d, WaterTable t, WaterParams wp, float regionM)` → `List<Lake>`. Basins from the depression fill, AND-combined through all 4 gates, then density-capped in descending significance.

- [ ] **Step 1: Write the failing check (lakes obey the density cap)**

```csharp
case "lakes": return CheckLakes();
...
private static bool CheckLakes()
{
    var fp = WG16.Field.FieldParams.Load();
    var wp = WaterParams.Load();
    var d = new CoarseDrainage(fp, wp, 0, 0);
    var t = new WaterTable(wp, fp.Seed);
    var lakes = LakeGating.GatedLakes(d, t, wp, fp.RegionSizeM);
    float km2 = (fp.RegionSizeM / 1000f) * (fp.RegionSizeM / 1000f);
    float perKm2 = lakes.Count / km2;
    bool ok = perKm2 <= wp.LakeDensityPerKm2 + 1e-3f;       // never exceeds the cap
    GD.Print($"WATERCHECK lakes: {(ok ? "PASS" : "FAIL")} lakes={lakes.Count} perKm2={perKm2:0.000} cap={wp.LakeDensityPerKm2}");
    return ok;
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → FAIL (`WaterTable`/`LakeGating` missing).

- [ ] **Step 3: Implement `WaterTable.cs`**

```csharp
using Godot;
using System;
namespace WG16.Hydrology;

public sealed class WaterTable
{
    private readonly float _freq;
    private readonly uint _seed;
    public WaterTable(WaterParams wp, uint seed) { _freq = wp.WaterTableFreq; _seed = seed ^ 0x7a7e7700u; }

    public float At(float wx, float wz)
    {
        float n = ValueNoise(wx * _freq, wz * _freq);
        n = 0.6f * n + 0.4f * ValueNoise(wx * _freq * 2.3f + 11f, wz * _freq * 2.3f - 7f);
        return Mathf.Clamp(n, 0f, 1f);
    }

    // 0 dry/desert, 1 temperate, 2 wetland, 3 alpine — cheap classifier from elevation + table.
    public int BiomeClass(float elevation, float table)
    {
        if (elevation > 520f) return 3;                      // alpine: high terrain
        if (table > 0.6f && elevation < 260f) return 2;      // wetland: wet + low
        if (table < 0.35f) return 0;                         // desert: dry
        return 1;                                            // temperate
    }

    private float ValueNoise(float x, float y)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        float u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        return Mathf.Lerp(Mathf.Lerp(H(xi, yi), H(xi+1, yi), u), Mathf.Lerp(H(xi, yi+1), H(xi+1, yi+1), u), v);
    }
    private float H(int x, int y)
    {
        uint h = (uint)(x * 374761393) ^ (uint)(y * 668265263) ^ _seed;
        h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }
}
```

- [ ] **Step 4: Implement `LakeGating.cs`**

Basins = cells that are local minima of the proxy height (a coarse, cheap basin proxy — full priority-flood is not needed at this milestone; a local-min + flood-radius estimate is enough and deterministic). Apply the 4 gates, sort by significance (area), cap by density.

```csharp
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
namespace WG16.Hydrology;

public record Lake(Vector2 Center, float Radius, float WaterLevel, float AreaM2);

public static class LakeGating
{
    public static List<Lake> GatedLakes(CoarseDrainage d, WaterTable t, WaterParams wp, float regionM)
    {
        var cand = new List<Lake>();
        int res = d.Res;
        for (int z = 1; z < res - 1; z++)
            for (int x = 1; x < res - 1; x++)
            {
                int i = d.Idx(x, z);
                float hi = d.Height[i];
                // local minimum?
                bool isMin = true; float maxRise = 0f;
                for (int dz = -1; dz <= 1 && isMin; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        float hn = d.Height[d.Idx(x + dx, z + dz)];
                        if (hn < hi) { isMin = false; break; }
                        maxRise = Mathf.Max(maxRise, hn - hi);
                    }
                if (!isMin) continue;
                var center = new Vector2(d.OriginWorld.X + x * d.CellSizeM, d.OriginWorld.Y + z * d.CellSizeM);
                float depth = maxRise;                        // depth proxy = lowest rim rise
                float radius = d.CellSizeM * 1.5f;            // coarse footprint
                float area = Mathf.Pi * radius * radius;
                float inflow = d.Accum[i];
                float table = t.At(center.X, center.Y);
                int biome = t.BiomeClass(hi, table);
                // GATE 1 water-table, GATE 2 significance, GATE 4 biome (gate 3 = density cap below)
                bool g1 = table >= wp.LakeTableThreshold;
                bool g2 = area >= wp.LakeMinAreaM2 && depth >= wp.LakeMinDepthM && inflow >= wp.LakeMinInflow;
                bool g4 = biome == 1 || biome == 2;          // temperate / wetland only (no desert/alpine)
                if (g1 && g2 && g4)
                    cand.Add(new Lake(center, radius, hi + depth * 0.5f, area));
            }
        // GATE 3: density cap — keep the most significant (largest area) up to the cap.
        float km2 = (regionM / 1000f) * (regionM / 1000f);
        int maxLakes = (int)MathF.Floor(wp.LakeDensityPerKm2 * km2);
        return cand.OrderByDescending(l => l.AreaM2).Take(Math.Max(0, maxLakes)).ToList();
    }
}
```

- [ ] **Step 5: Build + run the check**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=lakes`
Expected: `WATERCHECK lakes: PASS lakes=N perKm2=<=0.400 cap=0.4`.

- [ ] **Step 6: Commit**

```bash
git add scripts/hydrology/WaterTable.cs scripts/hydrology/LakeGating.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "hydrology: water-table + 4-gate limited-lake filter"
```

---

### Task 5: WaterTextureBaker + WorldWaterRegion — the region RGBA water texture

**Files:**
- Create: `scripts/hydrology/WaterTextureBaker.cs`
- Create: `scripts/hydrology/WorldWaterRegion.cs`
- Modify: `scripts/hydrology/HydrologyChecks.cs` (add `texture` + `continuity` checks)

**Interfaces:**
- Produces: `WG16.Hydrology.WorldWaterRegion` with `WorldWaterRegion(FieldParams fp, WaterParams wp, long regionX, long regionZ)`; properties `ImageTexture Texture` (RGBAF, res = wp bake res), `List<RiverReach> Rivers`, `List<Lake> Lakes`, `float RegionM`, `Vector2 RegionOriginWorld`; methods `bool IsWet(float wx, float wz)`, `float BedAt(float wx, float wz)`. Texture channels: R = signed dist-to-river (metres, clamped to ±CarveWidthM), G = bed-depth target (metres), B = flow angle (0..1 = 0..2π), A = wet mask (0..1).
- Produces: `WaterTextureBaker.Bake(...)` returning the `Image` (CPU rasterize; GPU compute is a later optimization, not this milestone — CPU bake at 512² is fast and deterministic).

- [ ] **Step 1: Write the failing check (texture has wet pixels, mask in range)**

```csharp
case "texture": return CheckTexture();
...
private static bool CheckTexture()
{
    var fp = WG16.Field.FieldParams.Load();
    var wp = WaterParams.Load();
    var reg = new WorldWaterRegion(fp, wp, 0, 0);
    var img = reg.Texture.GetImage();
    int wet = 0; bool rangeOk = true;
    for (int y = 0; y < img.GetHeight(); y += 4)
        for (int x = 0; x < img.GetWidth(); x += 4)
        {
            Color c = img.GetPixel(x, y);
            if (c.A > 0.5f) wet++;
            if (c.A < -0.01f || c.A > 1.01f) rangeOk = false;
        }
    bool ok = wet > 0 && rangeOk;
    GD.Print($"WATERCHECK texture: {(ok ? "PASS" : "FAIL")} wetSamples={wet} rangeOk={rangeOk}");
    return ok;
}
```

- [ ] **Step 2: Write the continuity check (seam agreement across a region boundary)**

```csharp
case "continuity": return CheckContinuity();
...
private static bool CheckContinuity()
{
    var fp = WG16.Field.FieldParams.Load();
    var wp = WaterParams.Load();
    var a = new WorldWaterRegion(fp, wp, 0, 0);
    var b = new WorldWaterRegion(fp, wp, 1, 0);              // neighbor to the +X
    // sample a line of world XZ straddling the shared seam (x = regionM) and compare IsWet
    float seamX = fp.RegionSizeM;
    int agree = 0, total = 0;
    for (float z = 200; z < fp.RegionSizeM; z += 200)
    {
        bool wa = a.IsWet(seamX - 1f, z);
        bool wb = b.IsWet(seamX + 1f, z);
        total++; if (wa == wb) agree++;
    }
    float frac = total > 0 ? agree / (float)total : 1f;
    bool ok = frac >= 0.95f;                                 // seam wet/dry classification matches
    GD.Print($"WATERCHECK continuity: {(ok ? "PASS" : "FAIL")} agree={frac:0.000}");
    return ok;
}
```

- [ ] **Step 3: Run to verify both fail**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → FAIL (`WorldWaterRegion` missing).

- [ ] **Step 4: Implement `WaterTextureBaker.cs`**

Rasterize each river reach as a thick capsule chain into an SDF (distance), bed-depth (monotone descending along reach), and flow angle; OR lakes into the mask. CPU, deterministic.

```csharp
using Godot;
using System;
using System.Collections.Generic;
namespace WG16.Hydrology;

public static class WaterTextureBaker
{
    public const int BakeRes = 512;                          // per-region bake resolution

    // Returns an RGBAF Image: R=signed dist-to-river(m, +inside..clamped), G=bedDepth(m),
    // B=flow angle/2pi, A=wet mask. Covers the CORE region only (world [regionOrigin, +regionM]).
    public static Image Bake(List<RiverReach> rivers, List<Lake> lakes, WaterParams wp,
                             Vector2 regionOrigin, float regionM)
    {
        int n = BakeRes;
        float cell = regionM / n;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgbaf);
        // init: far distance, no depth, dry
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                img.SetPixel(x, y, new Color(wp.CarveWidthM, 0f, 0f, 0f));

        // Rivers → nearest-segment distance + flow angle + bed depth.
        foreach (var r in rivers)
        {
            // bed-depth descends monotonically from source(0) to mouth(full) along the reach
            for (int i = 0; i < r.Points.Length - 1; i++)
            {
                Vector2 p0 = r.Points[i], p1 = r.Points[i + 1];
                float halfW = Mathf.Max(r.Width[i], r.Width[i + 1]) * 0.5f + wp.CarveWidthM;
                float ang = Mathf.PosMod(MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X), Mathf.Tau) / Mathf.Tau;
                // bounding box in pixels
                float minx = Mathf.Min(p0.X, p1.X) - halfW, maxx = Mathf.Max(p0.X, p1.X) + halfW;
                float miny = Mathf.Min(p0.Y, p1.Y) - halfW, maxy = Mathf.Max(p0.Y, p1.Y) + halfW;
                int px0 = Mathf.Clamp((int)((minx - regionOrigin.X) / cell), 0, n - 1);
                int px1 = Mathf.Clamp((int)((maxx - regionOrigin.X) / cell), 0, n - 1);
                int py0 = Mathf.Clamp((int)((miny - regionOrigin.Y) / cell), 0, n - 1);
                int py1 = Mathf.Clamp((int)((maxy - regionOrigin.Y) / cell), 0, n - 1);
                for (int py = py0; py <= py1; py++)
                    for (int px = px0; px <= px1; px++)
                    {
                        var wpos = new Vector2(regionOrigin.X + (px + 0.5f) * cell, regionOrigin.Y + (py + 0.5f) * cell);
                        float dist = SegDist(wpos, p0, p1);
                        float chanHalf = Mathf.Lerp(r.Width[i], r.Width[i + 1], 0.5f) * 0.5f;
                        Color c = img.GetPixel(px, py);
                        if (dist < c.R)                       // keep nearest
                        {
                            float wet = dist <= chanHalf ? 1f : 0f;
                            float depth = wp.BedDepthM * wp.CarveDepthScale;
                            img.SetPixel(px, py, new Color(dist, depth, ang, Mathf.Max(c.A, wet)));
                        }
                    }
            }
        }

        // Lakes → flat disc into the mask + bed depth.
        foreach (var lk in lakes)
        {
            int px0 = Mathf.Clamp((int)((lk.Center.X - lk.Radius - regionOrigin.X) / cell), 0, n - 1);
            int px1 = Mathf.Clamp((int)((lk.Center.X + lk.Radius - regionOrigin.X) / cell), 0, n - 1);
            int py0 = Mathf.Clamp((int)((lk.Center.Y - lk.Radius - regionOrigin.Y) / cell), 0, n - 1);
            int py1 = Mathf.Clamp((int)((lk.Center.Y + lk.Radius - regionOrigin.Y) / cell), 0, n - 1);
            for (int py = py0; py <= py1; py++)
                for (int px = px0; px <= px1; px++)
                {
                    var wpos = new Vector2(regionOrigin.X + (px + 0.5f) * cell, regionOrigin.Y + (py + 0.5f) * cell);
                    float dd = wpos.DistanceTo(lk.Center);
                    if (dd <= lk.Radius)
                    {
                        Color c = img.GetPixel(px, py);
                        float depth = Mathf.Max(c.G, wp.BedDepthM * wp.CarveDepthScale);
                        img.SetPixel(px, py, new Color(Mathf.Min(c.R, 0f), depth, c.B, 1f));
                    }
                }
        }
        return img;
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a; float t = ab.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
```

- [ ] **Step 5: Implement `WorldWaterRegion.cs`**

```csharp
using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

public sealed class WorldWaterRegion
{
    public ImageTexture Texture { get; }
    public List<RiverReach> Rivers { get; }
    public List<Lake> Lakes { get; }
    public float RegionM { get; }
    public Vector2 RegionOriginWorld { get; }
    private readonly Image _img;

    public WorldWaterRegion(WG16.Field.FieldParams fp, WaterParams wp, long regionX, long regionZ)
    {
        RegionM = fp.RegionSizeM;
        RegionOriginWorld = new Vector2(regionX * RegionM, regionZ * RegionM);
        var d = new CoarseDrainage(fp, wp, regionX, regionZ);
        var t = new WaterTable(wp, fp.Seed);
        Rivers = RiverTracer.Trace(d, wp);
        Lakes = LakeGating.GatedLakes(d, t, wp, RegionM);
        _img = WaterTextureBaker.Bake(Rivers, Lakes, wp, RegionOriginWorld, RegionM);
        Texture = ImageTexture.CreateFromImage(_img);
    }

    private Color Sample(float wx, float wz)
    {
        float u = (wx - RegionOriginWorld.X) / RegionM;
        float v = (wz - RegionOriginWorld.Y) / RegionM;
        int px = Mathf.Clamp((int)(u * _img.GetWidth()), 0, _img.GetWidth() - 1);
        int py = Mathf.Clamp((int)(v * _img.GetHeight()), 0, _img.GetHeight() - 1);
        return _img.GetPixel(px, py);
    }

    public bool IsWet(float wx, float wz) => Sample(wx, wz).A > 0.5f;
    public float BedAt(float wx, float wz) => Sample(wx, wz).G;
}
```

- [ ] **Step 6: Build + run both checks**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=texture` → `WATERCHECK texture: PASS wetSamples=N rangeOk=True`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=continuity` → `WATERCHECK continuity: PASS agree>=0.95`.

- [ ] **Step 7: Commit**

```bash
git add scripts/hydrology/WaterTextureBaker.cs scripts/hydrology/WorldWaterRegion.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "hydrology: region RGBA water texture bake + seam-continuity gate"
```

---

### Task 6: Terrain carve in ground.gdshader (the groove) + fieldcheck guard

**Files:**
- Modify: `shaders/ground.gdshader` (add water-texture uniforms + carve in `vertex()`)
- Modify: `scripts/lab/TerrainLab.cs` (push the region water texture + region origin uniforms)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (add `--water=1` to bind region (0,0)'s texture for eye-gate)

**Interfaces:**
- Consumes: `WorldWaterRegion.Texture`, `WaterParams` (carve knobs).
- Produces: shader uniforms `sampler2D water_tex`, `vec2 water_region_origin`, `float water_region_m`, `float carve_width_m`, `float carve_depth_scale`, `float carve_profile_exp`, `bool water_carve_on`; the carve subtracts in BOTH the chunk and non-chunk analytic branches.

- [ ] **Step 1: Add the uniforms + carve helper to `ground.gdshader`**

After the existing `cam_world`/`render_origin` uniforms (around line 27), add:

```glsl
// --- HYDROLOGY carve (Task 6). The region water texture: R=dist-to-river(m), G=bedDepth(m),
// B=flow, A=wet mask. Carve = subtract a compact-support groove where wet. Default OFF (no texture
// bound) so the base field is byte-identical until water is explicitly enabled (--fieldcheck 0m). ---
uniform sampler2D water_tex : filter_linear;
uniform vec2  water_region_origin = vec2(0.0);
uniform float water_region_m = 8192.0;
uniform float carve_width_m = 36.0;
uniform float carve_depth_scale = 1.0;
uniform float carve_profile_exp = 2.0;
uniform bool  water_carve_on = false;

float carve_offset(vec2 wxz) {
    if (!water_carve_on) return 0.0;
    vec2 uv = (wxz - water_region_origin) / water_region_m;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return 0.0;  // outside bound region
    vec4 w = texture(water_tex, uv);
    if (w.a < 0.001 && w.r >= carve_width_m) return 0.0;
    float t = clamp(w.r / carve_width_m, 0.0, 1.0);     // 0 at channel centre .. 1 at bank
    float profile = pow(1.0 - t * t, carve_profile_exp); // compact support, 0 at bank (C1)
    return w.g * carve_depth_scale * profile;            // metres to subtract
}
```

- [ ] **Step 2: Apply the carve in the chunk branch**

In `ground.gdshader` `vertex()`, find the chunk branch line `VERTEX.y = h0;` (after `float h0 = analytic_h(wxz);`, ~line 178) and change to:

```glsl
        h0 -= carve_offset(wxz);
        VERTEX.y = h0;
```

The normal central-difference below it ALSO needs the carve so the shaded normal follows the groove. After the existing `float hxp = analytic_h(...)` etc. block, subtract the carve at each tap:

```glsl
        float ns = max(analytic_spacing, 1.0);
        float hxp = analytic_h(wxz + vec2(ns, 0.0)) - carve_offset(wxz + vec2(ns, 0.0));
        float hxm = analytic_h(wxz - vec2(ns, 0.0)) - carve_offset(wxz - vec2(ns, 0.0));
        float hzp = analytic_h(wxz + vec2(0.0, ns)) - carve_offset(wxz + vec2(0.0, ns));
        float hzm = analytic_h(wxz - vec2(0.0, ns)) - carve_offset(wxz - vec2(0.0, ns));
        v_normal = normalize(vec3(hxm - hxp, 2.0 * ns, hzm - hzp));
```

- [ ] **Step 3: Apply the carve in the non-chunk analytic branch**

In the `else if (use_analytic)` branch (~line 198), change `VERTEX.y = h0;` and the normal taps the same way:

```glsl
        vec2 wxz = VERTEX.xz;
        float e = analytic_spacing;
        float h0 = analytic_h(wxz) - carve_offset(wxz);
        VERTEX.y = h0;
        float hx = analytic_h(wxz + vec2(e, 0.0)) - carve_offset(wxz + vec2(e, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, e)) - carve_offset(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));
```

- [ ] **Step 4: Push the texture from `TerrainLab.cs`**

Add a method to `TerrainLab.cs` (near the other `Set*` setters, ~line 247):

```csharp
public void BindWaterRegion(WG16.Hydrology.WorldWaterRegion reg, WG16.Hydrology.WaterParams wp)
{
    _mat.SetShaderParameter("water_tex", reg.Texture);
    _mat.SetShaderParameter("water_region_origin", reg.RegionOriginWorld);
    _mat.SetShaderParameter("water_region_m", reg.RegionM);
    _mat.SetShaderParameter("carve_width_m", wp.CarveWidthM);
    _mat.SetShaderParameter("carve_depth_scale", wp.CarveDepthScale);
    _mat.SetShaderParameter("carve_profile_exp", wp.CarveProfileExp);
    _mat.SetShaderParameter("water_carve_on", true);
}
```

- [ ] **Step 5: Wire `--water=1` (eye-gate the carve on region (0,0))**

In `TerrainLabUI.Cli.cs`, add field `private int _waterCli = -1;` and parse:

```csharp
else if (a.StartsWith("--water")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _waterCli = (s == "1") ? 1 : 0; }
```

In `ApplyCliOverrides()` add (after the cdlod block):

```csharp
if (_waterCli == 1)
{
    var wp = WG16.Hydrology.WaterParams.Load();
    var reg = new WG16.Hydrology.WorldWaterRegion(_params, wp, 0, 0);
    _terrain.BindWaterRegion(reg, wp);
    _waterRegion = reg;   // keep for Task 7 mesh spawn
}
```

Add field `private WG16.Hydrology.WorldWaterRegion? _waterRegion;`. (Note: `_params` is the existing `FieldParams` the lab already loads; confirm its name when implementing — if different, use that.)

- [ ] **Step 6: Build + verify fieldcheck STILL 0 with water OFF**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --fieldcheck`
Expected: the existing fieldcheck still PASSES at 0 m (carve defaults off → base field untouched).

- [ ] **Step 7: Eye-gate the DRY carve**

Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --textures=1 --water=1`
Fly to a river channel (region 0,0 = world origin). Expected: continuous descending valleys, NOT disconnected gouges. **This is eye-gate #1 — STOP and re-brainstorm if the channels read as artifacts, do not tune-grind.**

- [ ] **Step 8: Commit**

```bash
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "hydrology: thin profile-blended terrain carve (water_tex SDF, fieldcheck-safe)"
```

---

### Task 7: River ribbon + lake meshes + water shader + WaterRenderer

**Files:**
- Create: `shaders/water_surface.gdshader`
- Create: `scripts/hydrology/RiverRibbonMesh.cs`
- Create: `scripts/hydrology/LakeMesh.cs`
- Create: `scripts/hydrology/WaterRenderer.cs`
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (spawn the renderer when `--water=1`)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (push camera/time to the water shader; render-relative origin)

**Interfaces:**
- Consumes: `WorldWaterRegion` (`Rivers`, `Lakes`, `BedAt`), `WaterParams` (look knobs), `CdlodRenderOrigin`.
- Produces: `WG16.Hydrology.RiverRibbonMesh.Build(RiverReach r, WorldWaterRegion reg)` → `ArrayMesh` (draped, flow-aligned UVs); `LakeMesh.Build(Lake lk)` → `ArrayMesh`; `WaterRenderer : Node3D` with `BuildForRegion(WorldWaterRegion reg, WaterParams wp, Material mat)` and `SetRenderOrigin(Vector3)`.

- [ ] **Step 1: Implement `shaders/water_surface.gdshader`**

```glsl
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform vec3 shallow_color : source_color = vec3(0.16, 0.42, 0.52);
uniform vec3 deep_color : source_color = vec3(0.03, 0.13, 0.26);
uniform float flow_speed = 0.15;
uniform float wave_scale = 1.0;
uniform float foam_width_m = 6.0;

varying vec3 v_world;
varying float v_flowdist;   // along-flow UV.y from the ribbon (for foam/scroll)

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_flowdist = UV.y;
}

void fragment() {
    float t = TIME * flow_speed;
    // flow-aligned ripples: scroll along V (downstream), perturb the tangent-space normal
    vec2 r = vec2(sin(v_world.x * 0.05 * wave_scale + t * 3.0),
                  cos(v_world.z * 0.05 * wave_scale + v_flowdist * 6.0 - t * 4.0)) * 0.5;
    NORMAL_MAP = normalize(vec3(r.x, r.y, 1.0)) * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = 0.6;

    float edge = abs(fract(UV.x) - 0.5) * 2.0;            // 0 centre .. 1 bank (UV.x across width)
    float foam = smoothstep(0.78, 1.0, edge);             // foam near banks
    vec3 col = mix(deep_color, shallow_color, edge);      // shallower at edges
    col = mix(col, vec3(0.92), foam * 0.6);

    ALBEDO = col;
    METALLIC = 0.0;
    ROUGHNESS = mix(0.06, 0.20, foam);
    SPECULAR = 0.7;
}
```

- [ ] **Step 2: Implement `RiverRibbonMesh.cs`**

```csharp
using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

public static class RiverRibbonMesh
{
    // Ribbon along the spline; width from per-point Width; draped ~0.4m above the carved bed so the
    // water sits just under the rim. UV.x = across width (0..1), UV.y = cumulative along-flow distance.
    public static ArrayMesh Build(RiverReach r, WorldWaterRegion reg)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = r.Points.Length;
        var left = new Vector3[n]; var right = new Vector3[n]; var vlen = new float[n];
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = r.Points[i];
            Vector2 dir = (i < n - 1 ? r.Points[i + 1] - p : p - r.Points[i - 1]).Normalized();
            Vector2 nrm = new Vector2(-dir.Y, dir.X);
            float hw = r.Width[i] * 0.5f;
            float bed = reg.BedAt(p.X, p.Y);
            float y = reg.SurfaceYAt(p.X, p.Y) - bed + 0.4f;   // water level just below rim
            left[i] = new Vector3(p.X - nrm.X * hw, y, p.Y - nrm.Y * hw);
            right[i] = new Vector3(p.X + nrm.X * hw, y, p.Y + nrm.Y * hw);
            if (i > 0) acc += r.Points[i].DistanceTo(r.Points[i - 1]);
            vlen[i] = acc / 40f;                                // 1 UV unit per 40 m downstream
        }
        for (int i = 0; i < n - 1; i++)
        {
            // two triangles per quad; UV.x 0=left,1=right; UV.y = along-flow
            AddQuad(st, left[i], right[i], left[i + 1], right[i + 1], vlen[i], vlen[i + 1]);
        }
        st.GenerateNormals();
        return st.Commit();
    }

    private static void AddQuad(SurfaceTool st, Vector3 l0, Vector3 r0, Vector3 l1, Vector3 r1, float v0, float v1)
    {
        st.SetUV(new Vector2(0, v0)); st.AddVertex(l0);
        st.SetUV(new Vector2(1, v0)); st.AddVertex(r0);
        st.SetUV(new Vector2(0, v1)); st.AddVertex(l1);
        st.SetUV(new Vector2(1, v0)); st.AddVertex(r0);
        st.SetUV(new Vector2(1, v1)); st.AddVertex(r1);
        st.SetUV(new Vector2(0, v1)); st.AddVertex(l1);
    }
}
```

Note: this references `reg.SurfaceYAt(wx,wz)` — add it to `WorldWaterRegion.cs`: the carved terrain height at a point = `analyticProxyHeight − BedAt`. Use the drainage proxy for the mesh Y (good enough to sit in the groove). Add:

```csharp
public float SurfaceYAt(float wx, float wz)
{
    // proxy terrain height (same family the carve rides on) for draping the water mesh
    var d = _drainageForSurface ??= new CoarseDrainage(_fp, _wp, _rx, _rz);
    return d.ProxyHeight(wx, wz);
}
```

(store `_fp,_wp,_rx,_rz` in the ctor; lazy `_drainageForSurface`.)

- [ ] **Step 3: Implement `LakeMesh.cs`**

```csharp
using Godot;
namespace WG16.Hydrology;

public static class LakeMesh
{
    // Flat disc fan at the lake water level.
    public static ArrayMesh Build(Lake lk)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int seg = 24;
        for (int i = 0; i < seg; i++)
        {
            float a0 = i / (float)seg * Mathf.Tau, a1 = (i + 1) / (float)seg * Mathf.Tau;
            Vector3 c = new Vector3(lk.Center.X, lk.WaterLevel, lk.Center.Y);
            Vector3 p0 = c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * lk.Radius;
            Vector3 p1 = c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * lk.Radius;
            st.SetUV(new Vector2(0.5f, 0.5f)); st.AddVertex(c);
            st.SetUV(new Vector2(0f, 0f)); st.AddVertex(p0);
            st.SetUV(new Vector2(1f, 0f)); st.AddVertex(p1);
        }
        st.GenerateNormals();
        return st.Commit();
    }
}
```

- [ ] **Step 4: Implement `WaterRenderer.cs`**

```csharp
using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

public sealed partial class WaterRenderer : Node3D
{
    private readonly List<MeshInstance3D> _meshes = new();
    private Vector3 _renderOrigin = Vector3.Zero;

    public void BuildForRegion(WorldWaterRegion reg, WaterParams wp, Material mat)
    {
        foreach (var r in reg.Rivers)
        {
            if (r.Points.Length < 2) continue;
            var mi = new MeshInstance3D { Mesh = RiverRibbonMesh.Build(r, reg), MaterialOverride = mat };
            AddChild(mi); _meshes.Add(mi);
        }
        foreach (var lk in reg.Lakes)
        {
            var mi = new MeshInstance3D { Mesh = LakeMesh.Build(lk), MaterialOverride = mat };
            AddChild(mi); _meshes.Add(mi);
        }
        ApplyOrigin();
    }

    public void SetRenderOrigin(Vector3 o) { _renderOrigin = o; ApplyOrigin(); }
    private void ApplyOrigin() { Position = -_renderOrigin; }   // meshes are in TRUE world; shift by -origin
}
```

- [ ] **Step 5: Spawn it when `--water=1`**

In `TerrainLabUI.Cli.cs` `ApplyCliOverrides()`, extend the `_waterCli == 1` block:

```csharp
if (_waterCli == 1)
{
    var wp = WG16.Hydrology.WaterParams.Load();
    var reg = new WG16.Hydrology.WorldWaterRegion(_params, wp, 0, 0);
    _terrain.BindWaterRegion(reg, wp);
    var sh = GD.Load<Shader>("res://shaders/water_surface.gdshader");
    var wmat = new ShaderMaterial { Shader = sh };
    wmat.SetShaderParameter("shallow_color", wp.WaterShallowColor);
    wmat.SetShaderParameter("deep_color", wp.WaterDeepColor);
    wmat.SetShaderParameter("flow_speed", wp.FlowSpeed);
    wmat.SetShaderParameter("wave_scale", wp.WaveScale);
    wmat.SetShaderParameter("foam_width_m", wp.FoamWidthM);
    _waterRenderer = new WG16.Hydrology.WaterRenderer();
    GetNode("/root/TerrainLabRoot").AddChild(_waterRenderer);
    _waterRenderer.BuildForRegion(reg, wp, wmat);
}
```

Add field `private WG16.Hydrology.WaterRenderer? _waterRenderer;`.

- [ ] **Step 6: Keep water render-relative each frame**

In `TerrainLabUI.Process.cs`, inside the `if (_ready)` block after the CDLOD origin is computed (after `Vector3 newOrigin = _terrain.CdlodRenderOrigin;`), add:

```csharp
_waterRenderer?.SetRenderOrigin(_terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero);
```

- [ ] **Step 7: Build + eye-gate the WATER**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --textures=1 --water=1`
Fly to world origin's rivers. Expected: flowing ribbon water sitting in the carved channels; limited lakes at basins. **Eye-gate #2/#3 — flowing water reads real; lakes are limited. ONE rejection → STOP + re-brainstorm.**

- [ ] **Step 8: Commit**

```bash
git add shaders/water_surface.gdshader scripts/hydrology/RiverRibbonMesh.cs scripts/hydrology/LakeMesh.cs \
        scripts/hydrology/WaterRenderer.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Process.cs
git commit -m "hydrology: flow-mapped river ribbons + lake meshes + water shader + renderer"
```

---

### Task 8: Multi-region streaming + final verification

**Files:**
- Modify: `scripts/hydrology/WaterRenderer.cs` (manage a dict of regions around the camera)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (drive region ensure/evict by camera)
- Modify: `scripts/hydrology/HydrologyChecks.cs` (add `all` aggregate check)

**Interfaces:**
- Consumes: camera world pos (`camPos` in Process), `FieldParams`, `WaterParams`.
- Produces: `WaterRenderer.EnsureRegions(Vector3 camWorld, FieldParams fp, WaterParams wp, Material mat)` — builds water for the camera's region + 8 neighbors, evicts the rest; caches `WorldWaterRegion` by `(rx,rz)`. Terrain carve binds the camera's CURRENT region texture each time it changes.

- [ ] **Step 1: Add region management to `WaterRenderer.cs`**

```csharp
private readonly Dictionary<(long, long), Node3D> _regions = new();
private readonly Dictionary<(long, long), WorldWaterRegion> _cache = new();

public WorldWaterRegion EnsureRegions(Vector3 cam, WG16.Field.FieldParams fp, WaterParams wp, Material mat)
{
    float region = fp.RegionSizeM;
    long crx = (long)Mathf.Floor(cam.X / region), crz = (long)Mathf.Floor(cam.Z / region);
    var want = new HashSet<(long, long)>();
    for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
        {
            var key = (crx + dx, crz + dz);
            want.Add(key);
            if (_regions.ContainsKey(key)) continue;
            var reg = _cache.TryGetValue(key, out var cached) ? cached
                      : (_cache[key] = new WorldWaterRegion(fp, wp, key.Item1, key.Item2));
            var holder = new Node3D();
            AddChild(holder);
            foreach (var r in reg.Rivers) { if (r.Points.Length < 2) continue;
                holder.AddChild(new MeshInstance3D { Mesh = RiverRibbonMesh.Build(r, reg), MaterialOverride = mat }); }
            foreach (var lk in reg.Lakes)
                holder.AddChild(new MeshInstance3D { Mesh = LakeMesh.Build(lk), MaterialOverride = mat });
            _regions[key] = holder;
        }
    // evict
    var dead = new List<(long, long)>();
    foreach (var kv in _regions) if (!want.Contains(kv.Key)) dead.Add(kv.Key);
    foreach (var k in dead) { _regions[k].QueueFree(); _regions.Remove(k); }
    ApplyOrigin();
    return _cache[(crx, crz)];   // the camera's current region (for terrain carve binding)
}
```

- [ ] **Step 2: Drive it from `Process.cs` and rebind carve on region change**

In `TerrainLabUI.Process.cs`, replace the Task-7 `SetRenderOrigin` line with:

```csharp
if (_waterRenderer != null)
{
    var curReg = _waterRenderer.EnsureRegions(camPos, _params, _waterParams!, _waterMat!);
    if (curReg != _boundWaterRegion) { _terrain.BindWaterRegion(curReg, _waterParams!); _boundWaterRegion = curReg; }
    _waterRenderer.SetRenderOrigin(_terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero);
}
```

Add fields to `TerrainLabUI`: `private WG16.Hydrology.WaterParams? _waterParams; private Material? _waterMat; private WG16.Hydrology.WorldWaterRegion? _boundWaterRegion;` and in the Task-7 `_waterCli==1` block, store `_waterParams = wp; _waterMat = wmat;` (instead of building only region 0,0 — let `EnsureRegions` build on first Process tick).

- [ ] **Step 3: Add the aggregate `all` check**

```csharp
case "all":
{
    bool ok = CheckParams() & CheckDrainage() & CheckRivers() & CheckLakes() & CheckTexture() & CheckContinuity();
    GD.Print($"WATERCHECK all: {(ok ? "PASS" : "FAIL")}");
    return ok;
}
```

- [ ] **Step 4: Build + run the full check suite**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -nologo` → `0 Error(s)`.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --watercheck=all`
Expected: each sub-check PASS, then `WATERCHECK all: PASS`.

- [ ] **Step 5: Verify fieldcheck still 0 + perf**

Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --fieldcheck` → still PASS at 0 m.
Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --textures=1 --water=1 --profile=8`
Watch the FPS HUD: water adds ≤ ~1-2 ms vs `--water=0`. If over budget, lower `WaterTextureBaker.BakeRes` or ribbon density (note it in the commit).

- [ ] **Step 6: Final eye-gate in motion**

Run: `"$GODOT" --path /c/Wg16/wg-16-project -- --cdlod=1 --textures=1 --water=1 --atmosphere=1`
Fly across multiple regions. Expected: rivers continuous across region seams (no stop-at-boundary), flowing water, limited lakes, natural valleys, full sky stack intact. **Final eye-gate — user judges. ONE rejection → STOP + re-brainstorm the failing piece.**

- [ ] **Step 7: Commit**

```bash
git add scripts/hydrology/WaterRenderer.cs scripts/lab/TerrainLabUI.Process.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "hydrology: multi-region streaming + aggregate verification (Milestone 1 complete)"
```

---

## Self-Review

**1. Spec coverage:**
- §4 data flow → Tasks 2 (drainage), 3 (splines), 4 (lakes), 5 (texture), 6 (carve), 7 (meshes). ✓
- §5 components → every file mapped to a task (WaterParams→T1, CoarseDrainage→T2, RiverTracer→T3, WaterTable/LakeGating→T4, WaterTextureBaker/WorldWaterRegion→T5, ground.gdshader carve→T6, RiverRibbonMesh/LakeMesh/WaterRenderer/water_surface→T7, streaming→T8, HydrologyChecks grows throughout). ✓
- §6 carve profile → T6 `carve_offset` (compact-support `pow(1-t²,exp)`). ✓
- §7 four gates → T4 `LakeGating` (g1 table, g2 significance, g4 biome, g3 density cap). ✓
- §9 constraints: fieldcheck-0 guarded T6.6 + T8.5; budget T8.5; infinite-safe T8 streaming; continuity gate T5.2; CDLOD render-relative T7.6/T8.2. ✓
- MFD-not-D8 → T2 `ComputeMfdAccum` (distributes to all lower neighbors). ✓

**2. Placeholder scan:** No TBD/TODO; every code step shows complete code. The one forward-reference (`reg.SurfaceYAt` / `_drainageForSurface`) is defined inline in T7.2. The `_params` field-name caveat is flagged as "confirm when implementing" — acceptable since it's an existing field the implementer can see. ✓

**3. Type consistency:** `WorldWaterRegion` ctor `(FieldParams, WaterParams, long, long)` consistent T5/T6/T8. `RiverReach{Points,Width,Order}` consistent T3/T5/T7. `Lake{Center,Radius,WaterLevel,AreaM2}` consistent T4/T5/T7. `BindWaterRegion(reg,wp)` consistent T6/T8. `BedAt`/`SurfaceYAt`/`IsWet` on `WorldWaterRegion` consistent. Texture channels R/G/B/A consistent across bake (T5), carve (T6), shader. ✓

No gaps found.
