# GPU Atmosphere AT-1 (Core Sky Color) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a physically-based (Hillaire) GPU sky-color provider — transmittance + multi-scatter + sky-view LUTs computed on the render-thread `Texture2Drd` seam — that the sky shader samples by view direction when `atmosphere_on`, **default OFF** (zero regression), to be A/B eye-gated vs the approved keyframed look.

**Architecture:** A new `AtmosphereCompute` node mirrors `CloudVolume`'s render-thread seam (memory `compute-to-material-callonrenderthread`): it builds 3 small LUTs on the **main** `RenderingDevice` via `RenderingServer.CallOnRenderThread`, assigns the sky-view LUT RID to a `Texture2Drd` **once**, and recomputes the LUTs **only when the sun/params change** (not per-frame). `cloud_sky.gdshader`'s `background()` samples the sky-view LUT when `atmosphere_on`, blending back to today's keyframed gradient by `night_factor` so the gated night look is untouched. `CloudVolume` stays the **sole writer** of the sky material (`AtmosphereCompute` produces a texture; `CloudVolume` binds it).

**Tech Stack:** Godot 4.6 mono (C# + GLSL compute via `RenderingDevice`), `Std430Writer` for param buffers, `Texture2Drd` for the compute→material bridge.

## Global Constraints

- **Pillars:** quality = performance = AAA-ish = long-term-best, regardless of time cost. Lead with the correct option.
- **Discipline:** AT-1 is ONE sub-phase. Build the core (Tasks 1–3), then STOP and drive the user's live A/B eye-gate. **Do NOT build Task 4 (presets) before the AT-1 core gate passes** (building ahead is the trap).
- **Default OFF:** `atmosphere_on=false` everywhere. The sky-view texture is only sampled when enabled → no first-frame "binding not valid" in the default look.
- **No regression when off:** with `atmosphere_on=false`, the rendered image must be **pixel-identical** to the current baseline (verify via `--auto-shot` A/B).
- **The eye is the only look-gate.** Mechanical checks (build / `--import` / `--atmoscheck` / `--auto-shot` / `--profmove`) gate correctness & cost only. Never judge the sky from a still — the user flies `review.tscn`.
- **No-TDD (GPU/visual):** the per-task "test" is the project's mechanical verification chain, not unit tests.
- **Seam law (memory `compute-to-material-callonrenderthread`):** create the LUT textures on the render-thread RD; assign each `Texture2Drd.TextureRdRid` **ONCE**; never reassign. One benign first-frame "binding not valid" line is expected & self-heals.
- **Headless can't run local-RD compute** — but `AtmosphereCompute` uses the **render-thread** RD (`RenderingServer.GetRenderingDevice()`), so it runs windowed. GLSL compiles at runtime (windowed), NOT at `--import`. Always verify windowed.
- **Coordination:** NEW files (`AtmosphereCompute.cs`, `shaders/atmosphere_*.glsl`, `data/atmosphere_presets.json`) + sky/light files only. Shared files (`lab_controls.json`, `TerrainLabUI*`) edited **small + additive**; `git add` your paths explicitly, never `-A`. Do NOT touch ground files or the cloud raymarch/shadow shaders. `--shadowcheck` must still PASS unchanged (AT-1 doesn't touch `layer_density`).
- **Run (one Godot at a time; kill strays first `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`):**
  `"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`
- **Build:** `dotnet build WG16.csproj`. **Import:** the `_console.exe` variant `--headless --path . --import`.

---

## File Structure

**Create:**
- `shaders/atmosphere_transmittance.glsl` — transmittance LUT compute pass (no deps).
- `shaders/atmosphere_multiscatter.glsl` — isotropic multi-scatter LUT (samples transmittance).
- `shaders/atmosphere_skyview.glsl` — sky-view LUT (samples transmittance + multi-scatter; uses the real world sun dir).
- `scripts/lab/AtmosphereCompute.cs` — the render-thread seam node (mirrors `CloudVolume`). Owns the 3 LUTs; exposes `SkyViewTexture` (Texture2Drd) + `Ready`; recomputes on dirty.
- `data/atmosphere_presets.json` — Task 4 (post-gate) only: exposure/tint presets.

**Modify:**
- `shaders/cloud_sky.gdshader` — add `atmosphere_on` / `atmo_skyview_tex` / `atmo_exposure` uniforms + `atmo_dir_to_uv`; `background()` samples the LUT when on (night-blended).
- `scripts/lab/CloudVolume.cs` — add `SetAtmosphereOn(bool)`, `SetAtmosphereSkyView(Texture2Drd)`, `SetKnob` case `"atmo_exposure"`; `BuildSkyMaterial` sets the new uniforms' defaults (sole `_skyMat` writer).
- `scripts/lab/TerrainLabUI.Clouds.cs` — add the `_atmosphere` field; `ApplyCloudBool` `"atmosphere_on"` case.
- `scripts/lab/TerrainLabUI.cs` — create `_atmosphere` in `_Ready`; `AddChild`+`Attach`+wire `SkyViewTexture` in `AttachClouds`.
- `scripts/lab/TerrainLabUI.Apply.cs` — `PushSunToCloud` also pushes the sun to `_atmosphere`.
- `scripts/lab/TerrainLabUI.Cli.cs` — parse `--atmosphere[=1]`, `--atmoscheck`, `--atmoexp=N`; apply in the CLI-override path.
- `data/lab_controls.json` — `atmosphere_on` toggle + `atmo_exposure` knob on the **Light** tab.

---

## Hillaire reference (shared atmosphere math — duplicated verbatim across the 3 compute shaders)

The 3 LUT shaders each embed this identical block (Godot RD GLSL has no reliable `#include` in this project's `File.ReadAllText` loader, so — like the 3 cloud shaders keep `layer_density` byte-identical — **keep this block identical across all three; edit all three together**). Units are **megameters (Mm)**; coefficients are 1/Mm (standard Hillaire/Bruneton Earth values).

```glsl
const float PI = 3.14159265358979;
const float groundRadiusMM = 6.360;
const float atmosphereRadiusMM = 6.460;
const vec3  rayleighScatteringBase = vec3(5.802, 13.558, 33.1);
const float mieScatteringBase = 3.996;
const float mieAbsorptionBase = 4.4;
const vec3  ozoneAbsorptionBase = vec3(0.650, 1.881, 0.085);

float safeacos(float x){ return acos(clamp(x, -1.0, 1.0)); }

float getMiePhase(float c){
    const float g = 0.8;
    float n = (1.0 - g*g) * (1.0 + c*c);
    float d = (2.0 + g*g) * pow(1.0 + g*g - 2.0*g*c, 1.5);
    return (3.0 / (8.0*PI)) * n / d;
}
float getRayleighPhase(float c){ return (3.0/(16.0*PI)) * (1.0 + c*c); }

void getScatteringValues(vec3 pos, out vec3 rayleighS, out float mieS, out vec3 extinction){
    float altKM = (length(pos) - groundRadiusMM) * 1000.0;
    float rD = exp(-altKM / 8.0);
    float mD = exp(-altKM / 1.2);
    rayleighS = rayleighScatteringBase * rD;
    mieS = mieScatteringBase * mD;
    float mieA = mieAbsorptionBase * mD;
    vec3 ozoneA = ozoneAbsorptionBase * max(0.0, 1.0 - abs(altKM - 25.0) / 15.0);
    extinction = rayleighS + vec3(mieS + mieA) + ozoneA;
}
// nearest positive intersection of ray (ro,rd) with sphere radius rad centred at origin; -1 if none.
float rayIntersectSphere(vec3 ro, vec3 rd, float rad){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - rad*rad;
    if (c > 0.0 && b > 0.0) return -1.0;
    float d = b*b - c;
    if (d < 0.0) return -1.0;
    if (d > b*b) return -b + sqrt(d);
    return -b - sqrt(d);
}
// transmittance LUT lookup: uv = (0.5+0.5*sunCosZenith, normalised height)
vec3 getValFromLUT(sampler2D lut, vec3 pos, vec3 sunDir){
    float h = length(pos);
    vec3 up = pos / h;
    float c = dot(sunDir, up);
    vec2 uv = vec2(clamp(0.5 + 0.5*c, 0.0, 1.0),
                   clamp((h - groundRadiusMM) / (atmosphereRadiusMM - groundRadiusMM), 0.0, 1.0));
    return texture(lut, uv).rgb;
}
```

The sky-view LUT direction mapping (used by `atmosphere_skyview.glsl` to build a ray per texel **and** by `cloud_sky.gdshader` to sample it — **must match**):
- `u = azimuth / 2π`, `azimuth = atan2(dir.z, dir.x)` wrapped to `[0,2π]`.
- `v = sqrt(elevation / (π/2))`, `elevation = asin(clamp(dir.y,0,1))` — sqrt warp = more texel density near the horizon.
- Inverse (compute): `az = u*2π`, `el = v*v*(π/2)`, `dir = vec3(cos(el)*cos(az), sin(el), cos(el)*sin(az))`.

---

## Task 1: AtmosphereCompute seam + transmittance LUT (compiles, dispatches, readback-sane)

Stands up the whole render-thread seam and the first (dependency-free) LUT. Deliverable: the node compiles & dispatches the transmittance pass, and `--atmoscheck` proves the LUT is finite/in-range/monotonic. Nothing is sampled by the sky yet (zero visual change).

**Files:**
- Create: `shaders/atmosphere_transmittance.glsl`
- Create: `scripts/lab/AtmosphereCompute.cs`
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs:15` (add `_atmosphere` field)
- Modify: `scripts/lab/TerrainLabUI.cs:59` (create node) and `AttachClouds` (~`:85`) (add + attach)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (parse `--atmosphere`, `--atmoscheck`) + the CLI-override apply site

**Interfaces:**
- Produces: `AtmosphereCompute` with `void Attach()`, `void SetEnabled(bool)`, `void SetSun(Vector3 toSun)`, `void RequestCheck()`, `Texture2Drd? SkyViewTexture { get; }`, `bool Ready { get; }`. (Task 1 builds only the transmittance LUT; the multiscatter/skyview RIDs land in Tasks 2/3 but the public surface is final now.)

- [ ] **Step 1: Write the transmittance compute shader**

Create `shaders/atmosphere_transmittance.glsl`:

```glsl
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1, std430) restrict readonly buffer Params { vec4 sun_turb; } P;

// ===== Hillaire shared block (keep identical across atmosphere_*.glsl) =====
const float PI = 3.14159265358979;
const float groundRadiusMM = 6.360;
const float atmosphereRadiusMM = 6.460;
const vec3  rayleighScatteringBase = vec3(5.802, 13.558, 33.1);
const float mieScatteringBase = 3.996;
const float mieAbsorptionBase = 4.4;
const vec3  ozoneAbsorptionBase = vec3(0.650, 1.881, 0.085);
void getScatteringValues(vec3 pos, out vec3 rayleighS, out float mieS, out vec3 extinction){
    float altKM = (length(pos) - groundRadiusMM) * 1000.0;
    float rD = exp(-altKM / 8.0);
    float mD = exp(-altKM / 1.2);
    rayleighS = rayleighScatteringBase * rD;
    mieS = mieScatteringBase * mD;
    float mieA = mieAbsorptionBase * mD;
    vec3 ozoneA = ozoneAbsorptionBase * max(0.0, 1.0 - abs(altKM - 25.0) / 15.0);
    extinction = rayleighS + vec3(mieS + mieA) + ozoneA;
}
float rayIntersectSphere(vec3 ro, vec3 rd, float rad){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - rad*rad;
    if (c > 0.0 && b > 0.0) return -1.0;
    float d = b*b - c;
    if (d < 0.0) return -1.0;
    if (d > b*b) return -b + sqrt(d);
    return -b - sqrt(d);
}
// ===== end shared block =====

vec3 sunTransmittance(vec3 pos, vec3 sunDir){
    if (rayIntersectSphere(pos, sunDir, groundRadiusMM) > 0.0) return vec3(0.0);
    float atmoDist = rayIntersectSphere(pos, sunDir, atmosphereRadiusMM);
    float t = 0.0;
    vec3 tr = vec3(1.0);
    const float STEPS = 40.0;
    for (float i = 0.0; i < STEPS; i += 1.0){
        float nT = ((i + 0.3) / STEPS) * atmoDist;
        float dt = nT - t; t = nT;
        vec3 np = pos + t * sunDir;
        vec3 rs; float ms; vec3 ext;
        getScatteringValues(np, rs, ms, ext);
        tr *= exp(-dt * ext);
    }
    return tr;
}

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    float sunCosZenith = 2.0 * uv.x - 1.0;
    float height = mix(groundRadiusMM, atmosphereRadiusMM, uv.y);
    vec3 pos = vec3(0.0, height, 0.0);
    vec3 sunDir = normalize(vec3(0.0, sunCosZenith, -sqrt(max(0.0, 1.0 - sunCosZenith * sunCosZenith))));
    imageStore(outTex, id, vec4(sunTransmittance(pos, sunDir), 1.0));
}
```

- [ ] **Step 2: Write `AtmosphereCompute.cs` (seam skeleton + transmittance pass)**

Create `scripts/lab/AtmosphereCompute.cs`. (Mirrors `CloudVolume`: render-thread RD, RIDs once, recompute on dirty. The multiscatter/skyview RIDs/dispatches are filled in Tasks 2/3 — guarded by `_msShader.IsValid`/`_skyShader.IsValid` so this compiles & runs now with just transmittance.)

```csharp
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
        if (!_transShader.IsValid) { GD.PrintErr("AtmosphereCompute: transmittance shader failed"); return; }
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
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit,
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
        Dispatch(_transPipe, _transSet, TransW, TransH);                 // 1. transmittance (no deps)
        if (_msShader.IsValid)  { Dispatch(_msPipe, _msSet, MsW, MsH); } // 2. multiscatter (reads transmittance)
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
    // and print transmittance range + skyview zenith-vs-horizon (zenith should be darker & bluer).
    private void DumpCheck()
    {
        byte[] t = _rd.TextureGetData(_transTex, 0);   // rgba16f
        float tmin = 1e9f, tmax = -1e9f; bool tfin = true;
        for (int i = 0; i < TransW * TransH; i++)
        {
            for (int ch = 0; ch < 3; ch++) { float v = Half(t, i * 8 + ch * 2); if (!float.IsFinite(v)) { tfin = false; } tmin = Mathf.Min(tmin, v); tmax = Mathf.Max(tmax, v); }
        }
        bool tpass = tfin && tmin >= -1e-3f && tmax <= 1.001f;
        GD.Print($"[atmocheck] transmittance: min={tmin:F3} max={tmax:F3} finite={tfin} -> {(tpass ? "PASS" : "FAIL")}");

        if (_skyShader.IsValid)
        {
            byte[] s = _rd.TextureGetData(_skyTex, 0);
            // zenith row = top (v≈1), horizon row = bottom (v≈0); column 0 = az 0. Average luma per row band.
            double zen = 0, hor = 0; int zc = 0, hc = 0; bool sfin = true; float smin = 1e9f;
            for (int y = 0; y < SkyH; y++)
            {
                for (int x = 0; x < SkyW; x++)
                {
                    int o = (y * SkyW + x) * 8;
                    float r = Half(s, o), g = Half(s, o + 2), b = Half(s, o + 4);
                    if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) { sfin = false; }
                    smin = Mathf.Min(smin, Mathf.Min(r, Mathf.Min(g, b)));
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (y >= SkyH - 4) { zen += lum; zc++; } else if (y < 4) { hor += lum; hc++; }
                }
            }
            bool spass = sfin && smin >= -1e-3f;
            GD.Print($"[atmocheck] skyview: horizonLuma={hor / Math.Max(1, hc):F4} zenithLuma={zen / Math.Max(1, zc):F4} minCh={smin:F4} finite={sfin} -> {(spass ? "PASS" : "FAIL")}");
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
```

> NOTE: this references `shaders/atmosphere_multiscatter.glsl` and `atmosphere_skyview.glsl`, created in Tasks 2/3. Until they exist, `Compile` logs an error and returns an invalid `Rid`; the `.IsValid` guards skip those passes so Task 1 runs on transmittance alone. **Create empty 1-line placeholder files is NOT needed** — `File.ReadAllText` on a missing file throws; instead the two `Compile` calls for ms/sky are added in Tasks 2/3. For Task 1, temporarily comment out the `_msShader =`/`_skyShader =` lines OR create the two files now with their final content (Tasks 2/3) — recommended: do Steps of Tasks 2 & 3's shader-write first if executing inline, so all three compile. (If subagent-driven, Task 1's subagent comments the two lines, Tasks 2/3 uncomment.)

- [ ] **Step 3: Add the `_atmosphere` field**

In `scripts/lab/TerrainLabUI.Clouds.cs`, after line 16 (`_godraysScreen` field):

```csharp
    private AtmosphereCompute? _atmosphere;   // AT-1 GPU physical sky (Hillaire LUTs); default off
```

- [ ] **Step 4: Create + attach the node**

In `scripts/lab/TerrainLabUI.cs` `_Ready`, right after line 60 (`_godraysScreen = new GodRaysScreen ...`):

```csharp
        _atmosphere = new AtmosphereCompute { Name = "AtmosphereCompute" };   // AT-1 (deferred-add like the cloud node)
```

In `AttachClouds` (after `_cloud.Attach(...)` and the shadow-texture wiring, before the method ends), add:

```csharp
        if (_atmosphere != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_atmosphere);
            _atmosphere.Attach();
            _cloud.SetAtmosphereSkyView(_atmosphere.SkyViewTexture);   // bind the (empty-RID) Texture2Drd now; RID fills on the render thread
        }
```

> `_cloud.SetAtmosphereSkyView` is added in Task 3. For Task 1 it does not exist yet — temporarily wrap that one line in `// ` and restore it in Task 3 (or do Task 3's CloudVolume step first when inline). The `AddChild`+`Attach` are valid in Task 1.

- [ ] **Step 5: CLI flags**

In `scripts/lab/TerrainLabUI.Cli.cs`, add fields near the other CLI fields (~line 45):

```csharp
    private int _atmosphereCli = -1;   // --atmosphere[=1] → enable the GPU atmosphere at startup
    private bool _atmoCheckCli;        // --atmoscheck → one-shot LUT numeric self-check
    private float _atmoExpCli = -1f;   // --atmoexp=N → atmosphere exposure override
```

In the arg-parse loop (with the other `else if (a.StartsWith(...))`):

```csharp
            else if (a.StartsWith("--atmosphere")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _atmosphereCli = (s == "1") ? 1 : 0; }
            else if (a == "--atmoscheck") { _atmoCheckCli = true; }
            else if (a.StartsWith("--atmoexp=")) { float.TryParse(a.Substring("--atmoexp=".Length), out _atmoExpCli); }
```

In the CLI-override apply path (where `ApplyCliOverrides` / the other `*Cli` flags are consumed — same place `_godraysOnCli`/`_celestialCli` are applied), add (after the cloud node is live):

```csharp
        if (_atmoExpCli >= 0f) { _cloud?.SetKnob("atmo_exposure", _atmoExpCli); }   // SetKnob case added in Task 3
        if (_atmosphereCli == 1) { _cloud?.SetAtmosphereOn(true); _atmosphere?.SetEnabled(true); }
        if (_atmoCheckCli) { _atmosphere?.RequestCheck(); }
```

> The `_cloud.SetAtmosphereOn`/`SetKnob("atmo_exposure")` calls are added in Task 3 — for Task 1 keep only `_atmosphere?.RequestCheck();` + `_atmosphere?.SetEnabled(true)` active (comment the `_cloud?` lines), restore in Task 3.

- [ ] **Step 6: Build**

Run: `dotnet build WG16.csproj`
Expected: `0 Error(s)` (pre-existing nullable warnings OK).

- [ ] **Step 7: Run the transmittance self-check (windowed — local-RD/GLSL needs a window)**

Kill strays, then run:
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --atmoscheck` (background or close after it prints).
Expected console:
- `AtmosphereCompute: LUT compute initialized on render thread`
- `[atmocheck] transmittance: min=0.0xx max=1.000 finite=True -> PASS`
- No `atmo_transmittance.glsl:` compile error.
- The rendered scene is **visually unchanged** (nothing samples the LUT yet).

- [ ] **Step 8: Commit**

```bash
git add shaders/atmosphere_transmittance.glsl scripts/lab/AtmosphereCompute.cs scripts/lab/TerrainLabUI.Clouds.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "atmosphere(AT-1): render-thread seam + transmittance LUT + --atmoscheck"
```

---

## Task 2: Multi-scatter LUT (depends on transmittance)

Adds the isotropic multi-scatter LUT (Hillaire `ψ_ms = L_2nd / (1 − f_ms)`) — what stops the physical sky from looking flat/too-dark, especially in twilight & shadow. Deliverable: `--atmoscheck` validates the MS LUT too.

**Files:**
- Create: `shaders/atmosphere_multiscatter.glsl`
- Modify: `scripts/lab/AtmosphereCompute.cs` (uncomment the `_msShader = Compile(...)` line from Task 1 if it was commented — it's already wired)

**Interfaces:**
- Consumes: the transmittance LUT (bound at `set=0, binding=1` as `sampler2D`).
- Produces: the multi-scatter LUT in `_msTex`, parameterised `(sunCosZenith, height)` like transmittance.

- [ ] **Step 1: Write the multiscatter compute shader**

Create `shaders/atmosphere_multiscatter.glsl`:

```glsl
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2, std430) restrict readonly buffer Params { vec4 sun_turb; } P;

