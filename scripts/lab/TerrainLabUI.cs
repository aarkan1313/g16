using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

/// On-screen panel for the terrain look lab: per-zone material dropdowns, a mask-
/// mode selector, a blend/shader-mode selector, and preset save/load. Builds the
/// base field once, then drives TerrainLab live from the controls.
public partial class TerrainLabUI : Control
{
    private static readonly string[] ZoneNames =
        { "valley", "valley→slope", "slope", "slope→cliff", "cliff", "high", "peak/snow" };
    private static readonly string[] MaskModes =
        { "height bands", "height+slope", "noise-broken", "curvature-aware", "steep=cliff", "noise biomes" };
    private static readonly string[] BlendModes =
        { "flat albedo", "full PBR", "PBR+anti-tile", "+macro tint", "full stack" };
    private static readonly string[] TileModes =
        { "none (sharp)", "IQ 2-tap", "hex-tiling" };

    private const string PresetsPath = "user://terrain_presets.json";

    private FieldCompute _fc = null!;
    private FieldParams _params = null!;
    private TerrainLab _terrain = null!;
    private readonly List<string> _materials = new();
    private readonly OptionButton[] _zonePick = new OptionButton[7];
    private OptionButton _maskPick = null!, _blendPick = null!, _presetPick = null!, _tilePick = null!;
    private LineEdit _presetName = null!;
    private Dictionary<string, Variant> _presets = new();

    // Optional headless A/B capture (CLI verification): --auto-shot=<path> fires a
    // screenshot from the scene's default camera after a warmup, then quits.
    // --blend=N / --mask=N override the defaults so each mode can be captured.
    private string? _autoShotPath;
    private double _autoShotT = -1.0;
    private int _overrideBlend = -1, _overrideMask = -1, _overrideTile = -1, _overrideMacro = -1, _overrideContact = -1;
    private int _overrideSplat = -1, _overrideSplatDebug = -1;
    private string? _camArg;   // "x,y,z,pitchDeg,yawDeg" — place capture camera near ground
    private float _texScale = -1f;   // override tex_scale_m for isolation runs

    public override void _Ready()
    {
        _fc = new FieldCompute();
        _params = FieldParams.Load();
        // UI lives under UILayer/UI, so reach up to the scene root and across to
        // the TerrainLab node (was "../TerrainLab", which resolved one level short).
        _terrain = GetNode<TerrainLab>("/root/TerrainLabRoot/TerrainLab");
        _terrain.Build(_fc, _params);

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
        }

        LoadLibrary();
        BuildPanel();
        ApplyDefaults();
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

