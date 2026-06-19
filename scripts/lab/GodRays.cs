using Godot;

namespace WG16.Lab;

/// God rays — Component A: in-scene, CLOUD-OCCLUDED volumetric shafts.
///
/// Owns ONE FogVolume (shape = World, so it fills the camera's volumetric-fog region
/// automatically — no manual camera-follow) running shaders/godray_fog.gdshader. The
/// shader samples the cloud shadow map (owned by CloudVolume) by world-XZ to gate the
/// emitted light, so the air glows where the sun breaks through a cloud gap.
///
/// Separation of concerns: this module CONSUMES the cloud shadow texture + region +
/// ground height + sun from CloudVolume (passed in by the UI); it never touches cloud
/// internals, the raymarch, or the sky. The Environment is borrowed only to guarantee
/// volumetric fog is enabled (FogVolumes render into the env's froxel grid) and long
/// enough for terrain-scale shafts. Default OFF — opt-in, per the redesign spec.
public partial class GodRays : Node3D
{
    private FogVolume _vol = null!;
    private ShaderMaterial _mat = null!;
    private Godot.Environment? _env;

    // saved env volumetric-fog state, restored when god rays are disabled, so toggling
    // god rays never permanently changes the Light-tab volumetric fog settings.
    private bool _savedVolfog;
    private float _savedLength;
    private bool _stateSaved;
    private bool _on;

    // Volumetric fog froxels are spread over [camera, length]. The scene uses 6000 m for
    // aerial haze, which makes the froxels far too coarse for crisp shafts. While god rays
    // are on we FORCE a much shorter length so froxel resolution concentrates near the
    // camera → sharper shafts (trades distant fog range; restored when god rays turn off).
    private const float GodrayFogLength = 2000f;

    // Build the material + volume in the CONSTRUCTOR (not _Ready): the UI pushes the sun
    // (PushSunToCloud) during its own _Ready, BEFORE this node's deferred AddChild runs
    // _Ready — so the params must exist as soon as `new GodRays()` returns, or SetSun NREs.
    public GodRays()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_fog.gdshader") };
        _vol = new FogVolume
        {
            Name = "GodRayFog",
            Shape = RenderingServer.FogVolumeShape.Box,      // big box around the play area (camera-follow in _Process)
            Size = new Vector3(8000, 3000, 8000),
            Position = new Vector3(0, 500, 0),
            Material = _mat,
            Visible = false,                                 // default OFF
        };
        AddChild(_vol);
    }

    /// Borrow the Environment so we can ensure volumetric fog is live while god rays are on.
    public void Attach(Godot.Environment env) { _env = env; }

    /// Bind the cloud shadow map (Texture2Drd from CloudVolume) + its world footprint.
    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _mat.SetShaderParameter("cloud_shadow_tex", tex); }
        _mat.SetShaderParameter("shadow_region", region);
    }

    /// The plane the cloud shadow map lives on (terrain mid height) — shafts project onto it.
    public void SetGroundHeight(float y) => _mat.SetShaderParameter("shadow_plane_y", y);

    /// dir = direction TOWARD the sun (same +Basis.Z convention as CloudVolume.SetSun).
    public void SetSun(Vector3 dir, Color color)
    {
        _mat.SetShaderParameter("sun_dir", dir.Normalized());
        _mat.SetShaderParameter("sun_color", color);
    }

    public void SetStrength(float s) => _mat.SetShaderParameter("strength", s);
    public void SetHaze(float h) => _mat.SetShaderParameter("haze", h);

    public bool On => _on;

    public void SetEnabled(bool on)
    {
        _on = on;
        if (_vol != null) { _vol.Visible = on; }
        if (_env == null) { return; }
        if (on)
        {
            // ensure the froxel grid exists + reaches far enough; remember prior state.
            if (!_stateSaved)
            {
                _savedVolfog = _env.VolumetricFogEnabled;
                _savedLength = _env.VolumetricFogLength;
                _stateSaved = true;
            }
            _env.VolumetricFogEnabled = true;
            _env.VolumetricFogLength = GodrayFogLength;   // force shorter → sharper shafts (see const)
        }
        else if (_stateSaved)
        {
            _env.VolumetricFogEnabled = _savedVolfog;
            _env.VolumetricFogLength = _savedLength;
            _stateSaved = false;
        }
    }
}
