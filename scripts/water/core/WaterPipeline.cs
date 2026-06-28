using System;
using System.Collections.Generic;
namespace Erosion.Core;

// The proven water chain as one engine-agnostic call. Split so the expensive,
// param-independent Hydrology.Compute can be cached by the caller (instant knob
// re-tuning): callers cache the WaterMap and call Build; Solve is the full convenience.
public static class WaterPipeline
{
    public static WaterData Build(WaterMap wm, HeightField eroded, WaterParams p)
    {
        var ls = WaterBodies.Compute(wm, p.MinDepth);
        var edges = RiverNetwork.Extract(wm, ls, p.RiverThreshold);
        var carved = ChannelCarve.Apply(eroded, wm.Filled, edges, p.ChannelDepth, p.WidthScale, ls.LakeId);
        var (wet, fx, fz, foam) = FlowField.Compute(wm, ls, carved, p);
        return new WaterData
        {
            Width = eroded.Width, Height = eroded.Height, CellSizeM = eroded.CellSizeM,
            Carved = carved, Filled = wm.Filled, Wet = wet,
            FlowX = fx, FlowZ = fz, Foam = foam, Lakes = ls, Rivers = edges,
        };
    }

    public static WaterData Solve(HeightField eroded, WaterParams p)
        => Build(Hydrology.Compute(eroded), eroded, p);
}
