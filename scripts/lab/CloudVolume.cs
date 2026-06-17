using Godot;

namespace WG16.Lab;

/// Owns the volumetric cloud subsystem: holds the cloud noise volumes + weather
/// field + live params, and (Stage 3+) drives the per-frame raymarch compute into
/// a texture the sky shader composites. Public knob setters are the ONLY surface
/// the UI touches — the raymarch internals stay private (separation of concerns).
///
/// STAGE 2: state + knob interface only (no rendering yet). The bake of the noise
/// volumes + weather happens here so they're ready; the raymarch/sky wiring is
/// added in Stage 3. Knob setters mutate the live params + (when present) push to
/// the sky material, so the Clouds tab drives real state immediately.
public partial class CloudVolume : Node
{
    private CloudNoiseCompute? _noise;
    private ImageTexture3D? _shapeTex, _detailTex;
    private ImageTexture? _weatherTex;
    private CloudParams _p = CloudParams.Defaults();
    private bool _enabled = true;
    private int _debug = 0;

    private ShaderMaterial? _skyMat;
    private Godot.Environment? _env;     // the scene environment we install the sky into
    private Sky? _cloudSky;              // our Sky resource (cloud shader material)
    private Sky? _origSky;               // the procedural sky to restore when clouds off

    /// Wire the subsystem to the scene's Environment. Called by TerrainLabUI once
    /// the scene is ready (keeps CloudVolume from hard-coding scene paths).
    public void Attach(Godot.Environment env)
    {
        _p = CloudParams.Load();
        _env = env;
        _origSky = env.Sky;
        BakeResources();
        BuildSkyMaterial();
        PushAllParams();
        if (_enabled) { InstallCloudSky(); }
    }

    /// One-time bake of the noise volumes + weather field (cheap; see Stage 1).
    public void BakeResources()
    {
        _noise ??= new CloudNoiseCompute();
        (_shapeTex, _detailTex) = _noise.Bake();
        _weatherTex = CloudWeather.Bake();
        GD.Print("CloudVolume: resources baked (shape/detail volumes + weather field)");
    }

    private void BuildSkyMaterial()
    {
        _skyMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_sky.gdshader") };
        _skyMat.SetShaderParameter("shape_tex", _shapeTex);
        _skyMat.SetShaderParameter("detail_tex", _detailTex);
        _skyMat.SetShaderParameter("weather_tex", _weatherTex);
        // Realtime = full per-frame sky update (Stage 3, full quality). Stage 4 will
        // switch this to Incremental for amortization.
        _cloudSky = new Sky { SkyMaterial = _skyMat, ProcessMode = Sky.ProcessModeEnum.Realtime, RadianceSize = Sky.RadianceSizeEnum.Size256 };
    }

    /// Push the full param set to the sky material (startup + after load).
    private void PushAllParams()
    {
        if (_skyMat == null) { return; }
        _skyMat.SetShaderParameter("cloud_enabled", _enabled);
        _skyMat.SetShaderParameter("cloud_coverage", _p.Coverage);
        _skyMat.SetShaderParameter("cloud_density", _p.Density);
        _skyMat.SetShaderParameter("cloud_type", _p.CloudType);
        _skyMat.SetShaderParameter("cloud_altitude", _p.AltitudeM);
        _skyMat.SetShaderParameter("cloud_thickness", _p.ThicknessM);
        _skyMat.SetShaderParameter("cloud_drift_speed", _p.DriftSpeed);
        _skyMat.SetShaderParameter("cloud_drift_dir", _p.DriftDirDeg);
        _skyMat.SetShaderParameter("cloud_hg", _p.HgAniso);
        _skyMat.SetShaderParameter("cloud_powder", _p.Powder);
        _skyMat.SetShaderParameter("cloud_sun_absorb", _p.SunAbsorption);
        _skyMat.SetShaderParameter("cloud_size", _p.Size);
        _skyMat.SetShaderParameter("cloud_detail", _p.Detail);
        _skyMat.SetShaderParameter("cloud_detail_size", _p.DetailSize);
        _skyMat.SetShaderParameter("cloud_edge", _p.Edge);
        _skyMat.SetShaderParameter("cloud_opacity", _p.Opacity);
        _skyMat.SetShaderParameter("cloud_brightness", _p.Brightness);
        _skyMat.SetShaderParameter("cloud_ambient", _p.Ambient);
        _skyMat.SetShaderParameter("cloud_steps", _p.RaymarchSteps);
        _skyMat.SetShaderParameter("cloud_debug", _debug);
    }

