using Godot;
using WG16.Field;

namespace WG16.Lab;

/// Terrain look lab presenter: same base field as the main lab, displaced plane,
/// but the material is the 7-zone look-lab shader whose zone materials, mask mode,
/// and blend mode are all set live from the UI panel.
public partial class TerrainLab : MeshInstance3D
{
    private ShaderMaterial _mat = null!;
    private float[]? _heights;
    private int _res;
    private float _regionSize;
    private float _spacing;
    private float _minBase, _maxBase;
    private const float AabbMarginM = 8f;

    private SplatCompute? _splat;
    // Splat bake params the UI can tweak before a rebake (Lever 1).
    public float MixScaleM = 26f, MixBias = 0.5f, EdgeNoiseM = 80f, EdgeNoiseAmp = 0.30f, MacroM = 480f;
    public int SplatMaskMode = 2;

    public void Build(FieldCompute fc, FieldParams p)
    {
        float[] heights = fc.ProducePage(p, -p.RegionSizeM * 0.5f, -p.RegionSizeM * 0.5f);
        _heights = heights;
        _res = p.HeightmapRes;
        _regionSize = p.RegionSizeM;
        _spacing = p.Spacing;

        var bytes = new byte[heights.Length * sizeof(float)];
        System.Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
        Image img = Image.CreateFromData(p.HeightmapRes, p.HeightmapRes, false, Image.Format.Rf, bytes);
        ImageTexture tex = ImageTexture.CreateFromImage(img);

        if (Mesh == null)
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(p.RegionSizeM, p.RegionSizeM),
                SubdivideWidth = p.HeightmapRes - 1,
                SubdivideDepth = p.HeightmapRes - 1,
            };
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain_lab.gdshader") };
            MaterialOverride = _mat;
        }
        _mat.SetShaderParameter("heightmap", tex);
        _mat.SetShaderParameter("region_size", p.RegionSizeM);
        _mat.SetShaderParameter("texel_world", p.Spacing);

        _minBase = float.MaxValue; _maxBase = float.MinValue;
        for (int i = 0; i < heights.Length; i++) { _minBase = Mathf.Min(_minBase, heights[i]); _maxBase = Mathf.Max(_maxBase, heights[i]); }
        CustomAabb = new Aabb(
            new Vector3(-_regionSize * 0.5f, _minBase - AabbMarginM, -_regionSize * 0.5f),
            new Vector3(_regionSize, (_maxBase - _minBase) + 2f * AabbMarginM, _regionSize));
        GD.Print($"TerrainLab: built {p.HeightmapRes}x{p.HeightmapRes} (h {_minBase:F0}..{_maxBase:F0} m)");

        PushSecondaryZones();   // initialize per-zone companion table
        RebakeSplat();   // Lever 1: bake the initial splat mask
    }

    /// (Re)bake the GPU splat mask from the current heights + splat params, and
    /// bind it to the material. Called on build and on demand from the UI.
    public void RebakeSplat()
    {
        if (_heights == null) { return; }
        _splat ??= new SplatCompute();
        var sp = new SplatCompute.Params
        {
            Res = (uint)_res,
            TexelWorld = _spacing,
            RegionSize = _regionSize,
            HValley = 100f, HSlope = 350f, HHigh = 700f, HPeak = 950f,
            SlopeCliffLo = 0.30f, SlopeCliffHi = 0.55f, BandSoftnessM = 120f,
            MixScaleM = MixScaleM, MixBias = MixBias,
            MaskMode = (uint)SplatMaskMode,
            EdgeNoiseM = EdgeNoiseM, EdgeNoiseAmp = EdgeNoiseAmp, MacroM = MacroM,
        };
        var tex = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", tex);
        GD.Print($"TerrainLab: splat baked (mixScale {MixScaleM:F0}, bias {MixBias:F2}, mask {SplatMaskMode})");
    }

    /// Assign a material (by folder name under res://assets/materials/) to a zone 0..6.
    public void SetZoneMaterial(int zone, string materialName)
    {
        string b = $"res://assets/materials/{materialName}";
        _mat.SetShaderParameter($"z{zone}_alb", LoadOr(b, "albedo"));
        _mat.SetShaderParameter($"z{zone}_nrm", LoadOr(b, "normal"));
        _mat.SetShaderParameter($"z{zone}_rgh", LoadOr(b, "roughness"));
    }

    public void SetMaskMode(int mode) => _mat.SetShaderParameter("mask_mode", mode);
    public void SetBlendMode(int mode) => _mat.SetShaderParameter("blend_mode", mode);
    public void SetFloat(string param, float v) => _mat.SetShaderParameter(param, v);
    public void SetInt(string param, int v) => _mat.SetShaderParameter(param, v);
    public void SetBool(string param, bool v) => _mat.SetShaderParameter(param, v);

    private readonly int[] _secZone = { 1, 2, 1, 4, 5, 4, 5 }; // default companion per zone
    /// Set which zone's textures act as the companion blended into zone `zone`.
    public void SetSecondaryZone(int zone, int companion)
    {
        _secZone[zone] = Mathf.Clamp(companion, 0, 6);
        _mat.SetShaderParameter("sec_zone", _secZone);
    }
    public void PushSecondaryZones() => _mat.SetShaderParameter("sec_zone", _secZone);

    private Texture2D? LoadOr(string baseDir, string map)
    {
        string path = $"{baseDir}/{map}.png";
        if (ResourceLoader.Exists(path)) { return GD.Load<Texture2D>(path); }
        // fall back to albedo so missing rough/normal maps don't break the bind.
        string alb = $"{baseDir}/albedo.png";
        return ResourceLoader.Exists(alb) ? GD.Load<Texture2D>(alb) : null;
    }

    public override void _ExitTree() => _splat?.Dispose();
}
