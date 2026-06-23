using Godot;

namespace WG16.Hydrology;

/// Renders the water surfaces. Tier 1 (this task): one large sea plane positioned at WaterParams.SeaLevel,
/// depth-shaded against the terrain height texture. Lakes/rivers added in later tasks. Decoupled from the lab.
/// The mesh NODE is placed at the water-surface Y (plane flat at local 0); the shader does depth/foam/motion.
public sealed class WaterRenderer
{
    private readonly int _res; private readonly float _cell;
    private MeshInstance3D _sea; private ShaderMaterial _seaMat;

    public WaterRenderer(int res, float cell) { _res = res; _cell = cell; }

    public void BuildSea(Node parent, WaterParams wp, float[] terrainHeight)
    {
        if (_sea == null)
        {
            _sea = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(_res * _cell, _res * _cell), SubdivideWidth = 128, SubdivideDepth = 128 },
            };
            _seaMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_surface.gdshader") };
            _seaMat.SetShaderParameter("region_size", _res * _cell);
            _sea.MaterialOverride = _seaMat;
            parent.AddChild(_sea);
        }
        _sea.Visible = wp.SeaEnabled;
        _sea.Position = new Vector3(0f, wp.SeaLevel, 0f);     // node at the sea surface; plane flat at local 0
        _seaMat.SetShaderParameter("water_level", wp.SeaLevel);
        _seaMat.SetShaderParameter("terrain_height_tex", RFTex(terrainHeight));
        UpdateLook(wp);
    }

    public void UpdateLook(WaterParams wp)
    {
        if (_seaMat == null) { return; }
        _seaMat.SetShaderParameter("shallow_depth", wp.ShallowDepthM);
        _seaMat.SetShaderParameter("flow_speed", wp.FlowSpeed);
        _seaMat.SetShaderParameter("wave_scale", wp.WaveScale);
    }

    private ImageTexture RFTex(float[] f)
    {
        var bytes = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes));
    }
}
