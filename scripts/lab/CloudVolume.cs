using Godot;
using System;
using Godot.Collections;

namespace WG16.Lab;

/// Owns the volumetric cloud subsystem. Stage 4 amortized path mirrors clayjohn's
/// proven Godot pattern (godot-volumetric-cloud-demo-v2): NOT a CompositorEffect —
/// a plain node that drives a compute dispatch on the MAIN RenderingDevice via
/// RenderingServer.CallOnRenderThread (the safe way to touch the render-thread RD).
/// The raymarch writes a lat-long hemisphere texture exposed to the sky shader as a
/// Texture2Drd material uniform. CRITICAL (per the Texture2Drd race, Godot #118292):
/// the output texture is created ONCE on the render thread and its RID assigned to
/// the Texture2Drd BEFORE any per-frame dispatch — never reassigned — so the sky
/// material's uniform set sees a valid texture from frame 1. Amortized: each dispatch
/// updates a strided subset of texels. Separation of concerns: noise bake, weather,
/// and this driver stay independent; the UI only calls the public knob setters.
public partial class CloudVolume : Node
{
    private CloudNoiseCompute? _noise;
    private byte[] _shapeBytes = System.Array.Empty<byte>(), _detailBytes = System.Array.Empty<byte>(), _weatherBytes = System.Array.Empty<byte>();
    private int _shapeRes, _detailRes;
    private CloudParams _p = CloudParams.Defaults();
    private bool _enabled = true;
    private int _debug = 0;

    private ShaderMaterial? _skyMat;
    private Godot.Environment? _env;
    private Sky? _cloudSky, _origSky;
    private Texture2Drd? _cloudTex;

    private Vector3 _sunDir = new Vector3(0.5f, 0.6f, 0.4f).Normalized();
    private Color _sunColor = new Color(1f, 0.95f, 0.85f);
    private float _sunEnergy = 1.3f;

    // render-thread compute resources (created in InitCompute on the render thread)
    private RenderingDevice _rd = null!;
    private Rid _shader, _pipeline, _outTex, _shapeTex, _detailTex, _weatherTex, _sampler, _paramBuf;
    private bool _computeReady;
    private int _frame;
    public const int TexW = 512, TexH = 128;

    public void Attach(Godot.Environment env, Camera3D cam)
    {
        _p = CloudParams.Load();
        _env = env;
        _origSky = env.Sky;
        BakeResources();
        BuildSkyMaterial();
        RenderingServer.CallOnRenderThread(Callable.From(InitCompute));   // create RD resources on render thread
        // NOTE: do NOT install the cloud sky yet — the Texture2Drd's RID is assigned
        // inside InitCompute (render thread, deferred). Installing the material before
        // that runs makes its uniform set bind an empty texture ("binding not valid").
        // _Process installs the sky on the first frame AFTER the RID is ready.
    }

    public void BakeResources()
    {
        _noise ??= new CloudNoiseCompute();
        (_shapeBytes, _shapeRes, _detailBytes, _detailRes) = _noise.BakeRaw();
        _weatherBytes = CloudWeather.BakeRaw();
        GD.Print("CloudVolume: resources baked (shape/detail volumes + weather field)");
    }

    private void BuildSkyMaterial()
    {
        _cloudTex = new Texture2Drd();   // empty RID now; filled on the render thread before any dispatch
        _skyMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_sky.gdshader") };
        _skyMat.SetShaderParameter("cloud_rd_tex", _cloudTex);
        _skyMat.SetShaderParameter("cloud_enabled", _enabled);
        _skyMat.SetShaderParameter("cloud_debug", _debug);
        _cloudSky = new Sky { SkyMaterial = _skyMat, ProcessMode = Sky.ProcessModeEnum.Realtime, RadianceSize = Sky.RadianceSizeEnum.Size256 };
    }

    private void InstallCloudSky() { if (_env != null && _cloudSky != null) { _env.Sky = _cloudSky; } }
    private void RestoreOrigSky() { if (_env != null && _origSky != null) { _env.Sky = _origSky; } }

