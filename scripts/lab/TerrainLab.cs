using Godot;
using WG16.Field;

namespace WG16.Lab;

/// Terrain presenter: builds the base field into a displaced PlaneMesh and renders it with the
/// MINIMAL placeholder ground shader (height/slope color). The full per-pixel material system was
/// stripped 2026-06-21 (the reset); CDLOD / infinite terrain is the next arc. Keeps the GI/shadow
/// proxy (geometry perf) and the generic shader-param passthroughs the cloud/sky lane uses
/// (cloud_shadow_*, cam_world). The base field geometry ("the bones") is untouched.
public partial class TerrainLab : MeshInstance3D
{
    private ShaderMaterial _mat = null!;
    private float _regionSize;
    private float _minBase, _maxBase;
    public float MidHeight => (_minBase + _maxBase) * 0.5f;   // for cloud-shadow march origin
    private const float AabbMarginM = 8f;

    private MeshInstance3D? _giProxy;   // coarse GI/shadow proxy (perf)
    public bool UseGiProxy = false;     // default OFF: detail mesh casts sharp shadows + feeds GI
    public int ProxyRes = 511;          // proxy subdivision (~512²); --proxyres=N

    public void Build(FieldCompute fc, FieldParams p)
    {
        float[] heights = fc.ProducePage(p, -p.RegionSizeM * 0.5f, -p.RegionSizeM * 0.5f);
        _regionSize = p.RegionSizeM;

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
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ground.gdshader") };
            MaterialOverride = _mat;
        }
        _mat.SetShaderParameter("heightmap", tex);
        _mat.SetShaderParameter("region_size", p.RegionSizeM);
        _mat.SetShaderParameter("texel_world", p.Spacing);

        // S1: feed the live-field (analytic) path in ground.gdshader the SAME params the bake used.
        _mat.SetShaderParameter("analytic_seed", (int)p.Seed);
        _mat.SetShaderParameter("analytic_spacing", p.Spacing);
        _mat.SetShaderParameter("fp_base_freq", p.BaseFreq);
        _mat.SetShaderParameter("fp_amplitude", p.AmplitudeM);
        _mat.SetShaderParameter("fp_lacunarity", p.Lacunarity);
        _mat.SetShaderParameter("fp_gain", p.Gain);
        _mat.SetShaderParameter("fp_octaves", (int)p.Octaves);
        _mat.SetShaderParameter("fp_field_mode", 0);
        _mat.SetShaderParameter("fp_cont_octaves", (int)p.ContOctaves);
        _mat.SetShaderParameter("fp_cont_freq", p.ContFreq);
        _mat.SetShaderParameter("fp_cont_weight", p.ContWeight);
        _mat.SetShaderParameter("fp_uplift_freq", p.UpliftFreq);
        _mat.SetShaderParameter("fp_uplift_weight", p.UpliftWeight);
        _mat.SetShaderParameter("fp_uplift_lo", p.UpliftLo);
        _mat.SetShaderParameter("fp_uplift_hi", p.UpliftHi);
        _mat.SetShaderParameter("fp_macro_pivot", p.MacroPivot);
        _mat.SetShaderParameter("fp_macro_amp", p.MacroAmp);
        _mat.SetShaderParameter("fp_hill_damp", p.HillDamp);
        _mat.SetShaderParameter("fp_ridge_freq", p.RidgeFreq);
        _mat.SetShaderParameter("fp_ridge_amp", p.RidgeAmp);
        _mat.SetShaderParameter("fp_mtn_lo", p.MtnLo);
        _mat.SetShaderParameter("fp_mtn_hi", p.MtnHi);
        _mat.SetShaderParameter("fp_grain_stretch", p.GrainStretch);
        _mat.SetShaderParameter("fp_cont_warp", p.ContWarp);
        _mat.SetShaderParameter("fp_uplift_warp", p.UpliftWarp);
        _mat.SetShaderParameter("fp_massif_freq", p.MassifFreq);
        _mat.SetShaderParameter("fp_massif_floor", p.MassifFloor);
        _mat.SetShaderParameter("fp_foothill_w", p.FoothillW);
        _mat.SetShaderParameter("fp_foothill_h", p.FoothillH);

        LoadGroundMaterials();   // minimal surfacing slice: bind the textured-path materials (path itself off by default)

