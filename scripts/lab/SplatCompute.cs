using Godot;
using System;

namespace WG16.Lab;

/// GPU-compute splat pass (Lever 1): bakes a per-texel material-weight mask
/// (dominant zone / secondary zone / intra-zone mix / boundary blend). Mirrors
/// FieldCompute's proven local-RenderingDevice + readback pattern: compute into a
/// storage buffer, read it back, upload as a normal ImageTexture the scene shader
/// samples. The readback (one GPU->CPU copy, only on bake — never per frame) buys
/// us out of the main-RD/cross-device texture-validity and render-thread races.
/// One job: produce the splat mask. Knows nothing about cameras/UI.
public sealed class SplatCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    // Mirrors the std430 ParamsBuf in splat_weights.glsl (field-for-field).
    public struct Params
    {
        public uint Res;
        public float TexelWorld, RegionSize;
        public float HValley, HSlope, HHigh, HPeak, SlopeCliffLo, SlopeCliffHi, BandSoftnessM;
        public float MixScaleM, MixBias;
        public uint MaskMode;
        public float EdgeNoiseM, EdgeNoiseAmp, MacroM;
        public uint RuleBased;   // 0 legacy bands | 1 rule engine
        public float CurvK;      // curvature scale (m)
        // --- Unit 4: breakup-mask shaping ---
        public float BkSlopeLo, BkSlopeHi, BkCurvScale, BkCavityGain, BkSunAzimuth;
        public uint  BkFlowIters;
    }

    public SplatCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string path = ProjectSettings.GlobalizePath("res://shaders/splat_weights.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("splat_weights.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "splat_weights");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Result of one bake: the legacy index map (splat_tex) + the two Phase-A weightmaps.
    public readonly record struct BakeResult(ImageTexture Splat, ImageTexture WeightsA, ImageTexture WeightsB, ImageTexture Breakup);

    /// (Re)bake the mask from a heightfield page → an ImageTexture (RGBAF) to bind
    /// as sampler2D on the terrain material. Safe to call repeatedly (rebake).
    public BakeResult Bake(float[] heights, int res, Params p)
    {
        int cells = checked(res * res);

        // heights in (binding 0)
        var hBytes = new byte[heights.Length * sizeof(float)];
        Buffer.BlockCopy(heights, 0, hBytes, 0, hBytes.Length);
        Rid hBuf = _rd.StorageBufferCreate((uint)hBytes.Length, hBytes);
        var hU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        hU.AddId(hBuf);

        // splat out: 4 floats (RGBA) per cell (binding 1)
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * 4 * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        oU.AddId(oBuf);

        // params (binding 2)
        byte[] pBytes = BuildParams(p);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 };
        pU.AddId(pBuf);

        // Phase A weightmaps: one packed uint (Rgba8) per cell (bindings 3, 4)
        Rid waBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(uint)));
        var waU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 };
        waU.AddId(waBuf);
        Rid wbBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(uint)));
        var wbU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 };
        wbU.AddId(wbBuf);

        // Unit 4 breakup masks: one packed uint (Rgba8) per cell (binding 5)
        Rid bBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(uint)));
        var bU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 5 };
        bU.AddId(bBuf);

        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { hU, oU, pU, waU, wbU, bU }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();

        byte[] outBytes = _rd.BufferGetData(oBuf);
        byte[] waBytes = _rd.BufferGetData(waBuf);   // packed Rgba8, little-endian: R=w0,G=w1,B=w2,A=w3
        byte[] wbBytes = _rd.BufferGetData(wbBuf);
        byte[] bkBytes = _rd.BufferGetData(bBuf);    // packed Rgba8: R=slope,G=curv,B=cavity,A=wear
        _rd.FreeRid(set);
        _rd.FreeRid(hBuf);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);
        _rd.FreeRid(waBuf);
        _rd.FreeRid(wbBuf);
        _rd.FreeRid(bBuf);

        Image splatImg = Image.CreateFromData(res, res, false, Image.Format.Rgbaf, outBytes);
        Image waImg = Image.CreateFromData(res, res, false, Image.Format.Rgba8, waBytes);
        Image wbImg = Image.CreateFromData(res, res, false, Image.Format.Rgba8, wbBytes);
        Image bkImg = Image.CreateFromData(res, res, false, Image.Format.Rgba8, bkBytes);
        return new BakeResult(
            ImageTexture.CreateFromImage(splatImg),
            ImageTexture.CreateFromImage(waImg),
            ImageTexture.CreateFromImage(wbImg),
            ImageTexture.CreateFromImage(bkImg));
    }

    private static byte[] BuildParams(Params p)
    {
        // 18 base + 6 Unit-4 = 24 fields, std430 scalar layout (all 4-byte) → 96B (16-byte multiple).
        var b = new byte[96];
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(p.Res); F(p.TexelWorld); F(p.RegionSize);
        F(p.HValley); F(p.HSlope); F(p.HHigh); F(p.HPeak);
        F(p.SlopeCliffLo); F(p.SlopeCliffHi); F(p.BandSoftnessM);
        F(p.MixScaleM); F(p.MixBias);
        U(p.MaskMode); F(p.EdgeNoiseM); F(p.EdgeNoiseAmp); F(p.MacroM);
        U(p.RuleBased); F(p.CurvK);
        F(p.BkSlopeLo); F(p.BkSlopeHi); F(p.BkCurvScale); F(p.BkCavityGain); F(p.BkSunAzimuth);
        U(p.BkFlowIters);
        return b;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
