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
    private int _probeSsao = -1, _probeShadow = -1, _probeHb = -1;   // lighting/splat isolation
    private float _probeRoughFloor = -1f, _probeMixStr = -1f;

    /// One control: parsed registry fields + runtime state.
    private sealed class LabControl
    {
        public string Id = "", Label = "", Tab = "", Type = "";
        public string? Param, Setter, Field, Scene;  // shader uniform / mode-setter / TerrainLab field / scene-node target
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

        ParseCli();
        LoadLibrary();
        LoadRegistry();
        BuildPanel();
        ApplyAll();              // push all defaults to the shader (also fixes the .Value-doesn't-fire issue)
        ApplyCliOverrides();
        _ready = true;
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
            Rand = !c.TryGetProperty("rand", out var r) || r.GetBoolean(),
            Rebake = c.TryGetProperty("rebake", out var rb) && rb.GetBoolean(),
        };
        if (c.TryGetProperty("min", out var mn)) { lc.Min = mn.GetSingle(); }
        if (c.TryGetProperty("max", out var mx)) { lc.Max = mx.GetSingle(); }
        if (c.TryGetProperty("options", out var op)) { lc.Options = op.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
        if (c.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Array)
        {
            if (type == "toggle" || type == "scene") { lc.DefBool = d.GetBoolean(); }
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
            foreach (LabControl c in _controls.Where(c => c.Tab == tabName)) { BuildRow(col, c); }
        }

        BuildPresetsTab(tabs);
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
            {
                var sl = new HSlider { MinValue = c.Min, MaxValue = c.Max, Value = c.Default,
                    Step = (c.Max - c.Min) / 200.0, CustomMinimumSize = new Vector2(160, 0) };
                sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                var vlbl = new Label { Text = c.Default.ToString("0.00"), CustomMinimumSize = new Vector2(44, 0) };
                sl.ValueChanged += v => { c.Value = (float)v; vlbl.Text = ((float)v).ToString("0.00"); if (_ready) ApplyControl(c, true); };
                c.Value = c.Default; c.Widget = sl; c.ValLabel = vlbl;
                row.AddChild(sl); row.AddChild(vlbl);
                break;
            }
            case "toggle":
            case "scene":
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
        }
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

    private void Randomize()
    {
        bool needRebake = false;
        foreach (LabControl c in _controls)
        {
            if (c.Locked || !c.Rand) { continue; }
            switch (c.Type)
            {
                case "slider":
                {
                    float v = c.Min + (float)_rng.NextDouble() * (c.Max - c.Min);
                    SetWidgetValue(c, v);
                    break;
                }
                case "toggle":
                {
                    // bias toggles to stay ON (~75%) so a roll doesn't flatten everything
                    bool on = _rng.NextDouble() < 0.75;
                    SetWidgetValue(c, on);
                    break;
                }
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
            if (c.Rebake) { needRebake = true; }
        }
        if (needRebake) { _terrain.RebakeSplat(); }
        GD.Print("TerrainLab: randomized (unlocked controls)");
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
    }
    private void OverrideEnum(string id, int v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
    private void OverrideToggle(string id, bool v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }

    public override void _Process(double delta)
    {
        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                GD.Print($"TerrainLab: auto-shot -> {_autoShotPath}");
                _autoShotT = -1.0;
                GetTree().Quit();
            }
        }
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
