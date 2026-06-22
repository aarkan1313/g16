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
    private ArrayMesh[] _variants = System.Array.Empty<ArrayMesh>();   // S2d: [stitchMask] -> welded edge-stitch mesh
    private CdlodQuadtree _qt = null!;
    private float _minH, _maxH, _regionSize;
    private float[] _heights = System.Array.Empty<float>();   // baked heightmap (row-major, res²), for per-chunk AABB
    private int _hRes;
    private readonly List<MeshInstance3D> _pool = new();
    private readonly List<int> _poolMask = new();   // S2d: per-pool-slot last-assigned stitch mask (-1 = unset); avoids per-frame mesh re-upload
    private List<CdlodChunk> _lastLeaves = new();   // S2b: last Tick's selected leaves (for the test-path along-path invariant/count report)
    private bool _enabled;
    private bool _lodViz;

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable

    // S3: snapped camera-relative render space (floating-origin folded in). renderOrigin = camera XZ snapped
    // DOWN to _coarseSnap so render-relative coords stay bounded (no float drift) AND the field samples
    // bit-identical TRUE world XZ across snaps (relative + snapped origin == true world).
    private float _coarseSnap = 8192f;   // set in Setup to the root size (largest chunk grid → snap-invariant)
    private Vector3 _renderOrigin = Vector3.Zero;
    public Vector3 RenderOrigin => _renderOrigin;

    // S2b: exposed to TerrainLab so it can push the matching geomorph uniforms to the shader.
    public float GridResolution => GridN;
    public float SplitFactorValue => SplitFactor;

    // S2b: along-path report accessors for TerrainTestPaths (sampled every frame during a flight).
    public int LeafCountLastTick => _lastLeaves.Count;
    public bool InvariantHoldsNow(out string msg) => _qt.NeighborInvariantHolds(_lastLeaves, out msg);

    public void Setup(ShaderMaterial mat, FieldParams p, float minH, float maxH, float[] heights)
    {
        _mat = mat; _minH = minH; _maxH = maxH; _regionSize = p.RegionSizeM;
        _heights = heights; _hRes = p.HeightmapRes;
        _grid = CdlodMesh.BuildGrid(GridN);
        _variants = CdlodMesh.BuildStitchedVariants(GridN);   // S2d: 16 welded edge-stitch variants (by mask)
        _qt = new CdlodQuadtree(-_regionSize * 0.5f, -_regionSize * 0.5f, _regionSize, MaxDepth, SplitFactor);
        _coarseSnap = _regionSize;   // S3: snap renderOrigin to the coarsest chunk grid (root size)
        GD.Print($"CdlodTerrain: setup gridN={GridN} maxDepth={MaxDepth} split={SplitFactor} region={_regionSize:F0}");
    }

    /// Generous vertical bound from the field's global height envelope. S3: works for a streamed chunk
    /// ANYWHERE (no baked region needed) — born with this loose-but-safe AABB so it renders + shadows with no
    /// pop-in; the async ChunkAabbProvider (Task 5) tightens it a few frames later for cascade precision.
    /// A TIGHT per-chunk vertical AABB matters: Godot fits the directional shadow cascade's depth range to
    /// caster AABBs, so a full-region-tall AABB on every small chunk inflates the shadow-map depth range and
    /// destroys precision → soft self-shadow blobs. Until Task 5 tightens, this generous-but-safe outer bound
    /// keeps chunks rendering+shadowing everywhere with no pop-in (slightly loose far-out).
    private (float lo, float hi) ChunkHeightRange(Vector2 originXZ, float size)
    {
        return (_minH - 200f, _maxH + 200f);   // + safety margin for far-out amplitude beyond the sampled region
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
        // S3: snapped camera-relative render origin (folded floating-origin).
        _renderOrigin = new Vector3(
            Mathf.Floor(camPos.X / _coarseSnap) * _coarseSnap, 0f,
            Mathf.Floor(camPos.Z / _coarseSnap) * _coarseSnap);
        _mat?.SetShaderParameter("render_origin", _renderOrigin);
        List<CdlodChunk> leaves = _qt.SelectRoaming(camPos);   // S3: roaming root → infinite streaming
        _lastLeaves = leaves;   // S2b: expose to the test-path report (count + along-path invariant)
        EnsurePool(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            MeshInstance3D mi = _pool[i];
            float half = c.Size * 0.5f;
            // S3: RENDER-RELATIVE position (true center − renderOrigin); shader adds render_origin back.
            mi.Position = new Vector3(c.OriginXZ.X + half - _renderOrigin.X, 0f, c.OriginXZ.Y + half - _renderOrigin.Z);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);    // X/Z = chunk size; Y = 1 (world-unit height)
            // CustomAabb is LOCAL (pre-node-scale): X/Z = the unit grid (the node Scale stretches it to
            // the chunk footprint); Y = this chunk's TIGHT world height range (Y scale is 1). Tight Y is
            // required for shadow-cascade depth precision — see ChunkHeightRange.
            var (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size);
            // Vertical AABB margin. ChunkHeightRange samples the baked map at a COARSE stride, and S2b's
            // geomorph displaces each vertex's sample by up to ~one chunk vertex-span (chunk_size/(GridN-1))
            // toward the coarse grid — so on steep ground the actual displaced verts can sit well outside a
            // flat 8 m margin. Too tight -> the CSM cascade depth range misses those verts -> grid-aligned
            // shadow ACNE (dotted stipple, worst on slopes). Too loose -> inflated cascade depth -> soft
            // blobs (memory cdlod-chunk-shadow-aabb). Scale the margin to the chunk's vertex spacing (the
            // morph-displacement bound) with an 8 m floor: tight for fine chunks, enough for coarse ones.
            float m = Mathf.Max(8f, c.Size / (GridN - 1) * 1.5f);
            mi.CustomAabb = new Aabb(new Vector3(-0.5f, lo - m, -0.5f),
                                     new Vector3(1f, (hi - lo) + 2f * m, 1f));
            mi.SetInstanceShaderParameter("lod_viz", _lodViz ? (float)c.Level : -1.0f);
            // S2d: pick the edge-stitch variant for this chunk's neighbor configuration (mask). Welded
            // edges coincide with the coarser neighbor -> no crack. Falls back to the flat grid if variants
            // somehow weren't built. Only reassign when the mask CHANGED (avoid per-frame mesh re-upload churn).
            if (_variants.Length == 16)
            {
                int sm = c.StitchMask & 15;
                if (_poolMask[i] != sm) { mi.Mesh = _variants[sm]; _poolMask[i] = sm; }
            }
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
            _poolMask.Add(-1);   // S2d: -1 = no variant assigned yet (forces first Tick to set the mesh)
        }
    }
}
