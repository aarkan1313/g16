using Godot;
using WG16.Field;

namespace WG16.Presenter;

/// Builds the lab's single displaced-plane terrain from a produced heightfield.
/// Rebuild with fresh params for hot-reload and reseed. No bake stage: the
/// presenter draws exactly what the Field produces — no delta, no flow, no
/// overlays.
public partial class LabTerrain : MeshInstance3D
{
    private ShaderMaterial? _mat;
    private float[]? _heights;
    private int _res;
    private float _regionSize;
    private float _minBase;
    private float _maxBase;

    /// Headroom on the culling bounds beyond the measured height range.
    private const float AabbMarginM = 8f;

    public void Rebuild(FieldCompute fc, FieldParams p, uint fieldMode = 0)
    {
        ulong t0 = Time.GetTicksMsec();
        float[] heights = fc.ProducePage(
            p,
            originX: -p.RegionSizeM * 0.5f,
            originZ: -p.RegionSizeM * 0.5f,
            fieldMode: fieldMode);

        _heights = heights;
        _res = p.HeightmapRes;
        _regionSize = p.RegionSizeM;

        var bytes = new byte[heights.Length * sizeof(float)];
        System.Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
        Image img = Image.CreateFromData(p.HeightmapRes, p.HeightmapRes, false, Image.Format.Rf, bytes);
        ImageTexture tex = ImageTexture.CreateFromImage(img);

        if (Mesh == null)
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(p.RegionSizeM, p.RegionSizeM),
                // One vertex PER HEIGHTMAP TEXEL: at half density the mesh aliases
                // 1-texel features into rim teeth. The presenter must not lie.
                SubdivideWidth = p.HeightmapRes - 1,
                SubdivideDepth = p.HeightmapRes - 1,
            };
            _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/lab_terrain.gdshader") };
            MaterialOverride = _mat;
            _mat.SetShaderParameter("grass_tex", GD.Load<Texture2D>("res://assets/textures/grass.png"));
            _mat.SetShaderParameter("dirt_tex", GD.Load<Texture2D>("res://assets/textures/dirt.png"));
            _mat.SetShaderParameter("scree_tex", GD.Load<Texture2D>("res://assets/textures/scree.png"));
            _mat.SetShaderParameter("rock_tex", GD.Load<Texture2D>("res://assets/textures/rock.png"));
        }

        _mat!.SetShaderParameter("heightmap", tex);
        _mat.SetShaderParameter("region_size", p.RegionSizeM);
        _mat.SetShaderParameter("texel_world", p.Spacing);

        // Displacement happens in the VERTEX SHADER, so the PlaneMesh AABB is a
        // zero-thickness sheet at y=0 — the frustum culler would drop the whole
        // terrain at angles where that sheet leaves the view ("screen goes black
        // in ditches"). Tell the culler the truth.
        _minBase = float.MaxValue;
        _maxBase = float.MinValue;
        for (int i = 0; i < heights.Length; i++)
        {
            _minBase = Mathf.Min(_minBase, heights[i]);
            _maxBase = Mathf.Max(_maxBase, heights[i]);
        }
        SetCullAabb(_minBase, _maxBase);
        GD.Print($"LabTerrain: rebuilt {p.HeightmapRes}x{p.HeightmapRes} in {Time.GetTicksMsec() - t0} ms (seed {p.Seed}, mode {fieldMode})");
    }

    /// Culling bounds in local space (the mesh sits at the origin), spanning the
    /// region in xz and the given height range (+margin) in y.
    private void SetCullAabb(float minH, float maxH)
    {
        CustomAabb = new Aabb(
            new Vector3(-_regionSize * 0.5f, minH - AabbMarginM, -_regionSize * 0.5f),
            new Vector3(_regionSize, (maxH - minH) + 2f * AabbMarginM, _regionSize));
    }

    /// Toggle the shader's polished-vs-plain color treatment (experiment A/B).
    public void SetPolish(bool on)
    {
        _mat?.SetShaderParameter("polish", on ? 1.0f : 0.0f);
    }

    /// Independent material-group toggles (each falls back to a flat tone).
    public void SetRock(bool on)  { _mat?.SetShaderParameter("rock_on", on ? 1.0f : 0.0f); }
    public void SetGrass(bool on) { _mat?.SetShaderParameter("grass_on", on ? 1.0f : 0.0f); }
    public void SetScree(bool on) { _mat?.SetShaderParameter("scree_on", on ? 1.0f : 0.0f); }
    public void SetDirt(bool on)  { _mat?.SetShaderParameter("dirt_on", on ? 1.0f : 0.0f); }

    /// CPU mirror of the shader's height read: bilinear base. Powers walk mode.
    public float SampleHeight(float worldX, float worldZ)
    {
        if (_heights == null) { return 0f; }
        float fx = Mathf.Clamp((worldX + _regionSize * 0.5f) / _regionSize * (_res - 1), 0f, _res - 1.001f);
        float fz = Mathf.Clamp((worldZ + _regionSize * 0.5f) / _regionSize * (_res - 1), 0f, _res - 1.001f);
        int x0 = Mathf.Min((int)fx, _res - 2), z0 = Mathf.Min((int)fz, _res - 2);
        float tx = fx - x0, tz = fz - z0;
        return Bilerp(_heights, x0, z0, tx, tz);
    }

    private float Bilerp(float[] g, int x0, int z0, float tx, float tz)
    {
        int i = z0 * _res + x0;
        float a = Mathf.Lerp(g[i], g[i + 1], tx);
        float b = Mathf.Lerp(g[i + _res], g[i + _res + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }
}
