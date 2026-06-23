using System;
using WG16.Field;

namespace WG16.Hydrology;

/// The WATER PRODUCER for the infinite world: compute drainage + significant lakes ONCE on a cheap low-resolution
/// world-spanning coarse grid, cache it, and expose sampleable per-world-position water fields. The lab AND every
/// CDLOD chunk SAMPLE this instead of building a per-region graph — so water is tile-coherent by construction
/// (one shared map), has no per-chunk graph-build hitch (just a bilinear sample), and spans many regions (not the
/// misleading single-basin lab). Built from the same DrainageGraph + WaterBodies algorithm, run at world scale.
///
/// "Infinite" note: this instance covers a fixed large world span (CoverM). True infinity tiles this with a halo;
/// for now CoverM is sized to comfortably exceed the playable/lab area. Deterministic: same span → same map.
public sealed class CoarseWorldWater
{
    private readonly int _res;            // coarse grid cells per side over the world span
    private readonly float _originX, _originZ, _spacing, _coverM;
    private readonly float[] _surface;    // per coarse cell: water-surface Y where wet, else NoWater sentinel
    private readonly float[] _bed;        // per coarse cell: terrain (Filled) bed under the water
    public const float NoWater = -1e9f;

    public int LakeCount { get; }
    public float SeaLevel { get; }
    public bool SeaEnabled { get; }

    private CoarseWorldWater(int res, float ox, float oz, float sp, float cover,
                             float[] surface, float[] bed, int lakeCount, float seaLevel, bool seaEnabled)
    { _res = res; _originX = ox; _originZ = oz; _spacing = sp; _coverM = cover;
      _surface = surface; _bed = bed; LakeCount = lakeCount; SeaLevel = seaLevel; SeaEnabled = seaEnabled; }

    /// Build the world water map centered on (centerX, centerZ), covering coverM metres, at coarseSpacing.
    public static CoarseWorldWater Build(FieldCompute fc, FieldParams p, WaterParams wp, HydrologyParams hp,
                                         float centerX, float centerZ, float coverM)
    {
        int res = Math.Max(16, (int)MathF.Round(coverM / hp.CoarseSpacing));
        float sp = hp.CoarseSpacing;
        float ox = centerX - coverM * 0.5f, oz = centerZ - coverM * 0.5f;
        var cf = CoarseField.Build(fc, p, ox, oz, sp, res);
        var g = DrainageGraph.Build(cf, hp);            // priority-flood fill + drainage on the world-coarse grid
        var wb = WaterBodies.Build(g, wp);              // significance-filtered LIMITED lakes (+ rivers)

        var surface = new float[res * res];
        var bed = new float[res * res];
        for (int i = 0; i < surface.Length; i++) { surface[i] = NoWater; bed[i] = cf.H(i % res, i / res); }

        // SEA: every coarse cell whose terrain dips below the sea level is sea (when enabled).
        if (wp.SeaEnabled)
        {
            for (int i = 0; i < surface.Length; i++)
            { if (g.Filled[i] < wp.SeaLevel) { surface[i] = wp.SeaLevel; } }
        }

        // LAKES: mark cells inside each significant lake's basin (submerged below its spill surface) — organic,
        // gated by submergence like the carve, so it follows the basin not the bounding box. Lake wins over sea.
        foreach (var lk in wb.Lakes)
        {
            int x0 = (int)MathF.Floor((lk.MinX - ox) / sp), x1 = (int)MathF.Ceiling((lk.MaxX - ox) / sp);
            int z0 = (int)MathF.Floor((lk.MinZ - oz) / sp), z1 = (int)MathF.Ceiling((lk.MaxZ - oz) / sp);
            for (int z = Math.Max(z0, 0); z <= Math.Min(z1, res - 1); z++)
            for (int x = Math.Max(x0, 0); x <= Math.Min(x1, res - 1); x++)
            {
                int i = z * res + x;
                if (cf.H(x, z) <= lk.SurfaceLevel) { surface[i] = MathF.Max(surface[i] == NoWater ? -1e30f : surface[i], lk.SurfaceLevel); }
            }
        }

        return new CoarseWorldWater(res, ox, oz, sp, coverM, surface, bed, wb.Lakes.Count, wp.SeaLevel, wp.SeaEnabled);
    }

    private float SampleField(float[] f, float wx, float wz, float noData)
    {
        float gx = (wx - _originX) / _spacing, gz = (wz - _originZ) / _spacing;
        if (gx < 0 || gz < 0 || gx > _res - 1 || gz > _res - 1) { return noData; }
        int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
        int x1 = Math.Min(x0 + 1, _res - 1), z1 = Math.Min(z0 + 1, _res - 1);
        float fx = gx - x0, fz = gz - z0;
        // sentinel-aware: if any corner is NoWater, treat the whole sample as the nearest non-sentinel or noData.
        // for surface we want the max of contributing wet corners so shorelines stay crisp (not averaged to dry).
        float a = f[z0 * _res + x0], b = f[z0 * _res + x1], c = f[z1 * _res + x0], d = f[z1 * _res + x1];
        // simple: nearest cell (crisp). bilinear of a step field smears the waterline; nearest keeps it honest.
        int nx = (fx < 0.5f) ? x0 : x1, nz = (fz < 0.5f) ? z0 : z1;
        return f[nz * _res + nx];
    }

    /// Water-surface elevation at world (wx,wz), or NoWater if dry. (Nearest-cell — crisp waterline.)
    public float SurfaceAt(float wx, float wz) => SampleField(_surface, wx, wz, NoWater);

    /// Terrain bed elevation at world (wx,wz) from the coarse map (for water-depth shading).
    public float BedAt(float wx, float wz) => SampleField(_bed, wx, wz, 0f);

    public bool IsWet(float wx, float wz) => SurfaceAt(wx, wz) > NoWater;
}
