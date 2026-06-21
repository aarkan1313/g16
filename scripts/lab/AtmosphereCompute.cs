using Godot;
using System;
using Godot.Collections;

namespace WG16.Lab;

/// AT-1 GPU physical atmosphere (Hillaire LUTs). Mirrors CloudVolume's render-thread seam
/// (memory compute-to-material-callonrenderthread): a plain Node that builds the LUTs on the
/// MAIN RenderingDevice via RenderingServer.CallOnRenderThread, assigns the SKY-VIEW LUT RID to
/// a Texture2Drd ONCE, and recomputes ONLY when the sun/params change (a dirty flag) — never
/// per-frame. The sky material (cloud_sky.gdshader, owned by CloudVolume) samples the sky-view
/// Texture2Drd by direction when atmosphere_on. Default DISABLED — the texture is produced but
/// only sampled when the user enables it (so no first-frame binding error in the default look).
public partial class AtmosphereCompute : Node
{
    private const int TransW = 256, TransH = 64;
    private const int MsW = 32, MsH = 32;
    private const int SkyW = 192, SkyH = 108;

    private Vector3 _sunDir = new Vector3(0f, 0.6f, -0.8f).Normalized();   // world TO-sun
    private float _turbidity = 1.0f;     // reserved for presets (AT-1 core uses fixed Earth Mie)
    private bool _enabled = false;
    private bool _dirty = true;
    private bool _checkRequested;

    private RenderingDevice _rd = null!;
    private Rid _transShader, _transPipe, _transTex;
    private Rid _msShader, _msPipe, _msTex;
    private Rid _skyShader, _skyPipe, _skyTex;
    private Rid _sampler, _paramBuf;
    private Rid _transSet, _msSet, _skySet;
    private bool _setsBuilt;
    private Texture2Drd? _skyViewRd;
    private bool _ready;

    public Texture2Drd? SkyViewTexture => _skyViewRd;
    public bool Ready => _ready;

    public void Attach()
    {
        _skyViewRd = new Texture2Drd();   // empty RID now; filled on the render thread in InitCompute
        RenderingServer.CallOnRenderThread(Callable.From(InitCompute));
    }

    public void SetEnabled(bool on) { _enabled = on; if (on) { _dirty = true; } }

    public void SetSun(Vector3 toSun)
    {
        Vector3 d = toSun.Normalized();
        if (d.DistanceTo(_sunDir) > 1e-4f) { _sunDir = d; _dirty = true; }
    }

    public void SetTurbidity(float v) { v = Mathf.Max(0f, v); if (Mathf.Abs(v - _turbidity) > 1e-4f) { _turbidity = v; _dirty = true; } }

    /// One-shot numeric self-check (readback) on the next recompute. Forces a recompute.
    public void RequestCheck() { _checkRequested = true; _enabled = true; _dirty = true; }

    public override void _Process(double delta)
    {
        if (!_ready || !_enabled || !_dirty) { return; }
        _dirty = false;
        RenderingServer.CallOnRenderThread(Callable.From(Recompute));
    }

