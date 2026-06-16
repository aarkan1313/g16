# WG16 — Base Field, No Bake (design)

Date: 2026-06-15
Project: `C:\Wg16\wg-16-project` (fresh Godot 4.6 mono, C# + GLSL compute)

## 0. Why

WG15 produced a 5-layer procedural base field the user judged GOOD across 8 seeds
("everything read as good 1-8"). On top of it, a bake stage (erosion: valley_carve →
stream_power → hydraulic → thermal → alluvial) plus water/channels/skeleton churned across
~19 erosion versions and 5 abandoned water systems, all judged bad at the end. The
architecture (Field → Bakery → Presenter, deterministic bake-then-run) was sound; the
erosion *content* was the problem.

**Decision (user):** strip back to the proven base field, with NO bake stage at all, in a
fresh project — "try base field without bake, lets see what happens." Build piece by piece
so each slice can be seen before the next.

## 1. Scope

**IN:**
- **Field** — GPU compute `f(world, seed, params)` producing the 5-layer composition
  (continent → uplift → hills → ridges → macro base). Ported essentially verbatim from
  WG15 `field_height.glsl` (the proven code), trimmed of the `enable_world_precision` ABI
  (a gated no-op with no consumer; YAGNI). Params block 144B → 128B.
- **Presenter** — displaced `PlaneMesh` (one vertex per heightmap texel), vertex-shader
  displacement, correct culling AABB, `SampleHeight` for walk. No bake hooks.
- **Workbench** — lab root: hot-reload `field_params.json`, reseed (R), field-mode debug
  views (0–5), screenshot (F12), HUD.
- **FlyCamera** — RMB/LMB look, WASD+QE fly, Shift boost, wheel speed, walk mode.
- Hot-reloadable JSON params (field + presentation).

**OUT (dropped entirely):**
- Bakery, the whole erosion pipeline + all solvers (StreamPower/Thermal/Alluvial/Hydraulic/
  ValleyCarve), FlowAnalysis, GridResample, WorldSkeleton.
- WaterPlane + all water shaders/code.
- The candidate / seed-set / isolation-preset / lab-focus cyclers and their threading.
- Rust tools (erosion_rs, dem_distill_rs), DemDistill.
- The `.bakes` cache, `delta_map`/`flow_map`, T/V overlay toggles.
- ~50 erosion/water/skeleton gates and probes.
- The docs/research corpus (kept in WG15 as reference; not carried over).

## 2. Architecture — three units, clean boundaries

| Unit | Files | Responsibility | Depends on |
|------|-------|----------------|------------|
| Field | `shaders/field_height.glsl`, `scripts/field/FieldCompute.cs`, `scripts/field/FieldParams.cs` | Pure heightfield generation on the GPU | nothing (Godot RenderingDevice only) |
| Presenter | `scripts/presenter/LabTerrain.cs`, `shaders/lab_terrain.gdshader` | Draw the heightfield as a displaced plane | Field (consumes heights) |
| Workbench | `scripts/workbench/Workbench.cs`, `scripts/workbench/FlyCamera.cs`, `scripts/workbench/PresentationParams.cs`, `scenes/lab.tscn` | Wire input, hot-reload, HUD | Field + Presenter |

Boundary test (each unit answers in one line):
- Field: "give me an N×N page of heights for a world window" → `float[]`.
- Presenter: "draw these heights" → mesh + material, plus CPU `SampleHeight`.
- Workbench: "wire input, reload params, show HUD" → the running lab.

No bake dependency anywhere. The data flow is one direction: params → Field → Presenter →
screen.

## 3. Build order (piece by piece — user directive)

The point is to SEE each slice. Milestones, each independently runnable:

1. **M1 — raw geometry.** Field + Presenter with a minimal material (flat/height-tint),
   fly camera, HUD. Confirms the base field generates and displaces correctly. THE FIRST
   LOOK.
2. **M2 — reseed + field views.** R reseed, 0–5 single-layer debug views, F12 screenshot,
   hot-reload of `field_params.json`. Confirms the 5 layers are each alive (the WG15
   "expression" idea, by eye).
3. **M3 — splat presentation.** Port the 4-texture height/slope splat shader
   (grass/rock/snow/gravel, border jitter, distance-faded detail bump), MINUS the
   `delta_map`/`flow_map` sampling and overlay toggles. Plus `presentation_params.json`
   hot-reload, sun/fog/env. This is the textured look the user judged good — now on the
   pure base.

Each milestone is committed and launchable before the next begins.

## 4. Parameters

`data/field_params.json` carries ONLY the field-shaping knobs (the WG15 set minus the
erosion_pipeline / water / skeleton / bake_apron blocks):
seed, region_size_m, heightmap_res, base_freq, octaves, lacunarity, gain, amplitude_m,
cont_freq, cont_weight, uplift_freq, uplift_weight, uplift_lo, uplift_hi, macro_pivot,
macro_amp, hill_damp, ridge_freq, ridge_amp, mtn_lo, mtn_hi, grain_stretch, cont_octaves,
cont_warp, uplift_warp, massif_freq, massif_floor, foothill_w, foothill_h.
(Values copied from the WG15 proven default.)

`Spacing = region_size_m / heightmap_res` (derived, band-limit driver — unchanged).

`data/presentation_params.json` (M3): tex scales, splat thresholds, sun/fog, walk speeds —
the WG15 set minus water_min/water_band/shore_softness (no water).

## 5. Non-goals / explicit deferrals

- No erosion, ever, in this project's M1–M3. If the user wants erosion later, it returns as
  a fresh, judged-early, single pass — NOT a port of the 19-version pipeline.
- No infinite/streaming/LOD. One region, centered at origin, like the WG15 lab.
- No gates initially. The WG15 gate culture is an asset but is erosion/bake-centric; a
  base-field expression check can be re-added if the field is ever edited. For a verbatim
  port of proven code, the user's eye is the gate (M1 first look).

## 6. Risks

- **ABI lockstep.** Trimming the params block to 128B means GLSL `ParamsBuf` and C#
  `BuildParamsBytes` must stay field-for-field aligned. Mitigation: port both together, keep
  the byte offsets contiguous, no precision fields.
- **Texture import.** The 4 splat PNGs (~24 MB) are copied from WG15 with their `.import`
  files; Godot must re-import on first open. Mitigation: copy `.import` too, let Godot
  regenerate `.godot/imported`.
- **First-run C# registration.** Godot mono sometimes needs a build before scripts register.
  Mitigation: `dotnet build` before launching; launch with `--rendering-driver vulkan`
  (WG15's GPU-compute-stable driver) one process at a time.
