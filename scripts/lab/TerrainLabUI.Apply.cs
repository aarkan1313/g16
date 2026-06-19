using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // ---- apply ----------------------------------------------------------------

    private void ApplyAll()
    {
        foreach (LabControl c in _controls) { ApplyControl(c, false); }
        _terrain.PushSecondaryZones();
        _terrain.RebakeSplat();
    }

    /// Push one control's current value to the shader/terrain. rebakeIfNeeded: when
    /// a 'rebake' control (mask structure) changes interactively, re-run the bake.
    private void ApplyControl(LabControl c, bool rebakeIfNeeded)
    {
        switch (c.Type)
        {
            case "slider":
                if (c.Field != null) { SetTerrainField(c.Field, c.Value.AsSingle()); }
                else if (c.Param != null) { _terrain.SetFloat(c.Param, c.Value.AsSingle()); }
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
                if (c.Param != null) { _terrain.SetBool(c.Param, c.Value.AsBool()); }
                break;
            case "enum":
                int iv = c.Value.AsInt32();
                if (c.Setter == "mask") { _terrain.SetMaskMode(iv); }
                else if (c.Setter == "blend") { _terrain.SetBlendMode(iv); }
                else if (c.Param != null) { _terrain.SetInt(c.Param, iv); }
                break;
            case "material":
                _terrain.SetZoneMaterial(c.Zone, _materials[Math.Clamp(c.Value.AsInt32(), 0, _materials.Count - 1)]);
                break;
            case "companion":
                _terrain.SetSecondaryZone(c.Zone, c.Value.AsInt32());
                break;
            case "scene":
                ApplyScene(c.Scene, c.Value.AsBool());
                break;
            case "scenef":
                ApplySceneFloat(c.Scene, c.Value.AsSingle());
                break;
        }
        if (rebakeIfNeeded && c.Rebake) { _terrain.RebakeSplat(); }
    }

    private void SetTerrainField(string field, float v)
    {
        if (field == "MixScaleM") { _terrain.MixScaleM = v; }
        else if (field == "MixBias") { _terrain.MixBias = v; }
    }

    private void ApplyScene(string? target, bool on)
    {
        switch (target)
        {
            case "ssao":   GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.SsaoEnabled = on; break;
            case "fog":    GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.FogEnabled = on; break;
            case "shadow": GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun").ShadowEnabled = on; break;
            case "sun":    GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun").Visible = on; break;
            case "sdfgi":  GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.SdfgiEnabled = on; break;
            case "volfog": GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment.VolumetricFogEnabled = on; break;
        }
    }

    private float _sunAngle = 35f, _sunAzimuth = 40f;
    private void ApplySceneFloat(string? target, float v)
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        switch (target)
        {
            case "sun_energy":      sun.LightEnergy = v; _baseSunEnergy = v; OvercastDirty(); PushSunToCloud(sun); break;
            case "sun_soft":        sun.ShadowBlur = v; break;   // shadow softness (separate from disc)
            case "sun_disc":        sun.LightAngularDistance = v; break;   // visible sun size (PCSS penumbra too)
            case "sun_angle":       _sunAngle = v; OrientSun(sun); break;
            case "sun_azimuth":     _sunAzimuth = v; OrientSun(sun); break;
            case "ambient":         env.AmbientLightEnergy = v; break;
            case "ssao_intensity":  env.SsaoIntensity = v; break;
            case "ssao_radius":     env.SsaoRadius = v; break;
            case "fog_density":     env.FogDensity = v; break;
            case "fog_aerial":      env.FogAerialPerspective = v; break;
            case "fog_heightd":     env.FogHeightDensity = v; break;
            case "exposure":        env.TonemapExposure = v; break;
            // While god rays own the base volfog (forced ~0 so it doesn't wash the shafts),
            // this slider must not write the live env — route it to the restore-target instead.
            case "volfog_d":        if (_godrays == null || !_godrays.RedirectBaseDensity(v)) { env.VolumetricFogDensity = v; } break;
            // (Light-tab "godray power" retired 2026-06-18 — god rays are now the unified
            //  cloud-occluded FogVolume system in the Clouds tab; see godray-redesign-spec.md)
        }
    }
    private void OrientSun(DirectionalLight3D sun)
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
        // the visible disc uses the BASE (un-dimmed) energy — overcast must not dim the sun in a gap.
        _cloud?.SetSunDiscEnergy(_baseSunEnergy);
        _godrays?.SetSun(toSun, sun.LightColor);   // volumetric shafts track the same sun
    }

    /// After a mood sets the scene, update the Light-tab slider widgets so they show
    /// the mood's values (sliders are live overrides on top of the chosen mood).
    private void SyncLightControlsToScene()
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        void Set(string id, float v) { if (_byId.TryGetValue(id, out var c)) { SetWidgetValueSilent(c, v); } }
        Set("sun_energy", sun.LightEnergy); Set("sun_angle", _sunAngle); Set("sun_azimuth", _sunAzimuth);
        Set("sun_soft", sun.ShadowBlur); Set("sun_disc", sun.LightAngularDistance); Set("ambient_e", env.AmbientLightEnergy);
        Set("ssao_i", env.SsaoIntensity); Set("ssao_r", env.SsaoRadius);
        Set("fog_d", env.FogDensity); Set("fog_aerial", env.FogAerialPerspective);
        Set("fog_heightd", env.FogHeightDensity); Set("exposure", env.TonemapExposure);
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
}