// ===== Hillaire shared block (keep identical across atmosphere_*.glsl) =====
const float PI = 3.14159265358979;
const float groundRadiusMM = 6.360;
const float atmosphereRadiusMM = 6.460;
const vec3  rayleighScatteringBase = vec3(5.802, 13.558, 33.1);
const float mieScatteringBase = 3.996;
const float mieAbsorptionBase = 4.4;
const vec3  ozoneAbsorptionBase = vec3(0.650, 1.881, 0.085);
float safeacos(float x){ return acos(clamp(x, -1.0, 1.0)); }
void getScatteringValues(vec3 pos, out vec3 rayleighS, out float mieS, out vec3 extinction){
    float altKM = (length(pos) - groundRadiusMM) * 1000.0;
    float rD = exp(-altKM / 8.0);
    float mD = exp(-altKM / 1.2);
    rayleighS = rayleighScatteringBase * rD;
    mieS = mieScatteringBase * mD;
    float mieA = mieAbsorptionBase * mD;
    vec3 ozoneA = ozoneAbsorptionBase * max(0.0, 1.0 - abs(altKM - 25.0) / 15.0);
    extinction = rayleighS + vec3(mieS + mieA) + ozoneA;
}
float rayIntersectSphere(vec3 ro, vec3 rd, float rad){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - rad*rad;
    if (c > 0.0 && b > 0.0) return -1.0;
    float d = b*b - c;
    if (d < 0.0) return -1.0;
    if (d > b*b) return -b + sqrt(d);
    return -b - sqrt(d);
}
vec3 getValFromLUT(sampler2D lut, vec3 pos, vec3 sunDir){
    float h = length(pos);
    vec3 up = pos / h;
    float c = dot(sunDir, up);
    vec2 uv = vec2(clamp(0.5 + 0.5*c, 0.0, 1.0),
                   clamp((h - groundRadiusMM) / (atmosphereRadiusMM - groundRadiusMM), 0.0, 1.0));
    return texture(lut, uv).rgb;
}
// ===== end shared block =====

