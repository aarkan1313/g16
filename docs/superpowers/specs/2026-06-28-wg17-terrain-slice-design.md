# WG17 Terrain Engine — Base Geometry Slice (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k` (bare Godot 4.6 / Forward+ project, no C# yet)
**Reference:** `C:\Wg16\wg-16-project` (WG16) + its audit `docs/MIGRATION-AUDIT-2026-06-28.md`
**Scope:** First migration slice — **base geometry only** (procedural Field + CDLOD infinite terrain).
Clouds, lighting, water, materials are explicitly **out of scope** for this slice.

---

## 1. Goal & Success Criteria

Re-create WG16's base geometry in WG17 as a **focused port-clean rewrite**: bring over the *proven*
CDLOD architecture (quadtree + identity-keyed pool + GPU field cache + floating-origin snap), rewriting
each file cleaner as we go, but keep the proven design. Get it on screen and flying first, profile, then
attack known hotspots.

**Success criteria (this slice):**
1. **Parity:** recognizable infinite procedural terrain that streams and flies, with no cracks, no LOD
   pops, no precision wobble — matching WG16's terrain look/behavior.
2. **Cleaner:** the GPU-compute binding is quarantined into clearly-named classes; the LOD brain is pure
   logic; consumers reach the field only through `IHeightSource`.
3. **At least as fast** as WG16's terrain at equivalent settings, measured by the profiler — and ideally
   faster once Slice 4 lands.

