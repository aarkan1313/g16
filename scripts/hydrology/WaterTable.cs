using Godot;
using System;
namespace WG16.Hydrology;

/// Low-frequency moisture / water-table layer (the page's Step 1). Drives where lakes/wetlands are
/// likely (wet regions vs dry) and a cheap biome classifier. Deterministic from seed.
public sealed class WaterTable
{
    private readonly float _freq;
    private readonly uint _seed;
    public WaterTable(WaterParams wp, uint seed) { _freq = wp.WaterTableFreq; _seed = seed ^ 0x7a7e7700u; }

    /// 0..1 moisture at a world point.
    public float At(float wx, float wz)
    {
        float n = ValueNoise(wx * _freq, wz * _freq);
        n = 0.6f * n + 0.4f * ValueNoise(wx * _freq * 2.3f + 11f, wz * _freq * 2.3f - 7f);
        return Mathf.Clamp(n, 0f, 1f);
    }

    /// 0 dry/desert, 1 temperate, 2 wetland, 3 alpine — cheap classifier from elevation + table.
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
        return Mathf.Lerp(Mathf.Lerp(H(xi, yi), H(xi + 1, yi), u), Mathf.Lerp(H(xi, yi + 1), H(xi + 1, yi + 1), u), v);
    }
    private float H(int x, int y)
    {
        uint h = (uint)(x * 374761393) ^ (uint)(y * 668265263) ^ _seed;
        h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }
}
