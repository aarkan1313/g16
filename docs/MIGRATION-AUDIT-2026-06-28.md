# WG16 Migration Audit — Module-by-Module Extraction Inventory

**Date:** 2026-06-28
**Scope:** Full deep audit of `C:\Wg16\wg-16-project` to plan an incremental, one-module-at-a-time
re-creation in a **fresh Godot 4.6 / C# repo**.
**Decision context:** Target stays Godot 4.6. Expect significant rewrites per module — this doc is the
"what we have / what it should do / what it relies on" inventory so each module can be redone deliberately,
one at a time. **First module to migrate: base geometry (procedural Field + CDLOD infinite terrain).**

---

## 0. Executive Summary

The project is **better-positioned for incremental migration than its history suggests.** It already has:

- Clean **namespace separation** (`WG16.Lab`, `Erosion.Core`, `WG16.Water`, `WG16.Field`, `WG16.Board`, `WG16.Workbench`).
- **No autoload singletons** — no hidden global state to untangle.
- Cross-subsystem communication mostly via **data + shader uniforms**, not direct code references.

**The entanglement is concentrated in exactly one place: the lab harness (`TerrainLabUI`, ~4,100 LOC).**
That harness is the param-registry dispatcher, the UI panel builder, the CLI/screenshot automation, and the
holder of every subsystem reference. **We do NOT migrate the harness.** Each module gets a small, purpose-built
driver in the new repo. Subsystems reach the harness through two clean interfaces — `ILabControls` and
`ILightingHost` — and those interfaces are the seams.

### Project facts
- **Build:** `Godot.NET.Sdk/4.6.2`, `net8.0`, `Nullable=enable`, `LangVersion=latest`, assembly name `WG16`, **no NuGet packages**.
- **Renderer:** Forward+, D3D12 on Windows, MSAA 2x, Jolt physics (unused by terrain).
- **Code size:** ~11,600 LOC C# across 188 files + 45 shader files. 711 commits.
- **Main scene:** `scenes/terrain_lab.tscn` (everything is a child of one `TerrainLabRoot`).

### Module verdicts at a glance

