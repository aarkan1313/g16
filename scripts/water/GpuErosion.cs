using Godot;
using Erosion.Core;
using System;
using System.IO;

namespace WG16.Water;

// Full GPU erosion pipeline on a local RenderingDevice: upload once, run droplet
// batches (atomic, race-free) then thermal iterations on the SAME buffer, read
// back once. The export-ready form for WG16's per-chunk bake.
public static class GpuErosion
{
    const float Scale = 65536f;

    public static HeightField Run(HeightField input, ErosionParams p)
    {
        var rd = RenderingServer.CreateLocalRenderingDevice();
        if (rd == null) { GD.PrintErr("GpuErosion: no local RenderingDevice (headless?)"); return input.Clone(); }

        int w = input.Width, h = input.Height, n = w * h;

        var dropletShader = rd.ShaderCreateFromSpirV(GD.Load<RDShaderFile>("res://shaders/droplet_erosion.glsl").GetSpirV());
        var thermalShader = rd.ShaderCreateFromSpirV(GD.Load<RDShaderFile>("res://shaders/thermal_erosion.glsl").GetSpirV());

        // heights (int fixed-point) + delta (zeroed) buffers
        var hInt = new int[n];
        for (int i = 0; i < n; i++) hInt[i] = (int)MathF.Round(input.Data[i] * Scale);
        var hBytes = new byte[n * 4]; Buffer.BlockCopy(hInt, 0, hBytes, 0, n * 4);
        var heightsBuf = rd.StorageBufferCreate((uint)(n * 4), hBytes);
        var deltaBuf = rd.StorageBufferCreate((uint)(n * 4), new byte[n * 4]);

        // brush
        var (bx, by, bw) = ErosionBrush.Build(p.ErosionRadius);
        int bc = bx.Length;
        var bms = new BinaryWriter(new MemoryStream());
        for (int k = 0; k < bc; k++) { bms.Write((float)bx[k]); bms.Write((float)by[k]); bms.Write(bw[k]); bms.Write(0f); }
        var brushBuf = rd.StorageBufferCreate((uint)(bc * 16), ((MemoryStream)bms.BaseStream).ToArray());

        // droplet params (numDroplets = perBatch)
        const int batches = 30;
        int perBatch = (p.DropletCount + batches - 1) / batches;
        var dms = new MemoryStream(); var dbw = new BinaryWriter(dms);
        dbw.Write(w); dbw.Write(h); dbw.Write(perBatch); dbw.Write(p.MaxLifetime);
        dbw.Write(p.Inertia); dbw.Write(p.CapacityFactor); dbw.Write(p.MinSlope); dbw.Write(p.ErosionRate);
        dbw.Write(p.DepositionRate); dbw.Write(p.Evaporation); dbw.Write(p.Gravity); dbw.Write(p.InitialWater);
        dbw.Write(p.InitialSpeed); dbw.Write(bc); dbw.Write(p.Seed); dbw.Write(Scale);
        var dropletParamBuf = rd.StorageBufferCreate((uint)dms.Length, dms.ToArray());

        // thermal params: width,height,phase (int), tanA,strength,cellSize,scale (float)
        var tms = new MemoryStream(); var tbw = new BinaryWriter(tms);
        tbw.Write(w); tbw.Write(h); tbw.Write(0);
        tbw.Write(MathF.Tan(p.TalusAngleDeg * MathF.PI / 180f)); tbw.Write(p.ThermalStrength);
        tbw.Write(input.CellSizeM); tbw.Write(Scale);
        var thermalParamBuf = rd.StorageBufferCreate((uint)tms.Length, tms.ToArray());

        var dropletSet = MakeSet(rd, dropletShader, heightsBuf, dropletParamBuf, brushBuf);
        var thermalSet = MakeSet(rd, thermalShader, heightsBuf, deltaBuf, thermalParamBuf);
        var dropletPipe = rd.ComputePipelineCreate(dropletShader);
        var thermalPipe = rd.ComputePipelineCreate(thermalShader);

        // droplet batches (sync between so later droplets see carved channels)
        uint dGroups = (uint)((perBatch + 63) / 64);
        for (int b = 0; b < batches; b++)
        {
            rd.BufferUpdate(dropletParamBuf, 56, 4, BitConverter.GetBytes(p.Seed + b * 9973));
            Dispatch(rd, dropletPipe, dropletSet, dGroups);
        }

        // thermal iterations (phase 0 accumulate, phase 1 apply)
        uint tGroups = (uint)((n + 63) / 64);
        for (int it = 0; it < p.ThermalIterations; it++)
        {
            rd.BufferUpdate(thermalParamBuf, 8, 4, BitConverter.GetBytes(0));
            Dispatch(rd, thermalPipe, thermalSet, tGroups);
            rd.BufferUpdate(thermalParamBuf, 8, 4, BitConverter.GetBytes(1));
            Dispatch(rd, thermalPipe, thermalSet, tGroups);
        }

        var outBytes = rd.BufferGetData(heightsBuf);
        var outInt = new int[n]; Buffer.BlockCopy(outBytes, 0, outInt, 0, n * 4);
        var outF = new HeightField(w, h, input.CellSizeM);
        for (int i = 0; i < n; i++) outF.Data[i] = outInt[i] / Scale;

        foreach (var rid in new[] { dropletSet, thermalSet, dropletPipe, thermalPipe,
                                    dropletParamBuf, thermalParamBuf, brushBuf, deltaBuf, heightsBuf,
                                    dropletShader, thermalShader })
            rd.FreeRid(rid);
        rd.Free();
        return outF;
    }

    static Rid MakeSet(RenderingDevice rd, Rid shader, Rid b0, Rid b1, Rid b2)
    {
        var u0 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 }; u0.AddId(b0);
        var u1 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; u1.AddId(b1);
        var u2 = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; u2.AddId(b2);
        return rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u0, u1, u2 }, shader, 0);
    }

    static void Dispatch(RenderingDevice rd, Rid pipe, Rid set, uint groups)
    {
        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, pipe);
        rd.ComputeListBindUniformSet(cl, set, 0);
        rd.ComputeListDispatch(cl, groups, 1, 1);
        rd.ComputeListEnd();
        rd.Submit();
        rd.Sync();
    }
}