    // One benign "Texture (binding 1) not a valid texture" error prints on the very
    // first frame: the sky material builds its uniform set before the render-thread
    // RID lands on the Texture2Drd. It self-heals on frame 2 (clouds render fine, no
    // repeat). Not worth more plumbing to suppress — documented so it's not chased.
    private bool _skyInstalled;
    public override void _Process(double delta)
    {
        if (!_enabled) { return; }
        // install the cloud sky only once the render-thread RID is live (avoids the
        // empty-Texture2Drd uniform-set error). _computeReady is set in InitCompute.
        if (_computeReady && !_skyInstalled) { InstallCloudSky(); _skyInstalled = true; }
        RenderingServer.CallOnRenderThread(Callable.From(RenderProcess));
    }

    // ---- render-thread compute (clayjohn CallOnRenderThread pattern) -----------
    private void InitCompute()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { return; }

        string path = ProjectSettings.GlobalizePath("res://shaders/cloud_raymarch.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty).Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { GD.PrintErr("cloud_raymarch.glsl: " + spirv.CompileErrorCompute); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "cloud_raymarch");
        _pipeline = _rd.ComputePipelineCreate(_shader);

        var of = new RDTextureFormat
        {
            Width = TexW, Height = TexH, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _outTex = _rd.TextureCreate(of, new RDTextureView());
        _rd.TextureClear(_outTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);   // valid sample before first dispatch
        _shapeTex = Create3D(_shapeRes, _shapeBytes);
        _detailTex = Create3D(_detailRes, _detailBytes);
        _weatherTex = Create2D(CloudWeather.Res, CloudWeather.Res, _weatherBytes);

        var ss = new RDSamplerState
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.Repeat, RepeatV = RenderingDevice.SamplerRepeatMode.Repeat, RepeatW = RenderingDevice.SamplerRepeatMode.Repeat,
        };
        _sampler = _rd.SamplerCreate(ss);
        _paramBuf = _rd.StorageBufferCreate((uint)(ParamFloats * sizeof(float)));

        // assign the RID ONCE, now, before any dispatch or material sampling
        if (_cloudTex != null) { _cloudTex.TextureRdRid = _outTex; }
        _computeReady = true;
        GD.Print("CloudVolume: compute initialized on render thread");
    }

    private Rid Create3D(int res, byte[] rgbaf)
    {
        var tf = new RDTextureFormat
        {
            Width = (uint)res, Height = (uint)res, Depth = (uint)res, TextureType = RenderingDevice.TextureType.Type3D,
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        Rid rid = _rd.TextureCreate(tf, new RDTextureView());
        _rd.TextureUpdate(rid, 0, rgbaf);
        return rid;
    }

    private Rid Create2D(int w, int h, byte[] rgbaf)
    {
        var tf = new RDTextureFormat
        {
            Width = (uint)w, Height = (uint)h, Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        Rid rid = _rd.TextureCreate(tf, new RDTextureView());
        _rd.TextureUpdate(rid, 0, rgbaf);
        return rid;
    }

    private void RenderProcess()
    {
        if (!_computeReady) { return; }
        CloudParams p = _p;
        int stride = Math.Clamp(p.TemporalFrames, 1, 64);
        int offset = _frame % stride;
        _frame++;

        byte[] pb = BuildParams(p, (float)Time.GetTicksMsec() / 1000.0f, offset, stride);
        _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);

        var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOut.AddId(_outTex);
        var uShape = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; uShape.AddId(_sampler); uShape.AddId(_shapeTex);
        var uDetail = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; uDetail.AddId(_sampler); uDetail.AddId(_detailTex);
        var uWeather = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 }; uWeather.AddId(_sampler); uWeather.AddId(_weatherTex);
        var uParam = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 }; uParam.AddId(_paramBuf);
        Rid set = _rd.UniformSetCreate(new Array<RDUniform> { uOut, uShape, uDetail, uWeather, uParam }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListDispatch(list, (uint)((TexW + 7) / 8), (uint)((TexH + 7) / 8), 1);
        _rd.ComputeListEnd();
        _rd.FreeRid(set);
    }

