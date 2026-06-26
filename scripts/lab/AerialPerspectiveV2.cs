using Godot;

namespace WG16.Lab;

/// AT-2 v2 screen-space aerial perspective. Fullscreen clip-space quad that reads the depth buffer,
/// reconstructs per-pixel world distance, samples the 64-slice log-Z froxel LUT produced by
/// AtmosphereCompute, and composites extinction+inscatter on geometry (sky skipped).
/// RenderPriority 120: below GodRaysScreen (127) → aerial hazes first, god rays draw on top.
public partial class AerialPerspectiveV2 : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private bool _on;

    public AerialPerspectiveV2()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/aerial_screen_v2.gdshader") };
        _mat.RenderPriority = 120;
        _quad = new MeshInstance3D
        {
            Name = "AerialV2ScreenQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_quad);
    }

    public void Attach(Camera3D cam) { _cam = cam; }
    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; _mat.SetShaderParameter("aerial_on", on); }
    public void SetAerialTexture(Texture3Drd? tex) { if (tex != null) { _mat.SetShaderParameter("aerial_tex", tex); } }
    public void SetStrength(float s) { _mat.SetShaderParameter("aerial_strength", Mathf.Clamp(s, 0f, 60f)); }
    public void SetHaze(Color c) { _mat.SetShaderParameter("aerial_haze", new Vector3(c.R, c.G, c.B)); }
    public void SetHazeStrength(float s) { _mat.SetShaderParameter("aerial_haze_str", Mathf.Clamp(s, 0f, 2f)); }
    public bool On => _on;

    public override void _Process(double delta)
    {
        if (!_on || _cam == null) { return; }
        Projection proj = _cam.GetCameraProjection();
        Projection vp = proj * new Projection(_cam.GlobalTransform.AffineInverse());
        _mat.SetShaderParameter("inv_view_proj", vp.Inverse());
        _mat.SetShaderParameter("cam_world", _cam.GlobalPosition);
        _mat.SetShaderParameter("aerial_far", _cam.Far);
        _mat.SetShaderParameter("aerial_z_near", 1f);
    }
}
