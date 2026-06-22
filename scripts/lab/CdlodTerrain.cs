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
    private readonly List<long> _poolKey = new();   // S3.5: per-slot chunk WORLD address (level,x,z) — re-request a tighten only when a slot's address changes
    private List<CdlodChunk> _lastLeaves = new();   // S2b: last Tick's selected leaves (for the test-path along-path invariant/count report)
    private bool _enabled;
    private bool _lodViz;

    // S3.5: async GPU AABB tightening. Chunks are born with the generous AABB (no pop-in); the provider
    // tightens each in place a few frames later via an async render-thread height-range. Self-contained +
    // tunable; TightenAabb=false falls back to the always-safe generous AABB.
    private FieldParams _fieldParams = null!;
    private ChunkAabbProvider _aabbProvider;
    public bool TightenAabb = true;                 // S3.5: disable → keep the generous AABB (fallback)
    private readonly Dictionary<long, (float lo, float hi)> _tightened = new();   // key → landed tight range
    private float _margin;   // current chunk's AABB Y margin (set per-leaf; reused when a tighten lands)

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable
    public int MaxChunkOps = 24;      // S3: max pooled-chunk births per frame (amortize streaming churn; tunable)

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
        _fieldParams = p;
        _aabbProvider = new ChunkAabbProvider(p);   // S3.5: async height-range provider (render-thread RD lazily inited)
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

    /// S3.5: configure the async AABB tightener (CLI/lab tunables). probeRes/maxReq <= 0 leave the default.
    public void ConfigureAabb(bool tighten, int probeRes = 0, int maxReq = 0)
    {
        TightenAabb = tighten;
        if (_aabbProvider != null)
        {
            if (probeRes > 0) { _aabbProvider.ProbeRes = probeRes; }
            if (maxReq > 0) { _aabbProvider.MaxRequestsPerFrame = maxReq; }
        }
        GD.Print($"CdlodTerrain: AABB tighten={(tighten ? "on" : "OFF")} probeRes={_aabbProvider?.ProbeRes} maxReq={_aabbProvider?.MaxRequestsPerFrame}");
    }

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
        DrainTightened();   // S3.5: collect any async height-ranges that landed since last frame
        int n = Mathf.Min(leaves.Count, _pool.Count);   // S3: budget may cap the pool below leaf count this frame
        for (int i = 0; i < n; i++)
        {
            CdlodChunk c = leaves[i];
            MeshInstance3D mi = _pool[i];
            float half = c.Size * 0.5f;
            // S3: RENDER-RELATIVE position (true center − renderOrigin); shader adds render_origin back.
            mi.Position = new Vector3(c.OriginXZ.X + half - _renderOrigin.X, 0f, c.OriginXZ.Y + half - _renderOrigin.Z);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);    // X/Z = chunk size; Y = 1 (world-unit height)
            // S3.5: this slot's chunk WORLD ADDRESS (level,x,z). When it CHANGES the slot now shows a
            // different chunk → queue an async tighten for the new address (deduped in the provider).
            long key = ChunkKey(c);
            if (_poolKey[i] != key && TightenAabb)
            {
                _aabbProvider.Request(key, c.OriginXZ, c.Size);
                _poolKey[i] = key;
            }
            // CustomAabb is LOCAL (pre-node-scale): X/Z = the unit grid (the node Scale stretches it to
            // the chunk footprint); Y = this chunk's world height range (Y scale is 1). Tight Y is required
            // for shadow-cascade depth precision. Born GENEROUS (ChunkHeightRange) so it renders+shadows with
            // no pop-in; once the async tighten for this key has landed, use the tight range instead.
            float lo, hi;
            if (TightenAabb && _tightened.TryGetValue(key, out var tr)) { lo = tr.lo; hi = tr.hi; }
            else { (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size); }
            // Vertical AABB margin. S2b's geomorph displaces each vertex's sample by up to ~one chunk
            // vertex-span (chunk_size/(GridN-1)) toward the coarse grid — so on steep ground the actual
            // displaced verts can sit well outside a flat 8 m margin. Too tight -> the CSM cascade depth range
            // misses those verts -> grid-aligned shadow ACNE (dotted stipple, worst on slopes). Too loose ->
            // inflated cascade depth -> soft blobs (memory cdlod-chunk-shadow-aabb). Scale the margin to the
            // chunk's vertex spacing (the morph-displacement bound) with an 8 m floor.
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
        for (int i = n; i < _pool.Count; i++) { _pool[i].Visible = false; }
        if (TightenAabb) { _aabbProvider.Pump(); }   // S3.5: dispatch queued tighten requests on the render thread
    }

    /// S3.5: drain async height-ranges that have landed and cache them by key so the per-leaf loop applies the
    /// tight AABB. Bounded keep: only addresses currently of interest stay (a slot re-requests if it recurs).
    private void DrainTightened()
    {
        while (_aabbProvider.TryTake(out long key, out float lo, out float hi))
        {
            _tightened[key] = (lo, hi);
        }
        // Bound the cache as the camera streams an infinite world: when it outgrows a generous multiple of the
        // live leaf set, drop everything not currently visible (those re-request cheaply if revisited). Cheap
        // amortized — runs only when the cap is exceeded.
        if (_tightened.Count > 8192)
        {
            var live = new HashSet<long>(_poolKey);
            var stale = new List<long>();
            foreach (var k in _tightened.Keys) { if (!live.Contains(k)) { stale.Add(k); } }
            foreach (var k in stale) { _tightened.Remove(k); }
        }
    }

    /// Stable per-chunk key from its WORLD address (level + integer XZ origin). Quantize the origin to whole
    /// metres (chunk origins are exact powers-of-two grid points, so this is lossless) and pack into 64 bits.
    private static long ChunkKey(CdlodChunk c)
    {
        long xi = (long)Mathf.Round(c.OriginXZ.X);
        long zi = (long)Mathf.Round(c.OriginXZ.Y);
        // pack: level (8 bits) | x (28 bits) | z (28 bits), biased to keep negatives positive.
        long x = (xi + (1L << 27)) & 0xFFFFFFF;
        long z = (zi + (1L << 27)) & 0xFFFFFFF;
        return ((long)(c.Level & 0xFF) << 56) | (x << 28) | z;
    }

    public override void _ExitTree()
    {
        _aabbProvider?.Dispose();   // S3.5: free the render-thread field shader/pipeline
    }

    private void EnsurePool(int n)
    {
        int births = 0;
        while (_pool.Count < n && births < MaxChunkOps)   // S3: cap births/frame so a fast camera doesn't spike
        {
            var mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat };
            AddChild(mi);
            _pool.Add(mi);
            _poolMask.Add(-1);   // S2d: -1 = no variant assigned yet (forces first Tick to set the mesh)
            _poolKey.Add(-1L);   // S3.5: -1 = no chunk address assigned yet (forces first Tick to request a tighten)
            births++;
        }
    }
}