const float MS_STEPS = 20.0;
const float SQRT_SAMPLES = 8.0;   // 8x8 = 64 sphere directions
const float GROUND_ALBEDO = 0.3;

void getMS(vec3 pos, vec3 sunDir, out vec3 lumTotal, out vec3 fmsTotal){
    lumTotal = vec3(0.0); fmsTotal = vec3(0.0);
    float invSamples = 1.0 / (SQRT_SAMPLES * SQRT_SAMPLES);
    for (float i = 0.0; i < SQRT_SAMPLES; i += 1.0){
        for (float j = 0.0; j < SQRT_SAMPLES; j += 1.0){
            float theta = PI * (i + 0.5) / SQRT_SAMPLES;
            float phi = safeacos(1.0 - 2.0 * (j + 0.5) / SQRT_SAMPLES);
            float cp = cos(phi), sp = sin(phi), ct = cos(theta), st = sin(theta);
            vec3 rayDir = vec3(cp * st, ct, sp * st);

            float atmoDist = rayIntersectSphere(pos, rayDir, atmosphereRadiusMM);
            float groundDist = rayIntersectSphere(pos, rayDir, groundRadiusMM);
            float tMax = (groundDist > 0.0) ? groundDist : atmoDist;

            float cosTheta = dot(rayDir, sunDir);
            float miePhase = getMiePhase(cosTheta);
            float rayPhase = getRayleighPhase(-cosTheta);

            vec3 lum = vec3(0.0), lumFactor = vec3(0.0), tr = vec3(1.0);
            float t = 0.0;
            for (float s = 0.0; s < MS_STEPS; s += 1.0){
                float nT = ((s + 0.3) / MS_STEPS) * tMax;
                float dt = nT - t; t = nT;
                vec3 np = pos + t * rayDir;
                vec3 rs; float ms; vec3 ext;
                getScatteringValues(np, rs, ms, ext);
                vec3 sampleTr = exp(-dt * ext);

                vec3 scatterNoPhase = rs + vec3(ms);
                vec3 scatterF = (scatterNoPhase - scatterNoPhase * sampleTr) / ext;
                lumFactor += tr * scatterF;

                vec3 sunTr = getValFromLUT(transLUT, np, sunDir);
                vec3 inS = rs * rayPhase * sunTr + vec3(ms) * miePhase * sunTr;
                vec3 scatterInt = (inS - inS * sampleTr) / ext;
                lum += tr * scatterInt;
                tr *= sampleTr;
            }
            if (groundDist > 0.0 && dot(pos, sunDir) > 0.0){
                vec3 hitPos = normalize(pos + groundDist * rayDir) * groundRadiusMM;
                lum += tr * vec3(GROUND_ALBEDO) * getValFromLUT(transLUT, hitPos, sunDir);
            }
            fmsTotal += lumFactor * invSamples;
            lumTotal += lum * invSamples;
        }
    }
}
float getMiePhase(float c){
    const float g = 0.8;
    float n = (1.0 - g*g) * (1.0 + c*c);
    float d = (2.0 + g*g) * pow(1.0 + g*g - 2.0*g*c, 1.5);
    return (3.0 / (8.0*PI)) * n / d;
}
float getRayleighPhase(float c){ return (3.0/(16.0*PI)) * (1.0 + c*c); }

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    float sunCosZenith = 2.0 * uv.x - 1.0;
    float height = mix(groundRadiusMM, atmosphereRadiusMM, uv.y);
    vec3 pos = vec3(0.0, height, 0.0);
    vec3 sunDir = normalize(vec3(0.0, sunCosZenith, -sqrt(max(0.0, 1.0 - sunCosZenith * sunCosZenith))));
    vec3 lum, fms;
    getMS(pos, sunDir, lum, fms);
    vec3 psi = lum / max(vec3(1e-4), vec3(1.0) - fms);   // geometric series sum of multiple scattering
    imageStore(outTex, id, vec4(psi, 1.0));
}
```

> GLSL note: `getMiePhase`/`getRayleighPhase` are defined AFTER `getMS` here only to keep the shared block verbatim — GLSL 450 requires declaration before use, so **move the two phase functions into the shared block (above `getScatteringValues`)** exactly as written in the Hillaire reference section. (The transmittance shader doesn't call them, so they can be omitted there or included harmlessly.) Keep the block identical across files by including the phase fns in all three.

- [ ] **Step 2: Build**

Run: `dotnet build WG16.csproj` — Expected `0 Error(s)`.

- [ ] **Step 3: Run the self-check (windowed)**

`"<godot>" ... scenes/review.tscn -- --atmoscheck`
Expected:
- No `atmo_multiscatter.glsl:` compile error.
- `[atmocheck] transmittance: ... -> PASS`
- (skyview line still prints only after Task 3; for now confirm MS compiles & dispatches — add a temporary MS readback line if desired, else rely on Task 3's skyview luma which depends on MS being sane).

- [ ] **Step 4: Commit**

```bash
git add shaders/atmosphere_multiscatter.glsl scripts/lab/AtmosphereCompute.cs
git commit -m "atmosphere(AT-1): multi-scatter LUT (Hillaire isotropic approximation)"
```

---

## Task 3: Sky-view LUT + sample it in the sky shader (the A/B toggle) — THE GATE

Adds the sky-view LUT (per-direction scattering using the real world sun dir) and wires the sky shader to sample it when `atmosphere_on`. After this, the physical sky is visible & A/B-toggleable → drive the user's eye-gate.

**Files:**
- Create: `shaders/atmosphere_skyview.glsl`
- Modify: `shaders/cloud_sky.gdshader` (uniforms + `background()`)
- Modify: `scripts/lab/CloudVolume.cs` (`SetAtmosphereOn`, `SetAtmosphereSkyView`, `SetKnob` `"atmo_exposure"`, defaults in `BuildSkyMaterial`)
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (`ApplyCloudBool` `"atmosphere_on"`)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (`PushSunToCloud` → `_atmosphere`)
- Modify: `data/lab_controls.json` (Light-tab toggle + exposure knob)
- Restore the Task 1/2 temporarily-commented `_cloud.SetAtmosphereSkyView` / `SetAtmosphereOn` / `SetKnob("atmo_exposure")` lines.

**Interfaces:**
- Consumes: transmittance (`binding 1`) + multiscatter (`binding 2`) LUTs; `P.sun_turb.xyz` = world to-sun.
- Produces: sky-view LUT in `_skyTex` (already RID-bound to `SkyViewTexture`); sampled in `cloud_sky.gdshader` as `atmo_skyview_tex`.

- [ ] **Step 1: Write the sky-view compute shader**

Create `shaders/atmosphere_skyview.glsl`:

```glsl
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2) uniform sampler2D msLUT;
layout(set = 0, binding = 3, std430) restrict readonly buffer Params { vec4 sun_turb; } P;

