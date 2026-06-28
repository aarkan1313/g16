using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Drainage substrate computed on the eroded terrain:
//   Filled = depression-filled surface (Barnes priority-flood) → lakes where Filled > terrain.
//   Accum  = flow accumulation (D8 on Filled) → rivers where Accum is large.
public sealed class WaterMap
{
    public required HeightField Terrain;
    public required float[] Filled; // depression-filled surface (meters)
    public required float[] Accum;  // upstream cell count draining through each cell
    public required int[] Down;     // steepest-descent neighbor index on Filled, or -1 at an outlet/edge
    public float LakeDepth(int i) => Filled[i] - Terrain.Data[i];
}

public static class Hydrology
{
    static readonly int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };
    const float Epsilon = 0.005f; // per-step fill increment (m) — breaks flats without visibly tilting lakes

    public static WaterMap Compute(HeightField t)
    {
        var filled = PriorityFlood(t);
        var accum = FlowAccumDInf(filled, t.Width, t.Height, t.CellSizeM);
        var down = SteepestDown(filled, t.Width, t.Height, t.CellSizeM);
        return new WaterMap { Terrain = t, Filled = filled, Accum = accum, Down = down };
    }

    const float BreachBedSlope = 0.02f; // per-cell descent (m) of a breach channel bed — keeps flow monotonic

    // Bounded breach-then-fill conditioning (Lindsay 2016 hybrid). WG16's field is un-drained
    // closed-basin mountain terrain; fill-only hydrology turns every basin into a deep lake.
    // This carves an outlet notch from each closed-basin pit through its lowest rim saddle so
    // the basin drains into a river. Basins too deep/long to breach within the bounds are left
    // untouched — PriorityFlood (downstream) fills them, so they become the genuine lakes.
    // Returns a NEW HeightField; the source field is not mutated. maxBreachDepth<=0 → no-op.
    public static HeightField Condition(HeightField t, float maxBreachDepth, int maxBreachLength)
    {
        int w = t.Width, h = t.Height, n = w * h;
        var z = new float[n]; Array.Copy(t.Data, z, n);
        var conditioned = new HeightField(w, h, t.CellSizeM);
        if (maxBreachDepth <= 0f) { Array.Copy(z, conditioned.Data, n); return conditioned; }

        // Priority-flood from the edges, recording backlink[c] = the cell c was discovered from.
        // Following backlink leads to a boundary with monotonically non-increasing spill level,
        // crossing the basin's lowest rim saddle — exactly the route an outlet breach must take.
        var done = new bool[n];
        var backlink = new int[n]; for (int i = 0; i < n; i++) backlink[i] = -1;
        var filled = new float[n];
        var pq = new PriorityQueue<int, float>();
        void Seed(int idx) { if (!done[idx]) { done[idx] = true; filled[idx] = z[idx]; pq.Enqueue(idx, filled[idx]); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
        while (pq.Count > 0)
        {
            int c = pq.Dequeue(); int cx = c % w, cy = c / w;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx; if (done[ni]) continue;
                done[ni] = true; backlink[ni] = c;
                filled[ni] = MathF.Max(z[ni], filled[c] + Epsilon);
                pq.Enqueue(ni, filled[ni]);
            }
        }

        // Pits = interior strict local minima on the raw surface (one per closed basin bottom).
        // Breach the lowest first so deeper basins carve the trunk and shallower ones drain into it.
        var pits = new List<int>();
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int c = y * w + x; float zc = z[c]; bool isPit = true;
            for (int k = 0; k < 8; k++) { if (z[(y + dy[k]) * w + (x + dx[k])] <= zc) { isPit = false; break; } }
            if (isPit) pits.Add(c);
        }
        pits.Sort((a, b) => z[a].CompareTo(z[b]));

        var path = new List<int>(maxBreachLength);
        var carveLevel = new List<float>(maxBreachLength);
        foreach (int pit in pits)
        {
            // Trace the spill route, simulating a descending channel bed, until we either reach
            // natural ground already below the bed (outlet found) or violate a bound (abort).
            path.Clear(); carveLevel.Clear();
            float carve = z[pit]; int cur = pit; bool ok = false;
            for (int step = 0; step < maxBreachLength; step++)
            {
                int nxt = backlink[cur];
                if (nxt < 0) { ok = true; break; }     // drained to the map boundary
                carve -= BreachBedSlope;
                if (z[nxt] <= carve) { ok = true; break; } // ground already lower than our bed → outlet
                if (z[nxt] - carve > maxBreachDepth) { ok = false; break; } // notch too deep → keep as lake
                path.Add(nxt); carveLevel.Add(carve);
                cur = nxt;
            }
            if (!ok) continue;
            for (int i = 0; i < path.Count; i++) z[path[i]] = carveLevel[i]; // commit the notch
        }

        Array.Copy(z, conditioned.Data, n);
        return conditioned;
    }

    // Steepest-descent downstream neighbor on the (depression-free) filled surface.
    // Following Down always reaches an edge — no loops — so river tracing terminates.
    static int[] SteepestDown(float[] filled, int w, int h, float cell)
    {
        int n = w * h; var down = new int[n];
        float diag = cell * 1.41421356f;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int c = y * w + x; float z = filled[c]; int best = -1; float bestSlope = 0f;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + dx[k], ny = y + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                float d = (dx[k] != 0 && dy[k] != 0) ? diag : cell;
                float slope = (z - filled[ni]) / d;
                if (slope > bestSlope) { bestSlope = slope; best = ni; }
            }
            down[c] = best;
        }
        return down;
    }

    // Barnes et al. priority-flood: fill closed depressions up to their lowest outlet.
    static float[] PriorityFlood(HeightField t)
    {
        int w = t.Width, h = t.Height, n = w * h;
        var filled = new float[n];
        var done = new bool[n];
        var pq = new PriorityQueue<int, float>();

        void Seed(int idx) { if (!done[idx]) { done[idx] = true; filled[idx] = t.Data[idx]; pq.Enqueue(idx, filled[idx]); } }
        for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }

        while (pq.Count > 0)
        {
            int c = pq.Dequeue();
            int cx = c % w, cy = c / w;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int ni = ny * w + nx;
                if (done[ni]) continue;
                done[ni] = true;
                // epsilon variant: raise to at least outlet level PLUS a tiny increment, so the
                // filled surface never has flats → every cell has a downhill neighbor → rivers
                // route across flats/basins to an outlet and never dead-end mid-terrain.
                filled[ni] = MathF.Max(t.Data[ni], filled[c] + Epsilon);
                pq.Enqueue(ni, filled[ni]);
            }
        }
        return filled;
    }

    // D-infinity flow accumulation (Tarboton 1997): flow follows the true gradient
    // angle over the 8 triangular facets and splits between the two bracketing
    // neighbors. De-grids rivers (no straight D8 streaks) and only forms channels
    // where flow genuinely converges. Processed high→low on the filled surface.
    static readonly int[] e1x = { 1, 0, 0, -1, -1, 0, 0, 1 };
    static readonly int[] e1y = { 0, -1, -1, 0, 0, 1, 1, 0 };
    static readonly int[] e2x = { 1, 1, -1, -1, -1, -1, 1, 1 };
    static readonly int[] e2y = { -1, -1, -1, -1, 1, 1, 1, 1 };

    static float[] FlowAccumDInf(float[] z, int w, int h, float cell)
    {
        int n = w * h;
        var accum = new float[n];
        for (int i = 0; i < n; i++) accum[i] = 1f;

        var order = new int[n];
        var keys = new float[n];
        for (int i = 0; i < n; i++) { order[i] = i; keys[i] = -z[i]; } // -z asc == z desc
        Array.Sort(keys, order); // primitive-key sort: ~5× faster than a delegate comparator

        const float PI4 = MathF.PI / 4f;
        float diag = cell * 1.41421356f;

        foreach (int c in order)
        {
            int cx = c % w, cy = c / w; float z0 = z[c];
            float best = 0f, br = 0f; int bf = -1;
            for (int f = 0; f < 8; f++)
            {
                int x1 = cx + e1x[f], y1 = cy + e1y[f], x2 = cx + e2x[f], y2 = cy + e2y[f];
                if (x1 < 0 || y1 < 0 || x1 >= w || y1 >= h) continue;
                if (x2 < 0 || y2 < 0 || x2 >= w || y2 >= h) continue;
                float ze1 = z[y1 * w + x1], ze2 = z[y2 * w + x2];
                float s1 = (z0 - ze1) / cell, s2 = (ze1 - ze2) / cell;
                float r = MathF.Atan2(s2, s1), s = MathF.Sqrt(s1 * s1 + s2 * s2);
                if (r < 0f) { r = 0f; s = s1; }
                else if (r > PI4) { r = PI4; s = (z0 - ze2) / diag; }
                if (s > best) { best = s; bf = f; br = r; }
            }
            if (bf < 0 || best <= 0f) continue;
            float pe2 = br / PI4, pe1 = 1f - pe2;
            accum[(cy + e1y[bf]) * w + (cx + e1x[bf])] += accum[c] * pe1;
            accum[(cy + e2y[bf]) * w + (cx + e2x[bf])] += accum[c] * pe2;
        }
        return accum;
    }
}
