using Godot;
using System;
using System.Collections.Generic;
namespace WG16.Hydrology;

/// One river reach: world-XZ polyline (Chaikin-smoothed), per-point width (from accumulated flow),
/// and Strahler order (1 for this milestone; merge-ordering is a later refinement).
public record RiverReach(Vector2[] Points, float[] Width, int Order);

/// Extracts river splines from the coarse drainage. A "source" is an above-threshold cell with no
/// above-threshold higher neighbor; from each source we walk steepest-descent (single-path for the
/// SPLINE — accumulation was MFD) until we leave the grid or hit a local minimum, then Chaikin-smooth.
/// Width scales with sqrt(flow) (the page's width = sqrt(accum) * scale).
public static class RiverTracer
{
    private const int MinReachCells = 8;   // drop reaches shorter than this (tiny orphan blips)

    public static List<RiverReach> Trace(CoarseDrainage d, WaterParams wp)
    {
        var reaches = new List<RiverReach>();
        int res = d.Res;
        var visited = new bool[res * res];

        // Collect all SOURCE cells (above threshold, no above-threshold higher neighbor), then trace them
        // in DESCENDING flow-accumulation order — trunks before tributaries. That ordering makes the
        // confluence rule (stop when the next cell is already a river) merge tributaries cleanly INTO the
        // established trunk, instead of fragmenting the trunk at the first tributary it meets.
        var sources = new List<int>();
        for (int z = 1; z < res - 1; z++)
            for (int x = 1; x < res - 1; x++)
            {
                int i = d.Idx(x, z);
                if (d.Accum[i] < wp.RiverAccumThreshold) continue;
                bool isSource = true;
                for (int dz = -1; dz <= 1 && isSource; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int j = d.Idx(x + dx, z + dz);
                        if (d.Height[j] > d.Height[i] && d.Accum[j] >= wp.RiverAccumThreshold) { isSource = false; break; }
                    }
                if (isSource) sources.Add(i);
            }
        sources.Sort((a, b) => d.Accum[b].CompareTo(d.Accum[a]));   // biggest flow first

        foreach (int src in sources)
            {
                if (visited[src]) continue;
                int x = src % res, z = src / res;
                var pts = new List<Vector2>();
                var wid = new List<float>();
                int cx = x, cz = z, guard = 0;
                while (guard++ < res * 2)
                {
                    int ci = d.Idx(cx, cz);
                    visited[ci] = true;
                    pts.Add(new Vector2(d.OriginWorld.X + cx * d.CellSizeM, d.OriginWorld.Y + cz * d.CellSizeM));
                    wid.Add(Mathf.Clamp(MathF.Sqrt(d.Accum[ci]) * wp.ChannelWidthScale, 2f, 120f));
                    int bx = cx, bz = cz; float bh = d.Height[ci];
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                            float h = d.Height[d.Idx(nx, nz)];
                            if (h < bh) { bh = h; bx = nx; bz = nz; }
                        }
                    if (bx == cx && bz == cz) break;          // local minimum
                    // CONFLUENCE: if the next cell is already part of another river, stop HERE — append the
                    // confluence point so the lines join, then terminate. This is what kills the "parallel
                    // duplicate lines down a shared channel" artifact (each tributary used to re-trace the
                    // whole trunk alongside the first reach instead of merging into it).
                    if (visited[d.Idx(bx, bz)])
                    {
                        pts.Add(new Vector2(d.OriginWorld.X + bx * d.CellSizeM, d.OriginWorld.Y + bz * d.CellSizeM));
                        wid.Add(Mathf.Clamp(MathF.Sqrt(d.Accum[d.Idx(bx, bz)]) * wp.ChannelWidthScale, 2f, 120f));
                        break;
                    }
                    cx = bx; cz = bz;
                    if (cx <= 0 || cz <= 0 || cx >= res - 1 || cz >= res - 1) break; // left grid
                }
                // Drop tiny orphan blips: only keep reaches long enough to read as a real channel.
                if (pts.Count >= MinReachCells)
                {
                    var sm = Chaikin(pts.ToArray(), 3);   // 3 iters: smoother spline → smoother SDF band (was 2; killed grid stair-step)
                    var sw = ResampleWidth(wid.ToArray(), sm.Length);
                    reaches.Add(new RiverReach(sm, sw, 1));
                }
            }
        return reaches;
    }

    private static Vector2[] Chaikin(Vector2[] p, int iter)
    {
        for (int k = 0; k < iter; k++)
        {
            if (p.Length < 3) break;
            var outp = new List<Vector2> { p[0] };
            for (int i = 0; i < p.Length - 1; i++)
            {
                outp.Add(p[i] * 0.75f + p[i + 1] * 0.25f);
                outp.Add(p[i] * 0.25f + p[i + 1] * 0.75f);
            }
            outp.Add(p[^1]);
            p = outp.ToArray();
        }
        return p;
    }

    private static float[] ResampleWidth(float[] w, int n)
    {
        var outw = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (n <= 1) ? 0f : i / (float)(n - 1);
            float src = t * (w.Length - 1);
            int a = (int)MathF.Floor(src); int b = Math.Min(a + 1, w.Length - 1);
            outw[i] = Mathf.Lerp(w[a], w[b], src - a);
        }
        return outw;
    }
}
