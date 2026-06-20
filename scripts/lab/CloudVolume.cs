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
    // camera world position — the world-space cloud raymarch starts rays here so the
    // visible clouds and the ground shadow map share one world-XZ frame (the fix for
    // the origin-dome-vs-world-shadow mismatch + camera-motion jitter). Pushed each
    // frame from TerrainLabUI._Process.
    private Vector3 _camWorld = Vector3.Zero;
    public void SetCameraWorld(Vector3 p) { _camWorld = p; }
    // mood sky colors → cloud ambient/background (set by TerrainLabUI.ApplyMood)
    // _sky*Base = the mood sky colors (input); _sky* = after the overcast grey-shift (what the
    // shaders see). Overcast greys BOTH the sky background AND the cloud ambient fill (the raymarch
    // ambient samples mix(_skyHorizon,_skyTop)), so the overcast knob is VISIBLE in the sky/clouds.
    private Color _skyTop = new Color(0.30f, 0.48f, 0.74f);
    private Color _skyHorizon = new Color(0.68f, 0.74f, 0.80f);
    private Color _skyTopBase = new Color(0.30f, 0.48f, 0.74f);
    private Color _skyHorizonBase = new Color(0.68f, 0.74f, 0.80f);
    private Color _groundBase = new Color(0.22f, 0.26f, 0.22f);

    // render-thread compute resources (created in InitCompute on the render thread)
    private RenderingDevice _rd = null!;
    private Rid _shader, _pipeline, _outTex, _shapeTex, _detailTex, _weatherTex, _sampler, _paramBuf;
    // cloud-shadow map (Stage 5): a second compute writes a top-down sun-transmittance
    // map over the terrain; the terrain light() samples it to attenuate the sun.
    private Rid _shadowShader, _shadowPipeline, _shadowTex, _shadowParamBuf;
    // Cached uniform sets, built once and reused every frame (the bound RIDs are stable; only
    // buffer contents change via BufferUpdate). Avoids per-frame UniformSetCreate/FreeRid churn.
    private Rid _cloudSet, _shadowSet;
    private bool _setsBuilt;
    private Texture2Drd? _shadowRd;
    private float _shadowStrength = 0.45f;   // was 0.7 — clouds are sparse; subtler ground shadow
    private bool _computeReady;
    private int _frame;
    private float _lastTime;          // for CPU drift integration (dt)
    private Vector2 _windOffset;      // accumulated wind offset (m) — changing speed changes rate, not position
    // Lat-long cloud dome resolution (az 0..2π : el 0..π/2 = 4:1). Configurable at launch via
    // --cloudtex=H (sets TexH=H, TexW=4H). Default raised to 1024×256: at 512×128 the lat-long
    // texels were visibly pixelly looking up (zenith). 1024 + the sky-shader's softened 5-tap
    // sample (cloud_sky.gdshader) reads clean in motion. Ship can dial down (--cloudtex=128) +
    // lean on temporal amortization for the mid-range budget. MUST be set before InitCompute.
    public static int TexW = 1024, TexH = 256;
    public const int ShadowRes = 512;
    // L1 fix: set from FieldParams.RegionSizeM at Attach so the shadow map + god-ray
    // UV stay locked to the actual terrain footprint (was hardcoded 8192, desynced
    // silently if field_params.json changed).
    private float RegionM = 8192f;

    private float _cellScale = 1.6f;   // clump-scale knob: higher = smaller/more clumps (anti-slab). >1 = tighter than the old fixed look.
    private System.Collections.Generic.List<CloudLayer> _layers = new();
    private int _layerCount;

    public void Attach(Godot.Environment env, Camera3D cam, float regionSizeM)
    {
        _p = CloudParams.Load();
        _layers = CloudLayers.Load();
        if (_layers.Count == 0) { _layers.Add(default); }   // ensure at least layer 0 (filled from knobs)
        _env = env;
        RegionM = regionSizeM;   // lock cloud shadow/god-ray footprint to the terrain
        _origSky = env.Sky;
        BakeResources();
        BuildSkyMaterial();
        RenderingServer.CallOnRenderThread(Callable.From(InitCompute));   // create RD resources on render thread
        // NOTE: do NOT install the cloud sky yet — the Texture2Drd's RID is assigned
        // inside InitCompute (render thread, deferred). Installing the material before
        // that runs makes its uniform set bind an empty texture ("binding not valid").
        // _Process installs the sky on the first frame AFTER the RID is ready.
    }

    private float _weatherMean = 0.5f;

    public void BakeResources()
    {
        _noise ??= new CloudNoiseCompute();
        (_shapeBytes, _shapeRes, _detailBytes, _detailRes) = _noise.BakeRaw();
        _weatherBytes = CloudWeather.BakeRaw();
        _weatherMean = CloudWeather.Mean(_weatherBytes);
        GD.Print($"CloudVolume: resources baked (weather mean {_weatherMean:F2})");
    }

    /// The overcast MOOD amount (0 clear → 1 full gloom): drives the diffuse-grey ambient dim +
    /// aerial fog tint (CPU proxy, no GPU readback). REALITY-ish + TUNABLE (user ask 2026-06-18):
    /// the gloom scales GRADUALLY with how much sky is covered (a gentle power curve — partly-cloudy
    /// barely dims, the grey flatness builds toward full cover) — NO hard threshold — times the
    /// `overcast_strength` knob so it's a controllable dial, not an automatic hard ramp. The future
    /// weather/biome system can drive the strength. (The sun DISC is decoupled — SetSunDiscEnergy —
    /// so this never dims the visible sun in a gap.)
    private float _overcastStrength = 0.7f;
    public float Overcast()
    {
        if (!_enabled) { return 0f; }
        // Steep curve (pow 3): partly-cloudy skies (Clear/Scattered/Broken) stay a clean blue —
        // the grey gloom only engages near genuine overcast. Reality: you need heavy cover to grey
        // the sky; a few clouds don't. × the tunable strength.
        return Mathf.Pow(Mathf.Clamp(_p.Coverage, 0f, 1f), 3.0f) * _overcastStrength;
    }

    private void BuildSkyMaterial()
    {
        _cloudTex = new Texture2Drd();   // empty RID now; filled on the render thread before any dispatch
        _shadowRd = new Texture2Drd();   // shadow map, bound to the terrain material (Stage 5b)
        _skyMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_sky.gdshader") };
        _skyMat.SetShaderParameter("cloud_rd_tex", _cloudTex);
        _skyMat.SetShaderParameter("cloud_enabled", _enabled);
        _skyMat.SetShaderParameter("cloud_debug", _debug);
        _skyMat.SetShaderParameter("sun_disc_energy", _sunDiscEnergy);
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
        // Install the cloud sky once the render-thread RID is live (avoids the empty-Texture2Drd
        // uniform-set error) — do this even when clouds are DISABLED, so the clouds-off look still
        // uses cloud_sky (clear gradient + sun disc/surface), not the legacy procedural sky.
        if (_computeReady && !_skyInstalled) { InstallCloudSky(); _skyInstalled = true; }
        if (!_enabled) { return; }
        ApplyOvercastSky();   // track the overcast knob/coverage live (greys sky + cloud ambient)
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
            Width = (uint)TexW, Height = (uint)TexH, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyToBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
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
        // size the buffer from an actual packed sample (Std430Writer determines the layout).
        _paramBuf = _rd.StorageBufferCreate((uint)BuildParams(_p, 0f, 0, 1).Length);

        // --- cloud-shadow map compute (Stage 5) ---
        string spath = ProjectSettings.GlobalizePath("res://shaders/cloud_shadow.glsl");
        string ssrc = System.IO.File.ReadAllText(spath)
            .Replace("#[compute]\r\n", string.Empty).Replace("#[compute]\n", string.Empty);
        var ssource = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = ssrc };
        RDShaderSpirV sspirv = _rd.ShaderCompileSpirVFromSource(ssource, false);
        if (!string.IsNullOrEmpty(sspirv.CompileErrorCompute)) { GD.PrintErr("cloud_shadow.glsl: " + sspirv.CompileErrorCompute); return; }
        _shadowShader = _rd.ShaderCreateFromSpirV(sspirv, "cloud_shadow");
        _shadowPipeline = _rd.ComputePipelineCreate(_shadowShader);
        var sf = new RDTextureFormat
        {
            Width = ShadowRes, Height = ShadowRes, Format = RenderingDevice.DataFormat.R16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _shadowTex = _rd.TextureCreate(sf, new RDTextureView());
        _rd.TextureClear(_shadowTex, new Color(1, 1, 1, 1), 0, 1, 0, 1);   // full sun until first dispatch
        _shadowParamBuf = _rd.StorageBufferCreate((uint)BuildShadowParams(_p, 0f).Length);

        // assign RIDs ONCE, before any dispatch or material sampling
        if (_cloudTex != null) { _cloudTex.TextureRdRid = _outTex; }
        if (_shadowRd != null) { _shadowRd.TextureRdRid = _shadowTex; }
        _computeReady = true;
        GD.Print("CloudVolume: compute initialized on render thread (clouds + shadow map)");
    }

    // Noise/weather volumes are [0,1]-ish — 16-bit half is ample (10-bit mantissa) and HALVES
    // the bandwidth on the hottest fetches (the density taps). The shaders sample as normalized
    // floats, so storage precision is invisible to them. Convert float32→half once at load.
    private static byte[] ToHalf(byte[] f32)
    {
        int n = f32.Length / 4;
        var outb = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short bits = BitConverter.HalfToInt16Bits((Half)BitConverter.ToSingle(f32, i * 4));
            outb[i * 2] = (byte)(bits & 0xFF);
            outb[i * 2 + 1] = (byte)((bits >> 8) & 0xFF);
        }
        return outb;
    }

    private Rid Create3D(int res, byte[] rgbaf)
    {
        var tf = new RDTextureFormat
        {
            Width = (uint)res, Height = (uint)res, Depth = (uint)res, TextureType = RenderingDevice.TextureType.Type3D,
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        Rid rid = _rd.TextureCreate(tf, new RDTextureView());
        _rd.TextureUpdate(rid, 0, ToHalf(rgbaf));
        return rid;
    }

    private Rid Create2D(int w, int h, byte[] rgbaf)
    {
        var tf = new RDTextureFormat
        {
            Width = (uint)w, Height = (uint)h, Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        Rid rid = _rd.TextureCreate(tf, new RDTextureView());
        _rd.TextureUpdate(rid, 0, ToHalf(rgbaf));
        return rid;
    }

    // Build the two compute uniform sets once (lazy, after InitCompute made the RIDs live).
    private void EnsureUniformSets()
    {
        if (_setsBuilt) { return; }
        var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOut.AddId(_outTex);
        var uShape = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; uShape.AddId(_sampler); uShape.AddId(_shapeTex);
        var uDetail = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; uDetail.AddId(_sampler); uDetail.AddId(_detailTex);
        var uWeather = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 }; uWeather.AddId(_sampler); uWeather.AddId(_weatherTex);
        var uParam = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 }; uParam.AddId(_paramBuf);
        _cloudSet = _rd.UniformSetCreate(new Array<RDUniform> { uOut, uShape, uDetail, uWeather, uParam }, _shader, 0);

        var sOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; sOut.AddId(_shadowTex);
        var sShape = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; sShape.AddId(_sampler); sShape.AddId(_shapeTex);
        var sDetail = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; sDetail.AddId(_sampler); sDetail.AddId(_detailTex);
        var sWeather = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 }; sWeather.AddId(_sampler); sWeather.AddId(_weatherTex);
        var sParam = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 }; sParam.AddId(_shadowParamBuf);
        _shadowSet = _rd.UniformSetCreate(new Array<RDUniform> { sOut, sShape, sDetail, sWeather, sParam }, _shadowShader, 0);
        _setsBuilt = true;
    }

    private void RenderProcess()
    {
        if (!_computeReady) { return; }
        CloudParams p = _p;
        int stride = Math.Clamp(p.TemporalFrames, 1, 64);
        int offset = _frame % stride;
        _frame++;

        // Integrate drift on the CPU: advance a persistent wind offset by dir*speed*dt each
        // frame. Changing the speed/dir knob then changes the RATE, not the position — the
        // old windOff = time*speed teleported clouds whenever the knob moved (the user's
        // "moving the speed knob moves the cloud" bug).
        float now = (float)Time.GetTicksMsec() / 1000.0f;
        float dt = (_lastTime > 0f) ? Mathf.Clamp(now - _lastTime, 0f, 0.1f) : 0f;
        _lastTime = now;
        float ang = Mathf.DegToRad(p.DriftDirDeg);
        _windOffset += new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * p.DriftSpeed * dt;

        byte[] pb = BuildParams(p, now, offset, stride);
        _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);

        // Build the two uniform sets ONCE and reuse them: all bound RIDs (textures, sampler,
        // param buffers) are stable after InitCompute — only the buffer CONTENTS change, via the
        // in-place BufferUpdate above. Recreating the sets every frame (the old code) churned 10
        // RDUniform + 2 Array allocations + UniformSetCreate/FreeRid per frame for no reason.
        EnsureUniformSets();

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, _cloudSet, 0);
        _rd.ComputeListDispatch(list, (uint)((TexW + 7) / 8), (uint)((TexH + 7) / 8), 1);
        _rd.ComputeListEnd();

        // --- cloud-shadow map dispatch (same density field, top-down toward sun) ---
        byte[] spb = BuildShadowParams(p, now);
        _rd.BufferUpdate(_shadowParamBuf, 0, (uint)spb.Length, spb);
        long slist = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(slist, _shadowPipeline);
        _rd.ComputeListBindUniformSet(slist, _shadowSet, 0);
        _rd.ComputeListDispatch(slist, (uint)((ShadowRes + 7) / 8), (uint)((ShadowRes + 7) / 8), 1);
        _rd.ComputeListEnd();

        if (_statsCountdown > 0 && --_statsCountdown == 0) { DumpDomeStats(p); }
    }

    private int _statsCountdown = 0;
    /// Request a one-shot readback of the rendered cloud dome (after N frames so it's
    /// converged). Answers the user's "barely visible" with MATH: how much of the visible
    /// sky has cloud (mean alpha) and how bright it is (sparse vs washed-out).
    public void RequestStats() { _statsCountdown = 30; }

    private void DumpDomeStats(CloudParams p)
    {
        byte[] data = _rd.TextureGetData(_outTex, 0);   // rgba16f, TexW×TexH
        int n = TexW * TexH;
        if (data.Length < n * 8) { GD.Print($"[cloudstats] readback short ({data.Length} bytes) — skipping"); return; }
        double sumA = 0, sumL = 0, maxA = 0; int covered = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 8;   // 4 channels × 2 bytes (half)
            float r = (float)BitConverter.ToHalf(data, o + 0);
            float g = (float)BitConverter.ToHalf(data, o + 2);
            float b = (float)BitConverter.ToHalf(data, o + 4);
            float a = (float)BitConverter.ToHalf(data, o + 6);
            double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            sumA += a; sumL += lum; if (a > maxA) maxA = a; if (a > 0.2) covered++;
        }
        GD.Print($"[cloudstats] coverage_knob={p.Coverage:F2}  perdeck={_perDeck:F0}  " +
                 $"meanAlpha={sumA / n:F3}  skyCovered(α>0.2)={100.0 * covered / n:F1}%  maxAlpha={maxA:F2}  meanCloudLuma={sumL / n:F3}");
        GD.Print("[cloudstats] read: skyCovered = how much of the dome has cloud; meanCloudLuma = how bright. " +
                 "Low covered = SPARSE; high covered + low luma = WASHED-OUT/too dark.");
    }

    // Field order MUST match cloud_shadow.glsl's ParamsBuf; Std430Writer handles alignment.
    private byte[] BuildShadowParams(CloudParams p, float time)
    {
        float[] layerData = PackLayers(out _layerCount);
        return new Std430Writer()
            .Vec4(_sunDir, 0f)                          // sun_dir
            .Vec2(ShadowRes, ShadowRes)                 // tex_size
            .Vec2(RegionM, 16f)                         // region (size, march steps)
            .F(time)
            .F(p.Coverage).F(p.Density).F(p.CloudType)
            .F(p.AltitudeM).F(p.ThicknessM)
            .F(p.DriftSpeed).F(p.DriftDirDeg)
            .F(p.Size).F(p.Detail).F(p.DetailSize).F(p.Edge)
            .F(_shadowStrength)
            .F(_groundHeight)                           // terrain mid-elevation
            .Vec4(_windOffset.X, _windOffset.Y, _cellScale, _layerCount)   // tail
            .Vec4Array(layerData)                       // layers[40] (5 vec4/layer; shadow uses 0-11)
            .ToArray();
    }
    private float _groundHeight = 250f;   // terrain mid-elevation (set at Attach)
    public void SetGroundHeight(float h) { _groundHeight = h; }

    /// Swap the active deck stack (roadmap #2: presets author a layer stack). Layer 0 stays
    /// knob-driven (see PackLayers) so the legacy UI + single-layer regression hold; the stack
    /// defines decks 1+ and the per-deck mix. Console-logs the result so the swap is verifiable
    /// without the eye. CloudVolume OWNS the active stack; CloudLayers OWNS the data; presets AUTHOR.
    public void SetLayers(System.Collections.Generic.List<CloudLayer> layers)
    {
        if (layers == null || layers.Count == 0) { return; }
        if (layers.Count > CloudLayers.MaxLayers) { layers = layers.GetRange(0, CloudLayers.MaxLayers); }
        _layers = layers;
        var on = _layers.FindAll(l => l.Enabled);
        GD.Print($"CloudVolume: layer stack set — {on.Count} deck(s), altitudes [{string.Join(", ", on.ConvertAll(l => l.Altitude.ToString("0")))}]");
    }

    /// Seam for the future weather/biome system (design): override one deck's share of the
    /// master coverage without touching the march. Narrow input, no render change.
    public void SetLayerWeight(int i, float w)
    {
        if (i < 0 || i >= _layers.Count) { return; }
        _layers[i] = _layers[i] with { CoverageWeight = Mathf.Max(0f, w) };
    }

    // Layer 0's deck params come from the legacy flat knobs (_p / _cellScale) so the single-
    // layer look + the existing UI keep working; layers 1+ are the JSON data verbatim.
    private float[] PackLayers(out int count)
    {
        var eff = new System.Collections.Generic.List<CloudLayer>(_layers);
        var l0 = eff[0];
        eff[0] = CloudLayers.WithCumulusLighting(l0 with {
            Altitude = _p.AltitudeM, Thickness = _p.ThicknessM, Size = _p.Size, CellScale = _cellScale,
            CoverageWeight = 1f, Density = _p.Density, Opacity = _p.Opacity, Type = _p.CloudType,
            Edge = _p.Edge, Detail = _p.Detail, DetailSize = _p.DetailSize, NoiseId = 0, Enabled = true });
        return CloudLayers.Pack(eff, out count);
    }

    // Byte layout is handled by Std430Writer (alignment-correct) — declare fields in the
    // SAME ORDER as cloud_raymarch.glsl's ParamsBuf and the offsets can't drift.
    private byte[] BuildParams(CloudParams p, float time, int offset, int stride)
    {
        float[] layerData = PackLayers(out _layerCount);
        return new Std430Writer()
            .Vec4(_sunDir, _sunEnergy)                  // sun_dir
            .Vec4(_sunColor)                            // sun_color
            .Vec4(_skyTop)                              // sky_top (mood)
            .Vec4(_skyHorizon)                          // sky_horizon (mood)
            .Vec4(_camWorld, 0f)                        // cam_world (ray origin)
            .Vec2(TexW, TexH)                           // tex_size
            .Vec2(offset, stride)                       // update
            .F(time)
            .F(p.Coverage).F(p.Density).F(p.CloudType)
            .F(p.AltitudeM).F(p.ThicknessM)
            .F(p.DriftSpeed).F(p.DriftDirDeg)
            .F(p.HgAniso).F(p.Powder).F(p.SunAbsorption)
            .F(p.Size).F(p.Detail).F(p.DetailSize).F(p.Edge).F(p.Opacity).F(p.Brightness).F(p.Ambient)
            .F(p.RaymarchSteps)
            .F(_perDeck)                                // 0 = global lighting (A), 1 = per-deck (B)
            .F(_dbgDeck)                                // >0.5 = deck-ID overlay
            .Vec4(_windOffset.X, _windOffset.Y, _cellScale, _layerCount)   // tail
            .Vec4Array(layerData)                       // layers[40] (5 vec4/layer)
            .ToArray();
    }
    private float _perDeck = 1f;   // per-deck phase/albedo/tint ON by default; --perdeck toggles
    public void SetPerDeck(float v) { _perDeck = Mathf.Clamp(v, 0f, 1f); }
    private float _dbgDeck = 0f;    // deck-ID overlay off by default; --deckdbg toggles
    public void SetDeckDebug(bool on) { _dbgDeck = on ? 1f : 0f; }

    public void SetSun(Vector3 dir, Color color, float energy) { _sunDir = dir.Normalized(); _sunColor = color; _sunEnergy = energy; }

    // The visible sun-disc brightness = the BASE (un-dimmed) sun energy, so raising coverage
    // (which dims the directional light via the overcast proxy) does NOT dim the disc — only an
    // actual cloud in front occludes it (dome composite). Pushed from TerrainLabUI._baseSunEnergy.
    private float _sunDiscEnergy = 1.3f;
    public void SetSunDiscEnergy(float e) { _sunDiscEnergy = e; _skyMat?.SetShaderParameter("sun_disc_energy", e); }
    private float _sunSize = 0.6f;          public float SunSize          => _sunSize;
    private float _sunLimb = 0.55f;         public float SunLimb          => _sunLimb;
    private float _sunCoronaSize = 1200f;   public float SunCoronaSize    => _sunCoronaSize;
    private float _sunCoronaEnergy = 2.0f;  public float SunCoronaEnergy  => _sunCoronaEnergy;
    private float _sunHaloSize = 90f;       public float SunHaloSize      => _sunHaloSize;
    private float _sunHaloEnergy = 0.4f;    public float SunHaloEnergy    => _sunHaloEnergy;
    private float _sunRedden = 1.0f;        public float SunRedden        => _sunRedden;
    private float _sunReddenOnset = 0.25f;  public float SunReddenOnset   => _sunReddenOnset;
    private float _sunHorizonGrow = 0.6f;   public float SunHorizonGrow   => _sunHorizonGrow;
    private float _sunCloudRedden = 0.8f;   public float SunCloudRedden   => _sunCloudRedden;
    public void SetSunSize(float deg) { _sunSize = Mathf.Clamp(deg, 0.05f, 8f); _skyMat?.SetShaderParameter("sun_size", _sunSize); }
    public void SetSunLimb(float v)   { _sunLimb = Mathf.Clamp(v, 0f, 1f); _skyMat?.SetShaderParameter("sun_limb", _sunLimb); }
    public void SetSunCoronaSize(float v)   { _sunCoronaSize = Mathf.Clamp(v, 40f, 6000f); _skyMat?.SetShaderParameter("sun_corona_size", _sunCoronaSize); }
    public void SetSunCoronaEnergy(float v) { _sunCoronaEnergy = Mathf.Max(v, 0f); _skyMat?.SetShaderParameter("sun_corona_energy", _sunCoronaEnergy); }
    public void SetSunHaloSize(float v)     { _sunHaloSize = Mathf.Clamp(v, 8f, 400f); _skyMat?.SetShaderParameter("sun_halo_size", _sunHaloSize); }
    public void SetSunHaloEnergy(float v)   { _sunHaloEnergy = Mathf.Max(v, 0f); _skyMat?.SetShaderParameter("sun_halo_energy", _sunHaloEnergy); }
    public void SetSunCloudRedden(float v) { _sunCloudRedden = Mathf.Clamp(v, 0f, 2f); _skyMat?.SetShaderParameter("sun_cloud_redden", _sunCloudRedden); }
    public void SetSunRedden(float v)      { _sunRedden = Mathf.Clamp(v, 0f, 2f); _skyMat?.SetShaderParameter("sun_redden", _sunRedden); }
    public void SetSunReddenOnset(float v) { _sunReddenOnset = Mathf.Clamp(v, 0.02f, 0.8f); _skyMat?.SetShaderParameter("sun_redden_onset", _sunReddenOnset); }
    public void SetSunHorizonGrow(float v) { _sunHorizonGrow = Mathf.Clamp(v, 0f, 3f); _skyMat?.SetShaderParameter("sun_horizon_grow", _sunHorizonGrow); }

    // --- sun SURFACE (procedural granulation) — material-uniform setters ---
    public void SetSunSurfaceOn(bool on)      { _skyMat?.SetShaderParameter("sun_surface_on", on); }
    public void SetSunSurfaceCells(float v)   { _skyMat?.SetShaderParameter("sun_surface_cells", v); }
    public void SetSunSurfaceContrast(float v){ _skyMat?.SetShaderParameter("sun_surface_contrast", v); }
    public void SetSunSurfaceSpots(float v)   { _skyMat?.SetShaderParameter("sun_surface_spots", v); }
    public void SetSunSurfaceChurn(float v)   { _skyMat?.SetShaderParameter("sun_surface_churn", v); }
    public void SetSunSurfaceWarm(float v)    { _skyMat?.SetShaderParameter("sun_surface_warm", v); }
    public void SetSunSurfaceColor(Color c)   { _skyMat?.SetShaderParameter("sun_surface_color", new Vector3(c.R, c.G, c.B)); }

    /// Mood sky colors → cloud ambient/scatter (compute) + the sky-shader background
    /// gradient, so clouds + the sky behind them track the chosen mood/time-of-day.
    public void SetSkyColors(Color top, Color horizon, Color ground)
    {
        _skyTopBase = top; _skyHorizonBase = horizon; _groundBase = ground;
        ApplyOvercastSky();
    }

    // Flat overcast sky = a dull desaturated light grey (a touch lighter at the horizon). Lerp the
    // mood sky colors toward it by the overcast amount → the `overcast gloom` knob visibly greys
    // the sky AND flattens the cloud ambient. Called per-frame so the knob/coverage track live.
    private static readonly Color OvercastTop = new Color(0.55f, 0.58f, 0.62f);
    private static readonly Color OvercastHorizon = new Color(0.66f, 0.68f, 0.70f);
    private static readonly Color OvercastGround = new Color(0.32f, 0.34f, 0.34f);
    private void ApplyOvercastSky()
    {
        float oc = Overcast();
        _skyTop = _skyTopBase.Lerp(OvercastTop, oc);
        _skyHorizon = _skyHorizonBase.Lerp(OvercastHorizon, oc);
        Color ground = _groundBase.Lerp(OvercastGround, oc);
        _skyMat?.SetShaderParameter("sky_top", _skyTop);
        _skyMat?.SetShaderParameter("sky_horizon", _skyHorizon);
        _skyMat?.SetShaderParameter("ground_color", ground);
    }

    public CloudParams Params => _p;
    public bool Enabled => _enabled;
    public bool ComputeReady => _computeReady;     // shadow Texture2Drd RID is live
    public Color SkyHorizonColor => _skyHorizon;   // for aerial-perspective tinting

    // ---- public knob interface (UI → here only). Knobs mutate _p; RenderProcess
    //      reads _p each frame on the render thread. -------------------------------
    public void SetDebug(int mode) { _debug = mode; _skyMat?.SetShaderParameter("cloud_debug", mode); }

    public void SetKnobBool(string knob, bool on)
    {
        if (knob == "enabled")
        {
            // Toggle clouds via the shader uniform ONLY — never swap env.Sky back and forth.
            // Each env.Sky assignment queues an async sky/radiance rebuild on the render thread
            // that transiently builds a uniform set against a not-yet-valid Texture2Drd (binding 1
            // = cloud_rd_tex; binding 34 = a dependent material) → the "Texture not valid" error
            // burst. cloud_sky.gdshader branches on `cloud_enabled`: when off it renders the
            // clear-sky gradient + sun disc (this lane's sky), so no resource swap is needed. The
            // cloud sky installs exactly once, in _Process, after the RID lands.
            _enabled = on;
            _skyMat?.SetShaderParameter("cloud_enabled", on);
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
            case "shadow_strength": _shadowStrength = v; break;   // ground-shadow darkness (Stage 5)
            case "cell_scale":      _cellScale = v; break;        // clump scale (anti-slab; higher = smaller clumps)
            case "perdeck":         _perDeck = Mathf.Clamp(v, 0f, 1f); break;   // 0=global lighting, 1=per-deck
            case "overcast_strength": _overcastStrength = Mathf.Max(0f, v); break;   // overcast gloom dial
        }
    }

    /// The cloud-shadow map (sun transmittance over the terrain), for the terrain
    /// material's light() to sample. Valid after InitCompute (cleared to full-sun
    /// before then). Null until BuildSkyMaterial runs.
    public Texture2Drd? ShadowTexture => _shadowRd;
    public float RegionSize => RegionM;

    // NOTE: a benign 3-line "Texture (binding 1/34) not valid" burst can print at app QUIT only:
    // freeing the cloud RIDs here (render thread) races the compositor building one last frame's
    // sky/terrain uniform sets. Exit-only, harmless, and NOT the mid-session preset-switch burst
    // (that was the env.Sky swap on cloud toggle — removed; clouds now toggle via the uniform only).
    // Left as-is: hardening teardown (detach sky + null the Texture2Drd RIDs before freeing) adds
    // its own main-vs-render-thread ordering risk for zero in-session benefit.
    public override void _ExitTree()
    {
        _noise?.Dispose();
        if (_computeReady)
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                if (_setsBuilt) { _rd.FreeRid(_cloudSet); _rd.FreeRid(_shadowSet); }
                _rd.FreeRid(_sampler); _rd.FreeRid(_paramBuf);
                _rd.FreeRid(_outTex); _rd.FreeRid(_shapeTex); _rd.FreeRid(_detailTex); _rd.FreeRid(_weatherTex);
                _rd.FreeRid(_pipeline); _rd.FreeRid(_shader);
                _rd.FreeRid(_shadowParamBuf); _rd.FreeRid(_shadowTex);
                _rd.FreeRid(_shadowPipeline); _rd.FreeRid(_shadowShader);
            }));
        }
        // Texture2Drd wrappers are RefCounted (freed when refs drop). No FogVolume to
        // clean up — god rays are in-march now (refactor T7).
    }
}