    /// Debug view: 0 normal | 1 shell=magenta | 2 raw density. Stored so it survives
    /// being set before Attach (PushAllParams re-applies it).
    public void SetDebug(int mode) { _debug = mode; PushSky("cloud_debug", mode); }

    private void InstallCloudSky() { if (_env != null && _cloudSky != null) { _env.Sky = _cloudSky; } }
    private void RestoreOrigSky() { if (_env != null && _origSky != null) { _env.Sky = _origSky; } }

    public ImageTexture3D? ShapeTex => _shapeTex;
    public ImageTexture3D? DetailTex => _detailTex;
    public ImageTexture? WeatherTex => _weatherTex;
    public CloudParams Params => _p;
    public bool Enabled => _enabled;

    // ---- public knob interface (UI → here only) -------------------------------
    public void SetKnobBool(string knob, bool on)
    {
        if (knob == "enabled")
        {
            _enabled = on;
            PushSky("cloud_enabled", on);
            if (on) { InstallCloudSky(); } else { RestoreOrigSky(); }
        }
    }

    public void SetKnobInt(string knob, int v)
    {
        switch (knob)
        {
            case "raymarch_steps":   _p = _p with { RaymarchSteps = v }; PushSky("cloud_steps", v); break;
            case "temporal_frames":  _p = _p with { TemporalFrames = v }; break;
        }
    }

    public void SetKnob(string knob, float v)
    {
        switch (knob)
        {
            case "coverage":        _p = _p with { Coverage = v };       PushSky("cloud_coverage", v); break;
            case "density":         _p = _p with { Density = v };        PushSky("cloud_density", v); break;
            case "cloud_type":      _p = _p with { CloudType = v };      PushSky("cloud_type", v); break;
            case "altitude_m":      _p = _p with { AltitudeM = v };      PushSky("cloud_altitude", v); break;
            case "thickness_m":     _p = _p with { ThicknessM = v };     PushSky("cloud_thickness", v); break;
            case "drift_speed":     _p = _p with { DriftSpeed = v };     PushSky("cloud_drift_speed", v); break;
            case "drift_dir_deg":   _p = _p with { DriftDirDeg = v };    PushSky("cloud_drift_dir", v); break;
            case "hg_aniso":        _p = _p with { HgAniso = v };        PushSky("cloud_hg", v); break;
            case "powder":          _p = _p with { Powder = v };         PushSky("cloud_powder", v); break;
            case "sun_absorption":  _p = _p with { SunAbsorption = v };  PushSky("cloud_sun_absorb", v); break;
            case "size":            _p = _p with { Size = v };           PushSky("cloud_size", v); break;
            case "detail":          _p = _p with { Detail = v };         PushSky("cloud_detail", v); break;
            case "detail_size":     _p = _p with { DetailSize = v };     PushSky("cloud_detail_size", v); break;
            case "edge":            _p = _p with { Edge = v };           PushSky("cloud_edge", v); break;
            case "opacity":         _p = _p with { Opacity = v };        PushSky("cloud_opacity", v); break;
            case "brightness":      _p = _p with { Brightness = v };     PushSky("cloud_brightness", v); break;
            case "ambient":         _p = _p with { Ambient = v };        PushSky("cloud_ambient", v); break;
            case "update_res_scale": _p = _p with { UpdateResScale = v }; break;   // perf; read by dispatch
            case "shadow_strength": PushSky("cloud_shadow_strength", v); break;    // wired to terrain in Stage 5
        }
    }

    private void PushSky(string uniform, Variant value)
    {
        _skyMat?.SetShaderParameter(uniform, value);
    }

    public override void _ExitTree() => _noise?.Dispose();
}
