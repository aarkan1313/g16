using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Narrow façade over the TerrainLabUI control registry (decomposition Phase 2 linchpin). Extracted
/// satellite modules (SkyPresets, PresetsManager, LabRandomizer, LabReviewController) depend on THIS
/// rather than the whole god-class: they read/iterate controls and write values, but know nothing of
/// the panel build, the apply dispatch, or the scene. TerrainLabUI implements it over its existing
/// _byId / _controls / _ready / SetWidgetValue(Silent) fields, so behavior is unchanged.
public interface ILabControls
{
    /// The re-entry gate (TerrainLabUI._ready): suppress widget callbacks while batch-applying.
    bool IsReady { get; set; }

    /// All controls, zone/companion-expanded, in registry order.
    IReadOnlyList<LabControl> Controls { get; }

    /// The keyed registry (id, or "id#z" for zone-expanded entries) → control. Presets save/restore
    /// by these exact keys, so they need the map, not just the value list.
    IReadOnlyDictionary<string, LabControl> ById { get; }

    /// Look a control up by id (or "id#z" for zone-expanded entries).
    bool TryGet(string id, out LabControl c);

    /// Set a control's value through the canonical write path (suppresses _ready, updates the
    /// widget display, applies the effect). Equivalent to SetWidgetValue.
    void SetValue(LabControl c, Variant v);

    /// Set a control's display value WITHOUT applying the effect (day-cycle slider sync).
    /// Equivalent to SetWidgetValueSilent.
    void SetValueSilent(LabControl c, float v);
}
