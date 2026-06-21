using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// Per-material placement rule (height/slope bands + the blend height-bias multiplier).
/// Data-driven (data/ground_materials.json); one per material slot. Biome = a different manifest.
public struct RuleData
{
    public float HMin, HMax, SlopeMin, SlopeMax, HeightAmp;
}

/// Global per-pixel placement tunables (promoted out of GLSL per the no-magic-numbers rule).
/// Pushed to ground.gdshader uniforms; the key ones are also Debug-tab sliders.
public struct PlacementParams
{
    public float BandSoftM, SlopeSoft, WarpM, WarpAmpM, PatchM, NoiseGain, HeightBias;
    public float RoughFloor, NrmStrength;   // surface knobs (Unit 3): min roughness, normal-map tilt strength
    public float BlendAa;                   // Unit-4: min height-blend AA band (fwidth widens it)
    public static PlacementParams Default => new PlacementParams
    {
        BandSoftM = 60f, SlopeSoft = 0.08f, WarpM = 120f, WarpAmpM = 80f,
        PatchM = 40f, NoiseGain = 0.8f, HeightBias = 1f,
        RoughFloor = 0.15f, NrmStrength = 0.7f, BlendAa = 0.02f,
    };
}

/// The packed result: four texture arrays (one per channel) + the rule table + tunables.
public sealed class GroundArrays
{
    public Texture2DArray Albedo = null!, Normal = null!, Orm = null!, Height = null!;
    public int Count;
    public RuleData[] Rules = Array.Empty<RuleData>();
    public string[] Names = Array.Empty<string>();
    public int TexRes = 1024;
    public float TexScaleM = 11f;
    public PlacementParams Placement = PlacementParams.Default;
}

/// Builds the four ground material texture arrays from data/ground_materials.json.
/// Pure builder: no scene/UI. Real height comes from the re-hosted Poisson normal->height bake
/// (HeightCompute, windowed only; headless falls back to a flat proxy so the self-check still runs).
/// Mirrors the local-RD readback pattern used by the rest of the lab.
public static class GroundMaterialArrays
{
    private const int HeightBakeIters = 64;   // matches TerrainLab.HeightIters (mesoscale relief)

