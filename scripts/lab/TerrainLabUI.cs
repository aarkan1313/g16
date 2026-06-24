using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

/// On-screen panel for the terrain look lab — DATA-DRIVEN: every control is
/// defined in data/lab_controls.json and built from a registry. Tabs + per-tab
/// scroll keep it on-screen; each control has a lock; a Randomizer rolls all
/// unlocked controls. Drives TerrainLab live. Presets persist values + locks.
public partial class TerrainLabUI : Control
{
    private const string PresetsPath = "user://terrain_presets.json";
    private const string RegistryPath = "res://data/lab_controls.json";

    private FieldCompute _fc = null!;
    private FieldParams _params = null!;
    private TerrainLab _terrain = null!;
    private readonly List<string> _materials = new();

    private string[] _zoneNames = Array.Empty<string>();
    private readonly List<LabControl> _controls = new();           // flat registry (zone/companion expanded)
    private readonly Dictionary<string, LabControl> _byId = new(); // id (or id#z) -> control
    private Dictionary<string, Variant> _presets = new();
    private System.Random _rng = new();
    private bool _ready;   // suppress callbacks while building/applying

    // S3.5 spike state (--aabbspike): drive one async height-range request over a few frames, compare to sync.
    private ChunkAabbProvider _spikeProvider;
    private int _spikeFrame = -1;          // -1 = not started; >=0 = frames since the request
    private bool _spikeDone;

    // S3 live pop meter (--popmeter): measures height/normal/origin-snap each frame as you fly + HUD line.
    private LivePopMeter _popMeter;
    private Label _popMeterLabel;

    /// One control: parsed registry fields + runtime state.
    private sealed class LabControl
    {
        public string Id = "", Label = "", Tab = "", Type = "";
        public string? Param, Setter, Field, Scene, Cloud;  // shader uniform / mode-setter / TerrainLab field / scene-node target / CloudVolume knob
        public float Min, Max, Default;
        public bool DefBool;
        public Color DefColor = new(1f, 1f, 1f);   // for type "scenecolor"
        public string[] Options = Array.Empty<string>();
        public bool Rand = true, Rebake;
        public int Zone = -1;                     // for material/companion (0..6), else -1
        public string? ItemSchema, DataPath;      // for type "objectlist" (U2): schema name + data array file
        public int MinItems = 1, MaxItems = 7;    // for type "objectlist": list bounds
        public Variant Value;                     // current value
        public bool Locked;
        public CheckBox? LockBox;
        public Control? Widget;                   // the editing control (slider/checkbox/dropdown)
        public Label? ValLabel;
    }

    public override void _Ready()
    {
        // U1 self-check (--objectlistcheck): pure-logic ObjectListControl model/callback test. Runs FIRST,
        // before FieldCompute (which NullRefs under --headless: no local RenderingDevice), then quits.
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--objectlistcheck") >= 0)
        {
            bool olOk = ObjectListCheck.Run(this, out string olMsg);
            GD.Print($"OBJECTLISTCHECK: {(olOk ? "PASS" : "FAIL")}  {olMsg}");
            GetTree().Quit(olOk ? 0 : 1);
            return;
        }
        // U3 self-check (--lumpresetcheck): luminary preset round-trip through the Json save/load path.
        // Pure logic — runs before FieldCompute (RenderingDevice-free), then quits.
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--lumpresetcheck") >= 0)
        {
            bool lpOk = LuminaryPresetCheck.Run(DictFromLuminary, LuminaryFromDict, LumDictToStorable, LumDictFromStorable, out string lpMsg);
            GD.Print($"LUMPRESETCHECK: {(lpOk ? "PASS" : "FAIL")}  {lpMsg}");
            GetTree().Quit(lpOk ? 0 : 1);
            return;
        }

        _fc = new FieldCompute();
        _params = FieldParams.Load();
        _terrain = GetNode<TerrainLab>("/root/TerrainLabRoot/TerrainLab");
        _terrain.Build(_fc, _params);

        // Cloud subsystem: create the node + add/attach DEFERRED — the tree is mid
        // setup during _Ready ("parent busy setting up children"), so both the
        // AddChild and the Attach must run after the current frame's setup.
        _cloud = new CloudVolume { Name = "CloudVolume" };
        _godraysScreen = new GodRaysScreen { Name = "GodRaysScreen" };     // screen-space radial scatter — THE god-ray layer
        _atmosphere = new AtmosphereCompute { Name = "AtmosphereCompute" };   // AT-1 GPU physical sky (deferred-add like the cloud node)
        _aerial = new AerialPerspective { Name = "AerialPerspective" };       // AT-2 screen-space aerial perspective (deferred-add)
        CallDeferred(nameof(AttachClouds));

        ParseCli();
        LoadLibrary();
        LoadGroundPalette();   // GM1: must run before LoadRegistry builds the material controls
        LoadMoods();
        LightingPresets.Load();   // Time/Weather/Grade presets + the day color script (decoupled lighting)
        LoadRegistry();
        LoadLuminariesFromDisk();   // U2: data-driven bodies become the composer's source of truth (before compose)
        BuildPanel();
        ApplyAll();              // push all defaults to the shader (also fixes the .Value-doesn't-fire issue)
        // Apply a default MOOD on spawn so the startup look matches picking a preset.
        // Without this, spawn used the raw .tscn env (no per-mood grade/fog/sun) and
        // looked worse than any preset — "presets good, spawn not good." Skipped when
        // a CLI --mood override is set (headless captures choose their own).
        if (_probeMood < 0 && _moods.Count > 0) { ApplyDefaultMood(); }
        ApplyCliOverrides();
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--luminarycheck") >= 0) { _lumCheckT = 0.0; }   // U2 numeric gate arm
        _ready = true;
    }

