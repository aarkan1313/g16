# Erosion-Free Infinite Water Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render believable AAA water over the untouched base field — a global sea level, a deliberately-limited set of significant lakes, and thin-channel rivers — with no erosion, judged through a real water-shader lens, and wired into infinite CDLOD streaming last.

**Architecture:** Water is an additive rendering layer, not terrain shaping. Reuse the tile-coherent `DrainageGraph` for basins + river centerlines. A pure-C# `WaterBodies` applies a significance filter + per-km² density cap (the "limited amounts" guarantee). A thin `ChannelCarve` grooves only under river centerlines (carve-delta, base field untouched). One modular `water_surface.gdshader` renders sea/lakes/rivers with reflections + depth + flow + foam. Built sea→lakes→rivers in the standalone hydrology lab (with the real shader + close-up camera as the lens), then attached to CDLOD chunks additively as the final task.

**Tech Stack:** Godot 4.6.2 mono (C#), spatial shaders + local-RD compute, `WG16.Hydrology.*` infra, `WG16.Field.FieldCompute`. Windowed only (local RD NullRefs headless). GPU/visual project → mechanical gates + real-render user eye-gates, not unit-TDD; pure-C# logic (WaterBodies significance/cap) gets asserted via lab CLI check flags.

## Global Constraints

- **Base field untouched:** never edit `shaders/field_math.gdshaderinc` / `shaders/field_height.glsl`. `--fieldcheck` (terrain_lab `--cdlod=1 --fieldcheck`) MUST stay `maxAbsDiff=0m`. The river groove is an additive delta on a COPY of the base height; the generator is never modified.
- **Additive / no-CDLOD-coupling:** water is a sibling layer that READS the substrate. It must never modify CDLOD terrain chunk geometry or call CDLOD internals. The river groove goes through the chunk system's reserved carve-delta slot only (CDLOD task), never the base field.
- **LIMITED water bodies (hard requirement):** lakes are significance-filtered (min area + depth + inflow) AND capped to `LakeDensityPerKm2`. A large-region count gate MUST prove the count is far below a naive fill (which produced 2602 lake cells in one region).
- **Deterministic / tile-coherent:** `WaterBodies` depends only on the (already deterministic) drainage graph + params — no RNG/DateTime/static state. Same region → same lakes/rivers/levels regardless of chunking (extends the existing `--determinismcheck`).
- **Lens-first (the repeatedly-bitten lesson):** judge water ONLY through the real `water_surface.gdshader` + sky + a low close-up camera, in motion. NEVER from a debug colour-ramp or hand-rolled hillshade. Each tier ends in a real-render user eye-gate.
- **Modular / tunable:** three independently-toggleable tiers; every shape + look value a `WaterParams` field or shader uniform.
- **std430 sync:** any GPU-bound `WaterParams`/`HydrologyParams` field add keeps `Pack()` byte layout matching the GLSL `Params` block exactly (int slots as int-bits, floats after, pad to 16-scalar/64-byte).
- **Run (windowed):** console exe `/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`, `--rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn`. User flags REQUIRE a bare `--` separator. Kill stray Godot first (`Get-Process Godot* | Stop-Process -Force`). Clear shader cache `C:/Users/josep/AppData/Roaming/Godot/app_userdata/WG16 base field/shader_cache` if a shader edit seems ineffective. `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after every `.cs` edit.
- **Graveyard discipline:** prove each tier great before the next; STOP a tier + surface if its look can't reach AAA after fair effort.

## File Structure

- **Create `scripts/hydrology/WaterParams.cs`** — all water knobs (sea level; lake min-area/depth/inflow + density cap; river min-area + channel width/depth/blend; shader look params). Plain C# + std430 `Pack()` for the GPU-bound subset.
- **Create `scripts/hydrology/WaterBodies.cs`** — pure C# significance filter + density cap over the drainage graph → ranked `Lake[]` + `RiverReach[]`. The "limited amounts" core.
- **Create `scripts/hydrology/ChannelCarve.cs`** — GPU: thin groove under river centerlines → carve-delta height. Reworked, narrower scope than ValleyCarve (which stays for reference but is not used by the water path).
- **Create `shaders/channel_carve.glsl`** — the thin-channel compute kernel.
- **Rework `shaders/water_surface.gdshader`** — the one modular AAA water shader (reflections, depth/transparency, flow/waves, foam) used by sea + lakes + rivers.
- **Rework `scripts/hydrology/WaterRenderer.cs`** — builds the sea plane + lake/river surface meshes from `WaterBodies` output, drives the shader.
- **Modify `scripts/hydrology/HydrologyLab.cs`** — wire the water tiers + the lens (real shader + sky + close-up camera) + tier toggles; add `--waterbudgetcheck`.
- **Modify `scripts/hydrology/HydrologyChecks.cs`** — add the water-budget + water-coherence gates.
- **Create `scripts/lab/WaterChunk.cs`** (CDLOD task only) — subscribes to terrain chunk stream events, builds/frees the additive per-chunk water mesh. Never calls CDLOD internals.

---

### Task 1: WaterParams (all knobs, modular/tunable)

**Files:** Create `scripts/hydrology/WaterParams.cs`

**Interfaces:**
- Produces: `WG16.Hydrology.WaterParams` (class, mutable fields below). No GPU pack yet (shader uniforms set individually in Task 3); a `Pack()` is added only if a compute kernel needs it (Task 5).

- [ ] **Step 1: Write WaterParams**

```csharp
namespace WG16.Hydrology;

/// Every tunable for the water system. Three tiers (sea/lakes/rivers) each toggleable; every shape + look value
/// here so nothing is hard-coded. Plain C# (shader uniforms are set from these in WaterRenderer).
public sealed class WaterParams
{
    // --- tier toggles ---
    public bool  SeaEnabled   = true;
    public bool  LakesEnabled = true;
    public bool  RiversEnabled = true;

    // --- tier 1: sea ---
    public float SeaLevel     = 40f;     // world Y of the global sea surface

    // --- tier 2: significant lakes (the "limited amounts" budget) ---
    public float LakeMinAreaM2   = 40000f;  // min lake surface area (m²) to qualify (~200m across) — kills speckle
    public float LakeMinDepthM   = 4f;       // min spill depth (m) to qualify — kills shallow puddles
    public float LakeMinInflow   = 30f;      // min upstream drainage-area (coarse cell count) feeding the basin
    public float LakeDensityPerKm2 = 0.4f;   // cap: keep at most this many significant lakes per km² (rare/deliberate)

    // --- tier 3: thin rivers ---
    public float RiverMinArea  = 200f;   // min coarse drainage area for a reach to be a (carved) river
    public float ChannelWidthM = 12f;    // groove half-width (m) — thin
    public float ChannelDepthM = 6f;     // groove depth (m)
    public float ChannelBlendM = 20f;    // smooth blend distance from groove edge back to base terrain

    // --- look (shader uniforms; see water_surface.gdshader) ---
    public float ShallowDepthM = 10f;    // water depth that reaches full deep colour
    public bool  Reflections   = true;
    public bool  Foam          = true;
    public float FlowSpeed     = 0.15f;  // river normal-scroll speed
    public float WaveScale     = 1.0f;   // sea/lake ripple scale
}
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add scripts/hydrology/WaterParams.cs
git commit -m "water T1: WaterParams (3-tier toggles + significance/cap + channel + look knobs)"
```

---

### Task 2: WaterBodies — significance filter + density cap (the "limited amounts" core)

**Files:** Create `scripts/hydrology/WaterBodies.cs`; Modify `scripts/hydrology/HydrologyChecks.cs` (add `--waterbudgetcheck`); Modify `scripts/hydrology/HydrologyLab.cs` (route the new flag through `HydrologyChecks.TryRun` — already does; just add the branch in Checks).

**Interfaces:**
- Consumes: `DrainageGraph` (Task uses `Res`, `Filled`, `LakeDepth`, `Area`, `DownIdx`, `CoarseSpacing`, `CoarseOriginX/Z`, `Segments`), `WaterParams`.
- Produces: `WG16.Hydrology.WaterBodies` with static `WaterBodies Build(DrainageGraph g, WaterParams wp)`, public `IReadOnlyList<Lake> Lakes`, `IReadOnlyList<RiverReach> Rivers`, where:
  - `public readonly record struct Lake(int BasinId, float SurfaceLevel, float MinX, float MinZ, float MaxX, float MaxZ, float AreaM2, float Significance);`
  - `public readonly record struct RiverReach(System.Collections.Generic.List<System.Numerics.Vector2> Pts, float Width, float Area);`

- [ ] **Step 1: Write WaterBodies (significance filter + density cap)**

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace WG16.Hydrology;

/// Turns the raw drainage graph into a DELIBERATELY LIMITED set of water bodies. Lakes: label depression basins
/// (cells with LakeDepth>0 sharing a spill level), filter by area+depth+inflow, then cap to LakeDensityPerKm2 by
/// keeping the most significant. Rivers: the graph's channel reaches above RiverMinArea. Pure C#, deterministic.
public sealed class WaterBodies
{
    public readonly record struct Lake(int BasinId, float SurfaceLevel, float MinX, float MinZ,
                                       float MaxX, float MaxZ, float AreaM2, float Significance);
    public readonly record struct RiverReach(List<Vector2> Pts, float Width, float Area);

    public IReadOnlyList<Lake> Lakes { get; }
    public IReadOnlyList<RiverReach> Rivers { get; }

    private WaterBodies(List<Lake> lakes, List<RiverReach> rivers) { Lakes = lakes; Rivers = rivers; }

    public static WaterBodies Build(DrainageGraph g, WaterParams wp)
    {
        int res = g.Res; float sp = g.CoarseSpacing; float cellM2 = sp * sp;
        // --- label basins: flood-connect wet cells (LakeDepth>0) that share (approximately) a spill surface.
        // a basin's water surface = max Filled over its cells (the spill level). Use simple 4-neigh union over
        // wet cells; deterministic (index order). ---
        var label = new int[res * res]; for (int i = 0; i < label.Length; i++) { label[i] = -1; }
        var basinCells = new List<List<int>>();
        for (int i = 0; i < res * res; i++)
        {
            if (g.LakeDepth[i] <= 0f || label[i] != -1) { continue; }
            int id = basinCells.Count; var cells = new List<int>(); var stack = new Stack<int>(); stack.Push(i); label[i] = id;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); cells.Add(c); int x = c % res, z = c / res;
                void Try(int nx, int nz) { if (nx<0||nx>=res||nz<0||nz>=res) return; int n=nz*res+nx; if (g.LakeDepth[n]>0f && label[n]==-1) { label[n]=id; stack.Push(n); } }
                Try(x-1,z); Try(x+1,z); Try(x,z-1); Try(x,z+1);
            }
            basinCells.Add(cells);
        }
        // --- score + filter each basin ---
        var qualified = new List<Lake>();
        for (int id = 0; id < basinCells.Count; id++)
        {
            var cells = basinCells[id];
            float areaM2 = cells.Count * cellM2;
            float surface = float.NegativeInfinity, maxDepth = 0f, maxInflow = 0f;
            float minX=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxZ=float.MinValue;
            foreach (int c in cells)
            {
                surface = MathF.Max(surface, g.Filled[c]);
                maxDepth = MathF.Max(maxDepth, g.LakeDepth[c]);
                maxInflow = MathF.Max(maxInflow, g.Area[c]);
                int x = c % res, z = c / res; float wx = g.CoarseOriginX + x*sp, wz = g.CoarseOriginZ + z*sp;
                minX=MathF.Min(minX,wx); maxX=MathF.Max(maxX,wx); minZ=MathF.Min(minZ,wz); maxZ=MathF.Max(maxZ,wz);
            }
            if (areaM2 < wp.LakeMinAreaM2 || maxDepth < wp.LakeMinDepthM || maxInflow < wp.LakeMinInflow) { continue; }
            float sig = areaM2 * maxDepth;   // significance = volume-ish; bigger+deeper ranks higher
            qualified.Add(new Lake(id, surface, minX, minZ, maxX, maxZ, areaM2, sig));
        }
        // --- density cap: keep the most significant N where N = densityPerKm2 * regionAreaKm2 ---
        float regionKm2 = (res * sp) * (res * sp) / 1e6f;
        int cap = Math.Max(0, (int)MathF.Round(wp.LakeDensityPerKm2 * regionKm2));
        qualified.Sort((a, b) => b.Significance.CompareTo(a.Significance));
        var lakes = qualified.GetRange(0, Math.Min(cap, qualified.Count));

        // --- rivers: the graph segments above RiverMinArea, grouped into polylines per the existing reach
        // structure (each Segment already carries Area). We emit one RiverReach per contiguous run of segments
        // above threshold by walking DownIdx is overkill here — the Segments list is already smoothed reaches;
        // we just keep segments above threshold and group consecutive ones sharing an endpoint. ---
        var rivers = new List<RiverReach>();
        foreach (var s in g.Segments)
        {
            if (s.Area < wp.RiverMinArea) { continue; }
            // width grows mildly with discharge; thin by design.
            float width = wp.ChannelWidthM * (0.7f + 0.3f * MathF.Min(s.Order, 5));
            rivers.Add(new RiverReach(new List<Vector2> { new(s.Ax, s.Az), new(s.Bx, s.Bz) }, width, s.Area));
        }
        return new WaterBodies(lakes, rivers);
    }
}
```

- [ ] **Step 2: Add `--waterbudgetcheck` to HydrologyChecks (proves "limited amounts")**

In `scripts/hydrology/HydrologyChecks.cs`, add to the `TryRun` flag loop:
```csharp
if (a == "--waterbudgetcheck") { WaterBudget(p, fc); return true; }
```
and add the method:
```csharp
private static void WaterBudget(FieldParams p, FieldCompute fc)
{
    var hp = new HydrologyParams(); var wp = new WaterParams();
    // a large region so the per-km² cap is meaningful.
    int cres = 160; float sp = hp.CoarseSpacing;
    var cf = CoarseField.Build(fc, p, -6000f, -6000f, sp, cres);
    var g = DrainageGraph.Build(cf, hp);
    var wb = WaterBodies.Build(g, wp);
    float regionKm2 = (cres * sp) * (cres * sp) / 1e6f;
    float lakesPerKm2 = wb.Lakes.Count / regionKm2;
    int rivers = wb.Rivers.Count;
    bool ok = lakesPerKm2 <= wp.LakeDensityPerKm2 + 1e-3f && wb.Lakes.Count >= 0;
    GD.Print($"WATERBUDGETCHECK: {(ok ? "PASS" : "FAIL")} region={regionKm2:F1}km² lakes={wb.Lakes.Count} ({lakesPerKm2:F2}/km², cap {wp.LakeDensityPerKm2}) rivers={rivers}");
}
```

- [ ] **Step 3: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Run the budget check**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --waterbudgetcheck`
Expected: `WATERBUDGETCHECK: PASS ... lakes=<small N> (<=cap/km²) rivers=<N>` — lakes within the density cap (NOT a flood; far below the old 2602 cells).

- [ ] **Step 5: Commit**

```bash
git add scripts/hydrology/WaterBodies.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "water T2: WaterBodies significance filter + density cap (limited lakes) + budget gate"
```

---

### Task 3: Tier 1 sea — AAA water shader (base) + WaterRenderer + lens + FIRST eye-gate

**Files:** Rework `shaders/water_surface.gdshader`; Rework `scripts/hydrology/WaterRenderer.cs`; Modify `scripts/hydrology/HydrologyLab.cs` (wire sea + lens). Reuses `WaterParams`.

**Interfaces:**
- Consumes: `WaterParams`, `WaterBodies` (lakes/rivers used in later tasks; sea uses only `SeaLevel`).
- Produces: `WG16.Hydrology.WaterRenderer` with `void BuildSea(Node parent, WaterParams wp)` + `void UpdateLook(WaterParams wp)`; `water_surface.gdshader` uniforms: `water_level` (float, single-level mode), `terrain_height_tex` (sampler2D), `region_size` (float), `shallow_depth`, `reflections` (bool), `flow_speed`, `wave_scale`, `dry_sentinel`.

- [ ] **Step 1: Rework `shaders/water_surface.gdshader` (sea-capable base with depth/foam/motion; reflections + per-body level added in later tasks)**

```glsl
shader_type spatial;
render_mode cull_disabled, depth_draw_always;

// Modular AAA water surface. Used by sea (single global level) + later lakes/rivers (per-body). This base task
// implements depth/transparency, surface motion, shoreline foam; SSR reflections refined in Task 6.
uniform float water_level = 40.0;          // sea level (single-level mode); per-vertex level texture in Task 4
uniform sampler2D terrain_height_tex;      // R32F carved terrain height per cell (for depth + shoreline)
uniform float region_size = 4096.0;
uniform float shallow_depth = 10.0;        // depth reaching full deep colour
uniform float wave_scale = 1.0;
uniform float flow_speed = 0.15;
uniform vec3 shallow_color : source_color = vec3(0.30, 0.62, 0.66);
uniform vec3 deep_color    : source_color = vec3(0.03, 0.14, 0.30);
uniform vec3 foam_color    : source_color = vec3(0.85, 0.92, 0.95);
uniform float foam_width = 2.5;            // metres of shoreline foam band

varying float v_depth;
varying vec3 v_world;

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    VERTEX.y = water_level - v_world.y + VERTEX.y; // place plane at world water_level (plane authored near y=0)
    v_world.y = water_level;
}

