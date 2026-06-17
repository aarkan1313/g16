using Godot;
using System;

namespace WG16.Lab;

/// GPU-compute cloud-coverage bake: produces ONE seamless tiling grayscale cloud
/// texture (high-contrast FBM) once at load, read back to an ImageTexture the
/// terrain shader samples in light() for sun attenuation. Mirrors SplatCompute's
/// local-RD + readback pattern (one GPU->CPU copy, only on bake — never per frame).
/// One job: produce the cloud texture. Knows nothing about cameras/UI/lighting.
public sealed class CloudCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    // Mirrors the std430 ParamsBuf in cloud_coverage.glsl (field-for-field).
    public struct Params
    {
        public uint Res;
        public float Seed;
        public float Contrast;
        public float Gain;
    }

    public CloudCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string path = ProjectSettings.GlobalizePath("res://shaders/cloud_coverage.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("cloud_coverage.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "cloud_coverage");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Bake the coverage texture at `res` (e.g. 512 — it is low-frequency and tiled,
    /// so it does not need field resolution). Returns an R-format ImageTexture with
    /// mipmaps. Safe to call repeatedly.
    public ImageTexture Bake(int res, Params p)
    {
        int cells = checked(res * res);
        p.Res = (uint)res;

        // coverage out: 1 float per cell (binding 0)
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        oU.AddId(oBuf);

        // params (binding 1)
        byte[] pBytes = BuildParams(p);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        pU.AddId(pBuf);

        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { oU, pU }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();

        byte[] outBytes = _rd.BufferGetData(oBuf);
        _rd.FreeRid(set);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);

        Image img = Image.CreateFromData(res, res, false, Image.Format.Rf, outBytes);
        img.GenerateMipmaps();   // mipmaps = no minification crawl in motion (banked lesson)
        return ImageTexture.CreateFromImage(img);
    }

    private static byte[] BuildParams(Params p)
    {
        // 4 fields, std430 scalar layout (all 4-byte) → pad to 16-byte multiple.
        var b = new byte[16];
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(p.Res); F(p.Seed); F(p.Contrast); F(p.Gain);
        return b;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
