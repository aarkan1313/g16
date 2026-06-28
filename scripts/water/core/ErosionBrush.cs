using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Radial falloff brush, normalized so total weight = 1 (mass-conserving).
// Shared by the CPU sim and the GPU compute path so both erode identically.
public static class ErosionBrush
{
    public static (int[] dx, int[] dy, float[] w) Build(int radius)
    {
        var xs = new List<int>(); var ys = new List<int>(); var ws = new List<float>();
        float sum = 0f;
        for (int j = -radius; j <= radius; j++)
        for (int i = -radius; i <= radius; i++)
        {
            float dist = MathF.Sqrt(i * i + j * j);
            if (dist > radius) continue;
            float wgt = 1f - dist / radius;
            xs.Add(i); ys.Add(j); ws.Add(wgt); sum += wgt;
        }
        var w = ws.ToArray();
        for (int k = 0; k < w.Length; k++) w[k] /= sum;
        return (xs.ToArray(), ys.ToArray(), w);
    }
}
