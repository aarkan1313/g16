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
    private List<string> _palNames;
    private Dictionary<string, string[]> _palRoles;
    private int _palIdx;

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
        string title, judge;
        switch (n)
        {
            case 1: // Sun disc — Stage 1 (NEEDS_REVIEW 3b)
                ApplyMood(0);                                   // golden hour = low warm sun
                Set("cloud_enabled", true); Set("cloud_coverage", 0.55f);
                Set("sun_size", 1.4f); Set("sun_corona_energy", 1.0f);
                Set("sun_halo_energy", 1.0f); Set("sun_redden", 1.0f);
                title = "1 · Sun disc (Stage 1)";
                judge = "Believable glowing sun — limb-darkened disc, tight corona, soft warm halo, horizon redden+grow? Raise cloud coverage to drift a cloud across it (dim+redden). No hard ring/banding.";
                break;
            case 2: // Lighting decouple + time-of-day — Stage 2 (3c)
                ApplyMood(5);                                   // clear alpine = neutral
                Set("cloud_enabled", true); Set("cloud_coverage", 0.40f);
                Set("time_of_day", 12.0f);
                title = "2 · Time-of-day / daylight (Stage 2)";
                judge = "Scrub Light tab 'time of day' 5->19: sun arc low-E -> high -> low-W, cohesive sky/light/ambient shift, no pops? Switch the 6 moods (dropdown) — do they still match their old looks?";
                break;
            case 3: // Ground GM1 — palette (1c)
                BaselineGround();
                CyclePalette(advance: _lastPreset == 3);
                string palName = (_palNames != null && _palNames.Count > 0) ? _palNames[_palIdx] : "?";
                title = $"3 · GM1 palette  [{palName}]  (press 3 again to cycle)";
                judge = "Reads photoreal/varied across cliffs/peaks under neutral light, not drab? Press 3 to A/B palettes. Isolate drab = lighting vs material saturation vs placement.";
                break;
            case 4: // Ground GM2 — real height + POM (1c)
                BaselineGround();
                Set("height_from_maps", true); Set("pom_on", true);
                title = "4 · GM2 real height + POM";
                judge = "Real crevice/relief depth under motion+light (not just 'raised a little')? No swimming/artifacts. Toggle 'real height (GM2)' / 'surface depth (POM)' (Detail tab) to A/B.";
                break;
            case 5: // Ground GM3-A — within-area variation (1c)
                BaselineGround();
                Set("variation_on", true);
                title = "5 · GM3-A within-area variation";
                judge = "On a uniform slope: stops reading uniform — drier-lighter-rougher vs damper-darker-smoother patches, organic, no squares/shimmer? Far ~unchanged. Toggle 'within-area variation' (Color tab).";
                break;
            case 6: // Clouds — feature review (5)
                ApplyMood(5);
                Set("cloud_enabled", true); Set("cloud_coverage", 0.50f);
                title = "6 · Clouds (feature review)";
                judge = "Cloud shape/lighting/motion read good? Use Clouds tab (coverage/type/presets/decks). Distant-sky/horizon is a known soft spot. Checklist: docs/cloud-next-steps.md.";
                break;
            case 7: // God rays — final pass (3)
                ApplyMood(0);                                   // low sun
                Set("cloud_enabled", true); Set("cloud_coverage", 0.72f);
                Set("cloud_godrays", true);
                title = "7 · God rays";
                judge = "Believable sun-through-cloud shafts (crisp, not uniform fog, not washing the scene)? Needs a cloud actually crossing the sun (coverage is high). Tune in Clouds tab.";
                break;
            case 8: // GI / SDFGI default — DECISION LANDED 2026-06-20 (0b / 2)
                ApplyMood(5);
                Set("cloud_enabled", false);
                Set("sdfgi_on", false); Set("gi_proxy", false);   // the approved artifact-free baseline
                title = "8 · GI / SDFGI — DECISION LANDED: off + proxy off";
                judge = "Approved default: SDFGI OFF + GI proxy OFF (sharp shadows, no cascade box, ~4.7 ms). To see WHY: Light tab 'GI (SDFGI)' ON = the moving bright cascade box that brightens as you approach. Re-evaluate GI when flora / erosion canyons / night land.";
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

    // --- helpers -------------------------------------------------------------

    /// Set a control to a value by its lab_controls.json id, routed through the registry so it
    /// reaches the shader/scene/cloud (not just the widget). Warns on an unknown id.
    private void Set(string id, Variant v)
    {
        if (_byId.TryGetValue(id, out var c)) SetWidgetValue(c, v);
        else GD.PushWarning($"[review] unknown control id '{id}' (skipped)");
    }

    /// Approved-era ground baseline: neutral midday light, clouds off, all new GM features off.
    private void BaselineGround()
    {
        ApplyMood(2);                                           // midday = neutral
        Set("cloud_enabled", false);
        Set("variation_on", false);
        Set("height_from_maps", false);
        Set("pom_on", false);
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

    private void LookAtSun()
    {
        var cam = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        var sun = GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (cam == null || sun == null) return;
        cam.Position = new Vector3(0, 400, 900);
        var toSun = sun.GlobalTransform.Basis.Z.Normalized();
        cam.LookAt(cam.GlobalPosition + toSun, Vector3.Up);
    }

    private void CyclePalette(bool advance)
    {
        if (_palRoles == null) LoadPalettes();
        if (_palNames == null || _palNames.Count == 0) return;
        if (advance) _palIdx = (_palIdx + 1) % _palNames.Count;
        var name = _palNames[_palIdx];
        if (_palRoles.TryGetValue(name, out var roles) && _terrain != null)
            for (int z = 0; z < roles.Length && z < 7; z++)
                _terrain.SetZoneMaterial(z, roles[z]);
    }

    private void LoadPalettes()
    {
        _palNames = new List<string>();
        _palRoles = new Dictionary<string, string[]>();
        using var f = Godot.FileAccess.Open("res://data/ground_palette.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) return;
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return;
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("palettes")) return;
        var pals = root["palettes"].AsGodotDictionary();
        foreach (var key in pals.Keys)
        {
            var name = key.AsString();
            var roleArr = pals[key].AsGodotDictionary();
            if (!roleArr.ContainsKey("roles")) continue;
            var arr = roleArr["roles"].AsGodotArray();
            var roles = new string[arr.Count];
            for (int i = 0; i < arr.Count; i++) roles[i] = arr[i].AsString();
            _palNames.Add(name);
            _palRoles[name] = roles;
        }
        var active = root.ContainsKey("active") ? root["active"].AsString() : "";
        var ai = _palNames.IndexOf(active);
        if (ai >= 0) _palIdx = ai;
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
