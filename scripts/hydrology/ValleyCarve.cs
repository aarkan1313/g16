using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Local-RD GPU carve: base height + drainage segments + params -> carved height + substrate fields.
/// Knows nothing about how the segment graph was built (SoC). Windowed only (local RD).
public sealed class ValleyCarve : IDisposable
{
    public sealed class CarveResult { public float[] Height, FlowAccum, ChannelMask, WaterLevel, Sediment; }

    private readonly RenderingDevice _rd;
    private readonly Rid _shader, _pipeline;
    private readonly int _res, _cells;

    public ValleyCarve(int res)
    {
        _res = res; _cells = res * res;
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/valley_carve.glsl"))
            .Replace("#[compute]\r\n", "").Replace("#[compute]\n", "");
        var spirv = _rd.ShaderCompileSpirVFromSource(new RDShaderSource
        { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) throw new InvalidOperationException("valley_carve.glsl: " + spirv.CompileErrorCompute);
        _shader = _rd.ShaderCreateFromSpirV(spirv, "valley_carve");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    public CarveResult Carve(float[] baseHeight, DrainageGraph graph, HydrologyParams hp)
    {
        hp.Res = _res;
        var segments = graph.Segments;
        // pack segments: 8 floats each [Ax,Az,Bx,Bz,Order,Area,BedA,BedB]. (>=1 float even if empty so RID valid.)
        int sc = segments.Count;
        var segF = new float[Math.Max(sc * 8, 1)];
        for (int s = 0; s < sc; s++)
        {
            var g = segments[s]; int o = s * 8;
            segF[o] = g.Ax; segF[o+1] = g.Az; segF[o+2] = g.Bx; segF[o+3] = g.Bz;
            segF[o+4] = g.Order; segF[o+5] = g.Area; segF[o+6] = g.BedA; segF[o+7] = g.BedB;
        }
        // coarse lake fields: Filled (spill surface = lake water level) + LakeDepth (>0 = submerged by fill).
        // sampled bilinearly in the shader to set wlevel for LAKES (not just channels) + flatten lake floors.

        Rid ppar = SbBytes(hp.Pack());
        Rid pbase = SbFloats(baseHeight);
        Rid pout = Sb(_cells*4), pacc = Sb(_cells*4), pcm = Sb(_cells*4), pwl = Sb(_cells*4), psd = Sb(_cells*4);
        Rid pseg = SbFloats(segF);
        Rid pfilled = SbFloats(graph.Filled);
        Rid plake = SbFloats(graph.LakeDepth);
        Rid[] bufs = { ppar, pbase, pout, pacc, pcm, pwl, psd, pseg, pfilled, plake };
        var u = new Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < bufs.Length; b++)
        { var ru = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b }; ru.AddId(bufs[b]); u.Add(ru); }
        Rid set = _rd.UniformSetCreate(u, _shader, 0);

        // origin: lab carves the region centered on 0, matching how ErosionLab seeds the base field.
        float originX = -_res * hp.CellSize * 0.5f, originZ = -_res * hp.CellSize * 0.5f;
        // push: segCount, carveOrigin xz, coarse grid res + its origin/spacing (for sampling lake fields).
        byte[] push = new byte[32];
        BitConverter.GetBytes(sc).CopyTo(push, 0);
        BitConverter.GetBytes(originX).CopyTo(push, 4);
        BitConverter.GetBytes(originZ).CopyTo(push, 8);
        BitConverter.GetBytes(graph.Res).CopyTo(push, 12);
        BitConverter.GetBytes(graph.CoarseOriginX).CopyTo(push, 16);
        BitConverter.GetBytes(graph.CoarseOriginZ).CopyTo(push, 20);
        BitConverter.GetBytes(graph.CoarseSpacing).CopyTo(push, 24);
        // LakeFill spectrum -> effective depth threshold: fill=1 => LakeMinDepth (most basins pool);
        // fill=0 => a huge threshold (no basin is deep enough => no lakes, everything drains out as rivers).
        float lakeThresh = Lerp(2000f, hp.LakeMinDepth, Mathf.Clamp(hp.LakeFill, 0f, 1f));
        BitConverter.GetBytes(lakeThresh).CopyTo(push, 28);

        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _pipeline);
        _rd.ComputeListBindUniformSet(l, set, 0);
        _rd.ComputeListSetPushConstant(l, push, (uint)push.Length);
        uint grp = (uint)((_res + 7) / 8);
        _rd.ComputeListDispatch(l, grp, grp, 1);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();

        var res = new CarveResult
        {
            Height = Read(pout), FlowAccum = Read(pacc), ChannelMask = Read(pcm),
            WaterLevel = Read(pwl), Sediment = Read(psd)
        };
        // free the uniform SET before its buffers (freeing a buffer still referenced by a live set triggers
        // Godot's "invalid ID" cascade).
        _rd.FreeRid(set);
        foreach (var r in bufs) { _rd.FreeRid(r); }
        return res;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private Rid Sb(int bytes) => _rd.StorageBufferCreate((uint)bytes);
    private Rid SbBytes(byte[] b) => _rd.StorageBufferCreate((uint)b.Length, b);
    private Rid SbFloats(float[] f) { var b = new byte[f.Length*4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return _rd.StorageBufferCreate((uint)b.Length, b); }
    private float[] Read(Rid b) { byte[] by = _rd.BufferGetData(b); var f = new float[_cells]; Buffer.BlockCopy(by, 0, f, 0, Math.Min(by.Length, _cells*4)); return f; }

    public void Dispose() { if (_pipeline.IsValid) _rd.FreeRid(_pipeline); if (_shader.IsValid) _rd.FreeRid(_shader); _rd.Free(); }
}
