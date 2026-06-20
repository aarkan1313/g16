using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Pure-data lighting state for the decoupled Sun & Light system (Stage 2). The final look is the
/// composition of three orthogonal axes (Time × Weather × Grade) — see
/// docs/superpowers/specs/2026-06-20-sun-light-system-architecture.md. These structs hold NO Godot
/// scene state and do NO scene writes; LightingComposer is the only writer. Loaded from
/// data/{time,weather,grade}_presets.json via the project's Godot.Json pattern.

/// TIME axis: the sun's day arc (position is analytic; the sky/sun-color/ambient come from the
/// GPU-compute atmosphere, NOT stored colors — added in Task 5). `TimeOfDay` in hours.
public sealed class TimeState
{
    public string Name = "";
    public float TimeOfDay = 12f;
    public float SunriseH = 6f, SunsetH = 18f;   // daylight window (hours)
    public float PeakElev = 60f;                 // sun elevation at solar noon (degrees)
    public float AzStart = 90f, AzEnd = 270f;    // sunrise→sunset compass azimuth sweep (E→W)

    public TimeState Clone() => (TimeState)MemberwiseClone();
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

    public static void Load()
    {
        Time.Clear(); Weather.Clear(); Grade.Clear();
        foreach (var d in ReadList("res://data/time_presets.json", "time")) { Time.Add(ParseTime(d)); }
        foreach (var d in ReadList("res://data/weather_presets.json", "weather")) { Weather.Add(ParseWeather(d)); }
        foreach (var d in ReadList("res://data/grade_presets.json", "grade")) { Grade.Add(ParseGrade(d)); }
    }

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
