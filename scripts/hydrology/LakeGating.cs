using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
namespace WG16.Hydrology;

/// One gated lake: world-XZ center, footprint radius, water level (world Y), area.
public record Lake(Vector2 Center, float Radius, float WaterLevel, float AreaM2);

/// The "limited lakes" filter. Each coarse local minimum is FLOOD-FILLED to its spill point to get a
/// TRUE basin depth + extent (single-cell rim-rise is a poor depth proxy on smooth macro terrain —
/// a real basin is shallow per-cell but deep overall). The basin then passes four AND-combined gates,
/// and a per-km² density cap keeps only the most significant. This makes lakes deliberate and limited
/// (the "too much water" failure of the trashed arc) rather than emergent.
public static class LakeGating
{
    // Priority-flood a basin from a local-minimum seed: grow the water level until it spills over the
    // lowest rim. Returns (spillLevel, filledCells). Bounded to maxCells so a non-basin can't run away.
    private static (float spill, List<int> cells) FloodBasin(CoarseDrainage d, int seed, int maxCells)
    {
        int res = d.Res;
        var inBasin = new HashSet<int> { seed };
        // frontier = candidate rim cells, ordered by height (lowest first = next to flood / spill)
        var frontier = new List<(float h, int idx)>();
        void AddNeighbors(int i)
        {
            int x = i % res, z = i / res;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                    int j = d.Idx(nx, nz);
                    if (!inBasin.Contains(j)) frontier.Add((d.Height[j], j));
                }
        }
        AddNeighbors(seed);
        float spill = d.Height[seed];
        while (inBasin.Count < maxCells && frontier.Count > 0)
        {
            // pop lowest rim cell
            int best = 0; for (int k = 1; k < frontier.Count; k++) if (frontier[k].h < frontier[best].h) best = k;
            var (h, idx) = frontier[best];
            frontier.RemoveAt(best);
            if (inBasin.Contains(idx)) continue;
            // if this rim cell is LOWER than every basin cell's confining level, water spills here → stop.
            // The spill level is the height at which the basin first connects to lower-outside terrain:
            // that's this rim cell's height (the lowest way out).
            spill = h;
            // Does flooding to this level let water escape? It escapes if this rim cell has a neighbor
            // OUTSIDE the basin that is lower than `h` (downhill exit). If so, stop — spill found.
            int rx = idx % res, rz = idx / res; bool escapes = false;
            for (int dz = -1; dz <= 1 && !escapes; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = rx + dx, nz = rz + dz;
                    if (nx < 0 || nz < 0 || nx >= res || nz >= res) continue;
                    int j = d.Idx(nx, nz);
                    if (!inBasin.Contains(j) && d.Height[j] < h) { escapes = true; break; }
                }
            if (escapes) break;
            // otherwise absorb this rim cell into the basin and keep growing
            inBasin.Add(idx);
            AddNeighbors(idx);
        }
        return (spill, inBasin.ToList());
    }

    public static List<Lake> GatedLakes(CoarseDrainage d, WaterTable t, WaterParams wp, float regionM)
    {
        var cand = new List<Lake>();
        int res = d.Res;
        float cell = d.CellSizeM;
        int maxCells = 64;                                   // basin flood cap (≈ 8x8 cells)
        var claimed = new bool[res * res];                   // a cell can belong to only one basin

        for (int z = 1; z < res - 1; z++)
            for (int x = 1; x < res - 1; x++)
            {
                int i = d.Idx(x, z);
                if (claimed[i]) continue;
                float hi = d.Height[i];
                bool isMin = true;
                for (int dz = -1; dz <= 1 && isMin; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        if (d.Height[d.Idx(x + dx, z + dz)] < hi) { isMin = false; break; }
                    }
                if (!isMin) continue;

                var (spill, cells) = FloodBasin(d, i, maxCells);
                foreach (int c in cells) claimed[c] = true;  // don't re-seed inside this basin
                float depth = spill - hi;                    // TRUE basin depth (spill − floor)
                float area = cells.Count * cell * cell;      // TRUE flooded footprint
                float radius = MathF.Sqrt(area / Mathf.Pi);  // equivalent-disc radius
                // centroid
                float cxw = 0, czw = 0;
                foreach (int c in cells) { cxw += (c % res); czw += (c / res); }
                cxw = d.OriginWorld.X + (cxw / cells.Count) * cell;
                czw = d.OriginWorld.Y + (czw / cells.Count) * cell;
                var center = new Vector2(cxw, czw);
                float inflow = d.Accum[i];
                float table = t.At(center.X, center.Y);
                int biome = t.BiomeClass(hi, table);
                // GATE 1 water-table, GATE 2 significance, GATE 4 biome (GATE 3 density cap below)
                bool g1 = table >= wp.LakeTableThreshold;
                bool g2 = area >= wp.LakeMinAreaM2 && depth >= wp.LakeMinDepthM && inflow >= wp.LakeMinInflow;
                bool g4 = biome == 1 || biome == 2;          // temperate / wetland only
                if (g1 && g2 && g4)
                    cand.Add(new Lake(center, radius, hi + depth, area)); // water level at spill
            }

        // GATE 3: density cap — keep the most significant (largest area) up to the cap.
        float km2 = (regionM / 1000f) * (regionM / 1000f);
        int maxLakes = (int)MathF.Floor(wp.LakeDensityPerKm2 * km2);
        return cand.OrderByDescending(l => l.AreaM2).Take(Math.Max(0, maxLakes)).ToList();
    }
}
