using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    private const int DefaultMoodIdx = 5;   // "Clear Alpine" — clean neutral good-day look
    private void ApplyDefaultMood()
    {
        int idx = Mathf.Clamp(DefaultMoodIdx, 0, _moods.Count - 1);
        ApplyMood(idx);
        if (_moodPick != null) { _moodPick.Select(idx); }   // reflect it in the dropdown
    }

    // ---- lighting MOODS (curated, coordinated looks) --------------------------

    private readonly List<string> _moodNames = new();
    private Godot.Collections.Array _moods = new();

    private void LoadMoods()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/lighting_moods.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("moods")) { return; }
        _moods = root["moods"].AsGodotArray();
        foreach (Variant m in _moods) { _moodNames.Add(m.AsGodotDictionary()["name"].AsString()); }
    }

    private static Color Col(Variant v) { var a = v.AsGodotArray(); return new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
    private static float F(Godot.Collections.Dictionary d, string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;

    /// Apply a coordinated mood: split it into the Time/SunDisc/Weather/Grade axis states, then compose.
    /// The actual scene writes live in ComposeLighting (TerrainLabUI.Lighting.cs) — the one writer.
    private int _currentMood = -1;
    private void ApplyMood(int idx)
    {
        if (idx < 0 || idx >= _moods.Count) { return; }
        _currentMood = idx;
        MoodToStates(_moods[idx].AsGodotDictionary());
        ComposeLighting();
        GD.Print($"TerrainLab: mood -> {_moodNames[idx]}");
    }
}
