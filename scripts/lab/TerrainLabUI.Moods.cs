using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    private const int DefaultMoodIdx = 5;   // "Clear Alpine" — clean neutral good-day look
    private void ApplyDefaultMood()
    {
        int idx = Mathf.Clamp(DefaultMoodIdx, 0, _moods.Count - 1);
        ApplyMood(idx);
        if (_moodPick != null) { _moodPick.Select(idx); }   // reflect it in the dropdown
    }

    // ---- lighting MOODS (curated, coordinated looks) --------------------------

    private readonly List<string> _moodNames = new();
    private Godot.Collections.Array _moods = new();

    private void LoadMoods()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/lighting_moods.json");
        if (!System.IO.File.Exists(abs)) { return; }
        Variant parsed = Json.ParseString(System.IO.File.ReadAllText(abs));
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("moods")) { return; }
        _moods = root["moods"].AsGodotArray();
        foreach (Variant m in _moods) { _moodNames.Add(m.AsGodotDictionary()["name"].AsString()); }
    }

    private static Color Col(Variant v) { var a = v.AsGodotArray(); return new Color(a[0].AsSingle(), a[1].AsSingle(), a[2].AsSingle()); }
    private static float F(Godot.Collections.Dictionary d, string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;

    /// Apply a complete coordinated mood: sun, sky, fog, ambient, exposure, glow.
    private int _currentMood = -1;
    private void ApplyMood(int idx)
    {
        if (idx < 0 || idx >= _moods.Count) { return; }
        _currentMood = idx;
        var m = _moods[idx].AsGodotDictionary();
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");

        _sunAngle = F(m, "sun_angle", 35f); _sunAzimuth = F(m, "sun_az", 40f); OrientSun(sun);
        sun.LightEnergy = F(m, "sun_energy", 1.3f);
        if (m.ContainsKey("sun_color")) { sun.LightColor = Col(m["sun_color"]); }
        // Soft shadows via ShadowBlur ONLY. LightAngularDistance also enlarges the
        // sky sun DISC + spreads its energy (that was the "too big & bright" sun), so
        // keep it tiny and let blur do the softening — decoupled.
        float soft = F(m, "shadow_soft", 1.0f);
        sun.ShadowBlur = soft;
        sun.LightAngularDistance = F(m, "sun_disc", 0.6f);   // mood baseline; live slider can override

        env.AmbientLightEnergy = F(m, "ambient", 0.4f);
        env.AmbientLightSkyContribution = F(m, "ambient_sky", 1.0f);
        // remember the mood's BASE lighting so overcast dimming scales from it (not
        // compounding frame-to-frame). Sun energy base captured after it's set below.
        _baseAmbient = env.AmbientLightEnergy;
        _baseSunEnergy = F(m, "sun_energy", 1.3f);
        if (env.Sky?.SkyMaterial is ProceduralSkyMaterial sky)
        {
            if (m.ContainsKey("sky_top")) { sky.SkyTopColor = Col(m["sky_top"]); }
            if (m.ContainsKey("sky_horizon")) { sky.SkyHorizonColor = Col(m["sky_horizon"]); sky.GroundHorizonColor = Col(m["sky_horizon"]); }
            if (m.ContainsKey("sky_ground")) { sky.GroundBottomColor = Col(m["sky_ground"]); }
        }
        // Our cloud sky is a ShaderMaterial (not ProceduralSkyMaterial), so feed the
        // mood sky colors to CloudVolume → clouds + their sky track the mood.
        if (_cloud != null)
        {
            Color top = m.ContainsKey("sky_top") ? Col(m["sky_top"]) : new Color(0.30f, 0.48f, 0.74f);
            Color hor = m.ContainsKey("sky_horizon") ? Col(m["sky_horizon"]) : new Color(0.68f, 0.74f, 0.80f);
            Color grd = m.ContainsKey("sky_ground") ? Col(m["sky_ground"]) : new Color(0.22f, 0.26f, 0.22f);
            _cloud.SetSkyColors(top, hor, grd);
        }

        // Fog: the mood JSON values were authored too thick (washed the terrain into
        // haze). Scale WAY down — fog should be a light distance cue, not a blanket.
        // Terrain must read clearly first; atmosphere is seasoning.
        env.FogEnabled = true;
        if (m.ContainsKey("fog_color")) { env.FogLightColor = Col(m["fog_color"]); }
        _baseFogColor = env.FogLightColor;   // for cloud/overcast aerial tinting
        env.FogDensity = F(m, "fog_density", 0.0006f) * 0.25f;
        env.FogAerialPerspective = Mathf.Min(F(m, "fog_aerial", 0.85f), 0.5f);
        env.FogHeight = F(m, "fog_height", -200f);
        env.FogHeightDensity = F(m, "fog_heightd", 0.04f) * 0.3f;
        env.FogSunScatter = F(m, "fog_sun_scatter", 0.2f) * 0.25f;
        // Volumetric fog OFF by default (it was the main 'can't see anything' culprit) —
        // UNLESS god rays are on, which REQUIRE the froxel grid. The god-ray system owns
        // volfog while active; moods must not stomp it off (it runs after SetEnabled at startup).
        env.VolumetricFogEnabled = _godrays != null && _godrays.On;

        env.TonemapExposure = F(m, "exposure", 1.0f);
        env.TonemapWhite = F(m, "white", 6.0f);
        // Per-mood color grade (built-in Environment adjustments). Each mood can set
        // its own contrast/saturation; defaults give a gentle cinematic lift.
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = F(m, "contrast", 1.08f);
        env.AdjustmentSaturation = F(m, "saturation", 1.12f);
        env.AdjustmentBrightness = F(m, "brightness", 1.0f);
        // Glow: keep it a subtle highlight sheen, NOT a sky-wide wash. The over-
        // bright halo around the sun was bloom catching the whole HDR sky — raise the
        // HDR threshold so ONLY the sun disc (very bright) blooms, kill constant bloom.
        env.GlowEnabled = true;
        env.GlowNormalized = true;
        env.GlowHdrThreshold = 1.6f;
        env.GlowBloom = 0.0f;
        env.GlowIntensity = F(m, "glow", 0.3f) * 0.35f;
        env.SetGlowLevel(4, 0.0f); env.SetGlowLevel(5, 0.0f); env.SetGlowLevel(6, 0.0f); // no huge-radius spread

        SyncLightControlsToScene();   // make the Light-tab sliders reflect the mood
        OvercastDirty();              // re-apply overcast scaling onto the new mood bases
        GD.Print($"TerrainLab: mood -> {_moodNames[idx]}");
    }
}
