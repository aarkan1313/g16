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
    private MoonState _moon = new();
    private Vector3 _lastMoonDir = Vector3.Zero;   // last composed moon direction (for --lookatmoon)
    private float _nightFactor = 0f;   // 0 = sun up (day), 1 = sun well below horizon (deep night). Set by DriveTime.

    /// Split a legacy mood dict into the axis states (same keys + defaults as the old ApplyMood).
    private void MoodToStates(Godot.Collections.Dictionary m)
    {
        _nightFactor = 0f;   // moods are daytime looks — clear any night grade left from a time-scrub
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
        // Ambient COLOR cools toward moonlight as night falls (day = white). CRITICAL: Godot's default
        // AmbientLightColor is BLACK, so without this the night ambient ENERGY lever multiplies by black
        // and never reaches the screen — this is what makes night_darkness actually visible on terrain.
        env.AmbientLightColor = new Color(1f, 1f, 1f).Lerp(NightAmbientTint, _nightFactor);
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
            _cloud.SetNightFactor(_nightFactor);

            // ── MOON (Stage 3b): default anti-solar (up at night) + tunable elev/az offsets. The disc
            //    reuses the sun-surface system; rendered + gated to night in cloud_sky.gdshader. ──
            Vector3 toSun = sun.GlobalTransform.Basis.Z.Normalized();   // +Z = toward the sun
            Vector3 anti = -toSun;
            float mElev = Mathf.Asin(Mathf.Clamp(anti.Y, -1f, 1f)) + Mathf.DegToRad(_moon.ElevOffset);
            float mAz = Mathf.Atan2(anti.Z, anti.X) + Mathf.DegToRad(_moon.AzOffset);
            float ce = Mathf.Cos(mElev);
            Vector3 moonDir = new Vector3(ce * Mathf.Cos(mAz), Mathf.Sin(mElev), ce * Mathf.Sin(mAz)).Normalized();
            _lastMoonDir = moonDir;
            _cloud.SetMoon(moonDir, _moon.Color, _moon.DiscEnergy);
            _cloud.SetMoonAppearance(_moon.Phase, _moon.Size, _moon.Limb, _moon.HaloSize, _moon.HaloEnergy);
            _cloud.SetMoonSurface(_moon.SurfCells, _moon.SurfContrast, _moon.SurfSpots, _moon.SurfChurn);
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

    /// TIME-OF-DAY driver (Task 4): one `hour` knob moves the sun along the analytic arc AND interpolates
    /// the day color script into _time, then composes. Mood-pick (MoodToStates) and time-scrub both write
    /// _time → last one wins, so the 6 moods still reproduce and scrubbing time gives a cohesive day.
    private void DriveTime(float hour)
    {
        _time.TimeOfDay = hour;
        float dayLen = Mathf.Max(_time.SunsetH - _time.SunriseH, 1e-3f);
        float f = (hour - _time.SunriseH) / dayLen;                  // 0 at sunrise, 1 at sunset; <0/>1 = night
        _time.SunAngle = _time.PeakElev * Mathf.Sin(Mathf.Pi * f);   // continuous: peak at noon, NEGATIVE at night
        _time.SunAzimuth = Mathf.Lerp(_time.AzStart, _time.AzEnd, f);// continues sweeping (extrapolates) at night

        // nightFactor: 0 while the sun is up, ramping to 1 once it is ~6° below the horizon (civil twilight).
        float belowDeg = Mathf.Max(-_time.SunAngle, 0f);
        float nf = Mathf.Clamp(belowDeg / 6f, 0f, 1f);
        _nightFactor = nf * nf * (3f - 2f * nf);                     // smoothstep

        TimeKey k = SampleDayScript(hour);
        _time.SunEnergy = k.SunEnergy;
        _time.SunColor = k.SunColor;

        // NIGHT GRADE (Stage 3a): night_darkness is the master night-brightness lever with REAL
        // perceptual range. Multiplying the near-black anchors did nothing, so instead lerp the
        // authored night look toward BLACK (scary, nd<1) or a MOONLIT target (nd>1). nd=1 = the
        // authored physical night. Endpoints are the Night* constants (one place = modular).
        float nd = _time.NightDarkness;
        Color gTop = NightGrade(k.SkyTop, NightBrightTop, nd);
        Color gHor = NightGrade(k.SkyHorizon, NightBrightHorizon, nd);
        Color gGnd = NightGrade(k.SkyGround, NightBrightGround, nd);
        _time.SkyTop = k.SkyTop.Lerp(gTop, _nightFactor);
        _time.SkyHorizon = k.SkyHorizon.Lerp(gHor, _nightFactor);
        _time.SkyGround = k.SkyGround.Lerp(gGnd, _nightFactor);

        // Ambient terrain fill: scary(0) → authored → moonlit ceiling, clamped to the floor. At night we
        // also drop sky-contribution so this energy lever actually lights the terrain (the near-black
        // night sky otherwise dominates ambient and the knob can't bite). The ambient COLOR is cooled in
        // ComposeLighting (default AmbientLightColor is black, which would zero out the energy lever).
        float ambNight = (nd <= 1f)
            ? Mathf.Lerp(0f, k.Ambient, nd)
            : Mathf.Lerp(k.Ambient, NightAmbientBright, Mathf.Clamp(nd - 1f, 0f, 1f));
        ambNight = Mathf.Max(ambNight, _time.NightAmbientFloor);
        _time.Ambient = Mathf.Lerp(k.Ambient, ambNight, _nightFactor);
        _time.AmbientSky = Mathf.Lerp(k.AmbientSky, NightSkyContribution, _nightFactor);
        ComposeLighting();
    }

    // Night-grade endpoints (modular: tune the night palette here). nd: 0 = scary-black, 1 = authored, 2 = moonlit-bright.
    private static readonly Color NightBrightTop = new(0.07f, 0.10f, 0.18f);
    private static readonly Color NightBrightHorizon = new(0.10f, 0.13f, 0.20f);
    private static readonly Color NightBrightGround = new(0.05f, 0.06f, 0.09f);
    private static readonly Color NightAmbientTint = new(0.55f, 0.62f, 0.85f);   // cool moonlight ambient fill
    private const float NightAmbientBright = 0.30f;     // moonlit-bright terrain ambient ceiling
    private const float NightSkyContribution = 0.25f;   // sky-contribution at deep night (down from ~0.8 so the energy lever bites)

    /// Night brightness grade: nd 0 = black (scary), 1 = authored, 2 = bright (moonlit) — real perceptual range.
    private static Color NightGrade(Color authored, Color bright, float nd)
    {
        if (nd <= 1f) return new Color(authored.R * nd, authored.G * nd, authored.B * nd, authored.A);  // 1→authored, 0→black
        return authored.Lerp(bright, Mathf.Clamp(nd - 1f, 0f, 1f));                                     // 1→authored, 2→bright
    }

    /// Interpolate the daytime color script (LightingPresets.DayScript anchors) at `hour`.
    private static TimeKey SampleDayScript(float hour)
    {
        var s = LightingPresets.DayScript;
        if (s.Count == 0) { return new TimeKey { Hour = hour }; }
        if (hour <= s[0].Hour) { return s[0]; }
        if (hour >= s[s.Count - 1].Hour) { return s[s.Count - 1]; }
        for (int i = 0; i < s.Count - 1; i++)
        {
            if (hour >= s[i].Hour && hour <= s[i + 1].Hour)
            {
                float t = (hour - s[i].Hour) / Mathf.Max(s[i + 1].Hour - s[i].Hour, 1e-4f);
                return new TimeKey
                {
                    Hour = hour, SunEnergy = Mathf.Lerp(s[i].SunEnergy, s[i + 1].SunEnergy, t),
                    SunColor = s[i].SunColor.Lerp(s[i + 1].SunColor, t),
                    SkyTop = s[i].SkyTop.Lerp(s[i + 1].SkyTop, t),
                    SkyHorizon = s[i].SkyHorizon.Lerp(s[i + 1].SkyHorizon, t),
                    SkyGround = s[i].SkyGround.Lerp(s[i + 1].SkyGround, t),
                    Ambient = Mathf.Lerp(s[i].Ambient, s[i + 1].Ambient, t),
                    AmbientSky = Mathf.Lerp(s[i].AmbientSky, s[i + 1].AmbientSky, t),
                };
            }
        }
        return s[s.Count - 1];
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
