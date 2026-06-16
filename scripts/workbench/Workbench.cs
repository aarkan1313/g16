using Godot;
using System;
using WG16.Field;
using WG16.Presenter;

namespace WG16.Workbench;

/// Lab root: hot-reloads params, reseeds, screenshots, shows the HUD, drives the
/// fly/walk camera. No bake stage — the lab generates the base field and draws
/// it. That's the whole product.
public partial class Workbench : Node3D
{
    private FieldCompute? _fc;
    private FieldParams _params = null!;
    private LabTerrain _terrain = null!;
    private Label _hud = null!;
    private FlyCamera _cam = null!;
    private PresentationParams _pres = null!;
    private ulong _lastMtime;
    private ulong _presMtime;
    private double _pollAccum;
    private uint _seedOverride;
    private int _shot;
    private uint _fieldMode;
    private bool _walk;
    private ulong _genMs;
    private bool _polish = true;   // P toggles the polished color treatment
    // Optional one-shot startup screenshot (CLI verification): set via
    // --auto-shot=<path> in the user args; fires once after a warmup delay.
    private string? _autoShotPath;
    private double _autoShotT = -1.0;

    private static readonly string[] ModeNames =
        { "full", "continent", "uplift", "hills", "ridges", "macro base" };

    public override void _Ready()
    {
        _fc = new FieldCompute();
        _params = FieldParams.Load();
        _seedOverride = _params.Seed;
        _lastMtime = FieldParams.ModifiedTime();
        _pres = PresentationParams.Load();
        _presMtime = PresentationParams.ModifiedTime();
        _terrain = GetNode<LabTerrain>("LabTerrain");
        _cam = GetNode<FlyCamera>("Camera");
        _hud = GetNode<Label>("HudLayer/Hud");
        Rebuild();
        ApplyPresentation();
        _terrain.SetPolish(_polish);

        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--auto-shot="))
            {
                _autoShotPath = a.Substring("--auto-shot=".Length);
                _autoShotT = 0.0;
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                GD.Print($"Workbench: auto-shot -> {_autoShotPath}");
                _autoShotT = -1.0;
                GetTree().Quit();
            }
        }

        if (_walk)
        {
            Vector3 pos = _cam.Position;
            _cam.Position = new Vector3(pos.X, _terrain.SampleHeight(pos.X, pos.Z) + _pres.EyeHeightM, pos.Z);
        }

        _pollAccum += delta;
        if (_pollAccum < 0.5) { return; }
        _pollAccum = 0;

        ulong pm = PresentationParams.ModifiedTime();
        if (pm != _presMtime)
        {
            _presMtime = pm;
            _pres = PresentationParams.Load();
            GD.Print("Workbench: presentation params changed -> apply");
            ApplyPresentation();
        }

        ulong m = FieldParams.ModifiedTime();
        if (m == _lastMtime) { return; }
        _lastMtime = m;
        // A bad mid-edit save must not kill the session — keep last good params.
        try
        {
            _params = FieldParams.Load();
        }
        catch (Exception e)
        {
            GD.Print($"Workbench: field_params.json rejected ({e.Message}) — keeping previous params");
            return;
        }
        _seedOverride = _params.Seed;
        GD.Print("Workbench: params changed on disk -> rebuild");
        Rebuild();
    }

    public override void _UnhandledKeyInput(InputEvent ev)
    {
        if (ev is not InputEventKey k || !k.Pressed || k.Echo) { return; }

        if (k.Keycode == Key.R)
        {
            _seedOverride = (uint)Random.Shared.Next();
            GD.Print($"Workbench: reseed -> {_seedOverride}");
            Rebuild();
        }

        if (k.Keycode == Key.F12)
        {
            string dir = "d:/tmp/wg16_shots";
            System.IO.Directory.CreateDirectory(dir);
            string path = $"{dir}/shot_{Time.GetTicksMsec()}_{_shot++}.png";
            GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"Workbench: screenshot -> {path}");
        }

        if (k.Keycode >= Key.Key0 && k.Keycode <= Key.Key5)
        {
            _fieldMode = (uint)(k.Keycode - Key.Key0);
            GD.Print($"Workbench: field mode {_fieldMode} ({ModeNames[(int)_fieldMode]})");
            Rebuild();
        }

        if (k.Keycode == Key.G)
        {
            _walk = !_walk;
            _cam.Walk = _walk;
            GD.Print($"Workbench: walk mode {(_walk ? "ON" : "off")}");
            _hud.Text = HudText();
        }

        if (k.Keycode == Key.P)
        {
            _polish = !_polish;
            _terrain.SetPolish(_polish);
            GD.Print($"Workbench: polish {(_polish ? "ON" : "off")}");
            _hud.Text = HudText();
        }

        if (k.Keycode == Key.Bracketleft || k.Keycode == Key.Bracketright)
        {
            float step = k.Keycode == Key.Bracketright ? 3f : -3f;
            _pres = _pres with { SunElevationDeg = Mathf.Clamp(_pres.SunElevationDeg + step, 4f, 80f) };
            ApplyPresentation();
            GD.Print($"Workbench: sun elevation {_pres.SunElevationDeg} deg (runtime only — copy into presentation_params.json to keep)");
        }
    }

    private FieldParams CurrentParams() => _params with { Seed = _seedOverride };

    private void ApplyPresentation()
    {
        var sun = GetNode<DirectionalLight3D>("Sun");
        sun.RotationDegrees = new Vector3(-_pres.SunElevationDeg, _pres.SunAzimuthDeg, 0f);
        sun.LightEnergy = _pres.SunEnergy;
        var env = GetNode<WorldEnvironment>("Env").Environment;
        env.FogDensity = _pres.FogDensity;
        _cam.WalkSpeed = _pres.WalkSpeedMs;
        _cam.WalkRunMult = _pres.WalkRunMult;
    }

    private void Rebuild()
    {
        FieldCompute fc = _fc ?? throw new InvalidOperationException("FieldCompute not initialized.");
        ulong t0 = Time.GetTicksMsec();
        _terrain.Rebuild(fc, CurrentParams(), _fieldMode);
        _genMs = Time.GetTicksMsec() - t0;
        _hud.Text = HudText();
    }

    private string HudText()
    {
        FieldParams p = CurrentParams();
        double nsPerSample = _genMs * 1_000_000.0 / ((double)p.HeightmapRes * p.HeightmapRes);
        return $"seed {p.Seed}  view {ModeNames[(int)_fieldMode]}  amp {p.AmplitudeM}m  ridge {p.RidgeAmp}m  " +
               $"res {p.HeightmapRes} ({p.Spacing}m/texel)\n" +
               $"gen {_genMs} ms (~{nsPerSample:F1} ns/sample incl. readback)\n" +
               $"0-5 view | R reseed | G walk {(_walk ? "ON" : "off")} | P polish {(_polish ? "ON" : "off")} | [/] sun | F12 shot | LMB/RMB+WASD fly";
    }

    public override void _ExitTree()
    {
        _fc?.Dispose();
    }
}
