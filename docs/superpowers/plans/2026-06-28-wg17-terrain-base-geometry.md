# WG17 Terrain Base Geometry — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port WG16's proven base geometry (procedural Field + CDLOD infinite terrain) into the fresh WG17 repo as a focused, cleaner rewrite that flies with no cracks/pops/wobble and is at least at parity on performance.

**Architecture:** Layered with the GPU-compute binding quarantined into named classes (`FieldCompute`, `ChunkFieldCache`); the LOD brain (`CdlodQuadtree`) is pure logic; height consumers reach the field only through an `IHeightSource` interface. Build in 5 slices, each ending at a see-able eye-gate + a profile number.

**Tech Stack:** Godot 4.6 (Forward+, D3D12), C# (.NET 8, nullable enabled), GLSL compute (RenderingDevice), GDShader spatial.

**Reference sources:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-terrain-slice-design.md`
- Audit (file manifest + LOC + contracts): `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md`
- WG16 source to port from: `C:\Wg16\wg-16-project\scripts\` and `\shaders\`
- **Target repo:** `C:\Wg16\WG17\terrainengine-10k\`

## Global Constraints

- **Target repo path (verbatim):** `C:\Wg16\WG17\terrainengine-10k` — all created files live here, NOT in WG16.
- **Build:** `Godot.NET.Sdk/4.6.2`, `<TargetFramework>net8.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`, `<EnableDynamicLoading>true</EnableDynamicLoading>`, **no NuGet packages**, assembly name `Terrainengine10k`.
- **Namespaces:** `Te10k.Core`, `Te10k.Field`, `Te10k.Terrain`, `Te10k.Checks` (clean break from `WG16.*`).
- **C# rebuild gotcha:** Godot does NOT rebuild C# on launch. Run `dotnet build` from the project dir after EVERY `.cs` edit before launching, or the change silently no-ops. (Shaders DO hot-compile — misleading.)
- **Launch path gotcha:** launch with an absolute `--path`, e.g. `godot --path C:/Wg16/WG17/terrainengine-10k ...` — never `.` (shell cwd differs).
- **CLI flag gotcha:** user flags after the scene need a bare `--` separator or they silently no-op.
- **Headless RD constraint:** `FieldCompute` uses a local `RenderingDevice`; bakes/checks must run WINDOWED, not `--headless`.
- **Shader contract is byte-exact:** port `field_math.gdshaderinc`, `field_height.glsl`, `field_bake.glsl`, `ground.gdshader` verbatim; only change `res://` paths and the include marker if needed. Do NOT "clean up" shader math this slice.
- **Layering rules (enforced in review):** `RenderingDevice`/`RenderingServer`/`CallOnRenderThread` appear ONLY in `FieldCompute.cs` and `ChunkFieldCache.cs`. `CdlodQuadtree` uses only math types (`Vector2/3`, `Mathf`) — no scene/RD types. `Terrain.cs` is the only scene-graph toucher. Height consumers depend on `IHeightSource`, never `FieldCompute` concretely.
- **No over-abstraction:** do NOT wrap `Node3D`/`MeshInstance3D` behind interfaces. The only seam is `IHeightSource`.
- **Out of scope this slice:** materials/surfacing (placeholder height-color only), water carving (hook present, unwired), clouds, lighting, the lab harness/UI.
- **Verification discipline:** every slice ends with (a) an eye-gate — look at it in motion — and (b) a profile number recorded in the commit message. Don't tune past "looks right + not slower"; real optimization is Task 6.

---

## File Structure

```
terrainengine-10k/
├── Terrainengine10k.csproj              # Task 1
├── project.godot                        # Task 1 (modify: +C# feature, +main_scene)
├── scenes/terrain.tscn                  # Task 2
├── src/core/IHeightSource.cs            # Task 3
├── src/core/FlyCamera.cs                # Task 2
├── src/field/FieldParams.cs             # Task 3
├── src/field/FieldCompute.cs            # Task 3  (implements IHeightSource; RD-quarantined)
├── src/terrain/Terrain.cs               # Task 4 (single mesh) → Task 5 (CDLOD façade)
├── src/terrain/CdlodQuadtree.cs         # Task 5 (pure)
├── src/terrain/CdlodMesh.cs             # Task 5
├── src/terrain/ChunkFieldCache.cs       # Task 6prep/Task 7 (RD-quarantined)
├── src/checks/*.cs                      # ported per-slice (Field/Stream/Morph/Stitch/Pop/Snap)
├── shaders/field_math.gdshaderinc       # Task 3
├── shaders/field_height.glsl            # Task 3
├── shaders/field_bake.glsl             # Task 7
├── shaders/ground.gdshader             # Task 4
└── data/field_params.json              # Task 3
```

