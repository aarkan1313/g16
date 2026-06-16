# WG16 — Tech Stack & Inventory

What we use, why, and the rules we hold it to. Kept short on purpose. Update it
when the stack actually changes, not when a file moves.

## The policy (one paragraph)

We use **the right tool for performance and quality, nothing speculative.** That
means C# for game/orchestration logic, GPU compute (GLSL) for the math that runs
per-pixel/per-cell at scale, and Rust **only** when a measured hot path needs it.
We do not add a tool because it might be useful later — we add it when a
profiler or a quality bar says we must. Everything is **modular**: small units
with one job and a clean interface, so any piece can be replaced without
rewriting its neighbors.

## The three languages and when each is appropriate

| Tool | Use it for | Don't use it for |
|------|-----------|------------------|
| **C# (Godot mono)** | Game logic, scene wiring, hot-reload, tooling, anything that isn't a measured bottleneck. The default. | Tight numeric inner loops over millions of cells (that's GPU or Rust). |
| **GPU compute (GLSL, RenderingDevice)** | Embarrassingly-parallel per-cell math: the height field, and later any per-texel map (masks, lighting prep, splat weights). Pure functions of position. | Anything with sequential dependencies between cells (flow routing, ordered passes) — those don't parallelize cleanly and belong on CPU. |
| **Rust (cdylib via P/Invoke)** | A CPU hot path that profiling proves is the bottleneck AND can't go on the GPU (e.g. serial routing, ordered simulation). Ported only after the C# version is correct and the look is locked. | First drafts. Anything unmeasured. Anything the GPU can do. Rust is an optimization, never a starting point. |

**The ladder:** write it in C# first (correct, readable, hot-reloadable). If it's
per-cell and parallel → GPU compute. If it's a measured serial bottleneck →
Rust. Never skip to Rust on a hunch — WG15 learned this: a Rust port hoped for
10× delivered 2.6× because the real cost was serial routing the GPU/rayon
couldn't touch. **Profile the parallel fraction, not the time share, before
promising a speedup.**

## Current inventory (what's actually in the tree today)

Three units, ~910 lines. No bake stage.

### Field — pure heightfield generation (GPU)
- `shaders/field_height.glsl` — the 5-layer composition (continent → uplift →
  hills → ridges → macro base) as a compute shader. The proven WG15 math.
- `scripts/field/FieldCompute.cs` — dispatches the shader via a local
  `RenderingDevice`, returns an `N×N float[]` page. Knows nothing about meshes
  or cameras.
- `scripts/field/FieldParams.cs` — all field knobs, loaded from JSON. A record.
- **Interface:** `ProducePage(params, origin, spacing, res, mode) → float[]`.
- **Depends on:** Godot RenderingDevice only.

### Presenter — draw the heightfield (C# + a small spatial shader)
- `scripts/presenter/LabTerrain.cs` — displaced `PlaneMesh`, one vertex per
  texel, correct cull AABB, `SampleHeight` for walk mode, `ApplyPresentation`
  pushes splat knobs.
- `shaders/lab_terrain.gdshader` — texture splat (M3): height/slope blend over 4
  textures (grass/rock/snow/gravel), border jitter, biplanar rock, distance-faded
  detail bump. No bake maps, no overlays.
- `assets/textures/` — grass/rock/snow/gravel PNGs (~19 MB, from WG15).
- **Interface:** `Rebuild(fc, params, mode)`, `ApplyPresentation(pp)`,
  `SampleHeight(x, z)`.
- **Depends on:** Field (consumes the `float[]`).

### Workbench — wire it together (C#)
- `scripts/workbench/Workbench.cs` — lab root: hot-reload, reseed, layer views,
  screenshot, HUD.
- `scripts/workbench/FlyCamera.cs` — fly/walk camera.
- `scripts/workbench/PresentationParams.cs` — lighting/walk knobs, JSON.
- `scenes/lab.tscn` — the scene wiring the three together.
- **Depends on:** Field + Presenter.

### Data (hot-reloadable, never literals in code)
- `data/field_params.json` — field knobs.
- `data/presentation_params.json` — sun/fog/walk knobs.

## Data flow

```
field_params.json ─┐
                   ├─> FieldCompute (GPU) ─> float[] heights ─> LabTerrain ─> screen
presentation.json ─┘                                              ▲
                                                          Workbench wires input,
                                                          hot-reload, HUD
```

One direction. No cycles. No bake, no cache, no disk round-trip — the field is a
fast pure function generated on demand (~390 ms for the full 2048² region).

## Modularity rules (the ones we actually enforce)

1. **One unit, one job.** Field generates, Presenter draws, Workbench wires. A
   file that starts doing two of those is a refactor signal.
2. **Talk through interfaces, not internals.** You can swap the splat shader
   without touching Field; swap the field math without touching the camera.
3. **All constants are hot-reloadable data.** No magic numbers in code — they go
   in a `*_params.json`. (WG15 rule that paid off.)
4. **New modules are additive.** Adding a feature = a new unit + a wiring line,
   not a reshape of an existing one. If a feature forces a rewrite of a neighbor,
   the boundary was wrong.
5. **Performance is a tool choice, not a rewrite.** When something's slow, move
   it down the ladder (C# → GPU → Rust) behind the same interface — callers
   don't change.

## What we are NOT using (and why)

- **No bake/cache stage.** It only existed in WG15 to hold erosion; we dropped
  erosion, so the bake has no reason to exist. The base field is generated live.
- **No erosion / water / streaming / LOD** yet. They come back only as fresh,
  separately-judged units if and when we decide to — never as a port of the old
  pipeline.
- **No Rust yet.** Nothing here is a measured bottleneck. It enters when one is.
