using Godot;

namespace WG16.Lab;

/// The distinct shadow responsibilities. INVARIANT: at most one ENABLED owner per slot (the registry
/// enforces/reports it). Blending across slots is the registry's job, not an owner's. Future decorations
/// add an owner (likely into NearCast via an object CSM), not a new slot.
public enum ShadowSlot
{
    NearCast,   // crisp near-band cast shadows (CSM) — terrain + future objects
    FarCast,    // world-anchored far-band terrain cast shadow (horizon-map / today's march)
    ContactAo,  // small-radius ambient crevice/valley darkening
}

/// One pluggable shadow contributor. Phase 0 ships the abstraction + one real owner (the existing
/// horizon march). Later phases add owners; the registry governs them uniformly.
public interface IShadowOwner
{
    string Name { get; }          // stable short id, e.g. "horizon-march"
    ShadowSlot Slot { get; }      // the single slot this owner occupies
    bool Enabled { get; set; }    // user/registry intent
    bool IsActive { get; }        // Enabled AND actually contributing this frame
    System.Collections.Generic.IReadOnlyList<string> DiagIds { get; }   // prefixes of the ShadowDiagnostics owner tags this owner accounts for
    void Tick(double delta);      // per-frame hook (Phase 0 owners may no-op)
}
