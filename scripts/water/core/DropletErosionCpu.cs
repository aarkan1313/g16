using System;
using System.Collections.Generic;
namespace Erosion.Core;

public sealed class ErosionResult
{
    public required HeightField Eroded;
    public required float[] DeltaMap;
}

public static class DropletErosionCpu
{
    public static ErosionResult Run(HeightField input, ErosionParams p)
    {
        var f = input.Clone();
        int w = f.Width, h = f.Height;
        var rng = new Random(p.Seed);

        // precompute brush offsets + weights (radial falloff) — spreads each cut over a disc
        var (bx, by, bw) = ErosionBrush.Build(p.ErosionRadius);

        for (int d = 0; d < p.DropletCount; d++)
        {
            float px = (float)(rng.NextDouble() * (w - 1));
            float py = (float)(rng.NextDouble() * (h - 1));
            float dirX = 0, dirY = 0, speed = p.InitialSpeed, water = p.InitialWater, sediment = 0f;
            int cellX = (int)px, cellY = (int)py; float offX = 0, offY = 0;

            for (int life = 0; life < p.MaxLifetime; life++)
            {
                cellX = (int)px; cellY = (int)py;
                offX = px - cellX; offY = py - cellY;
                var (oldH, gx, gy) = HeightAndGradient(f, px, py);

                dirX = dirX * p.Inertia - gx * (1 - p.Inertia);
                dirY = dirY * p.Inertia - gy * (1 - p.Inertia);
                float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (len < 1e-6f) break;
                dirX /= len; dirY /= len;
                px += dirX; py += dirY;
                if (px < 0 || px >= w - 1 || py < 0 || py >= h - 1) break;

                float newH = HeightAndGradient(f, px, py).h;
                float dH = newH - oldH;                 // >0 going uphill
                float capacity = MathF.Max(-dH, p.MinSlope) * speed * water * p.CapacityFactor;

                if (sediment > capacity || dH > 0)
                {
                    // deposit — fill the cell it climbs into (anti-pit) or shed excess
                    float deposit = (dH > 0) ? MathF.Min(dH, sediment)
                                             : (sediment - capacity) * p.DepositionRate;
                    sediment -= deposit;
                    DepositBilinear(f, cellX, cellY, offX, offY, deposit);
                }
                else
                {
                    float erode = MathF.Min((capacity - sediment) * p.ErosionRate, -dH);
                    sediment += erode;
                    ErodeBrush(f, cellX, cellY, bx, by, bw, erode);   // spread over disc → no single-cell holes
                }

                speed = MathF.Sqrt(MathF.Max(0f, speed * speed + dH * -p.Gravity));
                water *= (1 - p.Evaporation);
                if (water < 1e-4f) break;
            }

            // mass conservation: a dying droplet drops whatever it still carries
            if (sediment > 0f) DepositBilinear(f, cellX, cellY, offX, offY, sediment);
        }

        var delta = new float[f.Data.Length];
        for (int i = 0; i < delta.Length; i++) delta[i] = f.Data[i] - input.Data[i];
        return new ErosionResult { Eroded = f, DeltaMap = delta };
    }

    // bilinear height + smooth gradient from the 4 corners of the containing cell (Lague)
    static (float h, float gx, float gy) HeightAndGradient(HeightField f, float px, float py)
    {
        int x0 = Math.Min((int)px, f.Width - 2);
        int y0 = Math.Min((int)py, f.Height - 2);
        float u = px - x0, v = py - y0;
        float nw = f.Get(x0, y0), ne = f.Get(x0 + 1, y0), sw = f.Get(x0, y0 + 1), se = f.Get(x0 + 1, y0 + 1);
        float gx = (ne - nw) * (1 - v) + (se - sw) * v;
        float gy = (sw - nw) * (1 - u) + (se - ne) * u;
        float hh = nw * (1 - u) * (1 - v) + ne * u * (1 - v) + sw * (1 - u) * v + se * u * v;
        return (hh, gx, gy);
    }

    static void DepositBilinear(HeightField f, int x, int y, float cx, float cy, float amt)
    {
        Add(f, x, y, amt * (1 - cx) * (1 - cy));
        Add(f, x + 1, y, amt * cx * (1 - cy));
        Add(f, x, y + 1, amt * (1 - cx) * cy);
        Add(f, x + 1, y + 1, amt * cx * cy);
    }

    static void ErodeBrush(HeightField f, int x, int y, int[] bx, int[] by, float[] bw, float amt)
    {
        for (int k = 0; k < bx.Length; k++)
            Add(f, x + bx[k], y + by[k], -amt * bw[k]);
    }

    static void Add(HeightField f, int x, int y, float v)
    {
        if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return;
        f.Data[y * f.Width + x] += v;
    }
}
