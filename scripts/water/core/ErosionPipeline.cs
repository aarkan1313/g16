using System;
using System.Collections.Generic;
namespace Erosion.Core;

// The full erosion pass the lab and WG16 export use: hydraulic droplet carve,
// then a thermal/talus slump that fills the pits the droplet pass leaves.
public static class ErosionPipeline
{
    public static ErosionResult Run(HeightField input, ErosionParams p)
    {
        var droplet = DropletErosionCpu.Run(input, p);
        var final = (p.ThermalIterations > 0)
            ? ThermalErosionCpu.Run(droplet.Eroded, p.TalusAngleDeg, p.ThermalStrength, p.ThermalIterations)
            : droplet.Eroded;

        var delta = new float[final.Data.Length];
        for (int i = 0; i < delta.Length; i++) delta[i] = final.Data[i] - input.Data[i];
        return new ErosionResult { Eroded = final, DeltaMap = delta };
    }
}
