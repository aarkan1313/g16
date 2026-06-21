using Godot;

namespace WG16.Lab;

/// GROUND-LANE review bank — Shift+1..9. The plain number keys 1..9 (TerrainLabUI.Review.cs) are the
/// shared/light eye-gate items and are filling up, so the ground lane gets its own bank on Shift+N.
/// Implemented via _ShortcutInput (fires BEFORE the plain-digit _UnhandledInput), so Shift+digit is
/// caught + consumed here while a bare digit falls straight through to ApplyReview untouched. Kept in
/// its own partial file so this lane never edits the shared Review.cs. Active only when ReviewMode.
///
/// DATA-DRIVEN: slots are defined in data/ground_review.json (reloaded each press for live tuning).
/// The ground-v2 verification bank lives there. A slot NOT in the JSON falls back to the hardcoded
/// switch below (the old anti-tiling A/B on Shift+3).
public partial class TerrainLabUI : Control
{
    private bool _gTileHisto;   // Shift+3 A/B state (hardcoded fallback): false = IQ 2-tap (1), true = histogram (3)
    private int _g0Step;        // review-key-3 cumulative isolation step for the G-0 cluster (0=current .. 5=full fixed)
    private bool _gReviewNewOn; // ground-v2 A/B state (JSON 'ab' slots): true = NEW path shown
    private int _gReviewLastKey = -1;   // last Shift+N pressed (so re-pressing an 'ab' slot flips NEW↔OLD)

    public override void _ShortcutInput(InputEvent @event)
    {
        if (!ReviewMode || _byId == null || _byId.Count == 0) return;
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.ShiftPressed)
        {
            int n = k.Keycode switch
            {
                Key.Key1 => 1, Key.Key2 => 2, Key.Key3 => 3, Key.Key4 => 4, Key.Key5 => 5,
                Key.Key6 => 6, Key.Key7 => 7, Key.Key8 => 8, Key.Key9 => 9, _ => 0
            };
            if (n == 0) return;
            ApplyGroundReview(n);
            GetViewport().SetInputAsHandled();
        }
    }

    private void ApplyGroundReview(int n)
    {
        if (_reviewLabel == null) BuildReviewLabel();
        var st = FindGroundReviewState(n);   // reloads data/ground_review.json each press (live edits apply)
        if (st != null) { ApplyGroundReviewState(n, st); return; }
        ApplyGroundReviewHardcoded(n);       // slots not in the JSON keep their old behaviour
    }

    /// Apply one ground-v2 verification state from data/ground_review.json.
    private void ApplyGroundReviewState(int n, Godot.Collections.Dictionary st)
    {
        string label = st.ContainsKey("label") ? st["label"].AsString() : $"ground ⇧{n}";
        string judge = st.ContainsKey("judge") ? st["judge"].AsString() : "";
        float time = st.ContainsKey("time") ? (float)st["time"].AsDouble() : 16f;
        bool ab = st.ContainsKey("ab") && st["ab"].AsBool();
        int dbg = st.ContainsKey("gv2_debug") ? st["gv2_debug"].AsInt32() : 0;

        BaselineGround();                       // clouds off, neutral midday, old GM features off
        _timeRunning = false;                   // LOCK the sun so the A/B isn't confounded by drifting light
        Set("time_of_day", time);
        _terrain.PrewarmGroundV2();             // build the v2 arrays now so any toggle is instant (no bake hitch)

        bool newOn;
        if (ab)
        {
            _gReviewNewOn = (_gReviewLastKey == n) ? !_gReviewNewOn : true;   // re-press flips; new key starts NEW
            newOn = _gReviewNewOn;
        }
        else
        {
            newOn = !st.ContainsKey("groundv2") || st["groundv2"].AsBool();   // default NEW
        }
        _terrain.SetGroundV2(newOn);
        _terrain.SetGv2Debug(dbg);
        if (st.ContainsKey("cam")) { GReviewCam(st["cam"].AsGodotArray()); }

        string tag = ab ? (newOn ? "  [NEW]" : "  [OLD]") : "";
        _reviewLabel.Text = $"GROUND ⇧{n} · {label}{tag}\n{judge}";
        GD.Print($"[ground-review] ⇧{n} {label}{tag}");
        _gReviewLastKey = n;
    }

    /// Find the JSON state for Shift+n (reloaded each call so live edits to data/ground_review.json apply).
    private Godot.Collections.Dictionary FindGroundReviewState(int n)
    {
        using var f = Godot.FileAccess.Open("res://data/ground_review.json", Godot.FileAccess.ModeFlags.Read);
        if (f == null) { return null; }
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) { return null; }
        var root = parsed.AsGodotDictionary();
        if (!root.ContainsKey("states")) { return null; }
        foreach (Variant v in root["states"].AsGodotArray())
        {
            var s = v.AsGodotDictionary();
            if (s.ContainsKey("key") && s["key"].AsInt32() == n) { return s; }
        }
        return null;
    }

    /// Move the review camera to [x, y, z, pitchDeg, yawDeg].
    private void GReviewCam(Godot.Collections.Array cam)
    {
        if (cam == null || cam.Count < 5) { return; }
        var c = GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera");
        if (c == null) { return; }
        c.Position = new Vector3((float)cam[0].AsDouble(), (float)cam[1].AsDouble(), (float)cam[2].AsDouble());
        c.RotationDegrees = new Vector3((float)cam[3].AsDouble(), (float)cam[4].AsDouble(), 0f);
    }

    // Hardcoded fallback for slots not defined in data/ground_review.json.
    private void ApplyGroundReviewHardcoded(int n)
    {
        switch (n)
        {
            case 3: // GM1 surface — AAA anti-tiling A/B (OLD path): IQ 2-tap (blocky) <-> histogram-preserving
                _gTileHisto = !_gTileHisto;
                Set("tile_mode", _gTileHisto ? 3 : 1);
                _reviewLabel.Text =
                    $"GROUND ⇧3 · Anti-tiling: {(_gTileHisto ? "HISTOGRAM (AAA)" : "IQ 2-tap (old, blocky)")}\n" +
                    "Fly CLOSE to a cliff/slope: blocky 28 m seams gone? materials keep contrast (not washed)? " +
                    "no obvious repetition (vs Surface→'none')? no shimmer in motion? Toggle ⇧3 in motion. Perf top-right.";
                GD.Print($"[ground-review] anti-tiling -> {(_gTileHisto ? "histogram (3)" : "IQ (1)")}");
                break;
            default:
                _reviewLabel.Text = $"GROUND ⇧{n} · (unassigned — see data/ground_review.json)";
                break;
        }
    }

    /// Switch the 7 zone materials to a named palette from ground_palette.json (live, no restart).
    /// Mirrors Review.cs CyclePalette but targets a specific palette by name.
    private void ApplyPaletteByName(string name)
    {
        if (_palRoles == null) { LoadPalettes(); }
        if (_palRoles != null && _palRoles.TryGetValue(name, out var roles) && _terrain != null)
        {
            for (int z = 0; z < roles.Length && z < 7; z++) { _terrain.SetZoneMaterial(z, roles[z]); }
        }
    }
}
