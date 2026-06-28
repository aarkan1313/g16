using System;
using System.Collections.Generic;
namespace Erosion.Core;

public static class ProceduralTerrain
{
    public static HeightField Fbm(int size, float cellSizeM, int seed, float amplitudeM = 600f)
    {
        var f = new HeightField(size, size, cellSizeM);
        float[] amps = { 1f, 0.5f, 0.25f, 0.125f, 0.0625f, 0.03f, 0.015f, 0.0075f };
        float[] freqs = { 1f, 2f, 4f, 8f, 16f, 32f, 64f, 128f };
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float h = 0f;
            for (int o = 0; o < amps.Length; o++)
                h += amps[o] * ValueNoise(x / (float)size * freqs[o], y / (float)size * freqs[o], seed + o);
            f.Set(x, y, h * amplitudeM);
        }
        return f;
    }

    // Ridged multifractal: sharp ridgelines + valleys — real macro structure for
    // erosion to carve, instead of uniform rolling bumps.
    public static HeightField Mountains(int size, float cellSizeM, int seed, float amplitudeM = 1100f)
    {
        var f = new HeightField(size, size, cellSizeM);
        const int oct = 5; // fewer high-freq octaves → smooth macro ridges (less underfoot bumpiness)
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float nx = x / (float)size, ny = y / (float)size;
            float freq = 1.3f, amp = 1f, sum = 0f, norm = 0f;
            for (int o = 0; o < oct; o++)
            {
                float n = ValueNoise(nx * freq, ny * freq, seed + o);
                float r = 1f - MathF.Abs(2f * n - 1f); // ridge
                sum += r * r * amp;
                norm += amp;
                freq *= 2f; amp *= 0.5f;
            }
            float ridged = sum / norm;                 // [0,1]
            f.Set(x, y, MathF.Pow(ridged, 1.4f) * amplitudeM); // sharpen valleys
        }
        return f;
    }

    static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float tx = Smooth(x - x0), ty = Smooth(y - y0);
        float a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed);
        float c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
    }
    static float Smooth(float t) => t * t * (3 - 2 * t);
    static float Hash(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }
}
