using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Sky/celestial PRESET library — the four data-driven look systems that sit orthogonally on top of the
/// Time/Weather/Grade axes (pick any sun/moon/mood look on top of any time of day):
///   • sun presets      (data/sun_presets.json)       — disc size/limb + procedural surface knobs
///   • celestial presets(data/celestial_presets.json) — moon + stars + night looks
///   • fantasy presets  (data/fantasy_presets.json)   — cross-system "alien sky" = a sun + celestial + tint
///   • lighting moods   (data/lighting_moods.json)    — coordinated Time/SunDisc/Weather/Grade looks
///
/// Extracted from the TerrainLabUI god-class (decomposition Phase 3a). Co-locating all four removes the
/// cross-call wiring (fantasy applies a named sun + celestial as sub-presets). It routes everything through
/// the narrow ILabControls registry façade so nothing silently no-ops (an unknown id warns), and reaches the
/// moon/sky-tint state + recompose through the LightingComposer. The Light/Night-tab dropdown pickers stay
/// UI-side in TerrainLabUI (they just call ApplySun/ApplyCelestial/ApplyFantasy/ApplyMood here).
public sealed class SkyPresets
{
    private readonly ILabControls _reg;
    private readonly LightingComposer _lighting;
    private readonly Action<Color> _setSunSurfaceColor;   // null-safe wrapper over CloudVolume.SetSunSurfaceColor

    public const int DefaultMoodIdx = 5;   // "Clear Alpine" — clean neutral good-day look

    private readonly List<(string name, Godot.Collections.Dictionary values)> _sun = new();
    private int _sunIdx;
    private int _sunActive = -1;   // the "active" preset to apply at startup (-1 = none/neutral)
    private readonly List<(string name, Godot.Collections.Dictionary values)> _cel = new();
    private int _celActive = -1;
    private readonly List<(string name, Godot.Collections.Dictionary values)> _fantasy = new();
    private Godot.Collections.Array _moods = new();
    private readonly List<string> _moodNames = new();
    private int _currentMood = -1;

    public SkyPresets(ILabControls reg, LightingComposer lighting, Action<Color> setSunSurfaceColor)
    {
        _reg = reg;
        _lighting = lighting;
        _setSunSurfaceColor = setSunSurfaceColor;
    }

    // ---- accessors for the UI pickers, the review replay, and the TerrainLabUI forwarders ----
    public IReadOnlyList<(string name, Godot.Collections.Dictionary values)> Sun => _sun;
    public int SunIdx { get => _sunIdx; set => _sunIdx = value; }
    public IReadOnlyList<(string name, Godot.Collections.Dictionary values)> Fantasy => _fantasy;
    public int CelestialCount => _cel.Count;
    public string CelestialName(int i) => _cel[i].name;
    public IReadOnlyList<string> MoodNames => _moodNames;
    public int MoodCount => _moods.Count;
    public int CurrentMood => _currentMood;

