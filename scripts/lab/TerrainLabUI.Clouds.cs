using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // ---- cloud knobs → CloudVolume (separation: UI never touches cloud internals,
    //      only the public knob setters). _cloud is wired in Stage 3; null = no-op,
    //      so the Clouds tab is inert (but present + tunable in state) until then. --
    private CloudVolume? _cloud;
    private GodRaysScreen? _godraysScreen;    // screen-space radial scatter (GPU Gems 3) — THE god-ray layer
    private AtmosphereCompute? _atmosphere;   // AT-1 GPU physical sky (Hillaire LUTs); default off
    private bool _atmosphereOn;               // AT-1 on-state mirror (ComposeLighting drops FogSkyAffect so fog stops washing the physical sky)
    private bool _atmoMatActivated;           // AT-1 default-on: the sky material flips to the LUT once it's computed (one-time, in _Process)
    private AerialPerspectiveV2? _aerialV2;   // AT-2 v2 64-slice log-Z aerial (screen quad)
    private bool _aerialV2On;                 // UI toggle state (default off, K key toggles)
    private bool _aerialV2Activated;          // one-time RID push once the LUT is ready (_Process gate)
    private bool _cloudLightOn = true;        // AT-3 physical cloud lighting; default ON (eye-gate PASSED 2026-06-21)
    private bool _cloudLightActivated;        // one-time RID+strength push once both nodes are ready (_Process gate)
    private float _cloudLightStr = 10f;       // atmo cloud-light gain (LUT radiance → ambient); gate-tunable
    private void ApplyCloudFloat(string knob, float v)
    {
        // Screen-space god-ray tunables (GPU Gems radial scatter). The froxel-fog layer was dropped
        // 2026-06-19 — it read as a washy fog; screen-space is the crisp AAA look.
        switch (knob)
        {
            case "aerial_strength": _aerialV2?.SetStrength(v); return;
            case "godray_strength":     _godraysScreen?.SetStrength(v); return;     // beam intensity
            case "godray_length":       _godraysScreen?.SetDensity(v); return;      // LOWER density = longer beams
            case "godray_decay":        _godraysScreen?.SetDecay(v); return;        // shaft falloff
            case "godray_cloud_radius": _godraysScreen?.SetCloudRadius(v); return;  // cloud-detect radius around sun
            case "godray_cloud_lum":    _godraysScreen?.SetCloudLum(v); return;     // cloud darkness threshold
            case "cloud_light_strength": _cloudLightStr = Mathf.Max(0f, v); if (_cloudLightOn && _cloudLightActivated) { _cloud?.SetCloudAtmoLight(v); } return;   // AT-3 cloud-light gain
        }
        if (knob.StartsWith("cirrus_")) { _cloud?.SetCirrus(knob, v); return; }   // CO-2 cirrus sky-layer uniforms
        _cloud?.SetKnob(knob, v);
    }
    private void ApplyCloudInt(string knob, int v) => _cloud?.SetKnobInt(knob, v);
    private void ApplyCloudBool(string knob, bool on)
    {
        if (knob == "atmosphere_on") { _atmosphereOn = on; _cloud?.SetAtmosphereOn(on); _atmosphere?.SetEnabled(on); ComposeLighting(); return; }   // AT-1: ComposeLighting re-applies FogSkyAffect so fog stops washing the sky
        if (knob == "aerial_on") { _aerialV2On = on; _atmosphere?.SetAerialEnabled(on); _aerialV2?.SetEnabled(on); _aerialV2Activated = false; return; }   // AT-2 v2
        // AT-3 physical cloud lighting: off → push strength 0 (mood path); on → re-arm the readiness gate (_Process pushes RIDs+strength when both nodes ready).
        if (knob == "cloud_light") { _cloudLightOn = on; _atmosphere?.SetCloudLightWanted(on); if (!on) { _cloud?.SetCloudAtmoLight(0f); } _cloudLightActivated = false; return; }
        if (knob == "godrays") { _godraysScreen?.SetEnabled(on); return; }   // screen-space radial beams
        if (knob == "godray_backlit") { _godraysScreen?.SetCloudInvert(on); return; }   // occluder polarity (sun behind cloud)
        if (knob == "deck_debug") { _cloud?.SetDeckDebug(on); return; }   // deck-ID overlay (debug)
        if (knob == "cirrus_on") { _cloud?.SetCirrusOn(on); return; }     // CO-2 cirrus sky-layer toggle
        _cloud?.SetKnobBool(knob, on);
        // clouds-enabled is the master visual gate; terrain cloud shadows remain opt-in via the debug toggle.
        if (knob == "enabled") { _terrain.SetBool("cloud_shadow_on", on && _terrainCloudShadowOn); }
    }

    // ---- cloud PRESETS (named sky looks) — logic lives in CloudPresets.cs; picker UI + forwarders here ----
    private CloudPresets _cloudPresetsObj = null!;
    private void InitCloudPresets() => _cloudPresetsObj = new CloudPresets(this, _rng, () => _cloud);

    private void BuildCloudPresetPicker(VBoxContainer col)
    {
        _cloudPresetsObj.Load();
        col.AddChild(new Label { Text = "PRESET (named sky look)" });
        var pick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        pick.AddItem("— custom —", 0);
        for (int i = 0; i < _cloudPresetsObj.Count; i++) { pick.AddItem(_cloudPresetsObj.Name(i), i + 1); }
        pick.ItemSelected += idx => { if (idx >= 1) { _cloudPresetsObj.Apply((int)idx - 1); } };
        col.AddChild(pick);
        col.AddChild(new HSeparator());
    }

    private void ApplyCloudPreset(int idx, float jitter = 0f) => _cloudPresetsObj.Apply(idx, jitter);
}
