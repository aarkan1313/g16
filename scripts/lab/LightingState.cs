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
    // NIGHT (Stage 3a): the night half of the 24 h arc. Defaults = physical (mirror dip, near-black floor).
    public float NightNadir = -60f;        // sun elevation at solar midnight (deg, negative). default mirrors PeakElev.
    public float NightDarkness = 1.0f;     // scales night sky+ambient: 1 = authored, <1 dark-scary, >1 moonlit-bright
    public float NightAmbientFloor = 0.02f;// minimum ambient at deep night (≈0 = scary; raise for moonlit)
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

/// CELESTIAL — the MOON (Stage 3b). Pure data; orthogonal to the Time physics. Default position is
/// anti-solar (up at night) with tunable elev/az offsets (decoupled, per the tunable ethos). The disc
/// reuses the sun-surface system (sun_fbm) for maria/craters; `Phase` drives a terminator across the disc.
public sealed class MoonState
{
    public string Name = "";
    public float Phase = 1.0f;            // 0 = new (dark) · 0.5 = half · 1 = full
    // The moon follows its OWN arc (not the sun's). AzOffset ROTATES the moon's azimuth sweep off the sun's
    // (degrees), DeclScale sets its peak-elevation as a fraction of the sun's (own declination), ElevOffset
    // shifts it up/down — so its path + landing differ from the sun's even at full phase. Defaults = a distinct arc.
    public float ElevOffset = 0f, AzOffset = 35f;
    public float DeclScale = 0.72f;       // moon arc peak height as a fraction of the sun's peak elevation
    public float Size = 1.2f, Limb = 0.6f;         // angular radius (deg) + limb darkening
    public float DiscEnergy = 0.9f;                // overall brightness (cool, dimmer than the sun)
    public float HaloSize = 140f, HaloEnergy = 0.2f;
    public float SurfCells = 8f, SurfContrast = 0.6f, SurfSpots = 0.55f, SurfChurn = 0f;  // maria/crater mottle
    public Color Color = new(0.85f, 0.88f, 1.0f);  // cool moonlight white
    // MOONLIGHT (Stage 3c): a 2nd cool directional cast on the terrain, gated to night × moon-up × phase.
    public float LightEnergy = 0.5f;
    public Color LightColor = new(0.60f, 0.70f, 1.0f);  // cool blue moonlight

    public MoonState Clone() => (MoonState)MemberwiseClone();
}

/// CELESTIAL — STARS + NIGHT SKY (Stage 3d / Celestial C1). Pure data; procedural galaxy + nebulae baked
/// (night_sky_bake.glsl) + live starfield, rendered in cloud_sky.gdshader, faded in at night. Fantasy,
/// tunable — no real constellations (user's call). MwBrightness/MwWidth/MwTilt = the galaxy brightness/
/// width/tilt (control ids kept mw_* so celestial_presets.json still routes).
public sealed class StarsState
{
    // Live starfield (not baked).
    public float Brightness = 1.0f, Density = 0.5f, Twinkle = 0.5f, Rotation = 0.003f;
    // Galaxy (baked). MwBrightness is LIVE (a shader multiplier); the rest re-bake on change.
    public float MwBrightness = 0.8f, MwWidth = 0.11f, MwTilt = 0.6f;   // brightness/width up so the galaxy band actually reads
    public float CoreAz = 1.26f, CoreElev = 0.5f;          // core direction az/elev (rad); elev ~29° = galaxy sits up in the sky
    public float CoreSize = 0.5f, Curve = 0.0f, Dust = 0.5f;
    public Color CoreColor = new(0.95f, 0.75f, 0.55f);
    public Color ArmColor = new(0.45f, 0.55f, 0.85f);
    // Nebulae (baked). Count + a global density + the two lead colors are tunable; directions/scales are
    // fixed demo positions (preset territory) so the Night tab stays manageable.
    public int NebCount = 0;   // nebulae killed (user 2026-06-21) — galaxy + starfield only; tunable back up if wanted
    public float NebDensity = 0.55f;
    public Color Neb1Color = new(0.18f, 0.55f, 0.65f);     // teal
    public Color Neb2Color = new(0.65f, 0.22f, 0.6f);      // magenta
    public StarsState Clone() => (StarsState)MemberwiseClone();
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
