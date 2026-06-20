using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Celestial PRESETS (data/celestial_presets.json): curated moon + stars + night looks. Orthogonal to
/// the Time/Weather/Grade axes — pick any night look on top of any time/weather/grade. Mirrors the
/// sun-preset path (TerrainLabUI.SunPresets.cs): load → a Night-tab OptionButton → ApplyCelestialPreset,
/// routed through the registry so nothing silently no-ops (an unknown id warns). The optional
/// moon_color / moonlight_color (no sliders) are applied directly via the CloudVolume/state path.
public partial class TerrainLabUI : Control
{
    private readonly List<(string name, Godot.Collections.Dictionary values)> _celPresets = new();
    private int _celActiveIdx = -1;

    private void LoadCelestialPresets()
    {
        if (_celPresets.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/celestial_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[celestial] data/celestial_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _celPresets.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        string active = root.ContainsKey("active") ? root["active"].AsString() : "";
        _celActiveIdx = _celPresets.FindIndex(p => p.name == active);
        GD.Print($"[celestial] loaded {_celPresets.Count}, active='{active}' (idx {_celActiveIdx})");
    }

    /// Auto-apply the JSON "active" preset at startup (no-op when active is empty).
    private void ApplyActiveCelestialPreset()
    {
        LoadCelestialPresets();
        if (_celActiveIdx >= 0) { ApplyCelestialPreset(_celActiveIdx); }
    }

    /// Build the Night-tab dropdown (called from the Night-tab UI build).
    private void BuildCelestialPresetPicker(Container parent)
    {
        LoadCelestialPresets();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— celestial preset —", 0);
        for (int i = 0; i < _celPresets.Count; i++) { pick.AddItem(_celPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplyCelestialPreset((int)idx - 1); } };
        parent.AddChild(pick);
    }

    public void ApplyCelestialPreset(int idx)
    {
        if (idx < 0 || idx >= _celPresets.Count) { return; }
        var vals = _celPresets[idx].values;
        bool compose = false;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "moon_color")      { _moon.Color = ColFromArr(vals[k], _moon.Color); compose = true; continue; }
            if (id == "moonlight_color") { _moon.LightColor = ColFromArr(vals[k], _moon.LightColor); compose = true; continue; }
            if (_byId.TryGetValue(id, out var c)) { SetWidgetValue(c, vals[k]); }
            else { GD.PushWarning($"[celestial] unknown control id '{id}' in preset '{_celPresets[idx].name}'"); }
        }
        // a preset that omits the colors resets them to the cool defaults (so an 'exotic' tint doesn't linger).
        if (!vals.ContainsKey("moon_color"))      { _moon.Color = new Color(0.85f, 0.88f, 1.0f); compose = true; }
        if (!vals.ContainsKey("moonlight_color")) { _moon.LightColor = new Color(0.60f, 0.70f, 1.0f); compose = true; }
        if (compose) { ComposeLighting(); }
        GD.Print($"[celestial] applied '{_celPresets[idx].name}'");
    }

    private static Color ColFromArr(Variant v, Color def)
    {
        var a = v.AsGodotArray();
        return a.Count >= 3 ? new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()) : def;
    }
}
