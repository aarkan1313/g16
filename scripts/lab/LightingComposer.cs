using Godot;

namespace WG16.Lab;

/// What the LightingComposer needs from its host (TerrainLabUI) to do scene composition without owning
/// the whole UI. Keeps the composer decoupled from the ~245-field god-class: it reads these, it doesn't
/// reach into the UI. (C3 Unit 1 — extract the lighting composer out of TerrainLabUI.)
public interface ILightingHost
{
    Node SceneOwner { get; }          // for GetNode/CallDeferred against /root/TerrainLabRoot/*
    CloudVolume? Cloud { get; }        // sky-shader pass-through target (null = no cloud sky, ProceduralSky fallback)
    float Overcast { get; }            // current overcast amount (written by the cloud-coverage proxy)
    bool AtmosphereOn { get; }         // AT-1 GPU sky on?
    bool AerialOn { get; }             // AT-2 aerial froxel on?
    void OrientSun(DirectionalLight3D sun);   // orient + push sun to cloud/atmosphere (shared with the sun-angle sliders)
    void SyncLightControlsToScene();          // reflect the composed state back into the Light-tab sliders (UI)
}

/// The decoupled lighting COMPOSER (Stage 2 → C3). `Compose()` is the ONE place that writes the scene's
/// lighting (sun, env tonemap/adjust/glow/fog/ambient, sky colors, sun-disc, moon, stars), composed from
/// the pure-data axis states (Time × Weather × Grade + the Stage-1 SunDisc appearance + Moon/Stars). Was a
/// TerrainLabUI partial; extracted to its own class (C3 Unit 1) so the N-luminary feature has a clean home
/// and the god-class shrinks. TerrainLabUI keeps thin forwarding properties/methods (same names) so the
/// rest of the UI compiles unchanged. Behavior is byte-for-byte the pre-extraction ComposeLighting.
/// Spec: 2026-06-21-celestial-c3-n-luminaries-design.md (+ 2026-06-20-lighting-decouple-and-time-axis).
public sealed class LightingComposer
{
    private readonly ILightingHost _host;
    public LightingComposer(ILightingHost host) { _host = host; }

    // ── State owned by the composer (was TerrainLabUI fields; the UI now forwards to these). ──
    public TimeState Time { get; } = new();
    public SunDiscState SunDisc { get; } = new();
    public WeatherState Weather { get; } = new();
    public GradeState Grade { get; } = new();
    public MoonState Moon { get; } = new();
    public StarsState Stars { get; } = new();
    public Color SkyTint { get; set; } = Colors.White;   // ST4-2 fantasy sky tint (white = no tint)
    // Overcast-scaled bases (captured by Compose, scaled by ApplyOvercastScaling — the live sliders set these).
    public float BaseAmbient { get; set; } = 0.4f;
    public float BaseSunEnergy { get; set; } = 1.3f;
    public Color BaseFogColor { get; set; } = new(0.71f, 0.78f, 0.86f);
    // Sun orientation (degrees) — written by Compose from the arc, also by the sun-angle/azimuth sliders.
    public float SunAngle { get; set; } = 35f;
    public float SunAzimuth { get; set; } = 40f;
    public Vector3 LastMoonDir { get; private set; } = Vector3.Zero;   // last composed moon dir (for --lookatmoon)

    private bool _shadowTuned = false;             // #5: directional shadow atlas size set once (RenderingServer global)
    private DirectionalLight3D? _moonLight;        // Stage 3c moonlight (created lazily, parented to root)
    private float _nightFactor = 0f;               // 0 = sun up (day), 1 = sun well below horizon (deep night). Set by DriveTime.

    // Cached scene nodes — resolved once, not per-frame (Compose + ApplyOvercastScaling run every frame while
    // the day/night cycle plays; the nodes never move). Lazy so first access matches the old timing.
    private WorldEnvironment? _envNode;
    private DirectionalLight3D? _sunNode;
    private WorldEnvironment EnvNode => _envNode ??= _host.SceneOwner.GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
    private DirectionalLight3D SunNode => _sunNode ??= _host.SceneOwner.GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");

