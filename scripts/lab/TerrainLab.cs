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
    public float MidHeight => (_minBase + _maxBase) * 0.5f;   // for cloud-shadow march origin
    private const float AabbMarginM = 8f;

    private SplatCompute? _splat;
    private HeightCompute? _height;
    // AAA anti-tiling: per-zone albedo histogram LUTs packed into one Rf atlas (256 × 42),
    // rows = zone*6 + {fwd R,G,B | inv R,G,B}. Bound once as tile_lut; baked per zone material.
    private const int LutRows = 7 * HistogramCompute.RowsPerZone;   // 42
    private readonly float[] _lutData = new float[HistogramCompute.Bins * LutRows];
    private ImageTexture? _lutTex;
    // GM2: derive per-material height from normals (modular; behind shader's height_from_maps, default off).
    public bool  BakeHeightMaps = true;     // bake on material-set so height is ready when toggled on
    public int   HeightBakeRes = 512, HeightIters = 64;
    public float HeightAmp = 1.0f;
    public bool  HeightFlipY = false, HeightInvert = false;
    private MeshInstance3D? _giProxy;   // coarse GI/shadow proxy (perf)
    public bool UseGiProxy = false;     // default OFF (eye-gate 2026-06-20): SDFGI off → proxy's only job is shadows; detail mesh casts SHARP shadows (~+1.9 ms vs coarse proxy, no shifting). Toggle on for the cheap-coarse-shadow perf lever.
    public int ProxyRes = 511;          // 512² (~260k verts): the sweet spot — blob-free (user-verified 2026-06-19) at ~8.4 ms in-motion. 256² blobbed; 1024² clean but ~2 ms costlier. Tunable via --proxyres=N.
    // Splat bake params the UI can tweak before a rebake (Lever 1).
    public float MixScaleM = 26f, MixBias = 0.5f, EdgeNoiseM = 80f, EdgeNoiseAmp = 0.30f, MacroM = 480f;
    public int SplatMaskMode = 2;
    // G1 rule engine: meaningful, signal-driven material placement (vs legacy bands).
    public bool RuleBased = false;   // default off = current approved look
    public float CurvK = 3.0f;       // curvature scale (m): convex ridge vs concave hollow split
    // Unit 4 breakup-mask SHAPE params (rebake on change).
    public float BkSlopeLo = 0.30f, BkSlopeHi = 0.62f, BkCurvScale = 3.0f, BkCavityGain = 1.4f, BkSunAzimuth = 0.7f;
    public int   BkFlowIters = 3;
    // Placement breakpoints — promoted from hardcoded so they're live re-bake UI knobs.
    // Defaults preserve the previous hardcoded bake values exactly.
    public float HValley = 100f, HSlope = 350f, HHigh = 700f, HPeak = 950f;
    public float SlopeCliffLo = 0.30f, SlopeCliffHi = 0.55f, BandSoftnessM = 120f;

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

        // AAA anti-tiling: seed tile_lut with an IDENTITY transform so the shader's tile_mode==3
        // path is valid before any zone bakes (forward=value, inverse=value). Once only.
        if (_lutTex == null)
        {
            for (int z = 0; z < 7; z++)
                for (int c = 0; c < 3; c++)
                    for (int b = 0; b < HistogramCompute.Bins; b++)
                    {
                        float v = b / (float)(HistogramCompute.Bins - 1);
                        _lutData[(z * HistogramCompute.RowsPerZone + c)     * HistogramCompute.Bins + b] = v; // fwd identity
                        _lutData[(z * HistogramCompute.RowsPerZone + 3 + c) * HistogramCompute.Bins + b] = v; // inv identity
                    }
            PushLutAtlas();
        }

        // --- GI/shadow PROXY (perf): a coarse copy of the SAME heightfield. The render
        // mesh has ~4M verts for displacement detail, but SDFGI revoxelization + shadow
        // casting are LOW-FREQUENCY — they need the terrain's shape, not its fine verts.
        // So a ~256² proxy (≈65k verts) can feed GI + cast shadows ~60× cheaper, while the
        // detail mesh renders the view. Created inert; SetGiProxy(true) flips the roles.
        // Reuses _mat so it displaces by the same heightmap (vertex()); ShadowsOnly => never
        // drawn in the colour pass. Default OFF = current behaviour (detail mesh feeds both).
        if (_giProxy == null)
        {
            _giProxy = new MeshInstance3D
            {
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(p.RegionSizeM, p.RegionSizeM),
                    SubdivideWidth = ProxyRes,
                    SubdivideDepth = ProxyRes,
                },
                MaterialOverride = _mat,
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,         // inert until toggled on
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_giProxy);
        }

        _minBase = float.MaxValue; _maxBase = float.MinValue;
        for (int i = 0; i < heights.Length; i++) { _minBase = Mathf.Min(_minBase, heights[i]); _maxBase = Mathf.Max(_maxBase, heights[i]); }
        CustomAabb = new Aabb(
            new Vector3(-_regionSize * 0.5f, _minBase - AabbMarginM, -_regionSize * 0.5f),
            new Vector3(_regionSize, (_maxBase - _minBase) + 2f * AabbMarginM, _regionSize));
        if (_giProxy != null) { _giProxy.CustomAabb = CustomAabb; }
        SetGiProxy(UseGiProxy);   // apply the current toggle state to both meshes
        GD.Print($"TerrainLab: built {p.HeightmapRes}x{p.HeightmapRes} (h {_minBase:F0}..{_maxBase:F0} m)");
        // Splat bake + sec-zone push are driven by the UI's ApplyAll() right after
        // Build(), once the final registry params are set — so we don't bake here
        // (that was a redundant double-bake on startup).
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
            HValley = HValley, HSlope = HSlope, HHigh = HHigh, HPeak = HPeak,
            SlopeCliffLo = SlopeCliffLo, SlopeCliffHi = SlopeCliffHi, BandSoftnessM = BandSoftnessM,
            MixScaleM = MixScaleM, MixBias = MixBias,
            MaskMode = (uint)SplatMaskMode,
            EdgeNoiseM = EdgeNoiseM, EdgeNoiseAmp = EdgeNoiseAmp, MacroM = MacroM,
            RuleBased = RuleBased ? 1u : 0u, CurvK = CurvK,
            BkSlopeLo = BkSlopeLo, BkSlopeHi = BkSlopeHi, BkCurvScale = BkCurvScale,
            BkCavityGain = BkCavityGain, BkSunAzimuth = BkSunAzimuth, BkFlowIters = (uint)BkFlowIters,
        };
        var baked = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", baked.Splat);
        _mat.SetShaderParameter("splat_wa", baked.WeightsA);
        _mat.SetShaderParameter("splat_wb", baked.WeightsB);
        _mat.SetShaderParameter("breakup_tex", baked.Breakup);
        // Fragment must read the baked secondary (splat.g) when the rule engine is on;
        // keep it in lockstep with the bake so the two never disagree.
        _mat.SetShaderParameter("splat_rule_based", RuleBased);
        GD.Print($"TerrainLab: splat baked (rule {(RuleBased ? 1 : 0)}, curvK {CurvK:F1}, snow {HPeak:F0}, mask {SplatMaskMode})");
    }

    /// Assign a material (by folder name under res://assets/materials/) to a zone 0..6.
    public void SetZoneMaterial(int zone, string materialName)
    {
        string b = $"res://assets/materials/{materialName}";
        _mat.SetShaderParameter($"z{zone}_alb", LoadOr(b, "albedo"));
        _mat.SetShaderParameter($"z{zone}_nrm", LoadOr(b, "normal"));
        _mat.SetShaderParameter($"z{zone}_rgh", LoadOr(b, "roughness"));
        _mat.SetShaderParameter($"z{zone}_ao", LoadOr(b, "ao"));
        BakeZoneHeight(zone, materialName);
        BakeZoneHistogram(zone, materialName);
    }

    /// AAA anti-tiling: bake this material's albedo histogram LUTs into the shared tile_lut atlas.
    /// Cheap pure-C# reduction, baked once per material; inert until the shader's tile_mode is 3.
    private void BakeZoneHistogram(int zone, string materialName)
    {
        string p = ProjectSettings.GlobalizePath($"res://assets/materials/{materialName}/albedo.png");
        if (!System.IO.File.Exists(p)) { return; }   // no albedo → leave identity rows (mode-3 == plain for this zone)
        Image alb = Image.LoadFromFile(p);
        if (alb == null) { return; }
        float[] luts = HistogramCompute.ComputeLuts(alb);
        int baseRow = zone * HistogramCompute.RowsPerZone;
        System.Buffer.BlockCopy(luts, 0, _lutData, baseRow * HistogramCompute.Bins * sizeof(float), luts.Length * sizeof(float));
        PushLutAtlas();
    }

    /// (Re)build the tile_lut Rf atlas texture from _lutData and bind it to the material.
    private void PushLutAtlas()
    {
        var bytes = new byte[_lutData.Length * sizeof(float)];
        System.Buffer.BlockCopy(_lutData, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(HistogramCompute.Bins, LutRows, false, Image.Format.Rf, bytes);
        if (_lutTex == null) { _lutTex = ImageTexture.CreateFromImage(img); }
        else { _lutTex.Update(img); }
        _mat.SetShaderParameter("tile_lut", _lutTex);
    }

    /// GM2: derive this zone's height map from its normal map and bind z{zone}_hgt.
    /// Cheap, baked-once-per-material; inert until the shader's height_from_maps is on.
    private void BakeZoneHeight(int zone, string materialName)
    {
        if (!BakeHeightMaps) { return; }
        string p = ProjectSettings.GlobalizePath($"res://assets/materials/{materialName}/normal.png");
        if (!System.IO.File.Exists(p)) { return; }   // no normal → leave z*_hgt unbound (flat); height_from_maps off anyway
        Image nrm = Image.LoadFromFile(p);
        if (nrm == null) { return; }
        _height ??= new HeightCompute();
        ImageTexture hgt = _height.BakeHeight(nrm, HeightBakeRes, HeightIters, HeightAmp, HeightFlipY, HeightInvert);
        _mat.SetShaderParameter($"z{zone}_hgt", hgt);
    }

    /// Toggle the GI/shadow proxy. ON: the coarse proxy feeds SDFGI + casts shadows; the
    /// detail mesh renders the view only (no GI/shadow). OFF: current behaviour (detail mesh
    /// feeds both; proxy inert). Cuts SDFGI revoxelization + shadow raster ~60× in motion.
    public void SetGiProxy(bool on)
    {
        UseGiProxy = on;
        if (_giProxy == null) { return; }
        if (on)
        {
            GIMode = GeometryInstance3D.GIModeEnum.Disabled;            // detail: view only
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _giProxy.GIMode = GeometryInstance3D.GIModeEnum.Static;     // proxy: GI + shadows
            _giProxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        }
        else
        {
            GIMode = GeometryInstance3D.GIModeEnum.Static;              // detail: GI + shadows (current)
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            _giProxy.GIMode = GeometryInstance3D.GIModeEnum.Disabled;   // proxy: inert
            _giProxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        GD.Print($"TerrainLab: GI/shadow proxy {(on ? "ON (coarse proxy feeds GI+shadows)" : "off (detail mesh feeds GI+shadows)")}");
    }

    /// Rebuild the proxy mesh at a new subdivision (live A/B of GI-blob vs cost).
    public void SetProxyRes(int r)
    {
        ProxyRes = Mathf.Clamp(r, 31, 2047);
        if (_giProxy != null)
        {
            _giProxy.Mesh = new PlaneMesh
            {
                Size = new Vector2(_regionSize, _regionSize),
                SubdivideWidth = ProxyRes,
                SubdivideDepth = ProxyRes,
            };
            GD.Print($"TerrainLab: proxy res → {ProxyRes}² (~{((ProxyRes + 1) * (ProxyRes + 1)) / 1000}k verts)");
        }
    }

    public void SetMaskMode(int mode) => _mat.SetShaderParameter("mask_mode", mode);
    public void SetBlendMode(int mode) => _mat.SetShaderParameter("blend_mode", mode);
    public void SetFloat(string param, float v) => _mat.SetShaderParameter(param, v);
    public void SetInt(string param, int v) => _mat.SetShaderParameter(param, v);
    public void SetBool(string param, bool v) => _mat.SetShaderParameter(param, v);
    public void SetTexture(string param, Texture2D tex) => _mat.SetShaderParameter(param, tex);
    public void SetCameraWorld(Vector3 p) => _mat.SetShaderParameter("cam_world", p);

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

    public override void _ExitTree() { _splat?.Dispose(); _height?.Dispose(); }
}
