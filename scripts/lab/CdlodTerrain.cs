using Godot;
using System.Collections.Generic;
using WG16.Field;

namespace WG16.Lab;

/// CDLOD manager: each Tick(), selects leaf chunks from the quadtree and renders
/// each as an instance of ONE shared grid mesh + the ground material, placed by
/// transform (world-XZ derived from MODEL_MATRIX in the shader). Pools instances
/// to avoid per-frame alloc. S2a: LOD-select + cull only (geomorph is S2b).
public sealed partial class CdlodTerrain : Node3D
{
    private ShaderMaterial _mat = null!;
    private PlaneMesh _grid = null!;
    private CdlodQuadtree _qt = null!;
    private float _minH, _maxH, _regionSize;
    private float[] _heights = System.Array.Empty<float>();   // baked heightmap (row-major, res²), for per-chunk AABB
    private int _hRes;
    private readonly List<MeshInstance3D> _pool = new();
    private bool _enabled;
    private bool _lodViz;

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable

    // S2b: exposed to TerrainLab so it can push the matching geomorph uniforms to the shader.
    public float GridResolution => GridN;
    public float SplitFactorValue => SplitFactor;

    public void Setup(ShaderMaterial mat, FieldParams p, float minH, float maxH, float[] heights)
    {
        _mat = mat; _minH = minH; _maxH = maxH; _regionSize = p.RegionSizeM;
        _heights = heights; _hRes = p.HeightmapRes;
        _grid = CdlodMesh.BuildGrid(GridN);
        _qt = new CdlodQuadtree(-_regionSize * 0.5f, -_regionSize * 0.5f, _regionSize, MaxDepth, SplitFactor);
        GD.Print($"CdlodTerrain: setup gridN={GridN} maxDepth={MaxDepth} split={SplitFactor} region={_regionSize:F0}");
    }

    /// Min/max terrain height over a chunk's world-XZ footprint, sampled from the baked heightmap.
    /// A TIGHT per-chunk vertical AABB is essential: Godot fits the directional shadow cascade's
    /// depth range to caster AABBs, so a full-region-tall AABB on every small chunk inflates the
    /// shadow-map depth range and destroys precision → large soft self-shadow blobs (worst at low
    /// sun). Sampling a coarse stride keeps this cheap; margins cover sub-sample peaks.
    private (float lo, float hi) ChunkHeightRange(Vector2 originXZ, float size)
    {
        if (_heights.Length == 0 || _hRes <= 0) { return (_minH, _maxH); }   // fallback: global range
        float texel = _regionSize / _hRes;
        float h = _regionSize * 0.5f;
        int c0 = Mathf.Clamp((int)Mathf.Floor((originXZ.X + h) / texel), 0, _hRes - 1);
        int c1 = Mathf.Clamp((int)Mathf.Ceil((originXZ.X + size + h) / texel), 0, _hRes - 1);
        int r0 = Mathf.Clamp((int)Mathf.Floor((originXZ.Y + h) / texel), 0, _hRes - 1);
        int r1 = Mathf.Clamp((int)Mathf.Ceil((originXZ.Y + size + h) / texel), 0, _hRes - 1);
        // cap the sample count per chunk so coarse (huge) chunks stay cheap (~<=32² taps)
        int stepC = Mathf.Max(1, (c1 - c0) / 32);
        int stepR = Mathf.Max(1, (r1 - r0) / 32);
        float lo = float.MaxValue, hi = float.MinValue;
        for (int r = r0; r <= r1; r += stepR)
        for (int c = c0; c <= c1; c += stepC)
        {
            float v = _heights[r * _hRes + c];
            if (v < lo) { lo = v; }
            if (v > hi) { hi = v; }
        }
        if (lo > hi) { return (_minH, _maxH); }
        return (lo, hi);
    }

    public void SetEnabled(bool on)
    {
        _enabled = on;
        if (!on) { foreach (var mi in _pool) { mi.Visible = false; } }
        GD.Print($"CdlodTerrain: {(on ? "ENABLED" : "disabled")}");
    }

    public void SetLodViz(bool on) { _lodViz = on; }

    public void Tick(Vector3 camPos)
    {
        if (!_enabled) { return; }
        if (!IsInsideTree()) { return; }   // the AddChild is deferred (see TerrainLab.Build); skip until in-tree
        List<CdlodChunk> leaves = _qt.Select(camPos);
        EnsurePool(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            MeshInstance3D mi = _pool[i];
            float half = c.Size * 0.5f;
            mi.Position = new Vector3(c.OriginXZ.X + half, 0f, c.OriginXZ.Y + half);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);    // X/Z = chunk size; Y = 1 (world-unit height)
            // CustomAabb is LOCAL (pre-node-scale): X/Z = the unit grid (the node Scale stretches it to
            // the chunk footprint); Y = this chunk's TIGHT world height range (Y scale is 1). Tight Y is
            // required for shadow-cascade depth precision — see ChunkHeightRange.
            var (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size);
            const float m = 8f;   // margin for sub-sample peaks + the analytic-vs-baked epsilon
            mi.CustomAabb = new Aabb(new Vector3(-0.5f, lo - m, -0.5f),
                                     new Vector3(1f, (hi - lo) + 2f * m, 1f));
            mi.SetInstanceShaderParameter("lod_viz", _lodViz ? (float)c.Level : -1.0f);
            mi.Visible = true;
        }
        for (int i = leaves.Count; i < _pool.Count; i++) { _pool[i].Visible = false; }
    }

    private void EnsurePool(int n)
    {
        while (_pool.Count < n)
        {
            var mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat };
            AddChild(mi);
            _pool.Add(mi);
        }
    }
}
