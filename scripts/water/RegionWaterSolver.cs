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

    public RegionWaterSolver(FieldCompute fc, FieldParams fp, WaterParams wp, IEroder eroder, ErosionParams ep)
    {
        _fc = fc; _fp = fp; _wp = wp; _eroder = eroder; _ep = ep;
        _regionM = fp.RegionSizeM;
        // 2A: solve at 2× the field spacing (≈8 m) so n/64 stays under the 65535 compute-group
        // dispatch limit (the 4 m grid hits ~102k groups). Finer res needs GpuErosion's 1D dispatch
        // reworked to 2D — deferred to 2B/2E.
        _spacing = fp.Spacing * 2f;
        _interior = (int)System.MathF.Round(_regionM / _spacing);
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
        var eroded = _eroder.Erode(hf, _ep);
        var wm = Hydrology.Compute(eroded);
        return WaterPipeline.Build(wm, eroded, _wp);
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
        var eroded = _eroder.Erode(hf, _ep);
        var eroWm = Hydrology.Compute(eroded);
        return $"[diag] spacing={_spacing}m grid={gridN} relief({RegionDebugViz.Relief(hf)})\n" +
               $"[diag] RAW    {RegionDebugViz.HydroStats(rawWm, _wp.MinDepth, _wp.RiverThreshold)}\n" +
               $"[diag] ERODED {RegionDebugViz.HydroStats(eroWm, _wp.MinDepth, _wp.RiverThreshold)}";
    }

    public static ErosionParams DefaultErosion() => new ErosionParams
    {
        DropletCount = 3_000_000, Seed = 1, ErosionRate = 0.35f, DepositionRate = 0.06f,
        CapacityFactor = 3.5f, MaxLifetime = 96, ErosionRadius = 5,
        TalusAngleDeg = 40f, ThermalStrength = 0.6f, ThermalIterations = 70,
    };
}
