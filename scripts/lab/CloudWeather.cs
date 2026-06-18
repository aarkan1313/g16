using Godot;

namespace WG16.Lab;

/// Generates the 2D cloud WEATHER field (coverage + cloud-type), tileable, on the
/// CPU. One job: produce the weather map — knows nothing about raymarching or the
/// sky. It is small (256²) and low-frequency, so a CPU FBM is plenty (no GPU/RD,
/// and therefore no headless-RD gotcha). Channels:
///   R = coverage   (where clouds are, 0..1; the raymarch remaps density by this)
///   G = cloud type (0 = flat stratus  → 1 = towering cumulus)
/// Re-derivable from a seed (in-repo, not shipped).
public static class CloudWeather
{
    public const int Res = 256;

    /// Mean of the coverage (R) channel of a raw weather buffer — used as a CPU
    /// proxy for "how much of the sky the weather field clouds" (drives overcast
    /// dimming + aerial tint without a GPU readback).
    public static float Mean(byte[] raw)
    {
        if (raw.Length == 0) { return 0.5f; }
        int cells = raw.Length / (4 * sizeof(float));
        double sum = 0;
        for (int i = 0; i < cells; i++) { sum += System.BitConverter.ToSingle(raw, i * 16); }
        return (float)(sum / cells);
    }

    /// Raw RGBAF bytes (R=coverage bias, G=cloud type, B=density bias). Large-scale,
    /// LOW-frequency weather: the macro distribution of clouds (cloudy here, clear
    /// there) reads as weather systems. Sampled at a very large world scale in the
    /// raymarch (WEATHER_SCALE ~1/80km) so it never visibly repeats in view. Coverage
    /// spans the FULL 0..1 range (centered, light contrast) so the raymarch's coverage
    /// knob can reach TRUE CLEAR at one end and overcast at the other — the old
    /// Smoothstep(0.35,0.85) biased it upward into a permanent wash (the "can't get
    /// clouds to go" + "every preset full" complaint).
    public static byte[] BakeRaw(float seed = 5.0f)
    {
        var bytes = new byte[Res * Res * 4 * sizeof(float)];
        int o = 0;
        for (int y = 0; y < Res; y++)
        for (int x = 0; x < Res; x++)
        {
            float u = (x + 0.5f) / Res;
            float w = (y + 0.5f) / Res;
            // MACRO VARIETY (audit fix #2): multi-octave HIGH-CONTRAST coverage + a much
            // larger-scale "cloud system" mask, so there are genuinely cloudy regions AND
            // clear lanes (fronts) — not a statistically-uniform field that reads as
            // "sampled noise". The old single-low-freq ±0.35 wobble made every patch of
            // sky identical (THE root cause of the "uniform/procedural" look).
            float coverage = Fbm(u, w, 3f, seed);            // 4-octave detail
            float system   = Fbm(u, w, 1f, seed + 91.3f);    // very large scale: where weather IS
            float type     = Fbm(u, w, 2f, seed + 31.7f);
            float density  = Fbm(u, w, 3f, seed + 53.1f);
            // contrast-shape coverage (open real clear lanes) then gate by the system mask:
            // a region the system mask says is "clear" stays clear regardless of detail.
            coverage = Mathf.Clamp((coverage - 0.3f) / 0.5f, 0f, 1f);   // expand mid → 0..1 contrast
            coverage = coverage * coverage * (3f - 2f * coverage);      // smoothstep shaping
            float sysMask = Mathf.Clamp((system - 0.35f) / 0.35f, 0f, 1f);
            coverage *= sysMask;                              // clear lanes where no system
            WriteFloat(bytes, ref o, coverage);            // R coverage bias (shaped, gated)
            WriteFloat(bytes, ref o, type);                // G cloud type (region → kind, not just denser)
            WriteFloat(bytes, ref o, density);             // B density bias
            WriteFloat(bytes, ref o, 1f);                  // A
        }
        return bytes;
    }


    public static ImageTexture Bake(float seed = 5.0f)
    {
        Image img = Image.CreateFromData(Res, Res, false, Image.Format.Rgbaf, BakeRaw(seed));
        return ImageTexture.CreateFromImage(img);
    }

    // --- tileable 2D value-noise FBM (period = freq*2^octave, wraps seamlessly) ---
    private static float Fbm(float u, float v, float baseFreq, float seed)
    {
        float amp = 0.5f, sum = 0f, norm = 0f, freq = baseFreq;
        for (int i = 0; i < 4; i++)
        {
            sum += amp * VNoiseTiled(u, v, freq, seed + i * 19.3f);
            norm += amp; amp *= 0.55f; freq *= 2f;
        }
        return sum / Mathf.Max(norm, 1e-5f);
    }

    private static float VNoiseTiled(float u, float v, float freq, float seed)
    {
        float x = u * freq, y = v * freq;
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float fx = x - ix, fy = y - iy;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        int f = Mathf.RoundToInt(freq);
        float a = Hash(Wrap(ix, f), Wrap(iy, f), seed);
        float b = Hash(Wrap(ix + 1, f), Wrap(iy, f), seed);
        float c = Hash(Wrap(ix, f), Wrap(iy + 1, f), seed);
        float d = Hash(Wrap(ix + 1, f), Wrap(iy + 1, f), seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static int Wrap(int a, int n) => n > 0 ? ((a % n) + n) % n : a;

    private static float Hash(int x, int y, float seed)
    {
        float h = Mathf.Sin(x * 127.1f + y * 311.7f + seed) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    private static void WriteFloat(byte[] dst, ref int o, float v)
    {
        System.BitConverter.GetBytes(v).CopyTo(dst, o); o += 4;
    }
}
