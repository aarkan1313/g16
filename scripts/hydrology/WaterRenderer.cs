using Godot;

namespace WG16.Hydrology;

/// Static water surface renderer (Arc-2 seed): owns a grid MeshInstance3D + water_surface.gdshader material,
/// driven by a CarveResult's water_level + height. Lifts the mesh to the substrate water surface where wet,
/// collapses it under terrain where dry. Decoupled from the lab — feed it a CarveResult, it renders water that
/// agrees with the terrain. Add it under any parent Node3D.
public sealed class WaterRenderer
{
    private readonly int _res;
    private readonly float _cell;
    private MeshInstance3D _mesh;
    private ShaderMaterial _mat;

    public WaterRenderer(int res, float cell) { _res = res; _cell = cell; }

    /// Create the water mesh under `parent` (once) and/or update it from a fresh CarveResult.
    public void Update(Node parent, ValleyCarve.CarveResult carve)
    {
        if (_mesh == null)
        {
            _mesh = new MeshInstance3D
            {
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(_res * _cell, _res * _cell),
                    SubdivideWidth = _res - 1,
                    SubdivideDepth = _res - 1,
                },
            };
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_surface.gdshader") };
            _mat.SetShaderParameter("region_size", _res * _cell);
            _mesh.MaterialOverride = _mat;
            parent.AddChild(_mesh);
        }
        _mat.SetShaderParameter("water_level_tex", RFTex(carve.WaterLevel));
        _mat.SetShaderParameter("terrain_height_tex", RFTex(carve.Height));
    }

    private ImageTexture RFTex(float[] f)
    {
        var bytes = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes));
    }
}