// ===== Hillaire shared block (keep identical across atmosphere_*.glsl) =====
const float PI = 3.14159265358979;
const float groundRadiusMM = 6.360;
const float atmosphereRadiusMM = 6.460;
const vec3  rayleighScatteringBase = vec3(5.802, 13.558, 33.1);
const float mieScatteringBase = 3.996;
const float mieAbsorptionBase = 4.4;
const vec3  ozoneAbsorptionBase = vec3(0.650, 1.881, 0.085);
float getMiePhase(float c){
    const float g = 0.8;
    float n = (1.0 - g*g) * (1.0 + c*c);
    float d = (2.0 + g*g) * pow(1.0 + g*g - 2.0*g*c, 1.5);
    return (3.0 / (8.0*PI)) * n / d;
}
float getRayleighPhase(float c){ return (3.0/(16.0*PI)) * (1.0 + c*c); }
void getScatteringValues(vec3 pos, out vec3 rayleighS, out float mieS, out vec3 extinction){
    float altKM = (length(pos) - groundRadiusMM) * 1000.0;
    float rD = exp(-altKM / 8.0);
    float mD = exp(-altKM / 1.2);
    rayleighS = rayleighScatteringBase * rD;
    mieS = mieScatteringBase * mD;
    float mieA = mieAbsorptionBase * mD;
    vec3 ozoneA = ozoneAbsorptionBase * max(0.0, 1.0 - abs(altKM - 25.0) / 15.0);
    extinction = rayleighS + vec3(mieS + mieA) + ozoneA;
}
float rayIntersectSphere(vec3 ro, vec3 rd, float rad){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - rad*rad;
    if (c > 0.0 && b > 0.0) return -1.0;
    float d = b*b - c;
    if (d < 0.0) return -1.0;
    if (d > b*b) return -b + sqrt(d);
    return -b - sqrt(d);
}
vec3 getValFromLUT(sampler2D lut, vec3 pos, vec3 sunDir){
    float h = length(pos);
    vec3 up = pos / h;
    float c = dot(sunDir, up);
    vec2 uv = vec2(clamp(0.5 + 0.5*c, 0.0, 1.0),
                   clamp((h - groundRadiusMM) / (atmosphereRadiusMM - groundRadiusMM), 0.0, 1.0));
    return texture(lut, uv).rgb;
}
// ===== end shared block =====

