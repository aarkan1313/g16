using Godot;
using System;
using System.Collections.Generic;
using WG16.Field;

namespace WG16.Lab;

/// Per-chunk field CACHE (GPU-compute). On chunk birth, bakes the chunk's (GridN+2)² grid of
/// (height, normal) into one slice of a persistent RD Texture2DArray, async on the render thread —
/// then the vertex shader SAMPLES it instead of evaluating the field 5×/vertex/frame. Quality-
/// identical: `field_bake.glsl` runs the SAME field_height + fixed-step normal the live shader does.
///
/// SUBSUMES ChunkAabbProvider: the same baked grid yields the chunk's height min/max, so a chunk that
/// gets a cache bake does NOT also need the 7×7 AABB probe — TryTake carries the AABB lo/hi.
///
/// Mirrors ChunkAabbProvider's threading exactly (memory compute-to-material-callonrenderthread):
/// game-thread Request() queue + dedup; Pump() hands ≤MaxRequestsPerFrame to a render-thread callback
/// that dispatches the bake, reads the buffer back ON the render thread (game thread never blocks),
/// uploads the slice via TextureUpdate, and pushes (key, slot, lo, hi) to a completed queue the game
/// thread drains via TryTake. Windowed only (render RD NullRefs headless).
public sealed class ChunkFieldCache
{
    public int MaxRequestsPerFrame = 4;   // bake is ~86× the 7×7 probe → lower throttle than the AABB provider

    private struct Req { public long Key; public int Slot; public Vector2 OriginXZ; public float Size; }
    private struct Done { public long Key; public int Slot; public float Lo; public float Hi; }

    private readonly FieldParams _p;
    public int GridN { get; }
    public int Side { get; }     // GridN + 2 (1-texel border each side for the geomorph coarse-texel + edge normals)
    public int Slots { get; }    // texture-array layer count (cap ≥ steady-state active chunks)

    private readonly Queue<Req> _pending = new();
    private readonly HashSet<long> _queued = new();
    private readonly List<Done> _done = new();
    private readonly object _doneLock = new();

    // Render-thread RD state.
    private RenderingDevice _rd;
    private Rid _shader, _pipeline, _arrayRid;
    private bool _rtReady, _rtFailed;
    private readonly Texture2DArrayRD _tex = new();   // bound to the material; RID assigned once on the render thread

    public ChunkFieldCache(FieldParams p, int gridN, int slots)
    {
        _p = p; GridN = gridN; Side = gridN + 2; Slots = slots;
    }

    /// The material-bindable texture (a sampler2DArray). Its RID is empty until the render thread creates the
    /// array in the first Pump/Prewarm — bind it to the material AFTER Prewarm (deferred, like the cloud sky).
    public Texture2DArrayRD Tex => _tex;
    public bool Ready => _rtReady;

    public void Prewarm() { RenderingServer.CallOnRenderThread(Callable.From(EnsureRt)); }

    public void Request(long key, int slot, Vector2 originXZ, float size)
    {
        if (_queued.Contains(key)) { return; }
        _queued.Add(key);
        _pending.Enqueue(new Req { Key = key, Slot = slot, OriginXZ = originXZ, Size = size });
    }

    public bool TryTake(out long key, out int slot, out float lo, out float hi)
    {
        lock (_doneLock)
        {
            if (_done.Count == 0) { key = 0; slot = -1; lo = hi = 0f; return false; }
            Done d = _done[_done.Count - 1];
            _done.RemoveAt(_done.Count - 1);
            key = d.Key; slot = d.Slot; lo = d.Lo; hi = d.Hi;
        }
        _queued.Remove(key);
        return true;
    }

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
        string mathSrc = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/field_math.gdshaderinc"));
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/field_bake.glsl"))
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty)
            .Replace("// @@INCLUDE field_math", mathSrc);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            GD.PrintErr("ChunkFieldCache field_bake.glsl: " + spirv.CompileErrorCompute);
            _rtFailed = true; return;
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "field_bake");
        _pipeline = _rd.ComputePipelineCreate(_shader);

        // Persistent RGBA32F Texture2DArray: R=height, GBA=normal. Sampling + update.
        var f = new RDTextureFormat
        {
            Width = (uint)Side, Height = (uint)Side, Depth = 1, ArrayLayers = (uint)Slots,
            Mipmaps = 1, Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2DArray,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        _arrayRid = _rd.TextureCreate(f, new RDTextureView());
        _tex.TextureRdRid = _arrayRid;   // assign ONCE (Texture2Drd race, Godot #118292)
        _rtReady = true;
    }

    private void ProcessBatch(List<Req> batch)
    {
        EnsureRt();
        if (!_rtReady) { return; }
        foreach (Req r in batch)
        {
            (float lo, float hi) = BakeChunk(r.Slot, r.OriginXZ, r.Size);
            lock (_doneLock) { _done.Add(new Done { Key = r.Key, Slot = r.Slot, Lo = lo, Hi = hi }); }
        }
    }

    /// Dispatch field_bake over Side×Side, read back the vec4 grid, upload it into layer `slot`, and reduce the
    /// height channel to the chunk's min/max (subsuming the AABB probe). Runs inside the render-thread callback.
    private (float lo, float hi) BakeChunk(int slot, Vector2 originXZ, float size)
    {
        int res = Side;
        float vtxSpacing = size / (GridN - 1);             // chunk VERTEX spacing (positioning)
        Vector2 gridOrigin = originXZ - new Vector2(vtxSpacing, vtxSpacing);   // start 1 border texel before the chunk min
        int texels = res * res;

        Rid outBuf = _rd.StorageBufferCreate((uint)(texels * 4 * sizeof(float)));   // vec4 per texel
        var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; uOut.AddId(outBuf);
        // Field math uses p.Spacing (analytic_spacing, octave gate) — NOT the vertex spacing.
        byte[] pbytes = FieldCompute.PackParamsBytes(_p, gridOrigin.X, gridOrigin.Y, _p.Spacing, (uint)res, 0);
        Rid pBuf = _rd.StorageBufferCreate((uint)pbytes.Length, pbytes);
        var uP = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; uP.AddId(pBuf);
        byte[] gbytes = BitConverter.GetBytes(vtxSpacing);
        Rid gBuf = _rd.StorageBufferCreate((uint)gbytes.Length, gbytes);
        var uG = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; uG.AddId(gBuf);
        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uOut, uP, uG }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();

        byte[] bytes = _rd.BufferGetData(outBuf);
        _rd.TextureUpdate(_arrayRid, (uint)slot, bytes);   // RGBA32F slice == the vec4 buffer layout

        float lo = float.MaxValue, hi = float.MinValue;
        int stride = 4 * sizeof(float);
        int got = Mathf.Min(texels, bytes.Length / stride);
        for (int i = 0; i < got; i++)
        {
            float h = BitConverter.ToSingle(bytes, i * stride);   // height = .r channel
            if (h < lo) { lo = h; }
            if (h > hi) { hi = h; }
        }
        _rd.FreeRid(set); _rd.FreeRid(outBuf); _rd.FreeRid(pBuf); _rd.FreeRid(gBuf);
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
            if (_arrayRid.IsValid) { _rd.FreeRid(_arrayRid); }
        }));
    }
}
