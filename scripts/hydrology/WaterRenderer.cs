using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

/// Manages the river ribbon + lake surface meshes as additive sibling Node3Ds. Render-relative to the
/// CDLOD render origin (the meshes are authored in TRUE world XZ; we shift the whole node by −origin so
/// they line up with the render-relative terrain). NEVER touches terrain geometry or the base field.
public sealed partial class WaterRenderer : Node3D
{
    private readonly List<MeshInstance3D> _meshes = new();
    private Vector3 _renderOrigin = Vector3.Zero;

    public void BuildForRegion(WorldWaterRegion reg, WaterParams wp, Material mat)
    {
        foreach (var r in reg.Rivers)
        {
            if (r.Points.Length < 2) continue;
            var mi = new MeshInstance3D { Mesh = RiverRibbonMesh.Build(r, reg, wp), MaterialOverride = mat };
            AddChild(mi); _meshes.Add(mi);
        }
        foreach (var lk in reg.Lakes)
        {
            var mi = new MeshInstance3D { Mesh = LakeMesh.Build(lk, reg), MaterialOverride = mat };
            AddChild(mi); _meshes.Add(mi);
        }
        ApplyOrigin();
    }

    public void SetRenderOrigin(Vector3 o) { _renderOrigin = o; ApplyOrigin(); }
    private void ApplyOrigin() { Position = -_renderOrigin; }
}
