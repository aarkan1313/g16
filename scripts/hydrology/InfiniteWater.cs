using Godot;
using WG16.Field;

namespace WG16.Hydrology;

/// Additive infinite-world water for the CDLOD terrain scene. STANDALONE Node3D — attaches as a sibling of the
/// terrain, NEVER touches terrain geometry or the base field (graveyard discipline: water can't break the tuned
/// CDLOD lane). RT3: a global SEA plane at WaterParams.SeaLevel that follows the camera (one node, zero per-chunk
/// cost). Depth/shoreline shading + lakes/rivers come in RT4 by sampling the CoarseWorldWater map.
///
/// Add to terrain_lab.tscn as a child of the root, or instantiate from TerrainLab if it exposes a hook. Reads the
/// camera from the scene ("Camera"). Toggle/level via --sea= / --nosea (parsed here from cmdline).
public partial class InfiniteWater : Node3D
{
    private MeshInstance3D _sea = null!;
    private ShaderMaterial _seaMat = null!;
    private Camera3D _cam = null!;
    private WaterParams _wp = new();
    private const float SeaPlaneSize = 24000f;   // big enough to reach the far plane; follows the camera in XZ

    public override void _Ready()
    {
        // CLI: --sea=Y enables + sets the sea; default OFF (sea is a coastal feature — opt in for lowland worlds).
        bool seaOn = false; float seaY = _wp.SeaLevel;
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--sea=")) { seaOn = true; seaY = a.Substring(6).ToFloat(); }
            else if (a == "--nosea") { seaOn = false; }
        }
        _wp.SeaEnabled = seaOn; _wp.SeaLevel = seaY;

        _cam = GetParent().GetNodeOrNull<Camera3D>("Camera");

        _sea = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(SeaPlaneSize, SeaPlaneSize), SubdivideWidth = 64, SubdivideDepth = 64 },
            Visible = _wp.SeaEnabled,
        };
        _seaMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/infinite_water.gdshader") };
        _seaMat.SetShaderParameter("wave_scale", _wp.WaveScale);
        _seaMat.SetShaderParameter("flow_speed", _wp.FlowSpeed);
        _sea.MaterialOverride = _seaMat;
        AddChild(_sea);
        GD.Print($"InfiniteWater: sea={(_wp.SeaEnabled ? _wp.SeaLevel.ToString("F0") : "off")}");
    }

    public override void _Process(double delta)
    {
        if (!_wp.SeaEnabled || _cam == null) { return; }
        // follow the camera in XZ, sit at the sea level Y (so the plane always spans the view).
        Vector3 cp = _cam.GlobalPosition;
        _sea.GlobalPosition = new Vector3(cp.X, _wp.SeaLevel, cp.Z);
    }
}
