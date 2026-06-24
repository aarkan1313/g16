using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Cloud PRESETS — named sky looks (data/cloud_presets.json): a set of cloud-knob values + an optional
/// authored deck stack. Extracted from the TerrainLabUI cloud bridge (decomposition follow-up), parallel to
/// SkyPresets. Routes knob values through the ILabControls registry (so they reach CloudVolume via the normal
/// apply path) and pushes the deck stack to the CloudVolume directly. Supports seeded ± jitter around a
/// known-good preset for the coherent "surprise me" randomize. The Clouds-tab picker stays UI-side.
///
/// NOT here: the cloud node refs + the AT-1/2/3 on-state mirrors + the ApplyCloud* knob routers — those are
/// woven into the _Process frame loop (RID-activation gates, debug keys) and stay on TerrainLabUI.
public sealed class CloudPresets
{
    private readonly ILabControls _reg;
    private readonly Random _rng;
    private readonly Func<CloudVolume?> _getCloud;
    private readonly List<(string name, Godot.Collections.Dictionary values, Godot.Collections.Array? layers)> _presets = new();

    public CloudPresets(ILabControls reg, Random rng, Func<CloudVolume?> getCloud)
    {
        _reg = reg;
        _rng = rng;
        _getCloud = getCloud;
    }

    public int Count => _presets.Count;
    public string Name(int i) => _presets[i].name;

    public void Load()
    {
        if (_presets.Count > 0) { return; }
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
            if (d.ContainsKey("values")) { _presets.Add((name, d["values"].AsGodotDictionary(), layers)); }
        }
    }

    /// Apply a cloud preset by setting each named control through the registry (→ CloudVolume).
    /// Controls not in the preset are left as-is. jitter>0 = seeded ± around each value (surprise-me).
    public void Apply(int idx, float jitter = 0f)
    {
        if (idx < 0 || idx >= _presets.Count) { return; }
        // CO-4: reset the cloud-TYPE levers first so every preset is SELF-CONTAINED — a preset that doesn't
        // mention cirrus/stratus/profile/macro gets the clean cumulus look, not leftover state from a prior pick.
        foreach (var id in new[] { "cloud_profile_on", "cloud_cirrus_on" })
            if (_reg.TryGet(id, out var bc)) { _reg.SetValue(bc, false); }
        foreach (var id in new[] { "cloud_shape_mode", "cloud_anti_repeat" })
            if (_reg.TryGet(id, out var fc)) { _reg.SetValue(fc, 0.0f); }
        var values = _presets[idx].values;
        foreach (var key in values.Keys)
        {
            string id = key.AsString();
            if (!_reg.TryGet(id, out LabControl c)) { continue; }
            if (jitter > 0f && c.Locked) { continue; }   // randomize respects locks; explicit pick doesn't
            float v = values[key].AsSingle();
            // ranged presets / surprise-me (roadmap #3): seeded ± jitter of the knob's range, CENTERED on a
            // known-good preset value → always coherent. Respects the rand flag (so e.g. shadow_strength stays).
            if (jitter > 0f && c.Rand)
            {
                v = Mathf.Clamp(v + (float)(_rng.NextDouble() * 2.0 - 1.0) * jitter * (c.Max - c.Min), c.Min, c.Max);
            }
            _reg.SetValue(c, v);
        }
        // roadmap #2: the preset's deck stack (or the default stack when it authors none), applied AFTER the
        // knobs so layer 0 picks up this preset's coverage/density via PackLayers.
        var layers = _presets[idx].layers;
        _getCloud()?.SetLayers(layers != null ? CloudLayers.FromGodotArray(layers) : CloudLayers.Load());
        GD.Print($"Clouds: applied preset '{_presets[idx].name}'" + (jitter > 0f ? " (jittered)" : ""));
    }
}
