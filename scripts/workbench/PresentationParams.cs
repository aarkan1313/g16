using Godot;
using System.Text.Json;

namespace WG16.Workbench;

/// Presentation knobs (Presenter/Workbench side — SEPARATE from FieldParams so
/// the field stays stable while lighting/walk are tuned). Hot-reloaded like
/// field params. M1 set: sun/fog/env + walk.
public record PresentationParams(
    float SunElevationDeg,
    float SunAzimuthDeg,
    float SunEnergy,
    float FogDensity,
    float EyeHeightM,
    float WalkSpeedMs,
    float WalkRunMult)
{
    public const string Path = "res://data/presentation_params.json";

    public static PresentationParams Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement r = doc.RootElement;
        return new PresentationParams(
            r.GetProperty("sun_elevation_deg").GetSingle(),
            r.GetProperty("sun_azimuth_deg").GetSingle(),
            r.GetProperty("sun_energy").GetSingle(),
            r.GetProperty("fog_density").GetSingle(),
            r.GetProperty("eye_height_m").GetSingle(),
            r.GetProperty("walk_speed_ms").GetSingle(),
            r.GetProperty("walk_run_mult").GetSingle());
    }

    public static ulong ModifiedTime() => FileAccess.GetModifiedTime(Path);
}