const float SKY_STEPS = 32.0;

vec3 raymarchSky(vec3 pos, vec3 rayDir, vec3 sunDir){
    float atmoDist = rayIntersectSphere(pos, rayDir, atmosphereRadiusMM);
    float groundDist = rayIntersectSphere(pos, rayDir, groundRadiusMM);
    float tMax = (groundDist > 0.0) ? groundDist : atmoDist;
    if (tMax < 0.0) { return vec3(0.0); }

    float cosTheta = dot(rayDir, sunDir);
    float miePhase = getMiePhase(cosTheta);
    float rayPhase = getRayleighPhase(-cosTheta);

    vec3 lum = vec3(0.0), tr = vec3(1.0);
    float t = 0.0;
    for (float s = 0.0; s < SKY_STEPS; s += 1.0){
        float nT = ((s + 0.3) / SKY_STEPS) * tMax;
        float dt = nT - t; t = nT;
        vec3 np = pos + t * rayDir;
        vec3 rs; float ms; vec3 ext;
        getScatteringValues(np, rs, ms, ext);
        vec3 sampleTr = exp(-dt * ext);
        vec3 sunTr = getValFromLUT(transLUT, np, sunDir);
        vec3 psi = getValFromLUT(msLUT, np, sunDir);
        vec3 rayInS = rs * (rayPhase * sunTr + psi);
        vec3 mieInS = vec3(ms) * (miePhase * sunTr + psi);
        vec3 inS = rayInS + mieInS;
        vec3 scatterInt = (inS - inS * sampleTr) / ext;
        lum += tr * scatterInt;
        tr *= sampleTr;
    }
    return lum;
}

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    vec3 sunDir = normalize(P.sun_turb.xyz);
    vec3 pos = vec3(0.0, groundRadiusMM + 0.0002, 0.0);   // camera ~200 m above ground, inside the atmosphere
    float az = uv.x * 2.0 * PI;
    float el = (uv.y * uv.y) * (0.5 * PI);                 // sqrt warp → dense near the horizon
    float ce = cos(el);
    vec3 rayDir = vec3(ce * cos(az), sin(el), ce * sin(az));
    imageStore(outTex, id, vec4(raymarchSky(pos, rayDir, sunDir), 1.0));
}
```

- [ ] **Step 2: Sky-shader uniforms + sampling**

In `shaders/cloud_sky.gdshader`, after the `cloud_debug` uniform (line ~13) add:

```glsl
// --- AT-1 GPU atmosphere: sky-view LUT provider (default off = approved keyframed gradient) ---
uniform bool atmosphere_on = false;
uniform sampler2D atmo_skyview_tex : filter_linear, repeat_enable;   // Hillaire sky-view LUT (az,el)
uniform float atmo_exposure : hint_range(0.0, 60.0) = 10.0;          // scales LUT radiance into the tonemapper (gate-tuned)
```

Replace the `background()` function (lines 84–90) with:

```glsl
// AT-1 sky-view sampling mapping — MUST match atmosphere_skyview.glsl's per-texel ray build.
vec2 atmo_dir_to_uv(vec3 d){
    float az = atan(d.z, d.x); if (az < 0.0) az += 2.0 * PI;
    float el = asin(clamp(d.y, 0.0, 1.0));
    return vec2(az / (2.0 * PI), sqrt(el / (0.5 * PI)));
}

