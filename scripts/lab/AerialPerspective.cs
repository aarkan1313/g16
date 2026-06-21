using Godot;

namespace WG16.Lab;

/// AT-2 screen-space aerial perspective. A clip-space fullscreen quad (mirrors GodRaysScreen) that reads
/// the rendered frame + depth, samples the camera-aligned aerial froxel LUT (AtmosphereCompute.AerialTexture)
/// by (screen UV, reconstructed distance), and composites color·T + in-scatter on geometry (sky skipped).
///
/// The quad is a CLIP-SPACE fullscreen quad: its vertex shader writes POSITION directly, so the node's world
/// transform is irrelevant. RenderPriority 120 sits BELOW GodRaysScreen's 127 → aerial composites the haze
/// first, beams draw on top.
public partial class AerialPerspective : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private bool _on;

    public AerialPerspective()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/aerial_screen.gdshader") };
        _mat.RenderPriority = 120;   // BELOW GodRaysScreen's 127 → aerial composites first, beams on top
        _quad = new MeshInstance3D
        {
            Name = "AerialScreenQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },   // clip-space fullscreen via the vertex shader
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,   // never frustum-cull (real coverage is the whole screen, not its AABB)
            Visible = false,
        };
        AddChild(_quad);
    }

    public void Attach(Camera3D cam) { _cam = cam; }
    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; _mat.SetShaderParameter("aerial_on", on); }
    public void SetAerialTexture(Texture3Drd? tex) { if (tex != null) { _mat.SetShaderParameter("aerial_tex", tex); } }
    public void SetStrength(float s) { _mat.SetShaderParameter("aerial_strength", Mathf.Clamp(s, 0f, 60f)); }
    public bool On => _on;

    public override void _Process(double delta)
    {
        if (!_on || _cam == null) { return; }
        // depth→world reconstruction needs inverse(projection*view); cam_world is the view-ray origin.
        Projection proj = _cam.GetCameraProjection();
        Projection vp = proj * new Projection(_cam.GlobalTransform.AffineInverse());
        _mat.SetShaderParameter("inv_view_proj", vp.Inverse());
        _mat.SetShaderParameter("cam_world", _cam.GlobalPosition);
        _mat.SetShaderParameter("aerial_far", 32000f);   // MUST match AtmosphereCompute.SetCamera's farDist
    }
}
