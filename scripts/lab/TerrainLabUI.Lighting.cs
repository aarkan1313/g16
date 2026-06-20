using Godot;

namespace WG16.Lab;

/// The decoupled lighting COMPOSER (Stage 2). `ComposeLighting()` is the ONE place that writes the
/// scene's lighting (sun, env tonemap/adjust/glow/fog/ambient, sky colors, sun-disc), composed from the
/// pure-data axis states (Time × Weather × Grade + the Stage-1 SunDisc appearance). `MoodToStates()`
/// splits a legacy `lighting_moods.json` mood into those states. Implemented as a TerrainLabUI partial
/// (matching the Apply/Clouds/Moods split) because composition is tightly coupled to the UI's sun
/// orientation, overcast base, and slider sync. Spec: 2026-06-20-lighting-decouple-and-time-axis-design.md.
public partial class TerrainLabUI : Control
{
    private TimeState _time = new();
    private SunDiscState _sunDisc = new();
    private WeatherState _weather = new();
    private GradeState _grade = new();

    /// Split a legacy mood dict into the axis states (same keys + defaults as the old ApplyMood).
    private void MoodToStates(Godot.Collections.Dictionary m)
    {
        _time.SunAngle = F(m, "sun_angle", 35f); _time.SunAzimuth = F(m, "sun_az", 40f);
        _time.SunEnergy = F(m, "sun_energy", 1.3f);
        _time.SunColor = m.ContainsKey("sun_color") ? Col(m["sun_color"]) : new Color(1f, 0.95f, 0.86f);
        _time.Ambient = F(m, "ambient", 0.4f); _time.AmbientSky = F(m, "ambient_sky", 1.0f);
        _time.SkyTop = m.ContainsKey("sky_top") ? Col(m["sky_top"]) : new Color(0.30f, 0.48f, 0.74f);
        _time.SkyHorizon = m.ContainsKey("sky_horizon") ? Col(m["sky_horizon"]) : new Color(0.68f, 0.74f, 0.80f);
        _time.SkyGround = m.ContainsKey("sky_ground") ? Col(m["sky_ground"]) : new Color(0.22f, 0.26f, 0.22f);

        _sunDisc.ShadowSoft = F(m, "shadow_soft", 1.0f); _sunDisc.DiscAngular = F(m, "sun_disc", 0.6f);
        _sunDisc.Size = F(m, "sun_size", 0.6f); _sunDisc.Limb = F(m, "sun_limb", 0.55f);
        _sunDisc.CoronaSize = F(m, "sun_corona_size", 1200f); _sunDisc.CoronaEnergy = F(m, "sun_corona_energy", 2.0f);
        _sunDisc.HaloSize = F(m, "sun_halo_size", 90f); _sunDisc.HaloEnergy = F(m, "sun_halo_energy", 0.4f);
        _sunDisc.Redden = F(m, "sun_redden", 1.0f); _sunDisc.ReddenOnset = F(m, "sun_redden_onset", 0.25f);
        _sunDisc.HorizonGrow = F(m, "sun_horizon_grow", 0.6f); _sunDisc.CloudRedden = F(m, "sun_cloud_redden", 0.8f);

        _weather.FogColor = m.ContainsKey("fog_color") ? Col(m["fog_color"]) : new Color(0.74f, 0.81f, 0.90f);
        _weather.FogDensity = F(m, "fog_density", 0.0006f); _weather.FogAerial = F(m, "fog_aerial", 0.85f);
        _weather.FogHeight = F(m, "fog_height", -200f); _weather.FogHeightD = F(m, "fog_heightd", 0.04f);
        _weather.FogSunScatter = F(m, "fog_sun_scatter", 0.2f);

        _grade.Exposure = F(m, "exposure", 1.0f); _grade.White = F(m, "white", 6.0f); _grade.Glow = F(m, "glow", 0.3f);
        _grade.Contrast = F(m, "contrast", 1.08f); _grade.Saturation = F(m, "saturation", 1.12f); _grade.Brightness = F(m, "brightness", 1.0f);
    }

    /// THE ONE WRITER. Composes the scene lighting from _time/_sunDisc/_weather/_grade. Behavior here is
    /// byte-for-byte the old ApplyMood (Stage 2 Task 2) — later tasks change WHERE values come from
    /// (Task 4 sun arc, Task 6 atmosphere), not this composition.
    private void ComposeLighting()
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");

