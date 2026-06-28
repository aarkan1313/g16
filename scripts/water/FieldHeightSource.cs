using Erosion.Core;
using WG16.Field;

namespace WG16.Water;

// Bakes a region+halo window of WG16's REAL field_height into an Erosion.Core HeightField
// (cellSize = spacing). Row-major page (z*res+x) maps directly to HeightField.Data (y*W+x).
public static class FieldHeightSource
{
    public static HeightField Bake(FieldCompute fc, FieldParams p,
                                   float originX, float originZ, float spacing, int gridN)
    {
        float[] page = fc.ProducePage(p, originX, originZ, spacing, gridN, 0); // fieldMode 0 = full
        var hf = new HeightField(gridN, gridN, spacing);
        System.Array.Copy(page, hf.Data, page.Length);
        return hf;
    }
}