    private const int ParamFloats = 48;

    private byte[] BuildParams(CloudParams p, float time, int offset, int stride)
    {
        var b = new byte[ParamFloats * sizeof(float)];
        int o = 0;
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        Vector3 sun = _sunDir; Color sc = _sunColor;
        F(sun.X); F(sun.Y); F(sun.Z); F(_sunEnergy);
        F(sc.R); F(sc.G); F(sc.B); F(0f);
        F(0.30f); F(0.48f); F(0.74f); F(0f);
        F(0.68f); F(0.74f); F(0.80f); F(0f);
        F(TexW); F(TexH);
        F(offset); F(stride);
        F(time);
        F(p.Coverage); F(p.Density); F(p.CloudType);
        F(p.AltitudeM); F(p.ThicknessM);
        F(p.DriftSpeed); F(p.DriftDirDeg);
        F(p.HgAniso); F(p.Powder); F(p.SunAbsorption);
        F(p.Size); F(p.Detail); F(p.DetailSize); F(p.Edge); F(p.Opacity); F(p.Brightness); F(p.Ambient);
        F(p.RaymarchSteps);
        return b;
    }

    public void SetSun(Vector3 dir, Color color, float energy) { _sunDir = dir.Normalized(); _sunColor = color; _sunEnergy = energy; }

    public CloudParams Params => _p;
    public bool Enabled => _enabled;

    // ---- public knob interface (UI → here only). Knobs mutate _p; RenderProcess
    //      reads _p each frame on the render thread. -------------------------------
    public void SetDebug(int mode) { _debug = mode; _skyMat?.SetShaderParameter("cloud_debug", mode); }

    public void SetKnobBool(string knob, bool on)
    {
        if (knob == "enabled")
        {
            _enabled = on;
            _skyMat?.SetShaderParameter("cloud_enabled", on);
            if (on) { if (_computeReady) { InstallCloudSky(); _skyInstalled = true; } }
            else { RestoreOrigSky(); _skyInstalled = false; }
        }
    }

    public void SetKnobInt(string knob, int v)
    {
        switch (knob)
        {
            case "raymarch_steps":  _p = _p with { RaymarchSteps = v }; break;
            case "temporal_frames": _p = _p with { TemporalFrames = v }; break;
        }
    }

    public void SetKnob(string knob, float v)
    {
        switch (knob)
        {
            case "coverage":        _p = _p with { Coverage = v }; break;
            case "density":         _p = _p with { Density = v }; break;
            case "cloud_type":      _p = _p with { CloudType = v }; break;
            case "altitude_m":      _p = _p with { AltitudeM = v }; break;
            case "thickness_m":     _p = _p with { ThicknessM = v }; break;
            case "drift_speed":     _p = _p with { DriftSpeed = v }; break;
            case "drift_dir_deg":   _p = _p with { DriftDirDeg = v }; break;
            case "hg_aniso":        _p = _p with { HgAniso = v }; break;
            case "powder":          _p = _p with { Powder = v }; break;
            case "sun_absorption":  _p = _p with { SunAbsorption = v }; break;
            case "size":            _p = _p with { Size = v }; break;
            case "detail":          _p = _p with { Detail = v }; break;
            case "detail_size":     _p = _p with { DetailSize = v }; break;
            case "edge":            _p = _p with { Edge = v }; break;
            case "opacity":         _p = _p with { Opacity = v }; break;
            case "brightness":      _p = _p with { Brightness = v }; break;
            case "ambient":         _p = _p with { Ambient = v }; break;
            case "update_res_scale": _p = _p with { UpdateResScale = v }; break;
            case "shadow_strength": break;
        }
    }

    public override void _ExitTree()
    {
        _noise?.Dispose();
        if (_computeReady)
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                _rd.FreeRid(_sampler); _rd.FreeRid(_paramBuf);
                _rd.FreeRid(_outTex); _rd.FreeRid(_shapeTex); _rd.FreeRid(_detailTex); _rd.FreeRid(_weatherTex);
                _rd.FreeRid(_pipeline); _rd.FreeRid(_shader);
            }));
        }
    }
}
