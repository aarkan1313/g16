# WG17 Terrain Surfacing — Plan 1 of 2: Palette + Rules + Surfacer (C# core)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the data-driven surfacing core — load a curated PBR material palette into `Texture2DArray`s, the height/slope→weight rule table, and the `TerrainSurfacer` that binds it all to `ground.gdshader` — plus import the curated assets and the `SurfaceCheck` gate. (Plan 2 does the shader path.)

**Architecture:** Material placement is DATA, not a hardcoded slot array (WG16's failure). N palette materials load into 4 `Texture2DArray`s (albedo/normal/roughness/ao). `SurfaceRules` maps terrain attributes → per-layer weights from JSON. `TerrainSurfacer` is the only class that sets material uniforms on the shader.

**Tech Stack:** Godot 4.6 / C#, `Texture2DArray`, JSON config.

**Plan set (surfacing = 2 plans):** **Plan 1 (this) = C# core + assets + check.** Plan 2 = `ground.gdshader` surfacing path + eye-gate.

**Reference:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-terrain-surfacing-design.md`
- WG16 assets to draw from: `C:\Wg16\wg-16-project\assets/materials/` (738 dirs, 4-map sets), `data/material_verdicts.json` (the ~108 owner-PASS list).

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k`. **Namespace:** `Te10k.Surfacing` (+ `.Checks`).
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild:** `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit. **Launch:** absolute `--path`; bare `--` for flags.
- **DATA not code:** material placement is JSON-driven; adding/swapping a material edits JSON, never a hardcoded slot list. This is the anti-WG16 rule.
- **Palette sourcing:** ONLY owner-passing assets (cross-ref `material_verdicts.json`). Explicitly EXCLUDE the WG16 fail/dropped picks (`biome_grassland`=fail, `rock035`=dropped/near-black).
- **Texture import:** mipmaps ON (avoid the WG16 mipmap-fuzz gotcha); albedo sRGB, normal/roughness/ao LINEAR.
- **Depends on:** the shipped WG17 terrain slice (`ground.gdshader` exists; this plan binds to it but the shader's surfacing PATH is Plan 2 — Plan 1 binds uniforms the shader will consume).

---

## File Structure (this plan)

```
src/surfacing/SurfacePalette.cs   # Task 2 — load N materials → 4 Texture2DArrays + per-layer meta
src/surfacing/SurfaceRules.cs     # Task 3 — (height, slope[, biome]) → top-2 layer weights, from JSON
src/surfacing/TerrainSurfacer.cs  # Task 4 — bind arrays + rule/tiling/macro uniforms to ground.gdshader
src/surfacing/checks/SurfaceCheck.cs  # Task 5 — assert N>placeholder loaded + relief-view-fade OFF
data/surface_palette.json         # Task 1 — curated starter set
data/surface_rules.json           # Task 3 — thresholds + blend widths + macro/triplanar knobs
assets/materials/<curated>/       # Task 1 — copied curated subset (4 maps each, imported)
```

---

### Task 1: Curate + import the starter material set

**Files:** Create `data/surface_palette.json`; copy a curated subset into `assets/materials/`

**Interfaces:**
- Produces: ~8–12 material dirs in WG17 `assets/materials/` (each with albedo/normal/roughness/ao .png) and `data/surface_palette.json` listing them with `{ id, dir, tiling_m, tint }`.

- [ ] **Step 1: Pick the curated layers from the WG16 PASS list**

Read `C:\Wg16\wg-16-project\data\material_verdicts.json`; select ~8–12 owner-PASS materials covering: grassland, dirt/soil, sun-baked clay, fine scree, weathered grey bedrock (NOT near-black rock), fresh powder/firn snow, fine sand. Record their dir names. EXCLUDE `biome_grassland` (fail) and `rock035` (dropped).

- [ ] **Step 2: Copy the chosen material dirs into WG17**

Run (Bash), substituting the chosen dir names:
```bash
mkdir -p "C:/Wg16/WG17/terrainengine-10k/assets/materials"
for d in 01_fine_sand 01_fine_scree 01_weathered_grey_bedrock 13_sun_baked_clay <…the rest…>; do
  cp -r "C:/Wg16/wg-16-project/assets/materials/$d" "C:/Wg16/WG17/terrainengine-10k/assets/materials/$d"
done
# remove WG16 .import files so Godot regenerates them with WG17 settings
find "C:/Wg16/WG17/terrainengine-10k/assets/materials" -name "*.import" -delete
ls "C:/Wg16/WG17/terrainengine-10k/assets/materials"
```
(Use the actual passing dir names from Step 1.)

- [ ] **Step 2.5: Import with correct settings**

Open the project once so Godot imports the PNGs (`... --path "C:/Wg16/WG17/terrainengine-10k" --import` or just open the editor). Verify each map type: albedo = sRGB + mipmaps; normal/roughness/ao = linear + mipmaps. (Set per-folder import presets if needed.)

- [ ] **Step 3: Write data/surface_palette.json**

```jsonc
{
  "layers": [
    { "id": "grass",    "dir": "<grassland_dir>",          "tiling_m": 6,  "tint": [1,1,1] },
    { "id": "dirt",     "dir": "<dirt_dir>",               "tiling_m": 5,  "tint": [1,1,1] },
    { "id": "clay",     "dir": "13_sun_baked_clay",        "tiling_m": 7,  "tint": [1,1,1] },
    { "id": "scree",    "dir": "01_fine_scree",            "tiling_m": 4,  "tint": [1,1,1] },
    { "id": "rock",     "dir": "01_weathered_grey_bedrock","tiling_m": 9,  "tint": [1,1,1] },
    { "id": "snow",     "dir": "<firn_snow_dir>",          "tiling_m": 8,  "tint": [1,1,1] },
    { "id": "sand",     "dir": "01_fine_sand",             "tiling_m": 6,  "tint": [1,1,1] }
  ]
}
```
(Fill `<…>` with the Step-1 dir names. Order defines the layer index in the texture arrays.)

- [ ] **Step 4: Commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && git add -A && git commit -m "feat(surfacing): curated PBR material set + palette JSON (owner-pass assets only)"
```

---

### Task 2: SurfacePalette — load materials into Texture2DArrays

**Files:** Create `src/surfacing/SurfacePalette.cs`

**Interfaces:**
- Produces: `class SurfacePalette` with `static SurfacePalette Load()` (reads `data/surface_palette.json`); `int LayerCount`; `Texture2DArray Albedo`, `Normal`, `Roughness`, `Ao`; `float[] TilingM` (per layer); `Color[] Tint`; `int IndexOf(string id)`.

- [ ] **Step 1: Write SurfacePalette.cs**

Parse the palette JSON. For each layer, load the four PNGs (`res://assets/materials/<dir>/albedo.png` etc.) as `Image`s; assemble four `Texture2DArray`s (one per map type) via `Texture2DArray` `CreateFromImages` with all layers same size (assert/resize to a common res, e.g. 1024²). Expose `LayerCount`, the four arrays, `TilingM`, `Tint`, `IndexOf`. Comment: layer index = JSON order.

- [ ] **Step 2: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/surfacing/SurfacePalette.cs && git commit -m "feat(surfacing): SurfacePalette loads N materials into Texture2DArrays"
```

---

### Task 3: SurfaceRules — terrain attributes → layer weights

**Files:** Create `src/surfacing/SurfaceRules.cs`, `data/surface_rules.json`

**Interfaces:**
- Consumes: `SurfacePalette.IndexOf` (Task 2).
- Produces: `class SurfaceRules` with `static SurfaceRules Load(SurfacePalette pal)`; a packed rule buffer the shader reads — `float[] RuleData` (per rule: layerIndex, heightLo, heightHi, slopeLo, slopeHi, weight) + scalars `BlendWidthM`, `SlopeBlendDeg`, `MacroScaleM`, `MacroStrength`, `TriplanarOnDeg`, `TriplanarSharpness`. (The actual top-2 selection happens in the shader from this rule data; SurfaceRules just loads + packs it.)

- [ ] **Step 1: Write data/surface_rules.json**

Use the §4 example from the spec (grass/dirt/rock/scree/snow rules + `blend_width_m`, `slope_blend_deg`, `macro`, `triplanar`). Layer names must match palette `id`s.

- [ ] **Step 2: Write SurfaceRules.cs**

Parse `surface_rules.json`; resolve each rule's layer `id` → palette index via `pal.IndexOf`; pack rules into a `float[]` (6 floats/rule) + the scalar knobs as fields. Expose `RuleData`, `RuleCount`, and the scalars. Pure data — no scene writes.

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(surfacing): SurfaceRules — height/slope→layer-weight rule table (JSON)"
```

---

### Task 4: TerrainSurfacer — bind to ground.gdshader

**Files:** Create `src/surfacing/TerrainSurfacer.cs`

**Interfaces:**
- Consumes: `SurfacePalette` (Task 2), `SurfaceRules` (Task 3), the terrain's `ShaderMaterial` (from the shipped terrain slice).
- Produces: `class TerrainSurfacer` with `void Bind(ShaderMaterial groundMat, SurfacePalette pal, SurfaceRules rules)` — sets the four texture-array uniforms (`surf_albedo_arr`, `surf_normal_arr`, `surf_rough_arr`, `surf_ao_arr`), the packed rule uniforms (`surf_rule_data` as a float array or a small data texture, `surf_rule_count`), per-layer `surf_tiling[]`/`surf_tint[]`, and the scalar knobs (`surf_blend_width_m`, `surf_slope_blend_deg`, `surf_macro_scale_m`, `surf_macro_strength`, `surf_tri_on_deg`, `surf_tri_sharpness`), plus `surf_enabled=true`.

- [ ] **Step 1: Write TerrainSurfacer.cs**

Implement `Bind(...)`: set each uniform via `groundMat.SetShaderParameter(...)`. For `surf_rule_data`, if the array is small (<= a few hundred floats) pass a packed `float[]`/`PackedFloat32Array`; otherwise encode as a small `ImageTexture` (RGBA32F, one texel per rule) and bind that — document which. This is the ONLY class that sets material uniforms (the WG16 `LoadGroundMaterials` role, done right).

- [ ] **Step 2: Wire it where the terrain builds its material**

In the terrain's `_Ready`/`Build` path (the shipped terrain slice's `Terrain.cs`), after the ground material exists: `var pal = SurfacePalette.Load(); var rules = SurfaceRules.Load(pal); new TerrainSurfacer().Bind(groundMat, pal, rules);`. (The shader still renders the placeholder until Plan 2 adds the surfacing path — these uniforms are inert but bound.)

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(surfacing): TerrainSurfacer binds palette+rules to ground material"
```

---

### Task 5: SurfaceCheck — assert data-driven load + relief-fade OFF

**Files:** Create `src/surfacing/checks/SurfaceCheck.cs`

**Interfaces:**
- Consumes: `SurfacePalette`, the ground `ShaderMaterial`.
- Produces: `static bool Run(SurfacePalette pal, ShaderMaterial groundMat)` — asserts `pal.LayerCount >= 6` (real palette loaded, not a placeholder/hardcoded slot), all four texture arrays non-null with matching layer counts, and the shader's view-angle relief-fade is OFF (the `surf_relief_view_fade` uniform == 0 / absent — the structural guard). Prints `SURFACE PASS materials=N relief-view-fade=OFF`.

- [ ] **Step 1: Write SurfaceCheck.cs**

Assert `pal.LayerCount >= 6` and `pal.Albedo/Normal/Roughness/Ao` each have `pal.LayerCount` layers. Read back the `surf_relief_view_fade` shader param (Plan 2 defines it; default 0) and assert it's 0. Print PASS/FAIL with the material count + the flag.

- [ ] **Step 2: Wire `--surfacecheck` + run**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --surfacecheck
```
Expected: `SURFACE PASS materials=<N> relief-view-fade=OFF`, exit 0. (If `surf_relief_view_fade` doesn't exist yet because Plan 2 isn't done, the check treats absent as OFF — fine.)

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(surfacing): SurfaceCheck asserts data-driven palette + relief-fade OFF (PASS)"
```

---

## Self-Review

**Spec coverage (Plan 1 = C# core):** §3 SurfacePalette/SurfaceRules/TerrainSurfacer + Texture2DArray approach → Tasks 2–4; §4 rule table → Task 3; §6 curated palette from PASS assets, exclude fail/dropped → Task 1; §7 SurfaceCheck (N loaded + relief-fade OFF) → Task 5; §8 mipmaps-on/sRGB-vs-linear import → Task 1 Step 2.5. The SHADER path (§5) is Plan 2. ✓

**Placeholder scan:** the `<…dir…>` placeholders in JSON/copy commands are values to fill from the Task-1 verdicts selection (a real lookup step, specified), not unspecified work. No TBD/“handle edge cases”. ✓

**Type consistency:** `SurfacePalette` (LayerCount, Albedo/Normal/Roughness/Ao, TilingM, Tint, IndexOf) used consistently in Tasks 3–5; `SurfaceRules.Load(SurfacePalette)` + RuleData/scalars consistent; `TerrainSurfacer.Bind(ShaderMaterial, SurfacePalette, SurfaceRules)` matches the uniform names Plan 2 will consume (`surf_*`). ✓

**Note:** TDD-lite — the palette/rules loaders are config parsers verified by `SurfaceCheck` (Task 5) + the Plan 2 eye-gate; the behavior that matters (look) is shader-side. The check structurally guards the relief-fade bug. The `surf_*` uniform names here are the contract Plan 2 implements.