        // ── TIME: sun position + color, sky gradient. Sun ENERGY + AMBIENT energy come from
        //    ApplyOvercastScaling (the one writer for overcast-scaled fields). Capture the bases here. ──
        _sunAngle = _time.SunAngle; _sunAzimuth = _time.SunAzimuth; OrientSun(sun);
        sun.LightColor = _time.SunColor;
        _baseAmbient = _time.Ambient;
        _baseSunEnergy = _time.SunEnergy;
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial psky)
        {
            psky.SkyTopColor = _time.SkyTop;
            psky.SkyHorizonColor = _time.SkyHorizon; psky.GroundHorizonColor = _time.SkyHorizon;
            psky.GroundBottomColor = _time.SkyGround;
        }
        // ── SUN DISC (Stage-1 appearance) + shadow softness ──
        sun.ShadowBlur = _sunDisc.ShadowSoft;
        sun.LightAngularDistance = _sunDisc.DiscAngular;
        if (_cloud != null)
        {
            _cloud.SetSkyColors(_time.SkyTop, _time.SkyHorizon, _time.SkyGround);
            _cloud.SetSunSize(_sunDisc.Size); _cloud.SetSunLimb(_sunDisc.Limb);
            _cloud.SetSunCoronaSize(_sunDisc.CoronaSize); _cloud.SetSunCoronaEnergy(_sunDisc.CoronaEnergy);
            _cloud.SetSunHaloSize(_sunDisc.HaloSize); _cloud.SetSunHaloEnergy(_sunDisc.HaloEnergy);
            _cloud.SetSunRedden(_sunDisc.Redden); _cloud.SetSunReddenOnset(_sunDisc.ReddenOnset);
            _cloud.SetSunHorizonGrow(_sunDisc.HorizonGrow); _cloud.SetSunCloudRedden(_sunDisc.CloudRedden);
        }

        // ── WEATHER: depth fog (same down-scaling the old mood applied). FogLightColor is set by
        //    ApplyOvercastScaling (it tints toward cloud-grey under overcast). Capture the base here. ──
        env.FogEnabled = true;
        _baseFogColor = _weather.FogColor;
        env.FogDensity = _weather.FogDensity * 0.25f;
        env.FogAerialPerspective = Mathf.Min(_weather.FogAerial, 0.5f);
        env.FogHeight = _weather.FogHeight;
        env.FogHeightDensity = _weather.FogHeightD * 0.3f;
        env.FogSunScatter = _weather.FogSunScatter * 0.25f;
        env.VolumetricFogEnabled = false;

        // ── GRADE: tonemap + color adjustments + glow ──
        env.TonemapExposure = _grade.Exposure;
        env.TonemapWhite = _grade.White;
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = _grade.Contrast;
        env.AdjustmentSaturation = _grade.Saturation;
        env.AdjustmentBrightness = _grade.Brightness;
        env.GlowEnabled = true;
        env.GlowNormalized = true;
        env.GlowHdrThreshold = 1.6f;
        env.GlowBloom = 0.0f;
        env.GlowIntensity = _grade.Glow * 0.35f;
        env.SetGlowLevel(4, 0.0f); env.SetGlowLevel(5, 0.0f); env.SetGlowLevel(6, 0.0f);

        ApplyOvercastScaling();       // sun energy + ambient + fog color (overcast-scaled) — the one writer of these
        SyncLightControlsToScene();   // Light-tab sliders reflect the composed state
    }

    /// THE ONE WRITER of the overcast-scaled lighting (sun energy, ambient energy, fog color). Called by
    /// ComposeLighting (on any state change) and by UpdateOvercast (per-frame, when coverage changes), so
    /// the two share one formula/base and never diverge. Scales from _base* (which the live sun-energy
    /// slider updates) by the current _overcast amount.
    private void ApplyOvercastScaling()
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        float oc = _overcast;
        env.AmbientLightEnergy = _baseAmbient * Mathf.Lerp(1f, 0.7f, oc);     // sky fill DOWN (grey gloom)
        env.AmbientLightSkyContribution = _time.AmbientSky;
        sun.LightEnergy = _baseSunEnergy * (1f - oc * 0.8f);                  // direct sun DOWN under cloud
        env.FogLightColor = (_cloud != null) ? _baseFogColor.Lerp(_cloud.SkyHorizonColor, 0.55f * oc) : _baseFogColor;
    }
}
