using Godot;
using System;

namespace WG16.Erosion;

/// Local-RD pipe-model hydraulic erosion sim. Mirrors FieldCompute/CloudNoiseCompute: a local
/// RenderingDevice, SSBOs for the per-cell fields, one shader with a phase push-constant. Windowed only
/// (local RD NullRefs headless). NOT in the render loop — an offline tool; Submit/Sync per dispatch is fine.
public sealed class ErosionSim : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader, _pipeline;
    private Rid _params, _height, _water, _sediment, _flux, _velocity, _dh, _tflux, _sed2, _uset;
    private readonly int _res, _cells;
    public ErosionParams Params { get; set; } = new();

    // Race-free phase order: water flux/gather, erode→dh, thermal flux, apply (dh + gathered thermal),
    // transport→s2, swap s. No phase reads neighbor h/s while writing h/s (see the kernel's race rule).
    private const int PHASE_FLUX = 1, PHASE_WATER = 2, PHASE_ERODE = 3, PHASE_THERMAL_FLUX = 4,
                      PHASE_APPLY = 5, PHASE_TRANSPORT = 6, PHASE_SWAP_S = 7;

    public ErosionSim(int res)
    {
        _res = res; _cells = res * res;
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/erosion_sim.glsl"))
            .Replace("#[compute]\r\n", "").Replace("#[compute]\n", "");
        var spirv = _rd.ShaderCompileSpirVFromSource(new RDShaderSource
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = src,
        });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("erosion_sim.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "erosion_sim");
        _pipeline = _rd.ComputePipelineCreate(_shader);
        Alloc();
    }

    private Rid Sb(int bytes) => _rd.StorageBufferCreate((uint)bytes);
    private void Alloc()
    {
        Params.Res = _res;
        byte[] pp = Params.Pack();
        _params   = _rd.StorageBufferCreate((uint)pp.Length, pp);
        _height   = Sb(_cells * 4);
        _water    = Sb(_cells * 4);
        _sediment = Sb(_cells * 4);
        _flux     = Sb(_cells * 16);   // vec4
        _velocity = Sb(_cells * 8);    // vec2
        _dh       = Sb(_cells * 4);    // erode height delta
        _tflux    = Sb(_cells * 16);   // vec4 thermal slump outflow
        _sed2     = Sb(_cells * 4);    // transport double-buffer
        var u = new Godot.Collections.Array<RDUniform>();
        Rid[] bufs = { _params, _height, _water, _sediment, _flux, _velocity, _dh, _tflux, _sed2 };
        for (int b = 0; b < bufs.Length; b++)
        {
            var ru = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b };
            ru.AddId(bufs[b]); u.Add(ru);
        }
        _uset = _rd.UniformSetCreate(u, _shader, 0);
    }

    private void Clear(Rid buf, int bytes) => _rd.BufferClear(buf, 0, (uint)bytes);
    private void ClearDynamics()
    {
        Clear(_water, _cells * 4); Clear(_sediment, _cells * 4);
        Clear(_flux, _cells * 16); Clear(_velocity, _cells * 8);
        Clear(_dh, _cells * 4); Clear(_tflux, _cells * 16); Clear(_sed2, _cells * 4);
    }

    public void Seed(float[] height)
    {
        var hb = new byte[_cells * 4]; Buffer.BlockCopy(height, 0, hb, 0, hb.Length);
        _rd.BufferUpdate(_height, 0, (uint)hb.Length, hb);
        ClearDynamics();
        byte[] pp = Params.Pack();
        _rd.BufferUpdate(_params, 0, (uint)pp.Length, pp);
    }

    public void Reset() => ClearDynamics();

    private void Dispatch(int phase)
    {
        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _pipeline);
        _rd.ComputeListBindUniformSet(l, _uset, 0);
        byte[] push = new byte[16];                       // 16-byte push min
        BitConverter.GetBytes(phase).CopyTo(push, 0);
        _rd.ComputeListSetPushConstant(l, push, (uint)push.Length);
        uint g = (uint)((_res + 7) / 8);
        _rd.ComputeListDispatch(l, g, g, 1);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();   // local RD is blocking; fine for an offline tool
    }

    /// One full coupled step = all phases in race-free order, sharing the same state.
    public void Step(int n)
    {
        for (int k = 0; k < n; k++)
        {
            Dispatch(PHASE_FLUX);          // water pipes (read h+w, write own flux)
            Dispatch(PHASE_WATER);         // gather flux → own water + velocity
            Dispatch(PHASE_ERODE);         // read h/vel/s → own dh + own s
            Dispatch(PHASE_THERMAL_FLUX);  // read h → own slump outflow
            Dispatch(PHASE_APPLY);         // own dh + gathered thermal → own h
            Dispatch(PHASE_TRANSPORT);     // backtrace read s → s2
            Dispatch(PHASE_SWAP_S);        // s = s2
        }
    }

    public float[] ReadHeight() => ReadBuf(_height, _cells);

    /// 0 water, 1 sediment, 2 |velocity|.
    public float[] ReadDebug(int ch)
    {
        Rid b = ch == 0 ? _water : (ch == 1 ? _sediment : _velocity);
        int n = ch == 2 ? _cells * 2 : _cells;
        float[] f = ReadBuf(b, n);
        if (ch != 2) { return f; }
        var mag = new float[_cells];
        for (int i = 0; i < _cells; i++) { mag[i] = Mathf.Sqrt(f[2 * i] * f[2 * i] + f[2 * i + 1] * f[2 * i + 1]); }
        return mag;
    }

    private float[] ReadBuf(Rid b, int n)
    {
        byte[] bytes = _rd.BufferGetData(b);
        var f = new float[n]; Buffer.BlockCopy(bytes, 0, f, 0, Math.Min(bytes.Length, n * 4));
        return f;
    }

    public void Dispose()
    {
        foreach (var r in new[] { _uset, _params, _height, _water, _sediment, _flux, _velocity, _dh, _tflux, _sed2, _pipeline, _shader })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        _rd.Free();
    }
}
