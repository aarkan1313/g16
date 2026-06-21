# Ground Material-Rendering Core (Units 1–4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new, per-pixel-procedural ground material shader (`shaders/ground.gdshader`) + its C# texture-array backing, **alongside** the old `terrain_lab.gdshader`, behind a toggle defaulting to the current look — so the 4 m blockiness is gone *by construction* and the ground can be judged at parity before the old path is retired.

**Architecture:** Materials are packed into `sampler2DArray`s (albedo / normal / ORM / height). The new shader computes material **placement per fragment** from world-space terrain fields (height/slope/curvature/aspect + warped noise) → top-4 materials → samples them through the re-hosted histogram anti-tiling → height-aware blends them with `fwidth` AA. **No baked splat field anywhere.** The old shader/material stays the default; a runtime toggle swaps the terrain mesh's material to the new one for A/B.

**Tech Stack:** Godot 4.6.2 mono (C# + `.gdshader` spatial shaders), `Texture2DArray`, the existing local-RD compute pattern (for the Poisson height bake), the existing data-registry lab UI.

**Spec:** `docs/superpowers/specs/2026-06-21-ground-material-system-reset-design.md` (read it first).

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = best-long-term. Perf budget **8 ms in-motion** (`--profmove`).
- **GUARDRAIL — skin not bones, no big-bang teardown.** Do NOT touch `field_height.glsl` / `FieldCompute` (the geometry). Do NOT delete `terrain_lab.gdshader` or any old path in this plan — it stays the **default** until the new core PASSES the user's live parity eye-gate (a later, separate step). The new shader is **additive**.
- **Re-host, don't re-derive** the two keepers: the histogram anti-tiling (`terrain_lab.gdshader` `histo_sample_wp` + its LUTs, ~lines 322–366) and the custom BRDF (`terrain_lab.gdshader` `light()`, ~lines 1015–1064). Copy them with their source line refs noted; do not reinvent.
- **No magic numbers in code** — every tunable goes in `data/ground_materials.json` or a lab control (project rule).
- **Verification model (NOT pytest — project convention "no TDD; GPU/visual"):** each task verifies via, in order as applicable: (a) `dotnet build WG16.csproj` clean; (b) headless `--import`; (c) a **mechanical CLI self-check** that reads back GPU/data state, prints a PASS/FAIL line, and quits (the `--histcheck`/`--shadowcheck` pattern); (d) `--auto-shot=<png>` visual sanity; (e) the **user's live eye** for look (the only look-gate). Commit after each task.
- **One Godot at a time.** Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`.
- **Godot binary:** windowed `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`; headless `..._console.exe`.

---

## File Structure (decomposition locked here)

- **Create `shaders/ground.gdshader`** — the new spatial shader. Owns: placement, sampling+anti-tiling, blend, BRDF. The whole new skin in one file (kept focused; ~400–600 lines target, well under the old 1064).
- **Create `scripts/lab/GroundMaterialArrays.cs`** — C# unit. Owns: loading the manifest, packing the library into the 4 `Texture2DArray`s (albedo/normal/orm/height), deriving height via the Poisson bake. Pure builder; no scene/UI knowledge. Interface: `Build(manifest) → GroundArrays { Texture2DArray Albedo, Normal, Orm, Height; int Count; RuleData[] Rules }`.
- **Create `data/ground_materials.json`** — the material manifest + per-material placement rules + params. Data-driven, biome-ready.
- **Modify `scripts/lab/TerrainLab.cs`** — add `SetGroundV2(bool)` to swap the terrain mesh material old↔new, and `AttachGroundArrays(GroundArrays)` to bind the arrays + rule UBO to the new material. (TerrainLab already owns the mesh + `_mat`.)
- **Modify `scripts/lab/TerrainLabUI.Cli.cs` + `TerrainLabUI.cs`** — add `--groundarraycheck` (Unit 1 self-check) and `--groundv2[=1]` (swap to the new shader at startup for auto-shots).
- **Modify `scripts/lab/TerrainLabUI.Review.cs`** — wire review **key 4** (the old GM2 slot, now superseded) to the **ground-v2 parity A/B** (press 4 to toggle old↔new).
- **Modify `data/lab_controls.json`** — add a Debug-tab `ground v2 (new core)` toggle + a `gv2 debug` enum (off / placement-viz).

No file does two units' jobs; the shader is the one place the per-pixel pipeline lives (it must, to share derivatives), but each pipeline stage is its own function with a clean signature.

---

## Task 0: Scaffold the new shader + the old↔new toggle (parity baseline)

Goal: a new `ground.gdshader` that renders the terrain (single flat-tinted material for now) and a toggle that swaps the mesh material old↔new. Establishes the A/B harness before any real material work, so every later task is judged against the unchanged old default.

**Files:**
- Create: `shaders/ground.gdshader`
- Modify: `scripts/lab/TerrainLab.cs` (add `SetGroundV2`)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (`_groundV2Cli` + parse), `scripts/lab/TerrainLabUI.cs` (apply at startup), `data/lab_controls.json` (Debug toggle)

**Interfaces:**
- Produces: `TerrainLab.SetGroundV2(bool on)` — swaps `MeshInstance3D` material between `_mat` (old) and `_groundV2Mat` (new); `TerrainLab.GroundV2Material` getter (for later array binding).

- [ ] **Step 1: Write the minimal new shader**

Create `shaders/ground.gdshader`:
```glsl
shader_type spatial;
// GROUND v2 — per-pixel procedural material skin (2026-06-21 reset). Built ALONGSIDE
// terrain_lab.gdshader; swapped in via TerrainLab.SetGroundV2 for A/B. Units land here in order:
// 0 scaffold · 1 arrays · 2 placement · 3 sampling+anti-tile · 4 blend. See the plan.
render_mode world_vertex_coords;

uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

varying vec3 v_world;
varying float v_h;       // height (m)
varying float v_slope;   // 0 flat .. 1 vertical
varying float v_curv;    // local curvature (concave<0, convex>0), meters

void vertex() {
    // The mesh is already displaced by the presenter; read world pos straight off VERTEX
    // (world_vertex_coords). Slope/curv from the interpolated normal + neighbor sampling is
    // unavailable here cheaply, so derive slope from NORMAL and pass height through.
    v_world = VERTEX;
    v_h = VERTEX.y;
    v_slope = 1.0 - NORMAL.y;
    v_curv = 0.0; // filled in Task 2 from a screen-space proxy
}

void fragment() {
    // Task 0: flat tint so the swap is visible but unmistakably "new path". Real material in Tasks 2-4.
    ALBEDO = vec3(0.45, 0.40, 0.35);
    ROUGHNESS = 0.9;
}
```
Note: the old shader computes `v_slope`/`v_curv` in its vertex from neighbor height taps (`terrain_lab.gdshader:446-456`); Task 2 ports that exact derivation. Task 0 keeps it minimal.

- [ ] **Step 2: Add the material swap to TerrainLab.cs**

In `scripts/lab/TerrainLab.cs`, near where `_mat` (the old `ShaderMaterial`) is created, add a field + method. Find the `MeshInstance3D` the terrain renders on (it's `this` or `_mesh` — match the existing code). Add:
```csharp
private ShaderMaterial? _groundV2Mat;
public ShaderMaterial GroundV2Material => _groundV2Mat ??= new ShaderMaterial {
    Shader = GD.Load<Shader>("res://shaders/ground.gdshader")
};
private bool _groundV2On;
/// Swap the terrain mesh material between the old (terrain_lab) and new (ground v2) skin for A/B.
public void SetGroundV2(bool on) {
    _groundV2On = on;
    MaterialOverride = on ? GroundV2Material : _mat;   // adjust target if the material lives on a child mesh
    GroundV2Material.SetShaderParameter("region_size", _params.RegionSizeM);
    GroundV2Material.SetShaderParameter("texel_world", _params.RegionSizeM / Mathf.Max(1, _params.HeightmapRes - 1));
}
```
(If the terrain material is assigned to a child `MeshInstance3D` rather than `MaterialOverride` on `this`, set it on that node — read the existing `Build()` to see where `_mat` is assigned and mirror it.)

- [ ] **Step 3: Add the CLI + Debug toggle wiring**

In `scripts/lab/TerrainLabUI.Cli.cs` add a field by the others: `private int _groundV2Cli = -1;` and a parse line in `ParseCli` (next to `--review=`): `else if (a.StartsWith("--groundv2")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _groundV2Cli = (s == "1") ? 1 : 0; }`.

In `scripts/lab/TerrainLabUI.cs`, in `ApplyCliOverrides()` (or near the `_reviewCli` apply), add: `if (_groundV2Cli >= 0) { _terrain.SetGroundV2(_groundV2Cli == 1); }`.

In `data/ground_materials.json`... (created in Task 1). For now add the Debug toggle to `data/lab_controls.json` (in the Debug tab block): `{ "id": "ground_v2", "label": "ground v2 (new core)", "tab": "Debug", "type": "toggle", "setter": "groundv2", "default": false, "rand": false },` and add a case to `TerrainLabUI.Apply.cs` `SetTerrainBoolField` is for `field`; for a `setter`-toggle you need a path. Simplest: make it `"field": "GroundV2"` and add to `SetTerrainBoolField`: `else if (field == "GroundV2") { _terrain.SetGroundV2(v); }`.

- [ ] **Step 4: Build + import + verify the swap (mechanical + visual)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
```
Expected: `Build succeeded. 0 Error(s)`.
```bash
"<godot_console>" --headless --path /c/Wg16/wg-16-project --import
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --cam=0,140,260,-30,0 --auto-shot=C:/tmp/t0_old.png
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --groundv2=1 --cam=0,140,260,-30,0 --auto-shot=C:/tmp/t0_new.png
```
Expected: `t0_old.png` = the current terrain look; `t0_new.png` = flat grey-brown terrain (the new path renders, shape intact). Confirms the swap works and default is unchanged.

- [ ] **Step 5: Commit**

```bash
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs data/lab_controls.json
git commit -m "ground-v2(0): scaffold new shader + old<->new material toggle (default old)"
```

---

## Task 1: Unit 1 — material data model + texture arrays (C#)

Goal: pack the library materials into four `Texture2DArray`s (albedo, normal, ORM, height) from a manifest, deriving real height via the Poisson normal→height bake; expose a rule table. Verified by a mechanical CLI self-check.

**Files:**
- Create: `scripts/lab/GroundMaterialArrays.cs`
- Create: `data/ground_materials.json`
- Modify: `scripts/lab/TerrainLab.cs` (`AttachGroundArrays`), `scripts/lab/TerrainLabUI.Cli.cs` + `.cs` (`--groundarraycheck`)

**Interfaces:**
- Consumes: the material library at `assets/materials/<name>/{albedo,normal,roughness,ao}.png`; `HeightCompute` (re-host the Poisson bake — `scripts/lab/HeightCompute.cs`, the existing `height_from_normal.glsl`).
- Produces:
  ```csharp
  public struct RuleData { public float HMin, HMax, SlopeMin, SlopeMax, Curv, NoiseScaleM, NoiseGain, HeightAmp; }
  public sealed class GroundArrays {
      public Texture2DArray Albedo, Normal, Orm, Height;
      public int Count;
      public RuleData[] Rules;          // one per material slot
      public string[] Names;
  }
  public static class GroundMaterialArrays { public static GroundArrays Build(string manifestPath); }
  ```

- [ ] **Step 1: Write the manifest**

Create `data/ground_materials.json` — start with the current alpine set (7) so v2 has real materials to place. Rules are height/slope bands mirroring `zone_weights` (read `terrain_lab.gdshader:459-508` for the current band values: `h_valley=100, h_slope=350, h_high=700, h_peak=950`, `slope_cliff_lo=0.30, slope_cliff_hi=0.55`):
```json
{
  "_comment": "Ground v2 material manifest. Each entry packs into the texture arrays at its index. Rules drive per-pixel placement (height m / slope 0-1 / curvature). Biome = a manifest. tex_res = common array tiling resolution.",
  "tex_res": 1024,
  "tex_scale_m": 11.0,
  "materials": [
    { "name": "13_sun_baked_clay",                 "hmin": 0,   "hmax": 220, "slopemin": 0.0, "slopemax": 0.35, "height_amp": 1.0 },
    { "name": "01_fine_sand",                       "hmin": 120, "hmax": 420, "slopemin": 0.0, "slopemax": 0.40, "height_amp": 1.0 },
    { "name": "13_dry_loose_scree",                 "hmin": 300, "hmax": 760, "slopemin": 0.2, "slopemax": 0.65, "height_amp": 1.1 },
    { "name": "02_coarse_talus",                    "hmin": 500, "hmax": 900, "slopemin": 0.3, "slopemax": 0.75, "height_amp": 1.2 },
    { "name": "01_columnar_basalt_face",            "hmin": 400, "hmax": 1100,"slopemin": 0.5, "slopemax": 1.0,  "height_amp": 1.4 },
    { "name": "04_arctic_rock_with_orange_lichen",  "hmin": 700, "hmax": 1300,"slopemin": 0.2, "slopemax": 0.8,  "height_amp": 1.2 },
    { "name": "01_fresh_powder",                    "hmin": 900, "hmax": 3000,"slopemin": 0.0, "slopemax": 0.5,  "height_amp": 0.8 }
  ]
}
```

- [ ] **Step 2: Write GroundMaterialArrays.cs**

```csharp
using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

public struct RuleData { public float HMin, HMax, SlopeMin, SlopeMax, HeightAmp; }

public sealed class GroundArrays {
    public Texture2DArray Albedo = null!, Normal = null!, Orm = null!, Height = null!;
    public int Count;
    public RuleData[] Rules = System.Array.Empty<RuleData>();
    public string[] Names = System.Array.Empty<string>();
    public int TexRes = 1024;
    public float TexScaleM = 11f;
}

/// Builds the four ground material texture arrays from data/ground_materials.json.
/// Pure builder: no scene/UI. Mirrors the local-RD readback pattern used by the rest of the lab.
public static class GroundMaterialArrays {
    public static GroundArrays Build(string manifestPath) {
        var g = new GroundArrays();
        string abs = ProjectSettings.GlobalizePath(manifestPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        var root = doc.RootElement;
        g.TexRes = root.TryGetProperty("tex_res", out var tr) ? tr.GetInt32() : 1024;
        g.TexScaleM = root.TryGetProperty("tex_scale_m", out var ts) ? ts.GetSingle() : 11f;
        var mats = root.GetProperty("materials");
        g.Count = mats.GetArrayLength();

        var alb = new Godot.Collections.Array<Image>();
        var nrm = new Godot.Collections.Array<Image>();
        var orm = new Godot.Collections.Array<Image>();
        var hgt = new Godot.Collections.Array<Image>();
        var rules = new List<RuleData>();
        var names = new List<string>();

        foreach (var m in mats.EnumerateArray()) {
            string name = m.GetProperty("name").GetString() ?? "";
            names.Add(name);
            rules.Add(new RuleData {
                HMin = F(m, "hmin", 0), HMax = F(m, "hmax", 3000),
                SlopeMin = F(m, "slopemin", 0), SlopeMax = F(m, "slopemax", 1),
                HeightAmp = F(m, "height_amp", 1f),
            });
            string dir = $"res://assets/materials/{name}/";
            alb.Add(LoadResized(dir + "albedo.png",    g.TexRes, Image.Format.Rgba8, fallbackGrey: 0.5f));
            nrm.Add(LoadResized(dir + "normal.png",     g.TexRes, Image.Format.Rgba8, fallbackGrey: -1f, isNormal: true));
            orm.Add(BuildOrm(dir, g.TexRes));
            hgt.Add(BuildHeight(dir + "normal.png", g.TexRes));
        }
        foreach (var img in alb) img.GenerateMipmaps();
        foreach (var img in nrm) img.GenerateMipmaps();
        foreach (var img in orm) img.GenerateMipmaps();
        foreach (var img in hgt) img.GenerateMipmaps();

        g.Albedo = new Texture2DArray(); g.Albedo.CreateFromImages(alb);
        g.Normal = new Texture2DArray(); g.Normal.CreateFromImages(nrm);
        g.Orm    = new Texture2DArray(); g.Orm.CreateFromImages(orm);
        g.Height = new Texture2DArray(); g.Height.CreateFromImages(hgt);
        g.Rules = rules.ToArray(); g.Names = names.ToArray();
        return g;
    }

    private static float F(JsonElement e, string k, float d) => e.TryGetProperty(k, out var v) ? v.GetSingle() : d;

    private static Image LoadResized(string resPath, int res, Image.Format fmt, float fallbackGrey, bool isNormal = false) {
        string p = ProjectSettings.GlobalizePath(resPath);
        Image img = System.IO.File.Exists(p) ? Image.LoadFromFile(p) : null;
        if (img == null) {
            img = Image.CreateEmpty(res, res, true, fmt);
            img.Fill(isNormal ? new Color(0.5f, 0.5f, 1f) : new Color(fallbackGrey, fallbackGrey, fallbackGrey));
            return img;
        }
        if (img.GetFormat() != fmt) img.Convert(fmt);
        if (img.GetWidth() != res || img.GetHeight() != res) img.Resize(res, res, Image.Interpolation.Lanczos);
        return img;
    }

    // ORM: R=ambient occlusion, G=roughness, B=metallic(0). Combines the library's ao.png + roughness.png.
    private static Image BuildOrm(string dir, int res) {
        var ao   = LoadResized(dir + "ao.png",        res, Image.Format.Rgba8, 1f);
        var rgh  = LoadResized(dir + "roughness.png", res, Image.Format.Rgba8, 0.85f);
        var outI = Image.CreateEmpty(res, res, true, Image.Format.Rgba8);
        for (int y = 0; y < res; y++) for (int x = 0; x < res; x++) {
            float a = ao.GetPixel(x, y).R, r = rgh.GetPixel(x, y).R;
            outI.SetPixel(x, y, new Color(a, r, 0f, 1f));
        }
        return outI;
    }

    // HEIGHT: re-host the proven Poisson normal->height bake (HeightCompute / height_from_normal.glsl).
    // Returns an R8 (in RGBA8) image; uses HeightCompute if available, else a roughness-luma proxy fallback.
    private static Image BuildHeight(string normalPath, int res) {
        string p = ProjectSettings.GlobalizePath(normalPath);
        if (System.IO.File.Exists(p)) {
            var n = Image.LoadFromFile(p);
            // HeightCompute.BakeHeightFromNormal(Image normal) -> Image (R height), normalized 0..1.
            // Re-use the existing windowed local-RD bake; if running headless it returns null -> proxy below.
            var h = HeightCompute.BakeHeightFromNormalOrNull(n, res);
            if (h != null) return h;
        }
        var fallback = Image.CreateEmpty(res, res, true, Image.Format.Rgba8);
        fallback.Fill(new Color(0.5f, 0.5f, 0.5f));   // flat height proxy when no bake (headless / missing)
        return fallback;
    }
}
```
Note on `HeightCompute`: it currently bakes per *zone* via local-RD (windowed only). Add a thin static `BakeHeightFromNormalOrNull(Image normal, int res)` to `HeightCompute.cs` that wraps its existing Poisson dispatch for a single image and returns `null` under headless (no local RD) — mirror the `--headless` null-RD guard the project documents. If that wrapper is non-trivial, the proxy fallback keeps Task 1 unblocked; real height lands when run windowed.

- [ ] **Step 3: Bind arrays in TerrainLab + add the self-check**

In `scripts/lab/TerrainLab.cs` add:
```csharp
private GroundArrays? _gArrays;
public void AttachGroundArrays(GroundArrays g) {
    _gArrays = g;
    var mat = GroundV2Material;
    mat.SetShaderParameter("albedo_arr", g.Albedo);
    mat.SetShaderParameter("normal_arr", g.Normal);
    mat.SetShaderParameter("orm_arr", g.Orm);
    mat.SetShaderParameter("height_arr", g.Height);
    mat.SetShaderParameter("mat_count", g.Count);
    mat.SetShaderParameter("tex_scale_m", g.TexScaleM);
    // rule table as vec4 arrays (std140-friendly): xy=hmin/hmax, zw=slopemin/slopemax; a 2nd for height_amp.
    var bands = new Godot.Collections.Array();
    var amps = new Godot.Collections.Array();
    foreach (var r in g.Rules) { bands.Add(new Vector4(r.HMin, r.HMax, r.SlopeMin, r.SlopeMax)); amps.Add(r.HeightAmp); }
    mat.SetShaderParameter("rule_bands", bands);
    mat.SetShaderParameter("rule_amp", amps);
}
```
In `TerrainLabUI.Cli.cs` add `--groundarraycheck` to `ParseCli` (one-shot, like `--histcheck`):
```csharp
else if (a.StartsWith("--groundarraycheck")) {
    var g = GroundMaterialArrays.Build("res://data/ground_materials.json");
    GD.Print($"[groundarraycheck] count={g.Count} res={g.TexRes} " +
             $"albLayers={g.Albedo.GetLayers()} nrmLayers={g.Normal.GetLayers()} " +
             $"ormLayers={g.Orm.GetLayers()} hgtLayers={g.Height.GetLayers()} " +
             $"-> {(g.Count > 0 && g.Albedo.GetLayers() == g.Count ? "PASS" : "FAIL")}");
    GetTree().Quit(); return;
}
```
Wire the real build into startup: in `TerrainLabUI.cs` `AttachClouds`/`_Ready` (after `_terrain.Build`), add `_terrain.AttachGroundArrays(GroundMaterialArrays.Build("res://data/ground_materials.json"));` (windowed; guard with try/catch + PushWarning so a bad manifest doesn't crash startup).

- [ ] **Step 4: Build + run the self-check (mechanical PASS)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --groundarraycheck
```
Expected stdout: `[groundarraycheck] count=7 res=1024 albLayers=7 ... -> PASS`. (Windowed — the Poisson height bake needs a RenderingDevice; headless prints PASS with the height proxy.)

- [ ] **Step 5: Commit**

```bash
git add scripts/lab/GroundMaterialArrays.cs scripts/lab/HeightCompute.cs data/ground_materials.json scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "ground-v2(1): material texture arrays + manifest + --groundarraycheck"
```

---

## Task 2: Unit 2 — per-pixel procedural placement (shader)

Goal: in `ground.gdshader`, compute per-fragment material weights from the rule table + world fields + warped noise, select the **top-4**. Add a placement-viz debug mode to SEE it (flat per-material color). This is the unit that kills the blockiness — verify the regions are smooth, not grid-aligned.

**Files:**
- Modify: `shaders/ground.gdshader`
- Modify: `data/lab_controls.json` (`gv2_debug` enum), `scripts/lab/TerrainLab.cs`/`TerrainLabUI` (push the debug int)

**Interfaces:**
- Consumes: `rule_bands` (vec4[]), `rule_amp` (float[]), `mat_count` (int) from Task 1.
- Produces (shader-internal): `void placement(out ivec4 idx, out vec4 w)` — top-4 material indices + normalized weights at the fragment.

- [ ] **Step 1: Port the terrain fields into the vertex stage**

Replace `ground.gdshader` `vertex()` with the old shader's exact neighbor-tap derivation (copy from `terrain_lab.gdshader:446-456`, which needs `height_at(uv)` — also copy that `height_at` function + its `heightmap` sampler uniform, `terrain_lab.gdshader` ~lines 30 + the function). This gives correct `v_h`, `v_slope`, `v_curv`. (Re-host, don't reinvent — the heightfield sampling is proven.)

- [ ] **Step 2: Add the noise + placement functions**

Add to `ground.gdshader` (above `fragment`):
```glsl
uniform sampler2DArray albedo_arr : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray normal_arr : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray orm_arr    : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray height_arr : filter_linear_mipmap_anisotropic, repeat_enable;
uniform int   mat_count = 0;
uniform float tex_scale_m = 11.0;
uniform vec4  rule_bands[32];   // xy=hmin/hmax (m), zw=slopemin/slopemax
uniform float rule_amp[32];
uniform int   gv2_debug = 0;    // 0 off, 1 placement-viz (flat per-material color)

// hash + value noise (re-host from terrain_lab.gdshader 'vnoise' ~line 241 for identical character).
float hash21(vec2 p){ p=fract(p*vec2(123.34,456.21)); p+=dot(p,p+45.32); return fract(p.x*p.y); }
float vnoise(vec2 p, float wl){ p/=max(wl,1e-3); vec2 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
    float a=hash21(i),b=hash21(i+vec2(1,0)),c=hash21(i+vec2(0,1)),d=hash21(i+vec2(1,1));
    return mix(mix(a,b,f.x),mix(c,d,f.x),f.y); }

// Per-material placement weight from its rule + the fragment's terrain fields, modulated by warped noise.
float rule_weight(int i, float h, float slope, vec2 wxz){
    vec4 b = rule_bands[i];
    // smooth band membership in height + slope (soft edges so blends are gradual)
    float hw = smoothstep(b.x-60.0, b.x+60.0, h) * (1.0 - smoothstep(b.y-60.0, b.y+60.0, h));
    float sw = smoothstep(b.z-0.08, b.z+0.08, slope) * (1.0 - smoothstep(b.w-0.08, b.w+0.08, slope));
    // domain-warped multi-octave noise patches (the within-area variation, native)
    vec2 warp = vec2(vnoise(wxz+vec2(31.0,17.0),120.0), vnoise(wxz+vec2(7.0,91.0),120.0))-0.5;
    float n = vnoise(wxz*1.0 + warp*80.0 + float(i)*53.0, 40.0);
    return max(hw*sw*(0.5+0.8*n), 0.0);
}

void placement(out ivec4 idx, out vec4 w){
    idx = ivec4(0); w = vec4(0.0);
    for(int i=0;i<mat_count && i<32;i++){
        float wi = rule_weight(i, v_h, v_slope, v_world.xz);
        // insertion into the running top-4 (descending)
        if(wi > w.x){ w=vec4(wi,w.x,w.y,w.z); idx=ivec4(i,idx.x,idx.y,idx.z); }
        else if(wi > w.y){ w=vec4(w.x,wi,w.y,w.z); idx=ivec4(idx.x,i,idx.y,idx.z); }
        else if(wi > w.z){ w=vec4(w.x,w.y,wi,w.z); idx=ivec4(idx.x,idx.y,i,idx.z); }
        else if(wi > w.w){ w=vec4(w.x,w.y,w.z,wi); idx=ivec4(idx.x,idx.y,idx.z,i); }
    }
    float s = w.x+w.y+w.z+w.w;
    w = (s>1e-5) ? w/s : vec4(1.0,0.0,0.0,0.0);
}

vec3 dbg_mat_color(int i){ return 0.5+0.5*vec3(hash21(vec2(float(i),1.0)),hash21(vec2(float(i),2.0)),hash21(vec2(float(i),3.0))); }
```

- [ ] **Step 3: Wire the placement-viz into fragment()**

Replace `ground.gdshader` `fragment()`:
```glsl
void fragment() {
    ivec4 idx; vec4 w; placement(idx, w);
    if (gv2_debug == 1) {                       // placement viz: dominant material flat color
        ALBEDO = dbg_mat_color(idx.x); ROUGHNESS = 1.0; return;
    }
    // Tasks 3-4 replace the line below with real sampling+blend.
    ALBEDO = dbg_mat_color(idx.x); ROUGHNESS = 0.9;
}
```
Add the `gv2_debug` push: a `data/lab_controls.json` Debug enum `{ "id":"gv2_debug","label":"gv2 debug","tab":"Debug","type":"enum","param":"gv2_debug","options":["off","placement"],"default":0,"rand":false }` — but it targets the v2 material, not `_mat`; add a `TerrainLab.SetGroundV2Int(string,int)` and route this control via a setter (mirror Task 0's `GroundV2` field handling). Simplest: push it inside `SetGroundV2` and a dedicated `SetGv2Debug(int)` on TerrainLab that calls `GroundV2Material.SetShaderParameter("gv2_debug", v)`.

- [ ] **Step 4: Auto-shot the placement viz (visual: smooth, NOT grid-aligned)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --groundv2=1 --cam=0,140,200,-40,0 --auto-shot=C:/tmp/t2_placement.png
```
Open `t2_placement.png`. Expected: per-material color regions that follow the terrain (low=clay, slopes=scree, cliffs=basalt, peaks=white) with **organic warped boundaries — NO rectangular grid facets** (the whole point). If you see hard rectangles, the placement is accidentally reading a low-res field — it must not; debug `rule_weight`.

- [ ] **Step 5: Commit**

```bash
git add shaders/ground.gdshader data/lab_controls.json scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Apply.cs
git commit -m "ground-v2(2): per-pixel procedural placement + top-4 select + placement viz"
```

---

## Task 3: Unit 3 — sampling + anti-tiling (shader)

Goal: sample the top materials from the arrays through the re-hosted histogram anti-tiling + triplanar, so the placement viz becomes real textured ground.

**Files:** Modify `shaders/ground.gdshader`.

**Interfaces:**
- Consumes: `placement()` (Task 2), the four `sampler2DArray`s (Task 1).
- Produces (shader-internal): `vec4 sample_alb(int i, vec3 wp, vec3 nr)`, `vec3 sample_nrm(int i, ...)`, `vec3 sample_orm(int i, ...)`, `float sample_hgt(int i, ...)` — array-slot samples through anti-tiling.

- [ ] **Step 1: Re-host the histogram anti-tiling for array slots**

Copy `histo_sample_wp` + its inverse-histogram LUT machinery from `terrain_lab.gdshader` (~lines 322–366) into `ground.gdshader`, changing the sampler from `sampler2D` to a `sampler2DArray` + an `int layer` argument (sample with `textureGrad(arr, vec3(uv, float(layer)), dx, dy)`). Keep the algorithm identical (it's the keeper). The per-zone LUTs the old path baked become per-array-slot — for v1, you may bypass the LUT round-trip and use the bombing's plain multi-tap mean (the LUT is a contrast-restore refinement; land it as a follow-up if contrast washes). Note this simplification in a comment.

- [ ] **Step 2: Add triplanar array sampling**

Add `tri_w(nr)` (copy from `terrain_lab.gdshader` ~line where `tri_w` is defined) and a triplanar wrapper that blends the dominant 1–2 planes (the perf-cut branched triplanar the old shader uses — copy its `tp_alb` pattern). Produce `sample_alb/nrm/orm/hgt(int i, vec3 wp, vec3 nr)` each: compute the three planar uvs (`wp.zy/tex_scale_m`, `wp.xz/tex_scale_m`, `wp.xy/tex_scale_m`), sample the array slot `i` through the anti-tiling on the dominant plane(s), blend by `tri_w`.

- [ ] **Step 3: Use the dominant sample in fragment() (temporary)**

For this task, render just the dominant material textured (blend comes in Task 4) to verify sampling:
```glsl
    // Task 3: dominant material, real textures (blend of top-4 lands in Task 4).
    vec3 nr = normalize(v_world_normal);          // pass NORMAL through a varying if needed
    ALBEDO = sample_alb(idx.x, v_world, nr).rgb;
    vec3 orm = sample_orm(idx.x, v_world, nr);
    ROUGHNESS = orm.g; AO = orm.r;
    NORMAL_MAP = sample_nrm(idx.x, v_world, nr);
```

- [ ] **Step 4: Auto-shot (visual: real textured ground, no fuzz)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --groundv2=1 --cam=0,90,120,-30,0 --auto-shot=C:/tmp/t3_sampled.png
```
Expected: real material textures on the terrain (clay/scree/basalt/snow), sharp (mipmaps on → no shimmer fuzz), no obvious tiling repetition. Hard material edges between regions are expected here (blend is Task 4).

- [ ] **Step 5: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "ground-v2(3): texture-array sampling through re-hosted histogram anti-tiling + triplanar"
```

---

## Task 4: Unit 4 — height-aware blend + BRDF (shader) — the blockiness gate

Goal: blend the top-4 by weight × real height with `fwidth` AA → smooth per-pixel boundaries (no facets). Re-host the BRDF. This is the unit that must visibly beat the old blocky blend.

**Files:** Modify `shaders/ground.gdshader`.

**Interfaces:** Consumes Tasks 2–3. Produces final `ALBEDO/NORMAL_MAP/ROUGHNESS/AO` + a custom `light()`.

- [ ] **Step 1: Write the top-4 height-aware blend**

Replace `fragment()`'s body (after `placement`) with:
```glsl
    vec3 nr = normalize(v_world_normal);
    ivec4 idx; vec4 w; placement(idx, w);
    if (gv2_debug == 1){ ALBEDO = dbg_mat_color(idx.x); ROUGHNESS=1.0; return; }

    // height-bias each weight: a taller material wins the boundary (rock through sand).
    float h0 = sample_hgt(idx.x,v_world,nr)*rule_amp[idx.x];
    float h1 = sample_hgt(idx.y,v_world,nr)*rule_amp[idx.y];
    float h2 = sample_hgt(idx.z,v_world,nr)*rule_amp[idx.z];
    float h3 = sample_hgt(idx.w,v_world,nr)*rule_amp[idx.w];
    vec4 hb = vec4(h0,h1,h2,h3) + w;                 // weight + height
    // fwidth AA: soften the selection band to >=1px so nothing aliases (the G-1 lesson).
    float aa = max(fwidth(hb.x - max(max(hb.y,hb.z),hb.w)), 0.02);
    float top = max(max(hb.x,hb.y),max(hb.z,hb.w));
    vec4 bw = max(hb - (top - aa), 0.0);             // only materials within the AA band contribute
    float bs = bw.x+bw.y+bw.z+bw.w; bw = (bs>1e-5)? bw/bs : vec4(1,0,0,0);

    vec3 alb = sample_alb(idx.x,v_world,nr).rgb*bw.x + sample_alb(idx.y,v_world,nr).rgb*bw.y
             + sample_alb(idx.z,v_world,nr).rgb*bw.z + sample_alb(idx.w,v_world,nr).rgb*bw.w;
    vec3 orm = sample_orm(idx.x,v_world,nr)*bw.x + sample_orm(idx.y,v_world,nr)*bw.y
             + sample_orm(idx.z,v_world,nr)*bw.z + sample_orm(idx.w,v_world,nr)*bw.w;
    vec3 nm  = sample_nrm(idx.x,v_world,nr)*bw.x + sample_nrm(idx.y,v_world,nr)*bw.y
             + sample_nrm(idx.z,v_world,nr)*bw.z + sample_nrm(idx.w,v_world,nr)*bw.w;
    ALBEDO = alb; ROUGHNESS = clamp(orm.g, 0.15, 1.0); AO = orm.r; NORMAL_MAP = nm;
```
(Skip near-zero-weight fetches with branch guards if perf needs it — match the old shader's `TRI_EPS` pattern.)

- [ ] **Step 2: Re-host the BRDF**

Copy the custom `light()` from `terrain_lab.gdshader` (~lines 1015–1064) into `ground.gdshader` verbatim (Burley diffuse + GGX; drop the cloud-shadow hook for v1 or wire `cloud_shadow_tex` the same way the old shader does — note which). This keeps lighting identical to the approved look.

- [ ] **Step 3: Build + auto-shot the A/B (the blockiness gate, mechanical pre-check)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --cam=0,90,140,-38,0 --auto-shot=C:/tmp/t4_old.png
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --groundv2=1 --cam=0,90,140,-38,0 --auto-shot=C:/tmp/t4_new.png
```
Compare `t4_old.png` vs `t4_new.png`: the new one must show **smooth, organic material transitions with NO rectangular facets**, with real relief from the height-biased blend. This is the core deliverable.

- [ ] **Step 4: Commit**

```bash
git add shaders/ground.gdshader
git commit -m "ground-v2(4): top-4 height-aware blend + fwidth AA + re-hosted BRDF (no blocky facets)"
```

---

## Task 5: Parity eye-gate wiring + the flip/cleanup gate

Goal: wire the user's live A/B and define the PASS that flips the default + retires the old path. **No deletion happens in this task** — only on the user's PASS.

**Files:** Modify `scripts/lab/TerrainLabUI.Review.cs` (key 4 → ground-v2 A/B).

- [ ] **Step 1: Repurpose review key 4 to the ground-v2 parity A/B**

In `TerrainLabUI.Review.cs`, replace `case 4` (old GM2, now superseded by the reset) with a v2 toggle:
```csharp
case 4: // Ground v2 (new core) parity A/B — press 4 to toggle old<->new. Reset spec 2026-06-21.
{
    if (_lastPreset != 4) { BaselineGround(); _timeRunning = false; Set("time_of_day", 16.0f); CloseGround(); _gv2On = false; }
    else { _gv2On = !_gv2On; }
    _terrain.SetGroundV2(_gv2On);
    title = $"4 · Ground v2 (new core) — {(_gv2On ? "NEW (per-pixel procedural)" : "OLD (terrain_lab)")}  (press 4 to A/B)";
    judge = "Fly close/mid. NEW must beat OLD: NO blocky facets at transitions, materials placed sensibly (clay low / scree slopes / basalt cliffs / snow peaks), variation native, anti-tiling intact, no fuzz in motion. Sun is locked. This is the parity gate.";
    break;
}
```
Add `private bool _gv2On;` to the Review.cs fields. Leave the old `height_from_maps`/`pom_on` controls intact (they belong to the old path which still exists).

- [ ] **Step 2: Build + hand to the user (the only look-gate)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly
"<godot_console>" --headless --path /c/Wg16/wg-16-project --import
"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn
```
Drive the user: "Press **4**, fly close/mid, press **4** to A/B old↔new." Record their verdict in `DECISIONS.md` + `NEEDS_REVIEW.md`.

- [ ] **Step 3: Commit the gate wiring**

```bash
git add scripts/lab/TerrainLabUI.Review.cs
git commit -m "ground-v2(5): review key 4 = old<->new parity A/B (the gate)"
```

- [ ] **Step 4: ON USER PASS ONLY — flip default + retire old path (separate commit, gated)**

Do NOT do this until the user says the new core PASSES. Then: set `SetGroundV2(true)` as the startup default; delete the dead old paths (`interlock_blend`, the 4 m splat bake in `SplatCompute`, the bolted-on GM1/2/3 uniforms + the old `terrain_lab.gdshader` once nothing references it); update `lab_controls.json` to drop the retired controls; update ROADMAP/DECISIONS. This is the cleanup the audit wanted — but it is the *reward* for a passed gate, not part of the build.

---

## Self-Review

**Spec coverage:** Unit 1 (data model + arrays + real height) → Task 1. Unit 2 (per-pixel placement, no baked splat) → Task 2. Unit 3 (sampling + re-hosted anti-tiling + triplanar + mip/aniso) → Task 3. Unit 4 (top-4 height-aware blend + fwidth AA + BRDF) → Task 4. Guardrail (build-alongside, default-old, gate-at-parity, no big-bang delete) → Tasks 0 + 5. Keepers re-hosted (anti-tiling T3, BRDF T4, base geometry untouched). Biome-ready (manifest = a biome; rules data-driven). Out-of-scope (erosion/biomes/scale/detail-scatter) correctly absent.

**Placeholder scan:** the two genuinely-deferred items are flagged honestly, not hidden: the `HeightCompute` single-image wrapper (Task 1 Step 2 — proxy fallback keeps it unblocked) and the anti-tiling LUT contrast-restore (Task 3 Step 1 — bombing mean works, LUT is a refinement). Both are real fallbacks with a working path, not "TODO". All code steps show code; all verify steps show the command + expected output.

**Type consistency:** `GroundArrays`/`RuleData` fields match between Task 1 (definition) and Tasks 2–4 (`rule_bands`/`rule_amp`/`mat_count` uniforms, `idx`/`w` from `placement`, `sample_alb/nrm/orm/hgt` signatures). `SetGroundV2`/`AttachGroundArrays`/`GroundV2Material` consistent across Tasks 0–5. `gv2_debug`/`gv2On` consistent.

**Known iteration points (GPU reality, not plan gaps):** the shader functions are correct in structure but GLSL details (varying for the world normal, `world_vertex_coords` vs object space, exact `textureGrad` derivatives for array slots) will need a compile-fix pass against Godot 4.6 — expected for shader work; the build step in each task catches them. The *look* (rule bands, noise scales, blend AA width) is tuned against the eye-gate, per project convention.

---

## Execution Handoff

This plan is intended to be executed by a **fresh chat** (the user asked for a handoff). See the companion handoff doc `docs/superpowers/handoffs/2026-06-21-ground-v2-core-start-here.md` for cold-start orientation. When executing:

**1. Subagent-Driven (recommended for the C# tasks 0–1)** — fresh subagent per task, review between. **2. Inline Execution (recommended for the shader tasks 2–4)** — they share `ground.gdshader` + need tight build/auto-shot iteration, so one session holds the shader in context better.

Suggested: inline-execute, committing per task, with the user driving the live gate at Task 5.
