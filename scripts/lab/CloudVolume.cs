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

    // Set by Stage 3 once the sky material exists; null-safe until then.
    private ShaderMaterial? _skyMat;

    public override void _Ready()
    {
        _p = CloudParams.Load();
        BakeResources();
    }

    /// One-time bake of the noise volumes + weather field (cheap; see Stage 1).
    public void BakeResources()
    {
        _noise ??= new CloudNoiseCompute();
        (_shapeTex, _detailTex) = _noise.Bake();
        _weatherTex = CloudWeather.Bake();
        GD.Print("CloudVolume: resources baked (shape/detail volumes + weather field)");
    }

    public ImageTexture3D? ShapeTex => _shapeTex;
    public ImageTexture3D? DetailTex => _detailTex;
    public ImageTexture? WeatherTex => _weatherTex;
    public CloudParams Params => _p;
    public bool Enabled => _enabled;

    // ---- public knob interface (UI → here only) -------------------------------
    public void SetKnobBool(string knob, bool on)
    {
        if (knob == "enabled") { _enabled = on; PushSky("cloud_enabled", on); }
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
