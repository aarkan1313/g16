using Godot;

namespace WG16.Lab;

/// Forwarding shims for PresetsManager (decomposition Phase 3b). The save/load/disk LOGIC + the U3 color
/// serialization moved to PresetsManager.cs; this partial keeps the preset NAME field + the dropdown (UI)
/// and the old method names the button wiring calls. Same migration-shim pattern as the other extractions.
public partial class TerrainLabUI : Control
{
    private PresetsManager _presetsMgr = null!;
    private void InitPresetsManager() => _presetsMgr = new PresetsManager(this, () => _luminaryList);

    private void SavePreset()
    {
        string name = string.IsNullOrWhiteSpace(_presetName.Text) ? $"preset{_presetsMgr.Count + 1}" : _presetName.Text;
        _presetsMgr.Save(name);
        RefreshPresetList();
    }

    private void LoadSelectedPreset()
    {
        if (_presetPick.Selected < 0) { return; }
        _presetsMgr.Load(_presetPick.GetItemText(_presetPick.Selected));
    }

    private void RefreshPresetList()
    {
        _presetPick.Clear();
        foreach (string k in _presetsMgr.Names) { _presetPick.AddItem(k); }
    }

    private void LoadPresetsFromDisk()
    {
        _presetsMgr.LoadFromDisk();
        RefreshPresetList();
    }
}
