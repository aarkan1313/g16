using System;
using System.Collections.Generic;
namespace Erosion.Core;

public sealed record WaterParams(
    float MinDepth = 0.3f,        // wet threshold (m): Filled - Carved > MinDepth
    float RiverThreshold = 8000f, // flow-accum above which a cell is a river
    float ChannelDepth = 4f,      // carve depth (flow-graded inside ChannelCarve)
    float WidthScale = 4f,        // carve width scale
    float FoamSlope = 0.35f,      // downstream slope mapped to foam factor
    int FlowSmoothIters = 8,      // box-blur passes on the flow field
    int FoamSmoothIters = 4,      // box-blur passes on the foam field
    // Drainage conditioning (Hydrology.Condition). 0 depth = off → fill-only (legacy/lab).
    float MaxBreachDepth = 0f,    // max notch depth (m) a basin may be breached; deeper basins stay lakes
    int MaxBreachLength = 200,    // max cells to search for a basin's outlet along the spill route
    int MinLakeArea = 0);         // cull lakes smaller than this many cells (noise puddles); 0 = keep all
