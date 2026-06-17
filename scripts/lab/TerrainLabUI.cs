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
    private OptionButton _presetPick = null!;
    private OptionButton? _moodPick;
    private LineEdit _presetName = null!;
    private Dictionary<string, Variant> _presets = new();
    private System.Random _rng = new();
    private bool _ready;   // suppress callbacks while building/applying

    // Headless A/B capture + overrides (CLI verification), unchanged contract.
    private string? _autoShotPath;
    private double _autoShotT = -1.0;
    private int _overrideBlend = -1, _overrideMask = -1, _overrideTile = -1, _overrideMacro = -1, _overrideContact = -1;
    private int _overrideSplat = -1, _overrideSplatDebug = -1;
    private string? _camArg;
    private float _texScale = -1f;
    private int _probeSsao = -1, _probeShadow = -1, _probeHb = -1, _probeMood = -1;   // lighting/splat isolation
    private float _probeRoughFloor = -1f, _probeMixStr = -1f;

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
                      GetNode<Camera3D>("/root/TerrainLabRoot/Camera"));
        // bind the cloud-shadow map to the terrain material so light() can sample it
        if (_cloud.ShadowTexture != null)
        {
            _terrain.SetTexture("cloud_shadow_tex", _cloud.ShadowTexture);
            _terrain.SetFloat("cloud_shadow_region", _cloud.RegionSize);
            _terrain.SetBool("cloud_shadow_on", _cloud.Enabled);
        }
        // gap-aligned god rays: add the cloud-shadow-gated FogVolume (default OFF;
        // volumetric fog only enabled when god rays are toggled on, so the base look
        // is untouched until the user opts in + tunes it live).
        var genv = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        genv.VolumetricFogDensity = 0.0f;        // base fog 0 — the FogVolume supplies density in gaps
        var fog = _cloud.BuildGodrayVolume();
        GetNode("/root/TerrainLabRoot").AddChild(fog);

        // cloud CLI overrides apply here (after attach, so _cloud is live)
        if (_cloudDbg >= 0) { _cloud.SetDebug(_cloudDbg); }
        if (_cloudSteps > 0) { _cloud.SetKnobInt("raymarch_steps", _cloudSteps); }
        if (_cloudsOn >= 0) { _cloud.SetKnobBool("enabled", _cloudsOn == 1); }
        if (_covOverride >= 0f) { _cloud.SetKnob("coverage", _covOverride); }
        if (_godraysOnCli >= 0) { _cloud.SetGodraysEnabled(_godraysOnCli == 1); }
    }
    private float _covOverride = -1f;
    private int _godraysOnCli = -1;

    private const int DefaultMoodIdx = 5;   // "Clear Alpine" — clean neutral good-day look
    private void ApplyDefaultMood()
    {
        int idx = Mathf.Clamp(DefaultMoodIdx, 0, _moods.Count - 1);
        ApplyMood(idx);
        if (_moodPick != null) { _moodPick.Select(idx); }   // reflect it in the dropdown
    }

    private void ParseCli()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--auto-shot=")) { _autoShotPath = a.Substring("--auto-shot=".Length); _autoShotT = 0.0; }
            else if (a.StartsWith("--blend=")) { int.TryParse(a.Substring("--blend=".Length), out _overrideBlend); }
            else if (a.StartsWith("--mask=")) { int.TryParse(a.Substring("--mask=".Length), out _overrideMask); }
            else if (a.StartsWith("--tile=")) { int.TryParse(a.Substring("--tile=".Length), out _overrideTile); }
            else if (a.StartsWith("--macro=")) { _overrideMacro = a.Substring("--macro=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--contact=")) { _overrideContact = a.Substring("--contact=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splat=")) { _overrideSplat = a.Substring("--splat=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splatdebug=")) { int.TryParse(a.Substring("--splatdebug=".Length), out _overrideSplatDebug); }
            else if (a.StartsWith("--cam=")) { _camArg = a.Substring("--cam=".Length); }
            else if (a.StartsWith("--texscale=")) { if (float.TryParse(a.Substring("--texscale=".Length), out float ts)) _texScale = ts; }
            else if (a.StartsWith("--ssao=")) { _probeSsao = a.Substring("--ssao=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--shadow=")) { _probeShadow = a.Substring("--shadow=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--roughfloor=")) { if (float.TryParse(a.Substring("--roughfloor=".Length), out float rf)) _probeRoughFloor = rf; }
            else if (a.StartsWith("--mixstr=")) { if (float.TryParse(a.Substring("--mixstr=".Length), out float ms)) _probeMixStr = ms; }
            else if (a.StartsWith("--hb=")) { _probeHb = a.Substring("--hb=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--mood=")) { int.TryParse(a.Substring("--mood=".Length), out _probeMood); }
            else if (a.StartsWith("--clouddbg=")) { int.TryParse(a.Substring("--clouddbg=".Length), out _cloudDbg); }
            else if (a.StartsWith("--cloudsteps=")) { int.TryParse(a.Substring("--cloudsteps=".Length), out _cloudSteps); }
            else if (a.StartsWith("--clouds=")) { _cloudsOn = a.Substring("--clouds=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--coverage=")) { float.TryParse(a.Substring("--coverage=".Length), out _covOverride); }
            else if (a.StartsWith("--godrays=")) { _godraysOnCli = a.Substring("--godrays=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--profile")) { _profileT = 0.0; if (a.Contains("=") && double.TryParse(a.Substring(a.IndexOf('=')+1), out double d)) _profileDur = d;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0; }
        }
    }

    private void LoadLibrary()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/material_library.json");
        if (System.IO.File.Exists(abs))
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            foreach (JsonElement m in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                _materials.Add(m.GetProperty("name").GetString() ?? "");
            }
        }
        _materials.Sort();
    }

    private void LoadRegistry()
    {
        string abs = ProjectSettings.GlobalizePath(RegistryPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement root = doc.RootElement;
        _zoneNames = root.GetProperty("zone_names").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

        foreach (JsonElement c in root.GetProperty("controls").EnumerateArray())
        {
            string type = c.GetProperty("type").GetString() ?? "";
            if (type == "material" || type == "companion")
            {
                int[] defs = c.TryGetProperty("default", out var dArr)
                    ? dArr.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null;
                for (int z = 0; z < _zoneNames.Length; z++)
                {
                    var lc = BaseControl(c, type);
                    lc.Zone = z;
                    lc.Label = _zoneNames[z];
                    if (type == "companion") { lc.Default = defs != null ? defs[z] : Math.Max(0, z - 1); }
                    Register(lc, $"{lc.Id}#{z}");
                }
            }
            else
            {
                var lc = BaseControl(c, type);
                Register(lc, lc.Id);
            }
        }
    }

    private LabControl BaseControl(JsonElement c, string type)
    {
        var lc = new LabControl
        {
            Id = c.GetProperty("id").GetString() ?? "",
            Label = c.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            Tab = c.GetProperty("tab").GetString() ?? "",
            Type = type,
            Param = c.TryGetProperty("param", out var p) ? p.GetString() : null,
            Setter = c.TryGetProperty("setter", out var s) ? s.GetString() : null,
            Field = c.TryGetProperty("field", out var f) ? f.GetString() : null,
            Scene = c.TryGetProperty("scene", out var sc) ? sc.GetString() : null,
            Cloud = c.TryGetProperty("cloud", out var cl) ? cl.GetString() : null,
            Rand = !c.TryGetProperty("rand", out var r) || r.GetBoolean(),
            Rebake = c.TryGetProperty("rebake", out var rb) && rb.GetBoolean(),
        };
        if (c.TryGetProperty("min", out var mn)) { lc.Min = mn.GetSingle(); }
        if (c.TryGetProperty("max", out var mx)) { lc.Max = mx.GetSingle(); }
        if (c.TryGetProperty("options", out var op)) { lc.Options = op.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
        if (c.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Array)
        {
            if (type == "toggle" || type == "scene" || type == "cloud") { lc.DefBool = d.GetBoolean(); }
            else { lc.Default = d.GetSingle(); }
        }
        return lc;
    }

    private void Register(LabControl lc, string key)
    {
        _controls.Add(lc);
        _byId[key] = lc;
    }

    // ---- panel ----------------------------------------------------------------

    private void BuildPanel()
    {
        var panel = new PanelContainer { Position = new Vector2(8, 8) };
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.CustomMinimumSize = new Vector2(380, 0);
        // cap height so the TabContainer scrolls instead of running off-screen
        panel.SetAnchorAndOffset(Side.Bottom, 0, 0);
        panel.OffsetTop = 8; panel.OffsetBottom = -8; panel.OffsetLeft = 8;
        AddChild(panel);

        var outer = new VBoxContainer();
        panel.AddChild(outer);
        outer.AddChild(new Label { Text = "TERRAIN LOOK LAB" });

        // top bar: Randomize + lock all/none
        var bar = new HBoxContainer();
        var rnd = new Button { Text = "🎲 Randomize" };
        rnd.Pressed += Randomize;
        bar.AddChild(rnd);
        var lockAll = new Button { Text = "Lock all" };
        lockAll.Pressed += () => SetAllLocks(true);
        bar.AddChild(lockAll);
        var lockNone = new Button { Text = "Unlock all" };
        lockNone.Pressed += () => SetAllLocks(false);
        bar.AddChild(lockNone);
        outer.AddChild(bar);

        var bar2 = new HBoxContainer();
        var flat = new Button { Text = "⬛ FLAT BASELINE (fuzz hunt)" };
        flat.Pressed += FlatBaseline;
        bar2.AddChild(flat);
        outer.AddChild(bar2);

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(360, 560) };
        tabs.SizeFlagsVertical = SizeFlags.ExpandFill;
        outer.AddChild(tabs);

        foreach (string tabName in TabOrder())
        {
            var scroll = new ScrollContainer { Name = tabName, CustomMinimumSize = new Vector2(360, 0) };
            scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            tabs.AddChild(scroll);
            var col = new VBoxContainer();
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(col);

            // Per-tab Randomize + Lock/Unlock (operate only on this tab's controls).
            string tn = tabName;   // capture for closures
            var tabBar = new HBoxContainer();
            var tabRnd = new Button { Text = "🎲 tab" };
            tabRnd.Pressed += () => RandomizeTab(tn);
            tabBar.AddChild(tabRnd);
            var tabLock = new Button { Text = "🔒 tab" };
            tabLock.Pressed += () => SetTabLocks(tn, true);
            tabBar.AddChild(tabLock);
            var tabUnlock = new Button { Text = "🔓 tab" };
            tabUnlock.Pressed += () => SetTabLocks(tn, false);
            tabBar.AddChild(tabUnlock);
            col.AddChild(tabBar);

            // The Clouds tab leads with a cloud PRESET picker (named sky looks).
            if (tabName == "Clouds") { BuildCloudPresetPicker(col); }

            // The Light tab leads with a MOOD selector (curated coordinated looks);
            // the sliders below are live fine-tuning on top of the picked mood.
            if (tabName == "Light" && _moodNames.Count > 0)
            {
                col.AddChild(new Label { Text = "MOOD (pick a vibe — tunes everything)" });
                _moodPick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
                for (int i = 0; i < _moodNames.Count; i++) { _moodPick.AddItem(_moodNames[i], i); }
                _moodPick.ItemSelected += idx => ApplyMood((int)idx);
                col.AddChild(_moodPick);
                col.AddChild(new HSeparator());
                col.AddChild(new Label { Text = "fine-tune:" });
            }
            foreach (LabControl c in _controls.Where(c => c.Tab == tabName)) { BuildRow(col, c); }
        }

        BuildPresetsTab(tabs);

        // Frame-time HUD, top-right (independent of the panel).
        _fpsLabel = new Label { Text = "— fps", Position = new Vector2(0, 8) };
        _fpsLabel.SetAnchorsPreset(LayoutPreset.TopRight);
        _fpsLabel.OffsetLeft = -150; _fpsLabel.OffsetRight = -8; _fpsLabel.OffsetTop = 8;
        _fpsLabel.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_fpsLabel);
    }

    private IEnumerable<string> TabOrder()
        => _controls.Select(c => c.Tab).Distinct();

    private void BuildRow(VBoxContainer col, LabControl c)
    {
        var row = new HBoxContainer();
        // per-control lock
        var lockBox = new CheckBox { TooltipText = "lock (skip on randomize)", CustomMinimumSize = new Vector2(28, 0) };
        lockBox.Toggled += on => c.Locked = on;
        c.LockBox = lockBox;
        row.AddChild(lockBox);
        row.AddChild(new Label { Text = c.Label, CustomMinimumSize = new Vector2(90, 0) });

        switch (c.Type)
        {
            case "slider":
            case "scenef":
            case "cloudf":
            case "cloudi":
            {
                // fine format for tiny ranges (e.g. fog density 0.0006)
                string fmt = (c.Type == "cloudi") ? "0" : ((c.Max - c.Min) < 0.05f ? "0.0000" : "0.00");
                var sl = new HSlider { MinValue = c.Min, MaxValue = c.Max, Value = c.Default,
                    Step = (c.Max - c.Min) / 400.0, CustomMinimumSize = new Vector2(160, 0) };
                sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                var vlbl = new Label { Text = c.Default.ToString(fmt), CustomMinimumSize = new Vector2(52, 0) };
                sl.ValueChanged += v => { c.Value = (float)v; vlbl.Text = ((float)v).ToString(fmt); if (_ready) ApplyControl(c, true); };
                c.Value = c.Default; c.Widget = sl; c.ValLabel = vlbl;
                row.AddChild(sl); row.AddChild(vlbl);
                break;
            }
            case "toggle":
            case "scene":
            case "cloud":
            {
                var cb = new CheckBox { ButtonPressed = c.DefBool };
                cb.Toggled += on => { c.Value = on; if (_ready) ApplyControl(c, true); };
                c.Value = c.DefBool; c.Widget = cb;
                row.AddChild(cb);
                break;
            }
            case "enum":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < c.Options.Length; i++) { ob.AddItem(c.Options[i], i); }
                ob.Select((int)c.Default);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = (int)c.Default; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
            case "material":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < _materials.Count; i++) { ob.AddItem(_materials[i], i); }
                int start = ZoneDefaultMaterialIndex(c.Zone);
                ob.Select(start);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = start; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
            case "companion":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < _zoneNames.Length; i++) { ob.AddItem(_zoneNames[i], i); }
                ob.Select((int)c.Default);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = (int)c.Default; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
        }
        col.AddChild(row);
    }

    private void BuildPresetsTab(TabContainer tabs)
    {
        var scroll = new ScrollContainer { Name = "Presets" };
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        tabs.AddChild(scroll);
        var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(vb);

        var rebakeBtn = new Button { Text = "Rebake splat" };
        rebakeBtn.Pressed += () => _terrain.RebakeSplat();
        vb.AddChild(rebakeBtn);

        var prow = new HBoxContainer();
        _presetName = new LineEdit { PlaceholderText = "preset name", CustomMinimumSize = new Vector2(160, 0) };
        prow.AddChild(_presetName);
        var saveBtn = new Button { Text = "Save" };
        saveBtn.Pressed += SavePreset;
        prow.AddChild(saveBtn);
        vb.AddChild(prow);

        var lrow = new HBoxContainer();
        _presetPick = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        lrow.AddChild(_presetPick);
        var loadBtn = new Button { Text = "Load" };
        loadBtn.Pressed += LoadSelectedPreset;
        lrow.AddChild(loadBtn);
        vb.AddChild(lrow);

        vb.AddChild(new Label { Text = "RMB/LMB+WASD fly · wheel speed" });
        LoadPresetsFromDisk();

        // --- Hero SHOTS: composition is a top 'good->great' lever. Fly to a framing
        // you like, save it; reload anytime. Seeded with a few decent vantages. ---
        vb.AddChild(new HSeparator());
        vb.AddChild(new Label { Text = "HERO SHOTS (camera framing)" });
        var srow = new HBoxContainer();
        _shotPick = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        srow.AddChild(_shotPick);
        var goBtn = new Button { Text = "Go" };
        goBtn.Pressed += GoToShot;
        srow.AddChild(goBtn);
        var saveShot = new Button { Text = "Save view" };
        saveShot.Pressed += SaveShot;
        srow.AddChild(saveShot);
        vb.AddChild(srow);
        SeedShots();
        RefreshShotList();
    }

    // ---- hero shots (camera viewpoints) --------------------------------------
    private OptionButton _shotPick = null!;
    private readonly List<(string name, Vector3 pos, Vector3 rot)> _shots = new();

    private void SeedShots()
    {
        // a few decent starting vantages found while probing (low-angle, depth).
        _shots.Add(("ridge vista", new Vector3(-1800, 120, -1200), new Vector3(4, 55, 0)));
        _shots.Add(("misty dawn", new Vector3(800, 90, 2600), new Vector3(6, 180, 0)));
        _shots.Add(("high overlook", new Vector3(0, 650, 1900), new Vector3(-14, 20, 0)));
    }
    private void RefreshShotList()
    {
        _shotPick.Clear();
        for (int i = 0; i < _shots.Count; i++) { _shotPick.AddItem(_shots[i].name, i); }
    }
    private void GoToShot()
    {
        int i = _shotPick.Selected;
        if (i < 0 || i >= _shots.Count) { return; }
        var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
        cam.Position = _shots[i].pos;
        cam.RotationDegrees = _shots[i].rot;
    }
    private void SaveShot()
    {
        var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
        _shots.Add(($"shot{_shots.Count + 1}", cam.Position, cam.RotationDegrees));
        RefreshShotList();
        _shotPick.Select(_shots.Count - 1);
        GD.Print($"TerrainLab: saved view (pos {cam.Position}, rot {cam.RotationDegrees})");
    }

    private int ZoneDefaultMaterialIndex(int zone)
    {
        string[] wanted = { "m8_grass_calm", "12_dry_lichen_carpet", "14_scree_with_lichen",
                            "13_dry_loose_scree", "01_dark_slate", "wgv3_alpine_scree", "01_fresh_powder" };
        int idx = (zone >= 0 && zone < wanted.Length) ? _materials.IndexOf(wanted[zone]) : -1;
        if (idx < 0) { idx = Math.Min(Math.Max(zone, 0), _materials.Count - 1); }
        return Math.Max(idx, 0);
    }

    // ---- apply ----------------------------------------------------------------

    private void ApplyAll()
    {
        foreach (LabControl c in _controls) { ApplyControl(c, false); }
        _terrain.PushSecondaryZones();
        _terrain.RebakeSplat();
    }

    /// Push one control's current value to the shader/terrain. rebakeIfNeeded: when
    /// a 'rebake' control (mask structure) changes interactively, re-run the bake.
    private void ApplyControl(LabControl c, bool rebakeIfNeeded)
    {
        switch (c.Type)
        {
            case "slider":
                if (c.Field != null) { SetTerrainField(c.Field, c.Value.AsSingle()); }
                else if (c.Param != null) { _terrain.SetFloat(c.Param, c.Value.AsSingle()); }
                break;
            case "cloudf":
                if (c.Cloud != null) { ApplyCloudFloat(c.Cloud, c.Value.AsSingle()); }
                break;
            case "cloudi":
                if (c.Cloud != null) { ApplyCloudInt(c.Cloud, Mathf.RoundToInt(c.Value.AsSingle())); }
                break;
            case "cloud":
                if (c.Cloud != null) { ApplyCloudBool(c.Cloud, c.Value.AsBool()); }
                break;
            case "toggle":
                if (c.Param != null) { _terrain.SetBool(c.Param, c.Value.AsBool()); }
                break;
            case "enum":
                int iv = c.Value.AsInt32();
                if (c.Setter == "mask") { _terrain.SetMaskMode(iv); }
                else if (c.Setter == "blend") { _terrain.SetBlendMode(iv); }
                else if (c.Param != null) { _terrain.SetInt(c.Param, iv); }
                break;
            case "material":
                _terrain.SetZoneMaterial(c.Zone, _materials[Math.Clamp(c.Value.AsInt32(), 0, _materials.Count - 1)]);
                break;
            case "companion":
                _terrain.SetSecondaryZone(c.Zone, c.Value.AsInt32());
                break;
            case "scene":
                ApplyScene(c.Scene, c.Value.AsBool());
                break;
            case "scenef":
                ApplySceneFloat(c.Scene, c.Value.AsSingle());
                break;
        }
        if (rebakeIfNeeded && c.Rebake) { _terrain.RebakeSplat(); }
    }

    private void SetTerrainField(string field, float v)
    {
        if (field == "MixScaleM") { _terrain.MixScaleM = v; }
        else if (field == "MixBias") { _terrain.MixBias = v; }
    }

    private void ApplyScene(string? target, bool on)
    {
        switch (target)
        {
            case "ssao":   GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.SsaoEnabled = on; break;
            case "fog":    GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.FogEnabled = on; break;
            case "shadow": GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun").ShadowEnabled = on; break;
            case "sun":    GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun").Visible = on; break;
            case "sdfgi":  GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.SdfgiEnabled = on; break;
            case "volfog": GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.VolumetricFogEnabled = on; break;
        }
    }

    private float _sunAngle = 35f, _sunAzimuth = 40f;
    private void ApplySceneFloat(string? target, float v)
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        switch (target)
        {
            case "sun_energy":      sun.LightEnergy = v; _baseSunEnergy = v; PushSunToCloud(sun); break;
            case "sun_soft":        sun.ShadowBlur = v; break;   // shadow softness (separate from disc)
            case "sun_disc":        sun.LightAngularDistance = v; break;   // visible sun size (PCSS penumbra too)
            case "sun_angle":       _sunAngle = v; OrientSun(sun); break;
            case "sun_azimuth":     _sunAzimuth = v; OrientSun(sun); break;
            case "ambient":         env.AmbientLightEnergy = v; break;
            case "ssao_intensity":  env.SsaoIntensity = v; break;
            case "ssao_radius":     env.SsaoRadius = v; break;
            case "fog_density":     env.FogDensity = v; break;
            case "fog_aerial":      env.FogAerialPerspective = v; break;
            case "fog_heightd":     env.FogHeightDensity = v; break;
            case "exposure":        env.TonemapExposure = v; break;
            case "volfog_d":        env.VolumetricFogDensity = v; break;
            case "godray":          sun.LightVolumetricFogEnergy = v; break;
        }
    }
    private void OrientSun(DirectionalLight3D sun)
    {
        // elevation from horizon + compass azimuth → a downward-pointing sun.
        sun.RotationDegrees = new Vector3(-_sunAngle, _sunAzimuth, 0f);
        PushSunToCloud(sun);
    }

    /// Feed the scene sun to the cloud compute (direction TOWARD the sun + color +
    /// energy) so cloud lighting tracks the sun/mood. A DirectionalLight points along
    /// -Z of its basis; the sun is in the opposite direction (-forward).
    private void PushSunToCloud(DirectionalLight3D sun)
    {
        Vector3 toSun = sun.GlobalTransform.Basis.Z.Normalized();   // -(-Z forward) = +Z
        _cloud?.SetSun(toSun, sun.LightColor, sun.LightEnergy);
    }

    // ---- cloud knobs → CloudVolume (separation: UI never touches cloud internals,
    //      only the public knob setters). _cloud is wired in Stage 3; null = no-op,
    //      so the Clouds tab is inert (but present + tunable in state) until then. --
    private CloudVolume? _cloud;
    private void ApplyCloudFloat(string knob, float v) => _cloud?.SetKnob(knob, v);
    private void ApplyCloudInt(string knob, int v) => _cloud?.SetKnobInt(knob, v);
    private void ApplyCloudBool(string knob, bool on)
    {
        if (knob == "godrays") { _cloud?.SetGodraysEnabled(on); return; }
        _cloud?.SetKnobBool(knob, on);
        // clouds-enabled also gates the ground-shadow sampling in the terrain light()
        if (knob == "enabled") { _terrain.SetBool("cloud_shadow_on", on); }
    }

    // ---- cloud PRESETS (named sky looks, data/cloud_presets.json) --------------
    private readonly List<(string name, Godot.Collections.Dictionary values)> _cloudPresets = new();

    private void BuildCloudPresetPicker(VBoxContainer col)
    {
        LoadCloudPresets();
        col.AddChild(new Label { Text = "PRESET (named sky look)" });
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— custom —", 0);
        for (int i = 0; i < _cloudPresets.Count; i++) { pick.AddItem(_cloudPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplyCloudPreset((int)idx - 1); } };
        col.AddChild(pick);
        col.AddChild(new HSeparator());
    }

    private void LoadCloudPresets()
    {
        if (_cloudPresets.Count > 0) { return; }
        string abs = ProjectSettings.GlobalizePath("res://data/cloud_presets.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        foreach (Variant p in root["presets"].AsGodotArray())
        {
            var d = p.AsGodotDictionary();
            string name = d.ContainsKey("name") ? d["name"].AsString() : "preset";
            if (d.ContainsKey("values")) { _cloudPresets.Add((name, d["values"].AsGodotDictionary())); }
        }
    }

    /// Apply a cloud preset by setting each named control's widget (routes through
    /// ApplyControl → CloudVolume). Controls not in the preset are left as-is.
    private void ApplyCloudPreset(int idx)
    {
        if (idx < 0 || idx >= _cloudPresets.Count) { return; }
        var values = _cloudPresets[idx].values;
        foreach (var key in values.Keys)
        {
            string id = key.AsString();
            if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, values[key].AsSingle()); }
        }
        GD.Print($"Clouds: applied preset '{_cloudPresets[idx].name}'");
    }

    // ---- lighting MOODS (curated, coordinated looks) --------------------------

    private readonly List<string> _moodNames = new();
    private Godot.Collections.Array _moods = new();

    private void LoadMoods()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/lighting_moods.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("moods")) { return; }
        _moods = root["moods"].AsGodotArray();
        foreach (Variant m in _moods) { _moodNames.Add(m.AsGodotDictionary()["name"].AsString()); }
    }

    private static Color Col(Variant v) { var a = v.AsGodotArray(); return new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
    private static float F(Godot.Collections.Dictionary d, string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;

    /// Apply a complete coordinated mood: sun, sky, fog, ambient, exposure, glow.
    private void ApplyMood(int idx)
    {
        if (idx < 0 || idx >= _moods.Count) { return; }
        var m = _moods[idx].AsGodotDictionary();
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");

        _sunAngle = F(m, "sun_angle", 35f); _sunAzimuth = F(m, "sun_az", 40f); OrientSun(sun);
        sun.LightEnergy = F(m, "sun_energy", 1.3f);
        if (m.ContainsKey("sun_color")) { sun.LightColor = Col(m["sun_color"]); }
        // Soft shadows via ShadowBlur ONLY. LightAngularDistance also enlarges the
        // sky sun DISC + spreads its energy (that was the "too big & bright" sun), so
        // keep it tiny and let blur do the softening — decoupled.
        float soft = F(m, "shadow_soft", 1.0f);
        sun.ShadowBlur = soft;
        sun.LightAngularDistance = F(m, "sun_disc", 0.6f);   // mood baseline; live slider can override

        env.AmbientLightEnergy = F(m, "ambient", 0.4f);
        env.AmbientLightSkyContribution = F(m, "ambient_sky", 1.0f);
        // remember the mood's BASE lighting so overcast dimming scales from it (not
        // compounding frame-to-frame). Sun energy base captured after it's set below.
        _baseAmbient = env.AmbientLightEnergy;
        _baseSunEnergy = F(m, "sun_energy", 1.3f);
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial sky)
        {
            if (m.ContainsKey("sky_top")) { sky.SkyTopColor = Col(m["sky_top"]); }
            if (m.ContainsKey("sky_horizon")) { sky.SkyHorizonColor = Col(m["sky_horizon"]); sky.GroundHorizonColor = Col(m["sky_horizon"]); }
            if (m.ContainsKey("sky_ground")) { sky.GroundBottomColor = Col(m["sky_ground"]); }
        }
        // Our cloud sky is a ShaderMaterial (not ProceduralSkyMaterial), so feed the
        // mood sky colors to CloudVolume → clouds + their sky track the mood.
        if (_cloud != null)
        {
            Color top = m.ContainsKey("sky_top") ? Col(m["sky_top"]) : new Color(0.30f, 0.48f, 0.74f);
            Color hor = m.ContainsKey("sky_horizon") ? Col(m["sky_horizon"]) : new Color(0.68f, 0.74f, 0.80f);
            Color grd = m.ContainsKey("sky_ground") ? Col(m["sky_ground"]) : new Color(0.22f, 0.26f, 0.22f);
            _cloud.SetSkyColors(top, hor, grd);
        }

        // Fog: the mood JSON values were authored too thick (washed the terrain into
        // haze). Scale WAY down — fog should be a light distance cue, not a blanket.
        // Terrain must read clearly first; atmosphere is seasoning.
        env.FogEnabled = true;
        if (m.ContainsKey("fog_color")) { env.FogLightColor = Col(m["fog_color"]); }
        _baseFogColor = env.FogLightColor;   // for cloud/overcast aerial tinting
        env.FogDensity = F(m, "fog_density", 0.0006f) * 0.25f;
        env.FogAerialPerspective = Mathf.Min(F(m, "fog_aerial", 0.85f), 0.5f);
        env.FogHeight = F(m, "fog_height", -200f);
        env.FogHeightDensity = F(m, "fog_heightd", 0.04f) * 0.3f;
        env.FogSunScatter = F(m, "fog_sun_scatter", 0.2f) * 0.25f;
        // Volumetric fog OFF by default (it was the main 'can't see anything' culprit).
        // Available as an opt-in toggle in the Light tab for those who want godrays.
        env.VolumetricFogEnabled = false;

        env.TonemapExposure = F(m, "exposure", 1.0f);
        env.TonemapWhite = F(m, "white", 6.0f);
        // Per-mood color grade (built-in Environment adjustments). Each mood can set
        // its own contrast/saturation; defaults give a gentle cinematic lift.
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = F(m, "contrast", 1.08f);
        env.AdjustmentSaturation = F(m, "saturation", 1.12f);
        env.AdjustmentBrightness = F(m, "brightness", 1.0f);
        // Glow: keep it a subtle highlight sheen, NOT a sky-wide wash. The over-
        // bright halo around the sun was bloom catching the whole HDR sky — raise the
        // HDR threshold so ONLY the sun disc (very bright) blooms, kill constant bloom.
        env.GlowEnabled = true;
        env.GlowNormalized = true;
        env.GlowHdrThreshold = 1.6f;
        env.GlowBloom = 0.0f;
        env.GlowIntensity = F(m, "glow", 0.3f) * 0.35f;
        env.SetGlowLevel(4, 0.0f); env.SetGlowLevel(5, 0.0f); env.SetGlowLevel(6, 0.0f); // no huge-radius spread

        SyncLightControlsToScene();   // make the Light-tab sliders reflect the mood
        GD.Print($"TerrainLab: mood -> {_moodNames[idx]}");
    }

    /// After a mood sets the scene, update the Light-tab slider widgets so they show
    /// the mood's values (sliders are live overrides on top of the chosen mood).
    private void SyncLightControlsToScene()
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        void Set(string id, float v) { if (_byId.TryGetValue(id, out var c)) { SetWidgetValueSilent(c, v); } }
        Set("sun_energy", sun.LightEnergy); Set("sun_angle", _sunAngle); Set("sun_azimuth", _sunAzimuth);
        Set("sun_soft", sun.ShadowBlur); Set("sun_disc", sun.LightAngularDistance); Set("ambient_e", env.AmbientLightEnergy);
        Set("ssao_i", env.SsaoIntensity); Set("ssao_r", env.SsaoRadius);
        Set("fog_d", env.FogDensity); Set("fog_aerial", env.FogAerialPerspective);
        Set("fog_heightd", env.FogHeightDensity); Set("exposure", env.TonemapExposure);
        PushSunToCloud(sun);   // clouds track the mood's sun
    }

    /// Update a slider widget + value WITHOUT re-applying (avoids fighting the mood).
    private void SetWidgetValueSilent(LabControl c, float v)
    {
        bool wasReady = _ready; _ready = false;
        if (c.Widget is HSlider sl) { sl.Value = v; }
        if (c.ValLabel != null) { c.ValLabel.Text = v.ToString((c.Max - c.Min) < 0.05f ? "0.0000" : "0.00"); }
        c.Value = v;
        _ready = wasReady;
    }

    /// Flat baseline: turn EVERY visual contributor off so the user can add them
    /// back one at a time and find what causes the speckle. The plainest possible
    /// render: albedo only, no normal maps, no specular, no macro/contact/splat,
    /// no SSAO/shadow/fog/sun-shading effects.
    private void FlatBaseline()
    {
        foreach (LabControl c in _controls)
        {
            if (c.Type == "toggle" || c.Type == "scene")
            {
                // turn OFF everything except 'sun' (keep some light so it's visible)
                bool target = c.Scene == "sun";
                SetWidgetValue(c, target);
            }
        }
        // force matte (no specular) + no normal maps explicitly
        if (_byId.TryGetValue("dbg_fullrough", out var fr)) { SetWidgetValue(fr, true); }
        if (_byId.TryGetValue("dbg_normalmap", out var nm)) { SetWidgetValue(nm, false); }
        GD.Print("TerrainLab: FLAT BASELINE — everything off; re-enable contributors one at a time (Debug tab for normals/spec/scene; Color/Detail/Splat tabs for the rest)");
    }

    // ---- randomize / lock -----------------------------------------------------

    /// Global randomize: rolls all UNLOCKED controls flagged rand:true.
    private void Randomize()
    {
        bool needRebake = false;
        foreach (LabControl c in _controls)
        {
            if (c.Locked || !c.Rand) { continue; }
            if (RandomizeControl(c)) { needRebake = true; }
        }
        if (needRebake) { _terrain.RebakeSplat(); }
        GD.Print("TerrainLab: randomized (unlocked controls)");
    }

    /// Per-tab randomize: rolls EVERY unlocked control on the tab, ignoring the
    /// rand flag (the user explicitly diced this tab, so roll everything tunable).
    private void RandomizeTab(string tab)
    {
        bool needRebake = false;
        foreach (LabControl c in _controls)
        {
            if (c.Tab != tab || c.Locked) { continue; }
            if (RandomizeControl(c)) { needRebake = true; }
        }
        if (needRebake) { _terrain.RebakeSplat(); }
        GD.Print($"TerrainLab: randomized tab '{tab}'");
    }

    /// Roll one control to a random value. Returns true if it needs a splat rebake.
    /// Covers every control type incl. cloud knobs (the previous switch missed the
    /// cloud/scene types, so those never randomized).
    private bool RandomizeControl(LabControl c)
    {
        switch (c.Type)
        {
            case "slider":
            case "scenef":
            case "cloudf":
                SetWidgetValue(c, c.Min + (float)_rng.NextDouble() * (c.Max - c.Min));
                break;
            case "cloudi":
                SetWidgetValue(c, (float)Math.Round(c.Min + _rng.NextDouble() * (c.Max - c.Min)));
                break;
            case "toggle":
            case "scene":
            case "cloud":
                SetWidgetValue(c, _rng.NextDouble() < 0.75);   // bias ON so a roll isn't all-off
                break;
            case "enum":
                SetWidgetValue(c, _rng.Next(c.Options.Length));
                break;
            case "material":
                SetWidgetValue(c, _rng.Next(_materials.Count));
                break;
            case "companion":
                SetWidgetValue(c, _rng.Next(_zoneNames.Length));
                break;
        }
        return c.Rebake;
    }

    /// Set a widget's value (updates UI + applies). Suppresses per-control rebake;
    /// the caller batches one rebake at the end.
    private void SetWidgetValue(LabControl c, Variant v)
    {
        bool wasReady = _ready; _ready = false;   // avoid double-apply via signal
        switch (c.Widget)
        {
            case HSlider sl: sl.Value = v.AsSingle(); if (c.ValLabel != null) { c.ValLabel.Text = v.AsSingle().ToString("0.00"); } break;
            case CheckBox cb: cb.ButtonPressed = v.AsBool(); break;
            case OptionButton ob: ob.Select(v.AsInt32()); break;
        }
        c.Value = v;
        _ready = wasReady;
        ApplyControl(c, false);
    }

    private void SetAllLocks(bool locked)
    {
        foreach (LabControl c in _controls) { c.Locked = locked; if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; } }
    }

    private void SetTabLocks(string tab, bool locked)
    {
        foreach (LabControl c in _controls)
        {
            if (c.Tab != tab) { continue; }
            c.Locked = locked;
            if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; }
        }
    }

    // ---- presets (registry-based) --------------------------------------------

    private void SavePreset()
    {
        string name = string.IsNullOrWhiteSpace(_presetName.Text) ? $"preset{_presets.Count + 1}" : _presetName.Text;
        var vals = new Godot.Collections.Dictionary();
        var locks = new Godot.Collections.Dictionary();
        foreach (var kv in _byId)
        {
            vals[kv.Key] = kv.Value.Value;
            if (kv.Value.Locked) { locks[kv.Key] = true; }
        }
        _presets[name] = new Godot.Collections.Dictionary { { "v", vals }, { "lock", locks } };
        WritePresets();
        RefreshPresetList();
        GD.Print($"TerrainLab: saved preset '{name}'");
    }

    private void LoadSelectedPreset()
    {
        if (_presetPick.Selected < 0) { return; }
        string name = _presetPick.GetItemText(_presetPick.Selected);
        if (!_presets.TryGetValue(name, out Variant ev)) { return; }
        var entry = ev.AsGodotDictionary();
        if (!entry.ContainsKey("v")) { return; }
        var vals = entry["v"].AsGodotDictionary();
        var locks = entry.ContainsKey("lock") ? entry["lock"].AsGodotDictionary() : new Godot.Collections.Dictionary();

        bool wasReady = _ready; _ready = false;
        foreach (var kv in vals)
        {
            if (_byId.TryGetValue(kv.Key.AsString(), out LabControl c)) { SetWidgetValue(c, kv.Value); }
        }
        foreach (var kv in _byId)
        {
            bool lk = locks.ContainsKey(kv.Key);
            kv.Value.Locked = lk;
            if (kv.Value.LockBox != null) { kv.Value.LockBox.ButtonPressed = lk; }
        }
        _ready = wasReady;
        _terrain.RebakeSplat();
        GD.Print($"TerrainLab: loaded preset '{name}'");
    }

    private void RefreshPresetList()
    {
        _presetPick.Clear();
        foreach (string k in _presets.Keys) { _presetPick.AddItem(k); }
    }

    private void WritePresets()
    {
        using var f = FileAccess.Open(PresetsPath, FileAccess.ModeFlags.Write);
        f.StoreString(Json.Stringify(_presets.ToGodotDictionary()));
    }

    private void LoadPresetsFromDisk()
    {
        if (!FileAccess.FileExists(PresetsPath)) { return; }
        using var f = FileAccess.Open(PresetsPath, FileAccess.ModeFlags.Read);
        Variant parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType == Variant.Type.Dictionary)
        {
            foreach (var kv in parsed.AsGodotDictionary()) { _presets[kv.Key.AsString()] = kv.Value; }
        }
        RefreshPresetList();
    }

    // ---- CLI overrides + auto-shot (unchanged contract) -----------------------

    private void ApplyCliOverrides()
    {
        if (_overrideMask >= 0) { OverrideEnum("mask_mode", _overrideMask); }
        if (_overrideBlend >= 0) { OverrideEnum("blend_mode", _overrideBlend); }
        if (_overrideTile >= 0) { OverrideEnum("tile_mode", _overrideTile); }
        if (_overrideSplatDebug >= 0) { OverrideEnum("splat_debug", _overrideSplatDebug); }
        if (_overrideMacro >= 0) { OverrideToggle("macro_on", _overrideMacro == 1); }
        if (_overrideContact >= 0) { OverrideToggle("contact_on", _overrideContact == 1); }
        if (_overrideSplat >= 0) { OverrideToggle("splat_on", _overrideSplat == 1); }
        if (_texScale > 0f && _byId.TryGetValue("tex_scale_m", out LabControl ts)) { SetWidgetValue(ts, _texScale); }
        if (_camArg != null)
        {
            string[] p = _camArg.Split(',');
            if (p.Length >= 5)
            {
                var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
                cam.Position = new Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
                cam.RotationDegrees = new Vector3(float.Parse(p[3]), float.Parse(p[4]), 0f);
            }
        }
        // Lighting isolation probes (fuzz hunt).
        if (_probeSsao >= 0)
        {
            var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
            env.Environment.SsaoEnabled = _probeSsao == 1;
        }
        if (_probeShadow >= 0)
        {
            var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            sun.ShadowEnabled = _probeShadow == 1;
        }
        if (_probeRoughFloor >= 0f) { _terrain.SetFloat("rough_floor", _probeRoughFloor); }
        if (_probeMixStr >= 0f) { _terrain.SetFloat("mix_strength", _probeMixStr); }
        if (_probeHb >= 0) { _terrain.SetBool("heightblend_on", _probeHb == 1); }
        if (_probeMood >= 0) { ApplyMood(_probeMood); }
    }
    private int _cloudDbg = -1;
    private int _cloudSteps = -1;
    private int _cloudsOn = -1;
    private void OverrideEnum(string id, int v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
    private void OverrideToggle(string id, bool v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }

    private Label? _fpsLabel;
    private double _fpsAccum;
    private int _fpsFrames;
    // overcast → GI/sun dimming + aerial-perspective tint (driven by cloud coverage)
    private float _baseAmbient = 0.4f, _baseSunEnergy = 1.3f;
    private Color _baseFogColor = new Color(0.71f, 0.78f, 0.86f);
    private bool _overcastDim = true;

    private void UpdateOvercast()
    {
        if (_cloud == null) { return; }
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        float oc = _overcastDim ? _cloud.Overcast() : 0f;
        const float OvercastAmt = 0.7f;   // how strongly full overcast dims (0..1)
        float k = 1f - oc * OvercastAmt;
        env.AmbientLightEnergy = _baseAmbient * Mathf.Lerp(1f, 1.15f, oc);   // sky fill slightly UP (diffuse dome)
        sun.LightEnergy = _baseSunEnergy * k;                                // direct sun DOWN under cloud
        // Aerial perspective: tint distance haze toward the cloud sky color so the
        // atmosphere reads coherent with the cover (stronger as overcast rises).
        Color sky = _cloud.SkyHorizonColor;
        env.FogLightColor = _baseFogColor.Lerp(sky, 0.35f + 0.45f * oc);
        // God-ray strength peaks at BROKEN cloud (gaps + cover both present); near 0
        // at clear or fully-overcast sky. Drives the sun's volumetric scatter energy.
        float broken = 4f * oc * (1f - oc);   // bell curve, max at oc=0.5
        sun.LightVolumetricFogEnergy = Mathf.Lerp(0.5f, 12f, broken);
    }

    public override void _Process(double delta)
    {
        if (_ready) { UpdateOvercast(); }

        // FPS / frame-time HUD (top-right). Cheap; updated ~4×/sec. The perf gate
        // needs a number, not a feeling — this is it.
        if (_fpsLabel != null)
        {
            _fpsAccum += delta; _fpsFrames++;
            if (_fpsAccum >= 0.25)
            {
                double avg = _fpsAccum / _fpsFrames;
                _fpsLabel.Text = $"{1.0 / avg,5:0} fps   {avg * 1000.0,5:0.0} ms";
                _fpsAccum = 0; _fpsFrames = 0;
            }
        }

        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                // print steady-state frame-time for the perf gate (averaged over warm-up)
                GD.Print($"TerrainLab: auto-shot -> {_autoShotPath}  (frame ~{Engine.GetFramesPerSecond():0} fps)");
                _autoShotT = -1.0;
                GetTree().Quit();
            }
        }

        // --profile=<secs>: warm up 1s, then average frame time, print fps + worst, quit
        if (_profileT >= 0.0)
        {
            _profileT += delta;
            if (_profileT > 1.0)
            {
                _profAccum += delta; _profFrames++;
                _profWorst = Math.Max(_profWorst, delta);
                if (_profileT > 1.0 + _profileDur)
                {
                    double avg = _profAccum / Math.Max(_profFrames, 1);
                    GD.Print($"PROFILE: avg {1.0/avg:0} fps ({avg*1000:0.0} ms)  worst {1.0/_profWorst:0} fps ({_profWorst*1000:0.0} ms)  over {_profFrames} frames");
                    _profileT = -1.0;
                    GetTree().Quit();
                }
            }
        }
    }
    private double _profileT = -1.0, _profileDur = 3.0, _profAccum = 0, _profWorst = 0;
    private int _profFrames = 0;

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
