using Godot;

namespace WG16.Lab;

/// Screen-space god rays (GPU Gems 3 Ch.13 radial light-scattering) — THE god-ray layer for WG16.
/// (The froxel-fog approach was dropped 2026-06-19: it read as a washy fog, not crisp beams.)
/// A clip-space fullscreen quad reads the rendered frame (sky+sun+clouds+terrain — confirmed present
/// in hint_screen_texture in Godot 4.6) and radial-blurs a luminance occlusion buffer outward from
/// the sun's screen position, adding the bright fan of beams back. Driven by the Clouds-tab
/// "god rays" toggle + "god ray strength"; default OFF.
///
/// The quad is a CLIP-SPACE fullscreen quad: its vertex shader writes POSITION directly, so the
/// quad's world transform is irrelevant — it covers the screen regardless of where the node sits.
/// No camera-parenting needed; just be in the tree + visible + draw last (high render_priority).
public partial class GodRaysScreen : Node3D
{
    private const float SampleScale64To32 = 64f / 32f;
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private DirectionalLight3D? _sun;
    private bool _on;

    public GodRaysScreen()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_screen.gdshader") };
        _mat.RenderPriority = 127;   // draw after the scene's transparent objects
        // occ_mode 2 = luminance occlusion from on-screen cloud silhouettes.
        _mat.SetShaderParameter("occ_mode", 2);
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

    public override void _Process(double delta)
    {
        if (!_on || _cam == null || _sun == null) { return; }
        // DirectionalLight shines along -Z; the sun (source) is at +Basis.Z (same convention as
        // PushSunToCloud / CloudVolume.SetSun). A far point along that ray is where the sun "is".
        Vector3 toSun = _sun.GlobalTransform.Basis.Z.Normalized();
        Vector3 sunWorld = _cam.GlobalPosition + toSun * 100000f;

        // Camera looks down -Z. align = how much the view faces the sun (1 = straight at it).
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
        float align = fwd.Dot(toSun);

        float gate = 0f;
        Vector2 uv = new Vector2(0.5f, 0.5f);
        // UnprojectPosition returns garbage for points BEHIND the camera → gate with IsPositionBehind.
        if (!_cam.IsPositionBehind(sunWorld) && align > 0f)
        {
            Vector2 px = _cam.UnprojectPosition(sunWorld);
            Vector2 vp = _cam.GetViewport().GetVisibleRect().Size;
            uv = px / vp;
            // GRADUAL onset (no sudden "lens-flare" pop as you turn toward the sun): a WIDE smoothstep
            // edge-fade + an eased alignment ramp so the beams build in smoothly as the sun nears center.
            const float edge = 0.32f;   // wider margin → fades in well before the sun reaches mid-screen
            float fx = Mathf.SmoothStep(0f, 1f, Mathf.Min(uv.X, 1f - uv.X) / edge);
            float fy = Mathf.SmoothStep(0f, 1f, Mathf.Min(uv.Y, 1f - uv.Y) / edge);
            // ease alignment from a threshold: ~0 until the view is fairly toward the sun, then ramps up.
            float a = Mathf.SmoothStep(0.15f, 0.85f, Mathf.Clamp(align, 0f, 1f));
            gate = fx * fy * a;
        }
        _mat.SetShaderParameter("sun_screen_uv", uv);
        _mat.SetShaderParameter("sun_gate", gate);

        // Aspect for the high-pass: the reference rays rotate around the sun in screen space, so they
        // must be aspect-corrected or the rotation stretches on a wide viewport (anisotropic = uneven
        // wash removal). Cheap to push each frame; tolerant of window resizes.
        Vector2 vpSize = _cam.GetViewport().GetVisibleRect().Size;
        _mat.SetShaderParameter("aspect", vpSize.Y > 0f ? vpSize.X / vpSize.Y : 1.7778f);
    }

    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; }
    public bool On => _on;

    // ---- Tunables (all driven by Clouds-tab controls; see lab_controls.json + ApplyCloudFloat). ----

    /// UI "god ray strength" → the GPU Gems `exposure` (overall beam intensity).
    public void SetStrength(float s) => _mat.SetShaderParameter("exposure", Mathf.Clamp(s * 0.1f, 0f, 2f));
    /// Beam length / reach: LOWER density = longer beams (shorter sample steps).
    public void SetDensity(float v) => _mat.SetShaderParameter("density", Mathf.Clamp(v, 0.05f, 1.0f));
    /// Per-step attenuation: →1 = longer shafts.
    public void SetDecay(float v)
    {
        float d = Mathf.Clamp(v, 0.5f, 1.0f);
        _mat.SetShaderParameter("decay", d);
        _mat.SetShaderParameter("decay_step", Mathf.Pow(d, SampleScale64To32));
    }
    /// Cloud detection radius around the sun (UV): how far from the sun darkness counts as cloud.
    public void SetCloudRadius(float v) => _mat.SetShaderParameter("cloud_radius", Mathf.Clamp(v, 0.05f, 1.5f));
    /// Cloud luminance threshold: sky brighter than this = open (lit); darker = cloud occluder.
    public void SetCloudLum(float v) => _mat.SetShaderParameter("cloud_lum", Mathf.Clamp(v, 0.0f, 2.0f));
    /// Occluder polarity (occ_mode 2). false = DAYLIT: the bright cloud is the occluder, clear sky is open
    /// (shafts in the gaps — the default, no sky veil). true = BACKLIT: dark cloud is the occluder (sun
    /// behind cloud). Wrong polarity for the scene re-introduces the milky full-sky wash.
    public void SetCloudInvert(bool backlit) => _mat.SetShaderParameter("cloud_invert", backlit);
    /// Warm/cool tint of the beams.
    public void SetTint(Color c) => _mat.SetShaderParameter("ray_tint", c);

    /// Structure-gate amount: 0 = raw additive (old wash+ring look), 1 = cloud-structure gate (clean
    /// beams only where clouds carve the sky; no open-sky veil, no ring). Default 1. (--godrayhp=N A/B.)
    public void SetHighpass(float v) => _mat.SetShaderParameter("highpass", Mathf.Clamp(v, 0f, 1f));
    /// Tangential offset (UV) of the high-pass mean — roughly half a shaft width. Larger = fills broader
    /// shafts (but too wide softens thin ones). ~0.02 default.
    public void SetGateWidth(float v) => _mat.SetShaderParameter("gate_width", Mathf.Clamp(v, 0.004f, 0.08f));

    /// DEBUG (--godraydbg=N): 0 = normal, 1 = occlusion mask, 2 = sun-UV marker + gate, 3 = isSky gate
    /// (green sky / red geometry), 5 = scatter visualizer (the beam contribution alone).
    public void SetDebug(int mode) => _mat.SetShaderParameter("debug_mode", mode);
}
