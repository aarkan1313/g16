using Godot;

namespace WG16.Lab;

/// Screen-space god rays (GPU Gems 3 radial scatter) — the CRISP beam layer atop the soft froxel
/// base (GodRaysVolumetric). Task 1 is a PASSTHROUGH experiment to learn what the captured frame
/// contains (does hint_screen_texture include the sky/sun/clouds?); the real radial blur + occlusion
/// mask + sun-UV gating come in later tasks once the capture path is known.
///
/// The quad is a CLIP-SPACE fullscreen quad: its vertex shader writes POSITION directly, so the
/// quad's world transform is irrelevant — it covers the screen regardless of where the node sits.
/// No camera-parenting needed; just be in the tree + visible + draw last (high render_priority).
public partial class GodRaysScreen : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private DirectionalLight3D? _sun;
    private bool _on;

    public GodRaysScreen()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_screen.gdshader") };
        _mat.RenderPriority = 127;   // draw after the scene's transparent objects
        _quad = new MeshInstance3D
        {
            Name = "GodRayScreenQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },   // clip-space fullscreen via the vertex shader
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // never frustum-cull it away (its real coverage is the whole screen, not its AABB).
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_quad);
    }

    public void Attach(Camera3D cam, DirectionalLight3D sun) { _cam = cam; _sun = sun; }

    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; }
    public bool On => _on;
}