---

### Task 1: Bootstrap C# project (Slice 0)

**Files:**
- Create: `C:\Wg16\WG17\terrainengine-10k\Terrainengine10k.csproj`
- Modify: `C:\Wg16\WG17\terrainengine-10k\project.godot`
- Create: `C:\Wg16\WG17\terrainengine-10k\.gitignore` (extend), `.editorconfig`, `.gitattributes` (copy from WG16)

**Interfaces:**
- Produces: a buildable, launchable empty Godot/C# project; `git` initialized with commit 0.

- [ ] **Step 1: Init git in the target repo**

Run (Bash):
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && git init -q && git status
```
Expected: a clean repo on `main` (or `master`) with untracked files listed.

- [ ] **Step 2: Create the csproj**

Create `Terrainengine10k.csproj`:
```xml
<Project Sdk="Godot.NET.Sdk/4.6.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Add the C# feature + main scene to project.godot**

In `project.godot`, set `config/features=PackedStringArray("4.6", "C#", "Forward Plus")` and under `[application]` add `run/main_scene="res://scenes/terrain.tscn"`. Leave the existing `[dotnet] project/assembly_name="Terrainengine10k"`, `[rendering] rendering_device/driver.windows="d3d12"`.

- [ ] **Step 4: Build**

Run (Bash):
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded` (the `.csproj` + Godot SDK restore the Godot C# glue).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "chore: bootstrap WG17 C# Godot project"
```

---

### Task 2: Minimal scene + fly camera (Slice 0 eye-gate)

**Files:**
- Create: `src/core/FlyCamera.cs` (port of `wg-16-project/scripts/workbench/FlyCamera.cs`, namespace → `Te10k.Core`)
- Create: `scenes/terrain.tscn`

**Interfaces:**
- Produces: `FlyCamera : Camera3D` with `[Export] float InitialSpeed`; a launchable scene `Root(Node3D) > Camera(FlyCamera), Sun(DirectionalLight3D), Env(WorldEnvironment)`.

- [ ] **Step 1: Port FlyCamera.cs**

Read `wg-16-project/scripts/workbench/FlyCamera.cs`, copy it to `src/core/FlyCamera.cs`, change `namespace` to `Te10k.Core`. No logic changes.

- [ ] **Step 2: Author terrain.tscn**

