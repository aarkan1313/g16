using Godot;

namespace WG16.Hydrology;

/// Renders the water surfaces. Tier 1 (this task): one large sea plane positioned at WaterParams.SeaLevel,
/// depth-shaded against the terrain height texture. Lakes/rivers added in later tasks. Decoupled from the lab.
/// The mesh NODE is placed at the water-surface Y (plane flat at local 0); the shader does depth/foam/motion.
public sealed class WaterRenderer
{
    private readonly int _res; private readonly float _cell;
    private MeshInstance3D _sea; private ShaderMaterial _seaMat;
    private readonly System.Collections.Generic.List<MeshInstance3D> _lakes = new();

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

    /// Tier 2: one plane per significant lake, positioned at its spill surface. Same shader (depth/foam/motion);
    /// the region-global UV means an offset lake plane still samples the correct terrain cell for depth.
    public void BuildLakes(Node parent, System.Collections.Generic.IReadOnlyList<WaterBodies.Lake> lakes, WaterParams wp, float[] terrainHeight)
    {
        foreach (var m in _lakes) { m.QueueFree(); }
        _lakes.Clear();
        if (!wp.LakesEnabled) { return; }
        var hTex = RFTex(terrainHeight);
        foreach (var lk in lakes)
        {
            // pad bounds a little so the plane fully covers the basin to its shoreline.
            float w = (lk.MaxX - lk.MinX) + 4f * _cell, d = (lk.MaxZ - lk.MinZ) + 4f * _cell;
            var mi = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d), SubdivideWidth = 24, SubdivideDepth = 24 } };
            mi.Position = new Vector3((lk.MinX + lk.MaxX) * 0.5f, lk.SurfaceLevel, (lk.MinZ + lk.MaxZ) * 0.5f);
            var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_surface.gdshader") };
            mat.SetShaderParameter("region_size", _res * _cell);
            mat.SetShaderParameter("water_level", lk.SurfaceLevel);
            mat.SetShaderParameter("terrain_height_tex", hTex);
            mat.SetShaderParameter("shallow_depth", wp.ShallowDepthM);
            mat.SetShaderParameter("flow_speed", wp.FlowSpeed);
            mat.SetShaderParameter("wave_scale", wp.WaveScale);
            mi.MaterialOverride = mat;
            parent.AddChild(mi); _lakes.Add(mi);
        }
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
