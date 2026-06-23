using Godot;
using WG16.Field;

namespace WG16.Erosion;

/// Standalone erosion lab: seed a region from the base field, step the pipe-model sim, display the eroded
/// height live with debug views + knobs. Windowed only (local RD). NOT wired into the CDLOD terrain — this is
/// the judging harness for E1 ("does the erosion look good"), before any bake/stream infra.
///   space = run/pause   S = single step   R = reset water   D = cycle debug view (lit/water/sediment/flow)
public partial class ErosionLab : Node3D
{
    private ErosionSim _sim = null!;
    private MeshInstance3D _mesh = null!;
    private ShaderMaterial _mat = null!;
    private Label _hud = null!;
    private int _res = 512;
    private float _cell = 8f;
    private bool _running;
    private int _debug = -1;          // -1 lit height, 0 water, 1 sediment, 2 flow
    private int _stepsPerFrame = 4;
    private long _totalSteps;
    private bool _sDown, _rDown, _dDown;

    public override void _Ready()
    {
        var p = FieldParams.Load();
        using (var fc = new FieldCompute())
        {
            float[] seed = fc.ProducePage(p, -_res * _cell * 0.5f, -_res * _cell * 0.5f, _cell, _res, 0);
            _sim = new ErosionSim(_res) { Params = new ErosionParams { Res = _res, CellSize = _cell } };
            _sim.Seed(seed);

            // numeric self-check path (--erosioncheck): step + assert finite/bounded, print, quit.
            foreach (string a in OS.GetCmdlineUserArgs())
            {
                if (a == "--erosioncheck")
                {
                    int steps = 600;
                    _sim.Step(steps);
                    float[] h = _sim.ReadHeight();
                    bool finite = true; float lo = 1e9f, hi = -1e9f;
                    foreach (float v in h) { if (!float.IsFinite(v)) { finite = false; } lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
                    // SAWTOOTH DETECTOR: mean |Laplacian| (4-neighbor 2nd diff). A grid-aligned sawtooth has
                    // huge cell-to-cell oscillation → high value; smooth valleys → low. Reported as a fraction
                    // of the height range so it's scale-free. The racy version scored ~10x the smooth one.
                    double lap = 0; long n = 0;
                    for (int z = 1; z < _res - 1; z++)
                    for (int x = 1; x < _res - 1; x++)
                    {
                        int j = z * _res + x;
                        float l = 4f * h[j] - h[j-1] - h[j+1] - h[j-_res] - h[j+_res];
                        lap += Mathf.Abs(l); n++;
                    }
                    float rough = (float)(lap / System.Math.Max(n, 1)) / Mathf.Max(hi - lo, 1f);
                    bool smooth = rough < 0.02f;   // empirical: sawtooth >> 0.02, real erosion well under
                    bool ok = finite && (hi - lo) < 5000f && smooth;
                    GD.Print($"EROSIONCHECK: {(ok ? "PASS" : "FAIL")}  finite={finite} range=[{lo:F1},{hi:F1}] roughness={rough:F4} (sawtooth if >0.02) after {steps} steps");
                    _sim.Dispose(); _sim = null!;
                    SetProcess(false);   // stop _Process firing on the disposed sim before the tree tears down
                    GetTree().Quit();
                    return;
                }
            }

            _mesh = new MeshInstance3D
            {
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(_res * _cell, _res * _cell),
                    SubdivideWidth = _res - 1,
                    SubdivideDepth = _res - 1,
                },
            };
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ground.gdshader") };
            _mat.SetShaderParameter("use_textures", true);
            _mesh.MaterialOverride = _mat;
            AddChild(_mesh);
            UploadHeight(seed);
        }

        _hud = new Label { Position = new Vector2(12, 12) };
        var ui = new CanvasLayer();
        ui.AddChild(_hud);
        AddChild(ui);
        GD.Print($"ErosionLab: seeded {_res}² region ({_res * _cell:F0} m). space=run S=step R=reset D=debug-view");
    }

    private void UploadHeight(float[] h)
    {
        var bytes = new byte[h.Length * 4];
        System.Buffer.BlockCopy(h, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes);
        _mat.SetShaderParameter("heightmap", ImageTexture.CreateFromImage(img));
        // display via the baked-heightmap branch of ground.gdshader (analytic + chunk both off).
        _mat.SetShaderParameter("use_analytic", false);
        _mat.SetShaderParameter("use_chunk", 0.0f);
        _mat.SetShaderParameter("region_size", _res * _cell);
        _mat.SetShaderParameter("texel_world", _cell);
    }

    public override void _Process(double delta)
    {
        if (_sim == null) { return; }

        if (Input.IsActionJustPressed("ui_accept")) { _running = !_running; }   // space

        bool s = Input.IsPhysicalKeyPressed(Key.S);
        if (s && !_sDown) { _sim.Step(1); _totalSteps += 1; RefreshDisplay(); }
        _sDown = s;

        bool r = Input.IsPhysicalKeyPressed(Key.R);
        if (r && !_rDown) { _sim.Reset(); _totalSteps = 0; }
        _rDown = r;

        bool d = Input.IsPhysicalKeyPressed(Key.D);
        if (d && !_dDown) { _debug = _debug >= 2 ? -1 : _debug + 1; RefreshDisplay(); }
        _dDown = d;

        if (_running) { _sim.Step(_stepsPerFrame); _totalSteps += _stepsPerFrame; RefreshDisplay(); }

        string view = _debug < 0 ? "lit height" : (_debug == 0 ? "water" : (_debug == 1 ? "sediment" : "flow"));
        _hud.Text = $"erosion lab  steps={_totalSteps}  {(_running ? "RUNNING" : "paused")}  view={view}\n" +
                    $"space=run S=step R=reset D=view   {Engine.GetFramesPerSecond():0} fps";
    }

    private void RefreshDisplay()
    {
        // geometry always follows the eroded bedrock height; debug views additionally false-colour a scalar
        // field (water/sediment/flow) via the ground shader's debug_field overlay so we can SEE where flow goes.
        float[] h = _sim.ReadHeight();
        UploadHeight(h);
        if (_debug < 0)
        {
            _mat.SetShaderParameter("debug_field", 0.0f);
        }
        else
        {
            float[] f = _sim.ReadDebug(_debug);   // 0 water, 1 sediment, 2 flow
            // normalize to ~[0,1] for the false-colour ramp (robust max).
            float mx = 1e-6f;
            for (int i = 0; i < f.Length; i++) { if (f[i] > mx) { mx = f[i]; } }
            for (int i = 0; i < f.Length; i++) { f[i] /= mx; }
            var bytes = new byte[f.Length * 4];
            System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
            var img = Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes);
            _mat.SetShaderParameter("debug_field_tex", ImageTexture.CreateFromImage(img));
            _mat.SetShaderParameter("debug_field", 1.0f);
        }
    }

    public override void _ExitTree() { _sim?.Dispose(); }
}
