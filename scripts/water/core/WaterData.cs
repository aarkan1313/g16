using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Engine-agnostic result of a water solve over one heightfield tile (region+halo in
// the infinite world). The transfer boundary: zero Godot types.
public sealed class WaterData
{
    public required int Width;
    public required int Height;
    public required float CellSizeM;
    public required HeightField Carved;   // terrain with river/lake channels cut
    public required float[] Filled;       // water-surface level per cell (m)
    public required bool[] Wet;           // Filled - Carved > MinDepth
    public required float[] FlowX;        // smoothed flow vector X per cell (length = speed)
    public required float[] FlowZ;        // smoothed flow vector Z per cell (0 = still/lake)
    public required float[] Foam;         // smoothed foam factor per cell (0..1)
    public required LakeSet Lakes;
    public required RiverNetwork.RiverEdge[] Rivers;
}
