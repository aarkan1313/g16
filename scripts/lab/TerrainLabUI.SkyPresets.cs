using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Forwarding shims for the SkyPresets library (decomposition Phase 3a). The sun/celestial/fantasy/mood
/// apply+load LOGIC moved to SkyPresets.cs; this partial keeps the OLD method/property names so the rest of
/// the UI (CLI, review replay, registry pickers, _Ready) compiles unchanged — the same migration-shim pattern
/// as TerrainLabUI.Lighting.cs. The dropdown pickers stay here because they build scene-tree UI.
public partial class TerrainLabUI : Control
{
    private SkyPresets _sky = null!;

    // Constructed in _Ready (before LoadMoods + any preset apply). _cloud is captured by the lambda so the
    // sun-surface setter is null-safe even though the CloudVolume node is attached deferred.
    private void InitSkyPresets() => _sky = new SkyPresets(this, _lighting, c => _cloud?.SetSunSurfaceColor(c));

    // ── load forwarders ──
    private void LoadMoods() => _sky.LoadMoods();
    private void LoadSunPresets() => _sky.LoadSun();
    private void LoadCelestialPresets() => _sky.LoadCelestial();
    private void LoadFantasyPresets() => _sky.LoadFantasy();

    // ── apply forwarders ──
    private void ApplyMood(int idx) => _sky.ApplyMood(idx);
    private void ApplySunPreset(int idx) => _sky.ApplySun(idx);
    private void ApplyCelestialPreset(int idx) => _sky.ApplyCelestial(idx);
    private void ApplyFantasyPreset(int idx) => _sky.ApplyFantasy(idx);
    private void ApplyActiveSunPreset() => _sky.ApplyActiveSun();
    private void ApplyActiveCelestialPreset() => _sky.ApplyActiveCelestial();
    private void ApplyDefaultMood()
    {
        int idx = Mathf.Clamp(SkyPresets.DefaultMoodIdx, 0, _sky.MoodCount - 1);
        _sky.ApplyMood(idx);
        if (_moodPick != null) { _moodPick.Select(idx); }   // reflect it in the dropdown
    }

    // ── state forwarders (old field names) used by review replay / registry / _Ready ──
    private int _currentMood => _sky.CurrentMood;
    private IReadOnlyList<string> _moodNames => _sky.MoodNames;
    private IReadOnlyList<(string name, Godot.Collections.Dictionary values)> _sunPresets => _sky.Sun;
    private int _sunPresetIdx { get => _sky.SunIdx; set => _sky.SunIdx = value; }
    private IReadOnlyList<(string name, Godot.Collections.Dictionary values)> _fantasyPresets => _sky.Fantasy;

    // ── dropdown pickers (scene-tree UI stays here; data + apply route through SkyPresets) ──
    private void BuildSunPresetPicker(Container parent)
    {
        _sky.LoadSun();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— sun preset —", 0);
        for (int i = 0; i < _sky.Sun.Count; i++) { pick.AddItem(_sky.Sun[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { _sky.ApplySun((int)idx - 1); } };
        parent.AddChild(pick);
    }

    private void BuildCelestialPresetPicker(Container parent)
    {
        _sky.LoadCelestial();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— celestial preset —", 0);
        for (int i = 0; i < _sky.CelestialCount; i++) { pick.AddItem(_sky.CelestialName(i), i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { _sky.ApplyCelestial((int)idx - 1); } };
        parent.AddChild(pick);
    }

    private void BuildFantasyPresetPicker(Container parent)
    {
        _sky.LoadFantasy();
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— fantasy / exotic sky —", 0);
        for (int i = 0; i < _sky.Fantasy.Count; i++) { pick.AddItem(_sky.Fantasy[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { _sky.ApplyFantasy((int)idx - 1); } };
        parent.AddChild(pick);
    }
}
