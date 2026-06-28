using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Separable box blur — removes small underfoot bumps while preserving macro
// landforms (valleys/ridges are much wider than the kernel). Tunable strength.
public static class Smoothing
{
    public static HeightField Box(HeightField src, int radius, int iterations)
    {
        if (radius < 1 || iterations < 1) return src.Clone();
        var f = src.Clone();
        int w = f.Width, h = f.Height;
        var tmp = new float[w * h];
        float inv = 1f / (2 * radius + 1);

        for (int it = 0; it < iterations; it++)
        {
            // horizontal
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float s = 0f;
                    for (int d = -radius; d <= radius; d++)
                        s += f.Data[row + Math.Clamp(x + d, 0, w - 1)];
                    tmp[row + x] = s * inv;
                }
            }
            // vertical
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                float s = 0f;
                for (int d = -radius; d <= radius; d++)
                    s += tmp[Math.Clamp(y + d, 0, h - 1) * w + x];
                f.Data[y * w + x] = s * inv;
            }
        }
        return f;
    }
}
