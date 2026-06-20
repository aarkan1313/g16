using Godot;
using System;

namespace WG16.Lab;

/// AAA anti-tiling (Deliot–Heitz). Builds, per albedo channel, a forward transform T
/// (value → Gaussian) and its inverse T⁻¹ (Gaussian → value) as 256-entry LUTs, so the
/// fragment shader can blend 3 stochastic tiles in Gaussian space and map back with the
/// original histogram preserved (no contrast loss, no seam). A histogram is a single linear
/// reduction → pure C# (no GPU compute, runs headless), one-time per bound material.
public static class HistogramCompute
{
    public const int Bins = 256;
    public const int RowsPerZone = 6;        // fwd R,G,B then inv R,G,B
    private const float GaussMean = 0.5f;
    private const float GaussStd  = 1.0f / 6.0f;   // ±3σ spans [0,1]

    /// Returns float[Bins*RowsPerZone]: rows 0..2 = forward (val→gauss) R/G/B,
    /// rows 3..5 = inverse (gauss→val) R/G/B. All in [0,1].
    public static float[] ComputeLuts(Image albedo)
    {
        Image img = (Image)albedo.Duplicate();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        // Stride-sample to ~512² for a fast, representative histogram.
        int target = 512;
        if (img.GetWidth() > target || img.GetHeight() > target) img.Resize(target, target, Image.Interpolation.Bilinear);
        byte[] px = img.GetData();
        int n = img.GetWidth() * img.GetHeight();

        var luts = new float[Bins * RowsPerZone];
        for (int c = 0; c < 3; c++)
        {
            // 1) histogram
            var hist = new int[Bins];
            for (int i = 0; i < n; i++) hist[px[i * 4 + c]]++;
            // 2) CDF (midpoint convention to avoid 0/1 saturation in the probit)
            var cdf = new float[Bins];
            int acc = 0;
            for (int b = 0; b < Bins; b++) { acc += hist[b]; cdf[b] = (acc - 0.5f * hist[b]) / Math.Max(n, 1); }
            // 3) forward LUT: value bin b → gaussian quantile of cdf[b]
            int fwdRow = c, invRow = 3 + c;
            for (int b = 0; b < Bins; b++)
                luts[fwdRow * Bins + b] = Mathf.Clamp(GaussMean + GaussStd * Probit(cdf[b]), 0f, 1f);
            // 4) inverse LUT: gaussian bin g (→ value v=g/255 in gaussian space) → value whose
            //    cdf matches Φ(gaussian). Search cdf for the matching uniform u = NormalCdf(gz).
            for (int g = 0; g < Bins; g++)
            {
                float gz = ((g / (float)(Bins - 1)) - GaussMean) / GaussStd;   // back to z
                float u  = NormalCdf(gz);                                       // target uniform
                luts[invRow * Bins + g] = InvertCdf(cdf, u);
            }
        }
        return luts;
    }

    // value v in [0,1] such that cdf(v) ≈ u, by linear search+lerp over the 256-bin cdf.
    private static float InvertCdf(float[] cdf, float u)
    {
        if (u <= cdf[0]) return 0f;
        for (int b = 1; b < Bins; b++)
            if (u <= cdf[b])
            {
                float t = (u - cdf[b - 1]) / Math.Max(cdf[b] - cdf[b - 1], 1e-6f);
                return Mathf.Clamp((b - 1 + t) / (Bins - 1), 0f, 1f);
            }
        return 1f;
    }

    // Acklam's inverse-normal-CDF (probit) approximation. Input p in (0,1) → z.
    private static float Probit(float p)
    {
        p = Mathf.Clamp(p, 1e-6f, 1f - 1e-6f);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] cc= { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        double plow = 0.02425, phigh = 1 - 0.02425, q, r, z;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); z = (((((cc[0]*q+cc[1])*q+cc[2])*q+cc[3])*q+cc[4])*q+cc[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        else if (p <= phigh) { q = p - 0.5; r = q*q; z = (((((a[0]*r+a[1])*r+a[2])*r+a[3])*r+a[4])*r+a[5])*q / (((((b[0]*r+b[1])*r+b[2])*r+b[3])*r+b[4])*r+1); }
        else { q = Math.Sqrt(-2 * Math.Log(1 - p)); z = -(((((cc[0]*q+cc[1])*q+cc[2])*q+cc[3])*q+cc[4])*q+cc[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        return (float)z;
    }

    // standard normal CDF via erf approximation (Abramowitz & Stegun 7.1.26).
    private static float NormalCdf(float z)
    {
        float sign = z < 0 ? -1f : 1f; z = Math.Abs(z) / 1.41421356f;
        float t = 1f / (1f + 0.3275911f * z);
        float y = 1f - (((((1.061405429f*t - 1.453152027f)*t) + 1.421413741f)*t - 0.284496736f)*t + 0.254829592f)*t*(float)Math.Exp(-z*z);
        return 0.5f * (1f + sign * y);
    }

    // Round-trip error measured over ACTUAL pixels (what the shader feeds through the LUTs) — the
    // only meaningful test. Bins with no pixels are intentionally undefined and excluded. Mirrors the
    // shader's linear-filtered LUT fetch (filter_linear).
    public static (float maxErr, float meanErr) RoundTripError(float[] luts, Image albedo)
    {
        Image img = (Image)albedo.Duplicate();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        int target = 256;
        if (img.GetWidth() > target || img.GetHeight() > target) img.Resize(target, target, Image.Interpolation.Bilinear);
        byte[] px = img.GetData();
        int n = img.GetWidth() * img.GetHeight();
        float maxe = 0f, sum = 0f; int cnt = 0;
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++)
            {
                float v = px[i * 4 + c] / 255f;
                float g = SampleLut(luts, c, v);          // forward row c
                float v2 = SampleLut(luts, 3 + c, g);     // inverse row 3+c
                float e = MathF.Abs(v2 - v); maxe = MathF.Max(maxe, e); sum += e; cnt++;
            }
        return (maxe, sum / Math.Max(cnt, 1));
    }

    // Linear-interpolated LUT fetch (matches the shader's filter_linear sampler).
    private static float SampleLut(float[] luts, int row, float x)
    {
        x = Mathf.Clamp(x, 0f, 1f) * (Bins - 1);
        int i0 = (int)MathF.Floor(x); int i1 = Math.Min(i0 + 1, Bins - 1); float t = x - i0;
        return luts[row * Bins + i0] * (1f - t) + luts[row * Bins + i1] * t;
    }
}
