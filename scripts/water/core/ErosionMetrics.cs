using System;
using System.Collections.Generic;
namespace Erosion.Core;

public static class ErosionMetrics
{
    static readonly int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static int PitCount(HeightField f)
    {
        int pits = 0;
        for (int y = 1; y < f.Height - 1; y++)
        for (int x = 1; x < f.Width - 1; x++)
        {
            float h = f.Get(x, y); bool isPit = true;
            for (int k = 0; k < 8; k++)
                if (f.Get(x + dx[k], y + dy[k]) <= h) { isPit = false; break; }
            if (isPit) pits++;
        }
        return pits;
    }

    // local minima that sit at least minDepthM below their LOWEST neighbor — "holes that matter"
    public static int PitCount(HeightField f, float minDepthM)
    {
        int pits = 0;
        for (int y = 1; y < f.Height - 1; y++)
        for (int x = 1; x < f.Width - 1; x++)
        {
            float h = f.Get(x, y), lowest = float.MaxValue;
            for (int k = 0; k < 8; k++) lowest = MathF.Min(lowest, f.Get(x + dx[k], y + dy[k]));
            if (lowest - h >= minDepthM) pits++;
        }
        return pits;
    }

    // deepest local minimum (meters below its lowest neighbor)
    public static float MaxPitDepth(HeightField f)
    {
        float max = 0f;
        for (int y = 1; y < f.Height - 1; y++)
        for (int x = 1; x < f.Width - 1; x++)
        {
            float h = f.Get(x, y), lowest = float.MaxValue;
            for (int k = 0; k < 8; k++) lowest = MathF.Min(lowest, f.Get(x + dx[k], y + dy[k]));
            max = MathF.Max(max, lowest - h);
        }
        return max;
    }

    public static float MassError(HeightField before, HeightField after)
    {
        double sum = 0;
        for (int i = 0; i < before.Data.Length; i++) sum += after.Data[i] - before.Data[i];
        return (float)(sum / before.Data.Length);
    }

    public static float SlopeBudget(HeightField f, float angleDeg)
    {
        float tan = MathF.Tan(angleDeg * MathF.PI / 180f);
        int steep = 0, n = 0;
        for (int y = 1; y < f.Height - 1; y++)
        for (int x = 1; x < f.Width - 1; x++)
        {
            float h = f.Get(x, y), maxS = 0f;
            for (int k = 0; k < 8; k++)
            {
                float d = MathF.Abs(h - f.Get(x + dx[k], y + dy[k]));
                float dist = (dx[k] != 0 && dy[k] != 0) ? f.CellSizeM * 1.41421f : f.CellSizeM;
                maxS = MathF.Max(maxS, d / dist);
            }
            if (maxS > tan) steep++; n++;
        }
        return n == 0 ? 0f : steep / (float)n;
    }

    public static float MaxDiff(HeightField a, HeightField b)
    {
        float m = 0f;
        for (int i = 0; i < a.Data.Length; i++) m = MathF.Max(m, MathF.Abs(a.Data[i] - b.Data[i]));
        return m;
    }
}
