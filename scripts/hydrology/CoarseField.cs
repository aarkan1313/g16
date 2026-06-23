using Godot;
using WG16.Field;

namespace WG16.Hydrology;

/// A small, coarse height grid over a region + halo, plus index<->world mapping. The DrainageGraph's ONLY
/// base-field dependency: it exposes plain arrays so the graph builder stays pure C# (no RD/rendering).
public sealed class CoarseField
{
    public readonly int Res;
    public readonly float Spacing, OriginX, OriginZ;
    private readonly float[] _h;   // row-major z*Res+x

    public CoarseField(float[] heights, int res, float originX, float originZ, float spacing)
    { _h = heights; Res = res; OriginX = originX; OriginZ = originZ; Spacing = spacing; }

    public float H(int x, int z)
    { x = Mathf.Clamp(x, 0, Res - 1); z = Mathf.Clamp(z, 0, Res - 1); return _h[z * Res + x]; }

    public (float wx, float wz) World(int x, int z) => (OriginX + x * Spacing, OriginZ + z * Spacing);

    /// Sample the base field over [originX,originZ] + halo at coarse spacing. Deterministic: same args => same grid.
    public static CoarseField Build(FieldCompute fc, FieldParams p, float originX, float originZ,
                                    float spacing, int res)
    {
        float[] h = fc.ProducePage(p, originX, originZ, spacing, res, 0);   // row-major z*res+x
        return new CoarseField(h, res, originX, originZ, spacing);
    }
}
