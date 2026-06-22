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
    // S3.6: CHUNK-IDENTITY-KEYED pool. The instance for a chunk is keyed by its world address (level,x,z),
    // NOT by list index — so a chunk that's still visible keeps its instance and is NOT re-pushed to the
    // rendering server every frame. Only genuine births/deaths/state-changes touch the BVH. This is what kills
    // the S2 "rebuild spike": the old index-keyed pool re-wrote Position+Scale+AABB(+mesh) for EVERY slot
    // every frame in motion (~474 BVH re-fits/frame) because SelectRoaming's traversal ORDER shifts as the
    // camera moves — even for chunks that didn't change. True per-frame churn is only ~5-15 chunks (measured),
    // so identity-keying cuts the per-frame instance touches ~30-95×.
    private sealed class ChunkSlot
    {
        public MeshInstance3D Mi = null!;
        public int Mask = -1;          // last-applied stitch-variant mask (-1 = unset)
        public bool Tightened;         // has this slot's AABB been set from a landed async tighten?
        public Vector2 OriginXZ;       // chunk world origin (for re-applying the render-relative position on a snap)
        public float Size;
        public int Level;
        public int SeenFrame = -1;     // last Tick frame this slot was in the visible set (vs _frame → retire)
    }
    private int _frame;   // monotonic Tick counter for the seen-this-frame test (no per-slot Variant churn)
    private bool _forceReapply;   // one-shot: re-push lod_viz + AABB to ALL live slots next Tick (live toggle A/B)
    private bool _aabbReset;      // one-shot: clear landed tights so existing chunks revert to the generous AABB
    private readonly Dictionary<long, ChunkSlot> _active = new();   // live chunks by world-address key
    private readonly Stack<MeshInstance3D> _free = new();           // retired instances, hidden, ready to reuse
    private readonly List<long> _scratchDead = new();              // reused per-frame: keys to retire (no per-frame alloc)
    private Vector3 _lastRenderOrigin = new(float.NaN, 0f, float.NaN);   // detect a snap → re-apply positions
    private int _instanceCount;   // total MeshInstance3D ever created (pool high-water; for Ensure/diagnostics)
    private List<CdlodChunk> _lastLeaves = new();   // S2b: last Tick's selected leaves (for the test-path along-path invariant/count report)
    private bool _enabled;
    private bool _lodViz;

    // S3.5: async GPU AABB tightening. Chunks are born with the generous AABB (no pop-in); the provider
    // tightens each in place a few frames later via an async render-thread height-range. Self-contained +
    // tunable; TightenAabb=false falls back to the always-safe generous AABB.
    private FieldParams _fieldParams = null!;
    private ChunkAabbProvider _aabbProvider;
    public bool TightenAabb = true;                 // S3.5: disable → keep the generous AABB (fallback)
    public bool PinOrigin = false;                  // DEBUG (--pinorigin): pin renderOrigin=0 (no snap) to isolate the snap-pop
    private readonly Dictionary<long, (float lo, float hi)> _tightened = new();   // key → landed tight range

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable
    public int MaxChunkOps = 24;      // S3: max pooled-chunk births per frame (amortize streaming churn; tunable)
    public int RetireGrace = 2;       // S3.6: frames a chunk may be unseen before retiring (bridges the budget-deferred birth hole without leaking; >=2 retires)

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
        _aabbProvider = new ChunkAabbProvider(p);   // S3.5: async height-range provider (render-thread RD)
        _aabbProvider.Prewarm();   // S3.6: compile the field shader during load, not on the first birth (no cold-start stall)
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
        if (!on)   // hide every live + free instance (S3.6 keyed pool)
        {
            foreach (var kv in _active) { kv.Value.Mi.Visible = false; }
            foreach (var mi in _free) { mi.Visible = false; }
        }
        GD.Print($"CdlodTerrain: {(on ? "ENABLED" : "disabled")}");
    }

    public void SetLodViz(bool on) { _lodViz = on; _forceReapply = true; }   // re-push lod_viz to existing chunks

    /// S3.5: configure the async AABB tightener (CLI/lab tunables). probeRes/maxReq <= 0 leave the default.
    public void ConfigureAabb(bool tighten, int probeRes = 0, int maxReq = 0)
    {
        bool was = TightenAabb;
        TightenAabb = tighten;
        if (_aabbProvider != null)
        {
            if (probeRes > 0) { _aabbProvider.ProbeRes = probeRes; }
            if (maxReq > 0) { _aabbProvider.MaxRequestsPerFrame = maxReq; }
        }
        // Toggling tighten OFF→ON or ON→OFF must re-AABB the EXISTING chunks (live A/B), not just new ones.
        if (was != tighten)
        {
            _forceReapply = true;
            if (!tighten) { _aabbReset = true; }   // OFF: drop landed tights so existing chunks snap back to generous
        }
        GD.Print($"CdlodTerrain: AABB tighten={(tighten ? "on" : "OFF")} probeRes={_aabbProvider?.ProbeRes} maxReq={_aabbProvider?.MaxRequestsPerFrame}");
    }

    public bool TightenEnabled => TightenAabb;
    public bool LodVizEnabled => _lodViz;
    public bool Enabled => _enabled;   // S3 floating-origin: Process only co-locates the camera when CDLOD is live

    public void Tick(Vector3 camPos)
    {
        if (!_enabled) { return; }
        if (!IsInsideTree()) { return; }   // the AddChild is deferred (see TerrainLab.Build); skip until in-tree
        // S3: snapped camera-relative render origin (folded floating-origin).
        _renderOrigin = PinOrigin ? Vector3.Zero : new Vector3(
            Mathf.Floor(camPos.X / _coarseSnap) * _coarseSnap, 0f,
            Mathf.Floor(camPos.Z / _coarseSnap) * _coarseSnap);
        _mat?.SetShaderParameter("render_origin", _renderOrigin);
        // A snap shifts every chunk's render-relative position → re-apply positions for all live slots this
        // frame. Snaps are rare (every 8192 m of travel), so this is a once-per-snap cost, not per-frame.
        bool snapped = _renderOrigin.X != _lastRenderOrigin.X || _renderOrigin.Z != _lastRenderOrigin.Z;
        _lastRenderOrigin = _renderOrigin;

        List<CdlodChunk> leaves = _qt.SelectRoaming(camPos);   // S3: roaming root → infinite streaming
        _lastLeaves = leaves;   // S2b: expose to the test-path report (count + along-path invariant)
        // Live A/B toggles (lodviz / tighten) force a one-frame full re-apply of existing chunks.
        if (_aabbReset) { _tightened.Clear(); foreach (var kv in _active) { kv.Value.Tightened = false; } _aabbReset = false; }
        bool force = _forceReapply; _forceReapply = false;
        DrainTightened();   // S3.5: collect any async height-ranges that landed since last frame

        // S3.6: identity-keyed reconcile. For each leaf still present: stamp it seen + UPDATE-ONLY-DELTAS.
        // Birth the genuinely-new (budget-capped). Retire whatever wasn't stamped this frame. A monotonic
        // _frame counter is the "seen" test — no per-slot Variant/Meta churn.
        snapped |= force;   // a forced re-apply re-pushes position+lod_viz+AABB to every live slot this frame
        _frame++;
        int births = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            long key = ChunkKey(c);
            if (_active.TryGetValue(key, out ChunkSlot slot))
            {
                slot.SeenFrame = _frame;            // still visible → keep
                ApplyChunk(slot, c, key, snapped);  // re-pushes ONLY what changed (mask / landed tighten / snap)
            }
            else
            {
                if (births >= MaxChunkOps) { continue; }   // S3: churn budget — rest appear next frame(s); RetireGrace bridges the deferred-birth hole
                births++;
                ChunkSlot ns = AcquireSlot();
                ns.SeenFrame = _frame;
                _active[key] = ns;
                if (TightenAabb) { _aabbProvider.Request(key, c.OriginXZ, c.Size); }   // queue a tighten for the new chunk
                ApplyChunk(ns, c, key, snapped: true);   // new slot → apply everything (treat as snapped)
            }
        }

        // Retire slots not seen this frame → hide + return to the free-list (NOT freed; reused next birth).
        // VANISHING-CHUNK FIX (grace period): a chunk gets RetireGrace frames of being unseen before it's
        // actually hidden. When flying fast a parent can leave the leaf set the SAME frame its replacement
        // children are budget-deferred (births capped at MaxChunkOps) — retiring it immediately leaves a HOLE
        // (parent hidden, children not yet born) → the brief flash the user saw. The grace bridges that: the
        // deferred children (24/frame) land within a frame or two and cover the spot before the parent retires.
        // CRUCIAL: the grace is BOUNDED (unlike the old "skip all retires when budgetHit", which under sustained
        // orbit churn let `active` balloon to ~8× the leaf count — a leak). A chunk unseen for >= RetireGrace
        // frames ALWAYS retires, so steady-state active stays ~leafCount and sustained saturation can't pile up.
        _scratchDead.Clear();
        foreach (var kv in _active) { if (_frame - kv.Value.SeenFrame >= RetireGrace) { _scratchDead.Add(kv.Key); } }
        for (int i = 0; i < _scratchDead.Count; i++)
        {
            ChunkSlot dead = _active[_scratchDead[i]];
            dead.Mi.Visible = false;
            _free.Push(dead.Mi);
            _active.Remove(_scratchDead[i]);
        }

        if (TightenAabb) { _aabbProvider.Pump(); }   // S3.5: dispatch queued tighten requests on the render thread
    }

    /// Push to the instance ONLY the state that actually changed for this chunk — the heart of the S3.6
    /// spike fix. An unchanged, still-visible chunk does ZERO rendering-server work here (no Position/Scale/
    /// AABB/mesh re-write → no BVH re-fit). Position re-applies only on a snap; mesh only on a mask change;
    /// AABB only when a tighten lands or the slot is new.
    private void ApplyChunk(ChunkSlot slot, CdlodChunk c, long key, bool snapped)
    {
        MeshInstance3D mi = slot.Mi;
        bool isNew = slot.Level < 0;   // AcquireSlot sets Level=-1 to force a full first apply
        if (snapped || isNew)
        {
            float half = c.Size * 0.5f;
            // S3: RENDER-RELATIVE position (true center − renderOrigin); shader adds render_origin back.
            mi.Position = new Vector3(c.OriginXZ.X + half - _renderOrigin.X, 0f, c.OriginXZ.Y + half - _renderOrigin.Z);
            mi.Scale = new Vector3(c.Size, 1f, c.Size);    // X/Z = chunk size; Y = 1 (world-unit height)
            slot.OriginXZ = c.OriginXZ; slot.Size = c.Size; slot.Level = c.Level;
            mi.SetInstanceShaderParameter("lod_viz", _lodViz ? (float)c.Level : -1.0f);
            mi.Visible = true;
        }
        // AABB: born GENEROUS (no pop-in); tightened in place once the async height-range lands. Re-set only
        // when new, on a snap (position changed), or when a tighten newly lands for this key.
        bool tightAvail = TightenAabb && _tightened.TryGetValue(key, out var tr);
        if (isNew || snapped || (tightAvail && !slot.Tightened))
        {
            float lo, hi;
            bool fromProbe = tightAvail;
            if (tightAvail) { (lo, hi) = _tightened[key]; slot.Tightened = true; }
            else { (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size); }
            // Margin scaled to the chunk's vertex spacing (the geomorph displacement bound) with an 8 m floor:
            // too tight → CSM cascade misses displaced verts → grid-aligned shadow acne; too loose → inflated
            // cascade depth → soft blobs (memory cdlod-chunk-shadow-aabb).
            float m = Mathf.Max(8f, c.Size / (GridN - 1) * 1.5f);
            // S3.5 cull-pop fix: a TIGHTENED range comes from the coarse ProbeRes×ProbeRes height probe, whose
            // samples are spaced size/(ProbeRes-1) apart — far wider than the mesh's verts on big chunks (e.g.
            // an 8192 m chunk: ~1365 m probe vs 128 m mesh). The probe can MISS a real peak/valley the mesh
            // renders, so its lo/hi can be too short → when the tighten lands a few frames after birth, Godot
            // frustum/shadow-culls the chunk while its true geometry is still on screen → it DISAPPEARS, then
            // reappears on the next re-eval (the intermittent "vanishing chunk"). Pad the margin by half the
            // probe spacing so the tightened AABB can never be shorter than the mesh between probe samples. This
            // scales with chunk size, so far/big chunks (coarsest probe, least shadow-precision need) get a
            // looser-but-safe box while near/small chunks (fine probe) stay tight.
            if (fromProbe) { m = Mathf.Max(m, c.Size / Mathf.Max(1, _aabbProvider.ProbeRes - 1) * 0.5f); }
            mi.CustomAabb = new Aabb(new Vector3(-0.5f, lo - m, -0.5f), new Vector3(1f, (hi - lo) + 2f * m, 1f));
        }
        // S2d: edge-stitch variant — reassign the mesh ONLY when this chunk's mask changed (avoids per-frame
        // mesh re-upload). The stitch mask CAN change frame-to-frame as neighbors split/merge, even when the
        // chunk itself is unchanged — so this is the one per-frame check that legitimately remains.
        if (_variants.Length == 16)
        {
            int sm = c.StitchMask & 15;
            if (slot.Mask != sm) { mi.Mesh = _variants[sm]; slot.Mask = sm; }
        }
    }

    /// Get an instance for a new chunk: reuse a retired one from the free-list, else create one (bounded by the
    /// churn budget at the call site). Marked Level=-1 so ApplyChunk does a full first push.
    private ChunkSlot AcquireSlot()
    {
        MeshInstance3D mi;
        if (_free.Count > 0) { mi = _free.Pop(); }
        else { mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat }; AddChild(mi); _instanceCount++; }
        return new ChunkSlot { Mi = mi, Mask = -1, Tightened = false, Level = -1 };
    }

    /// S3.5: drain async height-ranges that have landed and cache them by key. The keyed pool's ApplyChunk
    /// picks them up for the matching live slot on this/next frame. Bounded to avoid unbounded growth as the
    /// camera streams an infinite world.
    private void DrainTightened()
    {
        while (_aabbProvider.TryTake(out long key, out float lo, out float hi))
        {
            _tightened[key] = (lo, hi);
        }
        if (_tightened.Count > 8192)
        {
            _scratchDead.Clear();
            foreach (var k in _tightened.Keys) { if (!_active.ContainsKey(k)) { _scratchDead.Add(k); } }
            for (int i = 0; i < _scratchDead.Count; i++) { _tightened.Remove(_scratchDead[i]); }
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
}
