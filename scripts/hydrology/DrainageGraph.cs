using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Deterministic coarse drainage network. Pure C# (no RD/rendering). f(CoarseField, params) -> river segments.
/// Pipeline: priority-flood depression fill -> steepest-descent routing -> upstream-area accumulation ->
/// Strahler ordering -> emit channel segments (world-space). Same inputs => byte-identical output (tile-coherent).
public sealed class DrainageGraph
{
    public readonly record struct Segment(float Ax, float Az, float Bx, float Bz, int Order, float Area);

    public readonly int Res;
    public readonly float[] Filled;   // depression-filled coarse heights (row-major z*Res+x)
    public readonly int[]   DownIdx;  // steepest-descent neighbor index, or -1 at an edge/outlet
    public readonly float[] Area;     // upstream drainage (cell count, includes self=1)
    public readonly int[]   Order;    // Strahler order where channel, else 0
    public readonly List<Segment> Segments = new();

    private readonly int _n;
    private int Idx(int x, int z) => z * Res + x;

    private DrainageGraph(int res)
    { Res = res; _n = res * res; Filled = new float[_n]; DownIdx = new int[_n]; Area = new float[_n]; Order = new int[_n]; }

    public static DrainageGraph Build(CoarseField cf, HydrologyParams hp)
    {
        var g = new DrainageGraph(cf.Res);
        g.FillDepressions(cf);
        g.RouteSteepestDescent();
        g.AccumulateArea();
        g.StrahlerOrder(hp);
        g.EmitSegments(cf, hp);
        return g;
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
            foreach (var (nx, nz) in Neigh4(cx, cz))
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

    private IEnumerable<(int x, int z)> Neigh4(int x, int z)
    { if (x > 0) yield return (x - 1, z); if (x < Res - 1) yield return (x + 1, z);
      if (z > 0) yield return (x, z - 1); if (z < Res - 1) yield return (x, z + 1); }

    // steepest-descent on the FILLED surface (no pits remain, so every non-edge cell has a downhill neighbor).
    private void RouteSteepestDescent()
    {
        for (int z = 0; z < Res; z++) for (int x = 0; x < Res; x++)
        {
            int i = Idx(x, z); float hc = Filled[i]; int best = -1; float bestDrop = 0f;
            foreach (var (nx, nz) in Neigh4(x, z))
            { int ni = Idx(nx, nz); float drop = hc - Filled[ni]; if (drop > bestDrop) { bestDrop = drop; best = ni; } }
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
            Segments.Add(new Segment(wax, waz, wbx, wbz, Order[i], Area[i]));
        }
    }
}
