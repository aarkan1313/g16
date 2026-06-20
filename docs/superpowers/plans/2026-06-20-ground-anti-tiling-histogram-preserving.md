# AAA Anti-Tiling (histogram-preserving) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the blocky IQ 2-tap anti-tiling with Deliot–Heitz histogram-preserving tiling-and-blending (baked inverse-histogram LUTs, no-rotation triangle grid), behind a new `tile_mode=3`, so close-up ground stops seaming without reintroducing repetition.

**Architecture:** A pure-C# bake (`HistogramCompute`) builds per-material forward `T` + inverse `T⁻¹` LUTs from each albedo, packed into one small `tile_lut` atlas owned by `TerrainLab`. The fragment shader gains a `tile_mode==3` path: a shared no-rotation triangle-grid sampler taps 3 stochastic tiles; **albedo** blends in Gaussian space (forward LUT → variance blend → inverse LUT), **normal/roughness/AO** use the variance-preserving blend (no LUT). Default stays `tile_mode=1` (IQ) until the live eye-gate passes.

**Tech Stack:** Godot 4.6 mono (C# + GLSL fragment shader). No GPU compute (histogram is a CPU reduction). Verification is the project's GPU/visual loop, not unit tests.

## Global Constraints

- **Project dir / launch:** `C:\Wg16\wg-16-project`; always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`. ONE Godot at a time — `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` before each launch.
- **Godot exe:** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe` (`_console.exe` variant for headless `--import` / CLI prints).
- **Build:** `dotnet build WG16.csproj`. After a new `.cs`: build → headless `--import` (compile-check) before launching windowed.
- **No TDD — GPU/visual project.** "Tests" = build clean → headless `--import` → windowed `--auto-shot` A/B + `--profmove`, then the USER's live eye. The one genuine automatable check here is the LUT round-trip (Task 1), exposed as a `--histcheck` CLI print.
- **Discipline:** build to the eye-gate, not past it. Default `tile_mode` stays `1` (IQ); mode `3` is opt-in. Retiring the old IQ/hex paths happens only AFTER the gate passes (not in this plan).
- **Perf budget:** 8 ms in-motion whole-generator. Profile `--profmove` after the shader task.
- **Commit by default** to `experiment/presentation` (already HEAD). End commit messages with the Co-Authored-By line.
- **Lab registry gotcha:** a control `param` naming a missing uniform silently no-ops — verify ids render.

---

### Task 1: `HistogramCompute` — CPU histogram → forward/inverse LUTs, with a round-trip self-check

**Files:**
- Create: `scripts/lab/HistogramCompute.cs`
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (add a `--histcheck` branch that bakes one material's LUTs and prints round-trip error)

**Interfaces:**
- Produces: `WG16.Lab.HistogramCompute` with
  - `const int Bins = 256;` `const int RowsPerZone = 6;` (R/G/B forward, R/G/B inverse)
  - `static float[] ComputeLuts(Image albedo)` → returns `float[Bins * RowsPerZone]` laid out as rows `[0..2]=forward R/G/B`, `[3..5]=inverse R/G/B`, each row `Bins` wide, all values in `[0,1]`.
  - `static (float maxErr, float meanErr) RoundTripError(float[] luts)` → applies `T` then `T⁻¹` across a 0..1 ramp per channel; for verification only.

- [ ] **Step 1: Create `HistogramCompute.cs` with the histogram→CDF→Gaussian LUT math.**

```csharp
using Godot;
using System;

namespace WG16.Lab;

/// AAA anti-tiling (Deliot–Heitz). Builds, per albedo channel, a forward transform T
/// (value → Gaussian) and its inverse T⁻¹ (Gaussian → value) as 256-entry LUTs, so the
/// fragment shader can blend 3 stochastic tiles in Gaussian space and map back with the
/// original histogram preserved (no contrast loss, no seam). A histogram is a single linear
/// reduction → pure C# (no GPU compute, runs headless), one-time per bound material.
public static class HistogramCompute
{
    public const int Bins = 256;
    public const int RowsPerZone = 6;        // fwd R,G,B then inv R,G,B
    private const float GaussMean = 0.5f;
    private const float GaussStd  = 1.0f / 6.0f;   // ±3σ spans [0,1]

    /// Returns float[Bins*RowsPerZone]: rows 0..2 = forward (val→gauss) R/G/B,
    /// rows 3..5 = inverse (gauss→val) R/G/B. All in [0,1].
    public static float[] ComputeLuts(Image albedo)
    {
        Image img = (Image)albedo.Duplicate();
        if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
        // Stride-sample to ~512² for a fast, representative histogram.
        int target = 512;
        if (img.GetWidth() > target || img.GetHeight() > target) img.Resize(target, target, Image.Interpolation.Bilinear);
        byte[] px = img.GetData();
        int n = img.GetWidth() * img.GetHeight();

        var luts = new float[Bins * RowsPerZone];
        for (int c = 0; c < 3; c++)
        {
            // 1) histogram
            var hist = new int[Bins];
            for (int i = 0; i < n; i++) hist[px[i * 4 + c]]++;
            // 2) CDF (midpoint convention to avoid 0/1 saturation in the probit)
            var cdf = new float[Bins];
            int acc = 0;
            for (int b = 0; b < Bins; b++) { acc += hist[b]; cdf[b] = (acc - 0.5f * hist[b]) / Math.Max(n, 1); }
            // 3) forward LUT: value bin b → gaussian quantile of cdf[b]
            int fwdRow = c, invRow = 3 + c;
            for (int b = 0; b < Bins; b++)
                luts[fwdRow * Bins + b] = Mathf.Clamp(GaussMean + GaussStd * Probit(cdf[b]), 0f, 1f);
            // 4) inverse LUT: gaussian bin g (→ value v=g/255 in gaussian space) → value whose
            //    cdf matches Φ(gaussian). Search cdf for the matching uniform u = NormalCdf(gz).
            for (int g = 0; g < Bins; g++)
            {
                float gz = ((g / (float)(Bins - 1)) - GaussMean) / GaussStd;   // back to z
                float u  = NormalCdf(gz);                                       // target uniform
                luts[invRow * Bins + g] = InvertCdf(cdf, u);
            }
        }
        return luts;
    }

    // value v in [0,1] such that cdf(v) ≈ u, by linear search+lerp over the 256-bin cdf.
    private static float InvertCdf(float[] cdf, float u)
    {
        if (u <= cdf[0]) return 0f;
        for (int b = 1; b < Bins; b++)
            if (u <= cdf[b])
            {
                float t = (u - cdf[b - 1]) / Math.Max(cdf[b] - cdf[b - 1], 1e-6f);
                return Mathf.Clamp((b - 1 + t) / (Bins - 1), 0f, 1f);
            }
        return 1f;
    }

    // Acklam's inverse-normal-CDF (probit) approximation. Input p in (0,1) → z.
    private static float Probit(float p)
    {
        p = Mathf.Clamp(p, 1e-6f, 1f - 1e-6f);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] cc= { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        double plow = 0.02425, phigh = 1 - 0.02425, q, r, z;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); z = (((((cc[0]*q+cc[1])*q+cc[2])*q+cc[3])*q+cc[4])*q+cc[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        else if (p <= phigh) { q = p - 0.5; r = q*q; z = (((((a[0]*r+a[1])*r+a[2])*r+a[3])*r+a[4])*r+a[5])*q / (((((b[0]*r+b[1])*r+b[2])*r+b[3])*r+b[4])*r+1); }
        else { q = Math.Sqrt(-2 * Math.Log(1 - p)); z = -(((((cc[0]*q+cc[1])*q+cc[2])*q+cc[3])*q+cc[4])*q+cc[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        return (float)z;
    }

    // standard normal CDF via erf approximation (Abramowitz & Stegun 7.1.26).
    private static float NormalCdf(float z)
    {
        float sign = z < 0 ? -1f : 1f; z = Math.Abs(z) / 1.41421356f;
        float t = 1f / (1f + 0.3275911f * z);
        float y = 1f - (((((1.061405429f*t - 1.453152027f)*t) + 1.421413741f)*t - 0.284496736f)*t + 0.254829592f)*t*(float)Math.Exp(-z*z);
        return 0.5f * (1f + sign * y);
    }

    public static (float maxErr, float meanErr) RoundTripError(float[] luts)
    {
        float maxe = 0f, sum = 0f; int cnt = 0;
        for (int c = 0; c < 3; c++)
            for (int b = 0; b < Bins; b++)
            {
                float v = b / (float)(Bins - 1);
                float g = luts[c * Bins + b];                                  // forward
                int gi = Mathf.Clamp((int)MathF.Round(g * (Bins - 1)), 0, Bins - 1);
                float v2 = luts[(3 + c) * Bins + gi];                          // inverse
                float e = MathF.Abs(v2 - v); maxe = MathF.Max(maxe, e); sum += e; cnt++;
            }
        return (maxe, sum / cnt);
    }
}
```

- [ ] **Step 2: Add a `--histcheck` CLI branch in `TerrainLabUI.Cli.cs`.**

Find where other `--` flags are parsed (e.g. `--auto-shot`). Add, mirroring that style, a branch that bakes one known material's albedo and prints the round-trip error, then quits:

```csharp
// --histcheck : bake one material's LUTs and print T⁻¹(T(v)) round-trip error, then quit.
if (arg.StartsWith("--histcheck"))
{
    string mat = arg.Contains("=") ? arg.Split('=')[1] : "13_sun_baked_clay";
    string p = ProjectSettings.GlobalizePath($"res://assets/materials/{mat}/albedo.png");
    if (!System.IO.File.Exists(p)) { GD.Print($"[histcheck] no albedo for {mat}"); GetTree().Quit(); return; }
    var img = Image.LoadFromFile(p);
    var luts = HistogramCompute.ComputeLuts(img);
    var (maxe, meane) = HistogramCompute.RoundTripError(luts);
    GD.Print($"[histcheck] {mat}: roundtrip maxErr={maxe*255f:F2}/255 meanErr={meane*255f:F2}/255  -> {(maxe < 4f/255f ? \"PASS\" : \"FAIL\")}");
    GetTree().Quit();
    return;
}
```

(If `TerrainLabUI.Cli.cs` parses args in a loop with a different shape, match it — the point is one branch that runs `ComputeLuts` + `RoundTripError` and prints PASS/FAIL.)

- [ ] **Step 3: Build.**

Run: `dotnet build WG16.csproj`
Expected: `0 Error(s)`.

- [ ] **Step 4: Run the round-trip check (the real test).**

Run (one Godot only; console exe for stdout):
`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe; "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --histcheck=13_sun_baked_clay`
Expected: a line `[histcheck] 13_sun_baked_clay: roundtrip maxErr=…/255 meanErr=…/255 -> PASS` (maxErr < 4/255). If FAIL, the probit/CDF math is wrong — fix before proceeding (do NOT build the shader on a broken bake).

- [ ] **Step 5: Commit.**

```bash
git add scripts/lab/HistogramCompute.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "Anti-tiling T1: HistogramCompute CPU LUT bake + --histcheck round-trip"
```

---

### Task 2: `TerrainLab` owns the `tile_lut` atlas — bake on material-set, bind once

**Files:**
- Modify: `scripts/lab/TerrainLab.cs` (add atlas field + `BakeZoneHistogram`, call it from `SetZoneMaterial`, bind `tile_lut`)

**Interfaces:**
- Consumes: `HistogramCompute.ComputeLuts(Image)` → `float[256*6]`; `HistogramCompute.Bins`, `RowsPerZone`.
- Produces: shader uniform `tile_lut` (an `ImageTexture`, `Rf`, 256 × (7*6)=42), always bound (identity until a zone bakes). Layout: zone `z` occupies rows `z*6 .. z*6+5` (fwd R/G/B, inv R/G/B).

- [ ] **Step 1: Add the atlas field + identity initializer to `TerrainLab.cs`.**

Near the other private fields (e.g. by `private HeightCompute? _height;`):

```csharp
private const int LutRows = 7 * HistogramCompute.RowsPerZone;   // 42
private float[] _lutData = new float[HistogramCompute.Bins * LutRows];
private ImageTexture? _lutTex;
```

Add a helper that (re)builds the atlas texture from `_lutData` and binds it:

```csharp
private void PushLutAtlas()
{
    var bytes = new byte[_lutData.Length * sizeof(float)];
    Buffer.BlockCopy(_lutData, 0, bytes, 0, bytes.Length);
    var img = Image.CreateFromData(HistogramCompute.Bins, LutRows, false, Image.Format.Rf, bytes);
    if (_lutTex == null) _lutTex = ImageTexture.CreateFromImage(img);
    else _lutTex.Update(img);
    _mat.SetShaderParameter("tile_lut", _lutTex);
}
```

Initialize `_lutData` to an **identity** transform so the shader's mode-3 path is valid even before any bake (forward = value, inverse = value): in the constructor or `Ready` path where `_mat` is first set up, fill identity then `PushLutAtlas()`:

```csharp
for (int z = 0; z < 7; z++)
  for (int c = 0; c < 3; c++)
    for (int b = 0; b < HistogramCompute.Bins; b++)
    {
        float v = b / (float)(HistogramCompute.Bins - 1);
        _lutData[(z*HistogramCompute.RowsPerZone + c)     *HistogramCompute.Bins + b] = v;  // fwd identity
        _lutData[(z*HistogramCompute.RowsPerZone + 3 + c) *HistogramCompute.Bins + b] = v;  // inv identity
    }
PushLutAtlas();
```

(Place this where `heightmap`/`region_size` are first set — search `SetShaderParameter("region_size"` and add after that block, since `_mat` exists there.)

- [ ] **Step 2: Add `BakeZoneHistogram` and call it from `SetZoneMaterial`.**

In `SetZoneMaterial`, after the `z{zone}_alb` bind, add the call:

```csharp
        _mat.SetShaderParameter($"z{zone}_ao", LoadOr(b, "ao"));
        BakeZoneHeight(zone, materialName);
        BakeZoneHistogram(zone, materialName);   // <-- add
```

Add the method (near `BakeZoneHeight`):

```csharp
/// AAA anti-tiling: bake this material's albedo histogram LUTs into the shared tile_lut atlas.
private void BakeZoneHistogram(int zone, string materialName)
{
    string p = ProjectSettings.GlobalizePath($"res://assets/materials/{materialName}/albedo.png");
    if (!System.IO.File.Exists(p)) return;            // leave identity rows (mode-3 == plain for this zone)
    Image alb = Image.LoadFromFile(p);
    if (alb == null) return;
    float[] luts = HistogramCompute.ComputeLuts(alb);
    int baseRow = zone * HistogramCompute.RowsPerZone;
    Buffer.BlockCopy(luts, 0, _lutData, baseRow * HistogramCompute.Bins * sizeof(float), luts.Length * sizeof(float));
    PushLutAtlas();
}
```

Ensure `using System;` is present (for `Buffer`). 

- [ ] **Step 3: Build + headless import (compile-check).**

Run: `dotnet build WG16.csproj` → `0 Error(s)`, then
`"…Godot…_console.exe" --headless --path /c/Wg16/wg-16-project --import` → no C# errors.

- [ ] **Step 4: Smoke-launch (atlas binds, nothing breaks at default tile_mode=1).**

Run windowed (kill strays first):
`"…Godot….exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --auto-shot=c:/tmp/t2_atlas.png`
Expected: PNG saved, scene renders normally (tile_lut is bound but unused at mode 1 — binding a not-yet-referenced uniform is harmless). No errors in output.

- [ ] **Step 5: Commit.**

```bash
git add scripts/lab/TerrainLab.cs
git commit -m "Anti-tiling T2: TerrainLab owns tile_lut atlas, bakes histogram per zone-material"
```

---

### Task 3: Shader `tile_mode==3` — no-rotation triangle grid; albedo via LUT, normal/rough/ao variance-preserving

**Files:**
- Modify: `shaders/terrain_lab.gdshader` (add `tile_lut` uniform + LUT helpers + triangle-grid blend; thread `zone` into `s_alb`; mode-3 branches in `s_alb`/`s_nrm`/`s_rgh`/`s_ao`)

**Interfaces:**
- Consumes: `tile_lut` (bound by Task 2); existing `tri_grid`, `hash2v`, `tri_w`, `TRI_EPS`, `tex_scale_m`, `distanceWeight`, `tile_mode`.
- Produces: `tile_mode==3` rendering for all four maps. No change to mode 0/1/2 or to `tiled()`.

- [ ] **Step 1: Add the `tile_lut` uniform + LUT sample helpers.**

Just after the `splat_weights7`/sampler declarations area (anywhere in the global uniform block, e.g. near `uniform int tile_mode`), add:

```glsl
uniform sampler2D tile_lut : filter_linear, repeat_disable;   // 256 × 42 Rf: per-zone fwd/inv histogram LUTs
const float LUT_ROWS = 42.0;   // 7 zones × 6

// forward T: original channel value -> gaussian space, per zone+channel(0,1,2)
float lut_fwd(int zone, int ch, float v){
    float row = float(zone*6 + ch) + 0.5;
    return texture(tile_lut, vec2(clamp(v,0.0,1.0), row/LUT_ROWS)).r;
}
// inverse T⁻¹: gaussian value -> original, per zone+channel
float lut_inv(int zone, int ch, float g){
    float row = float(zone*6 + 3 + ch) + 0.5;
    return texture(tile_lut, vec2(clamp(g,0.0,1.0), row/LUT_ROWS)).r;
}
```

- [ ] **Step 2: Add a shared no-rotation triangle-grid tap (3 samples + weights).**

Add near `ar_sample_wp` (it reuses `tri_grid`, but with NO `load_rot`):

```glsl
// 3 stochastic tiles on the triangle grid, random TRANSLATION only (no rotation = no seam).
// Returns the 3 taps + the variance-sharpened weights; caller blends (in gaussian or plain space).
void tri_taps(sampler2D tex, vec2 uv, out vec4 t1, out vec4 t2, out vec4 t3, out vec3 w){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    float w1,w2,w3; vec2 v1,v2,v3;
    tri_grid(uv, w1,w2,w3, v1,v2,v3);
    t1 = textureGrad(tex, uv + hash2v(v1), dx, dy);
    t2 = textureGrad(tex, uv + hash2v(v2), dx, dy);
    t3 = textureGrad(tex, uv + hash2v(v3), dx, dy);
    w = vec3(w1,w2,w3); w *= w; w /= max(w.x+w.y+w.z, 1e-5);   // sharpen
}
// variance-preserving blend of 3 taps (keeps contrast). mean kept, deviation scaled by 1/sqrt(Σw²).
vec4 vp_blend(vec4 t1, vec4 t2, vec4 t3, vec3 w){
    vec4 mean = (t1+t2+t3)/3.0;
    vec4 b = t1*w.x + t2*w.y + t3*w.z;
    float vc = inversesqrt(max(dot(w,w),1e-5));
    return mean + (b-mean)*vc;
}
```

- [ ] **Step 3: Histogram albedo sampler (mode-3) + thread `zone` into `s_alb`.**

Add an albedo-specific histogram sampler and a single-plane helper. Then change `s_alb` to take a zone and branch on `tile_mode==3`:

```glsl
// histogram-preserving single-plane albedo tap at uv for a given zone.
vec3 histo_alb_plane(sampler2D tex, vec2 uv, vec3 wp, int zone){
    vec4 t1,t2,t3; vec3 w; tri_taps(tex, uv, t1,t2,t3, w);
    // to gaussian space per channel, variance blend, back via inverse LUT
    vec3 g1 = vec3(lut_fwd(zone,0,t1.r), lut_fwd(zone,1,t1.g), lut_fwd(zone,2,t1.b));
    vec3 g2 = vec3(lut_fwd(zone,0,t2.r), lut_fwd(zone,1,t2.g), lut_fwd(zone,2,t2.b));
    vec3 g3 = vec3(lut_fwd(zone,0,t3.r), lut_fwd(zone,1,t3.g), lut_fwd(zone,2,t3.b));
    vec3 gm = (g1+g2+g3)/3.0;
    vec3 gb = g1*w.x + g2*w.y + g3*w.z;
    float vc = inversesqrt(max(dot(w,w),1e-5));
    vec3 g = gm + (gb-gm)*vc;
    vec3 outc = vec3(lut_inv(zone,0,g.r), lut_inv(zone,1,g.g), lut_inv(zone,2,g.b));
    // distance LOD: cross-fade to a single plain tap far (where 3-tap is wasted + mips diverge)
    float far = distanceWeight(wp);
    vec3 plain = textureGrad(tex, uv, dFdx(uv), dFdy(uv)).rgb;
    return mix(outc, plain, smoothstep(0.7, 0.95, far));
}
```

Change `s_alb` signature to accept the zone and branch:

```glsl
vec3 s_alb(sampler2D t, vec3 wp, vec3 nr, int zone){
    vec3 bw=tri_w(nr);
    vec2 ux=wp.zy/tex_scale_m, uy=wp.xz/tex_scale_m, uz=wp.xy/tex_scale_m;
    if (tile_mode == 3){
        vec3 base = vec3(0.0);
        if(bw.x>TRI_EPS) base += histo_alb_plane(t, ux, wp, zone)*bw.x;
        if(bw.y>TRI_EPS) base += histo_alb_plane(t, uy, wp, zone)*bw.y;
        if(bw.z>TRI_EPS) base += histo_alb_plane(t, uz, wp, zone)*bw.z;
        return base;
    }
    vec3 base = tiled(t,ux).rgb*bw.x + tiled(t,uy).rgb*bw.y + tiled(t,uz).rgb*bw.z;
    if (tile_mode == 0 && blend_mode >= 2) {
        vec3 big = texture(t,ux/antitile_mult).rgb*bw.x + texture(t,uy/antitile_mult).rgb*bw.y + texture(t,uz/antitile_mult).rgb*bw.z;
        base = mix(base, big, 0.5);
    }
    return base;
}
```

Update the callers `trip_alb_by(int z, …)` to pass `z` into `s_alb(…, z)`. (Search `s_alb(` — it is called from `trip_alb_by`; each `s_alb(zX_alb, wp, nr)` becomes `s_alb(zX_alb, wp, nr, X)`.)

- [ ] **Step 4: Mode-3 for normal / roughness / AO (variance-preserving, no LUT).**

Add a single-plane variance sampler and branch `s_nrm`/`s_rgh`/`s_ao` on `tile_mode==3`:

```glsl
vec4 vp_plane(sampler2D tex, vec2 uv, vec3 wp){
    vec4 t1,t2,t3; vec3 w; tri_taps(tex, uv, t1,t2,t3, w);
    vec4 vp = vp_blend(t1,t2,t3,w);
    float far = distanceWeight(wp);
    vec4 plain = textureGrad(tex, uv, dFdx(uv), dFdy(uv));
    return mix(vp, plain, smoothstep(0.7, 0.95, far));
}
```

In `s_nrm`: `if (tile_mode==3) return vp_plane(t,wp.zy/tex_scale_m,wp).rgb*bw.x + vp_plane(t,wp.xz/tex_scale_m,wp).rgb*bw.y + vp_plane(t,wp.xy/tex_scale_m,wp).rgb*bw.z;` before the existing `tiled()` return. Same shape in `s_rgh` (use `.r`). `s_ao` already uses `ar_sample_wp`; leave it (variance-preserving already) — or, for consistency, branch it to `vp_plane` under mode 3 (optional, low value; skip to keep the diff small).

- [ ] **Step 5: Build + headless import (GLSL compiles via the material).**

Run: `dotnet build WG16.csproj` → `0 Error(s)`; then headless `--import` → no shader-compile errors printed. (A GLSL error shows as a shader-compile message on import/first-load.)

- [ ] **Step 6: A/B auto-shots — IQ (mode 1) vs histogram (mode 3).**

The review scene starts at default `tile_mode=1`. Capture both via the Surface `tile mode` control once Task 4 wires the enum; for now verify mode 3 renders by temporarily setting `tile_mode` default to 3 OR using `--tilemode=3` if a CLI exists. Minimal check here: windowed launch, fly close, confirm **no blocky 28 m seams** and **materials keep contrast** vs the IQ shot. Save `c:/tmp/t3_iq.png` and `c:/tmp/t3_histo.png`.

- [ ] **Step 7: Commit.**

```bash
git add shaders/terrain_lab.gdshader
git commit -m "Anti-tiling T3: tile_mode=3 histogram-preserving sampling (albedo LUT + variance normal/rough/ao)"
```

---

### Task 4: Wire the `tile mode` control + perf check — ready for the eye-gate

**Files:**
- Modify: `data/lab_controls.json` (Surface `tile mode` enum: add option `"3 histogram"`)

**Interfaces:**
- Consumes: shader `tile_mode` uniform (already routed by the registry via `param: "tile_mode"`).
- Produces: a user-facing `tile mode` dropdown option that switches IQ ↔ histogram live.

- [ ] **Step 1: Add the enum option.**

Find the `tile_mode` control (Surface tab) in `data/lab_controls.json`:

```json
{ "id": "tile_mode", "label": "tile mode", "tab": "Surface", "type": "enum",
  "param": "tile_mode", "options": ["none (sharp)", "IQ 2-tap", "hex-tiling"], "default": 1 }
```

Change `options` to add the 4th entry (keep `default: 1` — IQ stays default until the gate passes):

```json
  "options": ["none (sharp)", "IQ 2-tap", "hex-tiling", "histogram"], "default": 1
```

- [ ] **Step 2: Build (JSON only, but confirm no parse break) + headless import.**

Run: `dotnet build WG16.csproj` → `0 Error(s)`; headless `--import` → no warning `unknown control id` / no JSON parse error in output.

- [ ] **Step 3: Live A/B + perf — the real check.**

Launch windowed:
`"…Godot….exe" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`
- Press `3` (GM1 baseline). On the **Surface** tab, switch `tile mode` `IQ 2-tap` → `histogram`. Fly close/mid on a uniform slope and a cliff. Confirm: blocky seams gone, no obvious repetition, contrast preserved, no motion shimmer; `none (sharp)` shows raw repetition for reference.
- Profile both modes in motion: add `--profmove` to a separate launch; record IQ vs histogram ms. Confirm histogram stays within the 8 ms whole-frame budget (note the delta).

- [ ] **Step 4: Commit.**

```bash
git add data/lab_controls.json
git commit -m "Anti-tiling T4: expose tile_mode=3 (histogram) in the Surface tile-mode control"
```

- [ ] **Step 5: Hand to the live eye-gate.**

Drive `scenes/review.tscn` with the user (key 3, Surface → `tile mode` IQ ↔ histogram, close/mid/far on slope + cliff). Record the verdict in `NEEDS_REVIEW.md` §1c + a `DECISIONS.md` line. **On PASS:** flip `tile_mode` default → 3, then (separate cleanup, per the spec) retire the IQ/hex/legacy paths and re-judge the GM1/2/3-A batch. **On FAIL/tweak:** iterate `tex_scale_m`, the LOD band, or per-material fallback before re-gating.

---

## Self-Review

**1. Spec coverage:**
- Technique (bake + runtime) → Tasks 1 (bake) + 3 (runtime). ✓
- Component 1 `HistogramCompute` pure-C# → Task 1. ✓
- Component 2 unified seam (albedo LUT / normal-rough-ao variance) → Task 3 Steps 3–4. ✓
- Component 3 `tile_mode=3` toggle, default IQ → Task 4 (default 1 kept). ✓
- LUT atlas, one sampler, owned by TerrainLab → Task 2. ✓
- Distance LOD cross-fade to plain → Task 3 Steps 3–4 (`smoothstep(0.7,0.95,far)`). ✓
- Branched triplanar preserved → Task 3 `bw>TRI_EPS` guards. ✓
- Perf gate `--profmove` → Task 4 Step 3. ✓
- Bake round-trip risk → Task 1 Step 4 (`--histcheck`). ✓
- Sampler-count risk (+1) → Task 2 (single `tile_lut`). ✓
- Gate + retire-old-paths-on-pass → Task 4 Step 5 (retire deferred, per discipline). ✓

**2. Placeholder scan:** No "TBD/TODO". Code shown for every code step. The one soft spot — exact arg-parse shape in `TerrainLabUI.Cli.cs` (Task 1 Step 2) and exact `_mat`-init location (Task 2 Step 1) — are called out with the search string to anchor them, not left vague.

**3. Type consistency:** `HistogramCompute.ComputeLuts(Image)->float[Bins*RowsPerZone]`, `Bins=256`, `RowsPerZone=6` used identically in Tasks 1/2. Atlas `256×42 Rf`, row `zone*6 + (ch | 3+ch)` consistent between `PushLutAtlas`/`BakeZoneHistogram` (C#) and `lut_fwd`/`lut_inv` (GLSL: `row = zone*6 + ch` / `+3+ch`, `/LUT_ROWS` with `LUT_ROWS=42`). `s_alb` gains the `int zone` param in both its definition and the `trip_alb_by` callers (Task 3 Step 3). ✓
