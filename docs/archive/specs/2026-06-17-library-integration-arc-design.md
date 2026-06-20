# Library Integration — Arc Design (flora + terrain-editing → into WG16)

Date: 2026-06-17 · Status: awaiting user review · Lane: integration (wiring two
externally-built libraries into the look lab; additive, no base-field changes)

## Why

Two isolated build chats returned **library code** (no project access, no demo dependency):

- **`wg16_flora`** — procedural flora placement (CPU scatter oracle + MultiMesh builder +
  wind shader + GPU-scatter scaffold). Namespaces `Wg16.Flora.*`. v0.1: CPU-only.
- **`TerrainEditingSubsystem`** — additive height-delta edit layer (brush raise/lower on a
  GPU compute dirty-rect, R32F delta texture, CPU-mirror stroke undo, Godot adapter node).
  Namespace `AaaTerrainEditing.*`. First unit: Raise/Lower; Flatten/Smooth/Noise/PaintMask
  are interface stubs.

Both correctly internalized WG16's hard-won gotchas (CallOnRenderThread not CompositorEffect;
local-RD windowed not headless; everything behind toggles; CPU path as correctness oracle).
They are staged at `c:\Wg16\_incoming\` — **nothing has been wired into the project yet.**
This spec is the wiring plan our side owes (the ROADMAP's "integration owed on return" note).

## The honest sequencing problem (read this first)

Both libraries' **best inputs don't exist on our side yet:**

- Flora's *density provider* wants Unit 4's GPU breakup masks (slope/curv/cavity/aspect) and a
  biome/climate field — neither is built.
- Terrain-editing's *full value* (edits propagating) wants the breakup/splat/flora **re-bake
  chain** — also not built.

So a "complete" integration now would stub those feeders and get redone later. The decision
this spec makes: **wire a MINIMAL first pass that proves each library live behind a toggle,
using procedural/constant stand-ins for the missing feeders, and explicitly defer the
mask/biome/re-bake coupling to a later pass** once those units land. This matches the project
posture (spike cheap, put in front of the eye early, don't build a full system to first
judgment).

## What we have to connect to (verified against code)

| Seam | Our code today | Status |
|------|----------------|--------|
| Height sample | `LabTerrain.SampleHeight(x,z)` bilinear over `_heights float[]` (`scripts/presenter/LabTerrain.cs:99`) | ✅ exists |
| Normal / slope / curvature | none exposed | ⚠ derive from the height `float[]` (central differences) — cheap, add to a sampler |
| Biome id | none (no climate field yet) | ⚠ stub `biomeId = 0` until a climate/moisture field exists |
| Density field | Unit 4 breakup masks (planned, not built) | ⚠ first pass = procedural/constant density provider |
| Terrain vertex displacement | `terrain_lab.gdshader` displaces from the `Rf` `heightmap` texture | ✅ exists — add `height_delta_tex` sample + `+= delta` here |
| Region size / origin | `FieldParams.RegionSizeM`, region centered at origin, world XZ ∈ [−R/2, +R/2] | ✅ matches both libs' contract |
| Per-frame compute→material | `CloudVolume` CallOnRenderThread pattern (proven) | ✅ terrain-edit adapter uses the same pattern |
| Build system | single `WG16.csproj` | ⚠ flora source folds in by path; terrain-edit ships its own `.sln`/`.csproj` — fold **Core source** in, drop its `.csproj` (don't add a second build target) |

## Architecture — the two integrations are independent units

```
                    ┌─────────────────────────────────────────────┐
   base heightfield │ FLORA INTEGRATION (Unit L1)                  │
   (LabTerrain) ────┼─► Wg16FloraSurfaceProvider (height+normal+   │
                    │     slope+curv from the float[]; biome=0)    │
                    │   Wg16FloraDensityProvider (PROCEDURAL v1)    │
                    │     → CpuFloraScatterBackend → MultiMeshBuilder
                    │     → MultiMeshInstance3D under a flora root  │
                    └─────────────────────────────────────────────┘
                    ┌─────────────────────────────────────────────┐
   base heightfield │ TERRAIN-EDIT INTEGRATION (Unit L2)           │
   (shader displace)┼─► HeightDeltaEditLayerNode (R32F Texture2DRD)│
                    │   terrain_lab.gdshader: final = base + delta │
                    │   BrushStamp.Raise/Lower on mouse hit        │
                    │   CPU mirror + StrokeUndoStack (exact undo)  │
                    └─────────────────────────────────────────────┘
                    ┌─────────────────────────────────────────────┐
   LATER (Unit L3)  │ COUPLING: edit bumps DataVersion → re-bake   │
                    │ splat + breakup masks + flora scatter; flora │
                    │ density consumes breakup/biome/coverage      │
                    └─────────────────────────────────────────────┘
