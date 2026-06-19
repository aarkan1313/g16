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

    /// One control: parsed registry fields + runtime state.
    private sealed class LabControl
    {
        public string Id = "", Label = "", Tab = "", Type = "";
        public string? Param, Setter, Field, Scene, Cloud;  // shader uniform / mode-setter / TerrainLab field / scene-node target / CloudVolume knob
        public float Min, Max, Default;
        public bool DefBool;
        public string[] Options = Array.Empty<string>();
        public bool Rand = true, Rebake;
        public int Zone = -1;                     // for material/companion (0..6), else -1
        public Variant Value;                     // current value
        public bool Locked;
        public CheckBox? LockBox;
        public Control? Widget;                   // the editing control (slider/checkbox/dropdown)
        public Label? ValLabel;
    }

    public override void _Ready()
    {
        _fc = new FieldCompute();
        _params = FieldParams.Load();
        _terrain = GetNode<TerrainLab>("/root/TerrainLabRoot/TerrainLab");
        _terrain.Build(_fc, _params);

        // Cloud subsystem: create the node + add/attach DEFERRED — the tree is mid
        // setup during _Ready ("parent busy setting up children"), so both the
        // AddChild and the Attach must run after the current frame's setup.
        _cloud = new CloudVolume { Name = "CloudVolume" };
        _godrays = new GodRaysVolumetric { Name = "GodRaysVolumetric" };   // canonical sun-shadow + cloud-caster god rays
        CallDeferred(nameof(AttachClouds));

        ParseCli();
        LoadLibrary();
        LoadMoods();
        LoadRegistry();
        BuildPanel();
        ApplyAll();              // push all defaults to the shader (also fixes the .Value-doesn't-fire issue)
        // Apply a default MOOD on spawn so the startup look matches picking a preset.
        // Without this, spawn used the raw .tscn env (no per-mood grade/fog/sun) and
        // looked worse than any preset — "presets good, spawn not good." Skipped when
        // a CLI --mood override is set (headless captures choose their own).
        if (_probeMood < 0 && _moods.Count > 0) { ApplyDefaultMood(); }
        ApplyCliOverrides();
        _ready = true;
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
        // GOD RAYS (canonical rebuild 2026-06-19) — a cloud-shadow-caster quad puts the cloud shapes
        // into the DirectionalLight's shadow atlas; global volfog lit by that real shadow makes 3D
        // cloud-shaped shafts on terrain + fog. Consumes the SAME cloud shadow map + the scene sun.
        if (_godrays != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_godrays);
            _godrays.Attach(GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment,
                            GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
            _godrays.SetShadowTexture(_cloud.ShadowTexture, _cloud.RegionSize);
        }
        // cloud CLI overrides apply here (after attach, so _cloud is live)
        if (_cloudDbg >= 0) { _cloud.SetDebug(_cloudDbg); }
        if (_cloudSteps > 0) { _cloud.SetKnobInt("raymarch_steps", _cloudSteps); }
        if (_cloudsOn >= 0) { _cloud.SetKnobBool("enabled", _cloudsOn == 1); }
        if (_presetCli >= 0) { ApplyCloudPreset(_presetCli); }   // before coverage so --coverage can still override for testing
        if (_temporalCli > 0) { _cloud.SetKnobInt("temporal_frames", _temporalCli); }   // roadmap #4 amortization
        if (_covOverride >= 0f) { _cloud.SetKnob("coverage", _covOverride); }
        if (_perDeckCli >= 0f) { _cloud.SetPerDeck(_perDeckCli); }
        if (_deckDbgCli == 1) { _cloud.SetDeckDebug(true); }
        if (_cloudStatsCli) { _cloud.RequestStats(); }
        if (_godraysOnCli >= 0) { _godrays?.SetEnabled(_godraysOnCli == 1); }   // --godrays drives the canonical volumetric base
        if (_shadowDbgCli == 1) { _terrain.SetBool("cloud_shadow_debug", true); }   // proof: shadow map on ground
        if (_shadowCheckCli)   // numeric proof: correlate shadow vs cloud-overhead, print PASS/FAIL
        {
            var sunNode = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            // use the SAME "to sun" convention as PushSunToCloud (+Basis.Z), or the check
            // feeds a downward sun and the shadow march bails everywhere (false FAIL).
            Vector3 toSun = sunNode.GlobalTransform.Basis.Z.Normalized();
            CloudShadowCheck.Run(_cloud.Params, toSun, _cloud.RegionSize, _terrain.MidHeight, Vector2.Zero);
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
        // H3 fix: mood + sun were applied in _Ready BEFORE this deferred attach, so the
        // cloud's sky/sun pushes no-opped (material/env null). Re-apply now that _cloud
        // is live, so clouds track the spawn mood/sun instead of CloudParams defaults.
        if (_currentMood >= 0) { ApplyMood(_currentMood); }
        PushSunToCloud(GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
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