    private void InitCompute()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { return; }
        _transShader = Compile("res://shaders/atmosphere_transmittance.glsl", "atmo_transmittance");
        _msShader    = Compile("res://shaders/atmosphere_multiscatter.glsl", "atmo_multiscatter");
        _skyShader   = Compile("res://shaders/atmosphere_skyview.glsl", "atmo_skyview");
        if (!_transShader.IsValid) { GD.PrintErr("AtmosphereCompute: transmittance shader failed — aborting init"); return; }
        _transPipe = _rd.ComputePipelineCreate(_transShader);
        if (_msShader.IsValid)  { _msPipe  = _rd.ComputePipelineCreate(_msShader); }
        if (_skyShader.IsValid) { _skyPipe = _rd.ComputePipelineCreate(_skyShader); }
        _transTex = CreateTex(TransW, TransH);
        _msTex    = CreateTex(MsW, MsH);
        _skyTex   = CreateTex(SkyW, SkyH);
        var ss = new RDSamplerState
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        };
        _sampler = _rd.SamplerCreate(ss);
        _paramBuf = _rd.StorageBufferCreate((uint)BuildParams().Length);
        if (_skyViewRd != null) { _skyViewRd.TextureRdRid = _skyTex; }   // assign ONCE, before any sampling
        _ready = true;
        GD.Print("AtmosphereCompute: LUT compute initialized on render thread");
    }

    private Rid Compile(string path, string name)
    {
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath(path))
            .Replace("#[compute]\r\n", string.Empty).Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { GD.PrintErr($"{name}.glsl: {spirv.CompileErrorCompute}"); return new Rid(); }
        return _rd.ShaderCreateFromSpirV(spirv, name);
    }

    private Rid CreateTex(int w, int h)
    {
        var f = new RDTextureFormat
        {
            Width = (uint)w, Height = (uint)h, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        Rid r = _rd.TextureCreate(f, new RDTextureView());
        _rd.TextureClear(r, new Color(0, 0, 0, 1), 0, 1, 0, 1);
        return r;
    }

    // Single vec4 param: xyz = world to-sun dir, w = turbidity (reserved). Std430Writer keeps alignment.
    private byte[] BuildParams() => new Std430Writer().Vec4(_sunDir, _turbidity).ToArray();

    private void EnsureSets()
    {
        if (_setsBuilt) { return; }
        var tImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; tImg.AddId(_transTex);
        var tP   = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 }; tP.AddId(_paramBuf);
        _transSet = _rd.UniformSetCreate(new Array<RDUniform> { tImg, tP }, _transShader, 0);

        if (_msShader.IsValid)
        {
            var mImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; mImg.AddId(_msTex);
            var mT   = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; mT.AddId(_sampler); mT.AddId(_transTex);
            var mP   = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 }; mP.AddId(_paramBuf);
            _msSet = _rd.UniformSetCreate(new Array<RDUniform> { mImg, mT, mP }, _msShader, 0);
        }
        if (_skyShader.IsValid)
        {
            var sImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; sImg.AddId(_skyTex);
            var sT   = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; sT.AddId(_sampler); sT.AddId(_transTex);
            var sM   = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; sM.AddId(_sampler); sM.AddId(_msTex);
            var sP   = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; sP.AddId(_paramBuf);
            _skySet = _rd.UniformSetCreate(new Array<RDUniform> { sImg, sT, sM, sP }, _skyShader, 0);
        }
        _setsBuilt = true;
    }

    private void Recompute()
    {
        if (!_ready) { return; }
        byte[] pb = BuildParams();
        _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);
        EnsureSets();
        Dispatch(_transPipe, _transSet, TransW, TransH);                  // 1. transmittance (no deps)
        if (_msShader.IsValid)  { Dispatch(_msPipe, _msSet, MsW, MsH); }  // 2. multiscatter (reads transmittance)
        if (_skyShader.IsValid) { Dispatch(_skyPipe, _skySet, SkyW, SkyH); } // 3. skyview (reads both)
        if (_checkRequested) { _checkRequested = false; DumpCheck(); }
    }

    private void Dispatch(Rid pipe, Rid set, int w, int h)
    {
        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, pipe);
        _rd.ComputeListBindUniformSet(l, set, 0);
        _rd.ComputeListDispatch(l, (uint)((w + 7) / 8), (uint)((h + 7) / 8), 1);
        _rd.ComputeListEnd();   // barrier → the next pass sees this LUT
    }

    private static float Half(byte[] d, int o) => (float)BitConverter.ToHalf(d, o);

    // Numeric proof (analog of --shadowcheck): readback the LUTs, assert finite/non-negative,
    // and print transmittance range + skyview zenith-vs-horizon luma. Run via --atmoscheck.
    private void DumpCheck()
    {
        byte[] t = _rd.TextureGetData(_transTex, 0);   // rgba16f
        float tmin = 1e9f, tmax = -1e9f; bool tfin = true;
        for (int i = 0; i < TransW * TransH; i++)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                float v = Half(t, i * 8 + ch * 2);
                if (!float.IsFinite(v)) { tfin = false; }
                tmin = Mathf.Min(tmin, v); tmax = Mathf.Max(tmax, v);
            }
        }
        bool tpass = tfin && tmin >= -1e-3f && tmax <= 1.001f;
        GD.Print($"[atmocheck] transmittance: min={tmin:F3} max={tmax:F3} finite={tfin} -> {(tpass ? "PASS" : "FAIL")}");

        if (_skyShader.IsValid)
        {
            byte[] s = _rd.TextureGetData(_skyTex, 0);
            double zen = 0, hor = 0; int zc = 0, hc = 0; bool sfin = true; float smin = 1e9f, smax = -1e9f;
            for (int y = 0; y < SkyH; y++)
            {
                for (int x = 0; x < SkyW; x++)
                {
                    int o = (y * SkyW + x) * 8;
                    float r = Half(s, o), g = Half(s, o + 2), b = Half(s, o + 4);
                    if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) { sfin = false; }
                    smin = Mathf.Min(smin, Mathf.Min(r, Mathf.Min(g, b)));
                    smax = Mathf.Max(smax, Mathf.Max(r, Mathf.Max(g, b)));
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (y >= SkyH - 4) { zen += lum; zc++; } else if (y < 4) { hor += lum; hc++; }
                }
            }
            bool spass = sfin && smin >= -1e-3f;
            GD.Print($"[atmocheck] skyview: horizonLuma={hor / Math.Max(1, hc):F4} zenithLuma={zen / Math.Max(1, zc):F4} min={smin:F4} max={smax:F4} finite={sfin} -> {(spass ? "PASS" : "FAIL")}");
        }
    }

    public override void _ExitTree()
    {
        if (_ready)
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                if (_setsBuilt) { _rd.FreeRid(_transSet); if (_msSet.IsValid) { _rd.FreeRid(_msSet); } if (_skySet.IsValid) { _rd.FreeRid(_skySet); } }
                _rd.FreeRid(_sampler); _rd.FreeRid(_paramBuf);
                _rd.FreeRid(_transTex); _rd.FreeRid(_msTex); _rd.FreeRid(_skyTex);
                _rd.FreeRid(_transPipe); if (_msPipe.IsValid) { _rd.FreeRid(_msPipe); } if (_skyPipe.IsValid) { _rd.FreeRid(_skyPipe); }
                _rd.FreeRid(_transShader); if (_msShader.IsValid) { _rd.FreeRid(_msShader); } if (_skyShader.IsValid) { _rd.FreeRid(_skyShader); }
            }));
        }
    }
}
