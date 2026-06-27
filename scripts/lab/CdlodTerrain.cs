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
        public int CacheSlot = -1;     // field-cache texture-array layer for this chunk (-1 = none → live-eval path)
        public bool CacheReady;        // the chunk's bake has landed → vertex shader samples the cache, not the field
        public bool IsFar;             // horizon shell chunk: no field cache, no AABB
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
    // Per-chunk field CACHE (GPU-compute): bake height+normal on birth, sample instead of evaluating the field
    // 5×/vertex/frame. Subsumes the AABB probe (its baked grid yields the min/max). Reversible (--fieldcache=0).
    public bool FieldCache = true;
    private ChunkFieldCache _fieldCache;
    private bool _cacheBound;                        // material bound to the cache texture once its RID is live
    private const int CacheSlots = 1280;             // texture-array layers (cap ≥ steady-state active chunks; ~900 at Ring 8). 1280×67²×16B ≈ 90 MB
    private readonly Stack<int> _freeLayers = new(); // free texture-array layer indices
    // Defer flipping cache_ready until the GPU bake has DEFINITELY completed (the dispatch is fire-and-forget on
    // the render thread; flipping the same frame it lands can race the imageStore → a reused layer shows the
    // PREVIOUS chunk's data for a frame = a per-chunk flicker during motion). Hold landed bakes this many Ticks.
    private const int CacheReadyDelay = 3;
    // Delay returning cache layers to the free pool: a retired chunk's bake may still be in-flight on the render
    // thread. Returning the layer immediately lets a new chunk claim it before the old imageStore completes →
    // the new chunk's data is overwritten by the old bake for a frame = wrong/flat chunk during fast movement.
    private const int CacheLayerFreeDelay = 8;
    private readonly List<(int slot, int releaseFrame)> _cacheLayersPendingFree = new();
    private readonly List<(long key, int slot, int landFrame)> _cachePending = new();
    public bool PinOrigin = false;                  // DEBUG (--pinorigin): pin renderOrigin=0 (no snap) to isolate the snap-pop
    private readonly Dictionary<long, (float lo, float hi)> _tightened = new();   // key → landed tight range (7×7 probe)

    public int GridN = 65;            // verts/side per chunk (64 quads)
    public int MaxDepth = 6;          // finest LOD depth; tunable
    public float SplitFactor = 2.5f;  // subdivide when camDist < size*splitFactor; tunable
    public int MaxChunkOps = 24;      // near chunk births/frame (priority budget); --chunkops overrides
    public int FarChunkOps = 4;       // far/horizon root chunk births/frame (background budget; they fill lazily)
    public int MaxChunkOpsCeil = 256; // DISABLED — kept as diagnostic reference only, not used in default path
    public float ChunkOpsPerSpeed = 0.01f;   // DISABLED — kept as diagnostic reference only, not used in default path
    public int LoadRing = 8;          // full horizon ring (near + far shell); --loadring=N overrides
    public int ActiveRing = 4;        // near/far boundary — sub-root chunks (near CDLOD) vs root chunks (horizon shell)
    public float AabbTightenMaxSpeed = 250f;          // skip render-thread AABB readbacks during fast traversal; catch up when motion settles
    public float CenterHysteresis = 0.35f;   // ARC B Task 2: dead-band (× root size) the camera must travel PAST a
                                             // center-cell boundary before the loaded window re-centers (anti-thrash near a seam).
                                             // Widened 0.15→0.35: light taps near a cell seam were re-centering the window and
                                             // visibly toggling trailing-edge chunks load/unload.
    private Vector2I _centerCell;     // ARC B Task 2: current (hysteretic) window-center cell index
    private bool _centerInit;         // false until _centerCell is seeded from the first Tick's camera cell
    public float PredictLookahead = 0.0f;   // ARC B Task 4: seconds of camera velocity to bias the window-center
                                            // forward by (loads INTO the direction of travel so you can't outrun it; 0 = off).
                                            // DEFAULT OFF: a tap's velocity transient (spike → decay to 0) swung the biased
                                            // center out-and-back, toggling trailing-edge chunks. With the big radius (Ring 8 ≈
                                            // 65 km) you can't outrun the frontier at normal speeds, so prediction isn't needed.
    public int RetireGrace = 2;       // S3.6: frames a chunk may be unseen before retiring (bridges the budget-deferred birth hole without leaking; >=2 retires).
                                      // KEEP LOW: a merged chunk lingers Visible over its coarser replacement for these many frames
                                      // (LOD overlap → shimmer/despawn pop). 2 ≈ 33 ms (imperceptible); a 10-frame bump read as visible
                                      // spawn/despawn pop. The tap-thrash is fixed by PredictLookahead=0 + CenterHysteresis, not by grace.
    public int TotalSnaps, TotalBirths;   // perf instrumentation (cumulative since enable); active chunk count = ActiveCount
    public int TotalRebirths;             // CHURN diagnostic: births of a key retired within the last 30 frames (= thrash, not clean streaming)
    public int ActiveCount => _active.Count;
    public void ActiveDiagnostics(out int near, out int far, out int shadowCasters, out int cacheReady)
    {
        near = 0; far = 0; shadowCasters = 0; cacheReady = 0;
        foreach (var kv in _active)
        {
            ChunkSlot s = kv.Value;
            if (s.IsFar) { far++; } else { near++; }
            if (s.CacheReady) { cacheReady++; }
        }
    }
    private readonly Dictionary<long, int> _recentRetire = new();   // key → frame retired (for rebirth detection)

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
        _fieldCache = new ChunkFieldCache(p, GridN, CacheSlots);   // per-chunk height+normal cache (subsumes the AABB probe)
        _fieldCache.Prewarm();
        _freeLayers.Clear();
        for (int i = CacheSlots - 1; i >= 0; i--) { _freeLayers.Push(i); }   // 0..N-1 available
        _grid = CdlodMesh.BuildGrid(GridN);
        _variants = CdlodMesh.BuildStitchedVariants(GridN);   // S2d: 16 welded edge-stitch variants (by mask)
        _qt = new CdlodQuadtree(-_regionSize * 0.5f, -_regionSize * 0.5f, _regionSize, MaxDepth, SplitFactor);
        _coarseSnap = _regionSize;   // S3: snap renderOrigin to the coarsest chunk grid (root size)
        GD.Print($"CdlodTerrain: setup gridN={GridN} maxDepth={MaxDepth} split={SplitFactor} region={_regionSize:F0}");
    }

    /// Generous vertical bound from the field's global height envelope. S3: works for a streamed chunk
    /// ANYWHERE (no baked region needed) - born with this loose-but-safe AABB so it renders with no
    /// pop-in; the async ChunkAabbProvider tightens it a few frames later for culling precision.
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
    public void SetBakeReq(int n) { if (_fieldCache != null) { _fieldCache.MaxRequestsPerFrame = Mathf.Max(1, n); } }   // field-cache bake throttle

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
        GD.Print($"CdlodTerrain: AABB tighten={(tighten ? "on" : "OFF")} probeRes={_aabbProvider?.ProbeRes} maxReq={_aabbProvider?.MaxRequestsPerFrame} speedMax={AabbTightenMaxSpeed:F0}");
    }

    public void SetAabbTightenMaxSpeed(float metersPerSecond)
    {
        AabbTightenMaxSpeed = Mathf.Max(0f, metersPerSecond);
        GD.Print($"CdlodTerrain: AABB tighten speedMax={AabbTightenMaxSpeed:F0} m/s");
    }

    public bool TightenEnabled => TightenAabb;
    public bool LodVizEnabled => _lodViz;
    public bool Enabled => _enabled;   // S3 floating-origin: Process only co-locates the camera when CDLOD is live

    public void Tick(Vector3 camPos, Vector3 velXZ = default)
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
        if (snapped) { TotalSnaps++; }   // perf instrumentation: real renderOrigin snaps (8192 m crossings, pre-force)
        float speedXZ = new Vector2(velXZ.X, velXZ.Z).Length();
        bool allowAabbTighten = TightenAabb && speedXZ <= AabbTightenMaxSpeed;

        _qt.Ring = LoadRing;   // ARC B Task 1: live-tunable load-ring radius (slider/CLI → takes effect next select)
        // ARC B Task 4: bias the window center forward by the camera velocity (clamped to ~1.5 cells so a wild
        // speed can't load beyond the ring). LOD distance + the renderOrigin snap still use the TRUE camPos.
        Vector3 bias = velXZ * Mathf.Max(0f, PredictLookahead);
        float maxBias = _regionSize * 1.5f;
        if (bias.Length() > maxBias) { bias = bias.Normalized() * maxBias; }
        Vector2 centerOrigin = ComputeCenterOrigin(camPos + bias);   // Task 2 hysteresis on the (Task 4) predicted point
        _qt.SelectRoamingInto(camPos, centerOrigin, _lastLeaves);   // S3: roaming root → infinite streaming
        List<CdlodChunk> leaves = _lastLeaves;   // S2b: expose to the test-path report (count + along-path invariant)
        // Live A/B toggles (lodviz / tighten) force a one-frame full re-apply of existing chunks.
        if (_aabbReset) { _tightened.Clear(); foreach (var kv in _active) { kv.Value.Tightened = false; } _aabbReset = false; }
        bool force = _forceReapply; _forceReapply = false;
        DrainTightened();   // S3.5: collect any async height-ranges that landed since last frame

        // S3.6: identity-keyed reconcile. For each leaf still present: stamp it seen + UPDATE-ONLY-DELTAS.
        // Birth the genuinely-new (budget-capped). Retire whatever wasn't stamped this frame. A monotonic
        // _frame counter is the "seen" test — no per-slot Variant/Meta churn.
        snapped |= force;   // a forced re-apply re-pushes position+lod_viz+AABB to every live slot this frame
        _frame++;
        ReleaseDelayedCacheLayers();
        // Near vs far birth budgets (independent so far shell can't starve near detail).
        // Root-size chunks = far horizon shell; sub-root = near CDLOD.
        int nearBirths = 0, farBirths = 0;
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            long key = ChunkKey(c);
            if (_active.TryGetValue(key, out ChunkSlot slot))
            {
                slot.SeenFrame = _frame;            // still visible → keep
                ApplyChunk(slot, c, key, snapped);  // re-pushes ONLY what changed (mask / landed tighten / snap)
                QueueAabbTightenIfAllowed(allowAabbTighten, key, c, slot);
            }
            else
            {
                // Root-size chunks are far horizon shell — separate lazy budget, no cache, no AABB.
                bool isFar = c.Size >= _regionSize;
                if (isFar)  { if (farBirths  >= FarChunkOps)  { continue; } farBirths++;  }
                else        { if (nearBirths >= MaxChunkOps)   { continue; } nearBirths++; }

                if (_recentRetire.TryGetValue(key, out int rf) && _frame - rf < 30) { TotalRebirths++; }
                ChunkSlot ns = AcquireSlot();
                ns.SeenFrame = _frame;
                ns.IsFar = isFar;
                _active[key] = ns;

                // Near only: field cache bake + AABB tighten. Far root chunks are coarse — live eval is fine.
                if (!isFar && FieldCache && _fieldCache != null && _freeLayers.Count > 0)
                {
                    ns.CacheSlot = _freeLayers.Pop(); ns.CacheReady = false;
                    _fieldCache.Request(key, ns.CacheSlot, c.OriginXZ, c.Size);
                }
                ApplyChunk(ns, c, key, snapped: true);
                QueueAabbTightenIfAllowed(allowAabbTighten, key, c, ns);
            }
        }
        TotalBirths += nearBirths + farBirths;

        // Retire slots not seen this frame → hide + return to the free-list (NOT freed; reused next birth).
        // VANISHING-CHUNK FIX (grace period): a chunk gets RetireGrace frames of being unseen before it's
        // actually hidden. When flying fast a parent can leave the leaf set the SAME frame its replacement
        // children are budget-deferred (births capped at MaxChunkOps/FarChunkOps) — retiring it immediately leaves a HOLE
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
            if (dead.CacheSlot >= 0) { _cacheLayersPendingFree.Add((dead.CacheSlot, _frame + CacheLayerFreeDelay)); dead.CacheSlot = -1; }
            _recentRetire[_scratchDead[i]] = _frame;   // CHURN diagnostic: stamp retire frame for rebirth detection
            _active.Remove(_scratchDead[i]);
        }

        if (allowAabbTighten) { _aabbProvider.Pump(); }   // S3.5: dispatch queued tighten requests on the render thread
        if (FieldCache && _fieldCache != null) { _fieldCache.Pump(); }

        // Streaming diagnostic (DebugStream): which stage lags? births capped → throttle-bound; bakePend high →
        // bake backlog; leaves≫active → can't fill; leaves small → load-ring too small. Off by default.
        if (DebugStream)
        {
            int births = nearBirths + farBirths;
            _dbgBirthsAcc += births;
            if (nearBirths >= MaxChunkOps || farBirths >= FarChunkOps) { _dbgCapped++; }
            if (_frame % 30 == 0)
            {
                int bakePend = (FieldCache && _fieldCache != null) ? _fieldCache.PendingCount : 0;
                GD.Print($"[streamdiag] f={_frame} leaves={leaves.Count} active={_active.Count} near={nearBirths}/far={farBirths} births/30={_dbgBirthsAcc} capped={_dbgCapped}/30 bakePend={bakePend} cachePend={_cachePending.Count} tights={_tightened.Count} snaps={TotalSnaps}");
                _dbgBirthsAcc = 0; _dbgCapped = 0;
            }
        }
    }
    public bool DebugStream = false;   // --streamdbg: per-30-frame streaming-state log
    private int _dbgBirthsAcc, _dbgCapped;

    private void QueueAabbTightenIfAllowed(bool allowAabbTighten, long key, CdlodChunk c, ChunkSlot slot)
    {
        if (!allowAabbTighten || slot.IsFar || slot.Tightened || _tightened.ContainsKey(key)) { return; }
        _aabbProvider.Request(key, c.OriginXZ, c.Size);
    }

    /// ARC B Task 2: the cell-aligned world origin of the (hysteretic) window-center cell. The naive center is
    /// floor(cam / root); a raw floor re-centers the instant the camera crosses a cell boundary, so oscillating
    /// across a seam thrashes a strip load/unload. The dead-band keeps the current center cell until the camera
    /// is more than CenterHysteresis·root PAST the cell's boundary (then it re-floors to the camera's true cell,
    /// which also self-corrects on a teleport/fast move). This shifts only WHICH contiguous block is selected —
    /// NOT the renderOrigin snap (still floor(cam/root) in Tick), so --snapdiff reconstruction still round-trips.
    private Vector2 ComputeCenterOrigin(Vector3 centerPoint)
    {
        float root = _regionSize;
        float band = root * Mathf.Max(0f, CenterHysteresis);
        if (!_centerInit)
        {
            _centerCell = new Vector2I(Mathf.FloorToInt(centerPoint.X / root), Mathf.FloorToInt(centerPoint.Z / root));
            _centerInit = true;
        }
        _centerCell.X = HysteresisCell(centerPoint.X, _centerCell.X, root, band);
        _centerCell.Y = HysteresisCell(centerPoint.Z, _centerCell.Y, root, band);
        return new Vector2(_centerCell.X * root, _centerCell.Y * root);
    }

    /// Keep center-cell index k until p leaves the band-expanded cell span, then re-floor to p's true cell.
    private static int HysteresisCell(float p, int k, float root, float band)
    {
        float lo = (k * root) - band;
        float hi = ((k + 1) * root) + band;
        return (p < lo || p >= hi) ? Mathf.FloorToInt(p / root) : k;
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
            mi.SetInstanceShaderParameter("chunk_slot", (float)slot.CacheSlot);     // field-cache texture-array layer
            mi.SetInstanceShaderParameter("cache_ready", slot.CacheReady ? 1.0f : 0.0f);
            mi.Visible = true;
        }
        // AABB: born GENEROUS (no pop-in); tightened in place once the async height-range lands. Re-set only
        // when new, on a snap (position changed), or when a tighten newly lands for this key.
        bool hasTight = _tightened.TryGetValue(key, out var tr);   // unconditional so `tr` is always assigned
        bool tightAvail = TightenAabb && hasTight;
        if (isNew || snapped || (tightAvail && !slot.Tightened))
        {
            float lo, hi;
            bool fromProbe = tightAvail;
            if (tightAvail) { lo = tr.lo; hi = tr.hi; slot.Tightened = true; }
            else { (lo, hi) = ChunkHeightRange(c.OriginXZ, c.Size); }
            // Margin scaled to the chunk's vertex spacing (the geomorph displacement bound) with an 8 m floor:
            // too tight -> displaced verts get culled; too loose -> excess visible bounds.
            float m = Mathf.Max(8f, c.Size / (GridN - 1) * 1.5f);
            // S3.5 cull-pop fix: a TIGHTENED range comes from the coarse ProbeRes×ProbeRes height probe, whose
            // samples are spaced size/(ProbeRes-1) apart — far wider than the mesh's verts on big chunks (e.g.
            // an 8192 m chunk: ~1365 m probe vs 128 m mesh). The probe can MISS a real peak/valley the mesh
            // renders, so its lo/hi can be too short → when the tighten lands a few frames after birth, Godot
            // frustum-culls the chunk while its true geometry is still on screen → it DISAPPEARS, then
            // reappears on the next re-eval (the intermittent "vanishing chunk"). Pad the margin by half the
            // probe spacing so the tightened AABB can never be shorter than the mesh between probe samples. This
            // scales with chunk size, so far/big chunks get a
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
        else { mi = new MeshInstance3D { Mesh = _grid, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; AddChild(mi); _instanceCount++; }
        mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        return new ChunkSlot { Mi = mi, Mask = -1, Tightened = false, Level = -1 };
    }

    /// S3.5: drain async height-ranges that have landed and cache them by key. The keyed pool's ApplyChunk
    /// picks them up for the matching live slot on this/next frame. Bounded to avoid unbounded growth as the
    /// camera streams an infinite world.
    private void ReleaseDelayedCacheLayers()
    {
        for (int i = _cacheLayersPendingFree.Count - 1; i >= 0; i--)
        {
            var item = _cacheLayersPendingFree[i];
            if (_frame < item.releaseFrame) { continue; }
            _freeLayers.Push(item.slot);
            _cacheLayersPendingFree.RemoveAt(i);
        }
    }

    private void DrainTightened()
    {
        while (_aabbProvider.TryTake(out long key, out float lo, out float hi))
        {
            _tightened[key] = (lo, hi);
        }
        // Field-cache landings: bind the texture once its render-thread RID is live, then mark each landed chunk
        // ready (vertex shader flips to sampling) + route its exact min/max into the AABB-tighten path.
        if (FieldCache && _fieldCache != null)
        {
            if (!_cacheBound && _fieldCache.Ready)
            {
                _mat?.SetShaderParameter("chunk_cache", _fieldCache.Tex);
                _mat?.SetShaderParameter("cache_side", (float)_fieldCache.Side);
                _cacheBound = true;
            }
            while (_fieldCache.TryTake(out long ckey, out int cslot))
            {
                // STALE-BAKE GUARD: only honor a landed bake if the chunk is still live AND still owns this slot
                // (a fast retire→rebirth can reassign the layer before the bake lands). Stale landings are dropped
                // — the slot's current owner has its own bake queued.
                if (!_active.TryGetValue(ckey, out ChunkSlot owner) || owner.CacheSlot != cslot) { continue; }
                _cachePending.Add((ckey, cslot, _frame));   // landed → flip to sampling after CacheReadyDelay Ticks
            }
            for (int i = _cachePending.Count - 1; i >= 0; i--)
            {
                var (pk, ps, lf) = _cachePending[i];
                if (_frame - lf < CacheReadyDelay) { continue; }   // give the GPU imageStore time to finish
                if (_active.TryGetValue(pk, out ChunkSlot s) && s.CacheSlot == ps && !s.CacheReady)
                {
                    s.CacheReady = true;
                    s.Mi.SetInstanceShaderParameter("cache_ready", 1.0f);
                }
                _cachePending.RemoveAt(i);   // resolved (flipped, or the chunk retired / reused its slot)
            }
        }
        if (_tightened.Count > 8192)
        {
            _scratchDead.Clear();
            foreach (var k in _tightened.Keys) { if (!_active.ContainsKey(k)) { _scratchDead.Add(k); } }
            for (int i = 0; i < _scratchDead.Count; i++) { _tightened.Remove(_scratchDead[i]); }
        }
        if (_recentRetire.Count > 8192)   // CHURN diagnostic dict: drop entries older than the 30-frame rebirth window
        {
            _scratchDead.Clear();
            foreach (var kv in _recentRetire) { if (_frame - kv.Value >= 30) { _scratchDead.Add(kv.Key); } }
            for (int i = 0; i < _scratchDead.Count; i++) { _recentRetire.Remove(_scratchDead[i]); }
        }
    }

    /// Stable per-chunk key from its WORLD address (level + integer XZ origin), quantized to whole metres
    /// (chunk origins are exact powers-of-two grid points, so this is lossless). HASHED into 64 bits rather
    /// than bit-packed: the old `level<<56 | x(28) | z(28)` pack masked x/z to 28 bits, which silently
    /// COLLIDED past ±2^27 m (~±134,000 km) — a hard cap on the "infinite" world. A mix has no distance cap;
    /// for the few thousand live chunks the 64-bit collision chance is ~1e-13 (far below the noise floor). The
    /// key is opaque (only ever a Dictionary key / passed to the AABB provider — never decoded), so this is a
    /// drop-in. Deterministic, so it preserves the identity-keyed pool (same chunk → same key every frame).
    private static long ChunkKey(CdlodChunk c)
    {
        long xi = (long)Mathf.Round(c.OriginXZ.X);
        long zi = (long)Mathf.Round(c.OriginXZ.Y);
        // boost-style hash_combine seeded by level, then a splitmix64 avalanche finalizer.
        ulong h = (ulong)c.Level * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)xi + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h ^= (ulong)zi + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27; h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return (long)h;
    }

    public override void _ExitTree()
    {
        _aabbProvider?.Dispose();   // S3.5: free the render-thread field shader/pipeline
    }
}
