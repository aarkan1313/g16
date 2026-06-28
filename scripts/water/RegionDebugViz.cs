using Godot;
using Erosion.Core;

namespace WG16.Water;

// Debug proof that a region solved: one-line stats + a top-down PNG (terrain greyscale,
// lakes navy, river-edge cells cyan, other wet blue).
public static class RegionDebugViz
{
    public static string Stats(WaterData wd)
    {
        int n = wd.Width * wd.Height, wet = 0, lake = 0;
        for (int i = 0; i < n; i++) { if (wd.Wet[i]) wet++; if (wd.Lakes.LakeId[i] >= 0) lake++; }
        return $"rivers={wd.Rivers.Length} lakes={wd.Lakes.Lakes.Length} " +
               $"wet={100f * wet / n:F1}% lakeCells={100f * lake / n:F1}% grid={wd.Width}x{wd.Height}";
    }

    // Vertical relief of a baked field — tells whether the source terrain is flat/bumpy
    // (many shallow basins → flood) vs has real large-scale slope (drains to edges).
    public static string Relief(HeightField hf)
    {
        int n = hf.Width * hf.Height; float lo = float.MaxValue, hi = float.MinValue; double sum = 0;
        for (int i = 0; i < n; i++) { float v = hf.Data[i]; if (v < lo) lo = v; if (v > hi) hi = v; sum += v; }
        return $"min={lo:F1} max={hi:F1} mean={sum / n:F1} range={hi - lo:F1}m";
    }

    // Pit-fill stats on a WaterMap: how much of the surface is a >minDepth basin, how deep,
    // and how many cells exceed the river accumulation threshold. Isolates the flooding cause.
    public static string HydroStats(WaterMap wm, float minDepth, float riverThreshold)
    {
        int n = wm.Terrain.Width * wm.Terrain.Height, lake = 0, river = 0;
        double depthSum = 0; float maxDepth = 0;
        for (int i = 0; i < n; i++)
        {
            float d = wm.Filled[i] - wm.Terrain.Data[i];
            if (d > minDepth) { lake++; depthSum += d; if (d > maxDepth) maxDepth = d; }
            if (wm.Accum[i] > riverThreshold) river++;
        }
        return $"lakeCells={100f * lake / n:F1}% meanDepth={(lake > 0 ? depthSum / lake : 0):F2}m " +
               $"maxDepth={maxDepth:F1}m riverCells={100f * river / n:F2}%";
    }

    public static void DumpPng(WaterData wd, string osPath)
    {
        int w = wd.Width, h = wd.Height;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < w * h; i++) { float v = wd.Carved.Data[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
        float inv = hi > lo ? 1f / (hi - lo) : 0f;

        var river = new bool[w * h];
        foreach (var e in wd.Rivers) foreach (int c in e.Cells) river[c] = true;

        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgb8);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            Color col;
            if (wd.Lakes.LakeId[i] >= 0) col = new Color(0.05f, 0.12f, 0.35f);      // lake navy
            else if (river[i]) col = new Color(0.2f, 0.75f, 0.9f);                   // river cyan
            else if (wd.Wet[i]) col = new Color(0.2f, 0.4f, 0.7f);                   // other wet blue
            else { float g = (wd.Carved.Data[i] - lo) * inv; col = new Color(g * 0.6f + 0.2f, g * 0.6f + 0.25f, g * 0.5f + 0.2f); }
            img.SetPixel(x, y, col);
        }
        img.SavePng(osPath);
    }
}