    /// S2b: deferred --testpath=N start. Runs after the _testPaths sibling-add + SetupTestPaths (both
    /// deferred from TerrainLab.Build) so the path player is set up. Enables CDLOD (the harness needs the
    /// quadtree ticking), then starts the flight with cliQuit so the run prints its report and exits.
    private void StartTestPathDeferred() { _terrain.SetCdlod(true); _terrain.RunTestPath(_testPathCli, cliQuit: true); }

    // S3 --popmeter: on-screen HUD line for the live pop meter (top-left under the panel area, large + outlined).
    private void BuildPopMeterHud()
    {
        _popMeterLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "POPMETER  arming…",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _popMeterLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _popMeterLabel.OffsetTop = 40; _popMeterLabel.OffsetRight = -8; _popMeterLabel.OffsetLeft = -700;
        _popMeterLabel.AddThemeFontSizeOverride("font_size", 16);
        _popMeterLabel.AddThemeColorOverride("font_color", new Color(0.6f, 1f, 0.7f));
        _popMeterLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        _popMeterLabel.AddThemeConstantOverride("outline_size", 6);
        var layer = GetNodeOrNull<CanvasLayer>("/root/TerrainLabRoot/UILayer");
        Node parent = (Node)layer ?? this;
        parent.CallDeferred(Node.MethodName.AddChild, _popMeterLabel);   // tree is mid-setup in _Ready → defer the add
    }

    // S3 --popmeter: per-frame live measurement. Lazily builds the meter once CdlodTerrain exists (deferred).
    private void TickPopMeter(Vector3 camPos)
    {
        if (!_popMeterCli) { return; }
        if (_popMeter == null)
        {
            if (_terrain.Cdlod == null) { return; }   // CdlodTerrain is deferred-added; wait for it
            _popMeter = new LivePopMeter(_fc, _params, _terrain.Cdlod, 65, 2.5f);
        }
        _popMeter.Tick(camPos);
        if (_popMeterLabel != null) { _popMeterLabel.Text = _popMeter.Hud; }
    }

