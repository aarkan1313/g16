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

    // --- AT-2 aerial-perspective froxel LUT (3D, camera-frustum aligned) ---
    private const int AerialW = 32, AerialH = 32, AerialD = 32;
    private Rid _aerialShader, _aerialPipe, _aerialTex, _aerialSet;
    private Texture3Drd? _aerialRd;   // 3D LUT → Texture3Drd (sampled as sampler3D in aerial_screen.gdshader)
    private bool _camDirty = true, _aerialCheckRequested;
    private Vector3 _camPos = Vector3.Zero;
    private float _aerialFar = 32000f;                       // metres
    private Godot.Projection _invViewProj = Godot.Projection.Identity;

    public Texture2Drd? SkyViewTexture => _skyViewRd;
    public bool Ready => _ready;

    // AT-3 physical cloud lighting: 3 sun-dependent colors read back from the LUTs on the CPU each sun-change
    // (cross-node GPU sampling of these textures hazards the render device — seam-law). CloudVolume pushes
    // these as params. CloudZenith = sky radiance at the zenith; CloudHorizonSun = at the horizon toward the
    // sun; CloudSunTrans = sun transmittance toward the sun. Pre-multiplied by NOTHING (the shader scales).
    public Vector3 CloudZenith { get; private set; } = Vector3.One;
    public Vector3 CloudHorizonSun { get; private set; } = Vector3.One;
    public Vector3 CloudSunTrans { get; private set; } = Vector3.One;
    public bool CloudLightReady { get; private set; }
    // Readback cost control: only read the 3 colors back from the GPU when (a) the clouds want them (AT-3 on)
    // and (b) the sun moved enough to matter. The colors change smoothly, so a coarse sun-delta gives
    // imperceptible steps while avoiding a per-frame ~296 KB GPU→CPU sync stall during a running day/night
    // cycle (RecomputeAll fires every frame then). Look-neutral; the deeper fix (read only 3 texels via a
    // tiny GPU output) is banked for the end-of-arc perf pass (ROADMAP #7).
    private const float CloudColorSunDelta = 0.011f;   // ~0.6° of arc between cloud-color readbacks
    private bool _cloudLightWanted = true;             // AT-3 default-on; TerrainLabUI clears it when toggled off
    private Vector3 _lastCloudColorSun = new Vector3(2f, 2f, 2f);   // != any unit sun → forces the first readback
    public void SetCloudLightWanted(bool on) { _cloudLightWanted = on; if (on) { _lastCloudColorSun = new Vector3(2f, 2f, 2f); } }

    public Texture3Drd? AerialTexture => _aerialRd;
    public bool AerialReady => _ready && _aerialRd != null;
    /// Push the camera each frame (cheap aerial-only recompute path). farDist = max aerial range (m).
    /// Skips the per-frame aerial froxel recompute when nothing actually moved (static camera = no wasted
    /// dispatch). Exact equality is correct here: identical camera → identical froxel.
    public void SetCamera(Vector3 camPos, float farDist, Godot.Projection invViewProj)
    {
        if (camPos == _camPos && farDist == _aerialFar && invViewProj == _invViewProj) { return; }
        _camPos = camPos; _aerialFar = farDist; _invViewProj = invViewProj; _camDirty = true;
    }
    /// One-shot aerial readback on the next recompute. Forces enable + a recompute.
    public void RequestAerialCheck() { _aerialCheckRequested = true; _enabled = true; _camDirty = true; }

    public void Attach()
    {
        _skyViewRd = new Texture2Drd();   // empty RID now; filled on the render thread in InitCompute
        _aerialRd  = new Texture3Drd();   // AT-2 aerial froxel (3D); RID filled in InitCompute
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
        if (!_ready || !_enabled) { return; }
        // Sun/param change → recompute all four LUTs. Camera-only change → cheap aerial-only recompute.
        if (_dirty) { _dirty = false; _camDirty = false; RenderingServer.CallOnRenderThread(Callable.From(RecomputeAll)); }
        else if (_camDirty) { _camDirty = false; RenderingServer.CallOnRenderThread(Callable.From(RecomputeAerial)); }
    }

    private void InitCompute()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null) { return; }
        _transShader = Compile("res://shaders/atmosphere_transmittance.glsl", "atmo_transmittance");
        _msShader    = Compile("res://shaders/atmosphere_multiscatter.glsl", "atmo_multiscatter");
        _skyShader   = Compile("res://shaders/atmosphere_skyview.glsl", "atmo_skyview");
        _aerialShader = Compile("res://shaders/atmosphere_aerial.glsl", "atmo_aerial");
        if (!_transShader.IsValid) { GD.PrintErr("AtmosphereCompute: transmittance shader failed — aborting init"); return; }
        _transPipe = _rd.ComputePipelineCreate(_transShader);
        if (_msShader.IsValid)  { _msPipe  = _rd.ComputePipelineCreate(_msShader); }
        if (_skyShader.IsValid) { _skyPipe = _rd.ComputePipelineCreate(_skyShader); }
        if (_aerialShader.IsValid) { _aerialPipe = _rd.ComputePipelineCreate(_aerialShader); }
        _transTex = CreateTex(TransW, TransH);
        _msTex    = CreateTex(MsW, MsH);
        _skyTex   = CreateTex(SkyW, SkyH);
        _aerialTex = CreateTex3D(AerialW, AerialH, AerialD);
        var ss = new RDSamplerState
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear, MinFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        };
        _sampler = _rd.SamplerCreate(ss);
        _paramBuf = _rd.StorageBufferCreate((uint)BuildParams().Length);
        if (_skyViewRd != null) { _skyViewRd.TextureRdRid = _skyTex; }   // assign ONCE, before any sampling
        if (_aerialRd != null) { _aerialRd.TextureRdRid = _aerialTex; }  // assign ONCE (3D RID → Texture3Drd)
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

    private Rid CreateTex3D(int w, int h, int d)
    {
        var f = new RDTextureFormat
        {
            Width = (uint)w, Height = (uint)h, Depth = (uint)d, TextureType = RenderingDevice.TextureType.Type3D,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
        };
        Rid r = _rd.TextureCreate(f, new RDTextureView());
        _rd.TextureClear(r, new Color(0, 0, 0, 1), 0, 1, 0, 1);
        return r;
    }

    // Param buffer: vec4 sun_turb (xyz to-sun, w turbidity) + vec4 cam_pos_far (xyz cam m, w far m)
    // + mat4 inv_view_proj (4 columns as 4 vec4, std430 16B each, column-major). The AT-1 shaders read
    // only the first vec4 (sun_turb); the larger buffer is harmless. Std430Writer keeps alignment.
    private byte[] BuildParams()
    {
        var w = new Std430Writer().Vec4(_sunDir, _turbidity).Vec4(_camPos, _aerialFar);
        var p = _invViewProj;   // Godot.Projection columns X/Y/Z/W are Vector4 (column-major, same as GLSL mat4)
        w.Vec4(p.X.X, p.X.Y, p.X.Z, p.X.W).Vec4(p.Y.X, p.Y.Y, p.Y.Z, p.Y.W)
         .Vec4(p.Z.X, p.Z.Y, p.Z.Z, p.Z.W).Vec4(p.W.X, p.W.Y, p.W.Z, p.W.W);
        return w.ToArray();
    }

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
        if (_aerialShader.IsValid)
        {
            var aImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; aImg.AddId(_aerialTex);
            var aT   = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; aT.AddId(_sampler); aT.AddId(_transTex);
            var aM   = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; aM.AddId(_sampler); aM.AddId(_msTex);
            var aP   = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; aP.AddId(_paramBuf);
            _aerialSet = _rd.UniformSetCreate(new Array<RDUniform> { aImg, aT, aM, aP }, _aerialShader, 0);
        }
        _setsBuilt = true;
    }

    // Full recompute: all four LUTs (sun/param change). transmittance → multiscatter → skyview → aerial.
    private void RecomputeAll()
    {
        if (!_ready) { return; }
        byte[] pb = BuildParams();
        _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);
        EnsureSets();
        Dispatch(_transPipe, _transSet, TransW, TransH);                  // 1. transmittance (no deps)
        if (_msShader.IsValid)  { Dispatch(_msPipe, _msSet, MsW, MsH); }  // 2. multiscatter (reads transmittance)
        if (_skyShader.IsValid) { Dispatch(_skyPipe, _skySet, SkyW, SkyH); } // 3. skyview (reads both)
        DispatchAerial();                                                 // 4. aerial froxel (reads trans + ms)
        // AT-3: read the cloud-light colors back only when wanted + the sun moved enough (skips the per-frame
        // GPU→CPU sync stall during a running cycle, and all readback when AT-3 is off).
        if (_cloudLightWanted && _sunDir.DistanceTo(_lastCloudColorSun) > CloudColorSunDelta)
        {
            ComputeCloudLightColors();
            _lastCloudColorSun = _sunDir;
        }
        if (_checkRequested) { _checkRequested = false; DumpCheck(); }
        if (_aerialCheckRequested) { _aerialCheckRequested = false; DumpAerialCheck(); }
    }

    // Camera-only recompute: re-upload params (camera moved) + the aerial froxel only (cheap, per-frame).
    private void RecomputeAerial()
    {
        if (!_ready) { return; }
        byte[] pb = BuildParams();
        _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);
        EnsureSets();
        DispatchAerial();
        if (_aerialCheckRequested) { _aerialCheckRequested = false; DumpAerialCheck(); }
    }

    private void DispatchAerial()
    {
        if (!_aerialShader.IsValid) { return; }
        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _aerialPipe);
        _rd.ComputeListBindUniformSet(l, _aerialSet, 0);
        _rd.ComputeListDispatch(l, (uint)((AerialW + 7) / 8), (uint)((AerialH + 7) / 8), 1);  // z looped in-shader
        _rd.ComputeListEnd();
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

    // AT-3: read back the 3 sun-dependent colors the clouds need (zenith sky, horizon-toward-sun sky, sun
    // transmittance). Indexes the LUTs the same way atmo_dir_to_uv / getValFromLUT do. Sun-change cadence.
    private void ComputeCloudLightColors()
    {
        if (!_skyShader.IsValid) { return; }
        byte[] sky = _rd.TextureGetData(_skyTex, 0);     // SkyW×SkyH rgba16f
        byte[] tr  = _rd.TextureGetData(_transTex, 0);   // TransW×TransH rgba16f
        // zenith: atmo_dir_to_uv(0,1,0) → uv (0,1) → texel (0, SkyH-1)
        CloudZenith = SkyTexel(sky, 0, SkyH - 1);
        // horizon toward the sun: dir = normalize(sunDir.x, 0.05, sunDir.z); el small → low v row
        Vector3 hd = new Vector3(_sunDir.X, 0.05f, _sunDir.Z);
        if (hd.LengthSquared() < 1e-6f) { hd = new Vector3(0f, 0.05f, 1f); }
        hd = hd.Normalized();
        float az = Mathf.Atan2(hd.Z, hd.X); if (az < 0f) { az += 2f * Mathf.Pi; }
        float el = Mathf.Asin(Mathf.Clamp(hd.Y, 0f, 1f));
        int hx = Mathf.Clamp((int)(az / (2f * Mathf.Pi) * SkyW), 0, SkyW - 1);
        int hy = Mathf.Clamp((int)(Mathf.Sqrt(el / (0.5f * Mathf.Pi)) * SkyH), 0, SkyH - 1);
        CloudHorizonSun = SkyTexel(sky, hx, hy);
        // sun transmittance: uv = (0.5 + 0.5*sunCosZenith, ~0 altitude) (getValFromLUT, up = +Y)
        int tx = Mathf.Clamp((int)((0.5f + 0.5f * _sunDir.Y) * TransW), 0, TransW - 1);
        int ty = Mathf.Clamp((int)(0.02f * TransH), 0, TransH - 1);
        CloudSunTrans = TransTexel(tr, tx, ty);
        CloudLightReady = true;
    }
    private static Vector3 SkyTexel(byte[] d, int x, int y) { int o = (y * SkyW + x) * 8; return new Vector3(Half(d, o), Half(d, o + 2), Half(d, o + 4)); }
    private static Vector3 TransTexel(byte[] d, int x, int y) { int o = (y * TransW + x) * 8; return new Vector3(Half(d, o), Half(d, o + 2), Half(d, o + 4)); }

    // AT-2 aerial froxel self-check (analog of --atmoscheck): readback the 32³ LUT, assert finite,
    // non-negative in-scatter, and transmittance in [0,1]. Run via --aerialcheck.
    private void DumpAerialCheck()
    {
        byte[] d = _rd.TextureGetData(_aerialTex, 0);   // 32×32×32 rgba16f
        int n = AerialW * AerialH * AerialD; bool fin = true; float tmin = 1e9f, tmax = -1e9f, imin = 1e9f, imax = -1e9f;
        for (int i = 0; i < n; i++)
        {
            float r = Half(d, i * 8), g = Half(d, i * 8 + 2), b = Half(d, i * 8 + 4), a = Half(d, i * 8 + 6);
            if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) || !float.IsFinite(a)) { fin = false; }
            float ic = Mathf.Min(r, Mathf.Min(g, b)), ix = Mathf.Max(r, Mathf.Max(g, b));
            imin = Mathf.Min(imin, ic); imax = Mathf.Max(imax, ix);
            tmin = Mathf.Min(tmin, a); tmax = Mathf.Max(tmax, a);
        }
        // sane = finite + non-negative inscatter + transmittance in [0,1]. imax>0 confirms the froxel
        // carries real energy (not a degenerate all-zero LUT); printed for the daytime gate, not asserted
        // (legitimately ~0 at night / sun below horizon).
        bool pass = fin && imin >= -1e-3f && tmin >= -1e-3f && tmax <= 1.001f;
        GD.Print($"[aerialcheck] inscatter[{imin:F4}..{imax:F4}] transmittance[{tmin:F3}..{tmax:F3}] finite={fin} -> {(pass ? "PASS" : "FAIL")}");
    }

    public override void _ExitTree()
    {
        if (_ready)
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                if (_setsBuilt) { _rd.FreeRid(_transSet); if (_msSet.IsValid) { _rd.FreeRid(_msSet); } if (_skySet.IsValid) { _rd.FreeRid(_skySet); } if (_aerialSet.IsValid) { _rd.FreeRid(_aerialSet); } }
                _rd.FreeRid(_sampler); _rd.FreeRid(_paramBuf);
                _rd.FreeRid(_transTex); _rd.FreeRid(_msTex); _rd.FreeRid(_skyTex); if (_aerialTex.IsValid) { _rd.FreeRid(_aerialTex); }
                _rd.FreeRid(_transPipe); if (_msPipe.IsValid) { _rd.FreeRid(_msPipe); } if (_skyPipe.IsValid) { _rd.FreeRid(_skyPipe); } if (_aerialPipe.IsValid) { _rd.FreeRid(_aerialPipe); }
                _rd.FreeRid(_transShader); if (_msShader.IsValid) { _rd.FreeRid(_msShader); } if (_skyShader.IsValid) { _rd.FreeRid(_skyShader); } if (_aerialShader.IsValid) { _rd.FreeRid(_aerialShader); }
            }));
        }
    }
}
