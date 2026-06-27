using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WG16.Lab;

/// THE single authority for terrain shadows. Owns the pluggable owners, enforces/reports the hard
/// invariants (one ENABLED owner per slot; nothing casts except a registered owner), ticks them, and
/// exposes its declared owners so the independent ShadowDiagnostics auditor can be cross-checked against
/// it (--shadowcheck). Phase 0: framework + the existing horizon march as the first owner (no visual change).
public sealed class ShadowRegistry
{
    private readonly Node _host;
    private readonly List<IShadowOwner> _owners = new();

    public ShadowRegistry(Node host) { _host = host; }

    public IReadOnlyList<IShadowOwner> Owners => _owners;

    /// Register an owner once. Warns (does not throw) on a slot collision — the invariant is REPORTED by
    /// CheckInvariants and GATED by --shadowcheck, not crashed at boot.
    public void Register(IShadowOwner owner)
    {
        if (!_owners.Contains(owner)) { _owners.Add(owner); }
    }

    public void Tick(double delta)
    {
        foreach (IShadowOwner o in _owners) { if (o.Enabled) { o.Tick(delta); } }
    }

    /// Invariant 2: at most one ENABLED owner per slot. False + a report naming any conflict.
    public bool CheckInvariants(out string report)
    {
        var conflicts = _owners.Where(o => o.Enabled)
                               .GroupBy(o => o.Slot)
                               .Where(g => g.Count() > 1)
                               .ToList();
        if (conflicts.Count == 0) { report = "one-owner-per-slot OK"; return true; }
        var sb = new StringBuilder("slot conflicts: ");
        foreach (var g in conflicts) { sb.Append($"{g.Key}=[{string.Join(",", g.Select(o => o.Name))}] "); }
        report = sb.ToString().TrimEnd();
        return false;
    }

    /// DiagId prefixes of currently-active owners — what ShadowDiagnostics SHOULD attribute to the registry.
    public HashSet<string> ActiveDiagIds()
        => _owners.Where(o => o.IsActive).Select(o => o.DiagId).ToHashSet();
}
