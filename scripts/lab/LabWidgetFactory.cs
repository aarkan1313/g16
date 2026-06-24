using Godot;
using System;

namespace WG16.Lab;

/// Per-type editing-widget factory (decomposition follow-up). Builds ONE Godot control for a given control
/// type (slider/toggle/color/enum) wired to an onChanged(Variant) callback. Pure + stateless — no registry,
/// no _ready, no apply path — so it lifts cleanly out of the TerrainLabUI panel code. Used by the object-list
/// editor (the Night-tab "Sky bodies" luminary list) for its per-item field widgets. The flat-registry rows
/// have their own inline builder in BuildRow (they additionally bind c.Widget/c.Value into the registry).
public static class LabWidgetFactory
{
    /// Returns the editing Control; fires onChanged(Variant) on edit. valLabel is the value readout for
    /// slider types (null otherwise). Does NOT register into the control registry.
    public static Control Build(string type, string label, float min, float max,
        float defF, bool defBool, Color defColor, string[] options,
        Variant initial, Action<Variant> onChanged, out Label? valLabel)
    {
        valLabel = null;
        switch (type)
        {
            case "slider":
            case "scenef":
            case "float":
            {
                string fmt = (max - min) < 0.05f ? "0.0000" : "0.00";
                float start = initial.VariantType == Variant.Type.Nil ? defF : initial.AsSingle();
                var sl = new HSlider { MinValue = min, MaxValue = max, Value = start,
                    Step = (max - min) / 400.0, CustomMinimumSize = new Vector2(160, 0) };
                sl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                var vlbl = new Label { Text = start.ToString(fmt), CustomMinimumSize = new Vector2(52, 0) };
                sl.ValueChanged += v => { vlbl.Text = ((float)v).ToString(fmt); onChanged((float)v); };
                valLabel = vlbl;
                return sl;
            }
            case "toggle":
            case "scene":
            case "bool":
            {
                bool start = initial.VariantType == Variant.Type.Nil ? defBool : initial.AsBool();
                var cb = new CheckBox { ButtonPressed = start };
                cb.Toggled += on => onChanged(on);
                return cb;
            }
            case "scenecolor":
            case "color":
            {
                Color start = initial.VariantType == Variant.Type.Nil ? defColor : initial.AsColor();
                var cp = new ColorPickerButton { Color = start, CustomMinimumSize = new Vector2(160, 0), EditAlpha = false };
                cp.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                cp.ColorChanged += col => onChanged(col);
                return cp;
            }
            case "enum":
            {
                int start = initial.VariantType == Variant.Type.Nil ? (int)defF : initial.AsInt32();
                var ob = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
                ob.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                for (int i = 0; i < options.Length; i++) { ob.AddItem(options[i], i); }
                ob.Select(start);
                ob.ItemSelected += idx => onChanged((int)idx);
                return ob;
            }
        }
        return new Label { Text = $"?{type}" };
    }
}
