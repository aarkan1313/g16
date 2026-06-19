using Godot;

namespace WG16.Lab;

/// God rays — CANONICAL volumetric base (rebuild 2026-06-19, Option C: per-froxel cloud-shadow).
/// Shafts are the DirectionalLight's REAL shadowed in-scatter through global volumetric fog, GATED
/// per-froxel by the cloud transmittance map (sampled ground-projected along the sun ray). Cloud
/// gaps → bright shafts, cloud bodies → dim air; aligned with the terrain's ground cloud-shadows at
/// every altitude because it uses the SAME map + slant. See memory godray-emission-vs-albedo-rootcause.
///
/// Owns ONE WORLD-shape FogVolume running shaders/godray_fog.gdshader (the WORLD shape fills the env
/// froxel grid automatically — no Box, no camera-follow, so the viewer is never inside a uniform slab).
/// Configures the Environment volfog (low density base, forward anisotropy, no ambient inject) + the
/// sun (shadow ON, high volumetric_fog_energy) while active, restoring prior values on disable.
/// Consumes the cloud shadow texture + region + ground height + sun dir from the UI; never touches
/// cloud internals.
///
/// NOTE on the earlier caster-quad approach (deleted): a flat shadow-caster quad could only inject ONE
/// altitude slice of the cloud shadow and, for a top-down ground-referenced map, floated the shadow
/// ~10 km off the ground cloud-shadows at low sun (2026-06-19 audit). Per-froxel sampling here is the
/// geometrically correct fix — the shadow is computed at every froxel's own altitude.
public partial class GodRaysVolumetric : Node3D
{
    private FogVolume _vol = null!;
    private ShaderMaterial _mat = null!;
    private Godot.Environment? _env;
    private DirectionalLight3D? _sun;

    private bool _on;
    private bool _stateSaved;
    private bool _savedVolfog, _savedSunShadow;
    private float _savedLength, _savedDensity, _savedAnis, _savedAmbInject, _savedSunVolEnergy;
    private Color _savedAlbedo;

    // Canonical outdoor tuning (memory godray-emission-vs-albedo-rootcause):
    private const float EnvDensity = 0.0f;         // the WORLD FogVolume supplies the medium; env base off
    private static readonly Color EnvAlbedo = new Color(0f, 0f, 0f);   // env base scatters nothing on its own
    // Froxel range. Godot spreads a fixed froxel-depth count over this length, so LONGER = coarser
    // slices = mushier/banded shafts (2026-06-19 audit). 2000 m keeps slices fine while reaching valley
    // distance; well inside the scene's 8000 m directional shadow cascade.
    private const float EnvLength = 2000f;
    private const float EnvAnisotropy = 0.85f;     // forward scatter → air blazes toward the sun
    private const float EnvAmbientInject = 0.0f;   // keep lit/shadow contrast (no ambient fill → shafts read)
    // Shaft BRIGHTNESS = the sun's light_volumetric_fog_energy = UI strength × this (a LINEAR engine
    // multiplier on the in-scatter). Driving brightness HERE — not via a clamped ALBEDO multiply —
    // keeps the gap/shadow contrast intact at any strength (the earlier ALBEDO-clamp bug killed it).
    // SANE energy scale: the froxel fog is a SOFT base only (crisp beams come from the screen-space
    // layer). energy 84 (the earlier 7×12) whitewashed the whole frame — outdoor froxel fog blows out
    // fast (Godot docs: "open outdoor areas tend to look hazy"). Keep the multiplier low.
    private const float EnergyPerStrength = 1.0f;  // strength 4 (default) → energy 4; strength 12 → 12
    private float _strength = 4f;                  // UI "god ray strength" (lab_controls default)

    public GodRaysVolumetric()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_fog.gdshader") };
        _vol = new FogVolume
        {
            Name = "GodRayFog",
            // WORLD shape: the shader runs over the WHOLE env froxel grid automatically — no Box, no
            // camera-follow. (The old camera-centered Box put the viewer inside a uniform slab = flat haze.)
            Shape = RenderingServer.FogVolumeShape.World,
            Material = _mat,
            Visible = false,   // default OFF
        };
        AddChild(_vol);
    }

    public void Attach(Godot.Environment env, DirectionalLight3D sun) { _env = env; _sun = sun; }

    /// Bind the cloud shadow map (Texture2Drd from CloudVolume) + its world footprint.
    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _mat.SetShaderParameter("cloud_shadow_tex", tex); }
        _mat.SetShaderParameter("shadow_region", region);
    }

    /// The cloud-bake ground reference (CloudVolume._groundHeight = terrain mid). The per-froxel
    /// shadow projection unshifts by (froxel.y − ground_y) along the sun ray to this plane.
    public void SetGroundHeight(float y) => _mat.SetShaderParameter("ground_y", y);

    /// dir = direction TOWARD the sun (same +Basis.Z convention as CloudVolume.SetSun). Drives the
    /// per-froxel ground-projection. Just a material param — no node transform, no in-tree requirement.
    public void SetSunDir(Vector3 toSun) { if (toSun.LengthSquared() > 1e-4f) { _mat.SetShaderParameter("sun_dir", toSun.Normalized()); } }

    /// UI "god ray strength" → shaft BRIGHTNESS via the sun's volumetric fog energy (linear, no
    /// contrast clipping). Live-applies if god rays are on and the sun is bound.
    public void SetStrength(float s)
    {
        _strength = Mathf.Max(0f, s);
        if (_on && _sun != null) { _sun.LightVolumetricFogEnergy = _strength * EnergyPerStrength; }
    }

    public bool On => _on;

    public void SetEnabled(bool on)
    {
        _on = on;
        _vol.Visible = on;
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
            // Env froxel grid: ON, tight length (sharper), base density/albedo OFF (the WORLD FogVolume
            // supplies the medium), forward anisotropy + no ambient inject (preserve shaft contrast).
            _env.VolumetricFogEnabled = true;
            _env.VolumetricFogLength = EnvLength;
            _env.VolumetricFogDensity = EnvDensity;
            _env.VolumetricFogAlbedo = EnvAlbedo;
            _env.VolumetricFogAnisotropy = EnvAnisotropy;
            _env.VolumetricFogAmbientInject = EnvAmbientInject;
            // THE SUN: shafts ARE its shadowed in-scatter. Shadow ON (no shadow → no shaft) + set its
            // fog scatter energy from the UI strength (linear shaft brightness) so the gaps blaze.
            if (_sun != null) { _sun.ShadowEnabled = true; _sun.LightVolumetricFogEnergy = _strength * EnergyPerStrength; }
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
