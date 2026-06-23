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
            var ep = new ErosionParams { Res = _res, CellSize = _cell };
            // CLI param overrides for cheap isolation/tuning: --sm= --sn= --maxerode= --erode= --rain= --evap=
            // --capacity= --deposit= --flowexp= --accumrate= --talus= --talusrate=
            foreach (string a in OS.GetCmdlineUserArgs())
            {
                if (a.StartsWith("--sm=")) { ep.StreamM = a.Substring(5).ToFloat(); }
                else if (a.StartsWith("--sn=")) { ep.StreamN = a.Substring(5).ToFloat(); }
                else if (a.StartsWith("--maxerode=")) { ep.MaxErode = a.Substring(11).ToFloat(); }
                else if (a.StartsWith("--erode=")) { ep.Erode = a.Substring(8).ToFloat(); }
                else if (a.StartsWith("--rain=")) { ep.Rain = a.Substring(7).ToFloat(); }
                else if (a.StartsWith("--evap=")) { ep.Evaporate = a.Substring(7).ToFloat(); }
                else if (a.StartsWith("--capacity=")) { ep.Capacity = a.Substring(11).ToFloat(); }
                else if (a.StartsWith("--deposit=")) { ep.Deposit = a.Substring(10).ToFloat(); }
                else if (a.StartsWith("--flowexp=")) { ep.FlowExp = a.Substring(10).ToFloat(); }
                else if (a.StartsWith("--accumrate=")) { ep.AccumRate = a.Substring(12).ToFloat(); }
                else if (a.StartsWith("--talus=")) { ep.TalusAngle = a.Substring(8).ToFloat(); }
                else if (a.StartsWith("--talusrate=")) { ep.TalusRate = a.Substring(12).ToFloat(); }
            }
            _sim = new ErosionSim(_res) { Params = ep };
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
                    // flow_accum dynamic range: channels concentrate drainage, so max should be >> mean.
                    var (amax, amean) = _sim.AccumStats();
                    float aratio = amax / Mathf.Max(amean, 1e-6f);
                    bool drains = aratio > 10f;     // dendritic networks concentrate flow far above the mean
                    bool ok = finite && (hi - lo) < 5000f && smooth && drains;
                    GD.Print($"EROSIONCHECK: {(ok ? "PASS" : "FAIL")}  finite={finite} range=[{lo:F1},{hi:F1}] roughness={rough:F4} (sawtooth if >0.02) flow_accum max={amax:F0} mean={amean:F1} ratio={aratio:F0} (channels if >10) after {steps} steps");
                    _sim.Dispose(); _sim = null!;
                    SetProcess(false);   // stop _Process firing on the disposed sim before the tree tears down
                    GetTree().Quit();
                    return;
                }
                if (a == "--accumtest")
                {
                    // ISOLATE routing convergence: run ONLY accumulation on the pristine base field (no erosion).
                    // If a dendritic network forms here, the routing is sound and the 600-step streaks are an
                    // erosion-feedback artifact. If it stays starbursts, the routing/convergence itself is broken.
                    // count local minima (pits) in the PRISTINE base field — many pits fragment drainage.
                    {
                        float[] hb = _sim.ReadHeight(); int pits = 0;
                        for (int z = 1; z < _res - 1; z++)
                        for (int x = 1; x < _res - 1; x++)
                        {
                            int j = z * _res + x; float v = hb[j]; bool lowest = true;
                            for (int dz = -1; dz <= 1 && lowest; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0) { continue; }
                                if (hb[j + dz * _res + dx] < v) { lowest = false; break; }
                            }
                            if (lowest) { pits++; }
                        }
                        GD.Print($"ACCUMTEST: base field has {pits} local-minimum pits out of {(_res-2)*(_res-2)} interior cells ({100f*pits/((_res-2)*(_res-2)):F2}%)");
                    }
                    DumpHillshade(_sim.ReadHeight(), "accumtest_hillshade.png");
                    _sim.AccumOnly(50);   DumpField(_sim.ReadDebug(3), "accumtest_50.png",   logScale: true);
                    _sim.AccumOnly(450);  DumpField(_sim.ReadDebug(3), "accumtest_500.png",  logScale: true);
                    _sim.AccumOnly(1500); DumpField(_sim.ReadDebug(3), "accumtest_2000.png", logScale: true);
                    var (mx, mn) = _sim.AccumStats();
                    // DISCRIMINATOR: are high-accumulation cells in VALLEYS (low h, correct rivers) or on RIDGES
                    // (high h, inverted routing)? Compare mean height of the top-1% accum cells vs the global mean.
                    float[] hh = _sim.ReadHeight();
                    float[] aa = _sim.ReadDebug(3);
                    int nc = hh.Length;
                    var ord = new int[nc]; for (int q = 0; q < nc; q++) { ord[q] = q; }
                    System.Array.Sort(ord, (p, q) => aa[q].CompareTo(aa[p]));   // descending accum
                    int top = nc / 100;
                    double hAll = 0; foreach (float v in hh) { hAll += v; } hAll /= nc;
                    double hTop = 0; for (int q = 0; q < top; q++) { hTop += hh[ord[q]]; } hTop /= top;
                    GD.Print($"ACCUMTEST: accum max={mx:F0} mean={mn:F1} ratio={mx/Mathf.Max(mn,1e-6f):F0}  " +
                             $"height: global_mean={hAll:F1}  top1%-accum_mean={hTop:F1}  " +
                             $"({(hTop < hAll ? "VALLEYS ok" : "RIDGES — INVERTED!")})");
                    _sim.Dispose(); _sim = null!;
                    SetProcess(false);
                    GetTree().Quit();
                    return;
                }
                if (a == "--riverdump")
                {
                    // Dump full-res PNGs of the drainage skeleton so the river topology can be judged off-screen
                    // (downscaled viewport shots hide thin channels — ground-texture-feedback lesson). Greyscale:
                    // flow_accum log-scaled, channel_mask, and a hillshade of the eroded height.
                    // dump BEFORE erosion to isolate base-field structure from sim-introduced artifacts
                    DumpHillshade(_sim.ReadHeight(), "river_hillshade_step0.png");
                    // accum after enough steps to converge routing but with erosion's height change still tiny:
                    // if THIS already streaks vertically, the routing is biased on the (near-)pristine surface.
                    _sim.Step(40);
                    DumpField(_sim.ReadDebug(3), "river_accum_early.png", logScale: true);
                    _sim.Step(560);
                    DumpField(_sim.ReadDebug(3), "river_accum.png", logScale: true);
                    DumpField(_sim.ReadDebug(4), "river_channel.png", logScale: false);
                    float[] hER = _sim.ReadHeight();
                    DumpHillshade(hER, "river_hillshade.png");
                    DumpHeightHeat(hER, "river_height.png");   // raw height heatmap (no hand-rolled shading)
                    DumpRiverMap(hER, _sim.ReadDebug(3), "river_map.png");   // blue rivers over hillshade (the real read)
                    // ANISOTROPY PROBE: real vertical combing = much higher cell-to-cell roughness ALONG z than x.
                    double rx = 0, rz = 0; long nn = 0;
                    for (int z = 1; z < _res - 1; z++)
                    for (int x = 1; x < _res - 1; x++)
                    {
                        int j = z * _res + x;
                        rx += Mathf.Abs(2f * hER[j] - hER[j - 1] - hER[j + 1]);
                        rz += Mathf.Abs(2f * hER[j] - hER[j - _res] - hER[j + _res]);
                        nn++;
                    }
                    // also probe the ACCUM field anisotropy (channel mask combing lives here, not in height).
                    float[] aER = _sim.ReadDebug(3);
                    double arx = 0, arz = 0;
                    for (int z = 1; z < _res - 1; z++)
                    for (int x = 1; x < _res - 1; x++)
                    {
                        int j = z * _res + x;
                        arx += Mathf.Abs(2f * aER[j] - aER[j - 1] - aER[j + 1]);
                        arz += Mathf.Abs(2f * aER[j] - aER[j - _res] - aER[j + _res]);
                    }
                    GD.Print($"RIVERDUMP: wrote PNGs. HEIGHT anisotropy rough_x={rx / nn:F4} rough_z={rz / nn:F4} " +
                             $"ratio_z/x={rz / Mathf.Max(rx, 1e-9):F2}  |  ACCUM anisotropy rough_x={arx / nn:F2} " +
                             $"rough_z={arz / nn:F2} ratio_z/x={arz / Mathf.Max(arx, 1e-9):F2} (>>1 = vertical comb in accum)");
                    _sim.Dispose(); _sim = null!;
                    SetProcess(false);
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
            // Use the height/slope COLOUR RAMP (not the textured path): the lab binds no material textures,
            // so use_textures=true would sample unbound samplers → black albedo, making erosion hard to read
            // (black surface lit only by slope makes every micro-facet pop). The ramp needs no assets and gives
            // a clean readable height/slope view for judging valleys.
            _mat.SetShaderParameter("use_textures", false);
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
        if (d && !_dDown) { _debug = _debug >= 5 ? -1 : _debug + 1; RefreshDisplay(); }
        _dDown = d;

        if (_running) { _sim.Step(_stepsPerFrame); _totalSteps += _stepsPerFrame; RefreshDisplay(); }

        string view = _debug switch
        {
            < 0 => "lit height", 0 => "water", 1 => "sediment", 2 => "flow",
            3 => "flow_accum (rivers)", 4 => "channel_mask", _ => "water_level",
        };
        _hud.Text = $"erosion lab  steps={_totalSteps}  {(_running ? "RUNNING" : "paused")}  view={view}\n" +
                    $"space=run S=step R=reset D=view (lit→water→sed→flow→accum→channel→wlevel)   {Engine.GetFramesPerSecond():0} fps";
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
            float[] f = _sim.ReadDebug(_debug);   // 0 water,1 sediment,2 flow,3 flow_accum,4 cmask,5 water_level
            if (_debug == 3)
            {
                // flow_accum spans 1..thousands → log-scale so the dendritic skeleton is visible, not just the
                // few trunk cells. log(1+A)/log(1+max) maps the whole river network into [0,1].
                float mx = 1e-6f;
                for (int i = 0; i < f.Length; i++) { if (f[i] > mx) { mx = f[i]; } }
                float lm = Mathf.Log(1f + mx);
                for (int i = 0; i < f.Length; i++) { f[i] = Mathf.Log(1f + f[i]) / Mathf.Max(lm, 1e-6f); }
            }
            else if (_debug == 5)
            {
                // water_level uses -1e9 as the "no standing water" sentinel → show a clean presence mask (1/0).
                for (int i = 0; i < f.Length; i++) { f[i] = f[i] > -1e8f ? 1f : 0f; }
            }
            else
            {
                // normalize to ~[0,1] for the false-colour ramp (robust max).
                float mx = 1e-6f;
                for (int i = 0; i < f.Length; i++) { if (f[i] > mx) { mx = f[i]; } }
                for (int i = 0; i < f.Length; i++) { f[i] /= mx; }
            }
            var bytes = new byte[f.Length * 4];
            System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
            var img = Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes);
            _mat.SetShaderParameter("debug_field_tex", ImageTexture.CreateFromImage(img));
            _mat.SetShaderParameter("debug_field", 1.0f);
        }
    }

    public override void _ExitTree() { _sim?.Dispose(); }

    // --- offline river-topology dumps (--riverdump): full-res greyscale PNGs so thin channels stay visible ---
    private void DumpField(float[] f, string name, bool logScale)
    {
        float mx = 1e-6f;
        for (int i = 0; i < f.Length; i++) { if (f[i] > mx) { mx = f[i]; } }
        float lm = Mathf.Log(1f + mx);
        var img = Image.CreateEmpty(_res, _res, false, Image.Format.Rgb8);
        for (int z = 0; z < _res; z++)
        for (int x = 0; x < _res; x++)
        {
            float v = f[z * _res + x];
            v = logScale ? Mathf.Log(1f + v) / Mathf.Max(lm, 1e-6f) : v / mx;
            img.SetPixel(x, z, new Color(v, v, v));
        }
        img.SavePng(ProjectSettings.GlobalizePath("res://" + name));
    }

    // The real river-network read: hillshade terrain in grey, drainage as blue where log-accum is high.
    private void DumpRiverMap(float[] h, float[] accum, string name)
    {
        float amx = 1e-6f;
        for (int i = 0; i < accum.Length; i++) { if (accum[i] > amx) { amx = accum[i]; } }
        float lm = Mathf.Log(1f + amx);
        Vector3 lightDir = new Vector3(-0.4f, 0.85f, -0.35f).Normalized();
        var img = Image.CreateEmpty(_res, _res, false, Image.Format.Rgb8);
        for (int z = 0; z < _res; z++)
        for (int x = 0; x < _res; x++)
        {
            int xl = Mathf.Max(x - 1, 0), xr = Mathf.Min(x + 1, _res - 1);
            int zt = Mathf.Max(z - 1, 0), zb = Mathf.Min(z + 1, _res - 1);
            Vector3 nrm = new Vector3(-(h[z * _res + xr] - h[z * _res + xl]), 2f * _cell,
                                      -(h[zb * _res + x] - h[zt * _res + x])).Normalized();
            float shade = Mathf.Clamp(nrm.Dot(lightDir), 0f, 1f) * 0.8f + 0.2f;
            float riv = Mathf.Log(1f + accum[z * _res + x]) / Mathf.Max(lm, 1e-6f);
            riv = Mathf.Clamp((riv - 0.25f) / 0.75f, 0f, 1f);   // show the drainage network down to small tributaries
            // lerp terrain grey → river blue by drainage strength
            float r = Mathf.Lerp(shade, 0.05f, riv);
            float g = Mathf.Lerp(shade, 0.25f, riv);
            float b = Mathf.Lerp(shade, 0.95f, riv);
            img.SetPixel(x, z, new Color(r, g, b));
        }
        img.SavePng(ProjectSettings.GlobalizePath("res://" + name));
    }

    private void DumpHeightHeat(float[] h, string name)
    {
        float lo = 1e9f, hi = -1e9f;
        foreach (float v in h) { if (v < lo) { lo = v; } if (v > hi) { hi = v; } }
        float rng = Mathf.Max(hi - lo, 1e-6f);
        var img = Image.CreateEmpty(_res, _res, false, Image.Format.Rgb8);
        for (int z = 0; z < _res; z++)
        for (int x = 0; x < _res; x++)
        {
            float t = (h[z * _res + x] - lo) / rng;
            img.SetPixel(x, z, new Color(t, t, t));
        }
        img.SavePng(ProjectSettings.GlobalizePath("res://" + name));
    }

    private void DumpHillshade(float[] h, string name)
    {
        var img = Image.CreateEmpty(_res, _res, false, Image.Format.Rgb8);
        // simple Lambert hillshade from a NW light so valleys/ridges read as relief.
        Vector3 lightDir = new Vector3(-0.5f, 0.8f, -0.5f).Normalized();
        for (int z = 0; z < _res; z++)
        for (int x = 0; x < _res; x++)
        {
            int xl = Mathf.Max(x - 1, 0), xr = Mathf.Min(x + 1, _res - 1);
            int zt = Mathf.Max(z - 1, 0), zb = Mathf.Min(z + 1, _res - 1);
            float dhx = h[z * _res + xr] - h[z * _res + xl];
            float dhz = h[zb * _res + x] - h[zt * _res + x];
            Vector3 nrm = new Vector3(-dhx, 2f * _cell, -dhz).Normalized();
            float l = Mathf.Clamp(nrm.Dot(lightDir), 0f, 1f) * 0.85f + 0.15f;
            img.SetPixel(x, z, new Color(l, l, l));
        }
        img.SavePng(ProjectSettings.GlobalizePath("res://" + name));
    }
}
