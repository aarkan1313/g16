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
    // Stage 4 (ST4-1): auto day/night clock. When _timeRunning, _Process advances _time.TimeOfDay by
    // _timeSpeed hours per real second and re-DriveTime()s — manual scrub via the time-of-day slider still works.
    private bool _timeRunning = false;
    private float _timeSpeed = 1.0f;   // in-world hours per real second
    // ST4-2 fantasy: multiplies the sky gradient (white = no tint). Applied in ComposeLighting so it
    // persists through the running day/night cycle (DriveTime rewrites the base sky colors each frame).
    private Color _skyTint = Colors.White;
    private SunDiscState _sunDisc = new();
    private WeatherState _weather = new();
    private GradeState _grade = new();
    private MoonState _moon = new();
    private StarsState _stars = new();
    private Vector3 _lastMoonDir = Vector3.Zero;   // last composed moon direction (for --lookatmoon)
    private DirectionalLight3D? _moonLight;        // Stage 3c moonlight (created lazily, parented to root)
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
        _sunDisc.Size = F(m, "sun_size", 0.6f); _sunDisc.Limb = F(m, "sun_limb", 0.70f);   // polish: more spherical default
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
        // ST4-2 fantasy sky tint: multiply the sky gradient (white = no-op) → exotic presets recolor the
        // whole sky and the tint persists through the running cycle. Used by both sky paths below.
        Color tTop = _time.SkyTop * _skyTint, tHor = _time.SkyHorizon * _skyTint, tGnd = _time.SkyGround * _skyTint;
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial psky)
        {
            psky.SkyTopColor = tTop;
            psky.SkyHorizonColor = tHor; psky.GroundHorizonColor = tHor;
            psky.GroundBottomColor = tGnd;
        }
        // ── SUN DISC (Stage-1 appearance) + shadow softness ──
        sun.ShadowBlur = _sunDisc.ShadowSoft;
        sun.LightAngularDistance = _sunDisc.DiscAngular;
        if (_cloud != null)
        {
            _cloud.SetSkyColors(tTop, tHor, tGnd);   // tinted keyframed gradient (ST4-2)
            _cloud.SetSkyTint(_skyTint);             // ST4-2 fantasy/manual tint also modulates the physical (atmosphere) sky
            _cloud.SetSunSize(_sunDisc.Size); _cloud.SetSunLimb(_sunDisc.Limb);
            _cloud.SetSunCoronaSize(_sunDisc.CoronaSize); _cloud.SetSunCoronaEnergy(_sunDisc.CoronaEnergy);
            _cloud.SetSunHaloSize(_sunDisc.HaloSize); _cloud.SetSunHaloEnergy(_sunDisc.HaloEnergy);
            _cloud.SetSunRedden(_sunDisc.Redden); _cloud.SetSunReddenOnset(_sunDisc.ReddenOnset);
            _cloud.SetSunHorizonGrow(_sunDisc.HorizonGrow); _cloud.SetSunCloudRedden(_sunDisc.CloudRedden);
            _cloud.SetNightFactor(_nightFactor);

            // ── MOON (Stage 3b/3c): OWN arc, decoupled from the sun. The moon's elongation from the sun is
            //    set by its PHASE — full (1) is anti-solar (rises at dusk), new (0) rides with the sun,
            //    quarters 90° apart — so the moon lags the sun by phase*12 h and follows its own daily arc
            //    (same arc function as the sun, evaluated at the lagged hour), NOT a mirror of the sun. This
            //    also makes night visibility phase-correct (a new moon is up by day, not night). + tunable
            //    elev/az offsets. Orient the LIGHT via RotationDegrees EXACTLY like OrientSun (basis.Z = toward
            //    the body), then read moonDir back off the node so the disc + moonlight stay consistent. ──
            EnsureMoonLight();
            float moonDayLen = Mathf.Max(_time.SunsetH - _time.SunriseH, 1e-3f);
            float moonHour = _time.TimeOfDay - _moon.Phase * 12f;        // lag the sun by phase*12 hours
            moonHour -= Mathf.Floor(moonHour / 24f) * 24f;              // wrap to [0,24)
            float mf = (moonHour - _time.SunriseH) / moonDayLen;        // 0 at moon-rise .. 1 at moon-set
            float moonElev = _time.PeakElev * _moon.DeclScale * Mathf.Sin(Mathf.Pi * mf) + _moon.ElevOffset;  // own declination → different peak height
            float moonAz = Mathf.Lerp(_time.AzStart, _time.AzEnd, mf) + _moon.AzOffset;                       // AzOffset rotates the whole path off the sun's
            // Set LOCAL rotation (valid even before the deferred parent-add lands) and read the LOCAL basis.Z.
            // The MoonLight parents to TerrainLabRoot (identity transform), so local basis == world toward-moon —
            // same convention as the sun, but no GlobalTransform read that would error while not yet in the tree.
            _moonLight!.RotationDegrees = new Vector3(-moonElev, moonAz, 0f);
            Vector3 moonDir = _moonLight.Transform.Basis.Z.Normalized();
            _lastMoonDir = moonDir;
            _cloud.SetMoon(moonDir, _moon.Color, _moon.DiscEnergy);
            _cloud.SetMoonAppearance(_moon.Phase, _moon.Size, _moon.Limb, _moon.HaloSize, _moon.HaloEnergy);
            _cloud.SetMoonSurface(_moon.SurfCells, _moon.SurfContrast, _moon.SurfSpots, _moon.SurfChurn);

            // ── MOONLIGHT (Stage 3c): the same directional casts cool light, gated to night × moon-up ×
            //    phase. Cross-fades with the sun automatically (sun energy → 0 at night via the day script
            //    while this ramps in by nightFactor). Shadow-casting; off (invisible) in daylight. ──
            float moonUp = Mathf.Clamp((moonDir.Y + 0.05f) / 0.15f, 0f, 1f);   // ramps in as the moon clears the horizon
            float mAngle = (1f - _moon.Phase) * Mathf.Pi;
            float mIllum = 0.5f + 0.5f * Mathf.Cos(mAngle);                     // 0 new · 1 full
            float mEnergy = _moon.LightEnergy * _nightFactor * moonUp * mIllum;
            _moonLight.LightColor = _moon.LightColor;
            _moonLight.LightEnergy = mEnergy;
            _moonLight.Visible = mEnergy > 0.001f;                              // invisible = no shadow/cost in day

            // ── STARS + MILKY WAY (Stage 3d): procedural, faded in at night by the sky shader. ──
            _cloud.SetStars(_stars.Brightness, _stars.Density, _stars.Twinkle, _stars.Rotation);
            _cloud.SetMilkyWay(_stars.MwBrightness, _stars.MwWidth, _stars.MwTilt);
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
        // NIGHT: stop the depth fog from washing the night. Fog/aerial perspective is a DISTANCE effect
        // (far-off haze in a large world), so at night drop how much it tints the SKY dome — the sky keeps
        // its own dark night gradient and the moon/stars read against black (the atmosphere stage will own
        // sky haze later). Also dim the base fog color cool so terrain aerial-perspective stays night-right.
        env.FogSkyAffect = Mathf.Lerp(1.0f, 0.05f, _nightFactor);
        // AT-1: when the GPU atmosphere owns the sky, stop the depth fog washing the sky dome toward
        // fog-grey (it was masking the physical sky color → "not much going on"). Terrain aerial fog stays.
        if (_atmosphereOn) { env.FogSkyAffect = Mathf.Lerp(0.1f, 0.05f, _nightFactor); }
        _baseFogColor = _baseFogColor.Lerp(NightFogColor, _nightFactor);

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
    private static readonly Color NightFogColor = new(0.04f, 0.05f, 0.09f);      // dark cool night depth-fog (vs the bright day fog)
    private const float NightAmbientBright = 0.30f;     // moonlit-bright terrain ambient ceiling
    private const float NightSkyContribution = 0.25f;   // sky-contribution at deep night (down from ~0.8 so the energy lever bites)

    /// Night brightness grade: nd 0 = black (scary), 1 = authored, 2 = bright (moonlit) — real perceptual range.
    private static Color NightGrade(Color authored, Color bright, float nd)
    {
        if (nd <= 1f) return new Color(authored.R * nd, authored.G * nd, authored.B * nd, authored.A);  // 1→authored, 0→black
        return authored.Lerp(bright, Mathf.Clamp(nd - 1f, 0f, 1f));                                     // 1→authored, 2→bright
    }

    /// Lazily create the Stage-3c moonlight directional (parented to the scene root, shadow-casting).
    /// Separate from the scene Sun so the sky shader's LIGHT0 stays the sun; this only lights terrain.
    private bool _moonLightQueued;
    private void EnsureMoonLight()
    {
        _moonLight ??= new DirectionalLight3D { Name = "MoonLight", ShadowEnabled = true, LightEnergy = 0f, Visible = false };
        if (_moonLight.IsInsideTree() || _moonLightQueued) { return; }
        // DEFERRED add: ComposeLighting first runs during _Ready while the tree is "busy setting up
        // children", so a direct AddChild errors. Queue it once; it lands next idle frame.
        var root = GetNodeOrNull<Node3D>("/root/TerrainLabRoot");
        if (root != null) { root.CallDeferred(Node.MethodName.AddChild, _moonLight); _moonLightQueued = true; }
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
