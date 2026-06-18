using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// One cloud deck. Data only — no marching/scene knowledge (separation of concerns).
public readonly record struct CloudLayer(
    float Altitude, float Thickness, float Size, float CellScale,
    float CoverageWeight, float Density, float Opacity, float Type,
    float Edge, float Detail, float DetailSize, int NoiseId, bool Enabled);

/// Owns the cloud-layer array: load/validate from JSON, pack into a float[] run for the
/// GPU param buffer. One job. The march consumes the packed buffer; presets/weather write
/// the layers. Layer 0 mirrors the legacy flat knobs (set by CloudVolume at runtime).
public static class CloudLayers
{
    public const int MaxLayers = 8;
    public const int Stride = 12;   // floats per layer in the packed buffer (see Pack)
    public const string Path = "res://data/cloud_layers.json";

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
                float F(string k, float d) => e.TryGetProperty(k, out var v) ? v.GetSingle() : d;
                int I(string k, int d) => e.TryGetProperty(k, out var v) ? v.GetInt32() : d;
                bool B(string k, bool d) => e.TryGetProperty(k, out var v) ? v.GetBoolean() : d;
                list.Add(new CloudLayer(
                    F("altitude", 1800f), F("thickness", 1400f), F("size", 1f), F("cell_scale", 1.6f),
                    F("coverage_weight", 1f), F("density", 1f), F("opacity", 1f), F("type", 0.6f),
                    F("edge", 0.5f), F("detail", 0.4f), F("detail_size", 1f), I("noise_id", 0), B("enabled", true)));
            }
        }
        return list;
    }

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
            count++;
        }
        return packed;
    }
}
