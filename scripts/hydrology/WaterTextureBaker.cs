using Godot;
using System;
using System.Collections.Generic;
namespace WG16.Hydrology;

/// Rasterizes river splines + lake polygons into the per-region RGBA water texture — the ONE source
/// of truth every downstream consumer (terrain carve, ribbon meshes, foam) samples.
///   R = distance-to-river (metres, clamped to CarveWidthM; 0 inside a lake)
///   G = bed-depth target (metres to carve below local terrain)
///   B = flow direction angle / 2π (0..1)
///   A = wet mask (rivers + lakes), 0..1
/// CPU bake at BakeRes² — fast and deterministic; a GPU-compute bake is a later optimization.
public static class WaterTextureBaker
{
    public const int BakeRes = 512;                          // per-region bake resolution

    public static Image Bake(List<RiverReach> rivers, List<Lake> lakes, WaterParams wp,
                             Vector2 regionOrigin, float regionM)
    {
        int n = BakeRes;
        float cell = regionM / n;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgbaf);
        // init: far distance, no depth, dry
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                img.SetPixel(x, y, new Color(wp.CarveWidthM, 0f, 0f, 0f));

        // Rivers → nearest-segment distance + flow angle + bed depth.
        foreach (var r in rivers)
        {
            for (int i = 0; i < r.Points.Length - 1; i++)
            {
                Vector2 p0 = r.Points[i], p1 = r.Points[i + 1];
                float halfW = Mathf.Max(r.Width[i], r.Width[i + 1]) * 0.5f + wp.CarveWidthM;
                float ang = Mathf.PosMod(MathF.Atan2(p1.Y - p0.Y, p1.X - p0.X), Mathf.Tau) / Mathf.Tau;
                float minx = Mathf.Min(p0.X, p1.X) - halfW, maxx = Mathf.Max(p0.X, p1.X) + halfW;
                float miny = Mathf.Min(p0.Y, p1.Y) - halfW, maxy = Mathf.Max(p0.Y, p1.Y) + halfW;
                int px0 = Mathf.Clamp((int)((minx - regionOrigin.X) / cell), 0, n - 1);
                int px1 = Mathf.Clamp((int)((maxx - regionOrigin.X) / cell), 0, n - 1);
                int py0 = Mathf.Clamp((int)((miny - regionOrigin.Y) / cell), 0, n - 1);
                int py1 = Mathf.Clamp((int)((maxy - regionOrigin.Y) / cell), 0, n - 1);
                for (int py = py0; py <= py1; py++)
                    for (int px = px0; px <= px1; px++)
                    {
                        var wpos = new Vector2(regionOrigin.X + (px + 0.5f) * cell, regionOrigin.Y + (py + 0.5f) * cell);
                        float dist = SegDist(wpos, p0, p1);
                        if (dist >= wp.CarveWidthM) continue;          // outside carve reach
                        float chanHalf = Mathf.Lerp(r.Width[i], r.Width[i + 1], 0.5f) * 0.5f;
                        Color c = img.GetPixel(px, py);
                        if (dist < c.R)                                // keep nearest segment
                        {
                            float wet = dist <= chanHalf ? 1f : 0f;
                            float depth = wp.BedDepthM * wp.CarveDepthScale;
                            img.SetPixel(px, py, new Color(dist, depth, ang, Mathf.Max(c.A, wet)));
                        }
                    }
            }
        }

        // Lakes → flat disc into the mask + bed depth (distance 0 = full carve at centre).
        foreach (var lk in lakes)
        {
            int px0 = Mathf.Clamp((int)((lk.Center.X - lk.Radius - regionOrigin.X) / cell), 0, n - 1);
            int px1 = Mathf.Clamp((int)((lk.Center.X + lk.Radius - regionOrigin.X) / cell), 0, n - 1);
            int py0 = Mathf.Clamp((int)((lk.Center.Y - lk.Radius - regionOrigin.Y) / cell), 0, n - 1);
            int py1 = Mathf.Clamp((int)((lk.Center.Y + lk.Radius - regionOrigin.Y) / cell), 0, n - 1);
            for (int py = py0; py <= py1; py++)
                for (int px = px0; px <= px1; px++)
                {
                    var wpos = new Vector2(regionOrigin.X + (px + 0.5f) * cell, regionOrigin.Y + (py + 0.5f) * cell);
                    float dd = wpos.DistanceTo(lk.Center);
                    if (dd <= lk.Radius)
                    {
                        Color c = img.GetPixel(px, py);
                        float depth = Mathf.Max(c.G, wp.BedDepthM * wp.CarveDepthScale);
                        img.SetPixel(px, py, new Color(Mathf.Min(c.R, 0f), depth, c.B, 1f));
                    }
                }
        }
        return img;
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0f : Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }
}
