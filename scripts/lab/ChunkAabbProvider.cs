using Godot;
using System;
using System.Collections.Generic;
using WG16.Field;

namespace WG16.Lab;

/// S3 Task 5 — async GPU per-chunk height-range (min/max) for tightening a streamed chunk's shadow AABB,
/// WITHOUT stalling the game thread. Self-contained + tunable; the streaming core does not depend on its
/// result (chunks render with a generous AABB until a tighten lands), so a GPU-threading problem can't break
/// the infinite world — it only IMPROVES the AABB.
///
/// Mechanism (memory compute-to-material-callonrenderthread): all GPU work runs on the MAIN render-thread RD
/// via RenderingServer.CallOnRenderThread (FieldCompute's LOCAL RD Submit()/Sync() is blocking — no
/// cross-frame fence). A request is queued on the game thread; each Pump() hands up to MaxRequestsPerFrame
/// requests to a render-thread callback that dispatches field_height over a ProbeRes×ProbeRes grid, reads the
/// buffer back ON THE RENDER THREAD (so the game thread never blocks), reduces to min/max, and pushes the
/// result into a completed queue the game thread drains via TryTake. Born-generous → tightened-in-place a few
/// frames later (imperceptible: far chunks, brief, no geometry change). Windowed only (render RD NullRefs
/// headless — memory headless-no-local-rendering-device).
public sealed class ChunkAabbProvider
{
    public int ProbeRes = 7;             // S3: coarse grid side for the height-range probe (tunable; 5..9)
    public int MaxRequestsPerFrame = 8;  // S3: GPU dispatches handed to the render thread per Pump (throttle)

    private struct Req { public long Key; public Vector2 OriginXZ; public float Size; }
    private struct Done { public long Key; public float Lo; public float Hi; }

    private readonly FieldParams _p;
    // Game-thread request queue + dedup set (a chunk address is queued once until taken).
    private readonly Queue<Req> _pending = new();
    private readonly HashSet<long> _queued = new();
    // Completed results: produced on the render thread, drained on the game thread. Lock-guarded (cross-thread).
    private readonly List<Done> _done = new();
    private readonly object _doneLock = new();

    // Render-thread RD state (created once on the render thread in the first Pump).
    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private bool _rtReady;
    private bool _rtFailed;

    public ChunkAabbProvider(FieldParams p) { _p = p; }

    /// Pre-compile the field shader on the render thread NOW (during scene load) so the one-time SPIR-V compile
    /// + pipeline create never lands on the first chunk-birth frame mid-session (a ~100 ms cold-start stall if
    /// it coincides with the other render-thread inits). Idempotent.
    public void Prewarm() { RenderingServer.CallOnRenderThread(Callable.From(EnsureRt)); }

    /// Queue a tighten request for a chunk footprint, keyed by its stable world address (level,x,z). Deduped:
    /// a key already queued (or in flight) is ignored until its result is taken.
    public void Request(long key, Vector2 originXZ, float size)
    {
        if (_queued.Contains(key)) { return; }
        _queued.Add(key);
        _pending.Enqueue(new Req { Key = key, OriginXZ = originXZ, Size = size });
    }

    /// Drain one completed result. Returns false when none are ready. Game thread.
    public bool TryTake(out long key, out float lo, out float hi)
    {
        lock (_doneLock)
        {
            if (_done.Count == 0) { key = 0; lo = hi = 0f; return false; }
            Done d = _done[_done.Count - 1];
            _done.RemoveAt(_done.Count - 1);
            key = d.Key; lo = d.Lo; hi = d.Hi;
        }
        _queued.Remove(key);   // taken → allow a future re-request if the slot's address recurs
        return true;
    }

    /// Game-thread per-frame entry: dispatch up to MaxRequestsPerFrame queued requests on the render thread.
    /// Cheap when the queue is empty (no callback scheduled).
    public void Pump()
    {
        if (_pending.Count == 0 || _rtFailed) { return; }
        int n = Mathf.Min(MaxRequestsPerFrame, _pending.Count);
        var batch = new List<Req>(n);
        for (int i = 0; i < n; i++) { batch.Add(_pending.Dequeue()); }
        RenderingServer.CallOnRenderThread(Callable.From(() => ProcessBatch(batch)));
    }

    // ---- render-thread side ----

    private void EnsureRt()
    {
        if (_rtReady || _rtFailed) { return; }
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { _rtFailed = true; return; }
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/field_height.glsl");
        string mathPath = ProjectSettings.GlobalizePath("res://shaders/field_math.gdshaderinc");
        string mathSrc = System.IO.File.ReadAllText(mathPath);
        string src = System.IO.File.ReadAllText(shaderPath)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty)
            .Replace("// @@INCLUDE field_math", mathSrc);   // RD-GLSL has no #include (same splice as FieldCompute)
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            GD.PrintErr("ChunkAabbProvider field_height.glsl: " + spirv.CompileErrorCompute);
            _rtFailed = true; return;
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "field_height_aabb");
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _rtReady = true;
    }

    private void ProcessBatch(List<Req> batch)
    {
        EnsureRt();
        if (!_rtReady) { return; }
        foreach (Req r in batch)
        {
            (float lo, float hi) = HeightRange(r.OriginXZ, r.Size);
            lock (_doneLock) { _done.Add(new Done { Key = r.Key, Lo = lo, Hi = hi }); }
        }
    }

    /// Dispatch field_height over a ProbeRes×ProbeRes grid covering [origin, origin+size] and reduce to
    /// min/max. Runs on the render-thread RD: the BufferGetData here syncs on the RENDER thread (the game
    /// thread is not blocked — this whole method runs inside the CallOnRenderThread callback). Resources are
    /// transient (freed each call) — the dispatches are tiny (≤9²) so churn is negligible.
    private (float lo, float hi) HeightRange(Vector2 originXZ, float size)
    {
        int res = Mathf.Max(2, ProbeRes);
        float spacing = size / (res - 1);   // span exactly the footprint edge-to-edge
        int cells = res * res;

        Rid buffer = _rd.StorageBufferCreate((uint)(cells * sizeof(float)));
        var hUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        hUniform.AddId(buffer);
        byte[] pbytes = FieldCompute.PackParamsBytes(_p, originXZ.X, originXZ.Y, spacing, (uint)res, 0);
        Rid paramsBuf = _rd.StorageBufferCreate((uint)pbytes.Length, pbytes);
        var pUniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        pUniform.AddId(paramsBuf);
        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { hUniform, pUniform }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();   // barrier so the readback sees the result

        byte[] bytes = _rd.BufferGetData(buffer);
        float lo = float.MaxValue, hi = float.MinValue;
        int got = Mathf.Min(cells, bytes.Length / sizeof(float));
        for (int i = 0; i < got; i++)
        {
            float v = BitConverter.ToSingle(bytes, i * sizeof(float));
            if (v < lo) { lo = v; }
            if (v > hi) { hi = v; }
        }
        _rd.FreeRid(set);
        _rd.FreeRid(buffer);
        _rd.FreeRid(paramsBuf);
        if (lo > hi) { lo = 0f; hi = 0f; }
        return (lo, hi);
    }

    public void Dispose()
    {
        if (!_rtReady) { return; }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_pipeline.IsValid) { _rd.FreeRid(_pipeline); }
            if (_shader.IsValid) { _rd.FreeRid(_shader); }
        }));
    }
}