    // Mood-parsing helpers (mirror TerrainLabUI.Moods.cs Col/F — kept local so the composer is self-contained).
    private static Color Col(Variant v) { var a = v.AsGodotArray(); return new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
    private static float F(Godot.Collections.Dictionary d, string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;

    /// Split a legacy mood dict into the axis states (same keys + defaults as the old ApplyMood).
    public void MoodToStates(Godot.Collections.Dictionary m)
    {
        _nightFactor = 0f;   // moods are daytime looks — clear any night grade left from a time-scrub
        Time.SunAngle = F(m, "sun_angle", 35f); Time.SunAzimuth = F(m, "sun_az", 40f);
        Time.SunEnergy = F(m, "sun_energy", 1.3f);
        Time.SunColor = m.ContainsKey("sun_color") ? Col(m["sun_color"]) : new Color(1f, 0.95f, 0.86f);
        Time.Ambient = F(m, "ambient", 0.4f); Time.AmbientSky = F(m, "ambient_sky", 1.0f);
        Time.SkyTop = m.ContainsKey("sky_top") ? Col(m["sky_top"]) : new Color(0.30f, 0.48f, 0.74f);
        Time.SkyHorizon = m.ContainsKey("sky_horizon") ? Col(m["sky_horizon"]) : new Color(0.68f, 0.74f, 0.80f);
        Time.SkyGround = m.ContainsKey("sky_ground") ? Col(m["sky_ground"]) : new Color(0.22f, 0.26f, 0.22f);

        SunDisc.ShadowSoft = F(m, "shadow_soft", 1.0f); SunDisc.DiscAngular = F(m, "sun_disc", 0.6f);
        SunDisc.ShadowNormalBias = F(m, "shadow_bias", 1.0f); SunDisc.ShadowMaxDist = F(m, "shadow_dist", 6000f);
        SunDisc.Size = F(m, "sun_size", 0.6f); SunDisc.Limb = F(m, "sun_limb", 0.70f);   // polish: more spherical default
        SunDisc.CoronaSize = F(m, "sun_corona_size", 1200f); SunDisc.CoronaEnergy = F(m, "sun_corona_energy", 2.0f);
        SunDisc.HaloSize = F(m, "sun_halo_size", 90f); SunDisc.HaloEnergy = F(m, "sun_halo_energy", 0.4f);
        SunDisc.Redden = F(m, "sun_redden", 1.0f); SunDisc.ReddenOnset = F(m, "sun_redden_onset", 0.25f);
        SunDisc.HorizonGrow = F(m, "sun_horizon_grow", 0.6f); SunDisc.CloudRedden = F(m, "sun_cloud_redden", 0.8f);

        Weather.FogColor = m.ContainsKey("fog_color") ? Col(m["fog_color"]) : new Color(0.74f, 0.81f, 0.90f);
        Weather.FogDensity = F(m, "fog_density", 0.0006f); Weather.FogAerial = F(m, "fog_aerial", 0.85f);
        Weather.FogHeight = F(m, "fog_height", -200f); Weather.FogHeightD = F(m, "fog_heightd", 0.04f);
        Weather.FogSunScatter = F(m, "fog_sun_scatter", 0.2f);

        Grade.Exposure = F(m, "exposure", 1.0f); Grade.White = F(m, "white", 6.0f); Grade.Glow = F(m, "glow", 0.3f);
        Grade.Contrast = F(m, "contrast", 1.08f); Grade.Saturation = F(m, "saturation", 1.12f); Grade.Brightness = F(m, "brightness", 1.0f);
    }

    /// THE ONE WRITER. Composes the scene lighting from Time/SunDisc/Weather/Grade/Moon/Stars.
    public void Compose()
    {
        var env = EnvNode.Environment;
        var sun = SunNode;

        // ── TIME: sun position + color, sky gradient. Sun ENERGY + AMBIENT energy come from
        //    ApplyOvercastScaling (the one writer for overcast-scaled fields). Capture the bases here. ──
        SunAngle = Time.SunAngle; SunAzimuth = Time.SunAzimuth; _host.OrientSun(sun);
        sun.LightColor = Time.SunColor;
        BaseAmbient = Time.Ambient;
        BaseSunEnergy = Time.SunEnergy;
        // Ambient COLOR cools toward moonlight as night falls (day = white). CRITICAL: Godot's default
        // AmbientLightColor is BLACK, so without this the night ambient ENERGY lever multiplies by black
        // and never reaches the screen — this is what makes night_darkness actually visible on terrain.
        env.AmbientLightColor = new Color(1f, 1f, 1f).Lerp(NightAmbientTint, _nightFactor);
        // ST4-2 fantasy sky tint: multiply the sky gradient (white = no-op) → exotic presets recolor the
        // whole sky and the tint persists through the running cycle. Used by both sky paths below.
        Color tTop = Time.SkyTop * SkyTint, tHor = Time.SkyHorizon * SkyTint, tGnd = Time.SkyGround * SkyTint;
        // SKY-COLOR OWNERSHIP (read before tuning day_script daytime colors). The active sky is the cloud_sky
        // ShaderMaterial, installed by CloudVolume once its compute is ready. Until then (startup) — or in any
        // scene with no CloudVolume — the scene's ProceduralSkyMaterial is live, and THIS branch drives it.
        // It is a fallback, not the steady-state path. (Matches by type, so it's a no-op once the cloud sky is in.)
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial psky)
        {
            psky.SkyTopColor = tTop;
            psky.SkyHorizonColor = tHor; psky.GroundHorizonColor = tHor;
            psky.GroundBottomColor = tGnd;
        }
        // ── SUN DISC (Stage-1 appearance) + shadow softness ──
        sun.ShadowBlur = SunDisc.ShadowSoft;
        sun.LightAngularDistance = SunDisc.DiscAngular;
        // #5 SHADOW PASS (code-side, no scene/project edits → no conflict with the terrain chat). The blocky
        // far-cascade self-shadow + closer/farther quality jump were the directional shadow under-resolved at
        // distance: default 4096 atlas (~0.6 m/texel near → ~7.8 m far). Double the atlas + flatten the splits
        // + cross-fade cascades so far texel density isn't starved.
        if (!_shadowTuned)
        {
            RenderingServer.DirectionalShadowAtlasSetSize(8192, true);   // 4096→8192 = halve m/texel everywhere (perf lever — dial down once edges hold)
            RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);   // PCF blur → dissolves texel "squares" cheaply
            // SSAO was the harsh "second shadow system": intensity 2.0 raked across the faceted 4 m mesh and read
            // as jagged shadows. Dial to subtle valley AO (the look fix); revisit when the higher-res CDLOD mesh lands.
            if (env != null) { env.SsaoIntensity = 0.6f; }
            _shadowTuned = true;
        }
        sun.DirectionalShadowBlendSplits = true;                        // cross-fade cascade seams
        // Shadow caster params from SunDisc (lab-tunable; defaults == the former literals). Routed through
        // this one-writer so a slider edit survives the next recompose instead of being overwritten.
        sun.ShadowNormalBias = SunDisc.ShadowNormalBias;                // acne<->peter-panning lever (was unset)
        sun.DirectionalShadowMaxDistance = SunDisc.ShadowMaxDist;       // shadow draw distance (was 6000 literal)
        sun.DirectionalShadowSplit1 = SunDisc.ShadowSplit1;             // flatter split distribution
        sun.DirectionalShadowSplit2 = SunDisc.ShadowSplit2;
        sun.DirectionalShadowSplit3 = SunDisc.ShadowSplit3;
        var cloud = _host.Cloud;
        if (cloud != null)
        {
            // Keyframed gradient → cloud_sky `background()`. OWNERSHIP LAW: when the atmosphere arc is on
            // (AT-1/2/3, the default), the physical LUT OWNS the DAYTIME sky/cloud-ambient color and these
            // keyframed colors are mixed out by night_factor=0 — so tuning the day_script DAYTIME palette is
            // intentionally a no-op then. These colors still drive (a) the NIGHT sky (atmosphere bows out as
            // night_factor→1), and (b) the whole sky + cloud ambient when atmosphere is OFF. Hence: still pushed
            // every recompose (it's the night path), NOT redundant. To grade the physical day sky use sky_tint
            // (fantasy recolor) or the atmosphere exposure — day_script daytime colors are by-design superseded.
            cloud.SetSkyColors(tTop, tHor, tGnd);   // tinted keyframed gradient (ST4-2) — night sky + atmosphere-off fallback
            cloud.SetSkyTint(SkyTint);              // ST4-2 fantasy/manual tint also modulates the physical (atmosphere) sky
            cloud.SetSunSize(SunDisc.Size); cloud.SetSunLimb(SunDisc.Limb);
            cloud.SetSunCoronaSize(SunDisc.CoronaSize); cloud.SetSunCoronaEnergy(SunDisc.CoronaEnergy);
            cloud.SetSunHaloSize(SunDisc.HaloSize); cloud.SetSunHaloEnergy(SunDisc.HaloEnergy);
            cloud.SetSunRedden(SunDisc.Redden); cloud.SetSunReddenOnset(SunDisc.ReddenOnset);
            cloud.SetSunHorizonGrow(SunDisc.HorizonGrow); cloud.SetSunCloudRedden(SunDisc.CloudRedden);
            cloud.SetNightFactor(_nightFactor);

            // ── MOON (Stage 3b/3c): OWN arc, decoupled from the sun. The moon's elongation from the sun is
            //    set by its PHASE — full (1) is anti-solar (rises at dusk), new (0) rides with the sun,
            //    quarters 90° apart — so the moon lags the sun by phase*12 h and follows its own daily arc
            //    (same arc function as the sun, evaluated at the lagged hour), NOT a mirror of the sun. This
            //    also makes night visibility phase-correct (a new moon is up by day, not night). + tunable
            //    elev/az offsets. Orient the LIGHT via RotationDegrees EXACTLY like OrientSun (basis.Z = toward
            //    the body), then read moonDir back off the node so the disc + moonlight stay consistent. ──
            EnsureMoonLight();
            float moonDayLen = Mathf.Max(Time.SunsetH - Time.SunriseH, 1e-3f);
            float moonHour = Time.TimeOfDay - Moon.Phase * 12f;        // lag the sun by phase*12 hours
            moonHour -= Mathf.Floor(moonHour / 24f) * 24f;             // wrap to [0,24)
            float mf = (moonHour - Time.SunriseH) / moonDayLen;        // 0 at moon-rise .. 1 at moon-set
            float moonElev = Time.PeakElev * Moon.DeclScale * Mathf.Sin(Mathf.Pi * mf) + Moon.ElevOffset;  // own declination → different peak height
            float moonAz = Mathf.Lerp(Time.AzStart, Time.AzEnd, mf) + Moon.AzOffset;                       // AzOffset rotates the whole path off the sun's
            // Set LOCAL rotation (valid even before the deferred parent-add lands) and read the LOCAL basis.Z.
            // The MoonLight parents to TerrainLabRoot (identity transform), so local basis == world toward-moon —
            // same convention as the sun, but no GlobalTransform read that would error while not yet in the tree.
            _moonLight!.RotationDegrees = new Vector3(-moonElev, moonAz, 0f);
            Vector3 moonDir = _moonLight.Transform.Basis.Z.Normalized();
            LastMoonDir = moonDir;
            cloud.SetMoon(moonDir, Moon.Color, Moon.DiscEnergy);
            cloud.SetMoonAppearance(Moon.Phase, Moon.Size, Moon.Limb, Moon.HaloSize, Moon.HaloEnergy);
            cloud.SetMoonSurface(Moon.SurfCells, Moon.SurfContrast, Moon.SurfSpots, Moon.SurfChurn);

            // ── MOONLIGHT (Stage 3c): the same directional casts cool light, gated to night × moon-up ×
            //    phase. Cross-fades with the sun automatically (sun energy → 0 at night via the day script
            //    while this ramps in by nightFactor). Shadow-casting; off (invisible) in daylight. ──
            float moonUp = Mathf.Clamp((moonDir.Y + 0.05f) / 0.15f, 0f, 1f);   // ramps in as the moon clears the horizon
            float mAngle = (1f - Moon.Phase) * Mathf.Pi;
            float mIllum = 0.5f + 0.5f * Mathf.Cos(mAngle);                     // 0 new · 1 full
            float mEnergy = Moon.LightEnergy * _nightFactor * moonUp * mIllum;
            _moonLight.LightColor = Moon.LightColor;
            _moonLight.LightEnergy = mEnergy;
            _moonLight.Visible = mEnergy > 0.001f;                              // invisible = no shadow/cost in day
            // Night moonlight on CLOUDS (raymarch 2nd light): same phase·presence·night gating as the
            // directional, scaled by the user knob. 0 in day / new-moon → the raymarch skips the moon march.
            cloud.SetCloudMoon(moonDir, Moon.LightColor, mIllum * moonUp * _nightFactor * Moon.MoonCloudLight);

            // ── STARS + METEORS (Stage 3d / Celestial): procedural, faded in at night by the sky shader. ──
            cloud.SetStars(Stars.Brightness, Stars.Density, Stars.Twinkle, Stars.Rotation);
            cloud.SetMeteorsOn(Stars.MeteorsOn);
            cloud.SetMeteors(Stars.MeteorRate, Stars.MeteorBrightness, Stars.MeteorLength, Stars.MeteorSpeed, Stars.MeteorColor, Stars.MeteorColorVar);
            cloud.SetPlanets(Stars.PlanetsOn, Stars.PlanetBrightness);             // C2 planets
            cloud.SetBrightStars(Stars.BrightStarsOn, Stars.BrightStarBrightness); // C2 landmark stars
        }

        // ── WEATHER: depth fog (same down-scaling the old mood applied). FogLightColor is set by
        //    ApplyOvercastScaling (it tints toward cloud-grey under overcast). Capture the base here. ──
        env.FogEnabled = true;
        BaseFogColor = Weather.FogColor;
        env.FogDensity = Weather.FogDensity * 0.25f;
        env.FogAerialPerspective = Mathf.Min(Weather.FogAerial, 0.5f);
        // AT-2: when the physical aerial froxel owns distance haze, drop the built-in aerial perspective so
        // the two don't double-fog. Height fog / FogDensity stay (the froxel only replaces the distance/sky
        // blend). Toggling AT-2 off restores Weather.FogAerial exactly (this re-runs on Compose).
        if (_host.AerialOn) { env.FogAerialPerspective = 0.0f; }
        env.FogHeight = Weather.FogHeight;
        env.FogHeightDensity = Weather.FogHeightD * 0.3f;
        env.FogSunScatter = Weather.FogSunScatter * 0.25f;
        env.VolumetricFogEnabled = false;
        // NIGHT: stop the depth fog from washing the night. Fog/aerial perspective is a DISTANCE effect
        // (far-off haze in a large world), so at night drop how much it tints the SKY dome — the sky keeps
        // its own dark night gradient and the moon/stars read against black (the atmosphere stage will own
        // sky haze later). Also dim the base fog color cool so terrain aerial-perspective stays night-right.
        env.FogSkyAffect = Mathf.Lerp(1.0f, 0.05f, _nightFactor);
        // AT-1: when the GPU atmosphere owns the sky, stop the depth fog washing the sky dome toward
        // fog-grey (it was masking the physical sky color → "not much going on"). Terrain aerial fog stays.
        if (_host.AtmosphereOn) { env.FogSkyAffect = Mathf.Lerp(0.1f, 0.05f, _nightFactor); }
        BaseFogColor = BaseFogColor.Lerp(NightFogColor, _nightFactor);

        // ── GRADE: tonemap + color adjustments + glow ──
        env.TonemapExposure = Grade.Exposure;
        env.TonemapWhite = Grade.White;
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = Grade.Contrast;
        env.AdjustmentSaturation = Grade.Saturation;
        env.AdjustmentBrightness = Grade.Brightness;
        env.GlowEnabled = true;
        env.GlowNormalized = true;
        env.GlowHdrThreshold = 1.6f;
        env.GlowBloom = 0.0f;
        env.GlowIntensity = Grade.Glow * 0.35f;
        env.SetGlowLevel(4, 0.0f); env.SetGlowLevel(5, 0.0f); env.SetGlowLevel(6, 0.0f);

        ApplyOvercastScaling();           // sun energy + ambient + fog color (overcast-scaled) — the one writer of these
        _host.SyncLightControlsToScene(); // Light-tab sliders reflect the composed state
    }

