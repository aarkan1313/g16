using Erosion.Core;

namespace WG16.Water;

// Godot adapter: GPU droplet+thermal erosion behind IEroder. Mirrors the old
// Main.GpuErode (GpuErosion.Run + Box smoothing). Transfers to WG16 unchanged.
public sealed class GpuEroder : IEroder
{
    public HeightField Erode(HeightField baseField, ErosionParams ep)
        => Smoothing.Box(GpuErosion.Run(baseField, ep), radius: 2, iterations: 2);
}
