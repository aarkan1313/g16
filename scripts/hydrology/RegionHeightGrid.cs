using Godot;
namespace WG16.Hydrology;

/// A grid of the REAL field height (field_height.glsl, the exact function the terrain uses) over a
/// region + halo, produced ONCE per region via FieldCompute and sampled bilinearly. This is the single
/// source of truth that makes water meshes drape EXACTLY where the terrain is — no proxy mismatch, no
/// floating. Same shader on CPU-request and GPU-render → they cannot diverge.
public sealed class RegionHeightGrid
{
    private readonly float[] _h;     // row-major res², real field heights (metres)
    private readonly int _res;
    private readonly float _ox, _oz; // world XZ of cell [0,0]
    private readonly float _span;    // world metres covered (region + 2*halo)
    private readonly float _cell;

    public RegionHeightGrid(WG16.Field.FieldCompute fc, WG16.Field.FieldParams p,
                            float originX, float originZ, float span, int res)
    {
        _res = res; _ox = originX; _oz = originZ; _span = span;
        _cell = span / res;
        // ProducePage samples field_height on a res×res grid at (originX,originZ) with `spacing` between
        // cells — the EXACT field the terrain vertex shader evaluates. One dispatch for the whole region.
        _h = fc.ProducePage(p, originX, originZ, _cell, res, 0);
    }

    /// Bilinearly-interpolated REAL terrain height at a world point (clamped to the grid).
    public float HeightAt(float wx, float wz)
    {
        float fx = Mathf.Clamp((wx - _ox) / _cell, 0f, _res - 1.001f);
        float fz = Mathf.Clamp((wz - _oz) / _cell, 0f, _res - 1.001f);
        int x0 = (int)fx, z0 = (int)fz;
        int x1 = x0 + 1, z1 = z0 + 1;
        float tx = fx - x0, tz = fz - z0;
        float h00 = _h[z0 * _res + x0], h10 = _h[z0 * _res + x1];
        float h01 = _h[z1 * _res + x0], h11 = _h[z1 * _res + x1];
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }
}
