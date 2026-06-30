# Fast Water World Engine Integration

Fast Water is packaged as composable water bodies, not as one global water scene. An infinite world engine should instantiate the smallest body type that matches the authored or generated water feature, then query all bodies through `FastWaterBodyQuery`.

## Body Choices

- Use `FastWaterSurface` for finite lakes, ponds, pools, flooded basins, and local hero water.
- Use `FastWaterPath` for rivers, streams, canals, drainage channels, and authored shoreline strips.
- Use `FastWaterOcean` for broad camera-relative ocean water; it owns a near interactive tier and a far non-interactive tier.
- Use `FastWaterWaterfall` as a producer for falling sheets, lip foam, plunge foam, and downstream flow turbulence.
- Use `FastWaterFoamField` and `FastWaterFlowField` as optional maps that can be generated, baked, imported, or streamed by the host world engine.

## Runtime Contract

Gameplay and streaming code should not branch on concrete water classes. Every world water body should implement or forward these methods:

```gdscript
get_surface_height_at(world_pos: Vector3) -> float
get_water_altitude(world_pos: Vector3) -> float
get_water_depth_at(world_pos: Vector3) -> float
get_flow_at(world_pos: Vector3) -> Vector3
contains_water_point(world_pos: Vector3) -> bool
add_splash(world_pos: Vector3, velocity: Vector3, radius: float = 0.5)
add_wake_point(world_pos: Vector3, velocity: Vector3, radius: float = 0.5)
```

Use `FastWaterBodyProfile` to define body kind, query priority, containment margin, default depth, flow scale, and tags. Assign higher priorities to local rivers/streams than lakes, and lower priorities to background ocean. That lets `FastWaterBodyQuery.find_best_body_query()` pick a stream over the ocean at a river mouth without hard-coded scene assumptions.

### Querying oceans

`FastWaterBodyQuery` returns the `FastWaterOcean` node itself for ocean positions, never its near/far render tiers. The tiers carry a non-queryable body profile (`enabled = false`) so they render but never win a query; only the facade answers `get_surface_height_at`/`add_wake_point`/etc. and forwards interactions to the near interactive tier. Do not query or route gameplay events to the child surfaces directly — they are rendering LOD tiers, not query bodies (the far tier is non-interactive and would silently drop wakes).

Ocean height queries track the analytic swell: `FastWaterOcean.get_surface_height_at()` returns the displaced surface (shared wave stack with the shader at the default `Engine.time_scale`), so buoyant objects bob with the visible swell instead of sitting on a flat plane. `get_flow_at()` returns zero for open ocean but forwards to the near tier, so a host-assigned current/tidal flow field is honored.

### Quick start

```gdscript
# Finite lake/pool/hero water
var lake := FastWaterSurface.new()
lake.mesh_size_m = 60.0
lake.body_profile = FastWaterBodyProfile.lake()
add_child(lake)

# River/stream/canal
var river := FastWaterPath.new()
river.control_points = PackedVector3Array([Vector3(-20, 0, 0), Vector3(0, -0.4, 8), Vector3(20, -0.8, 4)])
river.auto_create_flow_field = true
add_child(river)

# Open-world ocean (near interactive + far cheap tier around the viewer)
var ocean := FastWaterOcean.new()
ocean.ocean_profile = FastWaterOceanProfile.world_lod() # or open_world() / performance()
ocean.target_camera = $Camera3D
ocean.wake_focus = $PlayerBoat   # optional: keep local wakes on the gameplay actor
add_child(ocean)
```

## Infinite World Pattern

Keep world streaming ownership outside the addon:

1. The world engine decides which water chunks, path segments, lakes, and ocean focus are active.
2. Fast Water nodes render and answer local water queries for those active bodies.
3. Generated data enters through public setters and import APIs: `set_flow_field_texture()`, `set_foam_field_texture()`, `set_wake_map_texture()`, `FastWaterFlowField.import_image()`, and `FastWaterFoamField.import_image()`.
4. Expensive helpers stay opt-in. Use planar reflection, bow wakes, rain impact FX, waterfall spray, and high particle counts only near the camera or hero actor.

For large oceans, do not scale one lake plane to world size. Use `FastWaterOcean` with `FastWaterOceanProfile.performance()` or `open_world()`, set `target_camera` for camera-relative recentering, and optionally set `wake_focus` to a player boat or swimmer so local wakes stay near the gameplay actor.

## World Water LOD

WG16/WG17 terrain uses stable world-XZ chunks, viewer-driven LOD selection, and floating-origin/camera-relative rendering so the high-quality region stays near the player without changing field truth. Water should follow the same ownership split:

