# Cloud Shadows (fresh rebuild) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add drifting cloud shadows to the look lab by attenuating only the direct sun term with a GPU-baked, noise-warped seamless cloud-coverage texture, behind a full live control group.

**Architecture:** A new GPU compute pass (`cloud_coverage.glsl` + `CloudCompute.cs`, mirroring `SplatCompute`) bakes one seamless tiling high-contrast cloud texture at load. The terrain shader gains a custom `light()` that samples that texture in world space (drift via `TIME`, anti-tiling via domain warp), shapes it into a shadow factor, and multiplies the direct sun diffuse+specular by it — leaving GI/ambient full so it can't wash out. When `cloud_enabled` is false, `light()` reproduces Godot's default lighting bit-for-bit. All knobs live in `data/lab_controls.json` under a new Clouds tab (no per-control C#).

**Tech Stack:** Godot 4.6 mono, C#, GLSL compute (RenderingDevice local-RD + readback), Godot spatial shader (`light()`).

**Verification model:** This is GPU/visual work with no unit-test harness. Each task's gate is `dotnet build` + headless `--import` + an `--auto-shot` render that draws clean, plus an A/B (clouds on vs off) where relevant. The FINAL gate is the user flying it live in motion (no stills for the motion artifact — banked lesson). Commit after each task.

**Environment (from HANDOFF):**
- Project dir: `C:\Wg16\wg-16-project`
- Godot console exe (for `--import` / `--auto-shot`): `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
- Godot windowed exe (for live fly): same path without `_console`.
- ALWAYS launch with `--rendering-driver vulkan`.
- ONE Godot process at a time. Kill first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`
- After adding a .cs/shader: `dotnet build WG16.csproj`, then headless `--import`, before launching.
- Auto-shot: append `-- --auto-shot=<abs_png_path>` (launches, waits ~1.5s, saves PNG, quits).

---

## Task 1: GPU coverage bake shader (`cloud_coverage.glsl`)

**Files:**
- Create: `shaders/cloud_coverage.glsl`

Mirrors `shaders/splat_weights.glsl` layout: `#[compute]` marker (stripped by C#), `local_size_x=8, local_size_y=8`, an output storage buffer of `res*res` floats (binding 0) and a params storage buffer (binding 1). Produces a **seamless tiling** (period = res) high-contrast cloud-coverage value in `[0,1]` per texel.

Seamless tiling is achieved by sampling value-noise on a **torus**: map the texel's normalized coords to angles and take noise of `(cos,sin)` pairs so the field wraps at the edges. High contrast comes from FBM (4 octaves) followed by a `smoothstep` contrast curve and a coverage-style remap — NOT a flat value-noise read (that was the old square).

- [ ] **Step 1: Write the shader**

```glsl
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict buffer OutBuf { float cov[]; };
layout(set = 0, binding = 1, std430) restrict buffer ParamsBuf {
    uint res;
    float seed;       // shifts the noise field
    float contrast;   // smoothstep tightening of the coverage edges
    float gain;       // FBM persistence
} P;

// Hash + value noise (matches the cheap hash style used elsewhere in the project).
float hash(vec2 p){ p += P.seed; return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p){
    vec2 i = floor(p); vec2 f = fract(p); f = f*f*(3.0-2.0*f);
    return mix(mix(hash(i),            hash(i+vec2(1,0)), f.x),
               mix(hash(i+vec2(0,1)),  hash(i+vec2(1,1)), f.x), f.y);
}

// Seamless FBM on a torus: tile coords -> angles -> sample a 4D-ish wrap via two
// (cos,sin) pairs flattened to 2D. Edges wrap because cos/sin are periodic.
float seamless_fbm(vec2 uv, float freq){
    float amp = 0.5, sum = 0.0, norm = 0.0;
    for (int o = 0; o < 4; o++){
        float f = freq * exp2(float(o));
        // wrap each axis independently over [0,1) at frequency f
        vec2 a = uv * 6.2831853 * f;
        vec2 w = vec2(cos(a.x) + sin(a.y), sin(a.x) + cos(a.y));
        sum  += amp * vnoise(w * 1.7 + float(o) * 19.3);
        norm += amp;
        amp  *= P.gain;
    }
    return sum / max(norm, 1e-5);
}

void main(){
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x >= P.res || id.y >= P.res) return;
    vec2 uv = (vec2(id) + 0.5) / float(P.res);     // [0,1), tiles seamlessly
    float n = seamless_fbm(uv, 3.0);
    // remap toward a patchy coverage look: center, stretch, contrast-curve.
    n = clamp((n - 0.5) * 1.8 + 0.5, 0.0, 1.0);
    n = smoothstep(0.5 - P.contrast, 0.5 + P.contrast, n);
    cov[id.y * P.res + id.x] = n;
}
```

- [ ] **Step 2: Commit**

```bash
git add shaders/cloud_coverage.glsl
git commit -m "Add cloud-coverage compute shader (seamless tiling FBM)"
```

(No standalone run yet — it's dispatched by Task 2. Compile is verified there, since the
shader only compiles inside a RenderingDevice.)

---

## Task 2: Bake dispatcher (`CloudCompute.cs`)

**Files:**
- Create: `scripts/lab/CloudCompute.cs`

Direct sibling of `scripts/lab/SplatCompute.cs` — same local-RD creation, `#[compute]` strip, SPIR-V compile (throw on error), pipeline create, dispatch, `Sync`, `BufferGetData`, free RIDs. Output is `res*res` floats → an `R`-format `ImageTexture` with **mipmaps generated** and repeat enabled (set on the Image, then the sampler in-shader declares `repeat_enable, filter_linear_mipmap`).

Note: Godot's `ImageTexture.CreateFromImage` honors `image.GenerateMipmaps()` called first. Repeat is a *sampler* property set in the shader uniform hint (Task 3), not on the texture — so here we only ensure mipmaps.

- [ ] **Step 1: Write the dispatcher**

```csharp
using Godot;
using System;

namespace WG16.Lab;

/// GPU-compute cloud-coverage bake: produces ONE seamless tiling grayscale cloud
/// texture (high-contrast FBM) once at load, read back to an ImageTexture the
/// terrain shader samples in light() for sun attenuation. Mirrors SplatCompute's
/// local-RD + readback pattern (one GPU->CPU copy, only on bake — never per frame).
/// One job: produce the cloud texture. Knows nothing about cameras/UI/lighting.
public sealed class CloudCompute : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader;
    private readonly Rid _pipeline;

    // Mirrors the std430 ParamsBuf in cloud_coverage.glsl (field-for-field).
    public struct Params
    {
        public uint Res;
        public float Seed;
        public float Contrast;
        public float Gain;
    }

    public CloudCompute()
    {
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string path = ProjectSettings.GlobalizePath("res://shaders/cloud_coverage.glsl");
        string src = System.IO.File.ReadAllText(path)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
        var source = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        RDShaderSpirV spirv = _rd.ShaderCompileSpirVFromSource(source, false);
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute))
        {
            throw new InvalidOperationException("cloud_coverage.glsl: " + spirv.CompileErrorCompute);
        }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "cloud_coverage");
        _pipeline = _rd.ComputePipelineCreate(_shader);
    }

    /// Bake the coverage texture at `res` (e.g. 512 — it is low-frequency and tiled,
    /// so it does not need field resolution). Returns an R-format ImageTexture with
    /// mipmaps. Safe to call repeatedly.
    public ImageTexture Bake(int res, Params p)
    {
        int cells = checked(res * res);
        p.Res = (uint)res;

        // coverage out: 1 float per cell (binding 0)
        Rid oBuf = _rd.StorageBufferCreate((uint)(cells * sizeof(float)));
        var oU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        oU.AddId(oBuf);

        // params (binding 1)
        byte[] pBytes = BuildParams(p);
        Rid pBuf = _rd.StorageBufferCreate((uint)pBytes.Length, pBytes);
        var pU = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
        pU.AddId(pBuf);

        Rid set = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { oU, pU }, _shader, 0);

        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        uint groups = (uint)((res + 7) / 8);
        _rd.ComputeListDispatch(list, groups, groups, 1);
        _rd.ComputeListEnd();
        _rd.Submit();
        _rd.Sync();

        byte[] outBytes = _rd.BufferGetData(oBuf);
        _rd.FreeRid(set);
        _rd.FreeRid(oBuf);
        _rd.FreeRid(pBuf);

        Image img = Image.CreateFromData(res, res, false, Image.Format.Rf, outBytes);
        img.GenerateMipmaps();   // mipmaps = no minification crawl in motion (banked lesson)
        return ImageTexture.CreateFromImage(img);
    }

    private static byte[] BuildParams(Params p)
    {
        // 4 fields, std430 scalar layout (all 4-byte) → pad to 16-byte multiple.
        var b = new byte[16];
        int o = 0;
        void U(uint v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        void F(float v) { BitConverter.GetBytes(v).CopyTo(b, o); o += 4; }
        U(p.Res); F(p.Seed); F(p.Contrast); F(p.Gain);
        return b;
    }

    public void Dispose()
    {
        _rd.FreeRid(_pipeline);
        _rd.FreeRid(_shader);
        _rd.Free();
    }
}
```

- [ ] **Step 2: Build**

Run (from project dir):
```
dotnet build WG16.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add scripts/lab/CloudCompute.cs
git commit -m "Add CloudCompute bake dispatcher (mirrors SplatCompute)"
```

---

## Task 3: Shader uniforms + custom `light()` (`terrain_lab.gdshader`)

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (replace the leftover cloud comment near line 127; replace the dead-comment block at the end of `fragment()` ~line 470-473; replace the trailing "no custom light()" comment ~line 487-488 with a real `light()`).

**Step 1: Add the cloud uniforms.** Insert after the existing `band_soft_mult` uniform block (~line 132), replacing the old removal note at ~line 127-128:

```glsl
// --- Cloud shadows (fresh 2026-06-17) ----------------------------------------
// A GPU-baked seamless cloud-coverage texture (CloudCompute), sampled in light()
// in WORLD space, drifting via TIME, domain-warped to break the tile lattice. It
// attenuates ONLY the direct sun term (diffuse+specular) — ambient/GI stay full,
// so shadows read lit-but-sunless and GI can't wash them out (the old failure).
// When cloud_enabled is false, light() reproduces Godot's default Lambert/Schlick.
uniform sampler2D cloud_tex : filter_linear_mipmap, repeat_enable, hint_default_white;
uniform bool cloud_enabled = true;
uniform float cloud_strength : hint_range(0.0, 1.0) = 0.6;   // how dark the shadow
uniform float cloud_coverage : hint_range(0.0, 1.0) = 0.5;   // how much sky is clouded
uniform float cloud_softness : hint_range(0.01, 0.5) = 0.15; // shadow edge width
uniform float cloud_scale_m : hint_range(200.0, 4000.0) = 1200.0; // patch size (m)
uniform float cloud_drift_speed : hint_range(0.0, 0.02) = 0.004;
uniform float cloud_drift_dir : hint_range(0.0, 360.0) = 45.0;    // degrees
uniform float cloud_warp : hint_range(0.0, 1.0) = 0.4;       // domain-warp amount
```

**Step 2: Replace the dead comment block** at the end of `fragment()` (the lines starting `// Cloud shadows are applied in light() below`, ~470-473) with a concise pointer:

```glsl
    // Cloud shadows attenuate the SUN in light() below (not albedo — albedo-darken
    // gets washed out by ambient+SDFGI). See the cloud uniforms above.
```

**Step 3: Replace the trailing comment** (`// (No custom light()...` ~line 487-488) with the real function:

```glsl
// Cloud factor in [0,1]: 1 = full sun, 0 = fully shadowed. World-space so shadows
// sit on the ground; TIME drift; domain warp breaks the tile lattice; mipmapped
// sample avoids motion crawl. coverage slides clouded fraction, softness the edge.
float cloud_sun_mul(vec3 wp){
    if (!cloud_enabled) return 1.0;
    float ang = radians(cloud_drift_dir);
    vec2 dir = vec2(cos(ang), sin(ang));
    vec2 uv = wp.xz / cloud_scale_m + TIME * cloud_drift_speed * dir;
    // domain warp: large-scale coverage read offsets the lookup
    float wsamp = texture(cloud_tex, uv * 0.35).r;
    uv += cloud_warp * (wsamp - 0.5);
    float c = texture(cloud_tex, uv).r;
    float shadow = smoothstep(cloud_coverage - cloud_softness,
                              cloud_coverage + cloud_softness, c);
    return 1.0 - cloud_strength * (1.0 - shadow);
}

// Custom light(): the SAME lighting body that was part of the user-approved
// "really good" look on backup/clouds-system-2026-06-16 (Lambert diffuse + GGX-ish
// spec, ATTENUATION carries the sun shadow map). ONLY the cloud factor source is
// fresh — the square came from the old noise, not from this lighting. With clouds
// off, cloud_sun_mul()==1.0, so the term reduces to that approved lighting exactly.
void light() {
    float cloud = cloud_sun_mul(v_world);   // v_world from vertex(); varyings valid in light()
    float ndotl = clamp(dot(NORMAL, LIGHT), 0.0, 1.0);
    DIFFUSE_LIGHT += ndotl * ATTENUATION * cloud * LIGHT_COLOR * ALBEDO;
    // cheap GGX-ish specular, also cloud- and shadow-attenuated
    vec3 H = normalize(VIEW + LIGHT);
    float ndoth = clamp(dot(NORMAL, H), 0.0, 1.0);
    float rough = max(ROUGHNESS, 0.04);
    float a = rough * rough;
    float spec = pow(ndoth, max(2.0 / (a * a) - 2.0, 1.0)) * (1.0 - rough);
    SPECULAR_LIGHT += spec * ndotl * ATTENUATION * cloud * LIGHT_COLOR * 0.25;
}
```

- [ ] **Step 4: Build + import to compile-check the shader**

```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; dotnet build WG16.csproj
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --headless --path . --import
```
Expected: build succeeds; import completes with no shader compile error printed for `terrain_lab.gdshader`.

- [ ] **Step 5: Commit**

```bash
git add shaders/terrain_lab.gdshader
git commit -m "Re-introduce custom light(): cloud-shadow sun attenuation (inert when off)"
```

---

## Task 4: Wire the bake + bind into `TerrainLab.cs`

**Files:**
- Modify: `scripts/lab/TerrainLab.cs`

Add a `CloudCompute` field, a `BakeClouds()` that binds `cloud_tex`, and a call at the end of `Build()` (after the material exists). The cloud knobs themselves flow through the existing `SetFloat`/`SetBool` (driven by the registry in Task 5) — no per-knob code here.

**Step 1: Add the field** next to `_splat` (~line 19):

```csharp
    private SplatCompute? _splat;
    private CloudCompute? _cloud;
    private const int CloudRes = 512;   // low-freq tiled texture; field-res is overkill
```

**Step 2: Add the bake method** (e.g. after `RebakeSplat()`):

```csharp
    /// Bake the seamless cloud-coverage texture once and bind it. Cheap (512²),
    /// called on build; re-derivable, seeded for variety.
    public void BakeClouds()
    {
        _cloud ??= new CloudCompute();
        var cp = new CloudCompute.Params { Seed = 11.0f, Contrast = 0.28f, Gain = 0.55f };
        var tex = _cloud.Bake(CloudRes, cp);
        _mat.SetShaderParameter("cloud_tex", tex);
        GD.Print("TerrainLab: cloud-coverage baked (512², seamless)");
    }
```

**Step 3: Call it at the end of `Build()`** — add inside the `if (Mesh == null)` block right after `MaterialOverride = _mat;` is NOT correct (mat may exist on rebuild); instead add after line 50 (`_mat.SetShaderParameter("texel_world", p.Spacing);`):

```csharp
        BakeClouds();
```

**Step 4: Dispose** — extend `_ExitTree` (line 118):

```csharp
    public override void _ExitTree() { _splat?.Dispose(); _cloud?.Dispose(); }
```

- [ ] **Step 5: Build**

```
dotnet build WG16.csproj
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add scripts/lab/TerrainLab.cs
git commit -m "Bake + bind cloud-coverage texture in TerrainLab.Build"
```

---

## Task 5: Clouds control group (`data/lab_controls.json`)

**Files:**
- Modify: `data/lab_controls.json` (add `"Clouds"` to the `tabs` array; add the control rows).

**Step 1: Add the tab.** Change the `tabs` line (line 4) to include Clouds before Debug:

```json
  "tabs": ["Zones", "Surface", "Color", "Detail", "Splat", "Light", "Clouds", "Debug"],
```

**Step 2: Add the control rows** inside the `controls` array (place after the Light-tab block, before the Debug controls — exact position is cosmetic; the `tab` field is what routes them):

```json
    { "id": "cloud_enabled", "label": "clouds", "tab": "Clouds", "type": "toggle",
      "param": "cloud_enabled", "default": true, "rand": false },
    { "id": "cloud_strength", "label": "strength", "tab": "Clouds", "type": "slider",
      "param": "cloud_strength", "min": 0, "max": 1, "default": 0.6, "rand": false },
    { "id": "cloud_coverage", "label": "coverage", "tab": "Clouds", "type": "slider",
      "param": "cloud_coverage", "min": 0, "max": 1, "default": 0.5, "rand": false },
    { "id": "cloud_softness", "label": "softness", "tab": "Clouds", "type": "slider",
      "param": "cloud_softness", "min": 0.01, "max": 0.5, "default": 0.15, "rand": false },
    { "id": "cloud_scale_m", "label": "scale m", "tab": "Clouds", "type": "slider",
      "param": "cloud_scale_m", "min": 200, "max": 4000, "default": 1200, "rand": false },
    { "id": "cloud_drift_speed", "label": "drift speed", "tab": "Clouds", "type": "slider",
      "param": "cloud_drift_speed", "min": 0, "max": 0.02, "default": 0.004, "rand": false },
    { "id": "cloud_drift_dir", "label": "drift dir", "tab": "Clouds", "type": "slider",
      "param": "cloud_drift_dir", "min": 0, "max": 360, "default": 45, "rand": false },
    { "id": "cloud_warp", "label": "warp", "tab": "Clouds", "type": "slider",
      "param": "cloud_warp", "min": 0, "max": 1, "default": 0.4, "rand": false },
```

- [ ] **Step 3: Validate JSON**

Run:
```
python -c "import json; json.load(open(r'data/lab_controls.json')); print('ok')"
```
Expected: `ok`

- [ ] **Step 4: Verify the UI builds the tab.** Confirm `TerrainLabUI.cs` builds tabs from the `tabs` array and routes controls by `tab`, and that `type:slider`+`param` / `type:toggle`+`param` push via `SetFloat`/`SetBool`. (Read it; if Clouds-tab controls would not be created or pushed, fix the registry rows to match the existing slider/toggle schema rather than changing C#.)

- [ ] **Step 5: Commit**

```bash
git add data/lab_controls.json
git commit -m "Add Clouds control tab + group to lab registry"
```

---

## Task 6: Headless render verification (A/B)

**Files:** none (verification only).

- [ ] **Step 1: Kill stray Godot, build, import.**

```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"C:\Users\josep\Downloads\...\Godot_..._console.exe" --headless --path . --import
```
Expected: clean build + import, no shader errors.

- [ ] **Step 2: Auto-shot with clouds ON (default).**

```
"C:\...\Godot_..._win64.exe" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- --auto-shot=C:/tmp/clouds_on.png
```
Expected: a PNG at `C:/tmp/clouds_on.png`; the print log shows "cloud-coverage baked"; no errors. Open it (Read tool) — terrain renders, no obvious square lattice in the still (note: motion is the real test, this only catches gross breakage).

- [ ] **Step 3: Confirm OFF == default.** This requires toggling `cloud_enabled` off. Since auto-shot can't toggle UI, verify by temporarily setting the uniform default to `false` is NOT needed — instead trust the `light()` early-return logic + confirm the ON shot is well-lit (not black/over-dark, which would indicate the `light()` rewrite changed base lighting). If the ON shot looks materially darker/different from a pre-change baseline shot, the `light()` default-equivalence is wrong — fix before handing to user.

- [ ] **Step 4: No commit** (verification only). Proceed to live judging.

---

## Task 7: Live judging handoff (THE gate)

**Files:** none.

- [ ] **Step 1: Kill stray processes, launch windowed for the user.**

```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"C:\...\Godot_..._win64.exe" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```

- [ ] **Step 2: Tell the user what to judge in MOTION:** drifting shadow patches across the terrain; NO squares / NO repeating lattice; shadowed ground reads lit-but-sunless (not flat-grey, not washed out); toggle `clouds` off in the Clouds tab to confirm it cleanly disappears (and the look returns to current). Have them dial strength/coverage/scale/warp/drift live.

- [ ] **Step 3: If the user approves:** add a DECISIONS.md entry, refresh HANDOFF §6 Current State, consider snapshotting cloud settings into mood presets (separate small follow-up). If the square/lattice returns: it is isolated to this system — judge live, adjust warp/scale/contrast or reconsider the source (per the 3-failed-fixes rule, do not patch indefinitely).

---

## Self-Review notes

- **Spec coverage:** coverage bake (T1), dispatcher mirroring SplatCompute (T2), custom light() attenuating sun only + inert when off (T3), wiring/bind (T4), Clouds control group + new tab, rand:false (T5), headless A/B (T6), live-motion gate (T7). All spec sections mapped.
- **Type consistency:** `CloudCompute.Params{Res,Seed,Contrast,Gain}` matches `BuildParams` order and the glsl `ParamsBuf`. `Bake(int res, Params p)` sets `p.Res` internally. `BakeClouds()`/`cloud_tex` names consistent across T3–T5. Uniform names in T3 exactly match `param` fields in T5.
- **Known watch-point (flagged, not a placeholder):** T5 Step 4 verifies the registry's slider/toggle schema actually routes a `param` push for a new tab; if `TerrainLabUI` hardcodes tab handling anywhere, adjust there. T6 Step 3 guards that the new `light()` didn't darken the base look.
