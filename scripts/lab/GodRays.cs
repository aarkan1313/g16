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
    private Camera3D? _cam;
    private DirectionalLight3D? _sun;   // CANONICAL: shafts are the sun's SHADOWED in-scatter — we
                                        // need the sun's shadow ON + a high volumetric_fog_energy.

    // saved env + sun state, restored when god rays are disabled, so toggling never permanently
    // changes the Light-tab volumetric fog / sun settings.
    private bool _savedVolfog;
    private float _savedLength, _savedDensity, _savedAnis, _savedAmbInject;
    private Color _savedAlbedo;
    private bool _savedSunShadow;
    private float _savedSunVolEnergy;
    private bool _stateSaved;
    private bool _on;

    // Volumetric fog froxels are spread over [camera, length]. The scene uses 6000 m for aerial
    // haze → froxels too coarse for shafts. Force a shorter length so froxel resolution
    // concentrates near the camera → sharper shafts (restored when god rays turn off).
    private const float GodrayFogLength = 3000f;

    // Canonical outdoor god-ray env tuning (see godray_fog.gdshader header + research):
    //  - the FogVolume (WORLD shape) supplies the density/albedo, so the env BASE density stays ~0
    //    (no separate uniform slab to wash the shafts) — albedo black for the same reason.
    //  - anisotropy HIGH → forward-scatter → the air blazes toward the sun (the glow shafts grow from).
    //  - ambient_inject 0 → shadowed froxels stay dark → preserves the lit/shadow CONTRAST = shafts.
    private const float GodrayBaseDensity = 0.0f;      // FogVolume provides density; env base off
    private static readonly Color GodrayBaseAlbedo = new Color(0f, 0f, 0f);
    private const float GodrayAnisotropy = 0.85f;      // strong forward scatter
    private const float GodrayAmbientInject = 0.0f;    // keep shadow contrast (no ambient fill)
    // The sun's per-light fog scatter must be cranked WAY up — the shaft brightness is this × the
    // thin medium. Docs cite a 200–5000 useful range; start high so shafts read at low density.
    private const float GodraySunVolEnergy = 64.0f;

    // Build the material + volume in the CONSTRUCTOR (not _Ready): the UI pushes the sun
    // (PushSunToCloud) during its own _Ready, BEFORE this node's deferred AddChild runs
    // _Ready — so the params must exist as soon as `new GodRays()` returns, or SetSun NREs.
    public GodRays()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_fog.gdshader") };
        _vol = new FogVolume
        {
            Name = "GodRayFog",
            // WORLD shape (canonical for outdoor global fog): the shader runs over the WHOLE
            // env froxel grid automatically — no Box, no camera-follow. The old Box centered on
            // the camera put the viewer INSIDE a uniform slab (a documented "flat haze" failure);
            // WORLD + the shader's height-falloff + cloud-gating gives proper shafts instead.
            Shape = RenderingServer.FogVolumeShape.World,
            Material = _mat,
            Visible = false,                                 // default OFF
        };
        AddChild(_vol);
    }

    /// Borrow the Environment (to drive volfog while on), the camera, and the SUN (whose shadow
    /// + volumetric_fog_energy we configure — the shafts ARE the sun's shadowed in-scatter).
    public void Attach(Godot.Environment env, Camera3D cam, DirectionalLight3D sun) { _env = env; _cam = cam; _sun = sun; }

    private double _diagAccum;
    public override void _Process(double delta)
    {
        if (!_on) { return; }
        // WORLD-shape FogVolume fills the whole froxel grid — no camera-follow needed. The
        // shader needs no cam_pos either (the engine supplies the view dir for the HG phase).

        // DIAGNOSTIC: while on, dump real runtime state once/sec so we read actual values
        // (FogVolume visibility, env volfog state) instead of assuming. Remove after debug.
        _diagAccum += delta;
        if (_diagAccum >= 1.0) { _diagAccum = 0.0; DumpState(); }
    }

    /// Altitude of the cloud deck the shafts project their gaps from (CloudVolume.AltitudeM).
    /// The shader marches each froxel up the sun ray to this height to sample the cloud gap.
    public void SetCloudHeight(float y) => _mat.SetShaderParameter("cloud_height", y);

    /// Bind the cloud shadow map (Texture2Drd from CloudVolume) + its world footprint.
    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _mat.SetShaderParameter("cloud_shadow_tex", tex); }
        _mat.SetShaderParameter("shadow_region", region);
    }

    /// dir = direction TOWARD the sun (same +Basis.Z convention as CloudVolume.SetSun). The shader
    /// marches each froxel along this to the cloud deck to sample the gap. (color is unused now —
    /// the engine supplies the real sun color/shadow; the shader's fog_tint is the medium albedo.)
    public void SetSun(Vector3 dir, Color color) => _mat.SetShaderParameter("sun_dir", dir.Normalized());

    public void SetStrength(float s) { _mat.SetShaderParameter("strength", s); GD.Print($"[godraydiag] SetStrength({s:F2})"); }

    public bool On => _on;

    /// DIAGNOSTIC: dump the full runtime state of the god-ray system so we can see (not
    /// assume) whether the FogVolume is live, where it is vs the camera, and what the env
    /// volfog actually holds. Called on demand from the UI ('G' key) so we read REAL values.
    public void DumpState()
    {
        string volState = _vol == null ? "VOL=null"
            : $"VOL vis={_vol.Visible} pos={_vol.GlobalPosition} size={_vol.Size} shape={_vol.Shape} mat={( _vol.Material != null)}";
        string envState = _env == null ? "ENV=null"
            : $"ENV volfog_on={_env.VolumetricFogEnabled} len={_env.VolumetricFogLength:F0} dens={_env.VolumetricFogDensity:F4} albedo={_env.VolumetricFogAlbedo}";
        string camState = _cam == null ? "CAM=null" : $"CAM pos={_cam.GlobalPosition}";
        GD.Print($"[godraydiag] on={_on} | {volState} | {envState} | {camState}");
    }

    /// While god rays own the Environment base volfog (density forced to ~0 so it doesn't
    /// wash the shafts), the Light-tab volfog-density slider must NOT write the live env —
    /// it would reintroduce the gray slab. Instead it records the value here so the user's
    /// intent is what we restore to when god rays turn off. Returns true if it consumed the
    /// write (god rays active); false → caller should apply to the env normally.
    public bool RedirectBaseDensity(float v)
    {
        if (!_on) { return false; }
        _savedDensity = v;   // restore-to target; live env stays at GodrayBaseDensity
        return true;
    }

    public void SetEnabled(bool on)
    {
        _on = on;
        if (_vol != null) { _vol.Visible = on; }
        GD.Print($"[godraydiag] SetEnabled({on}) — vol={( _vol != null)} env={( _env != null)} sun={( _sun != null)}");
        if (_env == null) { return; }
        if (on)
        {
            // remember prior env + sun state so the toggle is non-destructive.
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
            // Env froxel grid: ON, tight length (sharper), base density/albedo OFF (the WORLD
            // FogVolume supplies the medium), forward anisotropy + no ambient inject (contrast).
            _env.VolumetricFogEnabled = true;
            _env.VolumetricFogLength = GodrayFogLength;
            _env.VolumetricFogDensity = GodrayBaseDensity;
            _env.VolumetricFogAlbedo = GodrayBaseAlbedo;
            _env.VolumetricFogAnisotropy = GodrayAnisotropy;
            _env.VolumetricFogAmbientInject = GodrayAmbientInject;
            // THE SUN: shafts ARE its shadowed in-scatter. Shadow ON (no shadow → no shaft) and
            // crank its fog scatter energy so the thin medium lights up brightly in the gaps.
            if (_sun != null) { _sun.ShadowEnabled = true; _sun.LightVolumetricFogEnergy = GodraySunVolEnergy; }
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