1. Macro water appearance is world-space and deterministic. Waves, whitecaps, horizon tint, and large-scale color should match across distance tiers.
2. Expensive local effects are viewer-relative. Wake maps, hero ripples, bubbles, planar reflection, refraction, and micro/detail normals belong on near water only.
3. Stable data stays world-indexed. River paths, lake bounds, flow fields, foam fields, and baked/imported maps should be keyed by generated world chunks or authored features, not by moving screen rings.
4. Rendering tiers can move. `FastWaterOcean` snaps persistent near/far surfaces around `target_camera` or `wake_focus`; shader sampling remains in world coordinates, so a snap changes coverage, not the wave identity.
5. Tier boundaries should feather. `near_edge_fade_width` fades the high-quality near mesh into the far mesh so the near tier does not appear as a rectangular overlay, and `far_edge_fade_width` dissolves the far mesh's outer boundary so it meets the sky instead of cutting off when viewed from elevation.
6. Keep world coordinates bounded with a floating origin. The wave field is sampled from absolute world XZ in 32-bit floats, so single-precision begins to shimmer roughly past ~10 km from the origin. WG16/WG17 infinite worlds already rebase to a floating origin; keep the ocean inside that rebased frame (or near the world origin) rather than letting the player roam to very large absolute coordinates. Both the shader and the matching GDScript height query (`sample_wave_height`) read the same absolute XZ, so they stay consistent under rebasing — but neither corrects for large-magnitude float error.

Use `FastWaterOceanProfile.world_lod()` when the player can see broad water and you want the same ocean character at distance. It enables matched macro appearance across the near/far tiers while keeping high-cost local detail near-only. Use `performance()` for broader low-cost views, and author finite lakes/rivers as streamed world bodies that register with `FastWaterBodyQuery`.

For rivers and streams, generate `FastWaterPath.control_points` plus optional `point_widths_m`, `point_depths_m`, `point_flow_speeds_mps`, `point_bank_foam_strengths`, and `point_turbulence_strengths`. Enable `auto_create_flow_field` for local authored segments, or stream precomputed flow textures through `FastWaterFlowField.import_image()` for larger generated networks.

## Tuning Surfaces

- Visual look: `FastWaterVisualProfile`
- Ocean scale and cost: `FastWaterOceanProfile`
- Broad quality caps: `FastWaterQuality`
- Body semantics and query priority: `FastWaterBodyProfile`
- Weather response: `FastWaterEnvironmentState`, `FastWaterWeatherResponse`, and `FastWaterWeatherAdapter`
- Persistent whitewater: `FastWaterFoamField`
- Directional current and turbulence: `FastWaterFlowField`

These resources are intentionally data-owned. A world engine can create presets per biome, climate state, distance band, or gameplay zone without modifying shaders or demos.

## World Generator Wiring (checklist)

For a procedural world generator, keep streaming and authority in the host and treat Fast Water as a stateless renderer + query provider:

1. **Per-biome look is data.** Assign a `presets/*.tres` (visual/ocean/body/quality) per biome, climate, or zone. The generator never edits shaders or scripts — it swaps resources. Build new presets from `FastWaterVisualProfile`/`FastWaterOceanProfile`/`FastWaterBodyProfile`/`FastWaterQuality` and save them as `.tres`.
2. **Spawn the smallest body per generated feature.** Lakes/ponds → `FastWaterSurface`; rivers/streams → `FastWaterPath` (feed generated `control_points` + per-point width/depth/flow/foam/turbulence); broad water → `FastWaterOcean`; falls → `FastWaterWaterfall`. Set each body's `FastWaterBodyProfile.query_priority` (rivers > lakes > ocean) so the query picks the right body at confluences.
3. **Stream generated maps in, don't bake into the addon.** Push flow/foam/wake maps through `set_flow_field_texture()` / `set_foam_field_texture()` / `set_wake_map_texture()` or `FastWaterFlowField.import_image()` / `FastWaterFoamField.import_image()`, keyed by world chunk.
4. **Gameplay/AI query through one surface.** Use the `FastWater` autoload (`height_at`/`depth_at`/`flow_at`/`contains_point`/`nearest_body`) — it resolves whatever bodies are currently streamed in, so create/destroy is transparent. Never query the ocean's render tiers directly.
5. **Floating origin.** When the generator rebases the world, call `FastWaterOcean.set_wave_sample_offset(accumulated_shift)` so wave math (and the matching height query) stays precise far from the origin.
6. **React to water with signals/triggers.** `FastWaterSurface.splashed`/`wake_added`, `FastWaterVolume.body_entered_water`/`body_exited_water`, and `FastWaterSwimmer` events drive audio/AI/VFX without polling.
7. **Budget by distance/quality.** Far ocean tiers shed foam + detail automatically; keep planar reflection, bow wakes, and high particle counts near the camera/hero only, and use `FastWaterQuality` per platform.

Everything is optional and node-local: disabling any helper degrades gracefully, and nothing in the addon depends on the host's terrain, lighting, or weather classes.

## Verification

Run this contract before treating the addon as world-engine ready:

```powershell
Godot --headless --path C:\Wg16\wg-16-project --script res://addons/fast_water/tools/check_fast_water_world_integration_contract.gd
```

The contract builds a lake, a tuned stream, and a near/far ocean tier in one headless scene. It verifies body-query selection, per-point river tuning, flow-field creation, profile application, public wake routing, path ripple clock correctness, ocean focus recentering, and far-ocean non-interactivity. The ocean contract additionally verifies matched macro LOD appearance and near-only high-cost detail.
