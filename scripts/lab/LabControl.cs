using Godot;
using System;

namespace WG16.Lab;

/// One lab control: parsed registry fields (from data/lab_controls.json) + runtime widget state.
/// Lifted out of TerrainLabUI to a top-level type (decomposition Phase 2) so extracted satellite
/// modules — presets, randomizer, review — can reference it across class boundaries via ILabControls.
/// It is pure data; it holds live Godot widget refs (Widget/ValLabel/LockBox) but no behavior.
public sealed class LabControl
{
    public string Id = "", Label = "", Tab = "", Type = "";
    public string? Param, Setter, Field, Scene, Cloud;  // shader uniform / mode-setter / TerrainLab field / scene-node target / CloudVolume knob
    public float Min, Max, Default;
    public bool DefBool;
    public Color DefColor = new(1f, 1f, 1f);   // for type "scenecolor"
    public string[] Options = Array.Empty<string>();
    public bool Rand = true, Rebake;
    public int Zone = -1;                     // for material/companion (0..6), else -1
    public string? ItemSchema, DataPath;      // for type "objectlist" (U2): schema name + data array file
    public int MinItems = 1, MaxItems = 7;    // for type "objectlist": list bounds
    public Variant Value;                     // current value
    public bool Locked;
    public CheckBox? LockBox;
    public Control? Widget;                   // the editing control (slider/checkbox/dropdown)
    public Label? ValLabel;
}
