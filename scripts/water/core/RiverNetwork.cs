using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Connected river graph: trace flow downstream into continuous edges. Each edge is a
// continuous cell path (each cell is Down of the previous) that ends at a lake, an
// outlet (map edge), or a confluence with another river — never mid-slope. That
// guarantee is what makes rivers continuous and joined to lakes (vs the old threshold mask).
public static class RiverNetwork
{
    public enum EndKind { Lake, Outlet, Confluence }
    public sealed class RiverEdge { public required int[] Cells; public float Flow; public EndKind End; }

    public static RiverEdge[] Extract(WaterMap wm, LakeSet ls, float threshold)
    {
        int n = wm.Terrain.Width * wm.Terrain.Height;
        bool IsRiver(int i) => wm.Accum[i] > threshold && ls.LakeId[i] < 0;

        var inDeg = new int[n];
        for (int i = 0; i < n; i++)
            if (IsRiver(i) && wm.Down[i] >= 0 && IsRiver(wm.Down[i])) inDeg[wm.Down[i]]++;

        var onRiver = new bool[n];
        var edges = new List<RiverEdge>();

        for (int s = 0; s < n; s++)
        {
            if (!IsRiver(s) || inDeg[s] != 0) continue;     // start only at sources
            edges.Add(Trace(s, wm, ls, onRiver, IsRiver));
        }

        // lakes feed downstream: trace from each lake outlet if its exit qualifies
        foreach (var lake in ls.Lakes)
        {
            int d = wm.Down[lake.OutletCell];
            if (d < 0 || ls.LakeId[d] >= 0 || wm.Accum[d] <= threshold || onRiver[d]) continue;
            edges.Add(Trace(d, wm, ls, onRiver, IsRiver));
        }
        return edges.ToArray();
    }

    static RiverEdge Trace(int start, WaterMap wm, LakeSet ls, bool[] onRiver, System.Func<int, bool> IsRiver)
    {
        var path = new List<int>(); int c = start; EndKind end = EndKind.Outlet;
        while (true)
        {
            path.Add(c); onRiver[c] = true;
            int d = wm.Down[c];
            if (d < 0) { end = EndKind.Outlet; break; }                          // reached map edge
            if (ls.LakeId[d] >= 0) { path.Add(d); end = EndKind.Lake; break; }    // flowed into a lake
            if (onRiver[d]) { path.Add(d); end = EndKind.Confluence; break; }     // merged another river
            c = d;                                                               // else keep following flow (no break on accum dip)
        }
        return new RiverEdge { Cells = path.ToArray(), Flow = wm.Accum[path[^1]], End = end };
    }
}
