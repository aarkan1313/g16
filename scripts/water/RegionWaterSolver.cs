using Erosion.Core;
using WG16.Field;
using System.Collections.Generic;

namespace WG16.Water;

// Per-region water solve, synchronous, LRU-cached. Solves a region+halo window so the kept
// interior is seam-free; the full solve-grid WaterData is returned keyed to its true-world
// origin (region*RegionSizeM - halo). Async/threading is Phase 2E.
public sealed class RegionWaterSolver
{
    const int HaloCells = 256;   // 1024 m at 4 m spacing — wider than erosion reach
    const int CacheCap = 2;

    readonly FieldCompute _fc; readonly FieldParams _fp; readonly WaterParams _wp;
    readonly IEroder _eroder; readonly ErosionParams _ep;
    readonly float _regionM, _spacing; readonly int _interior;
    readonly Dictionary<(int, int), WaterData> _cache = new();
    readonly LinkedList<(int, int)> _lru = new();

    // Droplet density (droplets per cell) tuned at the 8 m solve (3M over the 1536² grid ≈ 1.27/cell);
    // preserved across resolution so erosion intensity is the same at native 4 m (GpuErosion thermal
    // now dispatches 2D, so the finer grid no longer hits Vulkan's 65535 group limit).
    const double DropletDensity = 3_000_000.0 / (1536.0 * 1536.0);

    public RegionWaterSolver(FieldCompute fc, FieldParams fp, WaterParams wp, IEroder eroder, ErosionParams ep)
    {
        _fc = fc; _fp = fp; _wp = wp; _eroder = eroder; _ep = ep;
        _regionM = fp.RegionSizeM;
        _spacing = fp.Spacing;                  // 2B: native 4 m solve (2D thermal dispatch lifts the limit)
        _interior = (int)System.MathF.Round(_regionM / _spacing);
    }

    // Erosion adapted to the solve resolution (ErosionParams is a mutable class, not a record —
    // clone field-by-field). Droplet count scaled to keep per-cell intensity constant; thermal
    // strength scaled by (spacing/8)² because the explicit talus-slump scheme is diffusion-like —
    // its stable step ∝ cellSize², so the 8 m-tuned 0.6 strength diverges at finer grids.
    const float ThermalRefSpacingM = 8f; // spacing the 8 m thermal strength was tuned/stable at
    ErosionParams ScaledErosion(int gridN)
    {
        // Stable thermal step ∝ cellSize², so cut per-iteration strength by (spacing/8)² to avoid the
        // explicit-scheme divergence at finer grids. (Iterations kept constant — scaling them up 4×
        // didn't change drainage, since thermal is mass-conserving, and quadrupled the solve time.)
        float clamp = System.MathF.Min(1f, (_spacing / ThermalRefSpacingM) * (_spacing / ThermalRefSpacingM));
        return new ErosionParams
        {
            DropletCount = (int)(DropletDensity * gridN * (long)gridN),
            MaxLifetime = _ep.MaxLifetime, Inertia = _ep.Inertia, CapacityFactor = _ep.CapacityFactor,
            MinSlope = _ep.MinSlope, ErosionRate = _ep.ErosionRate, DepositionRate = _ep.DepositionRate,
            Evaporation = _ep.Evaporation, Gravity = _ep.Gravity, InitialWater = _ep.InitialWater,
            InitialSpeed = _ep.InitialSpeed, ErosionRadius = _ep.ErosionRadius, Seed = _ep.Seed,
            TalusAngleDeg = _ep.TalusAngleDeg,
            ThermalStrength = _ep.ThermalStrength * clamp,
            ThermalIterations = _ep.ThermalIterations,
        };
    }

    public WaterData GetOrSolve(int rx, int rz)
    {
        var key = (rx, rz);
        if (_cache.TryGetValue(key, out var hit)) { _lru.Remove(key); _lru.AddFirst(key); return hit; }
        var wd = Solve(rx, rz);
        _cache[key] = wd; _lru.AddFirst(key);
        while (_lru.Count > CacheCap) { var last = _lru.Last!.Value; _lru.RemoveLast(); _cache.Remove(last); }
        return wd;
    }

    WaterData Solve(int rx, int rz)
    {
        int gridN = _interior + 2 * HaloCells;
        float originX = rx * _regionM - HaloCells * _spacing;
        float originZ = rz * _regionM - HaloCells * _spacing;
        var hf = FieldHeightSource.Bake(_fc, _fp, originX, originZ, _spacing, gridN);
        // Breach twice. PRE-erosion gives droplets exits so they carve real fluvial valleys.
        // Erosion's thermal/smoothing re-dams some notches, so breach AGAIN on the eroded surface
        // — that POST pass is the one that guarantees the surface Hydrology.Compute floods drains.
        var pre = Hydrology.Condition(hf, _wp.MaxBreachDepth, _wp.MaxBreachLength);
        var eroded = _eroder.Erode(pre, ScaledErosion(gridN));
        var conditioned = Hydrology.Condition(eroded, _wp.MaxBreachDepth, _wp.MaxBreachLength);
        var wm = Hydrology.Compute(conditioned);
        return WaterPipeline.Build(wm, conditioned, _wp);
    }

