using Godot;

namespace WG16.Lab;

/// DEDICATED god-ray test scene controller. Forces the screen-space god-ray condition in TOTAL
/// isolation — no clouds compute, no mood system, no preset/CLI ordering. Builds in code:
///   • a bright SUN DISC drawn by a simple sky shader (guaranteed bright source toward the sun),
///   • solid OCCLUDER pillars between the spawn camera and the sun (the "clouds" that break beams),
///   • the screen-space god-ray quad (shaders/godray_screen.gdshader) on a fullscreen quad,
///   • a live HUD printing sun-screen-UV / gate / align / behind / exposure so we SEE the math.
/// Camera starts already aimed at the sun. Fly with RMB-look + WASD. Keys: [/] exposure, ;/' threshold,
/// -/= decay, M cycles debug_mode (0 normal, 1 mask, 2 sun marker).
///
/// Once beams are confirmed here, the working shader params port straight back to GodRaysScreen.
public partial class GodRayTest : Node3D
{
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Label _hud = null!;

    // Sun aim (fixed, known): azimuth + elevation in degrees. Low elevation = dramatic rake.
    private const float SunAzimuthDeg = 0f;     // sun toward -Z (world), straight ahead of the start camera
    private const float SunElevationDeg = 18f;  // low-ish

    private int _debug;
    private float _exposure = 0.5f, _threshold = 0.6f, _decay = 0.96f, _density = 0.9f, _weight = 0.06f;

