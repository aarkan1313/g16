using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// One cloud deck. Data only — no marching/scene knowledge (separation of concerns).
/// Fields 0-11 are DENSITY; fields 19-21 are the VERTICAL PROFILE (CO-1) and are
/// also density-affecting. PhaseG..TintB are per-deck LIGHTING so cumulus vs cirrus
/// read as different cloud kinds (roadmap #1).
public readonly record struct CloudLayer(
    float Altitude, float Thickness, float Size, float CellScale,
    float CoverageWeight, float Density, float Opacity, float Type,
    float Edge, float Detail, float DetailSize,
    float PhaseG, float PhaseIso, float Albedo, float SunAbsorb,
    float TintR, float TintG, float TintB,
    float ProfileBottom, float ProfileTop, float Anvil, float ShapeMode, float AntiRepeat,
    int NoiseId, bool Enabled);

/// Owns the cloud-layer array: load/validate from JSON, pack into a float[] run for the
/// GPU param buffer. One job. The march consumes the packed buffer; presets/weather write
/// the layers. Layer 0 mirrors the legacy flat knobs (set by CloudVolume at runtime).
public static class CloudLayers
{
    public const int MaxLayers = 8;
    public const int Stride = 24;   // floats per layer: 12 density (0-11) + 7 lighting (12-18) + 3 profile (19-21) + shape-mode (22) + anti-repeat (23) = 6 vec4. See Pack.
    public const string Path = "res://data/cloud_layers.json";

    // Build one CloudLayer from key→value accessors. The ONLY place layer field names +
    // defaults live, so the file loader (Load) and the preset loader (FromGodotArray) can't
    // drift in schema. Separation of concerns: callers supply how to read a key; we own what
    // the keys ARE and their defaults.
    private static CloudLayer Build(System.Func<string, float, float> F, System.Func<string, int, int> I, System.Func<string, bool, bool> B)
        => new CloudLayer(
            F("altitude", 1800f), F("thickness", 1400f), F("size", 1f), F("cell_scale", 1.6f),
            F("coverage_weight", 1f), F("density", 1f), F("opacity", 1f), F("type", 0.6f),
            F("edge", 0.62f), F("detail", 0.56f), F("detail_size", 0.85f),
            F("phase_g", 0.8f), F("phase_iso", 0.2f), F("albedo", 1f), F("sun_absorb", 1f),
            F("tint_r", 1f), F("tint_g", 1f), F("tint_b", 1f),
            F("profile_bottom", 0f), F("profile_top", 1f), F("anvil", 0f), F("shape_mode", 0f), F("anti_repeat", 0f),
            I("noise_id", 0), B("enabled", true));

    /// The default deck stack from data/cloud_layers.json (System.Text.Json).
    public static List<CloudLayer> Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        if (!System.IO.File.Exists(abs)) { return new List<CloudLayer>(); }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        var list = new List<CloudLayer>();
        if (doc.RootElement.TryGetProperty("layers", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= MaxLayers) { break; }
                var el = e;
                list.Add(Build(
                    (k, d) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : d,
                    (k, d) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d,
                    (k, d) => el.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.True) : d));
            }
        }
        return list;
    }

    /// A deck stack authored inside a cloud PRESET (roadmap #2). Parsed from the Godot Variant
    /// array the preset loader already holds — keeps ALL layer-schema knowledge in this unit.
    public static List<CloudLayer> FromGodotArray(Godot.Collections.Array arr)
    {
        var list = new List<CloudLayer>();
        foreach (Godot.Variant v in arr)
        {
            if (list.Count >= MaxLayers) { break; }
            var e = v.AsGodotDictionary();
            list.Add(Build(
                (k, d) => e.ContainsKey(k) ? (float)e[k] : d,
                (k, d) => e.ContainsKey(k) ? (int)e[k] : d,
                (k, d) => e.ContainsKey(k) ? (bool)e[k] : d));
        }
        return list;
    }

    /// Layer 0 is the knob-driven cumulus deck: its per-deck LIGHTING is fixed here (one
    /// source of truth) so the renderer (CloudVolume.PackLayers) and the diagnostic
    /// (CloudLightCheck) can't drift. Cumulus = bold/bright sunlit edges (high albedo) with
    /// dark self-shadowed cores (high sun_absorb) → high-contrast 3D form; faint warm tint.
    public static CloudLayer WithCumulusLighting(CloudLayer l) => l with {
        PhaseG = 0.85f, PhaseIso = 0.12f, Albedo = 1.7f, SunAbsorb = 1.3f,
        TintR = 1.0f, TintG = 0.99f, TintB = 0.95f };

    /// Pack enabled layers into Stride floats each. Returns the float[] and active count.
    public static float[] Pack(List<CloudLayer> layers, out int count)
    {
        var packed = new float[MaxLayers * Stride];
        count = 0;
        foreach (var L in layers)
        {
            if (!L.Enabled || count >= MaxLayers) { continue; }
            int o = count * Stride;
            packed[o + 0] = L.Altitude;       packed[o + 1] = L.Thickness;
            packed[o + 2] = L.Size;           packed[o + 3] = L.CellScale;
            packed[o + 4] = L.CoverageWeight; packed[o + 5] = L.Density;
            packed[o + 6] = L.Opacity;        packed[o + 7] = L.Type;
            packed[o + 8] = L.Edge;           packed[o + 9] = L.Detail;
            packed[o + 10] = L.DetailSize;    packed[o + 11] = L.NoiseId;
            // 12-18: per-deck LIGHTING (raymarch only)
            packed[o + 12] = L.PhaseG;        packed[o + 13] = L.PhaseIso;
            packed[o + 14] = L.Albedo;        packed[o + 15] = L.SunAbsorb;
            packed[o + 16] = L.TintR;         packed[o + 17] = L.TintG;
            packed[o + 18] = L.TintB;
            // 19-21: VERTICAL PROFILE (CO-1) — density-affecting, read by BOTH shaders
            packed[o + 19] = L.ProfileBottom; packed[o + 20] = L.ProfileTop;
            packed[o + 21] = L.Anvil;         packed[o + 22] = L.ShapeMode;   // 22 = stratus shape-mode (CO-2)
            packed[o + 23] = L.AntiRepeat;    // 23 = macro-variety / anti-repetition strength (CO-3)
            count++;
        }
        return packed;
    }
}
