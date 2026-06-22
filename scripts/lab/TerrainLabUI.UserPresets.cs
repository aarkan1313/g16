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
        // U3: additive `lists` section — captures the live object-lists (today: the Sky-bodies luminaries)
        // alongside the flat controls. Old presets without `lists` still load (LoadSelectedPreset guards it).
        var lists = new Godot.Collections.Dictionary();
        if (_luminaryList != null)
        {
            var arr = new Godot.Collections.Array();
            foreach (var it in _luminaryList.Items) { arr.Add(LumDictToStorable(it)); }   // Color -> [r,g,b] (Json-safe)
            lists["luminaries"] = arr;
        }
        _presets[name] = new Godot.Collections.Dictionary { { "v", vals }, { "lock", locks }, { "lists", lists } };
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
        // U3: restore the object-lists (guarded — old presets have no `lists`). SetItems rebuilds the editor
        // AND fires the ListChanged callback (ApplyLuminaryDicts -> recompose), so the sky updates too.
        if (entry.ContainsKey("lists") && _luminaryList != null)
        {
            var lists = entry["lists"].AsGodotDictionary();
            if (lists.ContainsKey("luminaries"))
            {
                var arr = lists["luminaries"].AsGodotArray();
                var items = new System.Collections.Generic.List<Godot.Collections.Dictionary>();
                foreach (var v in arr) { items.Add(LumDictFromStorable(v.AsGodotDictionary())); }   // [r,g,b] -> Color
                _luminaryList.SetItems(items);
            }
        }
        _ready = wasReady;
        GD.Print($"TerrainLab: loaded preset '{name}'");
    }

    // U3 Color serialization: Godot Color Variants do NOT round-trip through Json (Stringify writes
    // "(r,g,b,a)" which ParseString can't read back → black). So at the storage boundary we convert any
    // Color value to a [r,g,b] float array (the established preset convention) and back. Non-color fields
    // pass through untouched. The live Items model stays Color-based (ColorPickerButton + AsColor()).
    private static Godot.Collections.Dictionary LumDictToStorable(Godot.Collections.Dictionary d)
    {
        var o = new Godot.Collections.Dictionary();
        foreach (var kv in d)
        {
            if (kv.Value.VariantType == Variant.Type.Color)
            {
                var c = kv.Value.AsColor();
                o[kv.Key] = new Godot.Collections.Array { c.R, c.G, c.B };
            }
            else { o[kv.Key] = kv.Value; }
        }
        return o;
    }

    private static Godot.Collections.Dictionary LumDictFromStorable(Godot.Collections.Dictionary d)
    {
        var o = new Godot.Collections.Dictionary();
        foreach (var kv in d)
        {
            if (kv.Value.VariantType == Variant.Type.Array)
            {
                var a = kv.Value.AsGodotArray();
                if (a.Count >= 3) { o[kv.Key] = new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); continue; }
            }
            o[kv.Key] = kv.Value;
        }
        return o;
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
