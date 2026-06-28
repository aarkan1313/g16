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
