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

    private const string PresetsPath = "user://terrain_presets.json";

    private FieldCompute _fc = null!;
    private FieldParams _params = null!;
    private TerrainLab _terrain = null!;
    private readonly List<string> _materials = new();
    private readonly OptionButton[] _zonePick = new OptionButton[7];
    private OptionButton _maskPick = null!, _blendPick = null!, _presetPick = null!;
    private LineEdit _presetName = null!;
    private Dictionary<string, Variant> _presets = new();

    public override void _Ready()
    {
        _fc = new FieldCompute();
        _params = FieldParams.Load();
        _terrain = GetNode<TerrainLab>("../TerrainLab");
        _terrain.Build(_fc, _params);

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