Create `scenes/terrain.tscn` with: `Root` (Node3D); `Camera` (Camera3D, script=FlyCamera.cs, `far=90000`, transform pos≈(0,1400,1200) pitched down ~45°, `InitialSpeed=5000`); `Sun` (DirectionalLight3D, rotated for a mid-morning angle, `light_energy=1.3`, `shadow_enabled=true`, `directional_shadow_max_distance=8000`); `Env` (WorldEnvironment with a basic Environment: tonemap filmic, a procedural sky, ambient energy ~0.4). (Mirror WG16's `scenes/terrain_lab.tscn` node values; omit the UILayer.)

- [ ] **Step 3: Build + launch eye-gate**

Run (Bash):
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "/c/path/to/Godot_v4.6-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
(Use the actual Godot 4.6 mono binary path on this machine.)
Expected: window opens to empty sky; RMB-drag + WASD flies the camera.

- [ ] **Step 4: Record baseline profile + commit**

Note the empty-scene frame ms (Godot's built-in monitor or a quick `Engine.GetFramesPerSecond()` print). Commit:
```bash
git add -A && git commit -m "feat: minimal terrain scene + fly camera (empty-frame baseline: <X>ms)"
```

---

### Task 3: Field generation + IHeightSource (Slice 1 — core)

**Files:**
- Create: `src/core/IHeightSource.cs`
- Create: `src/field/FieldParams.cs` (port of `scripts/field/FieldParams.cs`)
- Create: `src/field/FieldCompute.cs` (port of `scripts/field/FieldCompute.cs`)
- Create: `shaders/field_math.gdshaderinc`, `shaders/field_height.glsl` (port verbatim)
- Create: `data/field_params.json` (copy from WG16)

**Interfaces:**
- Produces:
  - `interface IHeightSource { float[] ProducePage(float originX, float originZ, float spacing, int res, uint fieldMode = 0); }`
  - `record FieldParams(...)` (30 fields) with `float Spacing`, `static FieldParams Load()`, `const string Path`.
  - `class FieldCompute : IHeightSource, IDisposable` with `float[] ProducePage(FieldParams p, float originX, float originZ, float? spacing, int? res, uint fieldMode)` AND the interface overload; `static byte[] PackParamsBytes(...)`.

- [ ] **Step 1: Write IHeightSource.cs**

```csharp
namespace Te10k.Core;

public interface IHeightSource
{
    // Square page of heights, row-major (z*res + x), at the given world origin.
    // fieldMode 0 = full composite; 1..N = debug single-layer views.
    float[] ProducePage(float originX, float originZ, float spacing, int res, uint fieldMode = 0);
}
```

- [ ] **Step 2: Port FieldParams.cs**

Copy `scripts/field/FieldParams.cs` to `src/field/FieldParams.cs`; change `namespace WG16.Field;` → `namespace Te10k.Field;`. Keep the 30-field record, `Spacing`, graceful `Load()`, `Path = "res://data/field_params.json"`. Copy `data/field_params.json` verbatim.

- [ ] **Step 3: Port shaders verbatim**

Copy `shaders/field_math.gdshaderinc` and `shaders/field_height.glsl` to `WG17/shaders/`. Keep the `// @@INCLUDE field_math` marker. Fix any `res://shaders/...` path references to match WG17.

- [ ] **Step 4: Port FieldCompute.cs**

Copy `scripts/field/FieldCompute.cs` → `src/field/FieldCompute.cs`; namespace → `Te10k.Field`; add `using Te10k.Core;` and make it implement `IHeightSource`. Add the interface overload that forwards to the existing method:
```csharp
public float[] ProducePage(float originX, float originZ, float spacing, int res, uint fieldMode = 0)
    => ProducePage(_lastParams, originX, originZ, spacing, res, fieldMode); // see Step 5 note
```
Keep the splice loader (`// @@INCLUDE field_math` → field_math text) and `PackParamsBytes` byte-exact.

- [ ] **Step 5: Resolve the params-on-interface detail**

`IHeightSource.ProducePage` has no `FieldParams` arg, but `FieldCompute` needs params. Decision: `FieldCompute` holds the active `FieldParams` (set via constructor or a `Configure(FieldParams)` method); the interface overload uses that. Implement `FieldCompute(FieldParams p)` storing `_params`, and have the interface overload call the full method with `_params`. Document this in a comment.

- [ ] **Step 6: Port FieldCheck and run the determinism gate**

Port `scripts/field/FieldCheck.cs` → `src/checks/FieldCheck.cs` (namespace `Te10k.Checks`). Wire a temporary `--fieldcheck` path in `Terrain.cs` (Task 4) OR a tiny throwaway autoload that calls `FieldCheck.Run(fc, p)` and quits. Run windowed:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "/c/.../Godot_v4.6...mono.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --fieldcheck
```
Expected: console prints `FieldCheck PASS` (byte-identical heights across two dispatches). This proves the std430 packing + splice survived the port.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: field generation + IHeightSource seam (FieldCheck PASS)"
```

---

### Task 4: Single displaced mesh (Slice 1 eye-gate)

**Files:**
- Create: `src/terrain/Terrain.cs` (single-mesh version; becomes the façade in Task 5)
- Create: `shaders/ground.gdshader` (port verbatim)
- Modify: `scenes/terrain.tscn` (attach `Terrain.cs` to a `Terrain` MeshInstance3D under Root)

**Interfaces:**
- Consumes: `FieldParams`, `FieldCompute` (Task 3).
- Produces: `class Terrain : MeshInstance3D` with `void Build(FieldCompute fc, FieldParams p)` that bakes one heightmap page, builds a `PlaneMesh`, binds `ground.gdshader` with all `fp_*`/source-select uniforms set from `p`, `use_analytic=true`, `use_chunk=0`.

- [ ] **Step 1: Port ground.gdshader verbatim**

Copy `shaders/ground.gdshader` → `WG17/shaders/`. Keep the `#include "res://shaders/field_math.gdshaderinc"`. Do not touch the field/material/debug uniform set. Fix `res://` paths if needed.

- [ ] **Step 2: Write Terrain.cs single-mesh Build()**

Port the relevant single-mesh portion of `scripts/lab/TerrainLab.cs`: `Build(fc, p)` → `fc.ProducePage(...)` for the baked heightmap, upload to an `ImageTexture` bound to `heightmap`, set `region_size`, `texel_world`, all `fp_*` uniforms from `p`, `use_analytic`, `use_chunk=0`. Attach a `PlaneMesh` sized to `RegionSizeM` with enough subdivisions to show relief. Namespace `Te10k.Terrain`.

- [ ] **Step 3: Wire scene + _Ready**

In `Terrain.cs` `_Ready()`: `var p = FieldParams.Load(); var fc = new FieldCompute(p); Build(fc, p);`. Attach `Terrain.cs` to a `Terrain` MeshInstance3D node under Root in `terrain.tscn`.

- [ ] **Step 4: Build + launch — EYE-GATE**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "/c/.../Godot_v4.6...mono.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected: **recognizable terrain** (continents/mountains/ridges), height-colored, NOT flat and NOT garbage. Fly over it. If flat/black → the uniform contract or splice is wrong; debug before proceeding.

- [ ] **Step 5: Record single-mesh profile + commit**

Note single-mesh frame ms. Commit:
```bash
git add -A && git commit -m "feat: single displaced terrain mesh — eye-gate PASS (single-mesh: <X>ms)"
```

---

### Task 5: CDLOD streaming (Slice 2 eye-gate)

**Files:**
- Create: `src/terrain/CdlodQuadtree.cs` (port, pure logic)
- Create: `src/terrain/CdlodMesh.cs` (port)
- Modify: `src/terrain/Terrain.cs` (add CDLOD façade: `SetCdlod`, `CdlodTick`, `SetLodViz`, accessor)
- Create: `src/terrain/CdlodTerrain.cs` (port; the streamed instance pool + Tick)
- Create: `src/checks/StreamCheck.cs`, `MorphCheck.cs`, `StitchCheck.cs` (ported)
- Modify: `scenes/terrain.tscn` driver — `_Process` calls `CdlodTick`

**Interfaces:**
- Consumes: `FieldParams`, `FieldCompute`, `ground.gdshader` chunk-mode uniforms.
- Produces: `CdlodQuadtree.SelectRoaming(camPos[, centerCellOrigin]) → List<CdlodChunk>`, `NeighborInvariantHolds(...)`, `Ring`; `CdlodMesh.BuildGrid(n)`, `BuildStitchedVariants(n)→ArrayMesh[16]`; `CdlodTerrain : Node3D` with `Setup(mat,p,minH,maxH,heights)`, `Tick(camPos, velXZ)`, `SetLodViz(bool)`, `RenderOrigin`, `LeafCountLastTick`, `InvariantHoldsNow(out msg)`; `Terrain.SetCdlod(bool)`, `Terrain.CdlodTick(camPos, velXZ)`, `Terrain.Cdlod`.

- [ ] **Step 1: Port CdlodQuadtree.cs (pure)**

Copy `scripts/lab/CdlodQuadtree.cs` → `src/terrain/`; namespace `Te10k.Terrain`. Verify it references only `Vector2/3`/`Mathf` — no scene/RD types (layering rule). Keep `SelectRoaming`, `NeighborInvariantHolds`, `Ring`, `SelfCheck`.

- [ ] **Step 2: Port CdlodMesh.cs**

Copy `scripts/lab/CdlodMesh.cs` → `src/terrain/`; namespace `Te10k.Terrain`. Keep `BuildGrid` + the 16 stitched edge-weld variants.

- [ ] **Step 3: Port CdlodTerrain.cs**

Copy `scripts/lab/CdlodTerrain.cs` → `src/terrain/`; namespace `Te10k.Terrain`. For THIS task, leave `ChunkFieldCache`/`ChunkAabbProvider` wiring stubbed/disabled (live field eval in shader, `cache_ready=0`). Keep `Setup`, `Tick`, `SetLodViz`, `RenderOrigin`, floating-origin snap, identity-keyed pool, stitch-variant selection. Keep `SetWaterDelta` present but it stays unwired.

- [ ] **Step 4: Add CDLOD façade to Terrain.cs + driver**

In `Terrain.cs`: add `SetCdlod(bool)` (sets `use_chunk`, `grid_n`, `split_factor`, builds/wires `CdlodTerrain` as a sibling), `CdlodTick(camPos, velXZ)` → `_cdlod.Tick(...)`, `SetLodViz(bool)`, `Cdlod` accessor. In the scene driver `_Process(delta)`: compute camera velocity, call `terrain.CdlodTick(cam.GlobalPosition, velXZ)`. Default CDLOD on.

- [ ] **Step 5: Build + launch — EYE-GATE (motion)**

Launch, fly OUT across the world. Expected: terrain streams in ahead of you, LOD transitions are smooth (geomorph, no popping), and there are **no cracks** at LOD boundaries. Toggle `--lodviz` to see LOD bands. If cracks → stitch variant selection; if pops → geomorph/morphK.

- [ ] **Step 6: Port + run the streaming checks**

Port `StreamCheck.cs`, `MorphCheck.cs`, `StitchCheck.cs` → `src/checks/`. Run each via a `--streamcheck`/`--morphcheck`/`--stitchcheck` path:
```bash
... --path "C:/Wg16/WG17/terrainengine-10k" -- --morphcheck
```
Expected: each prints PASS and quits 0.

- [ ] **Step 7: Record flying profile + commit**

Note flying frame ms. Commit:
```bash
git add -A && git commit -m "feat: CDLOD streaming — eye-gate PASS, Stream/Morph/Stitch PASS (flying: <X>ms)"
```

---

### Task 6: GPU field cache + floating origin (Slice 3 eye-gate)

**Files:**
- Create: `src/terrain/ChunkFieldCache.cs` (port, RD-quarantined)
- Create: `shaders/field_bake.glsl` (port verbatim)
- Modify: `src/terrain/CdlodTerrain.cs` (enable cache wiring: `chunk_cache`, `cache_side`, `chunk_slot`, `cache_ready`)
- Create: `src/checks/PopCheck.cs`, `SnapDiff.cs` (ported)

**Interfaces:**
- Consumes: `IHeightSource` (Task 3), `CdlodTerrain` (Task 5).
- Produces: `ChunkFieldCache(IHeightSource src, ...)` with `Request(key, slot, originXZ, size)`, `TryTake(out key, out slot)`, `Pump()`, `Tex` (Texture2DArray), `Ready`, `Side`; `CdlodTerrain.SetFieldCache(bool)`, `SetBakeReq(int)`.

- [ ] **Step 1: Port field_bake.glsl verbatim**

Copy `shaders/field_bake.glsl` → `WG17/shaders/`. Keep the `// @@INCLUDE field_math` marker and the `GridBuf` (`grid_spacing`, `layer`). Preserve the invariant: `ParamsBuf.spacing` ≠ `GridBuf.grid_spacing`.

- [ ] **Step 2: Port ChunkFieldCache.cs depending on IHeightSource**

Copy `scripts/lab/ChunkFieldCache.cs` → `src/terrain/`; namespace `Te10k.Terrain`. Change its constructor to take `IHeightSource` (and whatever scalar config it needs) instead of `FieldCompute` concretely — it only needs `PackParamsBytes` and a page; if it uses `PackParamsBytes` statically off `FieldCompute`, keep that static call (that's fine — it's the shared packer), but the height *source* dependency is the interface. Keep RD/`CallOnRenderThread` confined to this file.

- [ ] **Step 3: Wire the cache into CdlodTerrain**

Enable the cache path in `CdlodTerrain`: on chunk birth `Request` a bake; in `Tick`/drain set `chunk_slot` + flip `cache_ready=1` after the bake lands; bind `chunk_cache`/`cache_side` to the material. Add `SetFieldCache(bool)` / `SetBakeReq(int)` and an A/B default-on. Confirm floating-origin `render_origin` snap is active (snap interval = `RegionSizeM`).

- [ ] **Step 4: Build + launch — EYE-GATE (far flight)**

Launch, fly FAR — toward ~10 km out. Expected: no precision wobble, and crucially **no pop/seam when the render origin snaps** (every region-size of travel). Watch the horizon and a fixed near feature across a snap.

- [ ] **Step 5: Port + run PopCheck and SnapDiff**

Port `PopCheck.cs`, `SnapDiff.cs` → `src/checks/`. Run:
```bash
... -- --popcheck
... -- --snapdiff
```
Expected: both PASS (no height/normal pop at LOD swap; identical leaf across a snap).

- [ ] **Step 6: Record cache profile (A/B) + commit**

Record flying ms with cache ON vs OFF (`--fieldcache=0`). This is where WG16 saw −33% — confirm the win. Commit:
```bash
git add -A && git commit -m "feat: GPU field cache + floating origin — eye-gate PASS, Pop/Snap PASS (cache on: <X>ms, off: <Y>ms)"
```

---

### Task 7: Profile-driven hotspot optimization (Slice 4)

**Files:**
- Modify: whichever files the profiler implicates (likely `CdlodTerrain.cs`, possibly `ChunkFieldCache.cs`).
- Optional: `src/checks/LivePopMeter.cs` (ported HUD for in-motion metering).

**Interfaces:**
- Consumes: everything from Tasks 3–6.
- Produces: measured speedups; no new public API required.

- [ ] **Step 1: Profile under real motion**

Fly a representative path (border crossings + LOD churn) and capture frame-time percentiles + the worst spikes. Identify the top 1–2 hotspots. **Do not guess** — the WG16 history names candidates (index-keyed pool churn → confirm WG17 uses identity-keyed; birth-budget racing the field cache; ring/lookahead CPU floor), but let the profiler pick.

- [ ] **Step 2: Fix the top hotspot, re-measure**

Apply one targeted fix. Rebuild, re-profile the same path. Record before/after. If no improvement, revert and pick the next candidate.

- [ ] **Step 3: Re-run all checks**

```bash
... -- --fieldcheck ; ... -- --streamcheck ; ... -- --morphcheck ; ... -- --stitchcheck ; ... -- --popcheck ; ... -- --snapdiff
```
Expected: all still PASS (optimization didn't regress correctness).

- [ ] **Step 4: Eye-gate + commit**

Fly again, confirm look unchanged. Commit each optimization separately:
```bash
git add -A && git commit -m "perf: <hotspot> — flying <before>ms → <after>ms"
```

- [ ] **Step 5: Update the migration record**

Append a short "WG17 slice 1 outcome" note (final profile numbers, view distance achieved, any deviations) to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md` and commit it in the WG16 repo.

---

## Self-Review

**Spec coverage:**
- §1 goal/parity/cleaner/faster → Tasks 4 (eye-gate), 6 (cache), 7 (perf). ✓
- §2 layout + layering rules → Task 1 structure, enforced in Tasks 3/5/6. ✓
- §3 IHeightSource → Task 3 Step 1 + 5; consumed by ChunkFieldCache Task 6 Step 2. ✓
- §4 shader/std430 contract → Tasks 3/4/6 (verbatim port + FieldCheck guard). ✓
- §5 build order (5 slices) → Tasks 1–7 map 1:1 (Slice 0=T1+T2, 1=T3+T4, 2=T5, 3=T6, 4=T7). ✓
- §6 testing (the 6 checks + LivePopMeter) → ported in the slice that needs them (FieldCheck T3, Stream/Morph/Stitch T5, Pop/Snap T6, LivePopMeter T7). ✓
- §7 risks (build gotcha, launch path, headless RD, splice, snap) → Global Constraints + per-task steps. ✓
- §8 DoD → Task 7 Step 5 records outcome. ✓

**Placeholder scan:** Task 7 is intentionally profile-driven (the spec's Slice 4 is exploratory by design) — its steps specify the *method* (profile → fix top hotspot → re-measure → re-check) with concrete commands, not vague "optimize". The `<X>ms` / `/c/.../Godot...exe` are values to fill at run time (machine-specific), not unspecified work. Acceptable.

**Type consistency:** `ProducePage` interface signature matches Task 3 / Task 6 usage; `CdlodTick`/`Tick`/`SetCdlod`/`SetLodViz`/`SetFieldCache` names consistent across Tasks 5–6; `IHeightSource` consumed (not `FieldCompute`) in Task 6. ✓

**Note on TDD:** This is a *port* of proven code, so the discipline is port-verbatim → guard-with-self-checks → eye-gate → profile, rather than red/green TDD. The ported checks ARE the test suite (matching how WG16 validated the same code). This is a deliberate deviation from the skill's default TDD loop, appropriate to a migration.
