using Godot;
using System;

namespace WG16.Lab;

/// Randomize + lock logic for the lab controls (decomposition Phase 3c). Rolls unlocked controls, the
/// flat-baseline reset, and the per-tab / all locks. Extracted from the TerrainLabUI god-class: it depends
/// only on the ILabControls registry, a shared RNG, and three injected hooks — the coherent cloud "surprise
/// me" (which lives cloud-side), and the material/zone counts (registry-build data). Cloud knobs are NOT
/// rolled independently here (that produced incoherent skies); they go through the cloud preset+jitter path.
public sealed class LabRandomizer
{
    private readonly ILabControls _reg;
    private readonly Random _rng;
    private readonly Action _randomizeClouds;
    private readonly Func<int> _materialCount;
    private readonly Func<int> _zoneCount;

    public LabRandomizer(ILabControls reg, Random rng, Action randomizeClouds, Func<int> materialCount, Func<int> zoneCount)
    {
        _reg = reg;
        _rng = rng;
        _randomizeClouds = randomizeClouds;
        _materialCount = materialCount;
        _zoneCount = zoneCount;
    }

    /// Flat baseline: turn EVERY visual contributor off so the user can add them back one at a time and
    /// find what causes the speckle. The plainest possible render: albedo only, no normal maps, no specular,
    /// no macro/contact/splat, no SSAO/shadow/fog/sun-shading effects.
    public void FlatBaseline()
    {
        foreach (LabControl c in _reg.Controls)
        {
            if (c.Type == "toggle" || c.Type == "scene")
            {
                bool target = c.Scene == "sun";   // turn OFF everything except 'sun' (keep some light so it's visible)
                _reg.SetValue(c, target);
            }
        }
        // force matte (no specular) + no normal maps explicitly
        if (_reg.TryGet("dbg_fullrough", out var fr)) { _reg.SetValue(fr, true); }
        if (_reg.TryGet("dbg_normalmap", out var nm)) { _reg.SetValue(nm, false); }
        GD.Print("TerrainLab: FLAT BASELINE — everything off; re-enable contributors one at a time (Debug tab for normals/spec/scene; Color/Detail/Splat tabs for the rest)");
    }

    /// Global randomize: rolls all UNLOCKED controls flagged rand:true. Clouds are handled COHERENTLY.
    public void Randomize()
    {
        foreach (LabControl c in _reg.Controls)
        {
            if (c.Locked || !c.Rand) { continue; }
            RandomizeControl(c);
        }
        _randomizeClouds();
        GD.Print("TerrainLab: randomized (unlocked controls)");
    }

    /// Per-tab randomize: rolls EVERY unlocked control on the tab, ignoring the rand flag.
    public void RandomizeTab(string tab)
    {
        if (tab == "Clouds") { _randomizeClouds(); GD.Print("TerrainLab: randomized tab 'Clouds' (coherent surprise-me)"); return; }
        foreach (LabControl c in _reg.Controls)
        {
            if (c.Tab != tab || c.Locked) { continue; }
            RandomizeControl(c);
        }
        GD.Print($"TerrainLab: randomized tab '{tab}'");
    }

    /// Roll one control to a random value. Cloud knobs are skipped (rolled coherently via the cloud path).
    private bool RandomizeControl(LabControl c)
    {
        if (c.Type == "cloudf" || c.Type == "cloudi" || c.Type == "cloud") { return false; }
        switch (c.Type)
        {
            case "slider":
            case "scenef":
            case "cloudf":
                _reg.SetValue(c, c.Min + (float)_rng.NextDouble() * (c.Max - c.Min));
                break;
            case "cloudi":
                _reg.SetValue(c, (float)Math.Round(c.Min + _rng.NextDouble() * (c.Max - c.Min)));
                break;
            case "toggle":
            case "scene":
            case "cloud":
                _reg.SetValue(c, _rng.NextDouble() < 0.75);   // bias ON so a roll isn't all-off
                break;
            case "enum":
                _reg.SetValue(c, _rng.Next(c.Options.Length));
                break;
            case "material":
                _reg.SetValue(c, _rng.Next(_materialCount()));
                break;
            case "companion":
                _reg.SetValue(c, _rng.Next(_zoneCount()));
                break;
        }
        return c.Rebake;
    }

    public void SetAllLocks(bool locked)
    {
        foreach (LabControl c in _reg.Controls) { c.Locked = locked; if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; } }
    }

    public void SetTabLocks(string tab, bool locked)
    {
        foreach (LabControl c in _reg.Controls)
        {
            if (c.Tab != tab) { continue; }
            c.Locked = locked;
            if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; }
        }
    }
}
