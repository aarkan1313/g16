using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Hydrology;

/// Unified GPU water carve (pillars: water sits IN carved basins/channels). Takes the significant lakes + river
/// reaches and returns a carved height with smooth lake BOWLS (banks) + thin river GROOVES. Operates on a COPY
/// of the base height — the base field generator is never touched (--fieldcheck stays 0m). Local RD, windowed.
public sealed class WaterCarve : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader, _pipeline;
    private readonly int _res, _cells;
    private readonly float _cell;

    public WaterCarve(int res, float cell)
    {
        _res = res; _cells = res * res; _cell = cell;
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/water_carve.glsl"))
            .Replace("#[compute]\r\n", "").Replace("#[compute]\n", "");
        var sp = _rd.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        if (!string.IsNullOrEmpty(sp.CompileErrorCompute)) { throw new InvalidOperationException("water_carve.glsl: " + sp.CompileErrorCompute); }
        _shader = _rd.ShaderCreateFromSpirV(sp, "water_carve");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    public float[] Apply(float[] baseHeight, IReadOnlyList<WaterBodies.Lake> lakes,
                         IReadOnlyList<WaterBodies.RiverReach> rivers, WaterParams wp)
    {
        // pack lakes: 5 floats [cx,cz,halfx,halfz,surface]
        var lakeF = new List<float>();
        if (wp.LakesEnabled)
        {
            foreach (var lk in lakes)
            {
                lakeF.Add((lk.MinX + lk.MaxX) * 0.5f); lakeF.Add((lk.MinZ + lk.MaxZ) * 0.5f);
                lakeF.Add((lk.MaxX - lk.MinX) * 0.5f); lakeF.Add((lk.MaxZ - lk.MinZ) * 0.5f);
                lakeF.Add(lk.SurfaceLevel);
            }
        }
        int lakeCount = lakeF.Count / 5;
        // pack river segments: 4 floats [Ax,Az,Bx,Bz]
        var segF = new List<float>();
        if (wp.RiversEnabled)
        {
            foreach (var r in rivers)
                for (int k = 0; k + 1 < r.Pts.Count; k++)
                { var a = r.Pts[k]; var b = r.Pts[k + 1]; segF.Add(a.X); segF.Add(a.Y); segF.Add(b.X); segF.Add(b.Y); }
        }
        int segCount = segF.Count / 4;
        if (lakeCount == 0 && segCount == 0) { return (float[])baseHeight.Clone(); }

        float origin = -_res * _cell * 0.5f;
        var pb = new byte[48];   // 12 scalars, 16-aligned
        BitConverter.GetBytes(_res).CopyTo(pb, 0);
        BitConverter.GetBytes(_cell).CopyTo(pb, 4);
        BitConverter.GetBytes(origin).CopyTo(pb, 8);
        BitConverter.GetBytes(origin).CopyTo(pb, 12);
        BitConverter.GetBytes(wp.LakeBasinDepthM).CopyTo(pb, 16);
        BitConverter.GetBytes(wp.LakeBankBlendM).CopyTo(pb, 20);
        BitConverter.GetBytes(wp.ChannelWidthM).CopyTo(pb, 24);
        BitConverter.GetBytes(wp.ChannelDepthM).CopyTo(pb, 28);
        BitConverter.GetBytes(wp.ChannelBlendM).CopyTo(pb, 32);
        BitConverter.GetBytes(lakeCount).CopyTo(pb, 36);
        BitConverter.GetBytes(segCount).CopyTo(pb, 40);

        Rid pp = _rd.StorageBufferCreate((uint)pb.Length, pb);
        Rid pbase = SbF(baseHeight);
        Rid pout = Sb(_cells * 4);
        Rid plake = SbF(lakeF.Count > 0 ? lakeF.ToArray() : new float[1]);
        Rid pseg = SbF(segF.Count > 0 ? segF.ToArray() : new float[1]);
        Rid[] bufs = { pp, pbase, pout, plake, pseg };
        var u = new Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < bufs.Length; b++) { var ru = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b }; ru.AddId(bufs[b]); u.Add(ru); }
        Rid set = _rd.UniformSetCreate(u, _shader, 0);

        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _pipeline);
        _rd.ComputeListBindUniformSet(l, set, 0);
        uint g = (uint)((_res + 7) / 8);
        _rd.ComputeListDispatch(l, g, g, 1);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();

        byte[] outb = _rd.BufferGetData(pout);
        var outf = new float[_cells]; Buffer.BlockCopy(outb, 0, outf, 0, Math.Min(outb.Length, _cells * 4));
        _rd.FreeRid(set); foreach (var r in bufs) { _rd.FreeRid(r); }
        return outf;
    }

    private Rid Sb(int n) => _rd.StorageBufferCreate((uint)n);
    private Rid SbF(float[] f) { var b = new byte[f.Length * 4]; Buffer.BlockCopy(f, 0, b, 0, b.Length); return _rd.StorageBufferCreate((uint)b.Length, b); }
    public void Dispose() { if (_pipeline.IsValid) { _rd.FreeRid(_pipeline); } if (_shader.IsValid) { _rd.FreeRid(_shader); } _rd.Free(); }
}
