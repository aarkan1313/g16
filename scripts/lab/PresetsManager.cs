using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// User preset save/load — captures every registry control's value + lock state (plus the live object-lists,
/// today the Sky-bodies luminaries) to user://terrain_presets.json, and restores them. Extracted from the
/// TerrainLabUI god-class (decomposition Phase 3b). It owns the preset dict + disk I/O and routes control
/// reads/writes through the narrow ILabControls façade; the UI widgets (the name field + the dropdown) stay
/// on the TerrainLabUI side, which calls Save(name)/Load(name)/Names here.
public sealed class PresetsManager
{
    private const string PresetsPath = "user://terrain_presets.json";

    private readonly ILabControls _reg;
    private readonly System.Func<ObjectListControl?> _getLuminaryList;   // built later in BuildPanel → lazy
    private Dictionary<string, Variant> _presets = new();

    public PresetsManager(ILabControls reg, System.Func<ObjectListControl?> getLuminaryList)
    {
        _reg = reg;
        _getLuminaryList = getLuminaryList;
    }

    public IEnumerable<string> Names => _presets.Keys;
    public int Count => _presets.Count;

    /// Capture all controls (value + lock) + the live luminary list under `name`, then persist.
    public void Save(string name)
    {
        var vals = new Godot.Collections.Dictionary();
        var locks = new Godot.Collections.Dictionary();
        foreach (var kv in _reg.ById)
        {
            vals[kv.Key] = kv.Value.Value;
            if (kv.Value.Locked) { locks[kv.Key] = true; }
        }
        // U3: additive `lists` section — captures the live object-lists (today: the Sky-bodies luminaries)
        // alongside the flat controls. Old presets without `lists` still load (Load guards it).
        var lists = new Godot.Collections.Dictionary();
        var lum = _getLuminaryList();
        if (lum != null)
        {
            var arr = new Godot.Collections.Array();
            foreach (var it in lum.Items) { arr.Add(LumDictToStorable(it)); }   // Color -> [r,g,b] (Json-safe)
            lists["luminaries"] = arr;
        }
        _presets[name] = new Godot.Collections.Dictionary { { "v", vals }, { "lock", locks }, { "lists", lists } };
        WriteDisk();
        GD.Print($"TerrainLab: saved preset '{name}'");
    }

    /// Restore the named preset's control values + locks + luminaries. Returns false if not found.
    public bool Load(string name)
    {
        if (!_presets.TryGetValue(name, out Variant ev)) { return false; }
        var entry = ev.AsGodotDictionary();
        if (!entry.ContainsKey("v")) { return false; }
        var vals = entry["v"].AsGodotDictionary();
        var locks = entry.ContainsKey("lock") ? entry["lock"].AsGodotDictionary() : new Godot.Collections.Dictionary();

        bool wasReady = _reg.IsReady; _reg.IsReady = false;
        foreach (var kv in vals)
        {
            if (_reg.TryGet(kv.Key.AsString(), out LabControl c)) { _reg.SetValue(c, kv.Value); }
        }
        foreach (var kv in _reg.ById)
        {
            bool lk = locks.ContainsKey(kv.Key);
            kv.Value.Locked = lk;
            if (kv.Value.LockBox != null) { kv.Value.LockBox.ButtonPressed = lk; }
        }
        // U3: restore the object-lists (guarded — old presets have no `lists`). SetItems rebuilds the editor
        // AND fires the ListChanged callback (ApplyLuminaryDicts -> recompose), so the sky updates too.
        var lum = _getLuminaryList();
        if (entry.ContainsKey("lists") && lum != null)
        {
            var lists = entry["lists"].AsGodotDictionary();
            if (lists.ContainsKey("luminaries"))
            {
                var arr = lists["luminaries"].AsGodotArray();
                var items = new List<Godot.Collections.Dictionary>();
                foreach (var v in arr) { items.Add(LumDictFromStorable(v.AsGodotDictionary())); }   // [r,g,b] -> Color
                lum.SetItems(items);
            }
        }
        _reg.IsReady = wasReady;
        GD.Print($"TerrainLab: loaded preset '{name}'");
        return true;
    }

    public void LoadFromDisk()
    {
        if (!FileAccess.FileExists(PresetsPath)) { return; }
        using var f = FileAccess.Open(PresetsPath, FileAccess.ModeFlags.Read);
        Variant parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType == Variant.Type.Dictionary)
        {
            foreach (var kv in parsed.AsGodotDictionary()) { _presets[kv.Key.AsString()] = kv.Value; }
        }
    }

    private void WriteDisk()
    {
        using var f = FileAccess.Open(PresetsPath, FileAccess.ModeFlags.Write);
        f.StoreString(Json.Stringify(_presets.ToGodotDictionary()));
    }

    // U3 Color serialization: Godot Color Variants do NOT round-trip through Json (Stringify writes
    // "(r,g,b,a)" which ParseString can't read back → black). So at the storage boundary we convert any
    // Color value to a [r,g,b] float array (the established preset convention) and back. Non-color fields
    // pass through untouched. (Also reused by the --lumpresetcheck self-check.)
    public static Godot.Collections.Dictionary LumDictToStorable(Godot.Collections.Dictionary d)
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

    public static Godot.Collections.Dictionary LumDictFromStorable(Godot.Collections.Dictionary d)
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
}
