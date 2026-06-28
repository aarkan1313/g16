using System;
using System.Collections.Generic;
namespace Erosion.Core;

// Erosion behind an interface so the GPU implementation (Godot) stays out of the pure
// layers. Core ships a CPU implementation for tests; the lab/engine inject the GPU one.
public interface IEroder
{
    HeightField Erode(HeightField baseField, ErosionParams ep);
}

public sealed class CpuEroder : IEroder
{
    public HeightField Erode(HeightField baseField, ErosionParams ep)
        => Smoothing.Box(ErosionPipeline.Run(baseField, ep).Eroded, radius: 2, iterations: 2);
}
