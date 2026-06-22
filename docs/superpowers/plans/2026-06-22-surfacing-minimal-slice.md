# Minimal Surfacing Slice (readable landforms) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Texture the terrain with ~5 real materials placed per-pixel by height+slope (soft boundaries, no grid blockiness), triplanar on cliffs, real normal maps + mipmaps, so landforms read as 3D shape in motion — just enough to judge erosion. NOT the full texture-array arc.

**Architecture:** Edit the EXISTING `shaders/ground.gdshader` fragment stage to replace the height/slope COLOUR ramp with a textured material blend driven by the existing `v_h` (height) and `v_normal` (slope) varyings. Materials are bound as fixed `sampler2D` uniforms from `assets/materials/` by a thin C# loader in `TerrainLab.cs`. Per fragment: compute a height-band + slope-band weight per material with `fwidth`-soft boundaries, sample each (world-XZ tiled, mipmapped, triplanar on steep faces, real normal map), blend the contributors by normalized weight, feed the result to the existing `light()` BRDF.

**Tech Stack:** Godot 4.6.2 mono (C#), spatial `.gdshader`, `dotnet build`, windowed `--auto-shot` capture (the app cannot run headless). Verification is visual + the existing numeric guards, NOT unit tests.

## Global Constraints

- **Bones untouched (skin-not-bones):** do NOT edit `shaders/field_math.gdshaderinc`, `shaders/field_height.glsl`, or the field math in `ground.gdshader`'s `analytic_h`/`ground_fieldp`. `--fieldcheck` must stay `maxAbsDiff=0m` after every task.
- **C# stale-DLL gotcha:** run `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after EVERY `.cs` edit before launching. Shaders hot-compile, but the player runs the stale DLL otherwise.
- **Shader cache gotcha:** the player's compiled-shader cache is `C:/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache` (NOT the project `.godot/shader_cache`). If a shader edit appears to have no effect, delete that folder before relaunching.
- **Run invocation:** windowed only; user `--flags` go AFTER a bare `--`; `--auto-shot=` needs an OS path (`C:/tmp/wg16shots/x.png`); kill any running Godot first (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` + `..._console.exe`); absolute `--path /c/Wg16/wg-16-project`. Launch CDLOD with `-- --cdlod=1`.
- **Godot exe (console build, for stdout):** `/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`.
- **Diff captures with `--clouds=0`** (clouds animate during the 1.5s auto-shot warmup and pollute frame diffs). Use `--nofog` for a clean raw-terrain look.
- **Pillars / gate:** readable landforms (slope/depth legible) in motion is the bar — NOT AAA. The user's live eye is the only look-gate; never claim the look from a downscaled still. On eye-gate PASS: STOP, do not push to AAA, move to erosion.
- **Material count:** ~5 materials max (soft cap; keeps sampler count + fetches sane).

---

## File Structure

- **Modify `shaders/ground.gdshader`** — add material `sampler2D` uniforms + tunable threshold/scale uniforms; replace the `fragment()` colour-ramp body with a textured height/slope placement+blend; add a triplanar helper + a normal-combine helper. The `vertex()` stage is unchanged (it already outputs `v_h`, `v_normal`, and world XZ is reconstructable). Keep the old colour-ramp behind a `use_textures` uniform (default off) so it's a clean A/B and the default look is unchanged until the eye-gate flips it.
- **Modify `scripts/lab/TerrainLab.cs`** — add a `LoadGroundMaterials()` helper that loads ~5 material sets from `assets/materials/` and pushes their albedo/normal/roughness textures + the per-material params + the `use_textures` toggle to `_mat`. Call it from `Build()`.
- **Modify `scripts/lab/TerrainLabUI.Cli.cs`** — add `--textures[=0|1]` to flip `use_textures` at startup for A/B captures (mirrors the existing `--analytic` pattern).
- **(reference, do not edit)** `assets/materials/<name>/{albedo,normal,roughness}.png` — the on-disk PBR library (1024², mipmaps already enabled on import).

The slice is small enough to be one shader + one loader; tasks are split where a reviewer could reject one independently (binding works → placement reads → triplanar/normals read → tuning/flip).

---

## Reference: world-XZ in the fragment shader

`ground.gdshader`'s `vertex()` already computes the true sampled world XZ as `wxz` (chunk branch) / `VERTEX.xz` (analytic branch) and stores height in `v_h` and the world normal in `v_normal`. **Add a `varying vec2 v_surf_xz;`** set to that same world XZ in BOTH branches, so `fragment()` can tile textures in world space (stable across LOD + floating-origin, since it's true-world). This is the one new varying needed.

---

### Task 1: Bind materials + world-XZ varying (textured path shows a single material)

**Files:**
- Modify: `shaders/ground.gdshader` (uniforms near line 20-27; `vertex()` 105-186; `fragment()` 188-206)
- Modify: `scripts/lab/TerrainLab.cs` (`Build()` ~line 23-46; add `LoadGroundMaterials()`)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (CLI parse ~line 142; field ~line 256)

**Interfaces:**
- Produces (shader uniforms, consumed by later tasks): `uniform bool use_textures`, `uniform sampler2D mat0_alb..mat4_alb`, `mat0_nrm..mat4_nrm`, `mat0_rgh..mat4_rgh`, `uniform float mat_tiling = 12.0`, and `varying vec2 v_surf_xz`.
- Produces (C#): `TerrainLab.LoadGroundMaterials()`; `TerrainLab.SetTexturesOn(bool)`.

- [ ] **Step 1: Add the world-XZ varying + material uniforms to the shader**

In `shaders/ground.gdshader`, after the existing `varying` block (after `varying vec2 v_wxz;`) add:
```glsl
varying vec2 v_surf_xz;   // true-world XZ for world-space texture tiling (set in BOTH vertex branches)
```
After the `instance uniform float lod_viz` line, add the textured-path uniforms:
```glsl
// --- Minimal surfacing slice (readable landforms). Fixed-sampler textured path, A/B vs the colour ramp. ---
uniform bool use_textures = false;   // false = legacy height/slope colour ramp (default); true = textured
uniform float mat_tiling = 12.0;     // world metres per texture tile (smaller = more repeats)
uniform sampler2D mat0_alb : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mat0_nrm : filter_linear_mipmap, repeat_enable, hint_normal;
uniform sampler2D mat0_rgh : filter_linear_mipmap, repeat_enable;
uniform sampler2D mat1_alb : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mat1_nrm : filter_linear_mipmap, repeat_enable, hint_normal;
uniform sampler2D mat1_rgh : filter_linear_mipmap, repeat_enable;
uniform sampler2D mat2_alb : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mat2_nrm : filter_linear_mipmap, repeat_enable, hint_normal;
uniform sampler2D mat2_rgh : filter_linear_mipmap, repeat_enable;
uniform sampler2D mat3_alb : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mat3_nrm : filter_linear_mipmap, repeat_enable, hint_normal;
uniform sampler2D mat3_rgh : filter_linear_mipmap, repeat_enable;
uniform sampler2D mat4_alb : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D mat4_nrm : filter_linear_mipmap, repeat_enable, hint_normal;
uniform sampler2D mat4_rgh : filter_linear_mipmap, repeat_enable;
```

- [ ] **Step 2: Set `v_surf_xz` in both vertex branches**

In `vertex()`, the chunk branch (`use_chunk > 0.5`): after `v_wxz = wxz;` add:
```glsl
        v_surf_xz = wxz;
```
In the analytic branch (`else if (use_analytic)`): after `v_h = VERTEX.y;` add:
```glsl
        v_surf_xz = wxz;
```
In the baked branch (final `else`): after `v_h = VERTEX.y;` add:
```glsl
        v_surf_xz = (VERTEX.xz);
```

- [ ] **Step 3: Add a textured early-path to `fragment()` (single material for now)**

In `fragment()`, at the very top (before the existing `vec3 c = mix(col_low...`), add:
```glsl
    if (use_textures) {
        vec2 uv = v_surf_xz / mat_tiling;
        vec3 alb = texture(mat0_alb, uv).rgb;   // single material this task; placement comes in Task 2
        ALBEDO = alb;
        ROUGHNESS = texture(mat0_rgh, uv).r;
        NORMAL = v_normal;   // geometric normal; real normal-map combine comes in Task 3
        return;
    }
```
(Leaving the legacy ramp below untouched as the `use_textures=false` default.)

- [ ] **Step 4: Add the C# material loader**

In `scripts/lab/TerrainLab.cs`, add this method to the class:
```csharp
    // Minimal surfacing slice: bind ~5 fixed material sets from assets/materials by role. Fluid/disposable
    // (the full surfacing arc replaces this with texture arrays); the placement/blend LOGIC in the shader is
    // the kept work. Roles: 0 low/sand, 1 valley grass, 2 mid earth/scree, 3 slope rock, 4 peak snow.
    private static readonly string[] _matRoles =
    {
        "03_coarse_sand", "01_tussock_grass", "02_muddy_with_stones",
        "01_weathered_grey_bedrock", "01_fresh_powder",
    };
    public void LoadGroundMaterials()
    {
        for (int i = 0; i < _matRoles.Length; i++)
        {
            string b = $"res://assets/materials/{_matRoles[i]}/";
            var alb = GD.Load<Texture2D>(b + "albedo.png");
            var nrm = GD.Load<Texture2D>(b + "normal.png");
            var rgh = GD.Load<Texture2D>(b + "roughness.png");
            if (alb != null) { _mat.SetShaderParameter($"mat{i}_alb", alb); }
            if (nrm != null) { _mat.SetShaderParameter($"mat{i}_nrm", nrm); }
            if (rgh != null) { _mat.SetShaderParameter($"mat{i}_rgh", rgh); }
        }
        GD.Print($"TerrainLab: ground materials loaded ({_matRoles.Length} roles)");
    }
    public void SetTexturesOn(bool on) { _mat.SetShaderParameter("use_textures", on); }
```
Then in `Build()`, after the last `_mat.SetShaderParameter("fp_...", ...)` call, add:
```csharp
        LoadGroundMaterials();
```

- [ ] **Step 5: Add the `--textures` CLI flag**

In `scripts/lab/TerrainLabUI.Cli.cs`, in `ParseCli()` near the other `--analytic` parse, add:
```csharp
            else if (a.StartsWith("--textures")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _texturesCli = (s == "1") ? 1 : 0; }
```
Add the field near `_analyticCli`:
```csharp
    private int _texturesCli = -1;   // --textures[=0|1] → minimal surfacing slice on/off at startup
```
In `ApplyCliOverrides()`, near `if (_analyticCli >= 0)`, add:
```csharp
        if (_texturesCli >= 0) { _terrain.SetTexturesOn(_texturesCli == 1); }
```

- [ ] **Step 6: Build, verify field guard, capture A/B**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
```
Expected: `0 Error(s)`.
Then:
```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --auto-shot=C:/tmp/wg16shots/surf_field.png 2>&1 | grep FIELDCHECK
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --nofog --clouds=0 --textures=1 --cam=-30,900,3000,-12,90 --auto-shot=C:/tmp/wg16shots/surf_t1.png 2>&1 | grep -i "materials loaded\|auto-shot"
```
Expected: `FIELDCHECK: PASS maxAbsDiff=0m`; "ground materials loaded (5 roles)"; a shot written. Open `surf_t1.png` with the Read tool — terrain should now be covered in the single sand texture (tiled), NOT the colour ramp. (Tiling/repetition is fine at this stage.)

- [ ] **Step 7: Commit**

```bash
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "surfacing slice T1: bind 5 materials + world-XZ varying; single-texture path behind --textures"
```

---

### Task 2: Height + slope placement (the right material in the right place, soft boundaries)

**Files:**
- Modify: `shaders/ground.gdshader` (`fragment()` textured branch from Task 1; add uniforms)

**Interfaces:**
- Consumes: Task 1's `use_textures`, `mat{0..4}_*`, `mat_tiling`, `v_surf_xz`; existing `v_h`, `v_normal`.
- Produces: `uniform` band thresholds (listed below) used by Task 3-4; a `mat_weights()` placement returning per-material weights.

- [ ] **Step 1: Add placement threshold uniforms**

In `shaders/ground.gdshader`, after `uniform float mat_tiling`, add:
```glsl
// placement bands (metres for height, normalized 0..1 slope = 1 - normal.y). Soft via fwidth.
uniform float surf_h_sand  = 30.0;    // below → sand/low (mat0)
uniform float surf_h_grass = 220.0;   // sand→grass band top (mat1 valley)
uniform float surf_h_earth = 430.0;   // grass→earth band top (mat2 mid)
uniform float surf_h_rock  = 560.0;   // earth→rock; above → snow (mat4) by height
uniform float surf_slope_rock = 0.42; // slope above this forces rock (mat3) regardless of height
uniform float surf_slope_soft = 0.10; // half-width of the slope transition
```

- [ ] **Step 2: Replace the single-texture body with weighted placement + blend**

In `fragment()`, replace the Task-1 `if (use_textures) { ... return; }` block with:
```glsl
    if (use_textures) {
        vec2 uv = v_surf_xz / mat_tiling;
        float h = v_h;
        float slope = 1.0 - clamp(v_normal.y, 0.0, 1.0);   // 0 flat .. 1 vertical

        // height-band weights with fwidth-soft edges (per-pixel boundary → no grid snap).
        float fw = max(fwidth(h), 1.0);
        float w_sand  = 1.0 - smoothstep(surf_h_sand - fw,  surf_h_sand + fw,  h);
        float w_grass = smoothstep(surf_h_sand - fw, surf_h_sand + fw, h) * (1.0 - smoothstep(surf_h_grass - fw, surf_h_grass + fw, h));
        float w_earth = smoothstep(surf_h_grass - fw, surf_h_grass + fw, h) * (1.0 - smoothstep(surf_h_earth - fw, surf_h_earth + fw, h));
        float w_snow  = smoothstep(surf_h_earth - fw, surf_h_earth + fw, h);

        // slope forces rock (mat3): blends OUT the height materials on steep faces.
        float rock = smoothstep(surf_slope_rock - surf_slope_soft, surf_slope_rock + surf_slope_soft, slope);
        float wr = 1.0 - rock;
        float w0 = w_sand * wr, w1 = w_grass * wr, w2 = w_earth * wr, w4 = w_snow * wr;
        float w3 = rock;
        float sum = max(w0 + w1 + w2 + w3 + w4, 1e-4);

        vec3 alb = (w0 * texture(mat0_alb, uv).rgb + w1 * texture(mat1_alb, uv).rgb
                  + w2 * texture(mat2_alb, uv).rgb + w3 * texture(mat3_alb, uv).rgb
                  + w4 * texture(mat4_alb, uv).rgb) / sum;
        float rgh = (w0 * texture(mat0_rgh, uv).r + w1 * texture(mat1_rgh, uv).r
                  + w2 * texture(mat2_rgh, uv).r + w3 * texture(mat3_rgh, uv).r
                  + w4 * texture(mat4_rgh, uv).r) / sum;
        ALBEDO = alb;
        ROUGHNESS = clamp(rgh, 0.04, 1.0);
        NORMAL = v_normal;   // real normal-map combine in Task 3
        return;
    }
```

- [ ] **Step 3: Build (shaders hot-compile; no C# changed, but rebuild is safe) + capture**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
rm -rf "/c/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --nofog --clouds=0 --textures=1 --cam=-30,900,3000,-12,90 --auto-shot=C:/tmp/wg16shots/surf_t2.png 2>&1 | grep -i auto-shot
```
Open `surf_t2.png`: low ground reads sand, valleys grass, mid earth, steep faces rock, peaks snow; boundaries are soft (no hard grid facets). Tiling repetition still visible (fine).

- [ ] **Step 4: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "surfacing slice T2: per-pixel height+slope material placement with fwidth-soft boundaries"
```

---

### Task 3: Real normal maps + triplanar on cliffs (the 3D read)

**Files:**
- Modify: `shaders/ground.gdshader` (add helpers above `fragment()`; use them in the textured branch)

**Interfaces:**
- Consumes: Task 2's weights + uniforms, `mat{i}_nrm`, `v_normal`, `v_surf_xz`.
- Produces: `vec3 tri_sample(sampler2D, vec3 wpos, vec3 wn, float scale)` and a tangent-free normal combine.

- [ ] **Step 1: Add a triplanar sampler + world-space normal-perturb helper**

Above `void fragment()`, add:
```glsl
// Triplanar albedo/scalar sample by world position (no UV needed) — kills cliff stretching on steep faces.
vec3 tri_sample(sampler2D t, vec3 wpos, vec3 wn, float scale) {
    vec3 bw = pow(abs(wn), vec3(4.0));
    bw /= max(bw.x + bw.y + bw.z, 1e-4);
    vec3 cx = texture(t, wpos.zy / scale).rgb;
    vec3 cy = texture(t, wpos.xz / scale).rgb;
    vec3 cz = texture(t, wpos.xy / scale).rgb;
    return cx * bw.x + cy * bw.y + cz * bw.z;
}
// Perturb a world normal by a tangent-space normal map sampled on the dominant (XZ) plane. Cheap, tangent-free
// (good enough for the legibility bar): rotate the map's xy into world XZ, keep geometric normal as the base.
vec3 apply_nrm(vec3 geo_n, sampler2D nmap, vec2 uv, float strength) {
    vec3 n = texture(nmap, uv).xyz * 2.0 - 1.0;
    vec3 perturbed = normalize(geo_n + vec3(n.x, 0.0, n.y) * strength);
    return perturbed;
}
```

- [ ] **Step 2: Use triplanar for the rock material and combine normal maps**

In the textured branch of `fragment()`, replace the `alb`/`rgh`/`NORMAL` lines with:
```glsl
        vec3 wpos = vec3(v_surf_xz.x, v_h, v_surf_xz.y);
        // rock (mat3) uses triplanar (steep faces); others use planar world-XZ uv (cheaper, they're flat-ish).
        vec3 a0 = texture(mat0_alb, uv).rgb, a1 = texture(mat1_alb, uv).rgb, a2 = texture(mat2_alb, uv).rgb;
        vec3 a3 = tri_sample(mat3_alb, wpos, v_normal, mat_tiling), a4 = texture(mat4_alb, uv).rgb;
        vec3 alb = (w0*a0 + w1*a1 + w2*a2 + w3*a3 + w4*a4) / sum;
        float rgh = (w0*texture(mat0_rgh,uv).r + w1*texture(mat1_rgh,uv).r + w2*texture(mat2_rgh,uv).r
                   + w3*texture(mat3_rgh,uv).r + w4*texture(mat4_rgh,uv).r) / sum;
        // dominant material's normal map perturbs the geometric normal (cheap; legibility, not exactness).
        vec3 nrm = v_normal;
        nrm = apply_nrm(nrm, mat1_nrm, uv, (w1 + w2) / sum);   // grass/earth meso relief
        nrm = apply_nrm(nrm, mat0_nrm, uv, w0 / sum);          // sand ripples
        ALBEDO = alb;
        ROUGHNESS = clamp(rgh, 0.04, 1.0);
        NORMAL = normalize(nrm);
        return;
```
(Delete the now-duplicated `alb`/`rgh`/`ALBEDO`/`ROUGHNESS`/`NORMAL`/`return` lines from Task 2's block so the branch ends here.)

- [ ] **Step 3: Verify normal maps import as linear (the sRGB gotcha)**

```bash
grep -L "compress/normal_map\|flags/srgb=2" assets/materials/01_tussock_grass/normal.png.import
```
If the normal `.import` has `flags/srgb` other than `2` (disabled) OR lacks a normal-map role, the `hint_normal` sampler hint in-shader still treats the data correctly for our cheap combine, so this is informational. No action needed unless the shot shows inverted/flat lighting on slopes — then set the normal pngs' import "Normal Map" = Enabled in the editor and reimport.

- [ ] **Step 4: Build + capture close + mid (the 3D read is judged at close range)**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
rm -rf "/c/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --nofog --clouds=0 --textures=1 --cam=-30,250,800,-18,90 --auto-shot=C:/tmp/wg16shots/surf_t3_close.png 2>&1 | grep -i auto-shot
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --nofog --clouds=0 --textures=1 --cam=-30,900,3000,-12,90 --auto-shot=C:/tmp/wg16shots/surf_t3_mid.png 2>&1 | grep -i auto-shot
```
Open both: cliffs are no longer stretched (triplanar); surface has meso-relief from normal maps; valleys/ridges read as 3D shape. No mip fuzz in motion-scale detail.

- [ ] **Step 5: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "surfacing slice T3: triplanar on cliffs + normal-map perturb — landforms read 3D"
```

---

### Task 4: Tune thresholds live, eye-gate, decide default

**Files:**
- Modify: `scripts/lab/TerrainLab.cs` (optional: push default threshold values if the shader defaults need overriding)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (optional CLI overrides for tiling/thresholds if dialing needs them)

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: the final tuned look + the default-on decision.

- [ ] **Step 1: Run all guards to confirm no regression**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --clouds=0 --fieldcheck --cdlodcheck --morphcheck --stitchcheck --streamcheck --popcheck --snapdiff --auto-shot=C:/tmp/wg16shots/surf_guards.png 2>&1 | grep -iE "CHECK:|SNAPDIFF:"
```
Expected: all PASS, `FIELDCHECK maxAbsDiff=0m`.

- [ ] **Step 2: Profile the textured path against budget**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --textures=1 --profmove --profile=8 2>&1 | grep PROFILE
```
Note the avg/worst. If the textured branch blew the budget, reduce to a top-2-by-weight blend (sort w0..w4, sample only the two highest) — but only if needed; record the number either way.

- [ ] **Step 3: USER EYE-GATE (hand to the user — windowed, live)**

Launch for the user:
```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --textures=1
```
Ask the user to fly close/mid/far and confirm: **landforms read (valleys/ridges/slopes legible as 3D); materials sensibly placed; no grid facets; no fuzz; cliffs clean.** If they want thresholds/tiling/materials changed, adjust `surf_*` / `mat_tiling` defaults in the shader (or `_matRoles` in `TerrainLab.cs`) and re-capture/re-launch. Iterate until the user says it reads well enough to judge erosion.

- [ ] **Step 4: Flip default-on (once eye-gate PASSES)**

In `shaders/ground.gdshader`, change `uniform bool use_textures = false;` → `= true;` so the textured path is the default look. (The legacy ramp stays in the shader as `--textures=0` fallback.)

- [ ] **Step 5: Build, final capture, commit**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --nofog --clouds=0 --cam=-30,900,3000,-12,90 --auto-shot=C:/tmp/wg16shots/surf_final.png 2>&1 | grep -i auto-shot
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs
git commit -m "surfacing slice T4: tuned thresholds + default-on textured ground (eye-gate passed)"
```

- [ ] **Step 6: Update docs**

In `docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md` add a one-line note under the deferred/surfacing area: minimal surfacing slice DONE (readable landforms; full texture-array arc still pending). Commit:
```bash
git add docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md
git commit -m "docs: minimal surfacing slice done (readable landforms; full arc still pending)"
```

---

## Self-Review

**Spec coverage:**
- ~5 materials by role → Task 1 (`_matRoles`). ✓
- Per-pixel height+slope placement, no grid blockiness → Task 2 (`fwidth`-soft bands). ✓
- Mipmaps → Task 1 (`filter_linear_mipmap` + import already `mipmaps/generate=true`). ✓
- Triplanar on steep → Task 3 (`tri_sample` for rock). ✓
- Real normal maps (3D read) → Task 3 (`apply_nrm`). ✓
- Blend by weight, into existing lighting → Task 2/3 (weighted sum → ALBEDO/NORMAL/ROUGHNESS → light()). ✓
- Live-tunable, A/B, default current look until flip → `use_textures` (default false), `--textures`, Task 4 flip. ✓
- Bones untouched / `--fieldcheck` 0m → Global Constraints + verified Task 1 Step 6, Task 4 Step 1. ✓
- Perf budget → Task 4 Step 2 (`--profmove`), top-2 fallback noted. ✓
- Eye-gate then STOP → Task 4 Step 3. ✓
- OUT (arrays, manifest, placement engine, POM, biomes, lighting re-tune) → none of the tasks build these. ✓

**Placeholder scan:** no TBD/TODO; every shader + C# step shows the actual code. Material role names are concrete (verified to exist on disk). The one "if needed" (top-2 fallback, normal sRGB reimport) is a conditional contingency with a concrete trigger + action, not a deferred requirement.

**Type/name consistency:** `use_textures`, `mat{i}_alb/_nrm/_rgh`, `mat_tiling`, `v_surf_xz`, `surf_h_*`, `surf_slope_*`, `tri_sample`, `apply_nrm`, `LoadGroundMaterials`, `SetTexturesOn`, `_texturesCli` — used consistently across tasks.
