using Godot;
using System;
using Godot.Collections;

namespace WG16.Lab;

/// GM2: derives a tileable HEIGHT map from a material's NORMAL map by Jacobi-relaxed
/// Poisson integration on a local RenderingDevice (mirrors SplatCompute's bake+readback
/// pattern). One job: normal → height. Knows nothing about zones/cameras/UI — the caller
/// hands it a normal Image and gets back an ImageTexture. Reused across all role materials.
/// Runs once at material-set time (windowed; local-RD compute can't run --headless).
public sealed class HeightCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    public HeightCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string path = ProjectSettings.GlobalizePath("res://shaders/height_from_normal.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("height_from_normal.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "height_from_normal");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Bake a normalized [0,1] height ImageTexture (Rf) from a normal map.
    /// res: bake resolution (512 = enough mesoscale relief for POM/interlock).
    /// iters: Jacobi sweeps (more = lower-freq relief recovered). flipY/invert: convention fixes.
    public ImageTexture BakeHeight(Image normalImg, int res, int iters, float amp, bool flipY, bool invert)
    {
        int cells = checked(res * res);

        // normal → res×res Rgba8 → float4 buffer (binding 0)
        Image img = (Image)normalImg.Duplicate();
        if (img.GetFormat() != Image.Format.Rgba8) { img.Convert(Image.Format.Rgba8); }
        if (img.GetWidth() != res || img.GetHeight() != res) { img.Resize(res, res, Image.Interpolation.Bilinear); }
        byte[] px = img.GetData();
        var nf = new float[cells * 4];
        for (int i = 0; i < cells; i++)
        {
            nf[i * 4 + 0] = px[i * 4 + 0] / 255f;
            nf[i * 4 + 1] = px[i * 4 + 1] / 255f;
            nf[i * 4 + 2] = px[i * 4 + 2] / 255f;
            nf[i * 4 + 3] = 1f;
        }
        var nBytes = new byte[nf.Length * sizeof(float)];
        Buffer.BlockCopy(nf, 0, nBytes, 0, nBytes.Length);
        Rid nrmBuf = _rd.StorageBufferCreate((uint)nBytes.Length, nBytes);

        // ping-pong height buffers: hA seeded 0.5, hB scratch
        var halfBytes = new byte[cells * sizeof(float)];
        { var t = new float[cells]; for (int i = 0; i < cells; i++) t[i] = 0.5f; Buffer.BlockCopy(t, 0, halfBytes, 0, halfBytes.Length); }
        Rid hA = _rd.StorageBufferCreate((uint)(cells * sizeof(float)), halfBytes);
        Rid hB = _rd.StorageBufferCreate((uint)(cells * sizeof(float)));

        byte[] pBytes = BuildParams((uint)res, amp, flipY, invert);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);

        RDUniform U(Rid buf, int binding)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
            u.AddId(buf);
            return u;
        }
        var nU = U(nrmBuf, 0); var pU = U(pBuf, 3);
        Rid setAB = _rd.UniformSetCreate(new Array<RDUniform> { nU, U(hA, 1), U(hB, 2), pU }, _shader, 0);
        Rid setBA = _rd.UniformSetCreate(new Array<RDUniform> { nU, U(hB, 1), U(hA, 2), pU }, _shader, 0);

        uint groups = (uint)((res + 7) / 8);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        Rid lastWrite = hA;
        for (int k = 0; k < iters; k++)
        {
            bool even = (k % 2 == 0);
            _rd.ComputeListBindUniformSet(list, even ? setAB : setBA, 0);
            _rd.ComputeListDispatch(list, groups, groups, 1);
            _rd.ComputeListAddBarrier(list);
            lastWrite = even ? hB : hA;
        }
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();

        byte[] outBytes = _rd.BufferGetData(lastWrite);
        _rd.FreeRid(setAB); _rd.FreeRid(setBA);
        _rd.FreeRid(nrmBuf); _rd.FreeRid(hA); _rd.FreeRid(hB); _rd.FreeRid(pBuf);

        // CPU min/max normalize → full [0,1] range (cheap; avoids a GPU reduction)
        var hf = new float[cells];
        Buffer.BlockCopy(outBytes, 0, hf, 0, outBytes.Length);
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = 0; i < cells; i++) { if (hf[i] < mn) mn = hf[i]; if (hf[i] > mx) mx = hf[i]; }
        float inv = (mx - mn) > 1e-6f ? 1f / (mx - mn) : 1f;
        for (int i = 0; i < cells; i++) hf[i] = (hf[i] - mn) * inv;
        var rf = new byte[cells * sizeof(float)];
        Buffer.BlockCopy(hf, 0, rf, 0, rf.Length);

        Image himg = Image.CreateFromData(res, res, false, Image.Format.Rf, rf);
        himg.GenerateMipmaps();
        return ImageTexture.CreateFromImage(himg);
    }

    private static byte[] BuildParams(uint res, float amp, bool flipY, bool invert)
    {
        var b = new byte[16];   // uint res, float amp, uint flip_y, uint invert (std430 scalar)
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(res); F(amp); U(flipY ? 1u : 0u); U(invert ? 1u : 0u);
        return b;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