**Stretch / north star (NOT a hard gate this slice):** clean view distance toward **~10 km** (the repo's
"10k" codename; also WG16's known weak point per the `terrain-viewdistance-deeptune` history). We pick
concrete LOD-depth / load-ring numbers as the profiler tells us, not upfront.

**Non-goals this slice:** materials/surfacing (ship the height-color placeholder from `ground.gdshader`),
water carving (the `SetWaterDelta` hook stays present but unwired), clouds, lighting composition, the lab
harness / param-registry UI.

---

## 2. Architecture & Repo Layout

Layering mirrors the audit's findings, with the **GPU quarantine baked into the directory structure** so
it cannot erode over time:

```
terrainengine-10k/
├── Terrainengine10k.csproj      # Godot.NET.Sdk/4.6.2, net8.0, <Nullable>enable</Nullable>,
│                                #   <LangVersion>latest</LangVersion>, EnableDynamicLoading, no NuGet
├── project.godot                # + "C#" feature in config/features; main_scene = res://scenes/terrain.tscn
├── scenes/
│   └── terrain.tscn             # Root(Node3D) > Terrain, Camera(Camera3D+FlyCamera), Sun(DirectionalLight3D), Env(WorldEnvironment)
├── src/
│   ├── core/
│   │   ├── IHeightSource.cs      # THE SEAM (see §3). terrain ↔ field decoupling.
│   │   └── FlyCamera.cs          # free-fly camera (port of scripts/workbench/FlyCamera.cs)
│   ├── field/
│   │   ├── FieldParams.cs        # 30-field config record + Load() from JSON + Spacing
│   │   └── FieldCompute.cs       # implements IHeightSource; RenderingDevice lives ONLY here
│   ├── terrain/
│   │   ├── CdlodQuadtree.cs      # PURE logic — math types only, zero Godot scene/RD types
│   │   ├── CdlodMesh.cs          # BuildGrid(n) + BuildStitchedVariants[16] (edge-weld) — static
│   │   ├── ChunkFieldCache.cs    # GPU-quarantined; depends on IHeightSource, NOT FieldCompute
│   │   └── Terrain.cs            # scene façade (role of WG16 TerrainLab): Build/Tick/toggles
│   └── checks/                  # ported self-checks = our test suite (see §6)
├── shaders/
│   ├── field_math.gdshaderinc   # shared field math; FieldP struct; the single source of truth
│   ├── field_height.glsl        # compute shell; splices field_math via // @@INCLUDE
│   ├── field_bake.glsl          # per-chunk height+normal bake; splices field_math
│   └── ground.gdshader          # spatial shader: analytic-vs-baked toggle, geomorph, cache sampling
└── data/
    └── field_params.json        # 30 field knobs (1:1 with FieldParams and fp_* uniforms)
```

**Layering rules (enforced by review, not just convention):**
- `CdlodQuadtree` may use `Vector2/Vector3/Mathf` (cosmetic Godot math) but **no** `Node`, `MeshInstance3D`,
  `RenderingDevice`, or `RenderingServer`.
- `RenderingDevice` / `RenderingServer` / `CallOnRenderThread` appear **only** in `FieldCompute.cs` and
  `ChunkFieldCache.cs` — nowhere else.
- `Terrain.cs` is the only class that touches the scene graph (`AddChild`, `MeshInstance3D`, `ShaderMaterial`).
- Consumers of height data depend on `IHeightSource`, never on `FieldCompute` concretely.

---

## 3. The IHeightSource Seam

Validated against WG16's *actual* erosion data flow: `FieldHeightSource.Bake` and `ChunkFieldCache` both
need exactly one primitive — a square page of heights at a world origin. Erosion's entire field dependency
is the single call `FieldCompute.ProducePage(p, origin, spacing, gridN, fieldMode)` → `float[]`; everything
else in `Erosion.Core` is pure array math. So the seam is concrete, not speculative:

```csharp
public interface IHeightSource
{
    // Square page of heights, row-major (z*res + x), at the given world origin.
    // fieldMode 0 = full composite field; 1..N = debug single-layer views.
    float[] ProducePage(float originX, float originZ, float spacing, int res, uint fieldMode = 0);
}
```

- `FieldCompute : IHeightSource` (and keeps `PackParamsBytes` as the shared std430 packer).
- `ChunkFieldCache` takes an `IHeightSource` in its constructor.
- Later, water's region solver takes the same `IHeightSource` — **zero seam rework** when water migrates.

---

## 4. Shader / std430 Contract (the fragile part — port verbatim, then validate)

This is the single most fragile thing in the migration. C# interfaces cannot abstract a GPU shader's input
layout, so we port these **byte-exact** and guard with checks.

- **`field_math.gdshaderinc`** is the one source of field math, consumed three ways:
  - `ground.gdshader`: native `#include "res://shaders/field_math.gdshaderinc"`.
  - `field_height.glsl` / `field_bake.glsl`: a `// @@INCLUDE field_math` marker, **text-spliced at runtime**
    by the C# loader (RD-GLSL has no `#include`). All three splice paths must be verified after porting.
- **`ParamsBuf`** (compute push-constant SSBO) is **exactly 128 bytes std430**, packed by
  `FieldCompute.PackParamsBytes()`. `field_bake.glsl` adds a `GridBuf` (`grid_spacing` float + `layer` int).
  - **Critical invariant:** `ParamsBuf.spacing` is the LOD-independent field octave gate; `GridBuf.grid_spacing`
    is chunk vertex spacing for positioning only. Conflating them breaks determinism.
- **`ground.gdshader` uniforms** (119 total in WG16; the field-relevant subset is what this slice wires):
  - Field params: `fp_*` (base_freq, octaves, lacunarity, gain, amplitude, cont_*, uplift_*, macro_*,
    hill_damp, ridge_*, mtn_*, grain_stretch, massif_*, foothill_*), `analytic_seed`, `analytic_spacing`,
    `fp_field_mode` — fed from `FieldParams` in `Terrain.Build()`.
  - Source select: `use_analytic`, `heightmap`, `region_size`, `texel_world`.
  - Chunk mode: `use_chunk`, `cam_world`, `grid_n`, `split_factor`, `lod_viz` (instance), `render_origin`.
  - Field cache: `chunk_cache` (sampler2DArray), `cache_side`, `chunk_slot` (instance), `cache_ready` (instance).
  - Water hook (present, unwired this slice): `water_tex`, `water_region_origin`, `water_region_m`, `water_carve_on`.
  - Material/debug uniforms are ported as-is but driven only by the placeholder height-color path; surfacing
    is a later slice.

`FieldParams` is a 30-field record; `data/field_params.json` maps 1:1. `Load()` uses graceful fallbacks
(missing key → default), same pattern as WG16.

---

## 5. Build Order (each slice ends at a see-able eye-gate + a profile checkpoint)

Slices 1–3 are port-clean **parity**. Slice 4 earns "faster" with profiler data, not guesses.

| Slice | Build | Checkpoint |
|---|---|---|
| **0. Bootstrap** | `.csproj`; add "C#" feature; minimal `terrain.tscn` (Root / Camera+FlyCamera / Sun / Env); empty `Terrain` node clearing to sky. First git commit. | App launches; fly around empty scene. **Profile:** baseline empty-frame ms. |
| **1. Field + single mesh** | Port `FieldParams`, `FieldCompute`(+`IHeightSource`), `field_math.gdshaderinc`, `field_height.glsl`, `ground.gdshader`. One static `PlaneMesh` displaced by the field. **De-risks the shader/std430 seam first.** | **Eye-gate:** recognizable terrain, not flat/garbage. **Profile:** single-mesh ms. **Check:** port `FieldCheck` (GPU determinism). |
| **2. CDLOD streaming** | Port `CdlodQuadtree` (pure), `CdlodMesh` (grid + 16 stitch variants), `Terrain` façade driving `CdlodTick(camPos, vel)`. Live field eval in shader (no cache yet). | **Eye-gate:** fly out — streams in, LOD morphs, no cracks/pops. **Profile:** flying ms. **Checks:** `StreamCheck`, `MorphCheck`, `StitchCheck`. |
| **3. GPU field cache + floating origin** | Port `ChunkFieldCache` (GPU-quarantined, via `IHeightSource`) + `field_bake.glsl` + `render_origin` snap (snap interval = region size). | **Eye-gate:** fly far toward 10 km — no precision wobble / snap pop. **Profile:** measure hard (WG16's −33% win AND its 54 ms failure both live here). **Checks:** `PopCheck`, `SnapDiff`. |
| **4. Optimize hotspots** | With profiler data in hand, attack known WG16 baggage: identity-keyed pool (not index-keyed — avoids the ~474 BVH re-fits/frame churn), birth-budget vs field-cache race, ring/lookahead CPU floor. | **Profile-driven:** beat WG16 flying ms; push view distance toward 10 km. |

---

## 6. Testing Strategy

WG16 has **no unit tests** — its CLI self-check gates ARE the test suite, and they guard exactly the fragile
seams. We port them onto WG17's minimal driver (a few CLI flags on the `Terrain` node / a debug autoload),
not onto a harness:

- **`FieldCheck`** — same params produce byte-identical heights (std430 packing + splice correctness).
- **`StreamCheck`** — neighbor invariant + renderOrigin snap field-continuity while traversing.
- **`MorphCheck`** — geomorph C0-continuity (pop-free morph 0→1).
- **`StitchCheck`** — welded fine-edge verts match coarse neighbor lattice (crack-free).
- **`PopCheck`** — GPU ground-truth height/normal jumps at LOD swap.
- **`SnapDiff`** — fixed points resolve to identical leaf across a renderOrigin snap.
- **`LivePopMeter`** (optional HUD) — live per-frame pop/snap metering while flying.

Each is a headless-ish CLI gate that prints PASS/FAIL and quits with an exit code. They run windowed (the
local `RenderingDevice` needs a render context — see the `headless-no-local-rendering-device` constraint).

**Per-slice discipline:** quick eye-gate (look at it, in motion) + a profile number, then move on. Don't
tune a slice past "looks right + isn't slower"; real optimization is Slice 4.

---

## 7. Known Risks & Constraints

1. **Shader/std430 contract** — highest risk. Mitigation: port byte-exact; `FieldCheck` guards it first thing in Slice 1.
2. **`field_math.gdshaderinc` triple-splice** — verify all three consumers compile and agree after porting.
3. **Floating-origin snap** — Slice 3 is where WG16 had both its win and its pop bug (`cdlod-renderorigin-snap-pop`); `SnapDiff` guards it.
4. **Headless RenderingDevice** — `FieldCompute` uses a local RD; bakes need a windowed run, not `--headless`.
5. **Stale C# DLL** — Godot does not rebuild C# on launch; `dotnet build` after every `.cs` edit (the `wg16-csharp-stale-dll-gotcha`). Shaders DO hot-compile (misleading).
6. **Launch path** — launch with an absolute `--path` to the project, not `.` (the `wg16-launch-absolute-path` gotcha).
7. **No over-abstraction** — staying on Godot, do NOT wrap `Node3D`/`MeshInstance3D` behind interfaces; direct use is correct. The only seam is `IHeightSource`.

---

## 8. Definition of Done (this slice)

- WG17 launches to infinite procedural terrain, flies smoothly, streams with no cracks / pops / wobble out
  to a large distance (10 km targeted, parity-or-better confirmed by eye + profiler).
- All ported self-checks pass.
- GPU binding quarantined; `IHeightSource` is the only field seam; `CdlodQuadtree` is pure.
- Profiler shows ≥ parity with WG16 terrain; Slice 4 hotspot work logged with before/after numbers.
- Clean git history in the WG17 repo from commit 0.
