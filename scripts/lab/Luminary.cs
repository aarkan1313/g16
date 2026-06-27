using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// CELESTIAL C3 — the N-luminary data model + priority-budgeting allocator.
/// Spec: docs/superpowers/specs/2026-06-21-celestial-c3-n-luminaries-design.md.
///
/// A `Luminary` is one sky body (sun OR moon) with its own arc, appearance, and lighting capability.
/// The single-sun + single-moon look becomes a `List<Luminary>` of exactly two entries (one Sun, one
/// Moon) that reproduce today's look; adding entries is the N-sun/N-moon feature. Pure data — no Godot
/// scene state, no scene writes (LightingComposer remains the one writer), matching LightingState.cs.
///
/// "3 suns without killing the computer" lives in `LuminaryBudget`: the scarce hardware (up to four
/// Godot directional lights and three atmosphere-scattered suns) is allocated to the highest-priority
/// luminaries; everyone else renders as a cheap disc + summed ambient tint.

public enum LuminaryKind { Sun, Moon }

/// Disc surface mottle — sun-class granules / moon-class maria. Mirrors the existing sun_surface_* /
/// moon_surf_* uniforms so a Luminary is a superset of SunDiscState + MoonState surface fields.
public sealed class SurfaceParams
{
    public float Cells = 8f, Contrast = 0.0f, Spots = 0.0f, Churn = 0.0f, Warm = 0.3f;
    public SurfaceParams Clone() => (SurfaceParams)MemberwiseClone();
}

/// One sky body. Collapses TimeState's arc params + SunDiscState (sun appearance) + MoonState (moon
/// appearance) into a per-body record, plus the capability/priority flags the budgeter reads.
public sealed class Luminary
{
    public string Id = "luminary";
    public LuminaryKind Kind = LuminaryKind.Sun;

    // ── ARC (own celestial path; the existing sun/moon arc math, parameterized). Hours / degrees. ──
    public float TimeOffsetH = 0f;     // hours added to time-of-day before evaluating the arc (moon phase-lag = phase*12)
    public float PeakElev = 60f;       // peak elevation at this body's solar-noon (deg)
    public float AzStart = 90f, AzEnd = 270f;   // azimuth sweep endpoints (deg)
    public float DeclScale = 1.0f;     // peak-elevation scale vs the base arc (own declination)
    public float ElevOffset = 0f, AzOffset = 0f;   // rigid offsets to push the whole path off another body's

    // ── APPEARANCE (disc; superset of sun_* / moon_* uniforms) ──
    public Color Color = new(1f, 0.95f, 0.86f);   // light + disc tint
    public float Size = 0.6f, Limb = 0.55f;       // angular radius (deg), limb darkening
    public float DiscEnergy = 1.3f;               // disc brightness
    public float CoronaSize = 1200f, CoronaEnergy = 2.0f;   // sun-class inner bloom
    public float HaloSize = 90f, HaloEnergy = 0.4f;         // wide glow (both classes)
    public float Phase = 1.0f;        // moon-class: 0 new .. 0.5 half .. 1 full (terminator + lag + illum)
    public SurfaceParams Surface = new();
    public float Redden = 1.0f, ReddenOnset = 0.25f, HorizonGrow = 0.6f, CloudRedden = 0.8f;  // sun-class low-elev warm shift

    // ── CAPABILITY + PRIORITY (the budgeter's inputs) ──
    public bool IsPhysicalLight = true;       // wants a real Godot LIGHTn (terrain direct light)? else disc + ambient tint only
    public bool ContributesToAtmosphere = true;   // summed into the skyview/aerial LUT raymarch? (Sun-class)
    public float Priority = 1.0f;             // higher = wins scarce resources first (brightness × narrative weight)
    public float LightEnergy = 1.3f;          // terrain direct-light energy (the caller still gates moon-class by night/up/phase)
    public Color LightColor = new(1f, 0.95f, 0.86f);
    public float CloudLight = 1.0f;           // strength of this body's light on the cloud raymarch

    public Luminary Clone()
    {
        var c = (Luminary)MemberwiseClone();
        c.Surface = Surface.Clone();   // deep-copy the only reference field
        return c;
    }
}

