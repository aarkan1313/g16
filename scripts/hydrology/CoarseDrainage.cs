using Godot;
using System;
namespace WG16.Hydrology;

/// Coarse, cheap drainage solve over ONE region + a halo (so rivers flowing in from neighbors are
/// already established before they reach the core). Proxy height = the large-scale continent/uplift
/// macro shape (drainage only needs big-scale slope, not per-octave detail). Flow accumulation is
/// MULTIPLE-FLOW-DIRECTION (MFD): each cell distributes its water to ALL lower neighbors weighted by
/// slope — this is what avoids the D8 single-direction grid faceting (the right-angle-staircase
/// failure of the trashed erosion arc). Deterministic from (seed, regionCell).
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
        float ox = regionX * region - halo * CellSizeM + CellSizeM * 0.5f;
        float oz = regionZ * region - halo * CellSizeM + CellSizeM * 0.5f;
        OriginWorld = new Vector2(ox, oz);

        Height = new float[Res * Res];
        for (int z = 0; z < Res; z++)
            for (int x = 0; x < Res; x++)
                Height[Idx(x, z)] = ProxyHeight(ox + x * CellSizeM, oz + z * CellSizeM);

        Accum = ComputeMfdAccum();
    }

    /// Cheap macro proxy: low-freq continent + uplift, the large-scale slope drainage follows.
    /// Deterministic value-noise FBM — enough to route water, far cheaper than the full field.
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

    /// MFD flow accumulation: process cells high→low; each pushes its accumulated water to ALL lower
    /// neighbors weighted by slope. Distributing to MULTIPLE neighbors avoids D8 grid faceting.
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