    /// TIME-OF-DAY driver (Task 4): one `hour` knob moves the sun along the analytic arc AND interpolates
    /// the day color script into Time, then composes. Mood-pick (MoodToStates) and time-scrub both write
    /// Time → last one wins, so the 6 moods still reproduce and scrubbing time gives a cohesive day.
    public void DriveTime(float hour)
    {
        Time.TimeOfDay = hour;
        float dayLen = Mathf.Max(Time.SunsetH - Time.SunriseH, 1e-3f);
        float f = (hour - Time.SunriseH) / dayLen;                  // 0 at sunrise, 1 at sunset; <0/>1 = night
        Time.SunAngle = Time.PeakElev * Mathf.Sin(Mathf.Pi * f);   // continuous: peak at noon, NEGATIVE at night
        Time.SunAzimuth = Mathf.Lerp(Time.AzStart, Time.AzEnd, f); // continues sweeping (extrapolates) at night

        // nightFactor: 0 while the sun is up, ramping to 1 once it is ~6° below the horizon (civil twilight).
        float belowDeg = Mathf.Max(-Time.SunAngle, 0f);
        float nf = Mathf.Clamp(belowDeg / 6f, 0f, 1f);
        _nightFactor = nf * nf * (3f - 2f * nf);                    // smoothstep

        TimeKey k = SampleDayScript(hour);
        Time.SunEnergy = k.SunEnergy;
        Time.SunColor = k.SunColor;

        // NIGHT GRADE (Stage 3a): night_darkness is the master night-brightness lever with REAL
        // perceptual range. Multiplying the near-black anchors did nothing, so instead lerp the
        // authored night look toward BLACK (scary, nd<1) or a MOONLIT target (nd>1). nd=1 = the
        // authored physical night. Endpoints are the Night* constants (one place = modular).
        float nd = Time.NightDarkness;
        Color gTop = NightGrade(k.SkyTop, NightBrightTop, nd);
        Color gHor = NightGrade(k.SkyHorizon, NightBrightHorizon, nd);
        Color gGnd = NightGrade(k.SkyGround, NightBrightGround, nd);
        Time.SkyTop = k.SkyTop.Lerp(gTop, _nightFactor);
        Time.SkyHorizon = k.SkyHorizon.Lerp(gHor, _nightFactor);
        Time.SkyGround = k.SkyGround.Lerp(gGnd, _nightFactor);

        // Ambient terrain fill: scary(0) → authored → moonlit ceiling, clamped to the floor. At night we
        // also drop sky-contribution so this energy lever actually lights the terrain (the near-black
        // night sky otherwise dominates ambient and the knob can't bite). The ambient COLOR is cooled in
        // Compose (default AmbientLightColor is black, which would zero out the energy lever).
        float ambNight = (nd <= 1f)
            ? Mathf.Lerp(0f, k.Ambient, nd)
            : Mathf.Lerp(k.Ambient, NightAmbientBright, Mathf.Clamp(nd - 1f, 0f, 1f));
        ambNight = Mathf.Max(ambNight, Time.NightAmbientFloor);
        Time.Ambient = Mathf.Lerp(k.Ambient, ambNight, _nightFactor);
        Time.AmbientSky = Mathf.Lerp(k.AmbientSky, NightSkyContribution, _nightFactor);
        Compose();
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
        // DEFERRED add: Compose first runs during _Ready while the tree is "busy setting up children",
        // so a direct AddChild errors. Queue it once; it lands next idle frame.
        var root = _host.SceneOwner.GetNodeOrNull<Node3D>("/root/TerrainLabRoot");
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
    /// Compose (on any state change) and by UpdateOvercast (per-frame, when coverage changes), so the two
    /// share one formula/base and never diverge. Scales from Base* (which the live sun-energy slider updates)
    /// by the current overcast amount.
    public void ApplyOvercastScaling()
    {
        var env = EnvNode.Environment;
        var sun = SunNode;
        float oc = _host.Overcast;
        env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);     // sky fill DOWN (grey gloom)
        env.AmbientLightSkyContribution = Time.AmbientSky;
        sun.LightEnergy = BaseSunEnergy * (1f - oc * 0.8f);                  // direct sun DOWN under cloud
        var cloud = _host.Cloud;
        env.FogLightColor = (cloud != null) ? BaseFogColor.Lerp(cloud.SkyHorizonColor, 0.55f * oc) : BaseFogColor;
    }
}
