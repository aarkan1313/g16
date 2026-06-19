using Godot;

namespace WG16.Lab;

/// God rays — CANONICAL volumetric base (rebuild 2026-06-19). Shafts are the DirectionalLight's
/// REAL shadow scattering through global volumetric fog. The clouds occlude the sun via a
/// shadow-casting quad (cloud_shadow_caster.gdshader) alpha-cut by the cloud transmittance map, so
/// cloud gaps become bright shafts and cloud bodies cast shadow on BOTH terrain and fog. NO custom
/// fog-emission shader, NO FogVolume-as-light. See memory godray-emission-vs-albedo-rootcause.
///
/// Owns: the caster MeshInstance3D (a quad oriented ⟂ to the sun, high up, cast_shadow=ShadowsOnly
/// so it's invisible to the camera). Configures the Environment volfog (low density, forward
/// anisotropy, no ambient inject) + the sun (shadow ON, high volumetric_fog_energy) while active,
/// restoring prior values on disable. Consumes the cloud shadow texture + region + sun from the UI;
/// never touches cloud internals.
public partial class GodRaysVolumetric : Node3D
{
    private MeshInstance3D _caster = null!;
    private ShaderMaterial _casterMat = null!;
    private Godot.Environment? _env;
    private DirectionalLight3D? _sun;

    private bool _on;
    private bool _stateSaved;
    private bool _savedVolfog, _savedSunShadow;
    private float _savedLength, _savedDensity, _savedAnis, _savedAmbInject, _savedSunVolEnergy;
    private Color _savedAlbedo;

    // Canonical outdoor tuning (memory godray-emission-vs-albedo-rootcause):
    private const float EnvDensity = 0.02f;        // LOW — thin medium, see through it; only shafts pop
    private const float EnvLength = 3000f;         // froxel range; tighter = sharper near shafts
    private const float EnvAnisotropy = 0.85f;     // forward scatter → air blazes toward the sun
    private const float EnvAmbientInject = 0.0f;   // keep lit/shadow contrast (no ambient fill)
    private static readonly Color EnvAlbedo = new Color(1f, 0.97f, 0.92f);  // warm single-scatter
    private const float SunVolEnergy = 48f;        // shaft brightness = this × the thin medium
    private const float CasterAltitude = 2500f;    // quad height above terrain mid (above cloud base)
    private const float CasterSize = 16000f;       // covers the whole shadow region with sun-angle slack

    public GodRaysVolumetric()
    {
        _casterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_shadow_caster.gdshader") };
        var mesh = new PlaneMesh { Size = new Vector2(CasterSize, CasterSize) };
        _caster = new MeshInstance3D
        {
            Name = "CloudShadowCaster",
            Mesh = mesh,
            MaterialOverride = _casterMat,
            // SHADOWS_ONLY: never drawn to the camera, only writes the directional shadow atlas.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
            Visible = false,   // default OFF
        };
        AddChild(_caster);
    }

    public void Attach(Godot.Environment env, DirectionalLight3D sun) { _env = env; _sun = sun; }

    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _casterMat.SetShaderParameter("cloud_shadow_tex", tex); }
        _casterMat.SetShaderParameter("shadow_region", region);
    }

    /// Orient the caster quad perpendicular to the sun and position it high along the sun ray, so
    /// its cloud-cut shadow projects straight down the sun direction onto terrain + fog.
    public void SetSunDir(Vector3 toSun)
    {
        if (toSun.LengthSquared() < 1e-4f) { return; }
        toSun = toSun.Normalized();
        _caster.GlobalPosition = new Vector3(0f, CasterAltitude, 0f);
        // PlaneMesh normal is +Y; rotate so +Y points toward the sun (quad faces down the sun ray).
        Vector3 up = Vector3.Up;
        if (Mathf.Abs(up.Dot(toSun)) > 0.999f) { _caster.GlobalRotation = Vector3.Zero; }
        else
        {
            Vector3 axis = up.Cross(toSun).Normalized();
            float ang = Mathf.Acos(Mathf.Clamp(up.Dot(toSun), -1f, 1f));
            _caster.GlobalTransform = new Transform3D(new Basis(axis, ang), _caster.GlobalPosition);
        }
    }

    /// UI "god ray strength" → the caster cut threshold. Higher strength → lower threshold → more of
    /// the sky casts shadow (denser, more dramatic shafts). Clamped to a sane band.
    public void SetStrength(float s) => _casterMat.SetShaderParameter("cut_threshold", Mathf.Clamp(0.5f + (s - 4f) * 0.05f, 0.05f, 0.95f));

    public bool On => _on;

    public void SetEnabled(bool on)
    {
        _on = on;
        _caster.Visible = on;
        if (_env == null) { return; }
        if (on)
        {
            if (!_stateSaved)
            {
                _savedVolfog = _env.VolumetricFogEnabled;
                _savedLength = _env.VolumetricFogLength;
                _savedDensity = _env.VolumetricFogDensity;
                _savedAlbedo = _env.VolumetricFogAlbedo;
                _savedAnis = _env.VolumetricFogAnisotropy;
                _savedAmbInject = _env.VolumetricFogAmbientInject;
                if (_sun != null) { _savedSunShadow = _sun.ShadowEnabled; _savedSunVolEnergy = _sun.LightVolumetricFogEnergy; }
                _stateSaved = true;
            }
            _env.VolumetricFogEnabled = true;
            _env.VolumetricFogLength = EnvLength;
            _env.VolumetricFogDensity = EnvDensity;
            _env.VolumetricFogAlbedo = EnvAlbedo;
            _env.VolumetricFogAnisotropy = EnvAnisotropy;
            _env.VolumetricFogAmbientInject = EnvAmbientInject;
            if (_sun != null) { _sun.ShadowEnabled = true; _sun.LightVolumetricFogEnergy = SunVolEnergy; }
        }
        else if (_stateSaved)
        {
            _env.VolumetricFogEnabled = _savedVolfog;
            _env.VolumetricFogLength = _savedLength;
            _env.VolumetricFogDensity = _savedDensity;
            _env.VolumetricFogAlbedo = _savedAlbedo;
            _env.VolumetricFogAnisotropy = _savedAnis;
            _env.VolumetricFogAmbientInject = _savedAmbInject;
            if (_sun != null) { _sun.ShadowEnabled = _savedSunShadow; _sun.LightVolumetricFogEnergy = _savedSunVolEnergy; }
            _stateSaved = false;
        }
    }
}
