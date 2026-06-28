using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Per-cell downstream flow vector (length = slope-derived speed) + foam factor, blurred
// over wet cells so flow is coherent and foam de-staircased. Lifted from the old
// WaterMesh; pure so it transfers and is testable.
public static class FlowField
{
    public static (bool[] Wet, float[] FlowX, float[] FlowZ, float[] Foam)
        Compute(WaterMap wm, LakeSet ls, HeightField carved, WaterParams p)
    {
        int w = carved.Width, h = carved.Height, n = w * h;
        float cs = carved.CellSizeM, diag = cs * 1.41421356f;
        float foamLo = p.FoamSlope * 0.5f, foamHi = p.FoamSlope * 1.5f;

        var wet = new bool[n];
        var fx = new float[n]; var fz = new float[n]; var foam = new float[n];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (wm.Filled[i] - carved.Data[i] <= p.MinDepth) continue;
            wet[i] = true;
            if (ls.LakeId[i] >= 0) continue;                 // lakes: still, no flow/foam
            int d = wm.Down[i];
            if (d < 0) continue;
            int ddx = (d % w) - x, ddz = (d / w) - y;
            float l = MathF.Sqrt(ddx * ddx + ddz * ddz);
            float slope = (wm.Filled[i] - wm.Filled[d]) / (ddx != 0 && ddz != 0 ? diag : cs);
            float speed = Math.Clamp(0.3f + slope * 2.5f, 0.3f, 1.0f);
            if (l > 0f) { fx[i] = ddx / l * speed; fz[i] = ddz / l * speed; }
            foam[i] = Math.Clamp((slope - foamLo) / MathF.Max(1e-4f, foamHi - foamLo), 0f, 1f);
        }
        SmoothVec(fx, fz, wet, w, h, p.FlowSmoothIters);
        SmoothScalar(foam, wet, w, h, p.FoamSmoothIters);
        return (wet, fx, fz, foam);
    }

    static void SmoothVec(float[] fx, float[] fz, bool[] wet, int w, int h, int iters)
    {
        var ox = new float[fx.Length]; var oz = new float[fz.Length];
        for (int it = 0; it < iters; it++)
        {
            Array.Copy(fx, ox, fx.Length); Array.Copy(fz, oz, fz.Length);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; if (!wet[i]) continue;
                float sx = 0f, sz = 0f; int c = 0;
                for (int jy = -1; jy <= 1; jy++)
                for (int jx = -1; jx <= 1; jx++)
                {
                    int nx = x + jx, ny = y + jy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx; if (!wet[ni]) continue;
                    sx += ox[ni]; sz += oz[ni]; c++;
                }
                if (c > 0) { fx[i] = sx / c; fz[i] = sz / c; }
            }
        }
    }

    static void SmoothScalar(float[] f, bool[] wet, int w, int h, int iters)
    {
        var o = new float[f.Length];
        for (int it = 0; it < iters; it++)
        {
            Array.Copy(f, o, f.Length);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; if (!wet[i]) continue;
                float s = 0f; int c = 0;
                for (int jy = -1; jy <= 1; jy++)
                for (int jx = -1; jx <= 1; jx++)
                {
                    int nx = x + jx, ny = y + jy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx; if (!wet[ni]) continue;
                    s += o[ni]; c++;
                }
                if (c > 0) f[i] = s / c;
            }
        }
    }
}