vec3 background(vec3 dir){
    // keyframed gradient (the approved look — also the night fallback)
    vec3 grad = (dir.y >= 0.0)
        ? mix(sky_horizon, sky_top, pow(clamp(dir.y, 0.0, 1.0), 0.5))
        : mix(sky_horizon, ground_color, clamp(-dir.y * 4.0, 0.0, 1.0));
    if (atmosphere_on && dir.y >= 0.0){
        vec3 atmo = texture(atmo_skyview_tex, atmo_dir_to_uv(dir)).rgb * atmo_exposure;
        // DAY = physical atmosphere; blend to the approved keyframed gradient as night falls so the
        // gated night look (moon/stars/night-grade) is regression-free + the dusk→night tail is clean.
        return mix(atmo, grad, night_factor);
    }
    return grad;
}
```

- [ ] **Step 3: CloudVolume setters + defaults**

In `scripts/lab/CloudVolume.cs` `BuildSkyMaterial` (after the `cloud_debug` set, ~line 143):

```csharp
        _skyMat.SetShaderParameter("atmosphere_on", false);
        _skyMat.SetShaderParameter("atmo_exposure", _atmoExposure);
```

Add the field + setters (near the other sky-material setters, e.g. after `SetCirrus`):

```csharp
    // --- AT-1 GPU atmosphere: sky-view LUT provider (default off). CloudVolume stays the SOLE _skyMat writer. ---
    private float _atmoExposure = 10.0f;
    public void SetAtmosphereOn(bool on) { _skyMat?.SetShaderParameter("atmosphere_on", on); }
    public void SetAtmosphereSkyView(Texture2Drd? tex) { if (tex != null) { _skyMat?.SetShaderParameter("atmo_skyview_tex", tex); } }
