using Godot;
using System;
using Godot.Collections;

namespace WG16.Lab;

/// GPU bake of the volumetric-cloud noise volumes (Stage 1). One job: produce
/// tileable 3D noise as ImageTexture3D resources — knows nothing about clouds,
/// raymarching, the sun, or the scene (separation of concerns). Mirrors
/// SplatCompute's local-RenderingDevice + readback pattern. Baked ONCE at load
/// (never per frame); the volumes are re-derivable in-repo (seeded), not shipped.
///
/// Produces:
///   Shape  — 128³ RGBA (R = Perlin-Worley base, G/B/A = Worley octaves).
///   Detail — 32³  RGBA (detail packed in R; RGBA format for one upload path).
public sealed class CloudNoiseCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    public const int ShapeRes = 96;    // one-time bake; 96³ keeps Worley cost sane
    public const int DetailRes = 32;

    public CloudNoiseCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string path = ProjectSettings.GlobalizePath("res://shaders/cloud_noise_3d.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("cloud_noise_3d.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "cloud_noise_3d");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Bake both volumes. Safe to call once at load.
    public (ImageTexture3D shape, ImageTexture3D detail) Bake(float seed = 3.0f)
    {
        ImageTexture3D shape = BakeVolume(ShapeRes, mode: 0u, channels: 4, seed);
        ImageTexture3D detail = BakeVolume(DetailRes, mode: 1u, channels: 4, seed + 17.0f);
        return (shape, detail);
    }

    /// Bake both volumes as RAW RGBAF bytes (one contiguous block, Z-major) + res.
    /// Used by CloudVolume to create 3D textures directly on the MAIN RenderingDevice
    /// (RenderingServer.TextureGetRdTexture does not yield a usable RID for a local-RD
    /// ImageTexture3D, so the per-frame compute must own its own main-RD textures).
    public (byte[] shape, int shapeRes, byte[] detail, int detailRes) BakeRaw(float seed = 3.0f)
    {
        byte[] shape = BakeVolumeRaw(ShapeRes, mode: 0u, seed);
        byte[] detail = BakeVolumeRaw(DetailRes, mode: 1u, seed + 17.0f);
        return (shape, ShapeRes, detail, DetailRes);
    }

    /// Dispatch one volume and return RGBAF bytes (4 floats/cell, R replicated for
    /// the detail volume). Z-major: cell index = (z*res + y)*res + x.
    private byte[] BakeVolumeRaw(int res, uint mode, float seed)
    {
        int cells = checked(res * res * res);
        int floatsPerCell = (mode == 0u) ? 4 : 1;
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * floatsPerCell * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        oU.AddId(oBuf);
        byte[] pBytes = BuildParams((uint)res, mode, seed);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        pU.AddId(pBuf);
        Rid set = _rd.UniformSetCreate(new Array<RDUniform> { oU, pU }, _shader, 0);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 3) / 4);
        _rd.ComputeListDispatch(list, groups, groups, groups);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();
        byte[] outBytes = _rd.BufferGetData(oBuf);
        _rd.FreeRid(set); _rd.FreeRid(oBuf); _rd.FreeRid(pBuf);

        // expand to RGBAF (4 floats/cell)
        var floats = new float[cells * floatsPerCell];
        System.Buffer.BlockCopy(outBytes, 0, floats, 0, Math.Min(outBytes.Length, floats.Length * sizeof(float)));
        var rgba = new float[cells * 4];
        if (mode == 0u) { System.Buffer.BlockCopy(floats, 0, rgba, 0, rgba.Length * sizeof(float)); }
        else { for (int i = 0; i < cells; i++) { float r = floats[i]; int o = i * 4; rgba[o] = r; rgba[o+1] = r; rgba[o+2] = r; rgba[o+3] = r; } }
        var bytes = new byte[rgba.Length * sizeof(float)];
        System.Buffer.BlockCopy(rgba, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// Dispatch the compute for one volume and pack the readback into an
    /// ImageTexture3D (one RGBAF Image per Z-slice). For detail (1 channel in the
    /// shader) we still allocate RGBA and replicate R so a single upload path works.
    private ImageTexture3D BakeVolume(int res, uint mode, int channels, float seed)
    {
        int cells = checked(res * res * res);
        // The shader always indexes R as v[base] (detail) or v[base*4+c] (shape).
        // Allocate 4 floats/cell either way so the buffer size is uniform.
        int floatsPerCell = (mode == 0u) ? 4 : 1;
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * floatsPerCell * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        oU.AddId(oBuf);

        byte[] pBytes = BuildParams((uint)res, mode, seed);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        pU.AddId(pBuf);

        Rid set = _rd.UniformSetCreate(new Array<RDUniform> { oU, pU }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 3) / 4);   // local_size 4³
        _rd.ComputeListDispatch(list, groups, groups, groups);
        _rd.ComputeListEnd();
        var swGpu = System.Diagnostics.Stopwatch.StartNew();
        _rd.Submit();
        _rd.Sync();
        byte[] outBytes = _rd.BufferGetData(oBuf);
        swGpu.Stop();
        _rd.FreeRid(set);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);

        // Repack into one RGBAF Image per slice. The GPU output is already
        // contiguous; for the shape volume (RGBA, 16 bytes/cell) it matches Image's
        // RGBAF layout exactly, so each slice is a straight Buffer.BlockCopy — no
        // per-texel loop (that loop with per-float BitConverter.GetBytes was the
        // bottleneck). The detail volume (1 float/cell) is expanded to RGBA once.
        var swPack = System.Diagnostics.Stopwatch.StartNew();
        int sliceTexels = res * res;
        int sliceBytesRgba = sliceTexels * 4 * sizeof(float);

        byte[] rgbaAll;
        if (mode == 0u)
        {
            rgbaAll = outBytes;   // already RGBAF-contiguous
        }
        else
        {
            // expand R → RGBA (replicate) in one pass over floats
            var src = new float[cells];
            System.Buffer.BlockCopy(outBytes, 0, src, 0, cells * sizeof(float));
            var dst = new float[cells * 4];
            for (int i = 0; i < cells; i++) { float r = src[i]; int o = i * 4; dst[o] = r; dst[o + 1] = r; dst[o + 2] = r; dst[o + 3] = r; }
            rgbaAll = new byte[cells * 4 * sizeof(float)];
            System.Buffer.BlockCopy(dst, 0, rgbaAll, 0, rgbaAll.Length);
        }

        var slices = new Array<Image>();
        for (int z = 0; z < res; z++)
        {
            var rgba = new byte[sliceBytesRgba];
            System.Buffer.BlockCopy(rgbaAll, z * sliceBytesRgba, rgba, 0, sliceBytesRgba);
            slices.Add(Image.CreateFromData(res, res, false, Image.Format.Rgbaf, rgba));
        }

        var tex = new ImageTexture3D();
        tex.Create(Image.Format.Rgbaf, res, res, res, false, slices);
        swPack.Stop();
        GD.Print($"CloudNoiseCompute: baked {res}³ (mode {mode}) — gpu {swGpu.ElapsedMilliseconds}ms pack {swPack.ElapsedMilliseconds}ms");
        return tex;
    }

    private static byte[] BuildParams(uint res, uint mode, float seed)
    {
        var b = new byte[16];   // res,mode,seed,pad → 16B
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(res); U(mode); F(seed); F(0f);
        return b;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
