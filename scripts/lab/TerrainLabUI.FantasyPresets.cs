using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Fantasy / exotic CROSS-SYSTEM presets (data/fantasy_presets.json, Sky lane #4 ST4-2). Each preset is a
/// one-click alien sky composed from the EXISTING per-feature systems: it names a sun preset + a celestial
/// preset, then layers a sky tint + moon/moonlight color overrides + grade/cloud tweaks. Data only — no new
/// rendering; it fans out to ApplySunPreset / ApplyCelestialPreset and the scene-color/registry appliers.
/// Mirrors the sun/celestial preset path. Deliberately sets NO time_of_day, so a fantasy LOOK holds as the
/// ST4-1 day/night cycle runs (the tint/colors persist; night-only bodies like the moon show when it's night).
public partial class TerrainLabUI : Control
{
    private readonly List<(string name, Godot.Collections.Dictionary values)> _fantasyPresets = new();

    private void LoadFantasyPresets()
    {
        if (_fantasyPresets.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/fantasy_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[fantasy] data/fantasy_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _fantasyPresets.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        GD.Print($"[fantasy] loaded {_fantasyPresets.Count}");
    }

    /// Build the Night-tab dropdown (called from the Night-tab UI build, under the celestial picker).
    private void BuildFantasyPresetPicker(Container parent)
    {
        LoadFantasyPresets();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— fantasy / exotic sky —", 0);
        for (int i = 0; i < _fantasyPresets.Count; i++) { pick.AddItem(_fantasyPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplyFantasyPreset((int)idx - 1); } };
        parent.AddChild(pick);
    }

    /// Compose a fantasy sky: apply the named sun + celestial sub-presets first, then the cross-system
    /// overrides (sky tint, moon/moonlight color, exposure, cloud coverage…). One ComposeLighting at the end.
    public void ApplyFantasyPreset(int idx)
    {
        if (idx < 0 || idx >= _fantasyPresets.Count) { return; }
        LoadSunPresets(); LoadCelestialPresets();
        var vals = _fantasyPresets[idx].values;
        _skyTint = Colors.White;   // reset → self-contained (a preset that omits the tint clears a prior one)

        if (vals.ContainsKey("sun"))
        {
            int si = _sunPresets.FindIndex(p => p.name == vals["sun"].AsString());
            if (si >= 0) { ApplySunPreset(si); } else { GD.PushWarning($"[fantasy] '{_fantasyPresets[idx].name}': unknown sun preset '{vals["sun"].AsString()}'"); }
        }
        if (vals.ContainsKey("celestial"))
        {
            int ci = _celPresets.FindIndex(p => p.name == vals["celestial"].AsString());
            if (ci >= 0) { ApplyCelestialPreset(ci); } else { GD.PushWarning($"[fantasy] '{_fantasyPresets[idx].name}': unknown celestial preset '{vals["celestial"].AsString()}'"); }
        }

        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "sun" || id == "celestial") { continue; }
            if (id == "sky_tint")        { _skyTint = ColFromArr(vals[k], _skyTint); continue; }
            if (id == "moon_color")      { _moon.Color = ColFromArr(vals[k], _moon.Color); continue; }
            if (id == "moonlight_color") { _moon.LightColor = ColFromArr(vals[k], _moon.LightColor); continue; }
            if (_byId.TryGetValue(id, out var c)) { SetWidgetValue(c, vals[k]); }
            else { GD.PushWarning($"[fantasy] unknown control id '{id}' in preset '{_fantasyPresets[idx].name}'"); }
        }
        ComposeLighting();   // bake the sky tint + moon colors (sun/celestial appliers ran above)
        GD.Print($"[fantasy] applied '{_fantasyPresets[idx].name}'");
    }
}
