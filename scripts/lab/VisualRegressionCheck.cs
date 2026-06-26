using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Mathematical visual sanity gate for the rendered viewport. This is not a look verdict;
/// it catches gross regressions that screenshots reveal but humans should not have to spot:
/// black/blank frames, blown exposure, flat/void terrain, missing texture/edge signal, or
/// near-monochrome output. Run with --visualcheck[=optional.png].
public static class VisualRegressionCheck
{
    private const double MinEdge95 = 0.025; // below this, terrain/surfacing has collapsed toward flat output
    private const double MaxEdge95 = 0.055; // above this, unresolved texture/LOD noise is passing as detail

    public static bool Run(Image img, out string report)
    {
        int w = img.GetWidth();
        int h = img.GetHeight();
        if (w < 320 || h < 180)
        {
            report = $"image too small {w}x{h}";
            return false;
        }

        int step = Math.Max(1, Math.Min(w, h) / 360);
        int samples = 0;
        int dark = 0;
        int bright = 0;
        double sum = 0.0;
        double sumSq = 0.0;
        double chromaSum = 0.0;
        double edgeSum = 0.0;
        int edgeN = 0;
        var edgeVals = new List<double>(65536);

        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                Color c = img.GetPixel(x, y);
                if (!Finite(c.R) || !Finite(c.G) || !Finite(c.B))
                {
                    report = $"non-finite pixel at {x},{y}";
                    return false;
                }

                double r = Clamp01(c.R);
                double g = Clamp01(c.G);
                double b = Clamp01(c.B);
                double l = Luma(r, g, b);
                sum += l;
                sumSq += l * l;
                samples++;
                if (l < 0.02) { dark++; }
                if (l > 0.92) { bright++; }
                chromaSum += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

                if (x + step < w)
                {
                    double e = Math.Abs(l - PixelLuma(img, x + step, y));
                    edgeSum += e;
                    edgeN++;
                    edgeVals.Add(e);
                }
                if (y + step < h)
                {
                    double e = Math.Abs(l - PixelLuma(img, x, y + step));
                    edgeSum += e;
                    edgeN++;
                    edgeVals.Add(e);
                }
            }
        }

        double mean = sum / Math.Max(1, samples);
        double variance = Math.Max(0.0, sumSq / Math.Max(1, samples) - mean * mean);
        double std = Math.Sqrt(variance);
        double darkPct = (double)dark / Math.Max(1, samples);
        double brightPct = (double)bright / Math.Max(1, samples);
        double chroma = chromaSum / Math.Max(1, samples);
        double edgeMean = edgeSum / Math.Max(1, edgeN);
        edgeVals.Sort();
        double edge95 = edgeVals.Count == 0 ? 0.0 : edgeVals[Math.Clamp((int)Math.Ceiling(edgeVals.Count * 0.95) - 1, 0, edgeVals.Count - 1)];

        var failures = new List<string>();
        if (mean < 0.30 || mean > 0.80) { failures.Add($"mean {mean:F3} outside 0.30..0.80"); }
        if (std < 0.08 || std > 0.35) { failures.Add($"std {std:F3} outside 0.08..0.35"); }
        if (darkPct > 0.15) { failures.Add($"dark {darkPct:P1} > 15%"); }
        if (brightPct > 0.08) { failures.Add($"bright {brightPct:P1} > 8%"); }
        if (edgeMean < 0.010) { failures.Add($"edgeMean {edgeMean:F4} < 0.010"); }
        if (edge95 < MinEdge95 || edge95 > MaxEdge95) { failures.Add($"edge95 {edge95:F4} outside {MinEdge95:F3}..{MaxEdge95:F3}"); }
        if (chroma < 0.030) { failures.Add($"chroma {chroma:F3} < 0.030"); }

        string metrics = $"size={w}x{h} step={step} samples={samples} mean={mean:F3} std={std:F3} dark={darkPct:P1} bright={brightPct:P1} edgeMean={edgeMean:F4} edge95={edge95:F4} chroma={chroma:F3}";
        report = failures.Count == 0 ? metrics : $"{metrics} failures=[{string.Join("; ", failures)}]";
        return failures.Count == 0;
    }

    private static double PixelLuma(Image img, int x, int y)
    {
        Color c = img.GetPixel(x, y);
        return Luma(Clamp01(c.R), Clamp01(c.G), Clamp01(c.B));
    }

    private static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    private static double Clamp01(float v) => v < 0f ? 0.0 : v > 1f ? 1.0 : v;
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
