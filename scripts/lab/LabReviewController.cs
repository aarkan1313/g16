using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// VISUAL REVIEW presets (the eye-gate session). Active only when ReviewMode = true (scenes/review.tscn).
/// Keys 1-9 jump the lab to one eye-gate item: applies the relevant toggles/mood/time/camera from the
/// approved baseline and shows an on-screen "what to judge" banner. The T key cycles the S3 terrain A/B
/// views in BOTH scenes. Everything routes through the ILabControls registry so nothing silently no-ops.
///
/// Extracted from the TerrainLabUI god-class (decomposition Phase 3d). Most of its dependencies are the
/// modules already carved out — the registry (ILabControls), the sky preset library (SkyPresets), and the
/// lighting composer — plus the terrain, a time-running setter, and the host Node for camera/label nodes.
/// The host keeps the [Export] ReviewMode + the _UnhandledInput override (a Godot lifecycle method); it
/// forwards input here.
public sealed class LabReviewController
{
    private readonly Node _host;
    private readonly ILabControls _reg;
    private readonly SkyPresets _sky;
    private readonly LightingComposer _lighting;
    private readonly TerrainLab _terrain;
    private readonly bool _nightGate;
    private readonly Action<bool> _setTimeRunning;
    private readonly bool _reviewMode;

    private Label? _reviewLabel;
    private int _lastPreset = -1;
    private bool _co1ProfileOn;   // CO-1 vertical-profile A/B state (review key 6 toggles it)
    private int _co2Type;         // CO-2 type on review key 6: 0 cumulus · 1 stratus · 2 cirrus
    private int _fantasyIdx;      // ST4-2 fantasy preset on review key 7 (cycles)
    private int _atmoStep;        // AT-1 atmosphere time-of-day preset on review key 8
    private int _c3Idx;           // Celestial C3 multi-luminary on review key 5
    private int _terrainDebugMode; // S3: 0 default · 1 no-tighten · 2 lod-viz · 3 single mesh
    private bool _shadowReviewOn; // key 4: low-sun shadowless vs terrain horizon shadow A/B
    private List<Godot.Collections.Dictionary>? _nightStates;

    public LabReviewController(Node host, ILabControls reg, SkyPresets sky, LightingComposer lighting,
                               TerrainLab terrain, bool nightGate, Action<bool> setTimeRunning, bool reviewMode)
    {
        _host = host;
        _reg = reg;
        _sky = sky;
        _lighting = lighting;
        _terrain = terrain;
        _nightGate = nightGate;
        _setTimeRunning = setTimeRunning;
        _reviewMode = reviewMode;
    }

