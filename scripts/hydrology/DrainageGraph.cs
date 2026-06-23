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
    public readonly List<Segment> Segments = new();

    private readonly int _n;
    private int Idx(int x, int z) => z * Res + x;

    private DrainageGraph(int res)
    { Res = res; _n = res * res; Filled = new float[_n]; DownIdx = new int[_n]; Area = new float[_n]; Order = new int[_n]; Bed = new float[_n]; }

    public static DrainageGraph Build(CoarseField cf, HydrologyParams hp)
    {
        var g = new DrainageGraph(cf.Res);
        g.FillDepressions(cf);
        g.RouteSteepestDescent();
        g.AccumulateArea();
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

    // Priority-flood (Barnes 2014): flood inward from the edge, raising each cell to at least its lowest
    // already-processed neighbor. Guarantees every cell drains to the region edge. Deterministic: the priority
    // queue ties break by cell index, so identical inputs => identical fill.
    private void FillDepressions(CoarseField cf)
    {
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
            int cx = i % Res, cz = i / Res;
            foreach (var (nx, nz, _) in Neigh8(cx, cz))
            {
                int ni = Idx(nx, nz);
                if (closed[ni]) { continue; }
                closed[ni] = true;
                float nh = Math.Max(cf.H(nx, nz), h);   // raise into a depression to the spill level
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

    // steepest-descent (by SLOPE, D8) on the FILLED surface. No pits remain, so every non-edge cell has a
    // downhill neighbor; diagonals let rivers run at 45° instead of staircasing along the axes.
    private void RouteSteepestDescent()
    {
        for (int z = 0; z < Res; z++) for (int x = 0; x < Res; x++)
        {
            int i = Idx(x, z); float hc = Filled[i]; int best = -1; float bestSlope = 0f;
            foreach (var (nx, nz, dist) in Neigh8(x, z))
            { int ni = Idx(nx, nz); float slope = (hc - Filled[ni]) / dist; if (slope > bestSlope) { bestSlope = slope; best = ni; } }
            DownIdx[i] = best;   // -1 => edge outlet (no lower neighbor)
        }
    }

    // upstream area = self + sum of cells draining into this one. Process cells high->low so contributions
    // arrive before a cell forwards them downstream (a topo order via height sort; deterministic by index tie).
    private void AccumulateArea()
    {
        for (int i = 0; i < _n; i++) { Area[i] = 1f; }
        var order = new int[_n]; for (int i = 0; i < _n; i++) { order[i] = i; }
        Array.Sort(order, (a, b) => Filled[b] != Filled[a] ? Filled[b].CompareTo(Filled[a]) : a.CompareTo(b)); // high->low
        foreach (int i in order) { int d = DownIdx[i]; if (d >= 0) { Area[d] += Area[i]; } }
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

    private void EmitSegments(CoarseField cf, HydrologyParams hp)
    {
        for (int i = 0; i < _n; i++)
        {
            int d = DownIdx[i];
            if (d < 0 || Order[i] < 1) { continue; }
            int ax = i % Res, az = i / Res, bx = d % Res, bz = d / Res;
            var (wax, waz) = cf.World(ax, az); var (wbx, wbz) = cf.World(bx, bz);
            Segments.Add(new Segment(wax, waz, wbx, wbz, Order[i], Area[i], Bed[i], Bed[d]));
        }
    }
}