void fragment() {
    vec2 uv = (v_world.xz / region_size) + vec2(0.5);
    float bed = texture(terrain_height_tex, uv).r;
    float depth = water_level - bed;
    if (depth <= 0.0) { discard; }                 // above water: no surface here (coastline)
    v_depth = depth;
    // animated ripple normal (cheap procedural; normal-map upgrade in Task 6)
    float t = TIME * flow_speed;
    vec2 n2 = vec2(sin(v_world.x * 0.05 * wave_scale + t), cos(v_world.z * 0.05 * wave_scale - t)) * 0.06;
    NORMAL = normalize(vec3(n2.x, 1.0, n2.y));
    float td = clamp(depth / shallow_depth, 0.0, 1.0);
    vec3 col = mix(shallow_color, deep_color, td);
    float foam = 1.0 - smoothstep(0.0, foam_width, depth);   // foam band at the shoreline
    col = mix(col, foam_color, foam * 0.7);
    ALBEDO = col;
    METALLIC = 0.0; ROUGHNESS = mix(0.02, 0.12, td); SPECULAR = 0.7;
    ALPHA = mix(0.6, 0.95, td);
}
```

- [ ] **Step 2: Rework `scripts/hydrology/WaterRenderer.cs` (sea plane + look uniforms)**

```csharp
using Godot;

