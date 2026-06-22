using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// CDLOD shared grid + S2d stitched variants. The base is ONE flat unit grid (n×n verts, [-0.5,0.5] in
/// X/Z) reused by every chunk instance; the variants additionally STITCH any edge that faces a one-level-
/// coarser neighbor by COLLAPSING that edge's odd boundary vertices onto an even neighbor, so the edge
/// becomes the same polyline (even-vertex spacing) the coarse neighbor renders — removing the T-junction
/// that otherwise cracks once the shader displaces the two edges from the heightfield.
public static class CdlodMesh
{
    /// n = grid resolution (vertices per side); n-1 quads per side. Used by the one-chunk sanity path.
    public static PlaneMesh BuildGrid(int n)
    {
        n = Mathf.Clamp(n, 2, 256);
        return new PlaneMesh { Size = new Vector2(1f, 1f), SubdivideWidth = n - 1, SubdivideDepth = n - 1 };
    }

    /// 16 stitched variants indexed by stitch mask (bit0=-X bit1=+X bit2=-Z bit3=+Z). Requires n-1 EVEN
    /// (so every odd boundary index has an even neighbor) — GridN=65 -> n-1=64 (even). Built once at setup.
    public static ArrayMesh[] BuildStitchedVariants(int n)
    {
        n = Mathf.Clamp(n, 3, 256);
        var meshes = new ArrayMesh[16];
        for (int mask = 0; mask < 16; mask++) { meshes[mask] = BuildOne(n, mask); }
        return meshes;
    }

    private static ArrayMesh BuildOne(int n, int mask)
    {
        int last = n - 1;
        // INDEXED mesh: n×n unique vertices (same count as PlaneMesh — NOT a SurfaceTool vertex soup, which
        // was ~6× the verts and blew the perf budget). For a stitched edge, COLLAPSE each odd boundary
        // vertex's POSITION onto its lower even neighbor along the edge: the odd vert becomes spatially
        // coincident with the even one, so the two triangles that used it go zero-area (degenerate) and the
        // GPU discards them at raster — leaving a fan between even verts only, exactly matching the coarse
        // neighbor's edge spacing (no T-junction → no crack). Indices are the plain quad tiling; the collapse
        // is purely in vertex POSITIONS, so the index buffer is identical for all 16 variants.
        var verts = new Vector3[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            verts[j * n + i] = new Vector3((float)i / last - 0.5f, 0f, (float)j / last - 0.5f);
        }
        bool mX0 = (mask & 1) != 0, mX1 = (mask & 2) != 0, mZ0 = (mask & 4) != 0, mZ1 = (mask & 8) != 0;
        // collapse odd boundary verts onto the even neighbor at index-1 along the edge (corners are even -> fixed)
        if (mX0) { for (int j = 1; j < last; j += 2) { verts[j * n + 0]    = verts[(j - 1) * n + 0]; } }       // -X (i=0)
        if (mX1) { for (int j = 1; j < last; j += 2) { verts[j * n + last] = verts[(j - 1) * n + last]; } }    // +X (i=last)
        if (mZ0) { for (int i = 1; i < last; i += 2) { verts[0 * n + i]    = verts[0 * n + (i - 1)]; } }       // -Z (j=0)
        if (mZ1) { for (int i = 1; i < last; i += 2) { verts[last * n + i] = verts[last * n + (i - 1)]; } }    // +Z (j=last)

        var indices = new List<int>(last * last * 6);
        for (int j = 0; j < last; j++)
        for (int i = 0; i < last; i++)
        {
            int i00 = j * n + i,       i10 = j * n + (i + 1);
            int i01 = (j + 1) * n + i, i11 = (j + 1) * n + (i + 1);
            // Front face must be CCW viewed from +Y (above) — Godot culls back faces, so the wrong winding
            // makes the terrain invisible from above ("see-through"). Order: i00->i10->i11, i00->i11->i01.
            indices.Add(i00); indices.Add(i10); indices.Add(i11);   // tri A (CCW from +Y)
            indices.Add(i00); indices.Add(i11); indices.Add(i01);   // tri B
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var am = new ArrayMesh();
        // The chunk vertex shader writes NORMAL + height and ignores incoming normals/UVs, so positions +
        // indices are sufficient. (Custom AABB is set per-instance by CdlodTerrain.)
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return am;
    }
}