```

Add to the `SetKnob` switch (with the other cases):

```csharp
            case "atmo_exposure":   _atmoExposure = Mathf.Clamp(v, 0f, 60f); _skyMat?.SetShaderParameter("atmo_exposure", _atmoExposure); break;
```

- [ ] **Step 4: Route the toggle + push the sun**

In `scripts/lab/TerrainLabUI.Clouds.cs` `ApplyCloudBool` (top of the method, with the other early-returns):

```csharp
        if (knob == "atmosphere_on") { _cloud?.SetAtmosphereOn(on); _atmosphere?.SetEnabled(on); return; }   // AT-1
```

In `scripts/lab/TerrainLabUI.Apply.cs` `PushSunToCloud`, after `_cloud?.SetSun(...)`:

```csharp
        _atmosphere?.SetSun(toSun);   // AT-1: recompute the sky-view LUT when the sun moves
```

Restore the lines temporarily commented in Tasks 1/2: `_cloud.SetAtmosphereSkyView(_atmosphere.SkyViewTexture)` (in `AttachClouds`) and the `_cloud?.SetAtmosphereOn(true)` / `_cloud?.SetKnob("atmo_exposure", ...)` CLI lines.

- [ ] **Step 5: Lab controls (Light tab)**

In `data/lab_controls.json`, add two entries to the Light-tab control list (next to the other Light-tab sky controls):

```json
    { "id": "atmosphere_on", "label": "GPU atmosphere (AT-1)", "tab": "Light", "type": "cloud", "cloud": "atmosphere_on", "default": false, "rand": false },
    { "id": "atmo_exposure", "label": "atmosphere exposure", "tab": "Light", "type": "cloudf", "cloud": "atmo_exposure", "min": 0.0, "max": 60.0, "default": 10.0, "rand": false },
