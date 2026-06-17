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

    /// Raw RGBAF bytes (R=coverage, G=type). Used to upload to a main-RD texture.
    public static byte[] BakeRaw(float seed = 5.0f)
    {
        var bytes = new byte[Res * Res * 4 * sizeof(float)];
        int o = 0;
        for (int y = 0; y < Res; y++)
        for (int x = 0; x < Res; x++)
        {
            float u = (x + 0.5f) / Res;
            float w = (y + 0.5f) / Res;
            float coverage = Fbm(u, w, 3f, seed);          // big soft blobs
            float type = Fbm(u, w, 2f, seed + 31.7f);      // even larger regions
            // gentle contrast so coverage has clear gaps (sparse) rather than a wash.
            coverage = Smoothstep(0.35f, 0.85f, coverage);
            WriteFloat(bytes, ref o, coverage);            // R
            WriteFloat(bytes, ref o, type);                // G
            WriteFloat(bytes, ref o, 0f);                  // B (rain — unused for now)
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

    private static float Smoothstep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / Mathf.Max(e1 - e0, 1e-5f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static void WriteFloat(byte[] dst, ref int o, float v)
    {
        System.BitConverter.GetBytes(v).CopyTo(dst, o); o += 4;
    }
}