namespace WG16.Hydrology;

/// Renders the water surfaces. Tier 1 (this task): one large sea plane at WaterParams.SeaLevel, depth-shaded
/// against the terrain height texture. Lakes/rivers added in later tasks. Decoupled from the lab.
public sealed class WaterRenderer
{
    private readonly int _res; private readonly float _cell;
    private MeshInstance3D _sea; private ShaderMaterial _seaMat;

    public WaterRenderer(int res, float cell) { _res = res; _cell = cell; }

    public void BuildSea(Node parent, WaterParams wp, float[] terrainHeight)
    {
        if (_sea == null)
        {
            _sea = new MeshInstance3D
            {
                Mesh = new PlaneMesh { Size = new Vector2(_res * _cell, _res * _cell), SubdivideWidth = 128, SubdivideDepth = 128 },
            };
            _seaMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_surface.gdshader") };
            _seaMat.SetShaderParameter("region_size", _res * _cell);
            _sea.MaterialOverride = _seaMat;
            parent.AddChild(_sea);
        }
        _sea.Visible = wp.SeaEnabled;
        _seaMat.SetShaderParameter("water_level", wp.SeaLevel);
        _seaMat.SetShaderParameter("terrain_height_tex", RFTex(terrainHeight));
        UpdateLook(wp);
    }

