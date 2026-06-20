using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Pure-data lighting state for the decoupled Sun & Light system (Stage 2). The final look is the
/// composition of three orthogonal axes (Time × Weather × Grade) — see
/// docs/superpowers/specs/2026-06-20-sun-light-system-architecture.md. These structs hold NO Godot
/// scene state and do NO scene writes; LightingComposer is the only writer. Loaded from
/// data/{time,weather,grade}_presets.json via the project's Godot.Json pattern.

/// TIME axis: the sun's day arc. Position (elev+az) is analytic from the arc params (driven by
/// TimeOfDay in Task 4). The color/sky/ambient fields below are TRANSITIONAL — they preserve the
/// pre-atmosphere mood look during the refactor (Tasks 2–4); the GPU-compute atmosphere OVERRIDES
/// SunColor/SunEnergy/Sky*/Ambient* in Task 6. `TimeOfDay`/`*H` in hours, elevations/azimuths in degrees.
public sealed class TimeState
{
    public string Name = "";
    public float TimeOfDay = 12f;
    public float SunriseH = 6f, SunsetH = 18f;   // daylight window (hours)
    public float PeakElev = 60f;                 // sun elevation at solar noon (degrees)
    public float AzStart = 90f, AzEnd = 270f;    // sunrise→sunset compass azimuth sweep (E→W)
    // transitional sun + sky + ambient look (atmosphere-driven from Task 6):
    public float SunAngle = 35f, SunAzimuth = 40f, SunEnergy = 1.3f;
    public Color SunColor = new(1f, 0.95f, 0.86f);
    public float Ambient = 0.4f, AmbientSky = 1.0f;
    public Color SkyTop = new(0.30f, 0.48f, 0.74f), SkyHorizon = new(0.68f, 0.74f, 0.80f), SkyGround = new(0.22f, 0.26f, 0.22f);

    public TimeState Clone() => (TimeState)MemberwiseClone();
}

/// One anchor in the daytime COLOR SCRIPT (Task 4): the sky/sun-color/ambient look at a given hour.
/// time_of_day interpolates between the two bracketing anchors → a cohesive day without an atmosphere
/// model (the GPU atmosphere is a separate future stage). Authored in data/time_presets.json "day_script".
public sealed class TimeKey
{
    public float Hour = 12f, SunEnergy = 1.4f;
    public Color SunColor = new(1f, 0.95f, 0.86f);
    public Color SkyTop = new(0.30f, 0.48f, 0.74f), SkyHorizon = new(0.68f, 0.74f, 0.80f), SkyGround = new(0.22f, 0.26f, 0.22f);
    public float Ambient = 0.45f, AmbientSky = 0.95f;
}

/// CELESTIAL sun appearance (Stage-1 disc + shadow softness). Orthogonal to the Time physics; bound to
/// the cloud_sky.gdshader sun + the DirectionalLight shadow. Becomes part of the Celestial layer (Stage 3).
public sealed class SunDiscState
{
    public string Name = "";
    public float ShadowSoft = 1.0f, DiscAngular = 0.6f;   // sun.ShadowBlur, sun.LightAngularDistance (PCSS penumbra)
    public float Size = 0.6f, Limb = 0.55f;
    public float CoronaSize = 1200f, CoronaEnergy = 2.0f, HaloSize = 90f, HaloEnergy = 0.4f;
    public float Redden = 1.0f, ReddenOnset = 0.25f, HorizonGrow = 0.6f, CloudRedden = 0.8f;

    public SunDiscState Clone() => (SunDiscState)MemberwiseClone();
}

/// WEATHER axis: clouds (by preset id) + depth fog. Re-homes the existing cloud/fog systems unchanged.
public sealed class WeatherState
{
    public string Name = "";
    public string CloudPreset = "";              // cloud_presets.json entry name ("" = leave current)
    public Color FogColor = new(0.74f, 0.81f, 0.90f);
    public float FogDensity = 0.0006f, FogAerial = 0.85f, FogHeight = -200f, FogHeightD = 0.035f, FogSunScatter = 0.25f;

    public WeatherState Clone() => (WeatherState)MemberwiseClone();
}

/// GRADE axis: the artistic treatment (tonemap + adjustments + bloom). Independent of time + weather.
public sealed class GradeState
{
    public string Name = "";
    public float Exposure = 1.0f, White = 6.0f, Glow = 0.3f;
    public float Contrast = 1.08f, Saturation = 1.12f, Brightness = 1.0f;
    public Color Tint = new(1f, 1f, 1f);

