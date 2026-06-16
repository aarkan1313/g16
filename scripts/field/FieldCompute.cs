using Godot;
using System;

namespace WG16.Field;

/// Synchronous local RenderingDevice dispatcher for field_height.glsl.
/// Field knows nothing about meshes, cameras, or streaming.
public sealed class FieldCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    public FieldCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/field_height.glsl");
        string computeSource = System.IO.File.ReadAllText(shaderPath)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = computeSource,
        };

        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("field_height.glsl: " + spirv.CompileErrorCompute);
        }

        _shader = _rd.ShaderCreateFromSpirV(spirv, "field_height");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Produce one res*res page of heights, row-major (z * res + x).
    /// fieldMode: 0 full composition | 1..5 single-field debug views.
    public float[] ProducePage(FieldParams p, float originX = 0f, float originZ = 0f,
                               float? spacing = null, int? res = null, uint fieldMode = 0)
    {
        int r = res ?? p.HeightmapRes;
        int cells = checked(r * r);
        Rid buffer = _rd.StorageBufferCreate((uint)(cells * sizeof(float)));
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0,
        };
        uniform.AddId(buffer);
        byte[] paramsBytes = BuildParamsBytes(p, originX, originZ, spacing ?? p.Spacing, (uint)r, fieldMode);
        Rid paramsBuf = _rd.StorageBufferCreate((uint)paramsBytes.Length, paramsBytes);
        var paramsUniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1,
        };
        paramsUniform.AddId(paramsBuf);
        Rid set = _rd.UniformSetCreate(
            new Godot.Collections.Array<RDUniform> { uniform, paramsUniform }, _shader, 0);

        Dispatch(set, r);

        byte[] bytes = _rd.BufferGetData(buffer);
        var heights = new float[cells];
        Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
        _rd.FreeRid(set);
        _rd.FreeRid(buffer);
        _rd.FreeRid(paramsBuf);
        return heights;
    }

    private void Dispatch(Rid set, int res)
    {
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();
    }

    // 128-byte std430 block, field-for-field with ParamsBuf in field_height.glsl.
    private static byte[] BuildParamsBytes(FieldParams p, float originX, float originZ,
                                           float spacing, uint res, uint fieldMode)
    {
        var pc = new byte[128];
        BitConverter.GetBytes(originX).CopyTo(pc, 0);
        BitConverter.GetBytes(originZ).CopyTo(pc, 4);
        BitConverter.GetBytes(spacing).CopyTo(pc, 8);
        BitConverter.GetBytes(p.Seed).CopyTo(pc, 12);
        BitConverter.GetBytes(res).CopyTo(pc, 16);
        BitConverter.GetBytes(p.Octaves).CopyTo(pc, 20);
        BitConverter.GetBytes(p.BaseFreq).CopyTo(pc, 24);
        BitConverter.GetBytes(p.AmplitudeM).CopyTo(pc, 28);
        BitConverter.GetBytes(p.Lacunarity).CopyTo(pc, 32);
        BitConverter.GetBytes(p.Gain).CopyTo(pc, 36);
        BitConverter.GetBytes(fieldMode).CopyTo(pc, 40);
        BitConverter.GetBytes(p.ContFreq).CopyTo(pc, 44);
        BitConverter.GetBytes(p.ContWeight).CopyTo(pc, 48);
        BitConverter.GetBytes(p.UpliftFreq).CopyTo(pc, 52);
        BitConverter.GetBytes(p.UpliftWeight).CopyTo(pc, 56);
        BitConverter.GetBytes(p.UpliftLo).CopyTo(pc, 60);
        BitConverter.GetBytes(p.UpliftHi).CopyTo(pc, 64);
        BitConverter.GetBytes(p.MacroPivot).CopyTo(pc, 68);
        BitConverter.GetBytes(p.MacroAmp).CopyTo(pc, 72);
        BitConverter.GetBytes(p.HillDamp).CopyTo(pc, 76);
        BitConverter.GetBytes(p.RidgeFreq).CopyTo(pc, 80);
        BitConverter.GetBytes(p.RidgeAmp).CopyTo(pc, 84);
        BitConverter.GetBytes(p.MtnLo).CopyTo(pc, 88);
        BitConverter.GetBytes(p.MtnHi).CopyTo(pc, 92);
        BitConverter.GetBytes(p.GrainStretch).CopyTo(pc, 96);
        BitConverter.GetBytes(p.ContOctaves).CopyTo(pc, 100);
        BitConverter.GetBytes(p.ContWarp).CopyTo(pc, 104);
        BitConverter.GetBytes(p.UpliftWarp).CopyTo(pc, 108);
        BitConverter.GetBytes(p.MassifFreq).CopyTo(pc, 112);
        BitConverter.GetBytes(p.MassifFloor).CopyTo(pc, 116);
        BitConverter.GetBytes(p.FoothillW).CopyTo(pc, 120);
        BitConverter.GetBytes(p.FoothillH).CopyTo(pc, 124);
        return pc;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