    private void BuildPanel()
    {
        var panel = new PanelContainer
        {
            Position = new Vector2(8, 8),
            CustomMinimumSize = new Vector2(360, 0),
        };
        AddChild(panel);
        var vb = new VBoxContainer();
        panel.AddChild(vb);

        vb.AddChild(new Label { Text = "TERRAIN LOOK LAB" });

        // Per-zone material dropdowns.
        for (int z = 0; z < 7; z++)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = ZoneNames[z], CustomMinimumSize = new Vector2(96, 0) });
            var ob = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            for (int i = 0; i < _materials.Count; i++) { ob.AddItem(_materials[i], i); }
            int zone = z;
            ob.ItemSelected += idx => _terrain.SetZoneMaterial(zone, _materials[(int)idx]);
            _zonePick[z] = ob;
            row.AddChild(ob);
            vb.AddChild(row);
        }

        vb.AddChild(new HSeparator());

        _maskPick = AddSelector(vb, "mask mode", MaskModes, i => _terrain.SetMaskMode(i));
        _blendPick = AddSelector(vb, "blend mode", BlendModes, i => _terrain.SetBlendMode(i));

        vb.AddChild(new HSeparator());

        // Anti-tiling / sharpness controls.
        _tilePick = AddSelector(vb, "tile mode", TileModes, i => _terrain.SetInt("tile_mode", i));
        AddSlider(vb, "tex scale m", 4f, 80f, 28f, v => _terrain.SetFloat("tex_scale_m", v));   // 28=crisp (9 was mush)
        AddSlider(vb, "tri sharp", 1f, 32f, 8f, v => _terrain.SetFloat("tri_sharpness", v));
        AddSlider(vb, "hex rot", 0f, 1f, 0.7f, v => _terrain.SetFloat("hex_rot_strength", v));
        AddSlider(vb, "hex contrast", 0.3f, 1f, 0.7f, v => _terrain.SetFloat("hex_contrast", v));

        vb.AddChild(new HSeparator());

        // Lever 2: macro color variation.
        AddToggle(vb, "macro color", true, on => _terrain.SetBool("macro_on", on));
        AddSlider(vb, "macro val", 0f, 0.6f, 0.22f, v => _terrain.SetFloat("macro_val_amp", v));
        AddSlider(vb, "macro hue", 0f, 0.2f, 0.05f, v => _terrain.SetFloat("macro_hue_amp", v));
        AddSlider(vb, "macro sat", 0f, 0.6f, 0.18f, v => _terrain.SetFloat("macro_sat_amp", v));
        AddSlider(vb, "macro scl2", 60f, 400f, 170f, v => _terrain.SetFloat("macro_scale2", v));

        vb.AddChild(new HSeparator());

        // Lever 4: contact & wear shading.
        AddToggle(vb, "contact shade", true, on => _terrain.SetBool("contact_on", on));
        AddSlider(vb, "crevice", 0f, 1f, 0.45f, v => _terrain.SetFloat("crevice_amp", v));
        AddSlider(vb, "crev range", 0.5f, 8f, 2.5f, v => _terrain.SetFloat("crevice_range", v));
        AddSlider(vb, "slope wear", 0f, 1f, 0.35f, v => _terrain.SetFloat("slope_wear_amp", v));
        AddSlider(vb, "snow dust", 0f, 1f, 0f, v => _terrain.SetFloat("snow_dust_amp", v));

        vb.AddChild(new HSeparator());

        // Lever 1: GPU-baked splat material mixing.
        AddToggle(vb, "splat mix", false, on => _terrain.SetBool("splat_on", on));
        AddSlider(vb, "mix strength", 0f, 1f, 0.6f, v => _terrain.SetFloat("mix_strength", v)); // live
        // These change the BAKED mask → adjust the field, then Rebake.
        AddSlider(vb, "mix scale m", 8f, 120f, 26f, v => _terrain.MixScaleM = v);
        AddSlider(vb, "mix bias", 0.1f, 0.9f, 0.5f, v => _terrain.MixBias = v);
        var rebakeBtn = new Button { Text = "Rebake splat" };
        rebakeBtn.Pressed += () => _terrain.RebakeSplat();
        vb.AddChild(rebakeBtn);
        AddSelector(vb, "splat debug", new[] { "off", "zones", "mix amt" }, i => _terrain.SetInt("splat_debug", i));

        // Height-blended (relief-aware) transitions + band width — the AAA seam fix.
        AddToggle(vb, "height blend", true, on => _terrain.SetBool("heightblend_on", on));
        AddSlider(vb, "hb sharp", 0.02f, 0.5f, 0.15f, v => _terrain.SetFloat("heightblend_sharp", v));
        AddSlider(vb, "band soft", 0.3f, 3f, 1.4f, v => _terrain.SetFloat("band_soft_mult", v));

        vb.AddChild(new HSeparator());

        // Per-zone companion (secondary) material — which zone's textures blend into each.
        vb.AddChild(new Label { Text = "companion per zone" });
        int[] secDefault = { 1, 2, 1, 4, 5, 4, 5 };
        for (int z = 0; z < 7; z++)
        {
            int zone = z;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = ZoneNames[z], CustomMinimumSize = new Vector2(96, 0) });
            var ob = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
            for (int i = 0; i < ZoneNames.Length; i++) { ob.AddItem(ZoneNames[i], i); }
            ob.Select(secDefault[z]);
            ob.ItemSelected += idx => _terrain.SetSecondaryZone(zone, (int)idx);
            row.AddChild(ob);
            vb.AddChild(row);
        }

        vb.AddChild(new HSeparator());

        // Preset save/load.
        var prow = new HBoxContainer();
        _presetName = new LineEdit { PlaceholderText = "preset name", CustomMinimumSize = new Vector2(140, 0) };
        prow.AddChild(_presetName);
        var saveBtn = new Button { Text = "Save" };
        saveBtn.Pressed += SavePreset;
        prow.AddChild(saveBtn);
        vb.AddChild(prow);

        var lrow = new HBoxContainer();
        _presetPick = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        lrow.AddChild(_presetPick);
        var loadBtn = new Button { Text = "Load" };
        loadBtn.Pressed += LoadSelectedPreset;
        lrow.AddChild(loadBtn);
        vb.AddChild(lrow);

        vb.AddChild(new Label { Text = "RMB/LMB+WASD fly · wheel speed" });

        LoadPresetsFromDisk();
    }

    private OptionButton AddSelector(VBoxContainer vb, string label, string[] items, Action<int> onPick)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(96, 0) });
        var ob = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        for (int i = 0; i < items.Length; i++) { ob.AddItem(items[i], i); }
        ob.ItemSelected += idx => onPick((int)idx);
        row.AddChild(ob);
        vb.AddChild(row);
        return ob;
    }

    private HSlider AddSlider(VBoxContainer vb, string label, float min, float max, float val, Action<float> onChange)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(96, 0) });
        var sl = new HSlider { MinValue = min, MaxValue = max, Value = val, Step = (max - min) / 200.0,
                               CustomMinimumSize = new Vector2(180, 0) };
        var valLbl = new Label { Text = val.ToString("0.0"), CustomMinimumSize = new Vector2(48, 0) };
        sl.ValueChanged += v => { onChange((float)v); valLbl.Text = ((float)v).ToString("0.0"); };
        row.AddChild(sl);
        row.AddChild(valLbl);
        vb.AddChild(row);
        // Push the initial value to the shader NOW — setting .Value in code does not
        // reliably fire ValueChanged, so without this the shader keeps its own (wrong)
        // uniform default while the slider DISPLAYS the intended value. (This was the
        // bug: lab opened at tex_scale_m=9 mush despite the slider showing otherwise.)
        onChange(val);
        return sl;
    }

    private CheckBox AddToggle(VBoxContainer vb, string label, bool val, Action<bool> onChange)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = val };
        cb.Toggled += on => onChange(on);
        vb.AddChild(cb);
        onChange(val);   // push initial state (same reason as AddSlider)
        return cb;
    }

    /// Sensible alpine starting assignment (uses materials if present, else index 0).
    private void ApplyDefaults()
    {
        string[] wanted =
        {
            "m8_grass_calm",            // valley
            "12_dry_lichen_carpet",     // valley->slope
            "14_scree_with_lichen",     // slope
            "13_dry_loose_scree",       // slope->cliff
            "01_dark_slate",            // cliff
            "wgv3_alpine_scree",        // high
            "01_fresh_powder",          // peak/snow
        };
        for (int z = 0; z < 7; z++)
        {
            int idx = _materials.IndexOf(wanted[z]);
            if (idx < 0) { idx = Math.Min(z, _materials.Count - 1); }
            if (idx >= 0)
            {
                _zonePick[z].Select(idx);
                _terrain.SetZoneMaterial(z, _materials[idx]);
            }
        }
        _maskPick.Select(2); _terrain.SetMaskMode(2);     // noise-broken
        _blendPick.Select(3); _terrain.SetBlendMode(3);   // +macro tint
        _tilePick.Select(2); _terrain.SetInt("tile_mode", 2);   // hex-tiling (the fix)

        // CLI overrides for headless A/B capture.
        if (_overrideMask >= 0) { _maskPick.Select(_overrideMask); _terrain.SetMaskMode(_overrideMask); }
        if (_overrideBlend >= 0) { _blendPick.Select(_overrideBlend); _terrain.SetBlendMode(_overrideBlend); }
        if (_overrideTile >= 0) { _tilePick.Select(_overrideTile); _terrain.SetInt("tile_mode", _overrideTile); }
        if (_overrideMacro >= 0) { _terrain.SetBool("macro_on", _overrideMacro == 1); }
        if (_overrideContact >= 0) { _terrain.SetBool("contact_on", _overrideContact == 1); }
        if (_overrideSplat >= 0) { _terrain.SetBool("splat_on", _overrideSplat == 1); }
        if (_overrideSplatDebug >= 0) { _terrain.SetInt("splat_debug", _overrideSplatDebug); }
        if (_texScale > 0f) { _terrain.SetFloat("tex_scale_m", _texScale); }
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
    }

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

    private void SavePreset()
    {
        string name = string.IsNullOrWhiteSpace(_presetName.Text) ? $"preset{_presets.Count + 1}" : _presetName.Text;
        var zones = new Godot.Collections.Array<string>();
        for (int z = 0; z < 7; z++) { zones.Add(_materials[_zonePick[z].Selected]); }
        var entry = new Godot.Collections.Dictionary
        {
            { "zones", zones },
            { "mask", _maskPick.Selected },
            { "blend", _blendPick.Selected },
            { "tile", _tilePick.Selected },
        };
        _presets[name] = entry;
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
        var zones = entry["zones"].AsGodotArray<string>();
        for (int z = 0; z < 7 && z < zones.Count; z++)
        {
            int idx = _materials.IndexOf(zones[z]);
            if (idx >= 0) { _zonePick[z].Select(idx); _terrain.SetZoneMaterial(z, zones[z]); }
        }
        int mask = entry["mask"].AsInt32(), blend = entry["blend"].AsInt32();
        _maskPick.Select(mask); _terrain.SetMaskMode(mask);
        _blendPick.Select(blend); _terrain.SetBlendMode(blend);
        if (entry.ContainsKey("tile")) { int tile = entry["tile"].AsInt32(); _tilePick.Select(tile); _terrain.SetInt("tile_mode", tile); }
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
