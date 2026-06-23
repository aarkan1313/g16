using System;
using System.Collections.Generic;
using System.Numerics;

namespace WG16.Hydrology;

/// Turns the raw drainage graph into a DELIBERATELY LIMITED set of water bodies. Lakes: label depression basins
/// (connected cells with LakeDepth>0), filter by area+depth+inflow, then cap to LakeDensityPerKm2 by keeping the
/// most significant. Rivers: the graph's channel segments above RiverMinArea. Pure C#, deterministic/tile-coherent.
public sealed class WaterBodies
{
    public readonly record struct Lake(int BasinId, float SurfaceLevel, float MinX, float MinZ,
                                       float MaxX, float MaxZ, float AreaM2, float Significance);
    public readonly record struct RiverReach(List<Vector2> Pts, float Width, float Area);

    public IReadOnlyList<Lake> Lakes { get; }
    public IReadOnlyList<RiverReach> Rivers { get; }

    private WaterBodies(List<Lake> lakes, List<RiverReach> rivers) { Lakes = lakes; Rivers = rivers; }

    public static WaterBodies Build(DrainageGraph g, WaterParams wp)
    {
        int res = g.Res; float sp = g.CoarseSpacing; float cellM2 = sp * sp;

        // --- label basins: flood-connect wet cells (LakeDepth>0) over 4-neighbours; deterministic (index order). ---
        var label = new int[res * res]; for (int i = 0; i < label.Length; i++) { label[i] = -1; }
        var basinCells = new List<List<int>>();
        for (int i = 0; i < res * res; i++)
        {
            if (g.LakeDepth[i] <= 0f || label[i] != -1) { continue; }
            int id = basinCells.Count; var cells = new List<int>(); var stack = new Stack<int>(); stack.Push(i); label[i] = id;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); cells.Add(c); int x = c % res, z = c / res;
                void Try(int nx, int nz)
                { if (nx < 0 || nx >= res || nz < 0 || nz >= res) { return; } int n = nz * res + nx; if (g.LakeDepth[n] > 0f && label[n] == -1) { label[n] = id; stack.Push(n); } }
                Try(x - 1, z); Try(x + 1, z); Try(x, z - 1); Try(x, z + 1);
            }
            basinCells.Add(cells);
        }

        // --- score + filter each basin (area + depth + inflow) ---
        var qualified = new List<Lake>();
        for (int id = 0; id < basinCells.Count; id++)
        {
            var cells = basinCells[id];
            float areaM2 = cells.Count * cellM2;
            float surface = float.NegativeInfinity, maxDepth = 0f, maxInflow = 0f;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (int c in cells)
            {
                surface = MathF.Max(surface, g.Filled[c]);
                maxDepth = MathF.Max(maxDepth, g.LakeDepth[c]);
                maxInflow = MathF.Max(maxInflow, g.Area[c]);
                int x = c % res, z = c / res; float wx = g.CoarseOriginX + x * sp, wz = g.CoarseOriginZ + z * sp;
                minX = MathF.Min(minX, wx); maxX = MathF.Max(maxX, wx); minZ = MathF.Min(minZ, wz); maxZ = MathF.Max(maxZ, wz);
            }
            if (areaM2 < wp.LakeMinAreaM2 || maxDepth < wp.LakeMinDepthM || maxInflow < wp.LakeMinInflow) { continue; }
            float sig = areaM2 * maxDepth;   // significance ~ volume; bigger+deeper ranks higher
            qualified.Add(new Lake(id, surface, minX, minZ, maxX, maxZ, areaM2, sig));
        }

        // --- density cap: keep the most significant N where N = densityPerKm2 * regionAreaKm2 ---
        float regionKm2 = (res * sp) * (res * sp) / 1e6f;
        int cap = Math.Max(0, (int)MathF.Round(wp.LakeDensityPerKm2 * regionKm2));
        qualified.Sort((a, b) => b.Significance.CompareTo(a.Significance));
        var lakes = qualified.GetRange(0, Math.Min(cap, qualified.Count));

        // --- rivers: graph segments above RiverMinArea (thin by design; width grows mildly with order) ---
        var rivers = new List<RiverReach>();
        foreach (var s in g.Segments)
        {
            if (s.Area < wp.RiverMinArea) { continue; }
            float width = wp.ChannelWidthM * (0.7f + 0.3f * MathF.Min(s.Order, 5));
            rivers.Add(new RiverReach(new List<Vector2> { new(s.Ax, s.Az), new(s.Bx, s.Bz) }, width, s.Area));
        }
        return new WaterBodies(lakes, rivers);
    }
}
