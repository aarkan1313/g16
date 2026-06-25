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
    // Bake throttle. Higher clears the birth backlog faster → more chunks sampling sooner → closer to the full
    // steady-state win while flying (measured knee ≈16: flying 9.7→7.2 ms @ bakereq 4→16 on a 5090). The bake
    // only runs when chunks are being born, so a high value costs nothing once caught up; tune down (--bakereq)
    // on slower HW where a birth-burst's per-frame dispatch cost spikes. imageStore-direct (no readback sync).
    public int MaxRequestsPerFrame = 16;

    private struct Req { public long Key; public int Slot; public Vector2 OriginXZ; public float Size; }
    private struct Done { public long Key; public int Slot; public float Lo; public float Hi; }

    // Scaled-uint encode for the per-slot min/max atomics (MUST match field_bake.glsl). Heights → centimetres
    // + a positive offset so atomicMin/atomicMax order correctly; decode is the inverse. 1 cm precision.
    private static float DecodeHeight(uint scaled) => ((float)scaled - 1000000f) / 100f;

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
    private Rid _shader, _pipeline, _arrayRid, _minmaxBuf;
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

    /// Drain one landed bake, carrying the chunk's EXACT height min/max (for the shadow AABB). lo/hi are the
    /// reduction of the same baked grid the shader samples — so the AABB bounds exactly what renders.
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
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit,
        };
        _arrayRid = _rd.TextureCreate(f, new RDTextureView());
        _tex.TextureRdRid = _arrayRid;   // assign ONCE (Texture2Drd race, Godot #118292)
        _minmaxBuf = _rd.StorageBufferCreate((uint)(Slots * 2 * sizeof(uint)));   // per-slot [min,max] for the AABB
        _rtReady = true;
    }

    private void ProcessBatch(List<Req> batch)
    {
        EnsureRt();
        if (!_rtReady) { return; }
        foreach (Req r in batch) { BakeChunk(r.Slot, r.OriginXZ, r.Size); }   // each clears + dispatches its slot
        // ONE min/max readback for the whole batch (the height grid stays imageStore-only — never read back).
        // BufferGetData syncs the render thread; this replaces the separate ChunkAabbProvider probe+readback for
        // cached chunks (CdlodTerrain skips that probe when a cache slot was assigned), so it's sync-neutral.
        byte[] mm = _rd.BufferGetData(_minmaxBuf);
        lock (_doneLock)
        {
            foreach (Req r in batch)
            {
                int o = r.Slot * 2 * sizeof(uint);
                float lo = DecodeHeight(BitConverter.ToUInt32(mm, o));
                float hi = DecodeHeight(BitConverter.ToUInt32(mm, o + sizeof(uint)));
                _done.Add(new Done { Key = r.Key, Slot = r.Slot, Lo = lo, Hi = hi });
            }
        }
    }

    /// Dispatch field_bake over Side×Side, writing height+normal DIRECTLY into texture-array layer `slot` via
    /// imageStore. Also clears + accumulates this slot's height min/max (binding 3) for the EXACT shadow AABB.
    private void BakeChunk(int slot, Vector2 originXZ, float size)
    {
        int res = Side;
        float vtxSpacing = size / (GridN - 1);             // chunk VERTEX spacing (positioning)
        Vector2 gridOrigin = originXZ - new Vector2(vtxSpacing, vtxSpacing);   // start 1 border texel before the chunk min

        // Reset this slot's [min,max] before the dispatch accumulates into it: min = max-uint, max = 0, so the
        // first atomicMin/atomicMax with a real scaled height overwrites them. (Outside any compute list.)
        byte[] clr = new byte[2 * sizeof(uint)];
        BitConverter.GetBytes(uint.MaxValue).CopyTo(clr, 0);
        BitConverter.GetBytes(0u).CopyTo(clr, sizeof(uint));
        _rd.BufferUpdate(_minmaxBuf, (uint)(slot * 2 * sizeof(uint)), (uint)clr.Length, clr);

        var uImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uImg.AddId(_arrayRid);
        // Field math uses p.Spacing (analytic_spacing, octave gate) — NOT the vertex spacing.
        byte[] pbytes = FieldCompute.PackParamsBytes(_p, gridOrigin.X, gridOrigin.Y, _p.Spacing, (uint)res, 0);
        Rid pBuf = _rd.StorageBufferCreate((uint)pbytes.Length, pbytes);
        var uP = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; uP.AddId(pBuf);
        byte[] gbytes = new byte[8];   // std430: float grid_spacing @0, int layer @4
        BitConverter.GetBytes(vtxSpacing).CopyTo(gbytes, 0);
        BitConverter.GetBytes(slot).CopyTo(gbytes, 4);
        Rid gBuf = _rd.StorageBufferCreate((uint)gbytes.Length, gbytes);
        var uG = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; uG.AddId(gBuf);
        var uM = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; uM.AddId(_minmaxBuf);
        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { uImg, uP, uG, uM }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();

        _rd.FreeRid(set); _rd.FreeRid(pBuf); _rd.FreeRid(gBuf);   // _minmaxBuf is persistent — not freed here
    }

    public void Dispose()
    {
        if (!_rtReady) { return; }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_pipeline.IsValid) { _rd.FreeRid(_pipeline); }
            if (_shader.IsValid) { _rd.FreeRid(_shader); }
            if (_arrayRid.IsValid) { _rd.FreeRid(_arrayRid); }
            if (_minmaxBuf.IsValid) { _rd.FreeRid(_minmaxBuf); }
        }));
    }
}