    public override void _Ready()
    {
        // ---- Environment: sky background, so there's a bright sky to scatter (sky is in the frame).
        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.35f, 0.55f, 0.85f),
            SkyHorizonColor = new Color(0.75f, 0.82f, 0.92f),
            SunAngleMax = 12f,        // big bright sun disc in the sky toward the directional light
            SunCurve = 0.08f,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.5f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Name = "Env", Environment = env });

        // ---- Sun: aim it at the known azimuth/elevation. DirectionalLight shines along -Z, so to put
        // the sun toward -Z at SunElevation above the horizon, pitch down by (90-elev)? No — simplest:
        // rotate so the light DIRECTION points down-and-forward; the SOURCE (sun disc) is then up-back.
        _sun = new DirectionalLight3D { Name = "Sun", LightEnergy = 1.6f, ShadowEnabled = true };
        // RotationDegrees: X = -elevation puts the light pointing slightly up?  We set it so toSun
        // (= +Basis.Z) has the intended azimuth/elevation. Build the basis directly from the aim.
        _sun.GlobalTransform = SunTransformFromAim(SunAzimuthDeg, SunElevationDeg);
        AddChild(_sun);

        // ---- Occluders: solid dark slabs between the camera start (origin-ish) and the sun, at a
        // height where they cross the view-to-sun line. These are the guaranteed dark occluders that
        // carve the beams (stand in for clouds). Spread a few across the sky-ward direction.
        var occMat = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.05f, 0.06f), Roughness = 1f };
        Vector3 toSun = (-_sun.GlobalTransform.Basis.Z).Normalized();   // NOTE verify sign at runtime via HUD
        toSun = _sun.GlobalTransform.Basis.Z.Normalized();              // +Basis.Z convention (matches GodRaysScreen)
        for (int i = 0; i < 5; i++)
        {
            var slab = new MeshInstance3D
            {
                Name = $"Occluder{i}",
                Mesh = new BoxMesh { Size = new Vector3(18, 10, 2) },
                MaterialOverride = occMat,
            };
            // place them out toward the sun, fanned horizontally, at mid height so they sit between
            // the camera and the sun disc.
            float along = 60f + i * 22f;
            float side = (i - 2) * 14f;
            slab.Position = new Vector3(side, 40f + i * 6f, -along);   // -Z = toward the sun (azimuth 0)
            AddChild(slab);
        }

        // ---- Camera: start looking toward the sun (-Z), pitched up to the sun elevation. Simple
        // built-in look/move below (RMB-drag + WASD) — no external FlyCamera reattach (SetScript
        // timing crashes when setting its exports synchronously).
        _cam = new Camera3D { Name = "Camera", Far = 50000f };
        _cam.Position = new Vector3(0, 35, 40);
        _cam.RotationDegrees = new Vector3(SunElevationDeg, 0f, 0f);   // pitch up toward the low sun, yaw 0 = look -Z
        AddChild(_cam);

        // ---- God-ray fullscreen quad (the system under test).
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_screen.gdshader") };
        _mat.RenderPriority = 127;
        PushParams();
        _quad = new MeshInstance3D
        {
            Name = "GodRayQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
        };
        AddChild(_quad);

        // ---- HUD.
        var layer = new CanvasLayer { Name = "Hud" };
        _hud = new Label { Position = new Vector2(12, 12) };
        _hud.AddThemeFontSizeOverride("font_size", 18);
        layer.AddChild(_hud);
        AddChild(layer);

        GD.Print("[godraytest] ready — fly RMB+WASD; [ ] exposure, ; ' threshold, - = decay, M debug");

        // optional one-shot capture for headless verification: --shot=PATH (auto-quits after ~2s).
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--shot=")) { _shotPath = a.Substring("--shot=".Length); _shotT = 0.0; }
            else if (a.StartsWith("--dbg=")) { int.TryParse(a.Substring("--dbg=".Length), out _debug); PushParams(); }
        }
    }

    private string? _shotPath;
    private double _shotT = -1.0;

    /// Build a sun transform whose +Basis.Z (our "toSun") points at the given azimuth/elevation.
    private static Transform3D SunTransformFromAim(float azDeg, float elevDeg)
    {
        float az = Mathf.DegToRad(azDeg), el = Mathf.DegToRad(elevDeg);
        // toSun in world: azimuth 0 = -Z, increasing toward +X; elevation tilts up (+Y).
        Vector3 toSun = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), -Mathf.Cos(az) * Mathf.Cos(el)).Normalized();
        // Build a basis with +Z = toSun (so Basis.Z == toSun, matching the GodRaysScreen convention).
        Vector3 z = toSun;
        Vector3 x = Vector3.Up.Cross(z).Normalized();
        if (x.LengthSquared() < 1e-4f) { x = Vector3.Right; }
        Vector3 y = z.Cross(x).Normalized();
        return new Transform3D(new Basis(x, y, z), Vector3.Zero);
    }

    private void PushParams()
    {
        _mat.SetShaderParameter("exposure", _exposure);
        _mat.SetShaderParameter("mask_threshold", _threshold);
        _mat.SetShaderParameter("decay", _decay);
        _mat.SetShaderParameter("density", _density);
        _mat.SetShaderParameter("weight", _weight);
        _mat.SetShaderParameter("debug_mode", _debug);
    }

    public override void _Process(double delta)
    {
        // WASD + QE fly (camera-relative). Shift = boost.
        Vector3 mv = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) { mv.Z -= 1; }
        if (Input.IsKeyPressed(Key.S)) { mv.Z += 1; }
        if (Input.IsKeyPressed(Key.A)) { mv.X -= 1; }
        if (Input.IsKeyPressed(Key.D)) { mv.X += 1; }
        if (Input.IsKeyPressed(Key.E)) { mv.Y += 1; }
        if (Input.IsKeyPressed(Key.Q)) { mv.Y -= 1; }
        if (mv != Vector3.Zero)
        {
            float sp = (Input.IsKeyPressed(Key.Shift) ? 120f : 30f) * (float)delta;
            _cam.Translate(mv.Normalized() * sp);
        }

        // Compute sun screen-UV + gate exactly as GodRaysScreen does (this scene IS the test of it).
        Vector3 toSun = _sun.GlobalTransform.Basis.Z.Normalized();
        Vector3 sunWorld = _cam.GlobalPosition + toSun * 100000f;
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
        float align = fwd.Dot(toSun);
        bool behind = _cam.IsPositionBehind(sunWorld);
        float gate = 0f;
        Vector2 uv = new Vector2(0.5f, 0.5f);
        if (!behind && align > 0f)
        {
            Vector2 px = _cam.UnprojectPosition(sunWorld);
            Vector2 vp = _cam.GetViewport().GetVisibleRect().Size;
            uv = px / vp;
            const float edge = 0.15f;
            float fx = Mathf.Clamp(Mathf.Min(uv.X, 1f - uv.X) / edge, 0f, 1f);
            float fy = Mathf.Clamp(Mathf.Min(uv.Y, 1f - uv.Y) / edge, 0f, 1f);
            gate = fx * fy * Mathf.Clamp(align, 0f, 1f);
        }
        _mat.SetShaderParameter("sun_screen_uv", uv);
        _mat.SetShaderParameter("sun_gate", gate);

        _hud.Text =
            $"GOD RAY TEST   debug_mode={_debug} (M to cycle)\n" +
            $"toSun = {toSun}   (azimuth {SunAzimuthDeg}, elev {SunElevationDeg})\n" +
            $"sun screen uv = {uv}   gate = {gate:F2}\n" +
            $"align(view·sun) = {align:F2}   behind = {behind}\n" +
            $"exposure={_exposure:F2} [ ]   threshold={_threshold:F2} ; '   decay={_decay:F2} - =\n" +
            $"density={_density:F2}   weight={_weight:F2}\n" +
            (gate <= 0.001f ? ">>> GATE 0: sun off-screen or behind — turn toward the sun (it's a bright disc) <<<"
                            : ">>> gate ON — beams should fan from the sun; if not, raise exposure / lower threshold <<<");

        // --shot=PATH: capture the frame (HUD included) after a warm-up, then quit.
        if (_shotT >= 0.0 && _shotPath != null)
        {
            _shotT += delta;
            if (_shotT > 2.0)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_shotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_shotPath);
                GD.Print($"[godraytest] shot -> {_shotPath}");
                _shotT = -1.0;
                GetTree().Quit();
            }
        }
    }

    private Vector2 _look;
    public override void _UnhandledInput(InputEvent ev)
    {
        // RMB/LMB-drag look.
        if (ev is InputEventMouseMotion mm &&
            (Input.IsMouseButtonPressed(MouseButton.Right) || Input.IsMouseButtonPressed(MouseButton.Left)))
        {
            _look += mm.Relative * 0.3f;
            _cam.RotationDegrees = new Vector3(Mathf.Clamp(SunElevationDeg - _look.Y, -89f, 89f), -_look.X, 0f);
        }
        if (ev is not InputEventKey k || !k.Pressed) { return; }
        switch (k.Keycode)
        {
            case Key.Bracketleft:  _exposure = Mathf.Max(0f, _exposure - 0.05f); break;
            case Key.Bracketright: _exposure = Mathf.Min(3f, _exposure + 0.05f); break;
            case Key.Semicolon:    _threshold = Mathf.Max(0f, _threshold - 0.05f); break;
            case Key.Apostrophe:   _threshold = Mathf.Min(2f, _threshold + 0.05f); break;
            case Key.Minus:        _decay = Mathf.Max(0.5f, _decay - 0.01f); break;
            case Key.Equal:        _decay = Mathf.Min(1f, _decay + 0.01f); break;
            case Key.M:            _debug = (_debug + 1) % 3; break;
            default: return;
        }
        PushParams();
    }
}
