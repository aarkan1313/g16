using Godot;
namespace WG16.Hydrology;

/// Builds a flat triangulated disc at the lake's water level (a fan). UV.x maps centre→edge so the water
/// shader's bank-foam gradient works on lakes too.
public static class LakeMesh
{
    public static ArrayMesh Build(Lake lk, WorldWaterRegion reg)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int seg = 24;
        // Lake surface sits just below the REAL terrain at the basin centre (the lowest point), so it pools
        // in the depression instead of floating at a proxy level.
        float level = reg.SurfaceYAt(lk.Center.X, lk.Center.Y) + 1.0f;
        Vector3 c = new Vector3(lk.Center.X, level, lk.Center.Y);
        for (int i = 0; i < seg; i++)
        {
            float a0 = i / (float)seg * Mathf.Tau, a1 = (i + 1) / (float)seg * Mathf.Tau;
            Vector3 p0 = c + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * lk.Radius;
            Vector3 p1 = c + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * lk.Radius;
            st.SetUV(new Vector2(0.5f, 0.5f)); st.AddVertex(c);    // centre (UV.x 0.5 = deep)
            st.SetUV(new Vector2(0f, 0f)); st.AddVertex(p0);       // edge (UV.x 0 = shallow/foam)
            st.SetUV(new Vector2(1f, 0f)); st.AddVertex(p1);
        }
        st.GenerateNormals();
        return st.Commit();
    }
}