| Module | Migrate? | Verdict | Rough cost |
|---|---|---|---|
| **Field** (`WG16.Field`) | **1st** | Clean — the foundation everything sits on | ~1 day |
| **Terrain / CDLOD** | **1st (with Field)** | Code-clean, Godot-bound; co-bundle with Field | 2–4 days |
| **Water / Erosion** | later | Cleanest module (21/24 files pure C#); one `IHeightSource` seam | 1–2 days |
| **Clouds / Atmosphere / Godrays** | later | Moderately entangled; GPU plumbing is the work | 2–4 days |
| **Lighting / Sky / Luminary** | last | Pure-data states port free; rewrite the host→target adapter | 3–5 days |
| **Material Board / Workbench** | optional | Standalone tools, fully decoupled | <1 day each |

---

## 1. Architecture Map

```
                         ┌─────────────────────────────────────────┐
                         │  TerrainLabUI  (HARNESS — DO NOT MIGRATE) │
                         │  param registry · UI panels · CLI · A/B   │
                         └───────┬───────────────────────┬──────────┘
                  ILabControls   │                       │  ILightingHost
            ┌────────────────────┼───────────────────────┼───────────────┐
            ▼                    ▼                       ▼               ▼
      CloudVolume          TerrainLab (façade)     LightingComposer    SkyPresets
      AtmosphereCompute    └─ CdlodTerrain          └─ Luminary         (presets)
      GodRaysScreen           CdlodQuadtree            LightingState
      AerialPerspectiveV2     ChunkFieldCache
                              ChunkAabbProvider
                                    │
                                    ▼
                              FieldCompute / FieldParams   ◄── (also used by Water)
                                    ▲
                              RegionWaterSolver (Erosion.Core + WG16.Water)
```

**Two hubs decide everything:**

1. **`FieldCompute` / `FieldParams` (`scripts/field/`)** — the GPU heightfield generator. Terrain, water,
   clouds (camera ref only), and every terrain self-check read it. **Migrate first; freeze its API.**
   Referenced by: `CdlodTerrain`, `ChunkAabbProvider`, `ChunkFieldCache`, `TerrainLab`, `LivePopMeter`,
   `PopCheck`, `CloudVolume`, `FieldHeightSource`, `RegionWaterSolver`, `TerrainLabUI*`.

2. **`TerrainLabUI` (the god-host)** — 27 files reference it. It is scaffolding, not product. **Leave behind;
   build a minimal driver per module.**

---

## 2. MODULE 1 — Base Geometry: Field + CDLOD (MIGRATE FIRST)

### 2.1 What it should do
Render an **infinite, procedurally-generated heightfield** via CDLOD: a quadtree selects per-chunk LOD by
camera distance; chunks stream in/out of a pooled set of `MeshInstance3D`s; per-chunk height+normal is baked
on the GPU into a texture-array cache on chunk birth (no readback); floating-origin render-space snapping keeps
float precision bounded out to large distances. Sits on top of a static analytic field (`field_math.gdshaderinc`)
that the same code evaluates both on GPU compute (baking) and in the vertex/fragment shader (live eval fallback).

### 2.2 File manifest

**CORE (must migrate — ~1,720 LOC + shaders):**

| Path | LOC | Role |
|---|---|---|
| `scripts/field/FieldParams.cs` | 96 | 30-field config record + `Load()` from JSON + `Spacing` derived prop |
| `scripts/field/FieldCompute.cs` | 142 | Sync local-RD dispatcher for `field_height.glsl`; `ProducePage()`; `PackParamsBytes()` std430 serializer |
| `scripts/lab/CdlodTerrain.cs` | 529 | LOD select + instance pool + async AABB/cache + floating-origin snap; `Tick()`/`Setup()` |
| `scripts/lab/CdlodQuadtree.cs` | 198 | Pure quadtree traversal (no Godot); `SelectRoaming()`, `NeighborInvariantHolds()` |
| `scripts/lab/CdlodMesh.cs` | 75 | `BuildGrid(n)` + `BuildStitchedVariants[16]` (edge-weld variants) — static |
| `scripts/lab/ChunkAabbProvider.cs` | 173 | Async GPU per-chunk height-range probe (render thread); `Request`/`TryTake`/`Pump` |
| `scripts/lab/ChunkFieldCache.cs` | 180 | Async GPU per-chunk height+normal bake → Texture2DArray; `Request`/`TryTake`/`Pump`/`Tex` |
| `scripts/lab/TerrainLab.cs` | 300 | **Façade.** `Build(fc, params)`; single-mesh fallback; A/B toggles; shader param pass-through; GI proxy |
| `shaders/field_math.gdshaderinc` | 147 | **Shared field math** (continent+uplift+hills+ridges+macro); `FieldP` struct; spliced into 3 consumers |
| `shaders/field_height.glsl` | 49 | Compute shell (8×8 wg); splices field_math; writes `h[]` buffer |
| `shaders/field_bake.glsl` | 70 | Compute shell; splices field_math; `imageStore` → Texture2DArray layer |
| `shaders/ground.gdshader` | 680 | Spatial shader: analytic-vs-baked toggle, chunk geomorph, cache sampling, materials, water-carve hook |
| `data/field_params.json` | — | 30 field knobs (single source of truth; 1:1 with `FieldParams` and `fp_*` uniforms) |

**DIAGNOSTIC (optional — ~700 LOC; recommended to port for validation):**
`scripts/field/FieldCheck.cs` (28, GPU determinism), `TerrainTestPaths.cs` (107, LOD-crossing flights),
`StreamCheck.cs` (52), `PopCheck.cs` (133), `MorphCheck.cs` (91), `StitchCheck.cs` (87), `SnapDiff.cs` (70),
`LivePopMeter.cs` (153), `data/terrain_test_paths.json`.

**LEAVE BEHIND:** `lab_terrain.gdshader` (old layered presenter, superseded by `ground.gdshader`);
material/asset loading paths in `TerrainLab.LoadGroundMaterials()` (handle surfacing as its own later pass).

### 2.3 Public API surface (the façade contract to re-expose)

**`TerrainLab` (MeshInstance3D façade):**
`Build(FieldCompute, FieldParams)` · `SetCdlod(bool)` · `SetAnalytic(bool)` · `SetLodViz(bool)` ·
`SetFieldCache(bool)` · `SetBakeReq(int)` · `SetChunkOps(int)` · `SetLoadRing(int)` ·
`SetCdlodLookahead(float)` · `ConfigureCdlodAabb(tighten, probeRes, maxReq)` · `SetCdlodAabbSpeed(float)` ·
`CdlodTick(camPos, velXZ)` · `Cdlod` (accessor) · `SetGiProxy(bool)` · generic `SetFloat/Int/Bool/Color/Vector3/Texture/CameraWorld`.

**`CdlodTerrain` (Node3D):** `Setup(mat, params, minH, maxH, heights)` · `Tick(camPos, velXZ)` ·
`SetEnabled/SetLodViz/SetBakeReq/SetCastShadows` · `SetWaterDelta(tex, origin, m, on)` (water hook) ·
`RenderOrigin` · diagnostics: `LeafCountLastTick`, `InvariantHoldsNow`, `ActiveCount`, `TotalSnaps/Births/Rebirths`.
Tunables: `GridN, MaxDepth, SplitFactor, LoadRing, ActiveRing, CenterHysteresis, PredictLookahead, TightenAabb, FieldCache, PinOrigin`.

**`CdlodQuadtree` (pure):** `SelectRoaming(camPos[, centerCellOrigin])` · `LeafSizeAt(...)` ·
`NeighborInvariantHolds(...)` · `Ring` · static `SelfCheck(...)`. **Fully headless-testable.**

**`FieldCompute`:** `ProducePage(params, originX, originZ, spacing?, res?, fieldMode) → float[]` ·
`PackParamsBytes(...) → byte[]` (the exact 128-byte std430 layout — shared with the providers).

### 2.4 Shader uniform contract — THE MOST FRAGILE PART OF THE MIGRATION

`ground.gdshader` consumes ~60 uniforms in five groups. Get the contract wrong and terrain renders silently
flat/garbage. The full list is in §2.4-detail of the explorer pass; the structure:

1. **Field params** (`fp_base_freq, fp_octaves, fp_lacunarity, fp_gain, fp_amplitude, fp_cont_*, fp_uplift_*,
   fp_macro_*, fp_hill_damp, fp_ridge_*, fp_mtn_*, fp_grain_stretch, fp_massif_*, fp_foothill_*,
   analytic_seed, analytic_spacing, fp_field_mode`) — fed from `FieldParams` in `TerrainLab.Build()`.
2. **Source select** (`use_analytic, heightmap, region_size, texel_world`).
3. **Chunk mode** (`use_chunk, cam_world, grid_n, split_factor, lod_viz` [instance], `render_origin`).
4. **Field cache** (`chunk_cache` [sampler2DArray], `cache_side, chunk_slot` [instance], `cache_ready` [instance]).
5. **Materials** (`mat{0..4}_alb/nrm/rgh/ao, use_textures, mat_tiling`, color ramps, height/slope bands).
6. **Water carve hook** (`water_tex, water_region_origin, water_region_m, water_carve_on`).

**Compute push-constant contract** (`field_height.glsl` / `field_bake.glsl`): a single `ParamsBuf` SSBO of
**exactly 128 bytes std430**, packed by `FieldCompute.PackParamsBytes()`. `field_bake.glsl` adds a `GridBuf`
(`grid_spacing` + `layer`). **Critical:** `ParamsBuf.spacing` is the LOD-independent field octave gate;
`GridBuf.grid_spacing` is chunk vertex spacing for positioning only — conflating them breaks determinism.

**Splice mechanism:** `field_math.gdshaderinc` is the single source of field math, consumed three ways:
- `ground.gdshader` line 2: native `#include "res://shaders/field_math.gdshaderinc"`.
- `field_height.glsl` / `field_bake.glsl`: a `// @@INCLUDE field_math` marker, text-spliced at runtime by the
  C# loader (RD-GLSL has no `#include`). **Verify all three paths after migration.**

### 2.5 External dependencies & how to sever them
Good news: the core terrain code has **no references to `TerrainLabUI`, `ILabControls`, or `CloudVolume`**.
- `CdlodTerrain.SetWaterDelta(...)` — passive receiver; leave unimplemented in the new repo until water lands.
- `TerrainLab.LoadGroundMaterials()` — hardcoded `res://assets/materials/...` paths; **drop for the first slice**,
  re-introduce surfacing as a deliberate later pass.
- Diagnostics take `Camera3D` / `CdlodTerrain` / `FieldCompute` by **injection** — clean.

### 2.6 RenderingDevice / headless behavior
- `FieldCompute.ProducePage()` uses a **local RD, synchronous** — **fails under strict `--headless`**
  (no local RenderingDevice; see memory `headless-no-local-rendering-device`). Run windowed to bake.
- `ChunkAabbProvider` / `ChunkFieldCache` dispatch on the **render thread** via `CallOnRenderThread`, and
  **degrade gracefully** when no RD (generous AABB / live field eval) — just slower.

### 2.7 First-slice plan for the new repo
1. New Godot 4.6 repo, same `.csproj` (`Godot.NET.Sdk/4.6.2`, net8.0, nullable, no packages), assembly name of your choice.
2. Minimal scene: `Root(Node3D)` → `TerrainLab(MeshInstance3D)`, `Camera(Camera3D + FlyCamera)`, `Sun(DirectionalLight3D)`, `Env(WorldEnvironment)`.
3. Copy core files + 4 shaders + `field_params.json`. Re-path `res://shaders/...`.
4. Tiny driver (no harness): `_Ready` builds Field + TerrainLab; `_Process` calls `CdlodTick(cam.pos, vel)`.
   Optionally a handful of CLI flags (`--cdlod`, `--lodviz`, `--loadring`, `--popmeter`, `--profile`).
5. Port the self-checks (`FieldCheck`, `StreamCheck`, `PopCheck`, `MorphCheck`, `StitchCheck`, `SnapDiff`) — these
   are the closest thing to a test suite and validate the fragile seams (std430 packing, splice, snap, cache layers).

---

## 3. MODULE 2 — Water + Erosion + Hydrology (cleanest; migrate when ready)

**What it should do:** Hydraulic droplet erosion + thermal/talus slump carve the field; priority-flood hydrology
(breach-then-fill, Barnes hybrid) computes drainage; output is per-region carved terrain + flow/lake/river data,
keyed to true-world origin. (See memory `drainage-conditioning-shipped`, `erosion-lab-water-proven`.)

**Manifest:** `scripts/water/**` (24 files). 21 of 24 are **pure C# `Erosion.Core`** (zero Godot): `Hydrology.cs`
(211), `ErosionPipeline`, `DropletErosionCpu`, `ThermalErosionCpu`, `FlowField`, `ChannelCarve`, `RiverNetwork`,
`WaterBodies`, `HeightField`, `WaterData`, `WaterParams`, `ErosionParams`, `IEroder`, `Smoothing`.
Godot-bound (3): `GpuErosion.cs` (local-RD compute), `WaterDeltaTexture.cs`, `RegionDebugViz.cs`.
Entry point: `RegionWaterSolver.GetOrSolve/BuildDelta/Diagnose`. Shaders: `droplet_erosion.glsl`,
`thermal_erosion.glsl`, `water_surface.gdshader`. Config: `data/water_params.json`.

**Relies on / seams:**
- **`FieldCompute`/`FieldParams`** via `FieldHeightSource` — the one real knot. Sever with an `IHeightSource`
  interface (~3 hrs) so water can be driven by Field, a noise lib, or disk.
- Local RD in `GpuErosion` — wrap creation or swap to `CpuEroder` for headless.
- **No unit tests** despite pure-C# core — worth adding when migrating.

---

## 4. MODULE 3 — Clouds + Atmosphere + Godrays (later)

**What it should do:** Volumetric clouds (Perlin-Worley raymarch into a hemisphere texture, amortized over frames),
physical atmosphere (Hillaire LUTs), screen-space aerial perspective, and screen-space radial god rays.
(See memories `volumetric-clouds-research`, `godray-emission-vs-albedo-rootcause`, `cloud-lighting-model`.)

**Manifest (CORE):** `CloudVolume.cs` (775), `AtmosphereCompute.cs` (468), `CloudNoiseCompute.cs` (187),
`AerialPerspectiveV2.cs` (50), `GodRaysScreen.cs` (121), data classes `CloudParams/CloudLayers/CloudWeather/CloudPresets`.
Diagnostic: `CloudLightCheck.cs`. Shaders: `cloud_sky.gdshader`, `cloud_raymarch.glsl`, `cloud_noise_3d.glsl`,
`atmosphere_{transmittance,multiscatter,skyview,aerial_v2,cloudlight}.glsl`, `aerial_screen_v2.gdshader`,
`godray_screen.gdshader`, `cloud_density.gdshaderinc`. Config: `data/cloud_{params,layers,presets}.json`.

**Relies on / seams:**
- ~30 setter methods called by the host/lighting (sun, moon, sky colors, overcast, extra suns). Collapse to a
  single `SetState(ICloudState)` per frame.
- Owns/swaps the **Sky material** — inject it instead.
- `CallOnRenderThread` + `RenderingDevice` + `Texture2Drd` + GLSL→SPIR-V — Godot-native; staying on Godot 4.6 means
  this is **copy, not rewrite** (the rewrite risk only applies if leaving Godot — which we are not).
- `CloudPresets` routes through `ILabControls` — make it take an `IPresetApplier`.

**Note:** terrain reads `CloudVolume.Overcast()` (dims sun) and `SkyHorizonColor` (haze tint). These are *outbound
from clouds*, so they don't block terrain migrating first — terrain just won't dim until clouds arrive.

---

## 5. MODULE 4 — Lighting + Sky + Luminary (migrate last; it's the integrator)

**What it should do:** Compose the full day/night cycle and celestial appearance from orthogonal axes
(Time × Weather × Grade) plus an N-luminary model (suns/moons with a priority budgeter for scarce hardware:
4 physical lights, 3 atmosphere suns). Decoupled in "C3 Unit 1". (See memories `sun-light-arc`, `lighting-refactor-plan`.)

**Manifest (CORE):** `LightingComposer.cs` (655), `LightingState.cs` (193, **pure data**), `Luminary.cs` (144, **pure data**),
`SkyPresets.cs` (225). Diagnostic: `LuminaryCheckRunner`, `LuminaryPresetCheck`, `ShadowDiagnostics`.
Config: `data/{time,weather,grade,sun,celestial,fantasy}_presets.json`, `lighting_moods.json`, `luminaries.json`.

**Relies on / seams (the heaviest rewrite):**
- **Hardcoded scene paths** `/root/TerrainLabRoot/{Env,Sun}` baked into `LightingComposer` — parameterize.
- **`ILightingHost`** callback loop into `TerrainLabUI` (10 members — see §6). Replace with an injected
  `ILightingTarget` that abstracts `Environment`/`DirectionalLight` writes; composer emits an immutable
  `ComposedState`, the host applies it.
- Pure-data states (`TimeState/WeatherState/GradeState/SunDiscState/MoonState/StarsState`, `Luminary*`) **port for free.**
- Writes to clouds/atmosphere via setters — depends on Module 3 existing (hence: last).

---

## 6. The Seams — interfaces a new-repo driver must reimplement (minimally)

**`ILabControls`** (registry façade; satellites depend only on this, not the god-class):
```csharp
bool IsReady { get; set; }                              // suppress callbacks during batch apply
IReadOnlyList<LabControl> Controls { get; }
IReadOnlyDictionary<string, LabControl> ById { get; }   // keyed "id" or "id#z" (zone-expanded)
bool TryGet(string id, out LabControl c);
void SetValue(LabControl c, Variant v);                 // canonical path → ApplyControl dispatch
void SetValueSilent(LabControl c, float v);             // display-only (day-cycle slider sync)
```

**`ILightingHost`** (decouples composer from the UI):
```csharp
Node SceneOwner { get; }              CloudVolume? Cloud { get; }
float Overcast { get; }               bool AtmosphereOn { get; }
bool VolumetricFogOn { get; }         float CdlodViewDistance { get; }
float FogViewScale { get; }           AtmosphereCompute? Atmosphere { get; }
TerrainLab? Terrain { get; }
void OrientSun(DirectionalLight3D sun);   void SyncLightControlsToScene();
```

**Param registry mechanism (what a minimal driver must replicate, or skip):**
`data/lab_controls.json` (~147 controls) → `LabControl[]` (with zone/`#z` expansion) → `ApplyControl(c)` switch
that routes by `Type`: `slider/enum/toggle` → terrain shader `SetFloat/Int/Bool`; `cloud*` → `CloudVolume`;
`scene*/scenecolor` → scene nodes; `objectlist` → `ObjectListControl`. Presets round-trip as text-key → Variant
maps (Color → `[r,g,b]`), replayed through `SetValue`. **For a single-module driver you can skip all of this and
just call the module's setters directly** — only replicate the registry if you want the live-tuning UX.

---

## 7. Recommended migration order

```
1. Field            ← foundation; freeze its API + the std430/shader contract first
2. Terrain / CDLOD  ← co-bundle with Field; the visual backbone (THIS IS THE CURRENT FOCUS)
3. Water / Erosion  ← cleanest; proves the IHeightSource seam
4. Clouds / Atmos   ← stub lighting inputs; pure copy since staying on Godot
5. Lighting / Sky   ← last; rewrite host→target adapter; it integrates the rest
   + Material Board / Workbench whenever (independent)
```

## 8. Cross-cutting risks to watch every migration

1. **Shader contracts** — external `.gdshader`/`.glsl` text + the `field_math.gdshaderinc` splice + std430 packing.
   The single most fragile thing; port the self-checks to guard it.
2. **`/root/TerrainLabRoot` hardcoded paths** — in 9 files; parameterize on the way out.
3. **`data/*.json` `res://` config paths** — re-path per module.
4. **No real test suite** — the CLI self-check gates ARE the tests; they currently need the harness to run.
   Re-host them on the minimal driver.
5. **Headless RD** — `FieldCompute` and the GPU eroder use a local RD and need a windowed run to execute.

---

*Generated from a deep multi-agent audit pass. Companion source-of-truth for the new-repo migration effort.*