```

| Unit | What | Build phase |
|------|------|-------------|
| **L1 — Flora minimal wiring** | Surface provider (height + derived normal/slope/curv, biome=0) + a PROCEDURAL density provider; scatter on the current region; MultiMeshes under a flora root; Flora tab toggle + per-layer toggles + build-stats log. Real meshes optional (fallback geometry proves the pipeline). | **first** |
| **L2 — Terrain-edit minimal wiring** | `HeightDeltaEditLayerNode` publishing the R32F delta `Texture2DRD`; shader vertex path adds the delta (UV = TerrainSpace mapping); mouse-ray hit → `BrushStamp.Raise/Lower`; stroke model (Begin/Apply/Commit); CPU mirror + `StrokeUndoStack` for undo. Edit tab: brush op/radius/strength + undo/redo. | **second** |
| **L3 — Coupling / re-bake chain** | Edits bump terrain `DataVersion` → invalidate + re-bake splat, ground breakup masks (Unit 4), and flora scatter. Flora density provider switches from procedural to consuming breakup/biome/coverage fields. | LATER (needs Unit 4 + a climate field) |

## Unit L1 — flora minimal wiring (detail)

- **Surface provider** (`scripts/flora/Wg16FloraSurfaceProvider.cs`): implements
  `IFloraSurfaceProvider`. `TrySample` returns world position from `LabTerrain.SampleHeight`;
  **normal/slope/curvature derived by central differences** on the height field (sample
  ±spacing in X/Z → tangents → normal → slope degrees; curvature = Laplacian of height).
  `biomeId = 0`. `DataVersion` from a counter bumped on rebuild/reseed.
- **Density provider** (`scripts/flora/ProceduralFloraDensityProvider.cs`): v1 returns a
  procedural density (e.g. low-freq noise × the layer's own slope/height gates already in
  params). This is the deliberate stand-in for Unit 4's masks. Returns [0,1], clamped.
- **Scatter + build:** `CpuFloraScatterBackend().Scatter(request)` → `FloraMultiMeshBuilder.
  BuildLayerInstances(...)` → parent the `MultiMeshInstance3D`s under a `FloraRoot` node in
  `terrain_lab.tscn`. Fallback box/quad mesh if `mesh_path` empty (mechanical proof, not art).
- **Wiring point:** `TerrainLab` / `TerrainLabUI` after the field builds. Rebuild flora when
  the field reseeds/rebuilds (NOT per frame — guard on `DataVersion`).
- **Controls (Flora tab):** `flora.enabled`, per-layer `enabled`, `log_build_stats`,
  `force_rebuild`. Wired by control `id`/`setter` (NOT `param` — these aren't shader uniforms;
  the registry gotcha: a `param` naming a missing uniform silently no-ops).
- **Gate:** scatter runs, stats print sane counts, instances sit ON the terrain (height in Y),
  slope gate visibly thins steep areas, density=0 → zero instances, deterministic across
  rebuilds. Then the user's eye on distribution.

## Unit L2 — terrain-edit minimal wiring (detail)

- **Fold in:** `TerrainEditing.Core` source under `scripts/terrain_edit/core/` (drop its
  `.csproj`/`.sln` — compile inside `WG16.csproj`); `TerrainEditing.Godot4` adapter under
  `scripts/terrain_edit/`; `height_delta_brush_r32f.glsl` → `shaders/` (import as RDShaderFile).
- **Edit layer node:** add `HeightDeltaEditLayerNode`, `Initialize(settings, glsl_path)` with
  `TerrainSpace` matching our region (origin −R/2, size R), `Width=Height=field res`,
  `R32Float`. Once `DeltaTextureHandle.IsReady`, assign `DeltaTexture` to the terrain material's
  `height_delta_tex` ONCE (the proven assign-RID-once pattern).
- **Shader:** in `terrain_lab.gdshader` **vertex** path, sample `height_delta_tex` at
  `uv = (world_xz − edit_origin) / edit_size` and add `.r` to the base height before displacing.
  Keep the CPU `SampleHeight` mirror in sync for walk mode (add the same delta on CPU, or accept
  walk-mode ignores edits in v1 — note which).
- **Input:** mouse ray → terrain hit (raycast against the plane / sample height) → on drag,
  stamp when the center moves ≥ `radius × 0.2`; `BrushStamp.Raise/Lower(hit, radius, strength,
  dt, settings)` → `editLayer.ApplyBrush(stamp)`. Stroke model: press=Begin, drag=Apply×N,
  release=Commit. **Never `RenderingDevice.Sync()` mid-stroke.**
- **Undo:** CPU mirror (`CpuHeightDeltaLayer`) + `StrokeUndoStack` records exact before-values;
  undo/redo restores the mirror. ⚠ The lib does NOT yet push a restored CPU region back to the
  live GPU texture — v1 either (a) re-uploads the affected region via a small adapter method we
  add, or (b) documents undo as mirror-only until that method lands. Pick (a) if cheap.
- **Controls (Edit tab):** brush op (raise/lower), radius, strength, undo, redo, clear-edits.
- **Gate:** raise/lower visibly deforms terrain live, only the dirty rect updates (no full-map
  cost), edits persist, undo/redo exact, base field untouched (toggle edits off → original).

## Performance posture

- **L1 flora:** CPU scatter is a tool/build-time cost (per rebuild, not per frame); MultiMesh
  rendering is GPU-instanced. Don't rebuild every frame — guard on `DataVersion`. GPU scatter
  scaffold stays unwired until inputs are GPU textures (their own guidance + ours).
- **L2 edit:** brush is a dirty-rect compute dispatch (cheap, local); the only per-frame cost is
  the shader sampling one extra texture. No readback during strokes.

## Testing / validation

- **Mechanical first** (both): builds clean in `WG16.csproj`; flora demo-equivalent stats print;
  terrain-edit self-tests pass (`dotnet run` the SelfTests, + the Python numeric check) BEFORE
  wiring, so the libs are proven in isolation.
- **Live (windowed)** — the gate: L1 flora distribution looks right and sits on terrain; L2
  brush deforms live, dirty-rect only, undo exact, base untouched. Both behind toggles so the
  user can isolate. Never judge from a still (motion gotcha).

## Risk / undo

- Additive: both are new units + wiring lines; base field math untouched; `git checkout .`
  reverts. Edits live in a separate delta texture (toggle off → original terrain).
- Main risk = the **build-system reconciliation** (two foreign namespaces + a second `.csproj`).
  Mitigation: fold Core *source* into `WG16.csproj`, don't add build targets; keep the libs'
  Core unchanged so upstream fixes re-apply, adjust only the Godot4 adapter if API casing differs.

## NOT doing (YAGNI / boundaries)

- NOT building the chunk/streaming page manager (both libs model one region; chunking is a
  separate future arc — both libs documented the wrap-don't-rewrite path).
- NOT wiring flora GPU scatter (scaffold only; CPU oracle first).
- NOT implementing Flatten/Smooth/Noise/PaintMask brush kernels (interface stubs; raise/lower
  proves the pipeline).
- NOT building the L3 re-bake coupling until Unit 4 (breakup masks) + a biome/climate field
  exist — wiring it now means stubbing feeders and redoing it.
- NOT touching the base-field math (settled).

## Build order

L1 (flora minimal) → L2 (terrain-edit minimal) — each its own plan, each proven mechanically
then eye-gated live behind a toggle. L3 (coupling/re-bake) waits on Unit 4 + a climate field.
Companion plans written when reached; this doc is the arc.