    // The world window the region solve covers (interior + halo), so a delta texture sampled at
    // true world XZ normalises correctly: uv = (wxz - Origin) / SizeM over the full baked grid.
    public (float OriginX, float OriginZ, float SizeM, int Grid) RegionWindow(int rx, int rz)
    {
        int gridN = _interior + 2 * HaloCells;
        return (rx * _regionM - HaloCells * _spacing, rz * _regionM - HaloCells * _spacing, gridN * _spacing, gridN);
    }

    // Per-cell rendered height delta = solved(Carved) - rawField, row-major over the full baked grid.
    // This is the FULL coupled delta (erosion + breach + carve); the rendered terrain becomes the
    // solved surface so rivers sit in real valleys. The raw breach notch is a 1-cell-wide drainage
    // device — rendered literally it reads as a jagged deep slot, so a box blur WIDENS + SHALLOWS +
    // smooths it into a natural valley cross-section (volume spreads laterally → wide shallow trough,
    // which is how real river valleys look). Edges feathered to 0 over the halo (no boundary cliff).
    public float[] BuildDelta(int rx, int rz, int blurRadius)
    {
        var wd = GetOrSolve(rx, rz);
        var win = RegionWindow(rx, rz);
        var raw = FieldHeightSource.Bake(_fc, _fp, win.OriginX, win.OriginZ, _spacing, win.Grid);
        int g = win.Grid; var delta = new float[g * g];
        for (int i = 0; i < delta.Length; i++) delta[i] = wd.Carved.Data[i] - raw.Data[i];
        if (blurRadius > 0)
        {
            var hf = new HeightField(g, g, _spacing);
            System.Array.Copy(delta, hf.Data, delta.Length);
            var sm = Smoothing.Box(hf, blurRadius, 1);
            System.Array.Copy(sm.Data, delta, delta.Length);
        }
        FeatherEdges(delta, g, HaloCells / 2); // ramp to 0 across half the halo so the region edge has no cliff
        return delta;
    }

    static void FeatherEdges(float[] d, int g, int band)
    {
        if (band <= 0) return;
        for (int y = 0; y < g; y++)
        for (int x = 0; x < g; x++)
        {
            int edge = System.Math.Min(System.Math.Min(x, y), System.Math.Min(g - 1 - x, g - 1 - y));
            if (edge >= band) continue;
            d[y * g + x] *= edge / (float)band;
        }
    }

    // Flooding-cause probe: bakes once, then runs hydrology on the RAW field vs the ERODED
    // field so we can see whether the basins come from the source terrain or from erosion,
    // plus the field relief and lake-depth distribution. Returns a multi-line report.
    public string Diagnose(int rx, int rz)
    {
        int gridN = _interior + 2 * HaloCells;
        float originX = rx * _regionM - HaloCells * _spacing;
        float originZ = rz * _regionM - HaloCells * _spacing;
        var hf = FieldHeightSource.Bake(_fc, _fp, originX, originZ, _spacing, gridN);
        var rawWm = Hydrology.Compute(hf);
        var pre = Hydrology.Condition(hf, _wp.MaxBreachDepth, _wp.MaxBreachLength);
        var eroded = _eroder.Erode(pre, ScaledErosion(gridN));
        var eroWm = Hydrology.Compute(eroded);
        var conditioned = Hydrology.Condition(eroded, _wp.MaxBreachDepth, _wp.MaxBreachLength);
        var finalWm = Hydrology.Compute(conditioned);
        return $"[diag] spacing={_spacing}m grid={gridN} breachDepth={_wp.MaxBreachDepth}m relief({RegionDebugViz.Relief(hf)})\n" +
               $"[diag] RAW           {RegionDebugViz.HydroStats(rawWm, _wp.MinDepth, _wp.RiverThreshold)}\n" +
               $"[diag] PRE+ERODE     {RegionDebugViz.HydroStats(eroWm, _wp.MinDepth, _wp.RiverThreshold)}\n" +
               $"[diag] +POST(final)  {RegionDebugViz.HydroStats(finalWm, _wp.MinDepth, _wp.RiverThreshold)}";
    }

    public static ErosionParams DefaultErosion() => new ErosionParams
    {
        DropletCount = 3_000_000, Seed = 1, ErosionRate = 0.35f, DepositionRate = 0.06f,
        CapacityFactor = 3.5f, MaxLifetime = 96, ErosionRadius = 5,
        TalusAngleDeg = 40f, ThermalStrength = 0.6f, ThermalIterations = 70,
    };

    // Water params for WG16's closed-basin field, expressed for the native 4 m grid. The 8 m eye-gate
    // (PNG 2026-06-28) found the breach LENGTH bound decisive (basins drain far away, ~6.4 km); at 4 m
    // that reach is 1600 cells (was 800 at 8 m), and the 4096 m² lake-cull is 256 cells (was 64).
    // Breach basins up to 80 m deep (depth is metres, resolution-independent); deeper stay lakes.
    public static WaterParams DefaultWater() => new WaterParams(
        MaxBreachDepth: 90f, MaxBreachLength: 1600, MinLakeArea: 256);
}
