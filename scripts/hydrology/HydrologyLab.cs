using Godot;
using WG16.Field;

namespace WG16.Hydrology;

/// Thin interactive harness for the drainage-synthesis terrain + static water. Builds the substrate via
/// HydrologyPipeline, displays the carved terrain (ground.gdshader colour-ramp) + water (WaterRenderer), and
/// exposes live knobs. Windowed only (local RD). Mechanical gates live in HydrologyChecks; the build pipeline in
/// HydrologyPipeline; water in WaterRenderer — this file is just orchestration + input.
///   D = cycle substrate debug views   [ ] = carve strength   R = rebuild
public partial class HydrologyLab : Node3D
{
    private const int Res = 512;
    private const float Cell = 8f;

    private HydrologyPipeline _pipeline = null!;
    private WaterRenderer _water = null!;
    private WaterParams _wp = null!;
    private DrainageGraph _lastGraph = null!;
    private HydrologyParams _hp = null!;
    private ValleyCarve.CarveResult _carve = null!;
    private MeshInstance3D _mesh = null!;
    private ShaderMaterial _mat = null!;
    private Label _hud = null!;
    private int _debug = -1;            // -1 lit, 0 flow_accum, 1 channel_mask, 2 water_level, 3 sediment
    private bool _rDown, _dDown, _lbDown, _rbDown;
    private int _shotCountdown = -1;    // --hydroshot: capture the real viewport render then quit

    public override void _Ready()
    {
        var p = FieldParams.Load();
        using (var fc = new FieldCompute())
        {
            // mechanical gates: --coarsecheck / --drainagecheck / --determinismcheck / --carvecheck → print + quit.
            if (HydrologyChecks.TryRun(p, fc, Res, Cell)) { SetProcess(false); GetTree().Quit(); return; }

            _hp = new HydrologyParams { Res = Res, CellSize = Cell };
            _wp = new WaterParams();
            ApplyCliOverrides();

            _mesh = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(Res * Cell, Res * Cell), SubdivideWidth = Res - 1, SubdivideDepth = Res - 1 },
            };
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ground.gdshader") };
            _mat.SetShaderParameter("use_textures", false);   // height/slope colour ramp (no bound textures)
            _mesh.MaterialOverride = _mat;
            AddChild(_mesh);