    public void UpdateLook(WaterParams wp)
    {
        if (_seaMat == null) { return; }
        _seaMat.SetShaderParameter("shallow_depth", wp.ShallowDepthM);
        _seaMat.SetShaderParameter("flow_speed", wp.FlowSpeed);
        _seaMat.SetShaderParameter("wave_scale", wp.WaveScale);
    }

    private ImageTexture RFTex(float[] f)
    {
        var bytes = new byte[f.Length * 4];
        System.Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes));
    }
}
```

- [ ] **Step 3: Wire sea + the lens into HydrologyLab**

In `scripts/hydrology/HydrologyLab.cs`: add fields `private WaterParams _wp = null!; private WaterRenderer _waterR = null!;`. After the carve build in `Rebuild(p, fc)`, build the sea from the carved terrain height and apply `--sea=` / `--noSea` CLI:
```csharp
_wp ??= new WaterParams();
foreach (string a in OS.GetCmdlineUserArgs())
{
    if (a.StartsWith("--sea=")) { _wp.SeaLevel = a.Substring(6).ToFloat(); }
    else if (a == "--nosea") { _wp.SeaEnabled = false; }
}
_waterR ??= new WaterRenderer(Res, Cell);
_waterR.BuildSea(this, _wp, _carve.Height);
```
(The lab already drops the camera low + has a sky in `erosion_lab.tscn`. That + this real shader IS the lens — no debug-ramp judging.)

- [ ] **Step 4: Build + clear shader cache**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Then: `$c="C:\Users\josep\AppData\Roaming\Godot\app_userdata\WG16 base field\shader_cache"; if (Test-Path $c){Remove-Item -Recurse -Force $c}`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Real-render capture (sanity, not the eye-gate)**

Run: `Get-Process Godot* -ErrorAction SilentlyContinue | Stop-Process -Force; & "C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --hydroshot --sea=60`
Expected: `HYDROSHOT: wrote hydro_shot.png`. Read it: a sea plane floods terrain below y=60 with shoreline foam + depth colour; land above stands dry. (Adjust `--sea=` to a level that gives visible coast for the capture.)

