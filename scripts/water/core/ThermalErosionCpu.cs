using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Slope-limited talus / mass-wasting. Material on slopes steeper than the repose
// angle slumps to lower neighbors — fills pit rims (kills holes) without touching
// slopes gentler than the angle (preserves the natural look). Strictly mass-conserving.
public static class ThermalErosionCpu
{
    static readonly int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static HeightField Run(HeightField input, float talusAngleDeg, float strength, int iterations)
    {
        var f = input.Clone();
        int w = f.Width, h = f.Height;
        float tanA = MathF.Tan(talusAngleDeg * MathF.PI / 180f);
        var delta = new float[f.Data.Length];
        var excess = new float[8];

        for (int it = 0; it < iterations; it++)
        {
            Array.Clear(delta);
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                float h0 = f.Get(x, y);
                float sum = 0f, maxEx = 0f;
                for (int k = 0; k < 8; k++)
                {
                    float dist = (dx[k] != 0 && dy[k] != 0) ? f.CellSizeM * 1.41421f : f.CellSizeM;
                    float talus = tanA * dist;
                    float diff = h0 - f.Get(x + dx[k], y + dy[k]);   // >0: neighbor is lower
                    float ex = diff - talus;
                    if (ex > 0f) { excess[k] = ex; sum += ex; if (ex > maxEx) maxEx = ex; }
                    else excess[k] = 0f;
                }
                if (sum <= 0f) continue;
                // move at most half the worst excess, shared by how far each neighbor is below talus
                float move = strength * 0.5f * maxEx;
                for (int k = 0; k < 8; k++)
                {
                    if (excess[k] <= 0f) continue;
                    float portion = move * (excess[k] / sum);
                    delta[y * w + x] -= portion;
                    delta[(y + dy[k]) * w + (x + dx[k])] += portion;
                }
            }
            for (int i = 0; i < delta.Length; i++) f.Data[i] += delta[i];
        }
        return f;
    }
}
