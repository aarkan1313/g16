using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

/// Builds a flow-mapped ribbon mesh along a river spline. Width comes from per-point flow; the strip is
/// draped into the carved channel (just above the carved bed, below the rim). UV.x = across width (0..1,
/// for the bank-foam gradient), UV.y = cumulative downstream distance (for the flow-scrolled shader).
public static class RiverRibbonMesh
{
    public static ArrayMesh Build(RiverReach r, WorldWaterRegion reg, WaterParams wp)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = r.Points.Length;
        var left = new Vector3[n]; var right = new Vector3[n]; var vlen = new float[n];
        float acc = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = r.Points[i];
            Vector2 dir = (i < n - 1 ? r.Points[i + 1] - p : p - r.Points[i - 1]).Normalized();
            Vector2 nrm = new Vector2(-dir.Y, dir.X);
            float hw = r.Width[i] * 0.5f;
            // Water surface = REAL terrain height minus a small inset, so the strip sits JUST below the real
            // ground in the carved channel (carve drops the bed by ~bedDepth at the centre; the water fills
            // most of it). reg.SurfaceYAt is the EXACT field the terrain renders → no floating.
            float terrain = reg.SurfaceYAt(p.X, p.Y);
            float bed = reg.BedAt(p.X, p.Y);
            float y = terrain - bed * 0.30f;                  // sit ~0.3*bedDepth below the real surface
            left[i] = new Vector3(p.X - nrm.X * hw, y, p.Y - nrm.Y * hw);
            right[i] = new Vector3(p.X + nrm.X * hw, y, p.Y + nrm.Y * hw);
            if (i > 0) acc += r.Points[i].DistanceTo(r.Points[i - 1]);
            vlen[i] = acc / 40f;                              // 1 UV unit per 40 m downstream
        }
        for (int i = 0; i < n - 1; i++)
            AddQuad(st, left[i], right[i], left[i + 1], right[i + 1], vlen[i], vlen[i + 1]);
        st.GenerateNormals();
        return st.Commit();
    }

    private static void AddQuad(SurfaceTool st, Vector3 l0, Vector3 r0, Vector3 l1, Vector3 r1, float v0, float v1)
    {
        st.SetUV(new Vector2(0, v0)); st.AddVertex(l0);
        st.SetUV(new Vector2(1, v0)); st.AddVertex(r0);
        st.SetUV(new Vector2(0, v1)); st.AddVertex(l1);
        st.SetUV(new Vector2(1, v0)); st.AddVertex(r0);
        st.SetUV(new Vector2(1, v1)); st.AddVertex(r1);
        st.SetUV(new Vector2(0, v1)); st.AddVertex(l1);
    }
}
