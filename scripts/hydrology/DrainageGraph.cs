using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Deterministic coarse drainage network. Pure C# (no RD/rendering). f(CoarseField, params) -> river segments.
/// Pipeline: priority-flood depression fill -> steepest-descent routing -> upstream-area accumulation ->
/// Strahler ordering -> emit channel segments (world-space). Same inputs => byte-identical output (tile-coherent).
public sealed class DrainageGraph
{
    // Az/Bz are WORLD-Z (north), not elevation. BedA/BedB = river-bed ELEVATION at each endpoint (descends
    // downstream from A to B). The carve shader lowers terrain TOWARD this bed (not a fixed depth subtraction).
    public readonly record struct Segment(float Ax, float Az, float Bx, float Bz, int Order, float Area,
                                          float BedA, float BedB);

    public readonly int Res;
    public readonly float[] Filled;   // depression-filled coarse heights (row-major z*Res+x)
    public readonly int[]   DownIdx;  // steepest-descent neighbor index, or -1 at an edge/outlet
    public readonly float[] Area;     // upstream drainage (cell count, includes self=1)
    public readonly int[]   Order;    // Strahler order where channel, else 0
    public readonly float[] Bed;      // river-bed elevation per cell (monotonic downstream; from Filled)
    public readonly int[]   FloodRecv;// receiver from the priority-flood (the cell each was discovered from);
                                      // guarantees a drain path on flats where steepest-descent would dead-end
    public readonly float[] LakeDepth;// Filled - originalH: how deep the fill submerged this cell (>0 = under water)
    public float CoarseOriginX, CoarseOriginZ, CoarseSpacing;   // world transform of the coarse grid (for sampling)
    public readonly List<Segment> Segments = new();

    private readonly int _n;
    private int Idx(int x, int z) => z * Res + x;

    private DrainageGraph(int res)
    { Res = res; _n = res * res; Filled = new float[_n]; DownIdx = new int[_n]; Area = new float[_n]; Order = new int[_n]; Bed = new float[_n]; FloodRecv = new int[_n]; LakeDepth = new float[_n]; }

    public static DrainageGraph Build(CoarseField cf, HydrologyParams hp)
    {
        var g = new DrainageGraph(cf.Res);
        g.CoarseOriginX = cf.OriginX; g.CoarseOriginZ = cf.OriginZ; g.CoarseSpacing = cf.Spacing;
        g.FillDepressions(cf);
        g.RouteSteepestDescent();
        g.AccumulateArea(hp);
        g.StrahlerOrder(hp);
        g.ComputeBed(cf, hp);
        g.EmitSegments(cf, hp);
        return g;
    }

