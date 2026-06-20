using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Sun-disc PRESETS (data/sun_presets.json): curated disc looks (size/limb + the procedural
/// surface knobs). Disc-only — orthogonal to the Time/Weather/Grade axes (pick any sun look on
/// top of any time/weather/grade). Mirrors the cloud-preset path in TerrainLabUI.Clouds.cs:
/// load → a Light-tab OptionButton → ApplySunPreset, routed through the registry so nothing
/// silently no-ops (an unknown id warns). The optional sun_surface_color (no slider) is applied
/// directly via the CloudVolume setter.
public partial class TerrainLabUI : Control
{
    private readonly List<(string name, Godot.Collections.Dictionary values)> _sunPresets = new();
    private int _sunPresetIdx;
    private int _sunActiveIdx = -1;   // the "active" preset to apply at startup (-1 = none/neutral)

    private void LoadSunPresets()
    {
        if (_sunPresets.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/sun_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[sunpresets] data/sun_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _sunPresets.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        string active = root.ContainsKey("active") ? root["active"].AsString() : "";
        _sunActiveIdx = _sunPresets.FindIndex(p => p.name == active);
        if (_sunActiveIdx >= 0) { _sunPresetIdx = _sunActiveIdx; }   // review cycle starts on the default
        GD.Print($"[sunpresets] loaded {_sunPresets.Count}, active='{active}' (idx {_sunActiveIdx})");
    }

    /// Apply the JSON "active" preset (the everyday default). Called at startup when no --sunpreset.
    private void ApplyActiveSunPreset()
    {
        LoadSunPresets();
        if (_sunActiveIdx >= 0) { ApplySunPreset(_sunActiveIdx); }
    }

    /// Build the Light-tab dropdown (called from the Light-tab UI build).
    private void BuildSunPresetPicker(Container parent)
    {
        LoadSunPresets();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— sun preset —", 0);
        for (int i = 0; i < _sunPresets.Count; i++) { pick.AddItem(_sunPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplySunPreset((int)idx - 1); } };
        parent.AddChild(pick);
    }

    public void ApplySunPreset(int idx)
    {
        if (idx < 0 || idx >= _sunPresets.Count) { return; }
        var vals = _sunPresets[idx].values;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "sun_surface_color")
            {
                var arr = vals[k].AsGodotArray();
                if (arr.Count >= 3) { _cloud?.SetSunSurfaceColor(new Color(arr[0].AsSingle(), arr[1].AsSingle(), arr[2].AsSingle())); }
                continue;
            }
            if (_byId.TryGetValue(id, out var c)) { SetWidgetValue(c, vals[k]); }
            else { GD.PushWarning($"[sunpresets] unknown control id '{id}' in preset '{_sunPresets[idx].name}'"); }
        }
        // a preset that omits the color override resets it to off (so it doesn't linger from a prior pick).
        if (!vals.ContainsKey("sun_surface_color")) { _cloud?.SetSunSurfaceColor(new Color(0, 0, 0)); }
        _sunPresetIdx = idx;
        GD.Print($"[sunpresets] applied '{_sunPresets[idx].name}'");
    }
}