    /// Deferred cloud wiring (see _Ready). Adds the CloudVolume node to the scene
    /// root and attaches it to the Environment, now that tree setup has finished.
    private void AttachClouds()
    {
        if (_cloud == null) { return; }
        GetNode("/root/TerrainLabRoot").AddChild(_cloud);
        _cloud.Attach(GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment,
                      GetNode<Camera3D>("/root/TerrainLabRoot/Camera"), _params.RegionSizeM);
        _cloud.SetGroundHeight(_terrain.MidHeight);   // M4: shadow march from terrain mid-elevation
        // bind the cloud-shadow map to the terrain material so light() can sample it
        if (_cloud.ShadowTexture != null)
        {
            _terrain.SetTexture("cloud_shadow_tex", _cloud.ShadowTexture);
            _terrain.SetFloat("cloud_shadow_region", _cloud.RegionSize);
            // L2 fix: keep shadow sampling OFF until the render-thread RID is live
            // (avoids the terrain sampling an empty shadow Texture2Drd on frame 1).
            // _Process turns it on once _cloud.ComputeReady.
            _terrain.SetBool("cloud_shadow_on", false);
        }
        // GOD RAYS (2026-06-19): screen-space radial scatter (GPU Gems 3) is THE god-ray layer. The
        // froxel-fog approach was dropped — it read as a washy fog, not crisp beams (3 attempts).
        // Driven by the Clouds-tab "god rays" toggle + "god ray strength"; default OFF.
        if (_godraysScreen != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_godraysScreen);
            _godraysScreen.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"),
                                  GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
            // cloud-field occlusion: same shadow map the terrain samples, projected to the cloud deck.
            _godraysScreen.SetShadowTexture(_cloud.ShadowTexture, _cloud.RegionSize);
            _godraysScreen.SetCloudAltitude(_terrain.MidHeight + _cloud.Params.AltitudeM);
        }
        // AT-1 GPU atmosphere: render-thread LUT producer (Hillaire). Deferred-add like the cloud node.
        // Default OFF — produces the sky-view Texture2Drd but it's only sampled when atmosphere_on.
        if (_atmosphere != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_atmosphere);
            _atmosphere.Attach();
            _cloud.SetAtmosphereSkyView(_atmosphere.SkyViewTexture);   // bind the (empty-RID) Texture2Drd now; RID fills on the render thread
        }
        // AT-2 aerial perspective: screen-space composite that samples the atmosphere's aerial froxel LUT.
        // Default ON (with the atmosphere); the pass stays disabled until the LUT RID is live (_Process gate).
        if (_aerial != null && _atmosphere != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_aerial);
            _aerial.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"));
            _aerial.SetAerialTexture(_atmosphere.AerialTexture);   // bind the (empty-RID) Texture3Drd now; RID fills on the render thread
            _aerial.SetStrength(0.4f);                             // in-scatter gain. Subtle: ~0.4 reads as gentle distance haze; >1 washes (the inscatter is additive). Gate-tunable via 'aerial strength'.
        }
        // AT-1 atmosphere CLI overrides (after attach, so both nodes are live)
        if (_atmoExpCli >= 0f) { _cloud.SetKnob("atmo_exposure", _atmoExpCli); }
        if (_atmosphereCli == 1) { _atmosphereOn = true; _cloud.SetAtmosphereOn(true); _atmosphere?.SetEnabled(true); }
        else if (_atmosphereCli == 0) { _atmosphereOn = false; _cloud.SetAtmosphereOn(false); _atmosphere?.SetEnabled(false); ComposeLighting(); }   // explicit off (default is now ON)
        if (_atmoCheckCli) { _atmosphere?.RequestCheck(); }
        // AT-2 aerial froxel self-check: needs the atmosphere on (trans/ms LUTs feed the aerial march).
        if (_aerialCheckCli) { _atmosphereOn = true; _cloud.SetAtmosphereOn(true); _atmosphere?.SetEnabled(true); _atmosphere?.RequestAerialCheck(); }
        // AT-2 aerial perspective CLI: =0 turns it off (restores built-in fog via ComposeLighting); strength A/B.
        if (_aerialCli == 0) { _aerialOn = false; _aerial?.SetEnabled(false); ComposeLighting(); }
        if (_aerialStrCli >= 0f) { _aerial?.SetStrength(_aerialStrCli); }
        if (_aerialDbgCli >= 0) { _aerialDbg = _aerialDbgCli; _aerial?.SetDebug(_aerialDbgCli); }   // isolation viz at startup; U-key continues from here
        if (_aerialHazeCli >= 0f) { _aerialHazeOn = _aerialHazeCli > 0f; _aerial?.SetHazeStrength(_aerialHazeCli); }   // haze A/B at startup; Y-key continues
        // AT-3 physical cloud lighting CLI (default ON): =0 turns it off; =1 on. _Process pushes once ready.
        if (_cloudLightStrCli >= 0f) { _cloudLightStr = _cloudLightStrCli; }
        if (_cloudLightCli == 1) { _cloudLightOn = true; _atmosphere?.SetCloudLightWanted(true); _cloudLightActivated = false; }
        else if (_cloudLightCli == 0) { _cloudLightOn = false; _atmosphere?.SetCloudLightWanted(false); _cloud.SetCloudAtmoLight(0f); _cloudLightActivated = false; }
        // cloud CLI overrides apply here (after attach, so _cloud is live)
        if (_cloudDbg >= 0) { _cloud.SetDebug(_cloudDbg); }
        if (_cloudSteps > 0) { _cloud.SetKnobInt("raymarch_steps", _cloudSteps); }
        if (_cloudsOn >= 0) { _cloud.SetKnobBool("enabled", _cloudsOn == 1); }
        if (_temporalCli > 0) { _cloud.SetKnobInt("temporal_frames", _temporalCli); }   // roadmap #4 amortization
        if (_covOverride >= 0f) { _cloud.SetKnob("coverage", _covOverride); }
        if (_perDeckCli >= 0f) { _cloud.SetPerDeck(_perDeckCli); }
        if (_cloudProfileCli.Length > 0)   // --cloudprofile=b,t,a → enable CO-1 vertical profile + set values
        {
            var pp = _cloudProfileCli.Split(',');
            _cloud.SetKnobBool("profile_on", true);
            if (pp.Length > 0 && float.TryParse(pp[0], out float pb)) { _cloud.SetKnob("profile_bottom", pb); }
            if (pp.Length > 1 && float.TryParse(pp[1], out float pt)) { _cloud.SetKnob("profile_top", pt); }
            if (pp.Length > 2 && float.TryParse(pp[2], out float pa)) { _cloud.SetKnob("anvil", pa); }
            GD.Print($"[cloudprofile] CO-1 vertical profile ON ({_cloudProfileCli})");
        }
        if (_stratusCli >= 0f) { _cloud.SetKnob("shape_mode", _stratusCli); GD.Print($"[stratus] shape_mode={_stratusCli}"); }
        if (_cirrusCli >= 0f) { _cloud.SetCirrusOn(true); _cloud.SetCirrus("cirrus_coverage", _cirrusCli); GD.Print($"[cirrus] on cov={_cirrusCli}"); }
        if (_antiRepeatCli >= 0f) { _cloud.SetKnob("anti_repeat", _antiRepeatCli); GD.Print($"[antirepeat] {_antiRepeatCli}"); }
        if (_autoTimeCli >= 0f) { _timeSpeed = _autoTimeCli; _timeRunning = true; GD.Print($"[autotime] day/night cycle ON, {_autoTimeCli} h/s"); }
        if (_deckDbgCli == 1) { _cloud.SetDeckDebug(true); }
        if (_cloudStatsCli) { _cloud.RequestStats(); }
        if (_shadowDbgCli == 1) { _terrain.SetBool("cloud_shadow_debug", true); }   // proof: shadow map on ground
        if (_shadowCheckCli)   // numeric proof: correlate shadow vs cloud-overhead, print PASS/FAIL
        {
            var sunNode = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            // use the SAME "to sun" convention as PushSunToCloud (+Basis.Z), or the check
            // feeds a downward sun and the shadow march bails everywhere (false FAIL).
            Vector3 toSun = sunNode.GlobalTransform.Basis.Z.Normalized();
            float[] checkLayers = _cloud.PackedLayers(out int checkCount);   // production layer stack (profile/shape/anti applied)
            _checkRan = true; _checkPass &= CloudShadowCheck.Run(_cloud.Params, toSun, _cloud.RegionSize, _terrain.MidHeight, Vector2.Zero, checkLayers, checkCount);
        }
        if (_fieldCheckCli) { _checkRan = true; _checkPass &= FieldCheck.Run(_fc, _params); }   // S1: field determinism/parity self-check
        if (_cdlodCheckCli)   // S2a: quadtree neighbor-invariant + stats
        {
            var camCdlod = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            _checkRan = true; _checkPass &= CdlodQuadtree.SelfCheck(_params.RegionSizeM, 6, 2.5f, camCdlod.GlobalPosition);
        }
        if (_morphCheckCli)   // S2b: geomorph pop-free numeric backstop (mirrors ground.gdshader morph math)
        {
            // Test across the actual leaf-size ladder (region/2^depth: 128..8192 m at GridN=65, MaxDepth=6).
            // The pop-free property is scale-invariant, but checking each size confirms it holds end to end.
            bool allOk = true; string worstMsg = "ok";
            foreach (float s in new float[] { 128f, 256f, 512f, 1024f, 2048f, 4096f })
            {
                bool ok = MorphCheck.Run(65, 2.5f, s, out string m);
                if (!ok) { allOk = false; worstMsg = $"size={s:F0}m: {m}"; break; }
                worstMsg = $"size={s:F0}m: {m}";   // keep the last (largest) PASS message for the report
            }
            GD.Print($"MORPHCHECK: {(allOk ? "PASS" : "FAIL")}  {worstMsg}");
            _checkRan = true; _checkPass &= allOk;
        }
        if (_stitchCheckCli)   // S2d: edge-stitch seam-coincidence (welded fine edge on the coarse neighbor's lattice)
        {
            var camStitch = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            bool ok = StitchCheck.Run(_params.RegionSizeM, 6, 2.5f, 65, camStitch.GlobalPosition, out string m);
            GD.Print($"STITCHCHECK: {(ok ? "PASS" : "FAIL")}  {m}");
            _checkRan = true; _checkPass &= ok;
        }
        if (_streamCheckCli)   // S3: streaming invariant-along-traverse + renderOrigin snap field-continuity
        {
            bool ok = StreamCheck.Run(_params.RegionSizeM, 6, 2.5f, out string m);
            GD.Print($"STREAMCHECK: {(ok ? "PASS" : "FAIL")}  {m}");
            _checkRan = true; _checkPass &= ok;
        }
        if (_popCheckCli)   // S3: GPU ground-truth pop detector — rendered height + normal at a fixed point across LOD swaps
        {
            bool ok = PopCheck.Run(_fc, _params, 6, 2.5f, 65, out string m);
            GD.Print($"POPCHECK: {(ok ? "PASS" : "FAIL")}  {m}");
            _checkRan = true; _checkPass &= ok;
        }
        if (_snapDiffCli)   // S3: does a fixed world point get the SAME leaf across a renderOrigin snap? (the snap-pop)
        {
            bool ok = SnapDiff.Run(_params.RegionSizeM, 6, 2.5f, out string m);
            GD.Print($"SNAPDIFF: {(ok ? "PASS" : "FAIL")}  {m}");
            _checkRan = true; _checkPass &= ok;
        }
        if (_aabbSpikeCli)   // S3.5 SPIKE: kick off one async height-range; _Process collects + compares to sync
        {
            _spikeProvider = new ChunkAabbProvider(_params) { ProbeRes = 7 };
            _spikeProvider.Request(1L, new Vector2(0f, 0f), 2048f);   // one known chunk footprint at the origin
            _spikeFrame = 0;
        }
        if (_lightCheckCli)   // numeric proof: quantify per-deck lighting difference (cumulus vs cirrus)
        {
            var sunNode = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            var layers = CloudLayers.Load();
            while (layers.Count < 2) { layers.Add(default); }
            // same single source of truth as the renderer (CloudVolume.PackLayers) so the
            // check matches what's actually rendered for the knob-driven cumulus deck.
            var cumulus = CloudLayers.WithCumulusLighting(layers[0]);
            CloudLightCheck.Run(cumulus, layers[1], sunNode.LightColor, sunNode.LightEnergy,
                                _cloud.Params.Brightness, _cloud.Params.HgAniso);
        }
        // Exit-code the one-shot regression-gate self-checks (audit #1): if any ran, quit NOW with a
        // non-zero code on FAIL so CI/scripts can actually gate on them (was print-only → always exit 0).
        if (_checkRan)
        {
            GD.Print($"SELFCHECKS: {(_checkPass ? "ALL PASS" : "FAIL")} — exit {(_checkPass ? 0 : 1)}");
            GetTree().Quit(_checkPass ? 0 : 1);
            return;
        }
        // H3 fix: mood + sun were applied in _Ready BEFORE this deferred attach, so the
        // cloud's sky/sun pushes no-opped (material/env null). Re-apply now that _cloud
        // is live, so clouds track the spawn mood/sun instead of CloudParams defaults.
        if (_currentMood >= 0) { ApplyMood(_currentMood); }
        // --preset AFTER the spawn mood: a preset may set its own sun (sun_angle/azimuth) + cloud knobs,
        // and the mood re-apply above would otherwise stomp them (the showcase's low sun was lost this way).
        if (_presetCli >= 0) { ApplyCloudPreset(_presetCli); }
        if (_sunPresetCli >= 0) { ApplySunPreset(_sunPresetCli); } else { ApplyActiveSunPreset(); }
        if (_celestialCli >= 0) { ApplyCelestialPreset(_celestialCli); } else { ApplyActiveCelestialPreset(); }
        if (_fantasyCli >= 0) { ApplyFantasyPreset(_fantasyCli); }   // ST4-2 exotic sky override at launch
        if (_covOverride >= 0f) { _cloud.SetKnob("coverage", _covOverride); }   // --coverage still overrides the preset
        PushSunToCloud(GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
        // --godrays drives the screen-space beam layer (GodRaysScreen reads the sun per-frame).
        if (_godraysOnCli >= 0) { _godraysScreen?.SetEnabled(_godraysOnCli == 1); }
        if (_godrayDbgCli != 0) { _godraysScreen?.SetDebug(_godrayDbgCli); }   // --godraydbg=N diagnostic
        if (_godrayHpCli >= 0f) { _godraysScreen?.SetHighpass(_godrayHpCli); }   // --godrayhp=N A/B the high-pass
        if (_glowCli >= 0) { GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.GlowEnabled = _glowCli == 1; }   // --glow diagnostic
        if (_nightDarkCli >= 0f) { _time.NightDarkness = _nightDarkCli; }   // --nightdark=N: night brightness lever (A/B)
        if (_moonPhaseCli >= 0f) { _moon.Phase = _moonPhaseCli; }           // --moonphase=N: moon phase (A/B)
        if (_sunsCli >= 1) { _lighting.ExtraSunCount = _sunsCli - 1; }   // --suns=N → N-1 C3 extra suns (set before DriveTime so the first compose shows them)
        if (_moonsCli >= 1) { _lighting.ExtraMoonCount = _moonsCli - 1; }   // --moons=N → N-1 C3 extra moons
        if (_timeCli >= 0f) { DriveTime(_timeCli); }   // --time=H drives the decoupled Time axis (overrides the spawn mood's sun/sky)
        // --lookatsun: aim the camera straight at the sun (for god-ray verification — removes the
        // guesswork of matching --cam yaw to the sun azimuth).
        if (_lookAtSunCli)
        {
            var sunN = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            var camN = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            Vector3 toSun = sunN.GlobalTransform.Basis.Z.Normalized();   // +Basis.Z = toward sun
            camN.LookAt(camN.GlobalPosition + toSun, Vector3.Up);
        }
        // --lookatmoon: aim the camera at the moon (anti-solar, often high) for the 3b moon gate.
        if (_lookAtMoonCli && _lastMoonDir != Vector3.Zero)
        {
            var camN = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            camN.LookAt(camN.GlobalPosition + _lastMoonDir, Vector3.Up);
        }
        if (_meteorDebugCli) { _cloud?.SetMeteorDebug(true); }   // C2: force a meteor streak for headless capture
        // --review=N: drive a review preset at startup (verify/screenshot the keypress path headlessly). LAST so it wins.
        if (_reviewCli > 0) { ApplyReview(_reviewCli); }
    }

    public override void _ExitTree() => _fc?.Dispose();
}

internal static class DictExt
{
    public static Godot.Collections.Dictionary ToGodotDictionary(this Dictionary<string, Variant> d)
    {
        var g = new Godot.Collections.Dictionary();
        foreach (var kv in d) { g[kv.Key] = kv.Value; }
        return g;
    }
}
