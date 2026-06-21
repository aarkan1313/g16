using Godot;

namespace WG16.Lab;

/// CDLOD shared grid: ONE flat unit PlaneMesh reused by every chunk instance.
/// Size 1x1 centered at origin; an instance's Transform3D (translate to chunk
/// center, scale = chunk size) maps the unit grid onto a world chunk. The
/// vertex shader derives world-XZ from MODEL_MATRIX and displaces from the field.
public static class CdlodMesh
{
    /// n = grid resolution (vertices per side); n-1 quads per side. e.g. 32 or 64.
    public static PlaneMesh BuildGrid(int n)
    {
        n = Mathf.Clamp(n, 2, 256);
        return new PlaneMesh
        {
            Size = new Vector2(1f, 1f),
            SubdivideWidth = n - 1,
            SubdivideDepth = n - 1,
        };
    }
}
