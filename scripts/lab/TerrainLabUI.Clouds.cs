using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // ---- cloud knobs → CloudVolume (separation: UI never touches cloud internals,
    //      only the public knob setters). _cloud is wired in Stage 3; null = no-op,
    //      so the Clouds tab is inert (but present + tunable in state) until then. --
    private CloudVolume? _cloud;
    private GodRaysScreen? _godraysScreen;   // screen-space radial scatter (GPU Gems 3) — THE god-ray layer
    private AtmosphereCompute? _atmosphere;   // AT-1 GPU physical sky (Hillaire LUTs); default off
    private bool _atmosphereOn;               // AT-1 on-state mirror (ComposeLighting drops FogSkyAffect so fog stops washing the physical sky)
    private void ApplyCloudFloat(string knob, float v)
    {
        // Screen-space god-ray tunables (GPU Gems radial scatter). The froxel-fog layer was dropped
        // 2026-06-19 — it read as a washy fog; screen-space is the crisp AAA look.
        switch (knob)
        {
            case "godray_strength":     _godraysScreen?.SetStrength(v); return;     // beam intensity
            case "godray_length":       _godraysScreen?.SetDensity(v); return;      // LOWER density = longer beams
            case "godray_decay":        _godraysScreen?.SetDecay(v); return;        // shaft falloff
            case "godray_cloud_radius": _godraysScreen?.SetCloudRadius(v); return;  // cloud-detect radius around sun
            case "godray_cloud_lum":    _godraysScreen?.SetCloudLum(v); return;     // cloud darkness threshold
        }
        if (knob.StartsWith("cirrus_")) { _cloud?.SetCirrus(knob, v); return; }   // CO-2 cirrus sky-layer uniforms
        _cloud?.SetKnob(knob, v);
    }
    private void ApplyCloudInt(string knob, int v) => _cloud?.SetKnobInt(knob, v);
    private void ApplyCloudBool(string knob, bool on)
    {
        if (knob == "atmosphere_on") { _atmosphereOn = on; _cloud?.SetAtmosphereOn(on); _atmosphere?.SetEnabled(on); ComposeLighting(); return; }   // AT-1: ComposeLighting re-applies FogSkyAffect so fog stops washing the sky
        if (knob == "godrays") { _godraysScreen?.SetEnabled(on); return; }   // screen-space radial beams
        if (knob == "godray_backlit") { _godraysScreen?.SetCloudInvert(on); return; }   // occluder polarity (sun behind cloud)
        if (knob == "deck_debug") { _cloud?.SetDeckDebug(on); return; }   // deck-ID overlay (debug)
        if (knob == "cirrus_on") { _cloud?.SetCirrusOn(on); return; }     // CO-2 cirrus sky-layer toggle
        _cloud?.SetKnobBool(knob, on);
        // clouds-enabled also gates the ground-shadow sampling in the terrain light()
        if (knob == "enabled") { _terrain.SetBool("cloud_shadow_on", on); }
    }

    // ---- cloud PRESETS (named sky looks, data/cloud_presets.json) --------------
    private readonly List<(string name, Godot.Collections.Dictionary values, Godot.Collections.Array? layers)> _cloudPresets = new();

    private void BuildCloudPresetPicker(VBoxContainer col)
    {
        LoadCloudPresets();
        col.AddChild(new Label { Text = "PRESET (named sky look)" });
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— custom —", 0);
        for (int i = 0; i < _cloudPresets.Count; i++) { pick.AddItem(_cloudPresets[i].name, i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { ApplyCloudPreset((int)idx - 1); } };
        col.AddChild(pick);
        col.AddChild(new HSeparator());
    }

    private void LoadCloudPresets()
    {
        if (_cloudPresets.Count > 0) { return; }
        string abs = ProjectSettings.GlobalizePath("res://data/cloud_presets.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("presets")) { return; }
        foreach (Variant p in root["presets"].AsGodotArray())
        {
            var d = p.AsGodotDictionary();
            string name = d.ContainsKey("name") ? d["name"].AsString() : "preset";
            // optional "layers" block = an authored deck stack (roadmap #2); absent → default stack.
            Godot.Collections.Array? layers = d.ContainsKey("layers") ? d["layers"].AsGodotArray() : null;
            if (d.ContainsKey("values")) { _cloudPresets.Add((name, d["values"].AsGodotDictionary(), layers)); }
        }
    }

    /// Apply a cloud preset by setting each named control's widget (routes through
    /// ApplyControl → CloudVolume). Controls not in the preset are left as-is.
    private void ApplyCloudPreset(int idx, float jitter = 0f)
    {
        if (idx < 0 || idx >= _cloudPresets.Count) { return; }
        // CO-4: reset the cloud-TYPE levers first so every preset is SELF-CONTAINED — a preset that
        // doesn't mention cirrus/stratus/profile/macro gets the clean cumulus look, not leftover state
        // from a previously-selected preset. The preset's own `values` below re-enable what it wants.
        foreach (var id in new[] { "cloud_profile_on", "cloud_cirrus_on" })
            if (_byId.TryGetValue(id, out var bc)) { SetWidgetValue(bc, false); }
        foreach (var id in new[] { "cloud_shape_mode", "cloud_anti_repeat" })
            if (_byId.TryGetValue(id, out var fc)) { SetWidgetValue(fc, 0.0f); }
        var values = _cloudPresets[idx].values;
        foreach (var key in values.Keys)
        {
            string id = key.AsString();
            if (!_byId.TryGetValue(id, out LabControl c)) { continue; }
            if (jitter > 0f && c.Locked) { continue; }   // randomize respects locks; explicit pick doesn't
            float v = values[key].AsSingle();
            // ranged presets / surprise-me (roadmap #3): seeded ± jitter of the knob's range,
            // CENTERED on a known-good preset value → always coherent. Respects the rand flag
            // (so e.g. shadow_strength stays put).
            if (jitter > 0f && c.Rand)
            {
                v = Mathf.Clamp(v + (float)(_rng.NextDouble() * 2.0 - 1.0) * jitter * (c.Max - c.Min), c.Min, c.Max);
            }
            SetWidgetValue(c, v);
        }
        // roadmap #2: the preset's deck stack (or the default stack when it authors none), applied
        // AFTER the knobs so layer 0 picks up this preset's coverage/density via PackLayers.
        var layers = _cloudPresets[idx].layers;
        _cloud?.SetLayers(layers != null ? CloudLayers.FromGodotArray(layers) : CloudLayers.Load());
        GD.Print($"Clouds: applied preset '{_cloudPresets[idx].name}'" + (jitter > 0f ? " (jittered)" : ""));
    }
}
