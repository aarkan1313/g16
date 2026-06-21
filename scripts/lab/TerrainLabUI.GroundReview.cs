using Godot;

namespace WG16.Lab;

/// GROUND-LANE review bank — Shift+1..9. The plain number keys 1..9 (TerrainLabUI.Review.cs) are the
/// shared/light eye-gate items and are filling up, so the ground lane gets its own bank on Shift+N.
/// Implemented via _ShortcutInput (fires BEFORE the plain-digit _UnhandledInput), so Shift+digit is
/// caught + consumed here while a bare digit falls straight through to ApplyReview untouched. Kept in
/// its own partial file so this lane never edits the shared Review.cs. Active only when ReviewMode.
public partial class TerrainLabUI : Control
{
    private bool _gTileHisto;   // Shift+3 A/B state: false = IQ 2-tap (1), true = histogram (3)
    private bool _gFixedDefaults;   // Shift+1 A/B state: false = current (drab) defaults, true = G-0 "wrong-defaults" cluster

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
        switch (n)
        {
            case 1: // G-0 "wrong-defaults" A/B — flip the whole suppressed-good-tech cluster as one before/after.
            {
                _gFixedDefaults = !_gFixedDefaults;
                bool g0 = _gFixedDefaults;
                Set("rough_floor", g0 ? 0.15f : 0.5f);   // #1 lever: unclamp roughness (was pinned 0.5 = dead-matte)
                Set("tex_scale_m", g0 ? 11f : 28f);       // un-stretch textures (~3x finer; anti-tiling hides the repeat)
                Set("height_from_maps", g0);              // bind GM2 real Poisson relief
                Set("variation_on", g0);                  // GM3-A within-area variation
                ApplyPaletteByName(g0 ? "alpine_stone" : "alpine_green");  // contrast-rich vs the drab prior default
                _reviewLabel.Text =
                    $"GROUND ⇧1 · 'WRONG-DEFAULTS' A/B: {(g0 ? "FIXED (rough0.15·texscale11·real-height·variation·alpine_stone)" : "CURRENT (drab baseline)")}\n" +
                    "Fly CLOSE/MID under settled light. Does the ground gain specular/sheen + relief + color variety + crisper texture? " +
                    "WATCH for specular shimmer/fuzz as roughness unclamps (the floor was hiding it) — if it appears that's the bisect target, not a re-clamp. Toggle ⇧1 in motion.";
                GD.Print($"[ground-review] G-0 wrong-defaults -> {(g0 ? "FIXED" : "CURRENT")}");
                break;
            }
            case 3: // GM1 surface — AAA anti-tiling A/B: IQ 2-tap (old, blocky) <-> histogram-preserving
                _gTileHisto = !_gTileHisto;
                Set("tile_mode", _gTileHisto ? 3 : 1);
                _reviewLabel.Text =
                    $"GROUND ⇧3 · Anti-tiling: {(_gTileHisto ? "HISTOGRAM (AAA)" : "IQ 2-tap (old, blocky)")}\n" +
                    "Fly CLOSE to a cliff/slope: blocky 28 m seams gone? materials keep contrast (not washed)? " +
                    "no obvious repetition (vs Surface→'none')? no shimmer in motion? Toggle ⇧3 in motion. Perf top-right.";
                GD.Print($"[ground-review] anti-tiling -> {(_gTileHisto ? "histogram (3)" : "IQ (1)")}");
                break;
            default:
                _reviewLabel.Text = $"GROUND ⇧{n} · (unassigned — ground-lane bank)";
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
