using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Night-sky PRESETS (data/night_sky_presets.json): curated procedural-fantasy galaxy + nebula + starfield
/// looks (Celestial C1). Distinct from celestial_presets (moon + overall night look) — these drive the
/// galaxy/nebula bake + starfield. Mirrors TerrainLabUI.CelestialPresets.cs: load → a Night-tab
/// OptionButton → ApplyNightSkyPreset, routed through the registry so scalars update their sliders + apply
/// (an unknown id warns). Color ids (gx_*_color / neb*_color) are applied to _stars directly (the registry
/// color path can't take a JSON array), then one ComposeLighting re-bakes (coalesced by _mwDirty).
public partial class TerrainLabUI : Control
{
    private readonly List<(string name, Godot.Collections.Dictionary values)> _nsPresets = new();
    private int _nsActiveIdx = -1;

    private void LoadNightSkyPresets()
    {
        if (_nsPresets.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/night_sky_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[nightsky] data/night_sky_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _nsPresets.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        string active = root.ContainsKey("active") ? root["active"].AsString() : "";
        _nsActiveIdx = _nsPresets.FindIndex(p => p.name == active);
        GD.Print($"[nightsky] loaded {_nsPresets.Count}, active='{active}' (idx {_nsActiveIdx})");
    }

    /// Auto-apply a preset by index at startup (--nspreset=N, 1-based) or the JSON "active".
    private void ApplyNightSkyPresetCli(int oneBased)
    {
        LoadNightSkyPresets();
        if (oneBased >= 1 && oneBased <= _nsPresets.Count) { ApplyNightSkyPreset(oneBased - 1); }
        else if (_nsActiveIdx >= 0) { ApplyNightSkyPreset(_nsActiveIdx); }
    }

    /// Build the Night-tab dropdown (called from the Night-tab UI build).
    private void BuildNightSkyPresetPicker(Container parent)
    {
        LoadNightSkyPresets();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— night sky preset —", 0);
        for (int i = 0; i < _nsPresets.Count; i++) { pick.AddItem(_nsPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplyNightSkyPreset((int)idx - 1); } };
        parent.AddChild(pick);
    }

    public void ApplyNightSkyPreset(int idx)
    {
        if (idx < 0 || idx >= _nsPresets.Count) { return; }
        var vals = _nsPresets[idx].values;
        bool compose = false;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            switch (id)   // colors: the registry color path can't take a JSON array → set state directly
            {
                case "gx_core_color": _stars.CoreColor = ColFromArr(vals[k], _stars.CoreColor); compose = true; continue;
                case "gx_arm_color":  _stars.ArmColor  = ColFromArr(vals[k], _stars.ArmColor);  compose = true; continue;
                case "neb1_color":    _stars.Neb1Color = ColFromArr(vals[k], _stars.Neb1Color); compose = true; continue;
                case "neb2_color":    _stars.Neb2Color = ColFromArr(vals[k], _stars.Neb2Color); compose = true; continue;
            }
            if (_byId.TryGetValue(id, out var c)) { SetWidgetValue(c, vals[k]); }   // scalars: slider + apply (re-bakes)
            else { GD.PushWarning($"[nightsky] unknown control id '{id}' in preset '{_nsPresets[idx].name}'"); }
        }
        if (compose) { ComposeLighting(); }   // one re-bake for the color changes (coalesced with the scalar ones)
        GD.Print($"[nightsky] applied '{_nsPresets[idx].name}'");
    }
}