    // ================= SUN =================
    public void LoadSun()
    {
        if (_sun.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/sun_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[sunpresets] data/sun_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _sun.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        string active = root.ContainsKey("active") ? root["active"].AsString() : "";
        _sunActive = _sun.FindIndex(p => p.name == active);
        if (_sunActive >= 0) { _sunIdx = _sunActive; }   // review cycle starts on the default
        GD.Print($"[sunpresets] loaded {_sun.Count}, active='{active}' (idx {_sunActive})");
    }

    /// Apply the JSON "active" preset (the everyday default). Called at startup when no --sunpreset.
    public void ApplyActiveSun()
    {
        LoadSun();
        if (_sunActive >= 0) { ApplySun(_sunActive); }
    }

    public void ApplySun(int idx)
    {
        if (idx < 0 || idx >= _sun.Count) { return; }
        var vals = _sun[idx].values;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "sun_surface_color")
            {
                var arr = vals[k].AsGodotArray();
                if (arr.Count >= 3) { _setSunSurfaceColor(new Color(arr[0].AsSingle(), arr[1].AsSingle(), arr[2].AsSingle())); }
                continue;
            }
            if (_reg.TryGet(id, out var c)) { _reg.SetValue(c, vals[k]); }
            else { GD.PushWarning($"[sunpresets] unknown control id '{id}' in preset '{_sun[idx].name}'"); }
        }
        // a preset that omits the color override resets it to off (so it doesn't linger from a prior pick).
        if (!vals.ContainsKey("sun_surface_color")) { _setSunSurfaceColor(new Color(0, 0, 0)); }
        _sunIdx = idx;
        GD.Print($"[sunpresets] applied '{_sun[idx].name}'");
    }

    // ================= CELESTIAL =================
    public void LoadCelestial()
    {
        if (_cel.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/celestial_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[celestial] data/celestial_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _cel.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        string active = root.ContainsKey("active") ? root["active"].AsString() : "";
        _celActive = _cel.FindIndex(p => p.name == active);
        GD.Print($"[celestial] loaded {_cel.Count}, active='{active}' (idx {_celActive})");
    }

    public void ApplyActiveCelestial()
    {
        LoadCelestial();
        if (_celActive >= 0) { ApplyCelestial(_celActive); }
    }

    public void ApplyCelestial(int idx)
    {
        if (idx < 0 || idx >= _cel.Count) { return; }
        var vals = _cel[idx].values;
        var moon = _lighting.Moon;
        bool compose = false;
        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "moon_color")      { moon.Color = ColFromArr(vals[k], moon.Color); compose = true; continue; }
            if (id == "moonlight_color") { moon.LightColor = ColFromArr(vals[k], moon.LightColor); compose = true; continue; }
            if (_reg.TryGet(id, out var c)) { _reg.SetValue(c, vals[k]); }
            else { GD.PushWarning($"[celestial] unknown control id '{id}' in preset '{_cel[idx].name}'"); }
        }
        // a preset that omits the colors resets them to the cool defaults (so an 'exotic' tint doesn't linger).
        if (!vals.ContainsKey("moon_color"))      { moon.Color = new Color(0.85f, 0.88f, 1.0f); compose = true; }
        if (!vals.ContainsKey("moonlight_color")) { moon.LightColor = new Color(0.60f, 0.70f, 1.0f); compose = true; }
        if (compose) { _lighting.Compose(); }
        GD.Print($"[celestial] applied '{_cel[idx].name}'");
    }

    // ================= FANTASY (cross-system) =================
    public void LoadFantasy()
    {
        if (_fantasy.Count > 0) { return; }
        using var f = Godot.FileAccess.Open("res://data/fantasy_presets.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[fantasy] data/fantasy_presets.json missing"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        var pres = root["presets"].AsGodotDictionary();
        foreach (var key in pres.Keys) { _fantasy.Add((key.AsString(), pres[key].AsGodotDictionary())); }
        GD.Print($"[fantasy] loaded {_fantasy.Count}");
    }

    /// Compose a fantasy sky: apply the named sun + celestial sub-presets first, then the cross-system
    /// overrides (sky tint, moon/moonlight color, exposure, cloud coverage…). One Compose at the end.
    public void ApplyFantasy(int idx)
    {
        if (idx < 0 || idx >= _fantasy.Count) { return; }
        LoadSun(); LoadCelestial();
        var vals = _fantasy[idx].values;
        var moon = _lighting.Moon;
        _lighting.SkyTint = Colors.White;   // reset → self-contained (a preset that omits the tint clears a prior one)

        if (vals.ContainsKey("sun"))
        {
            int si = _sun.FindIndex(p => p.name == vals["sun"].AsString());
            if (si >= 0) { ApplySun(si); } else { GD.PushWarning($"[fantasy] '{_fantasy[idx].name}': unknown sun preset '{vals["sun"].AsString()}'"); }
        }
        if (vals.ContainsKey("celestial"))
        {
            int ci = _cel.FindIndex(p => p.name == vals["celestial"].AsString());
            if (ci >= 0) { ApplyCelestial(ci); } else { GD.PushWarning($"[fantasy] '{_fantasy[idx].name}': unknown celestial preset '{vals["celestial"].AsString()}'"); }
        }

        foreach (var k in vals.Keys)
        {
            string id = k.AsString();
            if (id == "sun" || id == "celestial") { continue; }
            if (id == "sky_tint")        { _lighting.SkyTint = ColFromArr(vals[k], _lighting.SkyTint); continue; }
            if (id == "moon_color")      { moon.Color = ColFromArr(vals[k], moon.Color); continue; }
            if (id == "moonlight_color") { moon.LightColor = ColFromArr(vals[k], moon.LightColor); continue; }
            if (_reg.TryGet(id, out var c)) { _reg.SetValue(c, vals[k]); }
            else { GD.PushWarning($"[fantasy] unknown control id '{id}' in preset '{_fantasy[idx].name}'"); }
        }
        _lighting.Compose();   // bake the sky tint + moon colors (sun/celestial appliers ran above)
        GD.Print($"[fantasy] applied '{_fantasy[idx].name}'");
    }

    // ================= MOODS =================
    public void LoadMoods()
    {
        if (_moods.Count > 0) { return; }
        string abs = ProjectSettings.GlobalizePath("res://data/lighting_moods.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("moods")) { return; }
        _moods = root["moods"].AsGodotArray();
        foreach (Variant m in _moods) { _moodNames.Add(m.AsGodotDictionary()["name"].AsString()); }
    }

    /// Apply a coordinated mood: split it into the Time/SunDisc/Weather/Grade axis states, then compose.
    /// The actual scene writes live in LightingComposer.Compose — the one writer.
    public void ApplyMood(int idx)
    {
        if (idx < 0 || idx >= _moods.Count) { return; }
        _currentMood = idx;
        _lighting.MoodToStates(_moods[idx].AsGodotDictionary());
        _lighting.Compose();
        GD.Print($"TerrainLab: mood -> {_moodNames[idx]}");
    }

    private static Color ColFromArr(Variant v, Color def)
    {
        var a = v.AsGodotArray();
        return a.Count >= 3 ? new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()) : def;
    }
}