        // --- GI/shadow PROXY (perf): a coarse copy of the SAME heightfield. The render mesh has
        // ~4M verts for displacement detail, but SDFGI + shadow casting are low-frequency — a coarse
        // proxy can feed them ~60× cheaper. Created inert; SetGiProxy(true) flips the roles. Reuses
        // _mat so it displaces by the same heightmap. Default OFF = detail mesh feeds both.
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
                GIMode = GeometryInstance3D.GIModeEnum.Disabled,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_giProxy);
        }

        _minBase = float.MaxValue; _maxBase = float.MinValue;
        for (int i = 0; i < heights.Length; i++) { _minBase = Mathf.Min(_minBase, heights[i]); _maxBase = Mathf.Max(_maxBase, heights[i]); }
        // GLOBAL fallback envelope for streamed CDLOD chunks (NOT the single-mesh AABB / MidHeight, which stay
        // the central region). The per-chunk shadow AABB is born GENEROUS (this range ± margin) and tightened a
        // few frames later when the field-cache min/max lands. The continent/uplift field varies REGIONALLY, so
        // the central region (0,0) min/max is NOT a safe bound far from origin — a far chunk in a high-uplift belt
        // can exceed it and gets frustum/shadow-CULLED until its tighten lands (the "chunks vanish far out" bug).
        // Union a WIDE, coarse field sample (±~100 km) so the fallback brackets the macro envelope everywhere it
        // matters during that brief birth→tighten window. One extra page at load.
        float cdlodMinH = _minBase, cdlodMaxH = _maxBase;
        float[] wide = fc.ProducePage(p, -100000f, -100000f, 800f, 256, 0);   // 256 × 800 m ≈ ±100 km, macro-resolving
        for (int i = 0; i < wide.Length; i++) { cdlodMinH = Mathf.Min(cdlodMinH, wide[i]); cdlodMaxH = Mathf.Max(cdlodMaxH, wide[i]); }
        CustomAabb = new Aabb(
            new Vector3(-_regionSize * 0.5f, _minBase - AabbMarginM, -_regionSize * 0.5f),
            new Vector3(_regionSize, (_maxBase - _minBase) + 2f * AabbMarginM, _regionSize));
        if (_giProxy != null) { _giProxy.CustomAabb = CustomAabb; }
        SetGiProxy(UseGiProxy);
        GD.Print($"TerrainLab: built {p.HeightmapRes}x{p.HeightmapRes} (h {_minBase:F0}..{_maxBase:F0} m)");

        // CdlodTerrain is a SIBLING (added to our parent), NOT our child — else hiding this
        // MeshInstance3D (Visible=false in SetCdlod) would also hide every chunk in the subtree.
        _cdlod ??= new CdlodTerrain { Name = "CdlodTerrain" };
        if (_cdlod.GetParent() == null)
        {
            // The tree is mid-setup when Build() runs from TerrainLabUI._Ready, so a direct
            // AddChild fails with "parent busy setting up children" and leaves _cdlod orphaned
            // (its chunk instances then never enter a viewport → nothing renders). Defer the add
            // to the next idle frame — same pattern the cloud/atmosphere sibling nodes use in
            // TerrainLabUI._Ready. Setup() needs no in-tree state, so it can run immediately.
            (GetParent() ?? (Node)this).CallDeferred(Node.MethodName.AddChild, _cdlod);
            _cdlod.Setup(_mat, p, cdlodMinH, cdlodMaxH, heights);   // wide global envelope for the streamed-chunk fallback AABB
        }

        // S2b: the LOD-crossing test-path player (sibling, deferred — same reason as _cdlod). Setup needs
        // the camera + cdlod (both siblings under our parent), so defer it one frame too.
        _testPaths ??= new TerrainTestPaths { Name = "TerrainTestPaths" };
        if (_testPaths.GetParent() == null)
        {
            (GetParent() ?? (Node)this).CallDeferred(Node.MethodName.AddChild, _testPaths);
            CallDeferred(nameof(SetupTestPaths));
        }
    }

    /// S1: flip the ground material between the live analytic field and the baked heightmap (A/B).
    public void SetAnalytic(bool on)
    {
        _mat?.SetShaderParameter("use_analytic", on);
        GD.Print($"TerrainLab: ground source = {(on ? "ANALYTIC (live field)" : "baked texture")}");
    }

    private MeshInstance3D? _cdlodTest;
    /// TASK 1 SANITY ONLY (superseded by CdlodTerrain in Task 3): one chunk instance
    /// covering the whole region — should look identical to the single analytic mesh.
    public void CdlodTestOneChunk(FieldParams p)
    {
        if (_cdlodTest != null) { return; }
        var grid = CdlodMesh.BuildGrid(65);   // 64 quads/side
        _cdlodTest = new MeshInstance3D { Mesh = grid, MaterialOverride = _mat };
        // unit grid centered at origin -> scale X/Z to region, Y scale 1 (height is world units)
        _cdlodTest.Scale = new Vector3(p.RegionSizeM, 1f, p.RegionSizeM);
        _mat?.SetShaderParameter("use_chunk", 1.0f);   // chunk-mode on the shared material
        _cdlodTest.CustomAabb = new Aabb(
            new Vector3(-0.5f, _minBase - AabbMarginM, -0.5f),     // unit-space AABB (pre-scale); Godot scales X/Z
            new Vector3(1f, (_maxBase - _minBase) + 2f * AabbMarginM, 1f));
        AddChild(_cdlodTest);
        Visible = false;                       // hide the original full mesh so we see ONLY the chunk
        GD.Print("TerrainLab: CDLOD one-chunk test instance added (full region)");
    }

    private CdlodTerrain? _cdlod;
    /// S2a: switch between the quadtree CDLOD terrain and the single full mesh (A/B).
    public void SetCdlod(bool on)
    {
        if (_cdlod == null) { return; }
        _mat?.SetShaderParameter("use_chunk", on ? 1.0f : 0.0f);   // chunk-mode on the shared material
        if (on && _cdlod != null)
        {
            // S2b: push the geomorph params so the shader's per-vertex morph window matches the
            // quadtree's actual split rule (camDist < size*SplitFactor). cam_world is pushed every
            // frame separately (SetCameraWorld from TerrainLabUI.Process).
            _mat?.SetShaderParameter("grid_n", _cdlod.GridResolution);
            _mat?.SetShaderParameter("split_factor", _cdlod.SplitFactorValue);
        }
        _cdlod.SetEnabled(on);
        Visible = !on;                          // hide the single full mesh (CdlodTerrain is a sibling, unaffected)
        if (_giProxy != null) { _giProxy.Visible = !on; }
        GD.Print($"TerrainLab: CDLOD {(on ? "ON (quadtree)" : "off (single mesh)")}");
    }
    public void SetCdlodViz(bool on) { _cdlod?.SetLodViz(on); }
    public void SetLoadRing(int r) { if (_cdlod != null) { _cdlod.LoadRing = Mathf.Clamp(r, 0, 8); } }   // ARC B Task 1 (8=17×17 ~65 km)
    public void SetFieldCache(bool on) { if (_cdlod != null) { _cdlod.FieldCache = on; } }   // per-chunk field cache A/B
    public void SetBakeReq(int n) { _cdlod?.SetBakeReq(n); }   // field-cache bake throttle
    public void SetChunkOps(int n) { if (_cdlod != null) { _cdlod.MaxChunkOps = Mathf.Max(1, n); } }   // per-frame birth cap (unthrottle = high)
    public int LoadRing => _cdlod?.LoadRing ?? 5;
    public void CdlodTick(Vector3 camPos, Vector3 velXZ = default) { _cdlod?.Tick(camPos, velXZ); }   // ARC B Task 4: vel for predictive loading
    public void SetCdlodLookahead(float seconds) { if (_cdlod != null) { _cdlod.PredictLookahead = Mathf.Max(0f, seconds); } }
    public void ConfigureCdlodAabb(bool tighten, int probeRes, int maxReq) { _cdlod?.ConfigureAabb(tighten, probeRes, maxReq); }
    public CdlodTerrain? Cdlod => _cdlod;   // S3 --popmeter: live meter reads RenderOrigin
    public void SetPinOrigin(bool on) { if (_cdlod != null) { _cdlod.PinOrigin = on; } }   // DEBUG --pinorigin
    // S3 floating-origin: the active render-frame offset, and whether CDLOD owns the camera frame this run.
    public Vector3 CdlodRenderOrigin => _cdlod?.RenderOrigin ?? Vector3.Zero;
    public bool CdlodActive => _cdlod != null && _cdlod.Enabled;

    // S2b: LOD-crossing test-path player (TerrainTestPaths sibling). RunTestPath starts a flight;
    // TickTestPath advances it each frame (called from TerrainLabUI.Process); the report prints on finish.
    private TerrainTestPaths? _testPaths;
    private void SetupTestPaths()
    {
        var cam = GetParent()?.GetNodeOrNull<Camera3D>("Camera");
        if (cam != null && _cdlod != null) { _testPaths?.Setup(cam, _cdlod); }
    }
    public void RunTestPath(int i, bool cliQuit = false) => _testPaths?.Start(i, cliQuit);
    public void TickTestPath(double delta) => _testPaths?.Tick(delta);
    public bool TestPathRunning => _testPaths?.Running ?? false;

    /// Toggle the GI/shadow proxy. ON: the coarse proxy feeds SDFGI + casts shadows; the detail mesh
    /// renders the view only. OFF: detail mesh feeds both; proxy inert.
    public void SetGiProxy(bool on)
    {
        UseGiProxy = on;
        if (_giProxy == null) { return; }
        if (on)
        {
            GIMode = GeometryInstance3D.GIModeEnum.Disabled;
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _giProxy.GIMode = GeometryInstance3D.GIModeEnum.Static;
            _giProxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
        }
        else
        {
            GIMode = GeometryInstance3D.GIModeEnum.Static;
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            _giProxy.GIMode = GeometryInstance3D.GIModeEnum.Disabled;
            _giProxy.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        GD.Print($"TerrainLab: GI/shadow proxy {(on ? "ON" : "off")}");
    }

    /// Rebuild the proxy mesh at a new subdivision.
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
            GD.Print($"TerrainLab: proxy res → {ProxyRes}²");
        }
    }

    // Generic shader-param passthroughs to the ground material. The cloud/sky lane uses these for
    // cloud_shadow_tex / cloud_shadow_region / cloud_shadow_on / cam_world; harmless if the minimal
    // placeholder doesn't declare a given uniform (Godot just stores it).
    public void SetFloat(string param, float v) => _mat.SetShaderParameter(param, v);
    public void SetInt(string param, int v) => _mat.SetShaderParameter(param, v);
    public void SetBool(string param, bool v) => _mat.SetShaderParameter(param, v);
    public void SetColor(string param, Color v) => _mat.SetShaderParameter(param, v);
    public void SetVector3(string param, Vector3 v) => _mat.SetShaderParameter(param, v);
    public void SetTexture(string param, Texture2D tex) => _mat.SetShaderParameter(param, tex);
    public void SetCameraWorld(Vector3 p) => _mat.SetShaderParameter("cam_world", p);

    /// Bind ONE region's water texture for the terrain carve. The vertex shader subtracts a thin
    /// profile-blended groove where the texture says "wet". Until this is called, water_carve_on stays
    /// false → the base field is byte-identical (--fieldcheck 0m).
    public void BindWaterRegion(WG16.Hydrology.WorldWaterRegion reg, WG16.Hydrology.WaterParams wp)
    {
        _mat.SetShaderParameter("water_tex", reg.Texture);
        _mat.SetShaderParameter("water_region_origin", reg.RegionOriginWorld);
        _mat.SetShaderParameter("water_region_m", reg.RegionM);
        _mat.SetShaderParameter("carve_width_m", wp.CarveWidthM);
        _mat.SetShaderParameter("carve_depth_scale", wp.CarveDepthScale);
        _mat.SetShaderParameter("carve_profile_exp", wp.CarveProfileExp);
        _mat.SetShaderParameter("water_carve_on", true);
    }

    // Minimal surfacing slice: bind ~5 fixed material sets from assets/materials by role. Fluid/disposable
    // (the full surfacing arc replaces this with texture arrays); the placement/blend LOGIC in the shader is
    // the kept work. Roles: 0 low/sand, 1 valley grass, 2 mid earth/scree, 3 slope rock, 4 peak snow.
    private static readonly string[] _matRoles =
    {
        "03_coarse_sand", "01_tussock_grass", "02_muddy_with_stones",
        "01_weathered_grey_bedrock", "01_fresh_powder",
    };
    public void LoadGroundMaterials()
    {
        for (int i = 0; i < _matRoles.Length; i++)
        {
            string b = $"res://assets/materials/{_matRoles[i]}/";
            var alb = GD.Load<Texture2D>(b + "albedo.png");
            var nrm = GD.Load<Texture2D>(b + "normal.png");
            var rgh = GD.Load<Texture2D>(b + "roughness.png");
            if (alb != null) { _mat.SetShaderParameter($"mat{i}_alb", alb); }
            if (nrm != null) { _mat.SetShaderParameter($"mat{i}_nrm", nrm); }
            if (rgh != null) { _mat.SetShaderParameter($"mat{i}_rgh", rgh); }
        }
        GD.Print($"TerrainLab: ground materials loaded ({_matRoles.Length} roles)");
    }
    public void SetTexturesOn(bool on) { _mat.SetShaderParameter("use_textures", on); }
}
