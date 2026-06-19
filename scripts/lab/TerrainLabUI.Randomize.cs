using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    /// Flat baseline: turn EVERY visual contributor off so the user can add them
    /// back one at a time and find what causes the speckle. The plainest possible
    /// render: albedo only, no normal maps, no specular, no macro/contact/splat,
    /// no SSAO/shadow/fog/sun-shading effects.
    private void FlatBaseline()
    {
        foreach (LabControl c in _controls)
        {
            if (c.Type == "toggle" || c.Type == "scene")
            {
                // turn OFF everything except 'sun' (keep some light so it's visible)
                bool target = c.Scene == "sun";
                SetWidgetValue(c, target);
            }
        }
        // force matte (no specular) + no normal maps explicitly
        if (_byId.TryGetValue("dbg_fullrough", out var fr)) { SetWidgetValue(fr, true); }
        if (_byId.TryGetValue("dbg_normalmap", out var nm)) { SetWidgetValue(nm, false); }
        GD.Print("TerrainLab: FLAT BASELINE — everything off; re-enable contributors one at a time (Debug tab for normals/spec/scene; Color/Detail/Splat tabs for the rest)");
    }

    // ---- randomize / lock -----------------------------------------------------

    /// Coherent cloud "surprise me" (roadmap #3): pick a known-good preset and apply seeded
    /// jitter around it, so the result is ALWAYS a believable sky — vs the old behaviour of
    /// rolling every cloud knob independently into incoherent garbage (the user's "randomize
    /// doesn't work"). One job: produce a coherent random cloud look.
    private void RandomizeClouds()
    {
        if (_cloudPresets.Count == 0) { return; }
        int idx = _rng.Next(_cloudPresets.Count);
        ApplyCloudPreset(idx, 0.18f);
    }

    /// Global randomize: rolls all UNLOCKED controls flagged rand:true. Cloud controls are
    /// handled COHERENTLY (preset + jitter), not rolled independently.
    private void Randomize()
    {
        bool needRebake = false;
        foreach (LabControl c in _controls)
        {
            if (c.Locked || !c.Rand) { continue; }
            if (RandomizeControl(c)) { needRebake = true; }
        }
        if (needRebake) { _terrain.RebakeSplat(); }
        RandomizeClouds();
        GD.Print("TerrainLab: randomized (unlocked controls)");
    }

    /// Per-tab randomize: rolls EVERY unlocked control on the tab, ignoring the
    /// rand flag (the user explicitly diced this tab, so roll everything tunable).
    private void RandomizeTab(string tab)
    {
        if (tab == "Clouds") { RandomizeClouds(); GD.Print("TerrainLab: randomized tab 'Clouds' (coherent surprise-me)"); return; }
        bool needRebake = false;
        foreach (LabControl c in _controls)
        {
            if (c.Tab != tab || c.Locked) { continue; }
            if (RandomizeControl(c)) { needRebake = true; }
        }
        if (needRebake) { _terrain.RebakeSplat(); }
        GD.Print($"TerrainLab: randomized tab '{tab}'");
    }

    /// Roll one control to a random value. Returns true if it needs a splat rebake.
    /// Covers every control type incl. cloud knobs (the previous switch missed the
    /// cloud/scene types, so those never randomized).
    private bool RandomizeControl(LabControl c)
    {
        // Cloud controls are randomized COHERENTLY via RandomizeClouds() (preset + seeded jitter),
        // NOT rolled independently here — independent per-knob rolls produce incoherent/garbage
        // skies (the user's "randomize doesn't work"). Skip them.
        if (c.Type == "cloudf" || c.Type == "cloudi" || c.Type == "cloud") { return false; }
        switch (c.Type)
        {
            case "slider":
            case "scenef":
            case "cloudf":
                SetWidgetValue(c, c.Min + (float)_rng.NextDouble() * (c.Max - c.Min));
                break;
            case "cloudi":
                SetWidgetValue(c, (float)Math.Round(c.Min + _rng.NextDouble() * (c.Max - c.Min)));
                break;
            case "toggle":
            case "scene":
            case "cloud":
                SetWidgetValue(c, _rng.NextDouble() < 0.75);   // bias ON so a roll isn't all-off
                break;
            case "enum":
                SetWidgetValue(c, _rng.Next(c.Options.Length));
                break;
            case "material":
                SetWidgetValue(c, _rng.Next(_materials.Count));
                break;
            case "companion":
                SetWidgetValue(c, _rng.Next(_zoneNames.Length));
                break;
        }
        return c.Rebake;
    }

    private void SetAllLocks(bool locked)
    {
        foreach (LabControl c in _controls) { c.Locked = locked; if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; } }
    }

    private void SetTabLocks(string tab, bool locked)
    {
        foreach (LabControl c in _controls)
        {
            if (c.Tab != tab) { continue; }
            c.Locked = locked;
            if (c.LockBox != null) { c.LockBox.ButtonPressed = locked; }
        }
    }
}