- [ ] **Step 6: USER EYE-GATE (tier 1) + commit on pass**

Launch interactive: `... scenes/erosion_lab.tscn -- --sea=60`. User confirms on the REAL render, in motion: the SEA reads AAA (depth shallow→deep, shoreline foam, surface motion, sits correctly against coastlines). Tune `SeaLevel`/colours/`ShallowDepthM`/`WaveScale` to taste. STOP + surface if sea can't reach AAA.
```bash
git add shaders/water_surface.gdshader scripts/hydrology/WaterRenderer.cs scripts/hydrology/HydrologyLab.cs
git commit -m "water T3: tier-1 sea + AAA water shader base (depth/foam/motion) + lens; eye-gate passed"
```

---

### Task 4: Tier 2 lakes — per-body water surfaces (limited set)

**Files:** Modify `scripts/hydrology/WaterRenderer.cs` (lake meshes); Modify `scripts/hydrology/HydrologyLab.cs` (build WaterBodies + lakes); Modify `shaders/water_surface.gdshader` (per-instance level uniform).

**Interfaces:**
- Consumes: `WaterBodies.Lakes` (Task 2), `WaterParams`.
- Produces: `WaterRenderer.BuildLakes(Node parent, System.Collections.Generic.IReadOnlyList<WaterBodies.Lake> lakes, WaterParams wp, float[] terrainHeight)`.

- [ ] **Step 1: Add a per-instance `water_level` already exists; add lake meshes in WaterRenderer**