    // Per-cell river-bed elevation: the Priority-Flood Filled surface is ALREADY monotonic downstream (every
    // cell drains to the edge), so it's the natural bed. We additionally (a) incise channels slightly below
    // Filled by a discharge-scaled amount so the channel sits in its valley, and (b) enforce STRICT descent
    // along DownIdx (Filled has flat spill-regions with zero slope; nudge each cell just above its receiver) so
    // the carved river floor always flows. Processed downstream-first (low Filled last) via an Area-independent
    // height sort. This is the research's "carve toward a monotonic bed elevation", reusing Filled.
    private void ComputeBed(CoarseField cf, HydrologyParams hp)
    {
        for (int i = 0; i < _n; i++) { Bed[i] = Filled[i]; }
        // process receivers before senders: sort by Filled ascending (outlet/low first), then a cell's receiver
        // (always <= it) is finalized first; we clamp Bed[i] >= Bed[receiver] + epsilon for strict flow.
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Filled[a] != Filled[b] ? Filled[a].CompareTo(Filled[b]) : a.CompareTo(b));
        float eps = hp.CoarseSpacing * 1e-3f;   // tiny per-cell drop so flat fills still descend
        foreach (int i in order)
        {
            int d = DownIdx[i];
            if (d < 0) { continue; }                       // edge outlet: keep Filled
            float floorByFlow = Bed[d] + eps;              // must stay above receiver
            Bed[i] = Math.Max(Bed[i], floorByFlow);        // raise to guarantee descent (never below receiver)
        }
    }

    // Priority-flood + flow-direction (Barnes 2014, combined variant): flood inward from the edge, raising each
    // cell to at least its lowest already-processed neighbor, AND record the receiver (the cell each was
    // discovered FROM) — giving every cell a guaranteed drain path to the edge even across filled flats, where
    // pure steepest-descent would dead-end. Also record LakeDepth = Filled - originalH (how far the fill
    // submerged the cell). Deterministic: priority-queue ties break by cell index.
    private void FillDepressions(CoarseField cf)
    {
        for (int i = 0; i < _n; i++) { FloodRecv[i] = -1; }
        var closed = new bool[_n];
        // min-heap of (height, index); tie-break by index for determinism
        var open = new SortedSet<(float h, int i)>(Comparer<(float h, int i)>.Create((a, b) =>
            a.h != b.h ? a.h.CompareTo(b.h) : a.i.CompareTo(b.i)));
        for (int x = 0; x < Res; x++) { Push(open, closed, cf, Idx(x, 0)); Push(open, closed, cf, Idx(x, Res - 1)); }
        for (int z = 0; z < Res; z++) { Push(open, closed, cf, Idx(0, z)); Push(open, closed, cf, Idx(Res - 1, z)); }
        while (open.Count > 0)
        {
            var (h, i) = open.Min; open.Remove(open.Min);
            Filled[i] = h;
            LakeDepth[i] = h - cf.H(i % Res, i / Res);   // >0 => fill raised water above original ground = lake
            int cx = i % Res, cz = i / Res;
            foreach (var (nx, nz, _) in Neigh8(cx, cz))
            {
                int ni = Idx(nx, nz);
                if (closed[ni]) { continue; }
                closed[ni] = true;
                FloodRecv[ni] = i;                       // discovered from i => drains toward i (toward the edge)
                float nh = Math.Max(cf.H(nx, nz), h);    // raise into a depression to the spill level
                open.Add((nh, ni));
            }
        }
    }

    private void Push(SortedSet<(float, int)> open, bool[] closed, CoarseField cf, int i)
    { if (!closed[i]) { closed[i] = true; int x = i % Res, z = i / Res; open.Add((cf.H(x, z), i)); } }

    // D8 neighborhood (8-connected). dist = step length in cells (1 for cardinals, sqrt2 for diagonals) so
    // routing can pick the steepest SLOPE (drop/dist), not raw drop — D8 breaks the D4 grid-axis staircase that
    // made rivers zigzag as L-shaped facets.
    private static readonly int[] _dx = { -1, 1, 0, 0, -1, 1, -1, 1 };
    private static readonly int[] _dz = { 0, 0, -1, 1, -1, -1, 1, 1 };
    private static readonly float[] _dlen = { 1f, 1f, 1f, 1f, 1.41421356f, 1.41421356f, 1.41421356f, 1.41421356f };

    private IEnumerable<(int x, int z, float dist)> Neigh8(int x, int z)
    {
        for (int k = 0; k < 8; k++)
        {
            int nx = x + _dx[k], nz = z + _dz[k];
            if (nx >= 0 && nx < Res && nz >= 0 && nz < Res) { yield return (nx, nz, _dlen[k]); }
        }
    }

    // D8 steepest-descent (by SLOPE) on the FILLED surface, FALLING BACK to the flood receiver on flats. On a
    // priority-flood plateau (filled depression spill-surface) no neighbor is strictly lower, so steepest-
    // descent alone would dead-end mid-domain; the flood receiver (the cell this was discovered from) always
    // points toward the edge, guaranteeing a complete drain path. Border cells with no flood receiver are true
    // outlets (-1).
    private void RouteSteepestDescent()
    {
        for (int z = 0; z < Res; z++) for (int x = 0; x < Res; x++)
        {
            int i = Idx(x, z); float hc = Filled[i]; int best = -1; float bestSlope = 0f;
            foreach (var (nx, nz, dist) in Neigh8(x, z))
            { int ni = Idx(nx, nz); float slope = (hc - Filled[ni]) / dist; if (slope > bestSlope) { bestSlope = slope; best = ni; } }
            // no strictly-lower neighbor (flat) → use the flood receiver so flow still reaches the edge.
            DownIdx[i] = best >= 0 ? best : FloodRecv[i];
        }
    }

    // upstream area via MULTIPLE-FLOW-DIRECTION (MFD): distribute each cell's area to ALL lower D8 neighbors,
    // weighted by slope^p (p≈MfdExp). Pure D8 single-receiver bakes the strongest grid-orientation bias of any
    // router (flow locked to 45° multiples, zero dispersion) → grid-faceted accumulation → faceted channel
    // initiation. MFD is nearly orientation-invariant, so drainage area (which drives discharge/width) is smooth
    // and the network de-facets. Processed high->low so a cell's full area is known before it disperses it.
    // (DownIdx stays single-receiver for topology/bed/segments; only ACCUMULATION is multi-flow.)
    private void AccumulateArea(HydrologyParams hp)
    {
        for (int i = 0; i < _n; i++) { Area[i] = 1f; }
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Filled[b] != Filled[a] ? Filled[b].CompareTo(Filled[a]) : a.CompareTo(b)); // high->low
        float p = hp.MfdExp;
        var w = new float[8];
        foreach (int i in order)
        {
            int cx = i % Res, cz = i / Res; float hc = Filled[i];
            float tot = 0f; int k = 0;
            foreach (var (nx, nz, dist) in Neigh8(cx, cz))
            {
                float slope = (hc - Filled[Idx(nx, nz)]) / dist;
                float wk = slope > 0f ? MathF.Pow(slope, p) : 0f;
                w[k++] = wk; tot += wk;
            }
            if (tot <= 1e-12f) { continue; }   // pit/flat: area stays (lake) — DownIdx fallback still routes topology
            float ai = Area[i]; k = 0;
            foreach (var (nx, nz, _) in Neigh8(cx, cz))
            { float frac = w[k++] / tot; if (frac > 0f) { Area[Idx(nx, nz)] += ai * frac; } }
        }
    }

    // Strahler: a cell is a channel if Area >= ChannelMinArea. Order rises where two equal-order channels meet.
    // Process upstream-first (small area first) so a cell's contributors already have their order. Deterministic.
    private void StrahlerOrder(HydrologyParams hp)
    {
        var contributors = new List<int>[_n];
        for (int i = 0; i < _n; i++)
        { int d = DownIdx[i]; if (d >= 0 && Area[i] >= hp.ChannelMinArea) { (contributors[d] ??= new List<int>()).Add(i); } }
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Area[a] != Area[b] ? Area[a].CompareTo(Area[b]) : a.CompareTo(b)); // small area first = upstream first
        foreach (int i in order)
        {
            if (Area[i] < hp.ChannelMinArea) { Order[i] = 0; continue; }
            var up = contributors[i];
            if (up == null || up.Count == 0) { Order[i] = 1; continue; }
            int maxo = 0, countMax = 0;
            foreach (int u in up) { if (Order[u] > maxo) { maxo = Order[u]; countMax = 1; } else if (Order[u] == maxo) { countMax++; } }
            Order[i] = countMax >= 2 ? maxo + 1 : maxo;   // two-or-more equal max => order increments
        }
    }

    // Emit SMOOTH river reaches, not raw grid hops. Raw cell->receiver segments are quantized to the 8 D8
    // directions → valleys feather/herringbone. Instead we partition the channel network into reaches (maximal
    // chains between a path-start and the next confluence/outlet), then Chaikin corner-cut each reach into a
    // smooth polyline (positions + bed elevations together) and emit the smoothed sub-segments. The carve loop
    // is unchanged; it just sees smooth curves. Confluences stay connected because a downstream reach starts at
    // the confluence cell where its tributaries ended.
    private void EmitSegments(CoarseField cf, HydrologyParams hp)
    {
        // count channel contributors per cell to find confluences + path starts.
        var inDeg = new int[_n];
        for (int i = 0; i < _n; i++)
        { int d = DownIdx[i]; if (d >= 0 && Order[i] >= 1 && Order[d] >= 1) { inDeg[d]++; } }

        for (int i = 0; i < _n; i++)
        {
            if (Order[i] < 1 || DownIdx[i] < 0 || Order[DownIdx[i]] < 1) { continue; }
            // a reach STARTS at a headwater (no channel contributor) or just below a confluence (the cell whose
            // receiver has >1 contributor is an END, so its receiver is a START). Simplest partition: start a
            // reach at any channel cell whose contributor count != 1 (headwater=0, confluence>=2). Single-in
            // cells are mid-reach and are swallowed by the walk from their start.
            if (inDeg[i] == 1) { continue; }

            // walk downstream collecting the reach until the next confluence (inDeg>1) or outlet/non-channel.
            var pts = new List<(float x, float z, float bed, int order, float area)>();
            int cur = i;
            int axc = cur % Res, azc = cur / Res; var (wx0, wz0) = cf.World(axc, azc);
            pts.Add((wx0, wz0, Bed[cur], Order[cur], Area[cur]));
            while (true)
            {
                int d = DownIdx[cur];
                if (d < 0 || Order[d] < 1) { break; }
                int dx = d % Res, dz = d / Res; var (wx, wz) = cf.World(dx, dz);
                pts.Add((wx, wz, Bed[d], Order[d], Area[d]));
                if (inDeg[d] > 1) { break; }   // reached a confluence: it starts the next reach
                cur = d;
            }
            if (pts.Count < 2) { continue; }

            EmitSmoothReach(pts);
        }
    }

    // Chaikin corner-cutting (2 iterations) on a reach polyline, then emit consecutive smoothed sub-segments.
    // Bed/order/area are carried per point and lerp'd by the same cutting weights so they stay consistent.
    private void EmitSmoothReach(List<(float x, float z, float bed, int order, float area)> pts)
    {
        for (int iter = 0; iter < 2; iter++)
        {
            if (pts.Count < 3) { break; }
            var np = new List<(float x, float z, float bed, int order, float area)>();
            np.Add(pts[0]);                                   // keep endpoints (preserve connectivity)
            for (int k = 0; k < pts.Count - 1; k++)
            {
                var p = pts[k]; var q = pts[k + 1];
                // Q point (1/4 from p) and R point (3/4 from p); carry bed/order/area by the same weights
                np.Add((Lerp(p.x, q.x, 0.25f), Lerp(p.z, q.z, 0.25f), Lerp(p.bed, q.bed, 0.25f),
                        p.order, Lerp(p.area, q.area, 0.25f)));
                np.Add((Lerp(p.x, q.x, 0.75f), Lerp(p.z, q.z, 0.75f), Lerp(p.bed, q.bed, 0.75f),
                        q.order, Lerp(p.area, q.area, 0.75f)));
            }
            np.Add(pts[pts.Count - 1]);
            pts = np;
        }
        for (int k = 0; k < pts.Count - 1; k++)
        {
            var a = pts[k]; var b = pts[k + 1];
            int order = Math.Max(a.order, b.order);
            float area = Math.Max(a.area, b.area);
            Segments.Add(new Segment(a.x, a.z, b.x, b.z, order, area, a.bed, b.bed));
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
