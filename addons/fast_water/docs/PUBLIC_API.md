# Fast Water Public API

This is the stable API surface to preserve unless `ROADMAP.md` explicitly calls for a breaking change.

## Interaction Events

These methods are accepted by `FastWaterSurface`, `FastWaterPath`, and `FastWaterOcean`:

```gdscript
water.add_splash(world_pos: Vector3, velocity: Vector3, radius: float = 0.5)
water.add_wake_point(world_pos: Vector3, velocity: Vector3, radius: float = 0.5)
```

`FastWaterOcean` forwards events to its near surface. The far ocean tier must remain non-interactive.

## Surface Queries

These methods are the shared water-body query contract:

```gdscript
water.get_surface_height_at(world_pos: Vector3) -> float
water.get_water_altitude(world_pos: Vector3) -> float
water.get_water_depth_at(world_pos: Vector3) -> float
water.get_flow_at(world_pos: Vector3) -> Vector3
water.contains_water_point(world_pos: Vector3) -> bool
```

`FastWaterBodyQuery` relies on this shape. New body types should implement the same methods.

`FastWaterSurface.sample_wave_height(world_x, world_z) -> float` returns the analytic swell height used by both the shader and the height query, so buoyancy bobs with the visible surface. `FastWaterOcean.get_surface_height_at` tracks this via its near tier; `FastWaterOcean.set_wave_sample_offset(offset: Vector2)` shifts the wave sampling frame for floating-origin worlds.

## Signals

`FastWaterSurface` emits gameplay/audio events so callers do not poll:

```gdscript
signal splashed(world_pos: Vector3, strength: float)
signal wake_added(world_pos: Vector3, velocity: Vector3)
```

`FastWaterVolume` (Area3D) and `FastWaterSwimmer` add water-presence events:

```gdscript
# FastWaterVolume
signal body_entered_water(body: Node3D)
signal body_exited_water(body: Node3D)
volume.get_surface_height_at(world_pos) -> float
volume.get_submersion_depth(world_pos) -> float
volume.is_point_submerged(world_pos) -> bool

# FastWaterSwimmer  (read is_submerged / submersion_depth)
signal submerged_changed(submerged: bool)
signal entered_water()
signal exited_water()
swimmer.get_water_flow() -> Vector3
```

## FastWater Service (autoload)

Enabling the plugin registers a `FastWater` autoload that wraps `FastWaterBodyQuery` with the scene tree, so engine/gameplay code can query without resolving nodes:

```gdscript
FastWater.height_at(world_pos) -> float        # surface Y, or -INF if no water
FastWater.depth_at(world_pos) -> float          # >0 under the surface
FastWater.flow_at(world_pos) -> Vector3
FastWater.contains_point(world_pos) -> bool
FastWater.nearest_body(world_pos, include_non_containing := true) -> Node
FastWater.query(world_pos, include_non_containing := true) -> Dictionary
```

Projects that prefer no autoload can call `FastWaterBodyQuery` statically with the tree instead.

## Texture Bindings

`FastWaterSurface` accepts external runtime textures:

```gdscript
surface.set_wake_map_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float)
surface.set_flow_field_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float, encode_scale_mps: float)
surface.set_foam_field_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float)
```

These methods should stay lightweight and should not allocate large resources per frame.

## Flow And Foam Map Baking

`FastWaterFlowField` and `FastWaterFoamField` expose PNG map round-trips for projects that want to bake or hand-author maps:

```gdscript
flow_field.export_image("res://flow.png") -> int
flow_field.import_image("res://flow.png", origin_xz, world_size_m, flow_encode_scale_mps) -> int

foam_field.export_image("res://foam.png") -> int
foam_field.import_image("res://foam.png", origin_xz, world_size_m) -> int
```

The current format is RGBA8. Flow maps encode flow direction/speed in RG, foam in B, and mask in A. Foam maps encode foam intensity in R, age in G, source kind in B, and opacity in A.

## Profiles And Weather

Profiles are data-first and safe to construct at runtime:

```gdscript
surface.apply_visual_profile(profile)
surface.apply_quality(quality)
surface.apply_environment_state(state, response)
surface.emit_rain_ripples(delta, intensity, world_center, area_size_m, wind_velocity, response, max_stamps)
ocean.apply_ocean_profile(profile)
```

Weather integration must remain a bridge. Fast Water consumes `FastWaterEnvironmentState`; it does not own seasons, clouds, time of day, fog, lightning, or global lighting.

## Debug And Inspection

Stable debug helpers:

```gdscript
ocean.get_surface_nodes() -> Array[Node]
ocean.get_ocean_debug_state() -> Dictionary
authoring_overlay.rebuild_now()
authoring_overlay.get_debug_state() -> Dictionary
waterfall.get_lod_state() -> Dictionary
waterfall.stamp_waterfall_response() -> int
```

`get_lod_state()` returns a dictionary with `state`, `visible`, `lod_enabled`, distance thresholds, segment counts, and spray-enabled state. The `state` value is one of `near`, `far`, or `culled`.

Contract gates may inspect internal state for deterministic tests, but gameplay code should stay on the public methods above.