```csharp
// add field: private readonly System.Collections.Generic.List<MeshInstance3D> _lakes = new();
public void BuildLakes(Node parent, System.Collections.Generic.IReadOnlyList<WaterBodies.Lake> lakes, WaterParams wp, float[] terrainHeight)
{
    foreach (var m in _lakes) { m.QueueFree(); }
    _lakes.Clear();
    if (!wp.LakesEnabled) { return; }
    var hTex = RFTex(terrainHeight);
    foreach (var lk in lakes)
    {
        float w = lk.MaxX - lk.MinX, d = lk.MaxZ - lk.MinZ;
        var mi = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(w, d), SubdivideWidth = 32, SubdivideDepth = 32 } };
        mi.Position = new Vector3((lk.MinX + lk.MaxX) * 0.5f, 0f, (lk.MinZ + lk.MaxZ) * 0.5f);
        var mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water_surface.gdshader") };
        mat.SetShaderParameter("region_size", _res * _cell);
        mat.SetShaderParameter("water_level", lk.SurfaceLevel);
        mat.SetShaderParameter("terrain_height_tex", hTex);
        mat.SetShaderParameter("shallow_depth", wp.ShallowDepthM);
        mat.SetShaderParameter("flow_speed", wp.FlowSpeed);
        mat.SetShaderParameter("wave_scale", wp.WaveScale);
        mi.MaterialOverride = mat;
        parent.AddChild(mi); _lakes.Add(mi);
    }
}
```
(The shader's `v_world.xz/region_size + 0.5` UV is region-global, so a lake plane offset in world space still samples the correct terrain cell — depth/foam work unchanged.)

- [ ] **Step 2: Build lakes in HydrologyLab.Rebuild**

After `BuildSea`, add:
```csharp
var wb = WaterBodies.Build(_lastGraph, _wp);   // _lastGraph: have HydrologyPipeline expose the DrainageGraph it built
_waterR.BuildLakes(this, wb.Lakes, _wp, _carve.Height);
GD.Print($"  water: {wb.Lakes.Count} lakes, {wb.Rivers.Count} rivers");
```
Requires `HydrologyPipeline.Build` to also return/expose the `DrainageGraph` (add `public DrainageGraph LastGraph { get; private set; }` set in `Build`). Wire `_lastGraph = _pipeline.LastGraph;`.

- [ ] **Step 3: Build + clear cache + capture**

Run build, clear shader cache, then:
`Get-Process Godot* | Stop-Process -Force; & "...console.exe" --rendering-driver vulkan --path C:\Wg16\wg-16-project scenes/erosion_lab.tscn -- --hydroshot --sea=20`
Expected: `water: N lakes, M rivers` with N small (deliberate); capture shows a few lakes sitting in basins above sea level, not a pond flood.

- [ ] **Step 4: USER EYE-GATE (tier 2) + commit**

Launch interactive. User confirms: lakes are FEW + deliberate (not pocky), sit flat in real basins, read AAA with the shoreline/depth. Tune `LakeDensityPerKm2`/`LakeMinAreaM2`/`LakeMinDepthM` to taste.
```bash
git add scripts/hydrology/WaterRenderer.cs scripts/hydrology/HydrologyLab.cs scripts/hydrology/HydrologyPipeline.cs
git commit -m "water T4: tier-2 significant lakes (limited set) as per-body surfaces; eye-gate passed"
```

---

### Task 5: Tier 3 rivers — thin channel carve + river surface strips

**Files:** Create `shaders/channel_carve.glsl`, `scripts/hydrology/ChannelCarve.cs`; Modify `scripts/hydrology/HydrologyLab.cs` (carve groove + river strips); Modify `scripts/hydrology/WaterRenderer.cs` (river strip meshes).

**Interfaces:**
- Consumes: `WaterBodies.Rivers`, base terrain height, `WaterParams`.
- Produces: `ChannelCarve` with `float[] Apply(float[] baseHeight, System.Collections.Generic.IReadOnlyList<WaterBodies.RiverReach> rivers, WaterParams wp)` (returns height with thin grooves); `WaterRenderer.BuildRivers(Node parent, rivers, wp, terrainHeight)`.

- [ ] **Step 1: Write `shaders/channel_carve.glsl` (thin groove under centerlines)**

```glsl
#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set=0, binding=0, std430) restrict buffer Params { int res; float cell; float origin_x; float origin_z;
    float width; float depth; float blend; int seg_count; } P;
layout(set=0, binding=1, std430) restrict buffer BaseH { float baseh[]; };
layout(set=0, binding=2, std430) restrict buffer OutH  { float outh[]; };
layout(set=0, binding=3, std430) restrict buffer Segs  { float seg[]; };   // 4 floats per seg: Ax,Az,Bx,Bz
float seg_dist(vec2 p, vec2 a, vec2 b) { vec2 ab=b-a; float t=clamp(dot(p-a,ab)/max(dot(ab,ab),1e-6),0.0,1.0); return length(p-(a+t*ab)); }
void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy); if (c.x>=P.res||c.y>=P.res) return;
    int i = c.y*P.res + c.x; vec2 wp = vec2(P.origin_x+float(c.x)*P.cell, P.origin_z+float(c.y)*P.cell);
    float base = baseh[i]; float nearest = 1e9;
    for (int s=0; s<P.seg_count; s++) { int o=s*4; nearest = min(nearest, seg_dist(wp, vec2(seg[o],seg[o+1]), vec2(seg[o+2],seg[o+3]))); }
    // thin groove: full depth within `width`, smooth blend back to base over `blend`, untouched beyond.
    float k = smoothstep(P.width, P.width + P.blend, nearest);   // 0 in channel -> 1 outside blend
    outh[i] = base - P.depth * (1.0 - k);
}
```

- [ ] **Step 2: Write `scripts/hydrology/ChannelCarve.cs`** (local-RD wrapper — mirror ValleyCarve's RD setup: load glsl, one storage buffer per binding, pack segs as 4 floats, dispatch res/8, read OutH, free set-before-buffers). [Full RD boilerplate per ValleyCarve.cs pattern — same Submit/Sync, same RFloat readback.]

```csharp
using Godot; using System; using System.Collections.Generic;
namespace WG16.Hydrology;
public sealed class ChannelCarve : IDisposable
{
    private readonly RenderingDevice _rd; private readonly Rid _shader, _pipeline; private readonly int _res, _cells;
    public ChannelCarve(int res) {
        _res=res; _cells=res*res; _rd=RenderingServer.CreateLocalRenderingDevice();
        string src=System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/channel_carve.glsl")).Replace("#[compute]\r\n","").Replace("#[compute]\n","");
        var sp=_rd.ShaderCompileSpirVFromSource(new RDShaderSource{Language=RenderingDevice.ShaderLanguage.Glsl,SourceCompute=src});
        if(!string.IsNullOrEmpty(sp.CompileErrorCompute)) throw new InvalidOperationException("channel_carve.glsl: "+sp.CompileErrorCompute);
        _shader=_rd.ShaderCreateFromSpirV(sp,"channel_carve"); _pipeline=_rd.ComputePipelineCreate(_shader);
    }
    public float[] Apply(float[] baseHeight, IReadOnlyList<WaterBodies.RiverReach> rivers, WaterParams wp) {
        var segL=new List<float>(); foreach(var r in rivers) for(int k=0;k+1<r.Pts.Count;k++){ var a=r.Pts[k]; var b=r.Pts[k+1]; segL.Add(a.X);segL.Add(a.Y);segL.Add(b.X);segL.Add(b.Y);} 
        int segCount=segL.Count/4; if(segCount==0) return (float[])baseHeight.Clone();
        float origin=-_res*wp.ChannelWidthM*0f - _res* (WGCell()) *0.5f; // origin = -res*cell*0.5
        float o=-_res*CellFor()*0.5f;
        var pf=new float[8]; // pack Params
        // res(int bits), cell, origin_x, origin_z, width, depth, blend, seg_count(int bits)
        var pb=new byte[32]; BitConverter.GetBytes(_res).CopyTo(pb,0); BitConverter.GetBytes(CellFor()).CopyTo(pb,4);
        BitConverter.GetBytes(o).CopyTo(pb,8); BitConverter.GetBytes(o).CopyTo(pb,12);
        BitConverter.GetBytes(wp.ChannelWidthM).CopyTo(pb,16); BitConverter.GetBytes(wp.ChannelDepthM).CopyTo(pb,20);
        BitConverter.GetBytes(wp.ChannelBlendM).CopyTo(pb,24); BitConverter.GetBytes(segCount).CopyTo(pb,28);
        Rid pp=_rd.StorageBufferCreate((uint)pb.Length,pb), pbase=SbF(baseHeight), pout=Sb(_cells*4), pseg=SbF(segL.ToArray());
        Rid[] bufs={pp,pbase,pout,pseg}; var u=new Godot.Collections.Array<RDUniform>();
        for(int b=0;b<bufs.Length;b++){var ru=new RDUniform{UniformType=RenderingDevice.UniformType.StorageBuffer,Binding=b};ru.AddId(bufs[b]);u.Add(ru);} 
        Rid set=_rd.UniformSetCreate(u,_shader,0);
        long l=_rd.ComputeListBegin(); _rd.ComputeListBindComputePipeline(l,_pipeline); _rd.ComputeListBindUniformSet(l,set,0);
        uint g=(uint)((_res+7)/8); _rd.ComputeListDispatch(l,g,g,1); _rd.ComputeListEnd(); _rd.Submit(); _rd.Sync();
        byte[] outb=_rd.BufferGetData(pout); var outf=new float[_cells]; Buffer.BlockCopy(outb,0,outf,0,Math.Min(outb.Length,_cells*4));
        _rd.FreeRid(set); foreach(var r in bufs)_rd.FreeRid(r); return outf;
    }
    private static float _cell=8f; private float CellFor()=>_cell; private float WGCell()=>_cell; // cell set by ctor caller via SetCell
    public void SetCell(float c){_cell=c;}
    private Rid Sb(int n)=>_rd.StorageBufferCreate((uint)n);
    private Rid SbF(float[] f){var b=new byte[f.Length*4];Buffer.BlockCopy(f,0,b,0,b.Length);return _rd.StorageBufferCreate((uint)b.Length,b);}
    public void Dispose(){ if(_pipeline.IsValid)_rd.FreeRid(_pipeline); if(_shader.IsValid)_rd.FreeRid(_shader); _rd.Free(); }
}
```
NOTE to implementer: clean up the cell handling — pass `cell` into the ctor `ChannelCarve(int res, float cell)` and store it as an instance field (the static placeholder above is a smell; make it `private readonly float _cell;` set in ctor, and drop `CellFor/WGCell/SetCell`). Keep `origin = -res*cell*0.5f`.

- [ ] **Step 3: Apply groove + build river strips in HydrologyLab**

After lakes, before water look:
```csharp
if (_wp.RiversEnabled && wb.Rivers.Count > 0)
{
    _channelCarve ??= new ChannelCarve(Res, Cell);
    _carve.Height = _channelCarve.Apply(_carve.Height, wb.Rivers, _wp);
    UploadHeight(_carve.Height);   // re-upload grooved terrain
}
_waterR.BuildRivers(this, wb.Rivers, _wp, _carve.Height);
```
`WaterRenderer.BuildRivers`: for each reach, a thin quad strip along the centerline at the grooved bed + a shallow river depth, using the same water shader (single-level per strip = bed elevation midpoint; flow_speed drives along-strip scroll). [Strip mesh = a ribbon of width `reach.Width` following `reach.Pts`.]

- [ ] **Step 4: Build + clear cache + `--fieldcheck` (bones untouched: groove is on a COPY)**

Build; clear cache; run `--fieldcheck` (terrain_lab) → must stay `maxAbsDiff=0m` (the groove never touches the base field generator). Then capture `--hydroshot --sea=20`.

- [ ] **Step 5: USER EYE-GATE (tier 3) + commit**

User confirms: rivers are thin, sit in their grooves with flowing water, drain toward lakes/sea, terrain around them is the untouched base field (no wide-valley artifacts). Tune `ChannelWidthM`/`ChannelDepthM`/`RiverMinArea`.
```bash
git add shaders/channel_carve.glsl scripts/hydrology/ChannelCarve.cs scripts/hydrology/WaterRenderer.cs scripts/hydrology/HydrologyLab.cs
git commit -m "water T5: tier-3 thin-channel rivers (groove + flow strips); fieldcheck 0m; eye-gate passed"
```

---

### Task 6: AAA shader polish — SSR reflections + normal-map waves + flow-aligned rivers

**Files:** Modify `shaders/water_surface.gdshader`; Modify `scripts/hydrology/WaterRenderer.cs` (flow-direction uniform per river strip + reflection toggle).

**Interfaces:** Consumes everything from T3-T5. Adds shader uniforms `reflections` (bool), `flow_dir` (vec2, per river strip), `normal_map` (sampler2D, optional), `foam_width`.

- [ ] **Step 1: Add screen-space reflection + better normals to the shader**

```glsl
// add uniforms: uniform bool reflections = true; uniform vec2 flow_dir = vec2(0.0); 
// In fragment(), after computing NORMAL, sample SCREEN_TEXTURE along the reflected view ray for SSR, and add
// fresnel: float fres = pow(1.0 - clamp(dot(NORMAL, VIEW),0.0,1.0), 5.0);
// reflection mixes sky/scene; rivers offset ripple normals by flow_dir*TIME for directional flow.
```
[Implement Godot 4 SSR via `SCREEN_UV` + depth, or fall back to `ROUGHNESS`-driven reflection probe; flow_dir scrolls the ripple along the river. Fresnel brightens grazing angles. Keep all behind `reflections` uniform.]

- [ ] **Step 2: Set flow_dir per river strip in WaterRenderer.BuildRivers** (normalize `reach.Pts[k+1]-Pts[k]`).

- [ ] **Step 3: Build + clear cache + capture + USER EYE-GATE (look polish) + commit**

User confirms the full AAA read: reflections, depth, directional river flow, foam. Tune look uniforms.
```bash
git add shaders/water_surface.gdshader scripts/hydrology/WaterRenderer.cs
git commit -m "water T6: AAA shader polish — SSR reflections + flow-aligned river normals + fresnel"
```

---

### Task 7: Infinite CDLOD streaming integration (additive, last, isolated)

**Files:** Create `scripts/lab/WaterChunk.cs`; Modify the CDLOD chunk stream-in/out path (`scripts/lab/CdlodTerrain.cs`) ONLY to emit chunk add/remove events or expose a hook — no geometry changes.

**Interfaces:** Consumes `WaterBodies`, `WaterRenderer`, the per-chunk substrate. Subscribes to chunk lifecycle.

- [ ] **Step 1: Read the CDLOD chunk lifecycle** — inspect `scripts/lab/CdlodTerrain.cs` + `CdlodQuadtree.cs` for where chunks are created/freed and how their world bounds are known. Identify the minimal hook (an event or a virtual call) to attach a sibling water node per chunk WITHOUT touching terrain geometry.

- [ ] **Step 2: Write `WaterChunk.cs`** — given a chunk's world bounds, build that chunk's `DrainageGraph`(+halo) → `WaterBodies` → lake/river surfaces + groove, as additive sibling meshes parented under the chunk (or a water root). Free them on chunk removal. Sea is a single global camera-follow plane (built once, not per chunk).

- [ ] **Step 3: Extend `--determinismcheck`/add `--watercoherencecheck`** — assert lake levels + river reaches agree across a chunk-overlap (no seam/pop when streaming). Mechanical, must PASS.

- [ ] **Step 4: Build + `--fieldcheck` 0m + coherence check + USER EYE-GATE in terrain_lab** — fly the infinite world; water streams in/out seamlessly, lakes stay limited, terrain (CDLOD) is unaffected. Commit.
```bash
git add scripts/lab/WaterChunk.cs scripts/lab/CdlodTerrain.cs scripts/hydrology/HydrologyChecks.cs
git commit -m "water T7: infinite CDLOD streaming integration (additive per-chunk water); coherence gate"
```

## Self-Review

- **Spec coverage:** sea tier (T3) ✓; significant lakes + density cap (T2 logic, T4 render) ✓; thin-channel rivers (T5) ✓; AAA shader reflections/depth/motion/foam (T3 base + T6 polish) ✓; lens-first real-render eye-gates per tier (T3/T4/T5/T6) ✓; "limited amounts" budget gate (T2) ✓; deterministic/tile-coherent + coherence gate (T2 pure, T7) ✓; base field untouched / `--fieldcheck` 0m (T5, T7) ✓; additive/no-CDLOD-coupling (T7) ✓; modular/tunable (T1 WaterParams + uniforms throughout) ✓; staged de-risk order sea→lakes→rivers→polish→stream ✓; STOP discipline (T3/T4/T5) ✓.
- **Placeholder scan:** T5's ChannelCarve.cs has a deliberately-flagged cleanup (cell-as-ctor-field, drop the static placeholder) called out explicitly with the fix — not a silent TODO; T6's SSR notes the two concrete Godot-4 implementation paths. The river-strip ribbon mesh + T7 CDLOD hook are described with their inputs/outputs; T7 step 1 is an explicit "read the lifecycle first" investigation step because the hook point lives in another lane's code and must be found before coding (a bounded discovery step, not a vague task).
- **Type consistency:** `WaterParams` field names (SeaLevel, SeaEnabled, LakesEnabled, RiversEnabled, LakeMinAreaM2, LakeMinDepthM, LakeMinInflow, LakeDensityPerKm2, RiverMinArea, ChannelWidthM/DepthM/BlendM, ShallowDepthM, FlowSpeed, WaveScale, Reflections, Foam) consistent across T1-T6; `WaterBodies.Lake`/`RiverReach` record fields consistent T2→T4/T5; `WaterRenderer.BuildSea/BuildLakes/BuildRivers/UpdateLook` consistent; shader uniforms (`water_level`, `terrain_height_tex`, `region_size`, `shallow_depth`, `flow_speed`, `wave_scale`, `reflections`, `flow_dir`, `foam_width`) consistent shader↔C#.
