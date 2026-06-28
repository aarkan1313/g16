using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Carves river channels below the water (Filled) surface. The `reference` MUST be the
// priority-flood Filled surface, which is already monotonic + continuous along every
// flow path — so bed = reference - depth is automatically a continuous downhill
// channel. No "cut through bumps" monotonic clamp is needed (it only over-deepens and
// gouges tunnels where rivers cross saddles).
//
// Width scales STRONGLY with flow (trunk rivers wide, tributaries thin streams); depth
// stays modest so big rivers are wide-and-shallow, not deep narrow slots. Bounded to
// river paths; only deepens; src is unchanged.
public static class ChannelCarve
{
    // lakeId (optional): cells with lakeId >= 0 are never carved, so the brush can't
    // gouge a trench into a lakebed near a river inlet (the "disconnected channel
    // under the lake" artifact). The river still meets the lake at the shoreline.
    public static HeightField Apply(HeightField src, float[] reference, RiverNetwork.RiverEdge[] edges,
                                    float channelDepth, float widthScale, int[]? lakeId = null)
    {
        var f = src.Clone();
        int w = f.Width, h = f.Height;
        float maxFlow = 1f;
        foreach (var e in edges) maxFlow = MathF.Max(maxFlow, e.Flow);

        foreach (var e in edges)
        {
            float fn = MathF.Sqrt(e.Flow / maxFlow);                       // 0..1
            float depth = channelDepth * (0.4f + 0.6f * fn);              // modest, flow-graded
            int radius = Math.Max(1, (int)MathF.Round(widthScale * (0.6f + 3.4f * fn))); // strong width range

            foreach (int c in e.Cells)
            {
                int cx = c % w, cy = c / w;
                float bed = reference[c] - depth;                          // Filled already monotonic → continuous channel
                for (int jy = -radius; jy <= radius; jy++)
                for (int jx = -radius; jx <= radius; jx++)
                {
                    int nx = cx + jx, ny = cy + jy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    float dist = MathF.Sqrt(jx * jx + jy * jy);
                    if (dist > radius) continue;
                    float t = dist / radius;                               // 0 center .. 1 rim
                    int i = ny * w + nx;
                    if (lakeId != null && lakeId[i] >= 0) continue;        // never carve into a lakebed
                    float target = bed + (f.Data[i] - bed) * (t * t);     // parabolic banks toward existing
                    if (target < f.Data[i]) f.Data[i] = target;           // only deepen
                }
            }
        }
        return f;
    }
}