    public GradeState Clone() => (GradeState)MemberwiseClone();
}

/// Loads the three preset lists from data/*.json (Godot.Json, same pattern as LoadMoods).
public static class LightingPresets
{
    public static readonly List<TimeState> Time = new();
    public static readonly List<WeatherState> Weather = new();
    public static readonly List<GradeState> Grade = new();
    public static readonly List<TimeKey> DayScript = new();   // the daytime color script (anchors by hour)

    public static void Load()
    {
        Time.Clear(); Weather.Clear(); Grade.Clear(); DayScript.Clear();
        foreach (var d in ReadList("res://data/time_presets.json", "time")) { Time.Add(ParseTime(d)); }
        foreach (var d in ReadList("res://data/weather_presets.json", "weather")) { Weather.Add(ParseWeather(d)); }
        foreach (var d in ReadList("res://data/grade_presets.json", "grade")) { Grade.Add(ParseGrade(d)); }
        foreach (var d in ReadList("res://data/time_presets.json", "day_script")) { DayScript.Add(ParseKey(d)); }
        DayScript.Sort((a, b) => a.Hour.CompareTo(b.Hour));
    }

    private static TimeKey ParseKey(Godot.Collections.Dictionary d) => new()
    {
        Hour = F(d, "hour", 12f), SunEnergy = F(d, "sun_energy", 1.4f),
        SunColor = C(d, "sun_color", new Color(1f, 0.95f, 0.86f)),
        SkyTop = C(d, "sky_top", new Color(0.30f, 0.48f, 0.74f)),
        SkyHorizon = C(d, "sky_horizon", new Color(0.68f, 0.74f, 0.80f)),
        SkyGround = C(d, "sky_ground", new Color(0.22f, 0.26f, 0.22f)),
        Ambient = F(d, "ambient", 0.45f), AmbientSky = F(d, "ambient_sky", 0.95f),
    };

    private static List<Godot.Collections.Dictionary> ReadList(string resPath, string key)
    {
        var outList = new List<Godot.Collections.Dictionary>();
        string abs = ProjectSettings.GlobalizePath(resPath);
        if (!System.IO.File.Exists(abs)) { return outList; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return outList; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey(key)) { return outList; }
        foreach (Variant v in root[key].AsGodotArray()) { outList.Add(v.AsGodotDictionary()); }
        return outList;
    }

    private static float F(Godot.Collections.Dictionary d, string k, float def) => d.ContainsKey(k) ? (float)d[k].AsDouble() : def;
    private static string S(Godot.Collections.Dictionary d, string k) => d.ContainsKey(k) ? d[k].AsString() : "";
    private static Color C(Godot.Collections.Dictionary d, string k, Color def)
    {
        if (!d.ContainsKey(k)) { return def; }
        var a = d[k].AsGodotArray();
        return a.Count >= 3 ? new Color((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble()) : def;
    }

    private static TimeState ParseTime(Godot.Collections.Dictionary d) => new()
    {
        Name = S(d, "name"), TimeOfDay = F(d, "time_of_day", 12f),
        SunriseH = F(d, "sunrise", 6f), SunsetH = F(d, "sunset", 18f), PeakElev = F(d, "peak_elev", 60f),
        AzStart = F(d, "az_start", 90f), AzEnd = F(d, "az_end", 270f),
    };

    private static WeatherState ParseWeather(Godot.Collections.Dictionary d) => new()
    {
        Name = S(d, "name"), CloudPreset = S(d, "cloud_preset"),
        FogColor = C(d, "fog_color", new Color(0.74f, 0.81f, 0.90f)),
        FogDensity = F(d, "fog_density", 0.0006f), FogAerial = F(d, "fog_aerial", 0.85f),
        FogHeight = F(d, "fog_height", -200f), FogHeightD = F(d, "fog_heightd", 0.035f), FogSunScatter = F(d, "fog_sun_scatter", 0.25f),
    };

    private static GradeState ParseGrade(Godot.Collections.Dictionary d) => new()
    {
        Name = S(d, "name"), Exposure = F(d, "exposure", 1.0f), White = F(d, "white", 6.0f), Glow = F(d, "glow", 0.3f),
        Contrast = F(d, "contrast", 1.08f), Saturation = F(d, "saturation", 1.12f), Brightness = F(d, "brightness", 1.0f),
        Tint = C(d, "tint", new Color(1f, 1f, 1f)),
    };
}
