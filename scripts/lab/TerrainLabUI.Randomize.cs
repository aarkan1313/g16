using Godot;

namespace WG16.Lab;

/// Forwarding shims for LabRandomizer (decomposition Phase 3c). The randomize/lock/flat-baseline LOGIC moved
/// to LabRandomizer.cs; this partial keeps the old method names the Registry button wiring calls, plus the
/// coherent cloud "surprise me" (RandomizeClouds) which stays here because it is cloud-coupled (_cloudPresets
/// + ApplyCloudPreset) and is injected into LabRandomizer as a delegate.
public partial class TerrainLabUI : Control
{
    private LabRandomizer _randomizer = null!;
    private void InitRandomizer() =>
        _randomizer = new LabRandomizer(this, _rng, RandomizeClouds, () => _materials.Count, () => _zoneNames.Length);

    /// Coherent cloud "surprise me" (roadmap #3): pick a known-good preset and apply seeded jitter around it,
    /// so the result is ALWAYS a believable sky — vs rolling every cloud knob independently into garbage.
    private void RandomizeClouds()
    {
        if (_cloudPresetsObj.Count == 0) { return; }
        int idx = _rng.Next(_cloudPresetsObj.Count);
        _cloudPresetsObj.Apply(idx, 0.18f);
    }

    // ── forwarders (old names the Registry buttons wire to) ──
    private void FlatBaseline() => _randomizer.FlatBaseline();
    private void Randomize() => _randomizer.Randomize();
    private void RandomizeTab(string tab) => _randomizer.RandomizeTab(tab);
    private void SetAllLocks(bool locked) => _randomizer.SetAllLocks(locked);
    private void SetTabLocks(string tab, bool locked) => _randomizer.SetTabLocks(tab, locked);
}
