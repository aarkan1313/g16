using Godot;
using System.Text.Json;

namespace WG16.Field;

/// All field knobs, loaded from data/field_params.json. These are the WG15
/// proven base-field knobs (continent / uplift / hills / ridges / macro base);
/// the erosion / water / skeleton / bake blocks are gone — this project is the
/// base field with no bake stage. Spacing is derived and passed per call.
public record FieldParams(
    uint Seed,
    float RegionSizeM,
    int HeightmapRes,
    float BaseFreq,
    uint Octaves,
    float Lacunarity,
    float Gain,
    float AmplitudeM,
    float ContFreq,
    float ContWeight,
    float UpliftFreq,
    float UpliftWeight,
    float UpliftLo,
    float UpliftHi,
    float MacroPivot,
    float MacroAmp,
    float HillDamp,
    float RidgeFreq,
    float RidgeAmp,
    float MtnLo,
    float MtnHi,
    float GrainStretch,
    uint ContOctaves,
    float ContWarp,
    float UpliftWarp,
    float MassifFreq,
    float MassifFloor,
    float FoothillW,
    float FoothillH)
{
    public const string Path = "res://data/field_params.json";

    public float Spacing => RegionSizeM / HeightmapRes;

    public static FieldParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        return new FieldParams(
            r.GetProperty("seed").GetUInt32(),
            r.GetProperty("region_size_m").GetSingle(),
            r.GetProperty("heightmap_res").GetInt32(),
            r.GetProperty("base_freq").GetSingle(),
            r.GetProperty("octaves").GetUInt32(),
            r.GetProperty("lacunarity").GetSingle(),
            r.GetProperty("gain").GetSingle(),
            r.GetProperty("amplitude_m").GetSingle(),
            r.GetProperty("cont_freq").GetSingle(),
            r.GetProperty("cont_weight").GetSingle(),
            r.GetProperty("uplift_freq").GetSingle(),
            r.GetProperty("uplift_weight").GetSingle(),
            r.GetProperty("uplift_lo").GetSingle(),
            r.GetProperty("uplift_hi").GetSingle(),
            r.GetProperty("macro_pivot").GetSingle(),
            r.GetProperty("macro_amp").GetSingle(),
            r.GetProperty("hill_damp").GetSingle(),
            r.GetProperty("ridge_freq").GetSingle(),
            r.GetProperty("ridge_amp").GetSingle(),
            r.GetProperty("mtn_lo").GetSingle(),
            r.GetProperty("mtn_hi").GetSingle(),
            r.GetProperty("grain_stretch").GetSingle(),
            r.GetProperty("cont_octaves").GetUInt32(),
            r.GetProperty("cont_warp").GetSingle(),
            r.GetProperty("uplift_warp").GetSingle(),
            r.GetProperty("massif_freq").GetSingle(),
            r.GetProperty("massif_floor").GetSingle(),
            r.GetProperty("foothill_w").GetSingle(),
            r.GetProperty("foothill_h").GetSingle());
    }

    public static ulong ModifiedTime() => FileAccess.GetModifiedTime(Path);
}