```

- [ ] **Step 6: Build + import**

Run: `dotnet build WG16.csproj` (Expected `0 Error(s)`), then headless import:
`"<godot_console.exe>" --headless --path . --import` (Expected: no errors; GLSL compute is NOT compiled here — windowed only).

- [ ] **Step 7: Mechanical A/B + self-check + perf (windowed, `--auto-shot`)**

1. **Regression (off == baseline):**
   `... scenes/review.tscn -- --auto-shot=/c/tmp/atmo_off.png` (atmosphere default off) and compare to a fresh baseline shot from `main`-look — must be **pixel-identical** (off changes nothing).
2. **On differs + sane:**
   `... scenes/review.tscn -- --atmosphere=1 --time=12 --atmoscheck --auto-shot=/c/tmp/atmo_noon.png`
   Expected console: `[atmocheck] skyview: horizonLuma=… zenithLuma=… minCh≥-0.001 finite=True -> PASS`, and **zenithLuma < horizonLuma** is NOT required (depends on exposure) but both finite & non-negative; the shot shows a blue sky brightening toward the horizon/sun.
3. **Dawn/dusk:** `--atmosphere=1 --time=6` and `--time=18` shots → warm reddened horizon near the sun, blue opposite.
4. **Perf:** `--atmosphere=1 --autotime=1 --profmove` → confirm in-motion cost within budget (LUT recompute is tiny; expect ≈ no change vs off). Log the number.
5. **Cloud regression:** `--shadowcheck` still PASS (AT-1 didn't touch the cloud shaders).

- [ ] **Step 8: Commit**

```bash
git add shaders/atmosphere_skyview.glsl shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Clouds.cs scripts/lab/TerrainLabUI.Apply.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Cli.cs data/lab_controls.json
git commit -m "atmosphere(AT-1): sky-view LUT + sky-shader provider (default off, A/B toggle)"
```

- [ ] **Step 9: USER LIVE A/B EYE-GATE (the real gate)**

Drive `review.tscn` for the user (one Godot at a time): toggle **Light tab → `GPU atmosphere (AT-1)`** on/off and scrub **`time of day`** dawn→noon→dusk→night; tune **`atmosphere exposure`** live to match/beat the keyframed look.
**Judge (user, in motion):** Does the physical sky read **good-or-better** than the keyframed look across the full day? Believable dawn/dusk/twilight gradient? No pops; clean dusk→night handoff (no lingering bright band)? Sun-halo region believable? Default stays OFF unless it wins → then flip the default in a follow-up.
Record the verdict in `DECISIONS.md` + `NEEDS_REVIEW.md`. **Stop here — do not start Task 4 (or AT-2) before this passes.**

---

## Task 4: Atmosphere presets + exposure/tint (POST-GATE — do NOT build before Task 3 passes)

Only after the AT-1 core wins (or is accepted opt-in) its eye-gate. Adds `data/atmosphere_presets.json` (earth_clear / hazy / alien) composing exposure + a sky tint multiply + (optional) a turbidity hook, mirroring the existing preset pattern. Lighter; spec'd when its turn comes. Left as a single follow-up task so the core stays focused on the gate.

---

## Self-Review

**1. Spec coverage** (`specs/2026-06-20-gpu-atmosphere-design.md`):
- "Transmittance + multi-scatter + sky-view LUTs on the seam" → Tasks 1/2/3. ✓
- "AtmosphereCompute.cs … mirrors CloudVolume's seam … RID assigned ONCE … recompute only on sun/param change" → Task 1 (`InitCompute` assigns once; `_Process` dispatches only when `_dirty`). ✓
- "background() samples the SKY-VIEW LUT when atmosphere_on; off → current gradient (no regression)" → Task 3 Step 2. ✓
- "toggleable sky-color provider, default OFF; flips only if it wins" → defaults off everywhere; Task 3 Step 9 gate. ✓
- "ComposeLighting pushes sun dir + params" → Task 3 Step 4 (`PushSunToCloud` → `_atmosphere.SetSun`). ✓
- "scenes/*.tscn add the AtmosphereCompute node (deferred-add)" → Task 1 Step 4 (created in `_Ready`, added/attached in `AttachClouds` — the lab adds nodes in code, not the .tscn; matches how `CloudVolume`/`GodRaysScreen` are done). ✓
- "data/atmosphere_presets.json (AT-1+)" → Task 4 (post-gate, per the "+"). ✓
- Risks: seam (assign once — Task 1); local-RD vs render-thread (render-thread RD — Task 1); look regression (default off + A/B — Task 3); headless (windowed verify — all run steps); night/twilight tail (night_factor blend — Task 3 Step 2); AT-3 ordering (out of scope — this is AT-1 only). ✓

**2. Placeholder scan:** No "TBD"/"add error handling"/"similar to". The two cross-task comment/uncomment notes (Task 1 ms/sky `Compile` lines, the `_cloud.SetAtmosphere*` calls) are explicit & exact, not placeholders — they exist because the C# references symbols added in later tasks; an inline executor can instead write Tasks 2/3's shader files + CloudVolume setters first to avoid commenting. ✓

**3. Type consistency:** `AtmosphereCompute` public surface (`Attach`/`SetEnabled`/`SetSun`/`SetTurbidity`/`RequestCheck`/`SkyViewTexture`/`Ready`) is used consistently in `TerrainLabUI.cs`, `.Apply.cs`, `.Cli.cs`. `CloudVolume.SetAtmosphereOn`/`SetAtmosphereSkyView`/`SetKnob("atmo_exposure")` match the call sites. Shader uniforms `atmosphere_on`/`atmo_skyview_tex`/`atmo_exposure` match the C# `SetShaderParameter` keys. The sky-view UV mapping (`atmo_dir_to_uv`) matches the compute's per-texel ray build (az = `u*2π`, el = `v²·π/2`). ✓
