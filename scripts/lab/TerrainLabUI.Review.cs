using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// VISUAL REVIEW presets (the eye-gate session). Active ONLY when ReviewMode = true (set in
/// scenes/review.tscn, false in terrain_lab.tscn). Keys 1-9 jump the lab to one eye-gate item:
/// it applies the relevant toggles/mood/time/camera from the approved baseline and shows an
/// on-screen "what to judge" banner. The full lab UI is still present for manual tuning. Each
/// preset maps to an item in docs/NEEDS_REVIEW.md. AA (gate 7) is NOT wired in the lab — noted,
/// not faked. Everything routes through the registry (`_byId` + SetWidgetValue) so nothing
/// silently no-ops; an unknown id warns. This is a separate partial so it adds nothing to the
/// shared class's behavior unless ReviewMode is on.
public partial class TerrainLabUI : Control
{
    [Export] public bool ReviewMode = false;

    private Label _reviewLabel;
    private int _lastPreset = -1;
    private bool _co1ProfileOn;   // CO-1 vertical-profile A/B state (review key 6 toggles it)
    private int _co2Type;         // CO-2 type on review key 6: 0 cumulus · 1 stratus · 2 cirrus
    private int _fantasyIdx;      // ST4-2 fantasy preset on review key 7 (cycles)
    private int _atmoStep;        // AT-1 atmosphere time-of-day preset on review key 8 (cycles dawn→noon→golden→dusk→night)
    private int _nsReviewIdx = -1; // Celestial C1 night-sky preset on review key 2 (-1 = tuned default, then cycles the 4)

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!ReviewMode || _byId == null || _byId.Count == 0) return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
            int n = k.Keycode switch
            {
                Key.Key1 => 1, Key.Key2 => 2, Key.Key3 => 3, Key.Key4 => 4, Key.Key5 => 5,
                Key.Key6 => 6, Key.Key7 => 7, Key.Key8 => 8, Key.Key9 => 9, _ => 0
            };
            if (n > 0) { ApplyReview(n); GetViewport().SetInputAsHandled(); }
        }
    }

    private void ApplyReview(int n)
    {
        if (_reviewLabel == null) BuildReviewLabel();
        if (_nightGate) { ApplyReviewNight(n); return; }   // --nightgate=1: keys read data/review_night.json
        string title, judge;
        switch (n)
        {
            case 1: // Sun disc + surface — Stage 1 (NEEDS_REVIEW 3b); press 1 again to cycle sun presets
                ApplyMood(0);                                   // golden hour = low warm sun
                Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                Set("sun_corona_energy", 1.0f); Set("sun_halo_energy", 1.0f); Set("sun_redden", 1.0f);
                LoadSunPresets();
                if (_sunPresets.Count > 0)
                {
                    if (_lastPreset == 1) { _sunPresetIdx = (_sunPresetIdx + 1) % _sunPresets.Count; }
                    ApplySunPreset(_sunPresetIdx);
                }
                string sunName = (_sunPresets.Count > 0) ? _sunPresets[_sunPresetIdx].name : "?";
                title = $"1 · Sun disc + surface  [{sunName}]  (press 1 to cycle)";
                judge = "Fly CLOSE to the sun. Surface believable (granulation, churn, spots), not a flat circle? No swimming or pole-spin (raise sun high). No clip-to-white. Press 1 to cycle presets; tune 'sun surface *' on the Light tab.";
                break;
            case 2: // Celestial C1 — procedural fantasy night sky (galaxy + nebulae + stars). Press 2 to cycle presets.
                LoadNightSkyPresets();
                if (_lastPreset != 2)
                {
                    // Drive night DIRECTLY (robust to the ground strip's Apply.cs scenef-path breakage) + lock
                    // the clock so it can't tick back to day. NOT calling ApplyMood (it throws post-strip).
                    DriveTime(0f); _timeRunning = false;
                    if (_byId.TryGetValue("time_of_day", out var tod)) { SetWidgetValueSilent(tod, 0f); }
                    Set("cloud_enabled", false);                // clear sky so the galaxy/stars read
                    Set("moon_energy", 0.0f);                   // moon disc off — don't wash the galaxy
                    Set("moonlight_energy", 0.0f);              // moonlight off (clean dark sky)
                    LookUpAtNightSky();                         // aim the camera straight at the galaxy core
                    _nsReviewIdx = _nsPresets.Count > 2 ? 2 : 0; // first press leads with the hero preset (Aurora Veil)
                }
                else if (_nsPresets.Count > 0) { _nsReviewIdx = (_nsReviewIdx + 1) % _nsPresets.Count; }
                if (_nsPresets.Count > 0) { ApplyNightSkyPreset(_nsReviewIdx); }
                string nsName = (_nsReviewIdx >= 0 && _nsReviewIdx < _nsPresets.Count) ? _nsPresets[_nsReviewIdx].name : "default";
                title = $"2 · Night sky (Celestial C1)  [{nsName}]  (press 2 to cycle presets)";
                judge = "Fantasy night sky: a galaxy with a bright CORE (not a uniform fog band), dust lanes, colored nebulae, varied stars. Press 2 to cycle default→Subtle→Crimson Rift→Aurora Veil→Deep Field. Tune 'galaxy *' / 'nebula *' / 'star *' on the Night tab; 'night sky baked (perf)' on/off must look identical. Fly to look around the sky.";
                break;
            // cases 3-5 (ground G-0 / v2 parity / variation) removed in the 2026-06-21 ground strip.
            case 6: // Clouds CO-1/CO-2 types — press 6 to cycle: cumulus(profile off→on) → stratus → cirrus
                if (_lastPreset != 6)
                {
                    ApplyMood(5);                                  // neutral midday
                    Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                    Set("cloud_profile_bottom", 0.15f); Set("cloud_profile_top", 0.6f); Set("cloud_anvil", 0.0f);
                    LookUpAtClouds();
                    _co2Type = 0; _co1ProfileOn = false;           // start at the approved slab look
                }
                else if (_co2Type == 0 && !_co1ProfileOn) { _co1ProfileOn = true; }   // cumulus: flip profile ON
                else { _co2Type = (_co2Type + 1) % 3; _co1ProfileOn = false; }         // then advance the type
                // reset all type levers, enable the selected one
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
            case 7: // Stage-4 FANTASY / exotic sky — press 7 to cycle the fantasy presets (god rays passed; still on Clouds tab)
                LoadFantasyPresets();
                if (_lastPreset != 7)
                {
                    ApplyMood(5); Set("cloud_enabled", true); Set("cloud_coverage", 0.4f); Set("time_of_day", 20f);
                    _fantasyIdx = 0;
                }
                else if (_fantasyPresets.Count > 0) { _fantasyIdx = (_fantasyIdx + 1) % _fantasyPresets.Count; }
                if (_fantasyPresets.Count > 0) { ApplyFantasyPreset(_fantasyIdx); }
                string fname = _fantasyPresets.Count > 0 ? _fantasyPresets[_fantasyIdx].name : "?";
                title = $"7 · Fantasy / exotic sky  [{fname}]  (press 7 to cycle)";
                judge = "Cohesive believable exotic sky? Scrub 'time of day' or press 'play day/night' (Light tab) — the look should HOLD as the cycle runs. blood_moon/violet_night read at NIGHT (moon up), alien_green/ember_dusk by day/dusk. Tune 'sky tint' (Light) + moon colors (Night).";
                break;
            case 8: // AT-3 PHYSICAL CLOUD LIGHTING gate (atmosphere + aerial stay on — the passed stack). Press 8 to
                    // walk an A/B sequence: each press flips 'physical cloud light' ON↔OFF at a FIXED frame, advancing
                    // the time every 2 presses (golden→noon→dusk→dawn; warm-first, where cloud reddening reads best).
                    // Camera tilts UP at the cloud deck once so undersides are visible. (The AT-2 aerial A/B harness
                    // is in git history — d7474e6 — if it needs revisiting; AT-2 passed 2026-06-21.)
                bool atmoFirst = _lastPreset != 8;                        // only the FIRST press reframes (no teleport while A/B-ing)
                if (atmoFirst) { _atmoStep = 0; ApplyMood(5); }           // first press: step 0 (golden, cloud-light ON) + neutral grade
                else { _atmoStep = (_atmoStep + 1) % 8; }                 // re-press: flip cloud light / advance time
                float[] atmoTimes = { 17.5f, 12f, 19.5f, 6.5f };         // golden, noon, dusk, dawn (index = step/2)
                string[] atmoNames = { "GOLDEN HOUR", "NOON", "DUSK", "DAWN" };
                int tIdx = _atmoStep / 2;
                bool clStep = (_atmoStep % 2) == 0;                       // even step = cloud light ON, odd = OFF (the A/B)
                Set("cloud_enabled", true); Set("cloud_coverage", 0.55f); // clouds IN VIEW (this gate is about lighting them)
                Set("atmosphere_on", true); Set("aerial_on", true);      // the passed AT-1/AT-2 stack stays on
                Set("cloud_light", clStep);                              // THE A/B: physical cloud lighting flips each press
                Set("atmo_exposure", 12f);
                Set("time_of_day", atmoTimes[tIdx]);
                if (atmoFirst) { LookUpAtClouds(); }                     // tilt up at the cloud deck ONCE; re-presses keep the view
                title = $"8 · CLOUD LIGHT (AT-3) — {atmoNames[tIdx]} ({atmoTimes[tIdx]:0.0}h) · cloud light {(clStep ? "ON" : "OFF")}   (press 8: A/B, then next time)";
                judge = "Each press 8 flips 'physical cloud light (AT-3)' ON↔OFF at a fixed frame (A/B), advancing the time every 2 presses: golden→noon→dusk→dawn. JUDGE: clouds should be lit by the REAL sky — cool/sky-colored ambient at noon, warm-reddened undersides + cooler tops at golden/dusk — consistent with the sky behind them; NO over-bright/over-red double-count vs OFF; no popping while time-cycling. Tune 'cloud light strength' (Light tab, default 10). Fly freely between presses.";
                break;
            case 9: // H1 BRDF clouds-off baseline (6)
                BaselineGround();
                title = "9 · BRDF / approved baseline (clouds off, H1)";
                judge = "Clouds-off terrain matches the approved look — no regression from the custom light() (Burley+GGX). The clean reference shot.  NOTE: AA (MSAA/FXAA/TAA) is NOT wired in the lab yet.";
                break;
            default: return;
        }
        _reviewLabel.Text = $"REVIEW   {title}\n{judge}";
        GD.Print($"[review] {title}");
        _lastPreset = n;
    }

    // --- Stage 3a NIGHT gate (data-driven from data/review_night.json) --------
    private List<Godot.Collections.Dictionary> _nightStates;

    private void ApplyReviewNight(int n)
    {
        LoadNightStates();   // reload each press so live edits to review_night.json take effect immediately
        int i = n - 1;
        if (_nightStates == null || i < 0 || i >= _nightStates.Count)
        {
            _reviewLabel.Text = $"NIGHT GATE   key {n}: no state (edit data/review_night.json)";
            return;
        }
        var s = _nightStates[i];
        float NF(string k, float d) => s.ContainsKey(k) ? (float)s[k].AsDouble() : d;
        ApplyMood(s.ContainsKey("mood") ? (int)s["mood"].AsInt32() : 5);
        Set("cloud_enabled", s.ContainsKey("clouds") && s["clouds"].AsBool());
        Set("cloud_coverage", NF("coverage", 0.3f));
        Set("night_darkness", NF("darkness", 1.0f));
        Set("night_ambient_floor", NF("floor", 0.02f));
        bool hasMoon = s.ContainsKey("moonphase");
        if (hasMoon) { _moon.Phase = NF("moonphase", 1.0f); }   // 3b: per-state moon phase
        Set("time_of_day", NF("time", 12.0f));   // last: DriveTime re-applies with the darkness/floor/moon above
        if (hasMoon)   // aim the camera at the (anti-solar, often high) moon so the keypress lands on it
        {
            var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
            if (cam != null && _lastMoonDir != Vector3.Zero) { cam.LookAt(cam.GlobalPosition + _lastMoonDir, Vector3.Up); }
        }
        string label = s.ContainsKey("label") ? s["label"].AsString() : $"night state {n}";
        _reviewLabel.Text = $"NIGHT GATE   {label}\nFly in motion. Light tab: 'time of day' / 'night darkness' / 'night ambient floor' to fine-tune. (no moon/stars yet = 3b+)";
        GD.Print($"[review-night] {label}");
        _lastPreset = n;
    }

    private void LoadNightStates()
    {
        _nightStates = new List<Godot.Collections.Dictionary>();
        using var f = Godot.FileAccess.Open("res://data/review_night.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { GD.PushWarning("[review-night] data/review_night.json not found"); return; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return;
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("states")) return;
        foreach (Variant v in root["states"].AsGodotArray()) { _nightStates.Add(v.AsGodotDictionary()); }
    }

    // --- helpers -------------------------------------------------------------

    /// Set a control to a value by its lab_controls.json id, routed through the registry so it
    /// reaches the shader/scene/cloud (not just the widget). Warns on an unknown id.
    private void Set(string id, Variant v)
    {
        if (_byId.TryGetValue(id, out var c)) SetWidgetValue(c, v);
        else GD.PushWarning($"[review] unknown control id '{id}' (skipped)");
    }

    /// Neutral ground baseline: midday light, clouds off. (Ground material levers were stripped 2026-06-21.)
    private void BaselineGround()
    {
        ApplyMood(2);                                           // midday = neutral
        Set("cloud_enabled", false);
    }

    private void CloseGround()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (cam != null) { cam.Position = new Vector3(0, 140, 320); cam.RotationDegrees = new Vector3(-22, 0, 0); }
    }

    private void GoToShot(int i)
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (cam == null || _shots == null || i < 0 || i >= _shots.Count) return;
        cam.Position = _shots[i].Item2;
        cam.RotationDegrees = _shots[i].Item3;
    }

    // Vantage for judging cloud vertical shape: a low-ish camera tilted UP at the cloud band,
    // so decks are seen from underneath (where slab-vs-volume reads clearest), not down from altitude.
    private void LookUpAtClouds()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (cam != null) { cam.Position = new Vector3(0, 280, 520); cam.RotationDegrees = new Vector3(16, 0, 0); }
    }

    // Vantage for judging the night sky: a mid-altitude camera tilted UP so a wide band of sky (where the
    // galaxy + nebulae live) fills the frame, with a sliver of horizon for grounding. Fly to look around.
    private void LookUpAtNightSky()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (cam == null) return;
        cam.Position = new Vector3(0, 280, 200);
        // aim straight at the galaxy core (computed from the live core az/elev) so the localized patch is
        // centred in the frame — it no longer spans the sky, so the framing must point right at it.
        float ce = Mathf.Cos(_stars.CoreElev);
        var core = new Vector3(ce * Mathf.Cos(_stars.CoreAz), Mathf.Sin(_stars.CoreElev), ce * Mathf.Sin(_stars.CoreAz));
        cam.LookAt(cam.GlobalPosition + core, Vector3.Up);
    }

    private void LookAtSun()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        var sun = GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (cam == null || sun == null) return;
        cam.Position = new Vector3(0, 400, 900);
        var toSun = sun.GlobalTransform.Basis.Z.Normalized();
        cam.LookAt(cam.GlobalPosition + toSun, Vector3.Up);
    }

    // AT-2 framing: look toward the sun's AZIMUTH but near-horizontal, so distant terrain recedes to the
    // horizon in the lower frame (where aerial haze reads) with the sky + sun glow above — at ANY time of
    // day (LookAtSun pitches up to empty sky at noon). The horizon line is centered for the AT-1 re-confirm.
    private void LookHorizonSun()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        var sun = GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (cam == null || sun == null) return;
        cam.Position = new Vector3(0, 520, 1100);
        var toSun = sun.GlobalTransform.Basis.Z.Normalized();
        // flatten the sun direction toward the horizon (keep azimuth, damp the elevation) and aim a touch
        // below horizontal so distant terrain fills the lower ~2/3 and the sky/sun sits in the upper third.
        var flat = new Vector3(toSun.X, toSun.Y * 0.12f - 0.06f, toSun.Z).Normalized();
        cam.LookAt(cam.GlobalPosition + flat, Vector3.Up);
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
        var layer = GetNodeOrNull<CanvasLayer>("/root/TerrainLabRoot/UILayer");
        if (layer != null) layer.AddChild(_reviewLabel);
        else AddChild(_reviewLabel);
    }
}
