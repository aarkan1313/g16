using Godot;
using System.Linq;

namespace WG16.Lab;

/// --shadowcheck: assert the registry (authority) and ShadowDiagnostics (independent auditor) AGREE.
/// PASS iff: (1) the registry's one-owner-per-slot invariant holds, and (2) every shadow owner the auditor
/// finds in the live scene is one the registry declares active (a prefix match) — i.e. NO rogue/undeclared
/// caster. Prints a SHADOWCHECK line; returns the pass bool (caller maps to exit 0/1 for CI).
public static class ShadowCheck
{
    public static bool Run(ShadowRegistry registry, Node host)
    {
        bool inv = registry.CheckInvariants(out string invReport);

        ShadowDiagnostics.Snapshot snap = ShadowDiagnostics.Capture(host);
        var declared = registry.ActiveDiagIds();

        // Auditor owner tags look like "kind:/path" or "terrain:horizon:/path". A tag is legitimate if some
        // declared DiagId is a prefix of it; otherwise it is a rogue/undeclared caster.
        string[] auditor = snap.Owners == "none" ? System.Array.Empty<string>() : snap.Owners.Split(',');
        string[] rogue = auditor.Where(a => !declared.Any(d => a.StartsWith(d))).ToArray();

        bool pass = inv && rogue.Length == 0;
        GD.Print($"SHADOWCHECK: pass={(pass ? "YES" : "NO")} invariants={(inv ? "OK" : invReport)} " +
                 $"declared=[{string.Join(",", declared)}] rogue=[{string.Join(",", rogue)}] " +
                 $"diag=({snap.ToProfileLine("PROFILE-SHADOWS")})");
        return pass;
    }
}
