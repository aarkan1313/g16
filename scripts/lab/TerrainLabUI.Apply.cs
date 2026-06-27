using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // #7 perf: cache the Env + Sun nodes (they never move) instead of re-walking the tree on every slider
    // edit / per-frame SyncLightControlsToScene during a day/night cycle. Lazy so first access matches the
    // old timing. Mirrors LightingComposer's EnvNode/SunNode caching.
    private WorldEnvironment? _uiEnvNode;
    private DirectionalLight3D? _uiSunNode;
    private WorldEnvironment UiEnv => _uiEnvNode ??= GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
    private DirectionalLight3D UiSun => _uiSunNode ??= GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");

    // ---- apply ----------------------------------------------------------------

    private void ApplyAll()
    {
        foreach (LabControl c in _controls) { ApplyControl(c, false); }
    }

    /// Push one control's current value to the shader/terrain. rebakeIfNeeded: when
    /// a 'rebake' control (mask structure) changes interactively, re-run the bake.
    private void ApplyControl(LabControl c, bool rebakeIfNeeded)
    {
        switch (c.Type)
        {
            case "slider":
                if (c.Param != null) { _terrain.SetFloat(c.Param, c.Value.AsSingle()); }
                break;
            case "cloudf":
                if (c.Cloud != null) { ApplyCloudFloat(c.Cloud, c.Value.AsSingle()); }
                break;
            case "cloudi":
                if (c.Cloud != null) { ApplyCloudInt(c.Cloud, Mathf.RoundToInt(c.Value.AsSingle())); }
                break;
            case "cloud":
                if (c.Cloud != null) { ApplyCloudBool(c.Cloud, c.Value.AsBool()); }
                break;
            case "toggle":
                if (c.Field != null) { SetTerrainBoolField(c.Field, c.Value.AsBool()); }
                else if (c.Param != null) { _terrain.SetBool(c.Param, c.Value.AsBool()); }
                break;
            case "enum":
                int iv = c.Value.AsInt32();
                if (c.Param != null) { _terrain.SetInt(c.Param, iv); }
                break;
            case "scene":
                ApplyScene(c.Scene, c.Value.AsBool());
                break;
            case "scenef":
                ApplySceneFloat(c.Scene, c.Value.AsSingle());
                break;
            case "scenecolor":
                ApplySceneColor(c.Scene, c.Value.AsColor());
                break;
        }
    }

    private void SetTerrainBoolField(string field, bool v)
    {
        if (field == "UseGiProxy") { _terrain.SetGiProxy(v); }
    }

    private void ApplySceneColor(string? target, Color col)
    {
        switch (target)
        {
            case "meteor_color":    _stars.MeteorColor = col; ComposeLighting(); break;
            case "moon_color":      _moon.Color = col; ComposeLighting(); break;
            case "moonlight_color": _moon.LightColor = col; ComposeLighting(); break;
            case "sky_tint":        _skyTint = col; ComposeLighting(); break;   // ST4-2 fantasy sky tint
        }
    }

    private void ApplyScene(string? target, bool on)
    {
        switch (target)
        {
            case "fog":    UiEnv.Environment.FogEnabled = on; break;
            case "meteors_on": _stars.MeteorsOn = on; ComposeLighting(); break;
            case "planets_on":      _stars.PlanetsOn = on; ComposeLighting(); break;        // C2 planets
            case "bright_stars_on": _stars.BrightStarsOn = on; ComposeLighting(); break;    // C2 landmark stars
            case "sun":    UiSun.Visible = on; break;
            case "sdfgi":  UiEnv.Environment.SdfgiEnabled = false; break;
            case "volfog": _volumetricFogOn = on; UiEnv.Environment.VolumetricFogEnabled = on; break;
            case "sun_surface_on": _cloud?.SetSunSurfaceOn(on); break;   // sun-disc surface (cloud_sky material)
            case "time_running":   _timeRunning = on; break;             // ST4-1 auto day/night cycle play/pause
        }
    }

    // _sunAngle / _sunAzimuth moved to LightingComposer (C3 Unit 1); accessed here via the forwarding
    // properties in TerrainLabUI.Lighting.cs (same names), so the slider/OrientSun code below is unchanged.
    private void ApplySceneFloat(string? target, float v)
    {
        var env = UiEnv.Environment;   // #7 perf: cached node (was a per-edit tree walk)
        var sun = UiSun;
        switch (target)
        {
            // sun energy + ambient go through ApplyOvercastScaling (the one writer of the overcast-scaled
            // fields) so they stay overcast-correct and never fight UpdateOvercast.
            case "sun_energy":      _baseSunEnergy = v; ApplyOvercastScaling(); PushSunToCloud(sun); break;
            case "load_ring":       _terrain.SetLoadRing(Mathf.RoundToInt(v)); break;   // ARC B Task 1: CDLOD load-ring radius (1=3×3, 2=5×5)
            case "fog_depth_begin": _lighting.FogDepthBegin = v; ComposeLighting(); break;   // VIEW-DISTANCE: depth-fog start distance (clear nearer than this)
            case "cdlod_lookahead": _terrain.SetCdlodLookahead(v); break;   // ARC B Task 4: predictive-loading lookahead (s; 0=off)
            case "cdlod_aabb_speed": _terrain.SetCdlodAabbSpeed(v); break;   // max speed that may run async AABB tightening
            case "sun_angle":       _sunAngle = v; OrientSun(sun); break;
            case "sun_azimuth":     _sunAzimuth = v; OrientSun(sun); break;
            case "time_of_day":     DriveTime(v); break;   // decoupled Time axis: sun arc + day color script
            case "time_speed":      _timeSpeed = v; break; // ST4-1 auto-cycle: in-world hours per real second
            case "day_sunrise":     _time.SunriseH = v; DriveTime(_time.TimeOfDay); break;
            case "day_sunset":      _time.SunsetH = v; DriveTime(_time.TimeOfDay); break;
            case "day_peak_elev":   _time.PeakElev = v; DriveTime(_time.TimeOfDay); break;
            case "night_darkness":      _time.NightDarkness = v; DriveTime(_time.TimeOfDay); break;
            case "night_ambient_floor": _time.NightAmbientFloor = v; DriveTime(_time.TimeOfDay); break;
            // MOON (Stage 3b) — Night tab. Each sets the _moon field then re-composes (re-pushes the moon).
            case "moon_phase":          _moon.Phase = v; ComposeLighting(); break;
            case "moon_size":           _moon.Size = v; ComposeLighting(); break;
            case "moon_limb":           _moon.Limb = v; ComposeLighting(); break;
            case "moon_energy":         _moon.DiscEnergy = v; ComposeLighting(); break;
            case "moon_halo_size":      _moon.HaloSize = v; ComposeLighting(); break;
            case "moon_halo_energy":    _moon.HaloEnergy = v; ComposeLighting(); break;
            case "moon_surf_contrast":  _moon.SurfContrast = v; ComposeLighting(); break;
            case "moon_surf_spots":     _moon.SurfSpots = v; ComposeLighting(); break;
            case "moon_surf_cells":     _moon.SurfCells = v; ComposeLighting(); break;
            case "moon_elev_off":       _moon.ElevOffset = v; ComposeLighting(); break;
            case "moon_az_off":         _moon.AzOffset = v; ComposeLighting(); break;
            case "moon_decl":           _moon.DeclScale = v; ComposeLighting(); break;   // moon arc peak height (own declination)
            case "moonlight_energy":    _moon.LightEnergy = v; ComposeLighting(); break;
            case "moon_cloud_light":    _moon.MoonCloudLight = v; ComposeLighting(); break;   // moonlight scattered through clouds
            case "star_brightness":     _stars.Brightness = v; ComposeLighting(); break;
            case "star_density":        _stars.Density = v; ComposeLighting(); break;
            case "star_twinkle":        _stars.Twinkle = v; ComposeLighting(); break;
            case "star_rotation":       _stars.Rotation = v; ComposeLighting(); break;
            case "meteor_rate":         _stars.MeteorRate = v; ComposeLighting(); break;
            case "meteor_brightness":   _stars.MeteorBrightness = v; ComposeLighting(); break;
            case "meteor_length":       _stars.MeteorLength = v; ComposeLighting(); break;
            case "meteor_speed":        _stars.MeteorSpeed = v; ComposeLighting(); break;
            case "meteor_color_var":    _stars.MeteorColorVar = v; ComposeLighting(); break;
            case "planet_brightness":      _stars.PlanetBrightness = v; ComposeLighting(); break;       // C2 planets
            case "bright_star_brightness": _stars.BrightStarBrightness = v; ComposeLighting(); break;   // C2 landmark stars
            case "extra_suns":   _lighting.ExtraSunCount = Mathf.RoundToInt(v); ComposeLighting(); break;    // C3: extra suns (0-3), live + preset-settable
            case "extra_moons":  _lighting.ExtraMoonCount = Mathf.RoundToInt(v); ComposeLighting(); break;   // C3: extra moons (0-2)
            case "inspect_energy":      _inspectEnergy = v; if (_inspectLight != null) { _inspectLight.LightEnergy = v; } break;
            case "ambient":         _baseAmbient = v; ApplyOvercastScaling(); break;
            case "fog_density":     env.FogDensity = v; break;
            case "fog_aerial":      env.FogAerialPerspective = v; break;
            case "fog_heightd":     env.FogHeightDensity = v; break;
            case "exposure":        env.TonemapExposure = v; break;
            case "volfog_d":        env.VolumetricFogDensity = v; break;
            case "sun_size":  _cloud?.SetSunSize(v); break;
            case "sun_limb":  _cloud?.SetSunLimb(v); break;
            case "sun_corona_size":   _cloud?.SetSunCoronaSize(v); break;
            case "sun_corona_energy": _cloud?.SetSunCoronaEnergy(v); break;
            case "sun_halo_size":     _cloud?.SetSunHaloSize(v); break;
            case "sun_halo_energy":   _cloud?.SetSunHaloEnergy(v); break;
            case "sun_cloud_redden": _cloud?.SetSunCloudRedden(v); break;
            case "sun_redden":       _cloud?.SetSunRedden(v); break;
            case "sun_redden_onset": _cloud?.SetSunReddenOnset(v); break;
            case "sun_horizon_grow": _cloud?.SetSunHorizonGrow(v); break;
            case "sun_surface_cells":    _cloud?.SetSunSurfaceCells(v); break;
            case "sun_surface_contrast": _cloud?.SetSunSurfaceContrast(v); break;
            case "sun_surface_spots":    _cloud?.SetSunSurfaceSpots(v); break;
            case "sun_surface_churn":    _cloud?.SetSunSurfaceChurn(v); break;
            case "sun_surface_warm":     _cloud?.SetSunSurfaceWarm(v); break;
        }
    }
    public void OrientSun(DirectionalLight3D sun)   // public: satisfies ILightingHost (composer calls back)
    {
        // elevation from horizon + compass azimuth → a downward-pointing sun.
        sun.RotationDegrees = new Vector3(-_sunAngle, _sunAzimuth, 0f);
        PushSunToCloud(sun);
    }

    /// Feed the scene sun to the cloud compute (direction TOWARD the sun + color +
    /// energy) so cloud lighting tracks the sun/mood. A DirectionalLight points along
    /// -Z of its basis; the sun is in the opposite direction (-forward).
    private void PushSunToCloud(DirectionalLight3D sun)
    {
        Vector3 toSun = sun.GlobalTransform.Basis.Z.Normalized();   // -(-Z forward) = +Z
        _cloud?.SetSun(toSun, sun.LightColor, sun.LightEnergy);
        _atmosphere?.SetSun(toSun);   // AT-1: recompute the sky-view LUT when the sun moves
        // the visible disc uses the BASE (un-dimmed) energy — overcast must not dim the sun in a gap.
        _cloud?.SetSunDiscEnergy(_baseSunEnergy);
        // (screen-space god rays read the sun themselves each frame in GodRaysScreen._Process.)
    }

    /// After a mood sets the scene, update the Light-tab slider widgets so they show
    /// the mood's values (sliders are live overrides on top of the chosen mood).
    public void SyncLightControlsToScene()   // public: satisfies ILightingHost (composer calls back)
    {
        var env = UiEnv.Environment;   // #7 perf: cached nodes (this runs every frame during a day/night cycle)
        var sun = UiSun;
        void Set(string id, float v) { if (_byId.TryGetValue(id, out var c)) { SetWidgetValueSilent(c, v); } }
        Set("sun_energy", sun.LightEnergy); Set("sun_angle", _sunAngle); Set("sun_azimuth", _sunAzimuth);
        Set("ambient_e", env.AmbientLightEnergy);
        Set("fog_d", env.FogDensity); Set("fog_aerial", env.FogAerialPerspective);
        Set("fog_heightd", env.FogHeightDensity); Set("exposure", env.TonemapExposure);
        // Sync the 10 sun disc/optical sliders from the CloudVolume backing fields
        // (these uniforms live only on _skyMat, not on scene objects, so we read getters).
        Set("sun_size",          _cloud?.SunSize          ?? 0.6f);
        Set("sun_limb",          _cloud?.SunLimb          ?? 0.55f);
        Set("sun_corona_size",   _cloud?.SunCoronaSize    ?? 1200f);
        Set("sun_corona_energy", _cloud?.SunCoronaEnergy  ?? 2.0f);
        Set("sun_halo_size",     _cloud?.SunHaloSize      ?? 90f);
        Set("sun_halo_energy",   _cloud?.SunHaloEnergy    ?? 0.4f);
        Set("sun_redden",        _cloud?.SunRedden        ?? 1.0f);
        Set("sun_redden_onset",  _cloud?.SunReddenOnset   ?? 0.25f);
        Set("sun_horizon_grow",  _cloud?.SunHorizonGrow   ?? 0.6f);
        Set("sun_cloud_redden",  _cloud?.SunCloudRedden   ?? 0.8f);
        PushSunToCloud(sun);   // clouds track the mood's sun
    }

    /// Update a slider widget + value WITHOUT re-applying (avoids fighting the mood).
    private void SetWidgetValueSilent(LabControl c, float v)
    {
        bool wasReady = _ready; _ready = false;
        if (c.Widget is HSlider sl) { sl.Value = v; }
        if (c.ValLabel != null) { c.ValLabel.Text = v.ToString((c.Max - c.Min) < 0.05f ? "0.0000" : "0.00"); }
        c.Value = v;
        _ready = wasReady;
    }

    /// Set a widget's value (updates UI + applies). Suppresses per-control rebake;
    /// the caller batches one rebake at the end.
    private void SetWidgetValue(LabControl c, Variant v)
    {
        bool wasReady = _ready; _ready = false;   // avoid double-apply via signal
        switch (c.Widget)
        {
            case HSlider sl: sl.Value = v.AsSingle(); if (c.ValLabel != null) { c.ValLabel.Text = v.AsSingle().ToString("0.00"); } break;
            case CheckBox cb: cb.ButtonPressed = v.AsBool(); break;
            case OptionButton ob: ob.Select(v.AsInt32()); break;
        }
        c.Value = v;
        _ready = wasReady;
        ApplyControl(c, false);
    }

    // ---- ILabControls (decomposition Phase 2): the narrow registry façade extracted modules use.
    // Thin wrappers over the existing registry fields/methods — no behavior change.
    bool ILabControls.IsReady { get => _ready; set => _ready = value; }
    IReadOnlyList<LabControl> ILabControls.Controls => _controls;
    IReadOnlyDictionary<string, LabControl> ILabControls.ById => _byId;
    bool ILabControls.TryGet(string id, out LabControl c) => _byId.TryGetValue(id, out c!);
    void ILabControls.SetValue(LabControl c, Variant v) => SetWidgetValue(c, v);
    void ILabControls.SetValueSilent(LabControl c, float v) => SetWidgetValueSilent(c, v);
}
