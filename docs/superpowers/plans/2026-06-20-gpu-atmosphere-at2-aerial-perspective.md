# GPU Atmosphere AT-2 (Aerial Perspective) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **This plan is the start point for a NEW session** (see the handoff `docs/superpowers/handoffs/2026-06-20-at2-aerial-build-start-here.md`).

**Goal:** Physical aerial perspective (distance haze) on terrain — in-scatter + extinction between camera and surface, varying with distance/altitude/sun/time — via a Hillaire froxel LUT + a screen-space composite, **without editing the terrain shader**. Default ON with the atmosphere; OFF restores today's built-in fog.

**Architecture:** Extend `AtmosphereCompute` with a 4th LUT — a camera-frustum-aligned 3D aerial froxel (32³, rgb=in-scatter, a=transmittance) recomputed per-frame from the camera + sun (reusing the transmittance + multi-scatter LUTs). A new clip-space fullscreen-quad pass (`AerialPerspective.cs` + `aerial_screen.gdshader`, mirroring `GodRaysScreen`) reads the rendered frame + depth, reconstructs per-pixel distance, samples the froxel, and composites `color·T + inscatter` on geometry only (sky skipped). When AT-2 is on, the built-in aerial fog drops so the froxel owns distance haze.

**Tech Stack:** Godot 4.6 mono (C# + GLSL compute via `RenderingDevice`), `Texture2Drd` compute→material bridge, `Std430Writer`, clip-space fullscreen-quad screen pass.

## Global Constraints

- **Pillars:** quality = performance = AAA-ish = long-term-best. The user explicitly chose this (froxel) route over the cheaper "drive the built-in fog" one.
- **Discipline:** AT-2 is ONE sub-phase past AT-1 (passed). Build T1 then T2; T2 ends at the user's live A/B eye-gate. Don't start AT-3.
- **No terrain-shader edit** (`terrain_lab.gdshader` = Ground lane). **No GodRays* / shaders/godray* edit** (other chat). AT-2 = new `AerialPerspective.cs` + `shaders/aerial_screen.gdshader` + `shaders/atmosphere_aerial.glsl`, mirroring those patterns.
- **Seam law** (memory `compute-to-material-callonrenderthread`): aerial LUT built on the render-thread RD, RID → a `Texture2Drd` assigned ONCE, never reassigned. Mirror the existing 3 LUTs in `AtmosphereCompute`.
- **Default ON** with the atmosphere; `--aerial=0` / Light-tab toggle = off. OFF must restore the built-in-fog look exactly (A/B pixel check on the terrain haze).
- **Keep the Hillaire shared block byte-identical** across all `atmosphere_*.glsl` (now 4 files) — edit them together.
- **No-TDD (GPU/visual):** per-task test = build → `--import` → `--auto-shot` A/B + `--aerialcheck`/`--atmoscheck`/`--shadowcheck` + `--profmove` → the user's live eye.
- **Run (one Godot at a time; kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`):**
  `"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`. Build: `dotnet build WG16.csproj` (from the project dir).
- **Coordination:** `git add` your paths explicitly, never `-A`. Commit-by-default; push only when asked.

## Reference templates (read before building)

- `scripts/lab/AtmosphereCompute.cs` — the LUT seam (InitCompute, CreateTex, EnsureSets, Recompute, Dispatch, BuildParams, DumpCheck, _ExitTree). The aerial LUT mirrors this; the new bits are a 3D texture + camera params + a per-frame recompute path.
- `shaders/atmosphere_skyview.glsl` — the raymarch + `getValFromLUT(transLUT/msLUT)` pattern the aerial march reuses; the Hillaire shared block to copy verbatim.
- `scripts/lab/GodRaysScreen.cs` + `shaders/godray_screen.gdshader` — the clip-space fullscreen-quad screen pass: how it reads `hint_screen_texture`/`hint_depth_texture`, reconstructs world position from `inv_view_proj` + `cam_world`, and is driven per-frame. AT-2's pass mirrors this exactly (own files).

---

## Task 1 (AT2-1): Aerial froxel LUT in AtmosphereCompute

Adds the 3D aerial LUT + camera params + a per-frame recompute path + a numeric self-check. Nothing is composited to screen yet (zero visual change). Deliverable: `--aerialcheck` proves the froxel is sane; the existing `--atmoscheck` still passes.

**Files:**
- Create: `shaders/atmosphere_aerial.glsl`
- Modify: `scripts/lab/AtmosphereCompute.cs`
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (push the camera each frame)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (`--aerialcheck`) + the CLI apply site

**Interfaces:**
- Produces: `AtmosphereCompute.AerialTexture` (`Texture2Drd`, 3D), `AtmosphereCompute.AerialReady` (bool), `AtmosphereCompute.SetCamera(Vector3 camPos, float farDist, Projection invViewProj)`, `AtmosphereCompute.RequestAerialCheck()`.

- [ ] **Step 1: Write `shaders/atmosphere_aerial.glsl`**

(Copy the Hillaire shared block VERBATIM from `atmosphere_skyview.glsl` — consts, `getMiePhase`, `getRayleighPhase`, `getScatteringValues`, `rayIntersectSphere`, `getValFromLUT`. Then:)

```glsl
#[compute]
#version 450

// AT-2 aerial-perspective froxel LUT. gid.xy = screen tile, looped z = depth slice (near→far,
// quadratic bias). Each froxel = accumulated in-scatter (rgb) + mean transmittance (a) from the
// camera to that slice's distance, raymarching the atmosphere (single+multi scatter). Units: world
// is METRES; the atmosphere is MEGAMETRES (×1e-6). Horizontal position is ignored (atmosphere is
// ~uniform over the ~32 km froxel range); only altitude (camPos.y + rayDir.y·t) matters.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image3D aerialTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2) uniform sampler2D msLUT;
layout(set = 0, binding = 3, std430) restrict readonly buffer Params {
    vec4 sun_turb;        // xyz = world to-sun, w = turbidity (reserved)
    vec4 cam_pos_far;     // xyz = camera world pos (m), w = max aerial distance (m, ~32000)
    mat4 inv_view_proj;   // NDC → world (same as godray_screen)
} P;

// ===== Hillaire shared block (paste verbatim from atmosphere_skyview.glsl) =====
// const PI/groundRadiusMM/... ; getMiePhase; getRayleighPhase; getScatteringValues;
// rayIntersectSphere; getValFromLUT  — KEEP IDENTICAL across atmosphere_*.glsl.
// ===== end shared block =====

void main(){
    ivec3 sz = imageSize(aerialTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }

    vec2 uv = (vec2(id) + 0.5) / vec2(sz.xy);
    // world ray for this tile (NDC far point through inv_view_proj, same as godray depth reconstruct)
    vec4 wf = P.inv_view_proj * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 camPos = P.cam_pos_far.xyz;
    vec3 rayDir = normalize(wf.xyz / wf.w - camPos);
    float maxDist = P.cam_pos_far.w;                 // metres
    float camY_Mm = camPos.y * 1e-6;
    vec3 sunDir = normalize(P.sun_turb.xyz);
    float cosT = dot(rayDir, sunDir);
    float miePhase = getMiePhase(cosT);
    float rayPhase = getRayleighPhase(-cosT);

    vec3 lum = vec3(0.0), tr = vec3(1.0);
    float prevT = 0.0;
    const int SUB = 4;
    for (int z = 0; z < sz.z; z++){
        float frac = (float(z) + 1.0) / float(sz.z);
        float t_m = maxDist * frac * frac;            // quadratic near-bias, metres
        for (int s = 0; s < SUB; s++){
            float mid = mix(prevT, t_m, (float(s) + 0.5) / float(SUB));   // metres
            float dt_Mm = ((t_m - prevT) / float(SUB)) * 1e-6;
            float alt_Mm = camY_Mm + rayDir.y * mid * 1e-6;
            vec3 pos = vec3(0.0, groundRadiusMM + alt_Mm, 0.0);
            vec3 rs; float ms; vec3 ext; getScatteringValues(pos, rs, ms, ext);
            vec3 sampleTr = exp(-dt_Mm * ext);
            vec3 sunTr = getValFromLUT(transLUT, pos, sunDir);
            vec3 psi = getValFromLUT(msLUT, pos, sunDir);
            vec3 inS = rs * (rayPhase * sunTr + psi) + vec3(ms) * (miePhase * sunTr + psi);
            vec3 scatterInt = (inS - inS * sampleTr) / max(ext, vec3(1e-6));
            lum += tr * scatterInt;
            tr *= sampleTr;
        }
        prevT = t_m;
        imageStore(aerialTex, ivec3(id, z), vec4(lum, dot(tr, vec3(0.33333))));   // rgb inscatter, a = mean T
    }
}
```

- [ ] **Step 2: AtmosphereCompute — fields + 3D texture + camera params**

In `AtmosphereCompute.cs`: add constants + fields:

```csharp
    private const int AerialW = 32, AerialH = 32, AerialD = 32;
    private Rid _aerialShader, _aerialPipe, _aerialTex, _aerialSet;
    private Texture2Drd? _aerialRd;
    private bool _camDirty = true, _aerialCheckRequested;
    private Vector3 _camPos = Vector3.Zero;
    private float _aerialFar = 32000f;                 // metres
    private Godot.Projection _invViewProj = Godot.Projection.Identity;
    public Texture2Drd? AerialTexture => _aerialRd;
    public bool AerialReady => _ready && _aerialRd != null;
    public void SetCamera(Vector3 camPos, float farDist, Godot.Projection invViewProj)
    {
        _camPos = camPos; _aerialFar = farDist; _invViewProj = invViewProj; _camDirty = true;
    }
    public void RequestAerialCheck() { _aerialCheckRequested = true; _enabled = true; _camDirty = true; }
```

Extend `BuildParams()` to write the larger struct (the existing 3 shaders read only the first vec4 — leaving the buffer larger is fine):

```csharp
    private byte[] BuildParams()
    {
        var w = new Std430Writer().Vec4(_sunDir, _turbidity).Vec4(_camPos, _aerialFar);
        // mat4 = 4 columns (Projection.X/Y/Z/W are Vector4) as 4 vec4 (std430 16B each)
        var p = _invViewProj;
        w.Vec4(p.X.X, p.X.Y, p.X.Z, p.X.W).Vec4(p.Y.X, p.Y.Y, p.Y.Z, p.Y.W)
         .Vec4(p.Z.X, p.Z.Y, p.Z.Z, p.Z.W).Vec4(p.W.X, p.W.Y, p.W.Z, p.W.W);
        return w.ToArray();
    }
```
> Verify `Std430Writer` has `.Vec4(Vector3,float)` and `.Vec4(float,float,float,float)` (CloudVolume uses both) — it does. If `Projection` columns differ (check `Godot.Projection` has `X,Y,Z,W` of type `Vector4`), write the 16 floats in column-major order regardless.

- [ ] **Step 3: AtmosphereCompute — create the 3D texture + shader/pipeline + RID**

Add a 3D-texture helper + wire the aerial LUT in `InitCompute` (after the skyview block, before `_ready = true`):

```csharp
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
```
In `InitCompute` (mirror the skyview compile + the once-RID assignment):
```csharp
        _aerialShader = Compile("res://shaders/atmosphere_aerial.glsl", "atmo_aerial");
        if (_aerialShader.IsValid) { _aerialPipe = _rd.ComputePipelineCreate(_aerialShader); }
        _aerialTex = CreateTex3D(AerialW, AerialH, AerialD);
        _aerialRd = new Texture2Drd();           // NOTE: Texture2Drd wraps a 3D RID fine; the material samples sampler3D
        // ... after the existing `if (_skyViewRd != null) { _skyViewRd.TextureRdRid = _skyTex; }` line, add:
        if (_aerialRd != null) { _aerialRd.TextureRdRid = _aerialTex; }   // assign ONCE
```
> If `Texture2Drd` rejects a 3D RID (it may be 2D-only), fall back to `Texture3Drd` (Godot 4.6 has it) for `_aerialRd` and declare `uniform sampler3D` in `aerial_screen.gdshader`. Confirm at build; the rest is identical.

Build the aerial uniform set in `EnsureSets` (image3D binding 0, transLUT 1, msLUT 2, params 3):
```csharp
        if (_aerialShader.IsValid)
        {
            var aImg = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; aImg.AddId(_aerialTex);
            var aT = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; aT.AddId(_sampler); aT.AddId(_transTex);
            var aM = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; aM.AddId(_sampler); aM.AddId(_msTex);
            var aP = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 3 }; aP.AddId(_paramBuf);
            _aerialSet = _rd.UniformSetCreate(new Array<RDUniform> { aImg, aT, aM, aP }, _aerialShader, 0);
        }
```

- [ ] **Step 4: AtmosphereCompute — split recompute (sun vs camera) + aerial dispatch**

Replace `_Process` + `Recompute` so the camera-only change recomputes ONLY the aerial LUT (cheap), while a sun/param change recomputes all four:

```csharp
    public override void _Process(double delta)
    {
        if (!_ready || !_enabled) { return; }
        if (_dirty) { _dirty = false; _camDirty = false; RenderingServer.CallOnRenderThread(Callable.From(RecomputeAll)); }
        else if (_camDirty) { _camDirty = false; RenderingServer.CallOnRenderThread(Callable.From(RecomputeAerial)); }
    }
    private void RecomputeAll()
    {
        if (!_ready) { return; }
        byte[] pb = BuildParams(); _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);
        EnsureSets();
        Dispatch(_transPipe, _transSet, TransW, TransH);
        if (_msShader.IsValid)  { Dispatch(_msPipe, _msSet, MsW, MsH); }
        if (_skyShader.IsValid) { Dispatch(_skyPipe, _skySet, SkyW, SkyH); }
        DispatchAerial();
        if (_checkRequested) { _checkRequested = false; DumpCheck(); }
        if (_aerialCheckRequested) { _aerialCheckRequested = false; DumpAerialCheck(); }
    }
    private void RecomputeAerial()
    {
        if (!_ready) { return; }
        byte[] pb = BuildParams(); _rd.BufferUpdate(_paramBuf, 0, (uint)pb.Length, pb);   // camera changed → re-upload params
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
        _rd.ComputeListDispatch(l, (uint)((AerialW + 7) / 8), (uint)((AerialH + 7) / 8), 1);   // z looped in-shader
        _rd.ComputeListEnd();
    }
```
> Keep the old `Recompute()` name only if referenced elsewhere; otherwise rename to `RecomputeAll`. Also free the aerial RIDs in `_ExitTree` (`_aerialSet`, `_aerialTex`, `_aerialPipe`, `_aerialShader`) with `.IsValid` guards, mirroring the existing frees.

- [ ] **Step 5: `--aerialcheck` readback**

Add to `AtmosphereCompute.cs` (mirror `DumpCheck`):
```csharp
    private void DumpAerialCheck()
    {
        byte[] d = _rd.TextureGetData(_aerialTex, 0);   // 32×32×32 rgba16f
        int n = AerialW * AerialH * AerialD; bool fin = true; float tmin = 1e9f, tmax = -1e9f, imin = 1e9f;
        for (int i = 0; i < n; i++)
        {
            float r = Half(d, i*8), g = Half(d, i*8+2), b = Half(d, i*8+4), a = Half(d, i*8+6);
            if (!float.IsFinite(r)||!float.IsFinite(g)||!float.IsFinite(b)||!float.IsFinite(a)) { fin = false; }
            imin = Mathf.Min(imin, Mathf.Min(r, Mathf.Min(g, b)));
            tmin = Mathf.Min(tmin, a); tmax = Mathf.Max(tmax, a);
        }
        bool pass = fin && imin >= -1e-3f && tmin >= -1e-3f && tmax <= 1.001f;
        GD.Print($"[aerialcheck] inscatterMin={imin:F4} transmittance[{tmin:F3}..{tmax:F3}] finite={fin} -> {(pass?"PASS":"FAIL")}");
    }
```

- [ ] **Step 6: Push the camera each frame (TerrainLabUI.Process.cs)**

In `_Process`, where `_cloud?.SetCameraWorld(camPos)` is called, also push the camera to the atmosphere (compute `invViewProj` like `GodRaysScreen` does):
```csharp
        if (_atmosphere != null && _atmosphereOn)
        {
            var camN = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            var proj = camN.GetCameraProjection();
            var vp = proj * new Godot.Projection(camN.GlobalTransform.AffineInverse());
            _atmosphere.SetCamera(camN.GlobalPosition, 32000f, vp.Inverse());
        }
```

- [ ] **Step 7: CLI `--aerialcheck`** (TerrainLabUI.Cli.cs)

Field `private bool _aerialCheckCli;`; parse `else if (a == "--aerialcheck") { _aerialCheckCli = true; }`; apply (in AttachClouds, after the atmosphere CLI): `if (_aerialCheckCli) { _atmosphereOn = true; _cloud.SetAtmosphereOn(true); _atmosphere?.SetEnabled(true); _atmosphere?.RequestAerialCheck(); }`.

- [ ] **Step 8: Build + verify (windowed)**

`dotnet build WG16.csproj` → 0 errors. Then:
`"<godot>" ... scenes/review.tscn -- --aerialcheck --time=12 --auto-shot=/c/tmp/at2_lutcheck.png`
Expected console: `AtmosphereCompute: LUT compute initialized...`, `[aerialcheck] ... -> PASS`, `[atmocheck]`-style lines still PASS if also requested. Scene visually UNCHANGED (nothing composited yet). `--shadowcheck` still PASS.

- [ ] **Step 9: Commit**

```bash
git add shaders/atmosphere_aerial.glsl scripts/lab/AtmosphereCompute.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "atmosphere(AT-2): aerial-perspective froxel LUT (no composite yet) + --aerialcheck"
```

---

## Task 2 (AT2-2): Screen-space aerial composite — THE GATE

Adds the fullscreen pass that applies the froxel to the scene, the fog handoff, the toggle/CLI/control, and the readiness gate. After this, aerial perspective is visible + A/B-toggleable → drive the user's eye-gate.

**Files:**
- Create: `shaders/aerial_screen.gdshader`
- Create: `scripts/lab/AerialPerspective.cs`
- Modify: `scripts/lab/TerrainLabUI.cs` (AttachClouds: create/attach/bind), `.Process.cs` (readiness gate), `.Lighting.cs` (fog handoff), `.Clouds.cs` (`aerial_on` field + ApplyCloudBool), `.Cli.cs` (`--aerial`, `--aerialstr`), `.Review.cs` (A/B), `data/lab_controls.json`.

**Interfaces:**
- Consumes: `AtmosphereCompute.AerialTexture` / `.AerialReady`.
- Produces: `AerialPerspective` with `Attach(Camera3D)`, `SetEnabled(bool)`, `SetAerialTexture(Texture2Drd)`, `SetStrength(float)`, `SetReady(bool)`.

- [ ] **Step 1: `shaders/aerial_screen.gdshader`**

```glsl
shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, cull_disabled, fog_disabled;

// Clip-space fullscreen quad (vertex writes POSITION directly — covers the screen regardless of node xform).
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex  : hint_depth_texture, filter_linear;
uniform sampler3D aerial_tex : filter_linear;            // froxel: rgb inscatter, a = transmittance
uniform mat4  inv_view_proj;
uniform vec3  cam_world;
uniform float aerial_far = 32000.0;                       // metres (matches the LUT)
uniform float aerial_strength = 10.0;                     // matches atmo_exposure (inscatter is in LUT radiance units)
uniform bool  aerial_on = false;

void vertex() { POSITION = vec4(VERTEX.xy, 1.0, 1.0); }   // z=1 (far), clip-space fullscreen

void fragment(){
    vec2 uv = SCREEN_UV;
    vec4 col = texture(screen_tex, uv);
    float d = texture(depth_tex, uv).r;
    // sky gate: depth at the far plane (no geometry) → leave the AT-1 sky untouched
    if (!aerial_on || d <= 0.000001) { ALPHA = 0.0; discard; }   // Godot: reversed-Z, far = 0
    // reconstruct world distance from depth (same as godray_screen)
    vec4 ndc = vec4(uv * 2.0 - 1.0, d, 1.0);
    vec4 wp = inv_view_proj * ndc; wp /= wp.w;
    float dist = length(wp.xyz - cam_world);              // metres
    // froxel z = inverse of the LUT's quadratic near-bias: frac = sqrt(dist/far)
    float zf = clamp(sqrt(clamp(dist / aerial_far, 0.0, 1.0)), 0.0, 1.0);
    vec4 aer = texture(aerial_tex, vec3(uv, zf));         // rgb inscatter, a = transmittance
    vec3 outc = col.rgb * aer.a + aer.rgb * aerial_strength;
    COLOR = vec4(outc, 1.0);
}
```
> Depth convention: Godot 4 uses reversed-Z (far ≈ 0, near ≈ 1) — the `d <= eps` sky gate + `ndc.z = d` reconstruction match `godray_screen.gdshader`; **mirror godray's exact lines if they differ** (it's the proven reference). If the sky gets haze (gate inverted), flip the `d` test.

- [ ] **Step 2: `scripts/lab/AerialPerspective.cs`** (mirror GodRaysScreen)

```csharp
using Godot;
namespace WG16.Lab;

/// AT-2 screen-space aerial perspective. A clip-space fullscreen quad (mirrors GodRaysScreen) that reads
/// the rendered frame + depth, samples the camera-aligned aerial froxel LUT (AtmosphereCompute.AerialTexture)
/// by (screen UV, reconstructed distance), and composites color·T + inscatter on geometry (sky skipped).
public partial class AerialPerspective : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private bool _on;

    public AerialPerspective()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/aerial_screen.gdshader") };
        _mat.RenderPriority = 120;   // BELOW GodRaysScreen's 127 → aerial composites first, beams on top
        _quad = new MeshInstance3D
        {
            Name = "AerialScreenQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_quad);
    }

    public void Attach(Camera3D cam) { _cam = cam; }
    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; _mat.SetShaderParameter("aerial_on", on); }
    public void SetAerialTexture(Texture2Drd? tex) { if (tex != null) { _mat.SetShaderParameter("aerial_tex", tex); } }
    public void SetReady(bool ready) { _mat.SetShaderParameter("aerial_on", ready && _on); }
    public void SetStrength(float s) { _mat.SetShaderParameter("aerial_strength", Mathf.Clamp(s, 0f, 60f)); }
    public bool On => _on;

    public override void _Process(double delta)
    {
        if (!_on || _cam == null) { return; }
        Projection proj = _cam.GetCameraProjection();
        Projection vp = proj * new Projection(_cam.GlobalTransform.AffineInverse());
        _mat.SetShaderParameter("inv_view_proj", vp.Inverse());
        _mat.SetShaderParameter("cam_world", _cam.GlobalPosition);
        _mat.SetShaderParameter("aerial_far", 32000f);
    }
}
```

- [ ] **Step 3: Create + attach + bind (TerrainLabUI.cs)**

Field near `_atmosphere`: `private AerialPerspective? _aerial;`. In `_Ready` (near the other node creates): `_aerial = new AerialPerspective { Name = "AerialPerspective" };`. In `AttachClouds` (after the atmosphere block): 
```csharp
        if (_aerial != null && _atmosphere != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_aerial);
            _aerial.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"));
            _aerial.SetAerialTexture(_atmosphere.AerialTexture);
            _aerial.SetStrength(_atmoExposureForAerial());   // start = atmo_exposure (match the sky); or 10f
        }
```
(If `_atmoExposureForAerial()` is awkward, just `_aerial.SetStrength(10f);` — gate-tunable.)

- [ ] **Step 4: Readiness gate + on-state (TerrainLabUI.Process.cs)**

Mirror the `_atmoMatActivated` gate: once `AerialReady` and aerial is on, enable the pass (avoids sampling an unbound 3D LUT). Add field `_aerialActivated`:
```csharp
        if (_aerialOn && !_aerialActivated && _atmosphere != null && _atmosphere.AerialReady)
        {
            _aerial?.SetEnabled(true);
            _aerialActivated = true;
        }
```

- [ ] **Step 5: Fog handoff (TerrainLabUI.Lighting.cs)**

In `ComposeLighting`, after the existing `FogAerialPerspective` line, drop the built-in aerial fog when AT-2 owns it:
```csharp
        if (_aerialOn) { env.FogAerialPerspective = 0.0f; }   // AT-2 froxel owns distance haze (no double-fog)
```
(Height fog / FogDensity stay; only the sky/aerial blend is handed off.)

- [ ] **Step 6: Toggle + field (TerrainLabUI.Clouds.cs)**

Fields: `private bool _aerialOn = true; private bool _aerialActivated;`. In `ApplyCloudBool` (top): `if (knob == "aerial_on") { _aerialOn = on; _aerial?.SetEnabled(on && (_atmosphere?.AerialReady ?? false)); if (!on) { _aerialActivated = false; } ComposeLighting(); return; }`.

- [ ] **Step 7: CLI + lab control**

`.Cli.cs`: `private int _aerialCli = -1;` + `private float _aerialStrCli = -1f;`; parse `--aerial[=1]` (mirror `--atmosphere`) + `--aerialstr=N`. Apply in AttachClouds: `if (_aerialCli == 0) { _aerialOn = false; _aerial?.SetEnabled(false); ComposeLighting(); }` and `if (_aerialStrCli >= 0f) { _aerial?.SetStrength(_aerialStrCli); }`.
`data/lab_controls.json` (Light tab): `{ "id": "aerial_on", "label": "aerial perspective (AT-2)", "tab": "Light", "type": "cloud", "cloud": "aerial_on", "default": true, "rand": false }` and an `aerial strength` `cloudf` (route `atmo_aerial_str` → `_aerial.SetStrength` via a new `ApplyCloudFloat` case, or reuse the existing float path with a dedicated id).

- [ ] **Step 8: Review-key A/B (TerrainLabUI.Review.cs)**

Extend the case-8 banner to mention "toggle `aerial perspective (AT-2)` on/off to A/B the distance haze," so the existing key-8 day-cycle doubles as the AT-2 gate (atmosphere + aerial both on; toggle aerial to compare).

- [ ] **Step 9: Build + import + mechanical A/B**

`dotnet build` → 0 errors; headless `--import`. Then windowed:
1. **OFF == baseline:** `--aerial=0 --time=12 --auto-shot=/c/tmp/at2_off.png` vs a built-in-fog baseline — terrain distance haze identical (AT-2 off restores the old fog).
2. **ON sunset:** `--time=18 --auto-shot=/c/tmp/at2_dusk.png` — distant mountains haze warm; near terrain crisp; sky unchanged vs AT-1.
3. **ON noon:** `--time=12` — distant blue-shift; near crisp.
4. **Sky double-count check:** compare the SKY region on/off — must be identical (gate works).
5. `--profmove` with `--autotime=3` (camera + sun moving = per-frame aerial recompute) — cost within budget; log it.
6. `--shadowcheck` still PASS.

- [ ] **Step 10: Commit**

```bash
git add shaders/aerial_screen.gdshader scripts/lab/AerialPerspective.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.Clouds.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Review.cs data/lab_controls.json
git commit -m "atmosphere(AT-2): screen-space aerial composite + fog handoff (default on, A/B toggle)"
```

- [ ] **Step 11: USER LIVE A/B EYE-GATE**

Drive `review.tscn` (key 8 cycle + the `aerial perspective (AT-2)` toggle). **Judge:** distant terrain hazes believably by time of day (warm low-sun / blue high), near terrain unaffected, sky unchanged (no double-haze), ties cleanly with no double-fog, no temporal popping in fast motion, perf OK. Tune `aerial strength` live. Record in DECISIONS + NEEDS_REVIEW. PASS → AT-2 done (flip default stays on); else iterate (strength, far distance, slice bias) or escalate to temporal jitter (risk #6).

---

## Self-Review

**Spec coverage** (`specs/2026-06-20-gpu-atmosphere-at2-aerial-perspective-design.md`):
- Aerial 3D froxel LUT in AtmosphereCompute → T1 (steps 1-5). ✓
- Camera params + per-frame recompute (camera-aligned) → T1 step 4 (split recompute) + step 6 (push). ✓
- Screen-space composite, no terrain edit → T2 (steps 1-3). ✓
- Sky gate (no double-count) → T2 step 1 (depth gate). ✓
- Fog handoff → T2 step 5. ✓
- Default on + `--aerial=0`/toggle, OFF restores fog → T2 steps 6-7-9. ✓
- Seam law (RID once) → T1 step 3. ✓
- Numeric self-check → T1 step 5 (`--aerialcheck`). ✓
- Eye-gate → T2 step 11. ✓
- Risks: double-haze (sky gate + fog handoff, T2 1/5), depth reconstruct (mirror godray, T2 1), pass ordering (RenderPriority 120 < 127, T2 2), per-frame recompute cost (T2 9 profmove), Texture2Drd first-frame (readiness gate, T2 4), banding/temporal (banked, T2 11). ✓

**Placeholder scan:** No "TBD"/"add error handling". The `> NOTE` blocks (Texture2Drd-vs-Texture3Drd fallback, depth-convention mirror, Std430 matrix order) are explicit verify-at-build items with the exact fallback, not vague placeholders — GPU API specifics that the build session confirms against the live engine.

**Type consistency:** `AerialTexture`/`AerialReady`/`SetCamera`/`RequestAerialCheck` (T1) match the consumers in T2 + Process.cs. `AerialPerspective.Attach/SetEnabled/SetAerialTexture/SetReady/SetStrength` (T2 step 2) match the call sites (T2 step 3/4/6/7). Shader uniforms (`aerial_tex`/`inv_view_proj`/`cam_world`/`aerial_far`/`aerial_strength`/`aerial_on`) match the C# `SetShaderParameter` keys. Param struct order (sun_turb, cam_pos_far, inv_view_proj) matches BuildParams' write order.