/// Scarce-resource budget. Data (not constants) so a low-end profile can drop atmosphere suns
/// without code edits. Defaults match the spec's table.
public sealed class LuminaryCaps
{
    public int MaxPhysicalLights = 4;   // Godot sky shaders read at most LIGHT0..LIGHT3 — HARD engine limit
    public int MaxAtmosphereSuns = 3;   // summed inside the ONE skyview raymarch (+~10-20% per sun, not a re-dispatch)
    public int MaxDiscs = 16;           // sky-shader disc-uniform array size

    public LuminaryCaps Clone() => (LuminaryCaps)MemberwiseClone();
}

/// The per-luminary grants the budgeter produces (parallel arrays, indexed like the input list).
public sealed class LuminaryAllocation
{
    public int[] LightSlot;     // 0..MaxPhysicalLights-1 hardware-light index, or -1 (disc-only → ambient tint)
    public bool[] Atmosphere;   // summed into the atmosphere LUT?
    public int[] Disc;          // 0..MaxDiscs-1 disc-array slot, or -1 (not drawn)
    public List<string> Notes;  // human-readable demotions (logged — no silent caps, per the audit rule)

    public LuminaryAllocation(int n)
    {
        LightSlot = new int[n]; Atmosphere = new bool[n]; Disc = new int[n];
        for (int i = 0; i < n; i++) { LightSlot[i] = -1; Disc[i] = -1; }
        Notes = new List<string>();
    }
}

/// The "3 suns ≈ today" allocator. PURE function: ranks luminaries by `weight` (= Priority × current
/// visibility, computed by the caller from each body's elevation) and hands out the scarce resources
/// top-first. Every overflow (a luminary that WANTED a resource but didn't get it) is recorded in Notes
/// so the demotion is logged, never silent.
public static class LuminaryBudget
{
    /// <param name="lums">the luminaries (any count)</param>
    /// <param name="weights">per-luminary rank weight (Priority × visibility); same length as lums</param>
    /// <param name="caps">the scarce-resource budget</param>
    public static LuminaryAllocation Allocate(IReadOnlyList<Luminary> lums, IReadOnlyList<float> weights, LuminaryCaps caps)
    {
        int n = lums.Count;
        var a = new LuminaryAllocation(n);
        if (n == 0) { return a; }

        // Rank highest-weight first; stable on ties (preserve list order) so allocation is deterministic.
        var order = new List<int>(n);
        for (int i = 0; i < n; i++) { order.Add(i); }
        order.Sort((x, y) =>
        {
            int c = weights[y].CompareTo(weights[x]);   // descending weight
            return c != 0 ? c : x.CompareTo(y);          // stable: lower index first
        });

        int lightsUsed = 0, atmoUsed = 0, discsUsed = 0;
        foreach (int i in order)
        {
            var L = lums[i];

            // Disc: every visible body wants to be drawn; beyond MaxDiscs it isn't (logged).
            if (discsUsed < caps.MaxDiscs) { a.Disc[i] = discsUsed++; }
            else { a.Notes.Add($"'{L.Id}' not drawn (disc cap {caps.MaxDiscs} reached)"); }

            // Physical light slot: only if it wants one and a slot is free; else disc-only + ambient tint.
            if (L.IsPhysicalLight)
            {
                if (lightsUsed < caps.MaxPhysicalLights) { a.LightSlot[i] = lightsUsed++; }
                else { a.Notes.Add($"'{L.Id}' demoted to disc-only (light cap {caps.MaxPhysicalLights} reached → ambient tint)"); }
            }

            // Atmosphere scatter: suns only; top MaxAtmosphereSuns. Others contribute ambient/tint, no scatter.
            if (L.ContributesToAtmosphere && L.Kind == LuminaryKind.Sun)
            {
                if (atmoUsed < caps.MaxAtmosphereSuns) { a.Atmosphere[i] = true; atmoUsed++; }
                else { a.Notes.Add($"'{L.Id}' not atmosphere-scattered (atmo-sun cap {caps.MaxAtmosphereSuns} reached → ambient tint only)"); }
            }
        }
        return a;
    }
}