    public void HandleInput(InputEvent @event)
    {
        // S3: the terrain-debug cycle key (T) is live in BOTH scenes — it doesn't need ReviewMode.
        if (@event is InputEventKey tk && tk.Pressed && !tk.Echo && tk.Keycode == Key.T)
        {
            CycleTerrainDebug();
            _host.GetViewport().SetInputAsHandled();
            return;
        }

        if (!_reviewMode || _reg.Controls.Count == 0) { return; }
        if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
            int n = k.Keycode switch
            {
                Key.Key1 => 1, Key.Key2 => 2, Key.Key3 => 3, Key.Key4 => 4, Key.Key5 => 5,
                Key.Key6 => 6, Key.Key7 => 7, Key.Key8 => 8, Key.Key9 => 9, _ => 0
            };
            if (n > 0) { ApplyReview(n); _host.GetViewport().SetInputAsHandled(); }
        }
    }

    /// One key (T) cycles the four S3 terrain A/B states live. Idempotent (sets the FULL config each step).
    private void CycleTerrainDebug()
    {
        if (!_reg.IsReady || _terrain == null) { return; }   // ignore key presses during load
        if (_reviewLabel == null) { BuildReviewLabel(); }
        _terrainDebugMode = (_terrainDebugMode + 1) % 4;
        string title, judge;
        switch (_terrainDebugMode)
        {
            case 1:
                _terrain.SetCdlod(true); _terrain.SetCdlodViz(false); _terrain.ConfigureCdlodAabb(false, 0, 0);
                title = "TERRAIN  [T]  2/4 · CDLOD, tighten OFF (generous AABB)";
                judge = "A/B async culling-AABB tightening: far-out chunks keep generous bounds vs mode 1. Press T to continue.";
                break;
            case 2:
                _terrain.SetCdlod(true); _terrain.ConfigureCdlodAabb(true, 0, 0); _terrain.SetCdlodViz(true);
                title = "TERRAIN  [T]  3/4 · LOD-viz (chunks tinted by level)";
                judge = "Fly: the LOD bands should roam smoothly with you, finest near camera. No band edge at the horizon. Press T to continue.";
                break;
            case 3:
                _terrain.SetCdlodViz(false); _terrain.SetCdlod(false);
                title = "TERRAIN  [T]  4/4 · single mesh (CDLOD OFF — pre-S3 baseline)";
                judge = "The non-streaming full mesh: finite region, no infinite world. A/B vs the CDLOD modes. Press T to return to mode 1.";
                break;
            default:
                _terrain.SetCdlod(true); _terrain.SetCdlodViz(false); _terrain.ConfigureCdlodAabb(true, 0, 0);
                title = "TERRAIN  [T]  1/4 · CDLOD + async tight AABBs (DEFAULT)";
                judge = "The shipping S3 look: infinite streaming with tight culling bounds. Fly a minute + teleport far out — no edge/hitch/shimmer/jitter. Press T to cycle A/B views.";
                break;
        }
        _reviewLabel!.Text = $"{title}\n{judge}";
        GD.Print($"[terraindebug] mode {_terrainDebugMode + 1}/4 — {title}");
    }

    public void ApplyReview(int n)
    {
        if (_reviewLabel == null) { BuildReviewLabel(); }
        if (_nightGate) { ApplyReviewNight(n); return; }   // --nightgate=1: keys read data/review_night.json
        if (n != 4) { _terrain.SetBool("dbg_unlit", false); }
        string title, judge;
        switch (n)
        {
            case 1: // Sun disc + surface — Stage 1; press 1 again to cycle sun presets
                _sky.ApplyMood(0);                                   // golden hour = low warm sun
                Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                Set("sun_corona_energy", 1.0f); Set("sun_halo_energy", 1.0f); Set("sun_redden", 1.0f);
                _sky.LoadSun();
                if (_sky.Sun.Count > 0)
                {
                    if (_lastPreset == 1) { _sky.SunIdx = (_sky.SunIdx + 1) % _sky.Sun.Count; }
                    _sky.ApplySun(_sky.SunIdx);
                }
                string sunName = (_sky.Sun.Count > 0) ? _sky.Sun[_sky.SunIdx].name : "?";
                title = $"1 · Sun disc + surface  [{sunName}]  (press 1 to cycle)";
                judge = "Fly CLOSE to the sun. Surface believable (granulation, churn, spots), not a flat circle? No swimming or pole-spin (raise sun high). No clip-to-white. Press 1 to cycle presets; tune 'sun surface *' on the Light tab.";
                break;
            case 2: // Night sky — moon + stars + meteors
                _lighting.DriveTime(0f); _setTimeRunning(false);
                if (_reg.TryGet("time_of_day", out var tod)) { _reg.SetValueSilent(tod, 0f); }
                Set("cloud_enabled", false);                    // clear sky so the stars + moon read
                {
                    var camN = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
                    if (camN != null)
                    {
                        camN.Position = new Vector3(0, 280, 200);
                        if (_lighting.LastMoonDir != Vector3.Zero) { camN.LookAt(camN.GlobalPosition + _lighting.LastMoonDir, Vector3.Up); }
                        else { camN.RotationDegrees = new Vector3(28, 180, 0); }
                    }
                }
                title = "2 · Night sky — stars + moon";
                judge = "Clear night: moon + stars + meteors. Tune 'star *' (brightness/density/twinkle), 'meteor *', and the 'moon *' knobs on the Night tab; moon phase/size/halo + moonlight on the ground.";
                break;
            case 3: // Night clouds — moonlit
                _lighting.DriveTime(0f); _setTimeRunning(false);
                if (_reg.TryGet("time_of_day", out var tod3)) { _reg.SetValueSilent(tod3, 0f); }
                Set("cloud_enabled", true); Set("cloud_coverage", 0.6f);   // real cloud masses to light
                Set("moon_phase", 1.0f);                                    // full moon = strongest moonlight
                LookUpAtClouds();
                title = "3 · Night clouds — moonlit";
                judge = "Night, moon up, clouds on: do the clouds get silver-lit edges/undersides (not flat black)? Tune Night tab 'moonlight on clouds' (+ 'moon brightness' / 'moon phase'). Drop it / new moon → clouds go dark (intended — no-moon night).";
                break;
            case 5: // Celestial C3 — multi-luminary. Press 5 to cycle.
                if (_lastPreset != 5) { _c3Idx = 0; } else { _c3Idx = (_c3Idx + 1) % 5; }
                Set("cloud_coverage", 0.06f);   // mostly clear so the discs read
                if (_c3Idx <= 2)
                {
                    int suns = _c3Idx + 2;                       // 2, 3, 4 total suns
                    Set("extra_moons", 0f);
                    Set("extra_suns", (float)(suns - 1));        // extra count = total - 1 (primary)
                    Set("time_of_day", 13f);                    // daytime so the suns are up
                    LookAtSun();
                    title = $"5 · C3 — {suns} SUNS (day)  (press 5 to cycle)";
                    judge = "Multiple suns: distinct discs (size/color), each lighting terrain; the sky scatters toward them. Companions are spread by azimuth — fly around / pan to see them all. Colors/sizes/spread are placeholder constants in LightingComposer — tell me what to change.";
                }
                else
                {
                    int moons = _c3Idx - 1;                      // idx3→2, idx4→3 total moons
                    Set("extra_suns", 0f);
                    Set("extra_moons", (float)(moons - 1));
                    Set("time_of_day", 23f);                    // night so the moons are up (also updates LastMoonDir)
                    var camC3 = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
                    if (camC3 != null)
                    {
                        camC3.Position = new Vector3(0, 280, 200);
                        if (_lighting.LastMoonDir != Vector3.Zero) { camC3.LookAt(camC3.GlobalPosition + _lighting.LastMoonDir, Vector3.Up); }
                        else { camC3.RotationDegrees = new Vector3(28, 180, 0); }
                    }
                    title = $"5 · C3 — {moons} MOONS (night)  (press 5 to cycle)";
                    judge = "Multiple moons: each its own size / color / PHASE on the clear night sky. Pan up/around to see them. Tune the ExtraMoon* constants in LightingComposer. Press 5 again to cycle back to the suns.";
                }
                break;
            case 4:
            {
                // ENGINE SUN-SHADOW A/B (standard Forward+ stack). Pressing 4 flips ONLY the Sun's engine
                // CSM ShadowEnabled on↔off. Lighting is held CONSTANT across the toggle, so the ONLY thing
                // that changes is the engine cast shadow — that is how you tell a real cast shadow from N·L
                // slope shading (the recurring confound). First press = ON; press 4 again to compare OFF.
                // NOTE Phase 0: the Sun scene node + CDLOD casters are not yet configured (Phase 1), so the
                // toggle may show little/no shadow here — that is correct for the clean baseline.
                var sunNode = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
                _shadowReviewOn = _lastPreset == 4 ? !(sunNode?.ShadowEnabled ?? false) : true;
                _sky.ApplyMood(5);
                Set("cloud_enabled", false);     // clouds OFF (their own shadows would confound the read)
                Set("cloud_godrays", false);
                Set("cloud_godray_backlit", false);
                Set("aerial_on", false);
                Set("volfog_on", false);
                // SHOW the sun. dbg_sun MUST stay true: dbg_sun=false hides the Sun DirectionalLight3D
                // (TerrainLabUI.Apply.cs:89) → LIGHT0_ENABLED false in cloud_sky.gdshader → sun_layers()
                // early-returns vec3(0), no disc at any size. Surface OFF keeps the bright 12x disc.
                Set("dbg_sun", true);
                Set("sun_surface_on", false);
                Set("sun_corona_energy", 2.0f);
                Set("sun_halo_energy", 0.6f);
                Set("sun_size", 1.6f);
                Set("atmosphere_on", true);
                Set("time_of_day", 16.7f);       // sun ~18-20 deg: low, clearly above horizon
                _setTimeRunning(false);          // freeze the clock so a moving sun isn't a confound
                Set("extra_suns", 0f);           // clear stray C3 luminaries so no extra directional fill
                Set("extra_moons", 0f);          // contaminates the shadow read.
                Set("dbg_fog", false);
                Set("sun_energy", 1.2f);         // lighting held CONSTANT in both A/B states
                Set("ambient_e", 0.30f);         // lower fill so the cast shadow READS
                Set("dbg_fullrough", true);      // matte: no specular flicker confounding the read
                Set("dbg_normalmap", false);
                _terrain.SetBool("dbg_unlit", false);   // KEEP terrain lit in BOTH states (constant lighting)
                if (sunNode != null) { sunNode.ShadowEnabled = _shadowReviewOn; }   // THE toggle: engine CSM on/off
                if (_lastPreset != 4) { FrameSunForShadowReview(); }   // first press: frame the sun + terrain
                title = _shadowReviewOn
                    ? "4 · Sun CSM shadows: ON  (press 4 → OFF)"
                    : "4 · Sun CSM shadows: OFF — same lit frame  (press 4 → ON)";
                judge = _shadowReviewOn
                    ? "Low sun, lit terrain. The ONLY change from OFF is the engine cast shadow. JUDGE (in motion): a shadow stays GLUED to its caster as you fly + yaw (no swim); NO chunk/LOD popping, flicker, or black blobs while moving. (Phase 0: casters not configured yet — little shadow expected until Phase 1.)"
                    : "Same lit low-sun frame, Sun shadows OFF. Any darkness you see HERE is slope shading (N·L), not a cast shadow. Press 4 to flip the engine cast shadow back ON and compare.";
                GD.Print($"[review-shadow] key4 sun-csm ShadowEnabled={_shadowReviewOn}");
                break;
            }
            case 6: // Clouds CO-1/CO-2 types — press 6 to cycle.
                if (_lastPreset != 6)
                {
                    _sky.ApplyMood(5);                                  // neutral midday
                    Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                    Set("cloud_profile_bottom", 0.15f); Set("cloud_profile_top", 0.6f); Set("cloud_anvil", 0.0f);
                    LookUpAtClouds();
                    _co2Type = 0; _co1ProfileOn = false;           // start at the approved slab look
                }
                else if (_co2Type == 0 && !_co1ProfileOn) { _co1ProfileOn = true; }   // cumulus: flip profile ON
                else { _co2Type = (_co2Type + 1) % 3; _co1ProfileOn = false; }         // then advance the type
                Set("cloud_shape_mode", _co2Type == 1 ? 1.0f : 0.0f);
                Set("cloud_cirrus_on", _co2Type == 2);
                Set("cloud_profile_on", _co2Type == 0 && _co1ProfileOn);
                title = _co2Type switch
                {
                    1 => "6 · Clouds CO-2: STRATUS sheet  (press 6 → cirrus)",
                    2 => "6 · Clouds CO-2: CIRRUS layer  (press 6 → cumulus)",
                    _ => $"6 · Clouds CO-1: cumulus vertical profile {(_co1ProfileOn ? "ON (3D)" : "OFF (slab)")}  (press 6 → {(_co1ProfileOn ? "stratus" : "profile ON")})"
                };
                judge = _co2Type switch
                {
                    1 => "Stratus: a flat connected overcast SHEET (not cumulus clumps)? Tune Clouds 'type: cumulus↔stratus' + coverage/density.",
                    2 => "Cirrus: believable high WIND-STREAKED filaments, thin/semi-transparent, fading at the horizon, warm near the sun? Tune Clouds 'cirrus: *'. Cumulus still composites over it.",
                    _ => "Cumulus vertical profile (CO-1): ON reads as a 3D volume, OFF = approved slab. Press 6 to cycle on → stratus → cirrus. NOTE: profile ON thins clouds — raise 'density'."
                };
                break;
            case 7: // Stage-4 FANTASY / exotic sky — press 7 to cycle the fantasy presets
                _sky.LoadFantasy();
                if (_lastPreset != 7)
                {
                    _sky.ApplyMood(5); Set("cloud_enabled", true); Set("cloud_coverage", 0.4f); Set("time_of_day", 20f);
                    _fantasyIdx = 0;
                }
                else if (_sky.Fantasy.Count > 0) { _fantasyIdx = (_fantasyIdx + 1) % _sky.Fantasy.Count; }
                if (_sky.Fantasy.Count > 0) { _sky.ApplyFantasy(_fantasyIdx); }
                string fname = _sky.Fantasy.Count > 0 ? _sky.Fantasy[_fantasyIdx].name : "?";
                title = $"7 · Fantasy / exotic sky  [{fname}]  (press 7 to cycle)";
                judge = "Cohesive believable exotic sky? Scrub 'time of day' or press 'play day/night' (Light tab) — the look should HOLD as the cycle runs. blood_moon/violet_night read at NIGHT (moon up), alien_green/ember_dusk by day/dusk. Tune 'sky tint' (Light) + moon colors (Night).";
                break;
            case 8: // AT-3 PHYSICAL CLOUD LIGHTING gate. Press 8 to walk an A/B sequence.
                bool atmoFirst = _lastPreset != 8;                        // only the FIRST press reframes
                if (atmoFirst) { _atmoStep = 0; _sky.ApplyMood(5); }      // first press: step 0 + neutral grade
                else { _atmoStep = (_atmoStep + 1) % 8; }                 // re-press: flip cloud light / advance time
                float[] atmoTimes = { 17.5f, 12f, 19.5f, 6.5f };         // golden, noon, dusk, dawn (index = step/2)
                string[] atmoNames = { "GOLDEN HOUR", "NOON", "DUSK", "DAWN" };
                int tIdx = _atmoStep / 2;
                bool clStep = (_atmoStep % 2) == 0;                       // even step = cloud light ON, odd = OFF
                Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                Set("atmosphere_on", true); Set("aerial_on", true);      // the passed AT-1/AT-2 stack stays on
                Set("cloud_light", clStep);                              // THE A/B: physical cloud lighting flips
                Set("atmo_exposure", 12f);
                Set("time_of_day", atmoTimes[tIdx]);
                if (atmoFirst) { LookUpAtClouds(); }                     // tilt up at the cloud deck ONCE
                title = $"8 · CLOUD LIGHT (AT-3) — {atmoNames[tIdx]} ({atmoTimes[tIdx]:0.0}h) · cloud light {(clStep ? "ON" : "OFF")}   (press 8: A/B, then next time)";
                judge = "Each press 8 flips 'physical cloud light (AT-3)' ON↔OFF at a fixed frame (A/B), advancing the time every 2 presses: golden→noon→dusk→dawn. JUDGE: clouds lit by the REAL sky — cool ambient at noon, warm-reddened undersides at golden/dusk; NO over-bright/over-red double-count vs OFF; no popping while time-cycling. Tune 'cloud light strength' (Light tab).";
                break;
            case 9: // H1 BRDF clouds-off baseline
                BaselineGround();
                title = "9 · BRDF / approved baseline (clouds off, H1)";
                judge = "Clouds-off terrain matches the approved look — no regression from the custom light() (Burley+GGX). The clean reference shot.  NOTE: AA (MSAA/FXAA/TAA) is NOT wired in the lab yet.";
                break;
            default: return;
        }
        _reviewLabel!.Text = $"REVIEW   {title}\n{judge}";
        GD.Print($"[review] {title}");
        _lastPreset = n;
    }

    // --- Stage 3a NIGHT gate (data-driven from data/review_night.json) --------
    private void ApplyReviewNight(int n)
    {
        LoadNightStates();   // reload each press so live edits to review_night.json take effect immediately
        int i = n - 1;
        if (_nightStates == null || i < 0 || i >= _nightStates.Count)
        {
            _reviewLabel!.Text = $"NIGHT GATE   key {n}: no state (edit data/review_night.json)";
            return;
        }
        var s = _nightStates[i];
        float NF(string k, float d) => s.ContainsKey(k) ? (float)s[k].AsDouble() : d;
        _sky.ApplyMood(s.ContainsKey("mood") ? (int)s["mood"].AsInt32() : 5);
        Set("cloud_enabled", s.ContainsKey("clouds") && s["clouds"].AsBool());
        Set("cloud_coverage", NF("coverage", 0.3f));
        Set("night_darkness", NF("darkness", 1.0f));
        Set("night_ambient_floor", NF("floor", 0.02f));
        bool hasMoon = s.ContainsKey("moonphase");
        if (hasMoon) { _lighting.Moon.Phase = NF("moonphase", 1.0f); }   // 3b: per-state moon phase
        Set("time_of_day", NF("time", 12.0f));   // last: DriveTime re-applies with the darkness/floor/moon above
        if (hasMoon)
        {
            var cam = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
            if (cam != null && _lighting.LastMoonDir != Vector3.Zero) { cam.LookAt(cam.GlobalPosition + _lighting.LastMoonDir, Vector3.Up); }
        }
        string label = s.ContainsKey("label") ? s["label"].AsString() : $"night state {n}";
        _reviewLabel!.Text = $"NIGHT GATE   {label}\nFly in motion. Light tab: 'time of day' / 'night darkness' / 'night ambient floor' to fine-tune.";
        GD.Print($"[review-night] {label}");
        _lastPreset = n;
    }

    private void LoadNightStates()
    {
        _nightStates = new List<Godot.Collections.Dictionary>();
        using var f = Godot.FileAccess.Open("res://data/review_night.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[review-night] data/review_night.json not found"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("states")) { return; }
        foreach (Variant v in root["states"].AsGodotArray()) { _nightStates.Add(v.AsGodotDictionary()); }
    }

    // --- helpers -------------------------------------------------------------

    /// Set a control by its lab_controls.json id, routed through the registry so it reaches the
    /// shader/scene/cloud (not just the widget). Warns on an unknown id.
    private void Set(string id, Variant v)
    {
        if (_reg.TryGet(id, out var c)) { _reg.SetValue(c, v); }
        else { GD.PushWarning($"[review] unknown control id '{id}' (skipped)"); }
    }

    private bool ControlBool(string id)
    {
        return _reg.TryGet(id, out var c) && c.Value.VariantType == Variant.Type.Bool && c.Value.AsBool();
    }

    private void BaselineGround()
    {
        _terrain.SetBool("dbg_unlit", false);
        _sky.ApplyMood(2);                                     // midday = neutral
        Set("cloud_enabled", false);
    }

    private void LookUpAtClouds()
    {
        var cam = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (cam != null) { cam.Position = new Vector3(0, 280, 520); cam.RotationDegrees = new Vector3(16, 0, 0); }
    }

    private void LookAtSun()
    {
        var cam = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        var sun = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (cam == null || sun == null) { return; }
        cam.Position = new Vector3(0, 400, 900);
        var toSun = sun.GlobalTransform.Basis.Z.Normalized();
        cam.LookAt(cam.GlobalPosition + toSun, Vector3.Up);
    }

    /// Key 4 vantage: stand HIGH on the anti-sun side and look ACROSS the terrain toward the (low) sun, so the
    /// disc is unoccluded in the upper frame AND long ridge shadows stretch toward the camera — the ideal
    /// far-shadow review angle. basis.Z of the sun light points toward the disc.
    private void FrameSunForShadowReview()
    {
        var cam = _host.GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        var sun = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (cam == null || sun == null) { return; }
        Vector3 toSun = sun.GlobalTransform.Basis.Z.Normalized();
        Vector3 backHoriz = new Vector3(-toSun.X, 0f, -toSun.Z).Normalized();   // away from the sun, level
        cam.Position = backHoriz * 700f + new Vector3(0f, 520f, 0f);            // clean high vantage (clears terrain); near-mid band inside the ~800m CSM bubble shows shadows
        cam.LookAt(cam.GlobalPosition + toSun * 0.7f + new Vector3(0f, -0.18f, 0f), Vector3.Up);  // sun upper frame, lit+shadowed terrain lower frame
    }

    private void BuildReviewLabel()
    {
        _reviewLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "REVIEW   press 1-9 to jump to an eye-gate item"
        };
        _reviewLabel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _reviewLabel.OffsetTop = 6;
        _reviewLabel.AddThemeFontSizeOverride("font_size", 18);
        _reviewLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 0.7f));
        _reviewLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f));
        _reviewLabel.AddThemeConstantOverride("outline_size", 6);
        var layer = _host.GetNodeOrNull<CanvasLayer>("/root/TerrainLabRoot/UILayer");
        if (layer != null) { layer.AddChild(_reviewLabel); }
        else { ((Node)_host).AddChild(_reviewLabel); }
    }
}
