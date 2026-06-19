using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
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
}
