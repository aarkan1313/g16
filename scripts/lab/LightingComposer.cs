using Godot;
using System.Collections.Generic;

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
    bool VolumetricFogOn { get; }      // optional legacy volumetric fog; default off
    float CdlodViewDistance { get; }   // ARC B Task 3: LoadRing·rootSize (load boundary), or 0 if CDLOD off → no fog coupling
    float FogViewScale { get; }        // ARC B Task 3: user multiplier on the radius-coupled fog baseline (0 = coupling off)
    int ShadowAtlasSize { get; }       // ARC A.1: directional shadow atlas px (8192 default; 6144/4096 = perf dial-down)
    AtmosphereCompute? Atmosphere { get; }   // C3 Unit 5: push extra suns to the sky-scatter LUTs
    TerrainLab? Terrain { get; }       // terrain material target for the analytic indirect-fill uniforms (relight #1)
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
    // VIEW-DISTANCE: DEPTH-mode distance fog. Starts close and ramps gradually so there is no
    // sudden wall or ground-ring artifact. FogDepthCurve ≤ 1.5 keeps the ramp near-linear.
    public float FogDepthBegin { get; set; } = 55000f;   // m: terrain stays clear out to 55 km
    public float FogDepthEnd   { get; set; } = 90000f;   // m: full fog by here (matches camera far-clip 90 km)
    public float FogDepthCurve { get; set; } = 1.5f;     // low = gradual ramp; high = wall at the far end
    // Sun orientation (degrees) — written by Compose from the arc, also by the sun-angle/azimuth sliders.
    public float SunAngle { get; set; } = 35f;
    public float SunAzimuth { get; set; } = 40f;
    public Vector3 LastMoonDir { get; private set; } = Vector3.Zero;   // last composed moon dir (for --lookatmoon)

    // ── C3 luminary model integration (Unit 2 — data wired, rendering still single-sun + moon). ──
    // The composer maintains the current sun + moon as a Luminary list and runs the budgeter each Compose.
    // The allocation is NOT yet driving rendering (that's Unit 4, gated) — this proves the model + budgeter
    // integrate with ZERO look change, and is the source of truth the N-sun rendering will read.
    public LuminaryCaps Caps { get; } = new();
    private readonly List<Luminary> _luminaries = new();
    private readonly List<float> _budgetWeights = new();   // #7 perf: reused per-frame in RebuildAndBudget
    public IReadOnlyList<Luminary> Luminaries => _luminaries;
    public LuminaryAllocation? Allocation { get; private set; }
    private int _lastLumCount = -1;

    // ── C3 Unit 4: EXTRA SUNS (visible). ExtraSunCount (set by --suns=N → N-1 extras) drives a shadowless
    // DirectionalLight pool (terrain light) + the disc arrays pushed to cloud_sky. 0 = single-sun (no-op,
    // shader never touched). Each extra rides its own offset arc; shadow stays on the primary only (the
    // budgeter caps shadow casters), so N suns don't multiply the costliest pass — the "3 suns ≈ today" rule.
    public int ExtraSunCount = 0;
    private const int MaxExtraSuns = 3;   // matches MAX_EXTRA_SUNS in cloud_sky.gdshader
    private readonly List<DirectionalLight3D> _extraSunLights = new();
    private readonly HashSet<DirectionalLight3D> _extraQueued = new();
    // Tasteful fantasy companion-sun palette + per-sun size (companions read smaller so the primary stays
    // dominant): a warm gold 2nd, a smaller pale-blue hot companion, a rose 3rd. Tuned via auto-shots.
    private static readonly Color[] ExtraSunColors = { new(1.0f, 0.72f, 0.40f), new(0.62f, 0.78f, 1.0f), new(1.0f, 0.58f, 0.62f) };
    private static readonly float[] ExtraSunSizeFac = { 0.82f, 0.66f, 0.78f };
    private readonly Vector3[] _extraDirs = new Vector3[MaxExtraSuns];
    private readonly Vector3[] _extraCols = new Vector3[MaxExtraSuns];
    private readonly float[] _extraSizes = new float[MaxExtraSuns];
    private readonly float[] _extraEnergies = new float[MaxExtraSuns];
    private readonly float[] _extraAtmoInten = new float[MaxExtraSuns];   // C3 Unit 5: per-sun sky-scatter strength

    // ── C3 Unit 6: EXTRA MOONS (visual disc accents; night-gated; no extra terrain light — the primary
    // moon owns moonlight). Own phase-lagged arc + az offset, like the primary moon. 0 = single moon (no-op).
    public int ExtraMoonCount = 0;
    private const int MaxExtraMoons = 2;   // matches MAX_EXTRA_MOONS in cloud_sky.gdshader
    private static readonly Color[] ExtraMoonColors = { new(1.0f, 0.90f, 0.72f), new(1.0f, 0.82f, 0.86f) };   // pale gold, pale rose
    private static readonly float[] ExtraMoonSizeFac = { 0.80f, 0.62f };
    private static readonly float[] ExtraMoonPhases = { 0.5f, 0.85f };   // distinct (half, gibbous)
    private readonly Vector3[] _exMoonDirs = new Vector3[MaxExtraMoons];
    private readonly Vector3[] _exMoonCols = new Vector3[MaxExtraMoons];
    private readonly float[] _exMoonSizes = new float[MaxExtraMoons];
    private readonly float[] _exMoonPhases = new float[MaxExtraMoons];

    // ── U2 data-driven luminaries (the objectlist source of truth). The list is [primary sun, extra
    //    suns..., primary moon, extra moons...] in load order. The FIRST Sun + FIRST Moon stay the
    //    existing single-sun/single-moon RENDER paths (byte-identical default); entries beyond them set
    //    ExtraSunCount/ExtraMoonCount + the per-extra appearance arrays — replacing the former hardcoded
    //    ExtraSunColors/ExtraMoonColors/... constants (kept as the --suns/--moons fallback when no data).
    private readonly List<Luminary> _editable = new();
    public List<Luminary> EditableLuminaries => _editable;
    private readonly Luminary?[] _extraSunData = new Luminary?[MaxExtraSuns];
    private readonly Luminary?[] _extraMoonData = new Luminary?[MaxExtraMoons];

    /// Replace the data-driven body list (from data/luminaries.json or a preset/editor) and re-derive the
    /// extra sun/moon counts + per-body appearance. The caller recomposes after (ComposeLighting). With the
    /// default [Sun, Moon] list the extra counts are 0 → the shader is never touched → byte-identical sky.
    public void LoadLuminaries(List<Luminary> bodies)
    {
        _editable.Clear();
        foreach (var b in bodies) { _editable.Add(b); }

        var suns = new List<Luminary>();
        var moons = new List<Luminary>();
        foreach (var b in _editable)
        {
            if (b.Kind == LuminaryKind.Sun) { suns.Add(b); } else { moons.Add(b); }
        }
        // PRIMARY (entry 0 of each kind): drive the existing single-sun/single-moon render path from the list,
        // but ONLY the fields that kind actually OWNS — so editing a primary body in the list renders, while
        // the default list stays byte-identical (luminaries.json's primary values == today's live defaults).
        //  • Sun: only the DISC SIZE. The primary sun's COLOR + ENERGY are owned by the time-of-day day-script
        //    (DriveTime→SampleDayScript rewrites them every frame); the list must not fight that decoupling.
        //  • Moon: color/size/phase/arc — all owned by MoonState (NOT the day-script), so fully list-driven.
        if (suns.Count > 0) { SunDisc.Size = suns[0].Size; }
        if (moons.Count > 0)
        {
            var m = moons[0];
            Moon.Color = m.Color; Moon.Size = m.Size; Moon.Phase = m.Phase;
            Moon.AzOffset = m.AzOffset; Moon.DeclScale = m.DeclScale;
        }
        // Extra suns/moons = list beyond the primary (entry 0 of each kind keeps the existing render path).
        ExtraSunCount = Mathf.Clamp(suns.Count - 1, 0, MaxExtraSuns);
        ExtraMoonCount = Mathf.Clamp(moons.Count - 1, 0, MaxExtraMoons);
        for (int i = 0; i < MaxExtraSuns; i++) { _extraSunData[i] = (i + 1 < suns.Count) ? suns[i + 1] : null; }
        for (int i = 0; i < MaxExtraMoons; i++) { _extraMoonData[i] = (i + 1 < moons.Count) ? moons[i + 1] : null; }
    }

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
        SunDisc.ShadowNormalBias = F(m, "shadow_bias", 1.0f); SunDisc.ShadowMaxDist = F(m, "shadow_dist", 3500f);
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
            RenderingServer.DirectionalShadowAtlasSetSize(_host.ShadowAtlasSize, true);   // ARC A.1: tunable (8192 default; 6144/4096 dial-down — the in-motion shadow-spike lever)
            RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);   // PCF blur → dissolves texel "squares" cheaply

            // SSAO/SSIL DISABLED: scene files and legacy plans drifted here; force the review baseline
            // from code so shadow tuning is not contaminated by screen-space AO.
            env.SsaoEnabled = false;

            // SSIL DISABLED (2026-06-23): measured ssil ON vs OFF auto-shot diff = 97% of pixels changed, mean
            // shift 52/255 (vs the sun shadow's 0.22) — screen-space indirect light was CRUSHING the whole terrain
            // into dark mud, and because it is screen-space the darkening shifted with view angle. That was the
            // long-hunted "anti-sun darkness / shadow that grows when you look down": SSIL, not shadows/SSAO/aerial.
            // Net-negative on large dune relief (steep depth gradients → false occlusion). Off until a tamed,
            // terrain-aware pass is justified. Toggle live with key N to A/B.
            env.SsilEnabled = false;
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
        // VIEW-DISTANCE REWORK (2026-06-25): distance fog is DEPTH-mode + DECOUPLED from the CDLOD load radius.
        // The old coupling forced density = 2.8/(LoadRing·rootSize), occluding ~94% at the load boundary — it
        // hid the streaming seam with haze and capped the clear view at ~16 km. Now the seam is hidden by
        // CLIPPING (camera far-clip ≤ loaded edge), and the fog is DEPTH-mode: zero until FogDepthBegin, then
        // a curved ramp to full at FogDepthEnd — so near/mid are clear and the haze deepens only far out
        // ("less up-close fog, deeper farther-away"). AT-2 aerial perspective adds the color-correct tint.
        env.FogMode = Godot.Environment.FogModeEnum.Depth;
        env.FogDepthBegin = FogDepthBegin;
        env.FogDepthEnd = Mathf.Max(FogDepthBegin + 1f, FogDepthEnd);
        env.FogDepthCurve = FogDepthCurve;
        env.FogAerialPerspective = Mathf.Min(Weather.FogAerial, 0.5f);
        env.FogHeight = Weather.FogHeight;
        env.FogHeightDensity = Weather.FogHeightD * 0.3f;
        env.FogSunScatter = Weather.FogSunScatter * 0.25f;
        env.VolumetricFogEnabled = _host.VolumetricFogOn;
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

        // Push sun direction to terrain for horizon shadow march.
        var terr = _host.Terrain;
        if (terr != null) { terr.SetVector3("sun_dir_to", SunNode.GlobalTransform.Basis.Z.Normalized()); }
        ApplyOvercastScaling();           // sun energy + ambient + fog color (overcast-scaled) — the one writer of these
        _host.SyncLightControlsToScene(); // Light-tab sliders reflect the composed state

        ComposeExtraSuns();               // C3 Unit 4: extra sun discs + terrain lights (no-op when ExtraSunCount==0)
        ComposeExtraMoons();              // C3 Unit 6: extra moon discs (no-op when ExtraMoonCount==0)
        RebuildAndBudget();               // C3: keep the luminary list + allocation current
    }

    /// C3 Unit 4: orient each extra sun on its own offset arc, drive its (shadowless) terrain light, and
    /// push the disc arrays to cloud_sky. No-op when ExtraSunCount==0 (the shader's extra_sun_count stays 0,
    /// never written → byte-identical single-sun sky). Each extra shares the primary's time but offsets its
    /// azimuth + lowers its declination so the suns read as distinct bodies across the sky.
    private void ComposeExtraSuns()
    {
        int n = Mathf.Clamp(ExtraSunCount, 0, MaxExtraSuns);
        if (n == 0) { return; }   // default path: never touch the shader → no regression
        EnsureExtraSunLights(n);
        float dayLen = Mathf.Max(Time.SunsetH - Time.SunriseH, 1e-3f);
        float f = (Time.TimeOfDay - Time.SunriseH) / dayLen;
        for (int i = 0; i < MaxExtraSuns; i++)
        {
            if (i < n)
            {
                // U2: per-extra appearance from the data list (falls back to the hardcoded palette/spread
                // when no data — so --suns=N with no luminaries.json still works exactly as before).
                var data = _extraSunData[i];
                float declScale = (data != null && data.DeclScale > 0f) ? data.DeclScale : 0.88f;
                float azOff = data != null ? data.AzOffset : 45f * (i + 1);   // spread the extras across the sky
                float elev = Time.PeakElev * declScale * Mathf.Sin(Mathf.Pi * f);
                float az = Mathf.Lerp(Time.AzStart, Time.AzEnd, f) + azOff;
                var L = _extraSunLights[i];
                L.RotationDegrees = new Vector3(-elev, az, 0f);    // same convention as OrientSun
                Vector3 dir = L.Transform.Basis.Z.Normalized();   // local==world (parented to identity root)
                float up = Mathf.Clamp((dir.Y + 0.02f) / 0.1f, 0f, 1f);   // above-horizon ramp (terrain light)
                Color c = data?.Color ?? ExtraSunColors[i % ExtraSunColors.Length];
                L.LightColor = c;
                L.LightEnergy = BaseSunEnergy * 0.55f * up;        // companion fill, dimmer than primary, gated above horizon
                L.Visible = L.LightEnergy > 0.001f;
                _extraDirs[i] = dir;
                _extraCols[i] = new Vector3(c.R, c.G, c.B);
                _extraSizes[i] = data != null ? data.Size : SunDisc.Size * ExtraSunSizeFac[i % ExtraSunSizeFac.Length];   // companions read smaller
                _extraEnergies[i] = 1.05f;                         // disc energy (< primary's 1.3); horizonGate fades it below the horizon
            }
            else { _extraDirs[i] = Vector3.Up; _extraCols[i] = Vector3.Zero; _extraSizes[i] = 0.6f; _extraEnergies[i] = 0f; }
            _extraAtmoInten[i] = (i < n) ? 0.7f : 0f;   // sky-scatter strength (<1 so N suns don't blow out)
        }
        _host.Cloud?.SetExtraSuns(n, _extraDirs, _extraCols, _extraSizes, _extraEnergies);
        // C3 Unit 5: feed the same extras to the atmosphere so the SKY COLOR responds (summed in the shared
        // skyview/aerial raymarch — cheap; transmittance/multiscatter LUTs are sun-independent and reused).
        _host.Atmosphere?.SetExtraSuns(n, _extraDirs, _extraCols, _extraAtmoInten);
    }

    /// C3 Unit 6: orient each extra moon on its own phase-lagged arc (+ az offset) and push the disc arrays.
    /// Visual accents only — night-gated in the shader; no extra DirectionalLight (the primary moon owns the
    /// terrain moonlight). Direction computed analytically (Basis.FromEuler, Godot's default YXZ = the same
    /// convention as the primary moon's node). No-op when ExtraMoonCount==0.
    private void ComposeExtraMoons()
    {
        int n = Mathf.Clamp(ExtraMoonCount, 0, MaxExtraMoons);
        if (n == 0) { return; }
        float moonDayLen = Mathf.Max(Time.SunsetH - Time.SunriseH, 1e-3f);
        for (int i = 0; i < MaxExtraMoons; i++)
        {
            if (i < n)
            {
                // U2: per-extra moon appearance from the data list (fallback = hardcoded palette/phases).
                var data = _extraMoonData[i];
                float phase = data?.Phase ?? ExtraMoonPhases[i];
                float azOff = (data != null ? data.AzOffset : 40f * (i + 1)) + Moon.AzOffset;   // offset from the primary moon's path
                float declScale = (data != null && data.DeclScale > 0f) ? data.DeclScale : 0.78f;
                float moonHour = Time.TimeOfDay - phase * 12f;   // own phase lag
                moonHour -= Mathf.Floor(moonHour / 24f) * 24f;
                float mf = (moonHour - Time.SunriseH) / moonDayLen;
                float elev = Time.PeakElev * declScale * Mathf.Sin(Mathf.Pi * mf);
                float az = Mathf.Lerp(Time.AzStart, Time.AzEnd, mf) + azOff;
                var b = Basis.FromEuler(new Vector3(Mathf.DegToRad(-elev), Mathf.DegToRad(az), 0f));
                _exMoonDirs[i] = b.Z.Normalized();
                Color c = data?.Color ?? ExtraMoonColors[i % ExtraMoonColors.Length];
                _exMoonCols[i] = new Vector3(c.R, c.G, c.B);
                _exMoonSizes[i] = data != null ? data.Size : Moon.Size * ExtraMoonSizeFac[i % ExtraMoonSizeFac.Length];
                _exMoonPhases[i] = phase;
            }
            else { _exMoonDirs[i] = Vector3.Up; _exMoonCols[i] = Vector3.Zero; _exMoonSizes[i] = 1f; _exMoonPhases[i] = 1f; }
        }
        _host.Cloud?.SetExtraMoons(n, _exMoonDirs, _exMoonCols, _exMoonSizes, _exMoonPhases);
    }

    /// Lazily build + deferred-add the extra-sun DirectionalLights (shadowless; the primary owns the shadow).
    /// Deferred-add mirrors EnsureMoonLight (Compose first runs while the tree is busy in _Ready).
    private void EnsureExtraSunLights(int count)
    {
        while (_extraSunLights.Count < count)
        {
            _extraSunLights.Add(new DirectionalLight3D { Name = $"ExtraSun{_extraSunLights.Count}", ShadowEnabled = false, LightEnergy = 0f, Visible = false });
        }
        var root = _host.SceneOwner.GetNodeOrNull<Node3D>("/root/TerrainLabRoot");
        if (root == null) { return; }
        for (int i = 0; i < count; i++)
        {
            var L = _extraSunLights[i];
            if (!L.IsInsideTree() && _extraQueued.Add(L)) { root.CallDeferred(Node.MethodName.AddChild, L); }
        }
    }

    /// C3 (Unit 2): represent the current sun + moon as Luminary entries and run the priority budgeter.
    /// Pure bookkeeping today — the allocation is not yet consumed by rendering (Unit 4 wires it to the
    /// disc-array shader + a DirectionalLight pool), so this has ZERO effect on the look. It exercises the
    /// data model + LuminaryBudget in-context so Unit 4 is wiring, not new logic. Weights = priority ×
    /// current visibility (elevation), so the budgeter ranks bodies exactly as it will with N suns.
    private void RebuildAndBudget()
    {
        // Sun visibility from its elevation (deg → 0 below horizon .. 1 at zenith). Moon from its up-ramp ×
        // illuminated fraction (mirrors the moonlight gating), using the dir composed above.
        float sunVis = Mathf.Clamp(Mathf.Sin(Mathf.DegToRad(Time.SunAngle)), 0f, 1f);
        float moonUp = Mathf.Clamp((LastMoonDir.Y + 0.05f) / 0.15f, 0f, 1f) * _nightFactor;
        float moonIllum = 0.5f + 0.5f * Mathf.Cos((1f - Moon.Phase) * Mathf.Pi);
        float moonVis = moonUp * moonIllum;

        _luminaries.Clear();
        _budgetWeights.Clear();   // #7 perf: reuse the weights list (was new List<float>() every frame in a cycle)
        var weights = _budgetWeights;
        _luminaries.Add(new Luminary
        {
            Id = "sun_primary", Kind = LuminaryKind.Sun,
            Color = Time.SunColor, Size = SunDisc.Size, Limb = SunDisc.Limb, DiscEnergy = sun_disc_ref(),
            IsPhysicalLight = true, CastsShadow = true, ContributesToAtmosphere = true,
            Priority = 100f, LightColor = Time.SunColor, LightEnergy = BaseSunEnergy,
        });
        weights.Add(100f * sunVis);
        // C3 Unit 4: extra suns (shadowless — the primary owns the shadow atlas; budgeter enforces this).
        int en = Mathf.Clamp(ExtraSunCount, 0, MaxExtraSuns);
        for (int i = 0; i < en; i++)
        {
            float eUp = Mathf.Clamp((_extraDirs[i].Y + 0.02f) / 0.1f, 0f, 1f);
            _luminaries.Add(new Luminary
            {
                Id = $"sun_extra{i}", Kind = LuminaryKind.Sun,
                Size = SunDisc.Size, DiscEnergy = 1.3f,
                IsPhysicalLight = true, CastsShadow = false, ContributesToAtmosphere = true,
                Priority = 80f - i, LightColor = ExtraSunColors[i % ExtraSunColors.Length], LightEnergy = BaseSunEnergy * 0.7f,
            });
            weights.Add((80f - i) * eUp);
        }
        _luminaries.Add(new Luminary
        {
            Id = "moon", Kind = LuminaryKind.Moon, Phase = Moon.Phase,
            Color = Moon.Color, Size = Moon.Size, Limb = Moon.Limb, DiscEnergy = Moon.DiscEnergy,
            IsPhysicalLight = true, CastsShadow = true, ContributesToAtmosphere = false,
            Priority = 10f, LightColor = Moon.LightColor, LightEnergy = Moon.LightEnergy,
        });
        weights.Add(10f * moonVis);
        Allocation = LuminaryBudget.Allocate(_luminaries, weights, Caps);

        // Log demotions ONCE per luminary-count change (no per-frame spam). With [sun, moon] under the
        // default caps nothing is demoted, so this is silent today — it surfaces only when suns are added.
        if (_luminaries.Count != _lastLumCount)
        {
            _lastLumCount = _luminaries.Count;
            foreach (var note in Allocation.Notes) { GD.Print($"[luminary] {note}"); }
        }
    }

    // The primary sun's disc brightness reference (kept equal to the existing sun_disc_energy push).
    private float sun_disc_ref() => 1.3f;

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
        env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);
        env.AmbientLightSkyContribution = Time.AmbientSky;
        sun.LightEnergy = BaseSunEnergy * (1f - oc * 0.8f);                  // direct sun DOWN under cloud
        var cloud = _host.Cloud;
        env.FogLightColor = (cloud != null) ? BaseFogColor.Lerp(cloud.SkyHorizonColor, 0.55f * oc) : BaseFogColor;
    }

}