            _pipeline = new HydrologyPipeline(Res, Cell);
            _water = new WaterRenderer(Res, Cell);
            Rebuild(p, fc);
        }

        _hud = new Label { Position = new Vector2(12, 12) };
        var ui = new CanvasLayer(); ui.AddChild(_hud); AddChild(ui);

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--hydroshot") >= 0) { _shotCountdown = 8; }

        // Initial camera: high vantage over the dry HIGH ground (south/+Z side sits above median height),
        // looking north-down ACROSS the terrain so both dry land and the low water read — not straight into the
        // basin. --cam=x,y,z,tx,ty,tz overrides (pos + look-at target).
        var cam = GetNodeOrNull<Camera3D>("Camera");
        if (cam != null)
        {
            Vector3 pos = new(0, 900, 2200), tgt = new(0, 0, -400);
            foreach (string a in OS.GetCmdlineUserArgs())
            {
                if (a.StartsWith("--cam="))
                {
                    var v = a.Substring(6).Split(',');
                    if (v.Length == 6)
                    { pos = new Vector3(v[0].ToFloat(), v[1].ToFloat(), v[2].ToFloat()); tgt = new Vector3(v[3].ToFloat(), v[4].ToFloat(), v[5].ToFloat()); }
                }
            }
            cam.Position = pos; cam.LookAt(tgt, Vector3.Up);
        }
    }

    private void ApplyCliOverrides()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--chanmin=")) { _hp.ChannelMinArea = a.Substring(10).ToFloat(); }
            else if (a.StartsWith("--coarsesp=")) { _hp.CoarseSpacing = a.Substring(11).ToFloat(); }
            else if (a.StartsWith("--depth=")) { _hp.DepthPerOrder = a.Substring(8).ToFloat(); }
            else if (a.StartsWith("--width=")) { _hp.WidthPerOrder = a.Substring(8).ToFloat(); }
            else if (a.StartsWith("--carve=")) { _hp.CarveStrength = a.Substring(8).ToFloat(); }
            else if (a.StartsWith("--carvemin=")) { _hp.CarveMinOrder = (int)a.Substring(11).ToFloat(); }
            else if (a.StartsWith("--lakemin=")) { _hp.LakeMinDepth = a.Substring(10).ToFloat(); }
            else if (a.StartsWith("--polish=")) { _hp.PolishSteps = (int)a.Substring(9).ToFloat(); }
            else if (a.StartsWith("--mfd=")) { _hp.MfdExp = a.Substring(6).ToFloat(); }
            else if (a.StartsWith("--lakefill=")) { _hp.LakeFill = a.Substring(11).ToFloat(); }
            // water knobs
            else if (a.StartsWith("--sea=")) { _wp.SeaLevel = a.Substring(6).ToFloat(); }
            else if (a == "--nosea") { _wp.SeaEnabled = false; }
            else if (a.StartsWith("--shallow=")) { _wp.ShallowDepthM = a.Substring(10).ToFloat(); }
            else if (a.StartsWith("--wave=")) { _wp.WaveScale = a.Substring(7).ToFloat(); }
            else if (a.StartsWith("--flow=")) { _wp.FlowSpeed = a.Substring(7).ToFloat(); }
        }
    }

    private void Rebuild(FieldParams p, FieldCompute fc)
    {
        _carve = _pipeline.Build(p, fc, _hp);
        _lastGraph = _pipeline.LastGraph;
        UploadHeight(_carve.Height);
        _water.BuildSea(this, _wp, _carve.Height);             // tier 1: global sea
        var wb = WaterBodies.Build(_lastGraph, _wp);           // tier 2/3: significance-filtered bodies
        _water.BuildLakes(this, wb.Lakes, _wp, _carve.Height); // tier 2: significant lakes (rivers in T5)
        RefreshDebug();
        GD.Print($"HydrologyLab: {_pipeline.SegmentCount} segments, sea={(_wp.SeaEnabled ? _wp.SeaLevel.ToString("F0") : "off")}, lakes={wb.Lakes.Count}, rivers={wb.Rivers.Count}");
    }

    private void Rebuild()   // live-knob rebuild (re-creates a FieldCompute; the _Ready one is disposed)
    {
        var p = FieldParams.Load();
        using var fc = new FieldCompute();
        Rebuild(p, fc);
    }

    public override void _Process(double delta)
    {
        if (_shotCountdown >= 0)
        {
            if (_shotCountdown == 0)
            {
                GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://hydro_shot.png"));
                GD.Print("HYDROSHOT: wrote hydro_shot.png");
                SetProcess(false); GetTree().Quit(); return;
            }
            _shotCountdown--; return;
        }

        bool d = Input.IsPhysicalKeyPressed(Key.D);
        if (d && !_dDown) { _debug = _debug >= 3 ? -1 : _debug + 1; RefreshDebug(); }
        _dDown = d;

        bool lb = Input.IsPhysicalKeyPressed(Key.Bracketleft), rb = Input.IsPhysicalKeyPressed(Key.Bracketright);
        if (lb && !_lbDown) { _hp.CarveStrength = Mathf.Max(0f, _hp.CarveStrength - 0.1f); Rebuild(); }
        if (rb && !_rbDown) { _hp.CarveStrength += 0.1f; Rebuild(); }
        _lbDown = lb; _rbDown = rb;

        bool r = Input.IsPhysicalKeyPressed(Key.R);
        if (r && !_rDown) { Rebuild(); }
        _rDown = r;

        string v = _debug switch { < 0 => "lit", 0 => "flow_accum", 1 => "channel_mask", 2 => "water_level", _ => "sediment" };
        _hud.Text = $"hydrology lab  carve={_hp.CarveStrength:F1} lakefill={_hp.LakeFill:F1}  view={v}\n" +
                    $"[ ] carve   D view (lit→accum→channel→wlevel→sed)   R rebuild   {Engine.GetFramesPerSecond():0} fps";
    }

    private void UploadHeight(float[] h)
    {
        var bytes = new byte[h.Length * 4]; System.Buffer.BlockCopy(h, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(Res, Res, false, Image.Format.Rf, bytes);
        _mat.SetShaderParameter("heightmap", ImageTexture.CreateFromImage(img));
        _mat.SetShaderParameter("use_analytic", false);
        _mat.SetShaderParameter("use_chunk", 0.0f);
        _mat.SetShaderParameter("region_size", Res * Cell);
        _mat.SetShaderParameter("texel_world", Cell);
    }

    private void RefreshDebug()
    {
        if (_debug < 0) { _mat.SetShaderParameter("debug_field", 0.0f); return; }
        float[] src = _debug switch
        { 0 => _carve.FlowAccum, 1 => _carve.ChannelMask, 2 => _carve.WaterLevel, _ => _carve.Sediment };
        var disp = (float[])src.Clone();
        if (_debug == 0)   // flow_accum: log-scale so the whole network reads, not just trunks
        {
            float mx = 1e-6f; foreach (float v in disp) { if (v > mx) { mx = v; } }
            float lm = Mathf.Log(1f + mx);
            for (int k = 0; k < disp.Length; k++) { disp[k] = Mathf.Log(1f + disp[k]) / Mathf.Max(lm, 1e-6f); }
        }
        else if (_debug == 2)   // water_level: -1e9 sentinel → presence mask
        { for (int k = 0; k < disp.Length; k++) { disp[k] = disp[k] > -1e8f ? 1f : 0f; } }
        else
        { float mx = 1e-6f; foreach (float v in disp) { if (v > mx) { mx = v; } } for (int k = 0; k < disp.Length; k++) { disp[k] /= mx; } }
        var bytes = new byte[disp.Length * 4]; System.Buffer.BlockCopy(disp, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(Res, Res, false, Image.Format.Rf, bytes);
        _mat.SetShaderParameter("debug_field_tex", ImageTexture.CreateFromImage(img));
        _mat.SetShaderParameter("debug_field", 1.0f);
    }

    public override void _ExitTree() { _pipeline?.Dispose(); }
}
