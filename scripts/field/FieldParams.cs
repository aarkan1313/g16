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

    // Graceful getters: a missing/renamed key falls back to the default instead of throwing a
    // KeyNotFoundException (which aborts the whole load + boot). Matches the other loaders'
    // tolerance (CloudParams/LightingState use defaults), so editing field_params.json — or a key
    // rename mid-refactor — degrades to the default for that one knob rather than killing the scene.
    private static float F(JsonElement r, string k, float d) =>
        r.TryGetProperty(k, out var v) ? v.GetSingle() : d;
    private static int I(JsonElement r, string k, int d) =>
        r.TryGetProperty(k, out var v) ? v.GetInt32() : d;
    private static uint U(JsonElement r, string k, uint d) =>
        r.TryGetProperty(k, out var v) ? v.GetUInt32() : d;

    public static FieldParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        // Defaults below are the WG15 proven base-field values (the committed field_params.json) — a
        // present key always wins; the default only catches an absent/renamed one.
        return new FieldParams(
            U(r, "seed", 1234u),
            F(r, "region_size_m", 8192.0f),
            I(r, "heightmap_res", 2048),
            F(r, "base_freq", 0.0011f),
            U(r, "octaves", 6u),
            F(r, "lacunarity", 2.0f),
            F(r, "gain", 0.5f),
            F(r, "amplitude_m", 240.0f),
            F(r, "cont_freq", 0.00026f),
            F(r, "cont_weight", 0.55f),
            F(r, "uplift_freq", 0.00022f),
            F(r, "uplift_weight", 0.78f),
            F(r, "uplift_lo", 0.20f),
            F(r, "uplift_hi", 0.82f),
            F(r, "macro_pivot", 0.48f),
            F(r, "macro_amp", 650.0f),
            F(r, "hill_damp", 2.0f),
            F(r, "ridge_freq", 0.00032f),
            F(r, "ridge_amp", 180.0f),
            F(r, "mtn_lo", 0.60f),
            F(r, "mtn_hi", 0.95f),
            F(r, "grain_stretch", 1.8f),
            U(r, "cont_octaves", 5u),
            F(r, "cont_warp", 0.18f),
            F(r, "uplift_warp", 0.30f),
            F(r, "massif_freq", 0.0005f),
            F(r, "massif_floor", 0.10f),
            F(r, "foothill_w", 2.4f),
            F(r, "foothill_h", 0.28f));
    }

    public static ulong ModifiedTime() => FileAccess.GetModifiedTime(Path);
}