    public static GroundArrays Build(string manifestPath)
    {
        var g = new GroundArrays();
        string abs = ProjectSettings.GlobalizePath(manifestPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        var root = doc.RootElement;
        g.TexRes = root.TryGetProperty("tex_res", out var tr) ? tr.GetInt32() : 1024;
        g.TexScaleM = root.TryGetProperty("tex_scale_m", out var ts) ? ts.GetSingle() : 11f;
        g.Placement = ParsePlacement(root);

        var mats = root.GetProperty("materials");
        g.Count = mats.GetArrayLength();

        var alb = new Godot.Collections.Array<Image>();
        var nrm = new Godot.Collections.Array<Image>();
        var orm = new Godot.Collections.Array<Image>();
        var hgt = new Godot.Collections.Array<Image>();
        var rules = new List<RuleData>();
        var names = new List<string>();

        // One HeightCompute reused across all materials. Constructing it needs a local RenderingDevice,
        // which is null under --headless -> ctor throws -> hc stays null -> the flat height proxy is used.
        HeightCompute? hc = null;
        try { hc = new HeightCompute(); }
        catch (Exception e) { GD.Print($"[ground-v2] no height bake (headless / no local RD): {e.Message} -> flat proxy"); }

        try
        {
            foreach (var m in mats.EnumerateArray())
            {
                string name = m.GetProperty("name").GetString() ?? "";
                names.Add(name);
                rules.Add(new RuleData
                {
                    HMin = F(m, "hmin", 0), HMax = F(m, "hmax", 3000),
                    SlopeMin = F(m, "slopemin", 0), SlopeMax = F(m, "slopemax", 1),
                    HeightAmp = F(m, "height_amp", 1f),
                });
                string dir = $"res://assets/materials/{name}/";
                alb.Add(LoadResized(dir + "albedo.png", g.TexRes, fallback: new Color(0.5f, 0.5f, 0.5f)));
                nrm.Add(LoadResized(dir + "normal.png", g.TexRes, fallback: new Color(0.5f, 0.5f, 1f)));
                orm.Add(BuildOrm(dir, g.TexRes));
                hgt.Add(BuildHeight(hc, dir, g.TexRes));
            }
        }
        finally { hc?.Dispose(); }

        foreach (var img in alb) img.GenerateMipmaps();
        foreach (var img in nrm) img.GenerateMipmaps();
        foreach (var img in orm) img.GenerateMipmaps();
        foreach (var img in hgt) img.GenerateMipmaps();

        g.Albedo = new Texture2DArray(); g.Albedo.CreateFromImages(alb);
        g.Normal = new Texture2DArray(); g.Normal.CreateFromImages(nrm);
        g.Orm = new Texture2DArray(); g.Orm.CreateFromImages(orm);
        g.Height = new Texture2DArray(); g.Height.CreateFromImages(hgt);
        g.Rules = rules.ToArray();
        g.Names = names.ToArray();
        return g;
    }

    private static PlacementParams ParsePlacement(JsonElement root)
    {
        var p = PlacementParams.Default;
        if (!root.TryGetProperty("placement", out var pl)) { return p; }
        p.BandSoftM = F(pl, "band_soft_m", p.BandSoftM);
        p.SlopeSoft = F(pl, "slope_soft", p.SlopeSoft);
        p.WarpM = F(pl, "warp_m", p.WarpM);
        p.WarpAmpM = F(pl, "warp_amp_m", p.WarpAmpM);
        p.PatchM = F(pl, "patch_m", p.PatchM);
        p.NoiseGain = F(pl, "noise_gain", p.NoiseGain);
        p.HeightBias = F(pl, "height_bias", p.HeightBias);
        p.RoughFloor = F(pl, "rough_floor", p.RoughFloor);
        p.NrmStrength = F(pl, "nrm_strength", p.NrmStrength);
        p.BlendAa = F(pl, "blend_aa", p.BlendAa);
        return p;
    }

    private static float F(JsonElement e, string k, float d) => e.TryGetProperty(k, out var v) ? v.GetSingle() : d;

    /// Load a library PNG (raw, like the existing histogram/height bakes), convert to Rgba8, resize to
    /// the array's common resolution. Missing file -> a flat fallback so a slot never breaks the bind.
    private static Image LoadResized(string resPath, int res, Color fallback)
    {
        string p = ProjectSettings.GlobalizePath(resPath);
        Image? img = System.IO.File.Exists(p) ? Image.LoadFromFile(p) : null;
        if (img == null)
        {
            img = Image.CreateEmpty(res, res, false, Image.Format.Rgba8);
            img.Fill(fallback);
            return img;
        }
        if (img.GetFormat() != Image.Format.Rgba8) { img.Convert(Image.Format.Rgba8); }
        if (img.GetWidth() != res || img.GetHeight() != res) { img.Resize(res, res, Image.Interpolation.Lanczos); }
        return img;
    }

    /// ORM: R=ambient occlusion, G=roughness, B=metallic(0). Combines the library's ao.png + roughness.png.
    /// Raw byte pack (no per-pixel GetPixel/SetPixel) so 7x1M pixels stay sub-100ms at load.
    private static Image BuildOrm(string dir, int res)
    {
        var ao = LoadResized(dir + "ao.png", res, new Color(1f, 1f, 1f));
        var rgh = LoadResized(dir + "roughness.png", res, new Color(0.85f, 0.85f, 0.85f));
        byte[] aoD = ao.GetData();
        byte[] rgD = rgh.GetData();
        int n = res * res;
        var outD = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            outD[i * 4 + 0] = aoD[i * 4 + 0];   // R = AO (red channel of ao map)
            outD[i * 4 + 1] = rgD[i * 4 + 0];   // G = roughness (red channel of roughness map)
            outD[i * 4 + 2] = 0;                // B = metallic (terrain is non-metal)
            outD[i * 4 + 3] = 255;
        }
        return Image.CreateFromData(res, res, false, Image.Format.Rgba8, outD);
    }

    /// HEIGHT (Rf): re-host the proven Poisson normal->height bake (HeightCompute). Baked at unit
    /// amplitude (per-material height_amp is applied later, in the Unit-4 blend). Headless / missing
    /// normal -> a flat 0.5 proxy (real height lands when run windowed). Kept Rf for full precision.
    private static Image BuildHeight(HeightCompute? hc, string dir, int res)
    {
        string p = ProjectSettings.GlobalizePath(dir + "normal.png");
        if (hc != null && System.IO.File.Exists(p))
        {
            try
            {
                var n = Image.LoadFromFile(p);
                if (n != null)
                {
                    var tex = hc.BakeHeight(n, res, HeightBakeIters, 1f, false, false);
                    var himg = tex.GetImage();
                    if (himg.GetFormat() != Image.Format.Rf) { himg.Convert(Image.Format.Rf); }
                    return himg;
                }
            }
            catch (Exception e) { GD.PushWarning($"[ground-v2] height bake failed for {dir}: {e.Message} -> flat proxy"); }
        }
        var fb = Image.CreateEmpty(res, res, false, Image.Format.Rf);
        fb.Fill(new Color(0.5f, 0f, 0f, 1f));
        return fb;
    }
}
