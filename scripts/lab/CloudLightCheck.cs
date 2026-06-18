using Godot;
using System;

namespace WG16.Lab;

/// Numeric self-check for PER-DECK LIGHTING (roadmap #1). The user couldn't tell the decks
/// apart by eye and asked to PROVE the difference with math. This mirrors cloud_raymarch.glsl's
/// lighting block on the CPU and evaluates it for each deck's params across sun angles + optical
/// depths, then reports how quantitatively distinct cumulus vs cirrus are — and how much the
/// per-deck path differs from the legacy global path. Pure CPU (no GPU readback) so it runs
/// anywhere; the RELATIVE deltas (cumulus vs cirrus) are what matter, robust to absolute drift.
///
/// KEEP IN SYNC with cloud_raymarch.glsl's phase_dual + the multi-scatter octave loop.
public static class CloudLightCheck
{
    // hg phase — identical to the shader's hg().
    static double Hg(double cosA, double g)
    {
        double g2 = g * g;
        return (1.0 - g2) / (4.0 * Math.PI * Math.Pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
    }

    // Multi-scatter sun term for a given phase + optical depth (shader's octave loop).
    static double SunTerm(double phase, double od)
    {
        double sun = 0.0, a = 1.0, b = 1.0, c = 1.0;
        for (int o = 0; o < 3; o++)
        {
            sun += a * Math.Exp(-od * b) * (phase * c + 0.5 * (1.0 - c)); // mix(phase,0.5,1-c)
            a *= 0.5; b *= 0.55; c *= 0.5;
        }
        return sun;
    }

    // global phase = phase_dual: mix(hg(cosA,hg_aniso), hg(cosA,-0.25), 0.35)
    static double GlobalPhase(double cosA, double hgAniso) => Hg(cosA, hgAniso) * (1 - 0.35) + Hg(cosA, -0.25) * 0.35;
    // per-deck phase: mix(hg(cosA,phaseG), hg(cosA,-0.25), phaseIso)
    static double DeckPhase(double cosA, double g, double iso) => Hg(cosA, g) * (1 - iso) + Hg(cosA, -0.25) * iso;

    static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    // Scattered luminance for one deck at (cosA, baseOD). baseOD = density-driven optical depth
    // BEFORE per-deck sun_absorb (so absorb genuinely darkens cumulus cores vs cirrus).
    static (double lum, double r, double g, double b) DeckLum(
        CloudLayer L, double cosA, double baseOD, Color sunCol, double sunE, double brightness, bool perDeck, double hgAniso)
    {
        double phase = perDeck ? DeckPhase(cosA, L.PhaseG, L.PhaseIso) : GlobalPhase(cosA, hgAniso);
        double od = baseOD * (perDeck ? L.SunAbsorb : 1.0) * 0.02;
        double sun = SunTerm(phase, od);
        double albedo = perDeck ? L.Albedo : 1.0;
        double tr = perDeck ? L.TintR : 1.0, tg = perDeck ? L.TintG : 1.0, tb = perDeck ? L.TintB : 1.0;
        double r = sunCol.R * sunE * sun * albedo * tr * brightness;
        double g = sunCol.G * sunE * sun * albedo * tg * brightness;
        double b = sunCol.B * sunE * sun * albedo * tb * brightness;
        return (Luma(r, g, b), r, g, b);
    }

    public static void Run(CloudLayer cumulus, CloudLayer cirrus, Color sunCol, double sunE, double brightness, double hgAniso)
    {
        // baseOD: ~edge (thin path to sun) vs ~core (thick path). Representative, not tuned.
        double odEdge = 8.0, odCore = 120.0;
        // sun angles: forward (toward sun), side (90°), back (away).
        double fwd = 0.85, side = 0.0, back = -0.85;

        GD.Print("[lightcheck] ===== PER-DECK LIGHTING DIFFERENCE (cumulus vs cirrus) =====");
        GD.Print($"[lightcheck] cumulus: g={cumulus.PhaseG:F2} iso={cumulus.PhaseIso:F2} albedo={cumulus.Albedo:F2} absorb={cumulus.SunAbsorb:F2} tint=({cumulus.TintR:F2},{cumulus.TintG:F2},{cumulus.TintB:F2})");
        GD.Print($"[lightcheck] cirrus : g={cirrus.PhaseG:F2} iso={cirrus.PhaseIso:F2} albedo={cirrus.Albedo:F2} absorb={cirrus.SunAbsorb:F2} tint=({cirrus.TintR:F2},{cirrus.TintG:F2},{cirrus.TintB:F2})");

        // 1) luminance at edge, three sun angles, per-deck ON
        foreach (var (name, od) in new[] { ("EDGE", odEdge), ("CORE", odCore) })
        {
            var cF = DeckLum(cumulus, fwd, od, sunCol, sunE, brightness, true, hgAniso);
            var cS = DeckLum(cumulus, side, od, sunCol, sunE, brightness, true, hgAniso);
            var cB = DeckLum(cumulus, back, od, sunCol, sunE, brightness, true, hgAniso);
            var rF = DeckLum(cirrus, fwd, od, sunCol, sunE, brightness, true, hgAniso);
            var rS = DeckLum(cirrus, side, od, sunCol, sunE, brightness, true, hgAniso);
            var rB = DeckLum(cirrus, back, od, sunCol, sunE, brightness, true, hgAniso);
            GD.Print($"[lightcheck] {name} luminance  cumulus[fwd={cF.lum:F2} side={cS.lum:F2} back={cB.lum:F2} silver={SafeRatio(cF.lum, cS.lum):F2}x]");
            GD.Print($"[lightcheck] {name} luminance  cirrus [fwd={rF.lum:F2} side={rS.lum:F2} back={rB.lum:F2} silver={SafeRatio(rF.lum, rS.lum):F2}x]");
            GD.Print($"[lightcheck] {name} Δ(cumulus,cirrus): fwd {PctDiff(cF.lum, rF.lum):F0}%  side {PctDiff(cS.lum, rS.lum):F0}%  back {PctDiff(cB.lum, rB.lum):F0}%");
        }

        // 2) tint chromaticity separation (normalize out luminance, compare hue)
        var (chCx, chCy) = Chroma(cumulus.TintR, cumulus.TintG, cumulus.TintB);
        var (chRx, chRy) = Chroma(cirrus.TintR, cirrus.TintG, cirrus.TintB);
        double tintSep = Math.Sqrt((chCx - chRx) * (chCx - chRx) + (chCy - chRy) * (chCy - chRy));
        GD.Print($"[lightcheck] tint chroma separation: {tintSep * 1000:F1} (×10⁻³ rg-chroma units; >5 ~ perceptible)");

        // 3) per-deck ON vs OFF: how much does the toggle move each deck at EDGE/forward (silver lining)?
        var cOn = DeckLum(cumulus, fwd, odEdge, sunCol, sunE, brightness, true, hgAniso);
        var cOff = DeckLum(cumulus, fwd, odEdge, sunCol, sunE, brightness, false, hgAniso);
        var rOn = DeckLum(cirrus, fwd, odEdge, sunCol, sunE, brightness, true, hgAniso);
        var rOff = DeckLum(cirrus, fwd, odEdge, sunCol, sunE, brightness, false, hgAniso);
        GD.Print($"[lightcheck] per-deck ON vs OFF (edge/fwd): cumulus {PctDiff(cOn.lum, cOff.lum):F0}%   cirrus {PctDiff(rOn.lum, rOff.lum):F0}%");

        // verdict: average |Δ| between decks across the 6 luminance probes
        double[] cu = { DeckLum(cumulus, fwd, odEdge, sunCol, sunE, brightness, true, hgAniso).lum,
                        DeckLum(cumulus, side, odEdge, sunCol, sunE, brightness, true, hgAniso).lum,
                        DeckLum(cumulus, fwd, odCore, sunCol, sunE, brightness, true, hgAniso).lum };
        double[] ci = { DeckLum(cirrus, fwd, odEdge, sunCol, sunE, brightness, true, hgAniso).lum,
                        DeckLum(cirrus, side, odEdge, sunCol, sunE, brightness, true, hgAniso).lum,
                        DeckLum(cirrus, fwd, odCore, sunCol, sunE, brightness, true, hgAniso).lum };
        double avg = 0; for (int i = 0; i < 3; i++) avg += Math.Abs(PctDiff(cu[i], ci[i])); avg /= 3;
        GD.Print($"[lightcheck] VERDICT: mean luminance separation between decks = {avg:F0}%  " +
                 (avg >= 40 ? "(STRONG — decks are clearly distinct)" :
                  avg >= 20 ? "(MODERATE — distinct but could push harder)" :
                              "(WEAK — params too close; push phase/albedo/absorb apart)"));
    }

    static double SafeRatio(double a, double b) => b > 1e-6 ? a / b : 0;
    static double PctDiff(double a, double b) { double m = (a + b) * 0.5; return m > 1e-6 ? 100.0 * (a - b) / m : 0; }
    static (double, double) Chroma(double r, double g, double b)
    {
        double s = r + g + b; if (s < 1e-6) return (0.33, 0.33);
        return (r / s, g / s);
    }
}
