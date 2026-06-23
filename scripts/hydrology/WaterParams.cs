using Godot;
using System.Text.Json;
namespace WG16.Hydrology;

/// All hydrology knobs, loaded from data/water_params.json with graceful key fallback (mirrors
/// WG16.Field.FieldParams). A missing/renamed key degrades to its default rather than throwing.
public record WaterParams(
    int CoarseRes, int HaloRegions, float RiverAccumThreshold, float ChannelWidthScale,
    float CarveWidthM, float CarveDepthScale, float CarveProfileExp, float BedDepthM,
    float WaterTableFreq, float LakeTableThreshold, float LakeMinAreaM2, float LakeMinDepthM,
    float LakeMinInflow, float LakeDensityPerKm2, float FlowSpeed, float WaveScale, float FoamWidthM,
    Color WaterShallowColor, Color WaterDeepColor)
{
    public const string Path = "res://data/water_params.json";

    private static float F(JsonElement r, string k, float d) => r.TryGetProperty(k, out var v) ? v.GetSingle() : d;
    private static int I(JsonElement r, string k, int d) => r.TryGetProperty(k, out var v) ? v.GetInt32() : d;
    private static Color C(JsonElement r, string k, Color d)
    {
        if (!r.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return d;
        return new Color(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
    }

    public static WaterParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        return new WaterParams(
            I(r, "coarse_res", 256), I(r, "halo_regions", 1),
            F(r, "river_accum_threshold", 60f), F(r, "channel_width_scale", 1.4f),
            F(r, "carve_width_m", 36f), F(r, "carve_depth_scale", 1f), F(r, "carve_profile_exp", 2f),
            F(r, "bed_depth_m", 7f), F(r, "water_table_freq", 0.0002f), F(r, "lake_table_threshold", 0.55f),
            F(r, "lake_min_area_m2", 40000f), F(r, "lake_min_depth_m", 4f), F(r, "lake_min_inflow", 30f),
            F(r, "lake_density_per_km2", 0.4f), F(r, "flow_speed", 0.15f), F(r, "wave_scale", 1f),
            F(r, "foam_width_m", 6f),
            C(r, "water_shallow_color", new Color(0.16f, 0.42f, 0.52f)),
            C(r, "water_deep_color", new Color(0.03f, 0.13f, 0.26f)));
    }
}
