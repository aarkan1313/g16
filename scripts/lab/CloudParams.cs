using Godot;
using System.Text.Json;

namespace WG16.Lab;

/// Cloud system knobs. Split by concern:
///  - LOOK knobs (coverage, density, type, lighting, drift) are pushed live from
///    the lab registry straight to the sky-shader uniforms — they live here only as
///    DEFAULTS so the system has a sane starting look before the UI touches it.
///  - PERF knobs (raymarch steps, update resolution, temporal frames) are read by
///    the C# dispatch (CloudVolume), so they must round-trip through C#.
/// Loaded from data/cloud_params.json if present; otherwise these defaults. Pure
/// data — knows nothing about how the values are consumed.
public record CloudParams(
    // look (defaults; UI overrides live)
    float Coverage,        // 0..1 — how much sky is clouded. LOW default = sparse.
    float Density,         // overall optical density (darkness/opacity)
    float CloudType,       // 0 stratus (flat) → 1 cumulus (towering)
    float AltitudeM,       // cloud-base height above the terrain
    float ThicknessM,      // vertical extent of the cloud layer
    float DriftSpeed,      // m/s scroll of the weather field
    float DriftDirDeg,     // drift heading
    float HgAniso,         // Henyey-Greenstein forward-scatter (silver lining)
    float Powder,          // powder/dark-edge term strength
    float SunAbsorption,   // Beer absorption toward the sun
    // shape / size / opacity
    float Size,            // overall feature size (×)
    float Detail,          // high-freq edge erosion amount
    float DetailSize,      // size of the wispy detail (×)
    float Edge,            // shape hardness (0 soft ↔ 1 crisp)
    float Opacity,         // extinction (translucent ↔ solid)
    float Brightness,      // overall cloud lightness
    float Ambient,         // sky-fill on shadowed sides
    // perf (read by CloudVolume)
    int RaymarchSteps,     // view-ray steps through the cloud shell
    float UpdateResScale,  // raymarch target res as a fraction of viewport (0..1)
    int TemporalFrames)    // frames to spread a full update over
{
    public const string Path = "res://data/cloud_params.json";

    public static CloudParams Defaults() => new(
        Coverage: 0.35f,        // sparse — the user's note: occasional patches, not a blanket
        Density: 1.0f,
        CloudType: 0.6f,        // mostly cumulus
        AltitudeM: 1800f,
        ThicknessM: 1400f,
        DriftSpeed: 12f,
        DriftDirDeg: 45f,
        HgAniso: 0.6f,
        Powder: 1.0f,
        SunAbsorption: 0.75f,
        Size: 1.0f,
        Detail: 0.4f,
        DetailSize: 1.0f,
        Edge: 0.5f,
        Opacity: 1.0f,
        Brightness: 1.0f,
        Ambient: 0.7f,
        RaymarchSteps: 96,
        UpdateResScale: 1.0f,   // Stage 3 is full-res; Stage 4 lowers this
        TemporalFrames: 1);     // Stage 3 updates every frame; Stage 4 raises this

    public static CloudParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        if (!System.IO.File.Exists(abs)) { return Defaults(); }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        CloudParams d = Defaults();
        float F(string k, float def) => r.TryGetProperty(k, out var e) ? e.GetSingle() : def;
        int I(string k, int def) => r.TryGetProperty(k, out var e) ? e.GetInt32() : def;
        return new CloudParams(
            F("coverage", d.Coverage), F("density", d.Density), F("cloud_type", d.CloudType),
            F("altitude_m", d.AltitudeM), F("thickness_m", d.ThicknessM),
            F("drift_speed", d.DriftSpeed), F("drift_dir_deg", d.DriftDirDeg),
            F("hg_aniso", d.HgAniso), F("powder", d.Powder), F("sun_absorption", d.SunAbsorption),
            F("size", d.Size), F("detail", d.Detail), F("detail_size", d.DetailSize),
            F("edge", d.Edge), F("opacity", d.Opacity), F("brightness", d.Brightness), F("ambient", d.Ambient),
            I("raymarch_steps", d.RaymarchSteps), F("update_res_scale", d.UpdateResScale),
            I("temporal_frames", d.TemporalFrames));
    }
}
