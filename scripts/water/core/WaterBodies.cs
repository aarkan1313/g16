using System;
using System.Collections.Generic;
namespace Erosion.Core;

public sealed class Lake { public float Level; public int OutletCell; public int CellCount; }
public sealed class LakeSet { public required int[] LakeId; public required Lake[] Lakes; }

// Groups pit-filled cells (Filled > terrain) into connected lakes. Each lake has one
// consistent spill level + an outlet cell (where it overflows downstream).
public static class WaterBodies
{
    static readonly int[] dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static LakeSet Compute(WaterMap wm, float minDepth)
    {
        int w = wm.Terrain.Width, h = wm.Terrain.Height, n = w * h;
        var id = new int[n]; for (int i = 0; i < n; i++) id[i] = -1;
        bool IsWater(int i) => wm.Filled[i] - wm.Terrain.Data[i] > minDepth;

        var lakes = new List<Lake>();
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (id[s] != -1 || !IsWater(s)) continue;
            int lid = lakes.Count; var lake = new Lake { Level = wm.Filled[s] };
            id[s] = lid; stack.Push(s); int count = 0;
            float outletZ = float.MaxValue; int outlet = s;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); count++;
                int cx = c % w, cy = c / w;
                int d = wm.Down[c];
                if (d >= 0 && !IsWater(d) && wm.Filled[c] < outletZ) { outletZ = wm.Filled[c]; outlet = c; }
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + dx[k], ny = cy + dy[k];
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (id[ni] == -1 && IsWater(ni)) { id[ni] = lid; stack.Push(ni); }
                }
            }
            lake.CellCount = count; lake.OutletCell = outlet;
            lake.Level = wm.Filled[outlet]; // spill elevation = the water surface level
            lakes.Add(lake);
        }
        return new LakeSet { LakeId = id, Lakes = lakes.ToArray() };
    }

    // Drop lakes below minArea cells (noise puddles the breach pass leaves behind): their cells
    // revert to dry/un-flagged, surviving lakes are re-indexed. Mutates ls (LakeId + Lakes) and wet.
    public static void CullSmall(LakeSet ls, bool[] wet, int minArea)
    {
        if (minArea <= 0) return;
        var lakes = ls.Lakes;
        var keep = new bool[lakes.Length];
        var remap = new int[lakes.Length];
        var survivors = new List<Lake>();
        for (int i = 0; i < lakes.Length; i++)
        {
            if (lakes[i].CellCount >= minArea) { keep[i] = true; remap[i] = survivors.Count; survivors.Add(lakes[i]); }
            else remap[i] = -1;
        }
        if (survivors.Count == lakes.Length) return; // nothing culled
        var id = ls.LakeId;
        for (int c = 0; c < id.Length; c++)
        {
            int lid = id[c]; if (lid < 0) continue;
            if (keep[lid]) id[c] = remap[lid];
            else { id[c] = -1; wet[c] = false; }
        }
        ls.Lakes = survivors.ToArray();
    }
}
