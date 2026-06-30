# Fast Water Integration Guide (10 minutes)

This is the short path to using Fast Water from a game or engine. For the deeper
world-streaming/LOD design see `WORLD_ENGINE_INTEGRATION.md`.

## 1. Enable the addon

Project Settings → Plugins → enable **Fast Water**. This registers the node types
and an autoload service named `FastWater`.

## 2. Add water

Pick the smallest body that matches the feature (all share one query API):

- **Lake / pool / hero water** → `FastWaterSurface`
- **River / stream / canal** → `FastWaterPath`
- **Open-world ocean** → `FastWaterOcean` (near interactive tier + cheap far tier)
- **Waterfall** → `FastWaterWaterfall`

```gdscript
var ocean := FastWaterOcean.new()
ocean.ocean_profile = preload("res://addons/fast_water/presets/ocean_world_lod.tres")
ocean.target_camera = $Camera3D
add_child(ocean)
```

## 3. Tune by data, not code

Duplicate any `.tres` in `addons/fast_water/presets/` (visual / ocean / body /
quality), edit it in the inspector, and assign it. Re-run
`tools/generate_fast_water_presets.gd` to rebuild the shipped set from code.

## 4. Ask "what is the water doing here?"

Use the `FastWater` autoload from anywhere — it resolves the active bodies for you:

```gdscript
var h     := FastWater.height_at(world_pos)     # surface Y, or -INF if no water
var depth := FastWater.depth_at(world_pos)      # >0 under the surface
var flow  := FastWater.flow_at(world_pos)       # current m/s (rivers/tidal)
var wet   := FastWater.contains_point(world_pos)
var body  := FastWater.nearest_body(world_pos)  # the concrete node, if needed
```

No autoload? Call `FastWaterBodyQuery` statically with the scene tree instead.

## 5. React to water (no polling)

```gdscript
surface.splashed.connect(func(pos, strength): play_splash_sound(pos, strength))
surface.wake_added.connect(func(pos, vel): spawn_wake_trail(pos))
```

`FastWaterVolume` (an `Area3D` — add a `CollisionShape3D` child) gives enter/exit
triggers and depth queries inside an authored region:

```gdscript
volume.body_entered_water.connect(func(b): b.start_swimming())
volume.body_exited_water.connect(func(b): b.stop_swimming())
```

## 6. Float actors

- **RigidBody3D** → add `FastWaterBuoyant` (probe-based buoyancy + drift).
- **CharacterBody3D / kinematic** → add `FastWaterSwimmer`; read `is_submerged` /
  `submersion_depth`, connect `entered_water` / `exited_water`, and optionally let it
  apply a vertical float toward the surface.

```gdscript
$Player/FastWaterSwimmer.entered_water.connect(_on_player_submerged)
```

## 7. Drive splashes/wakes from actors

Attach `FastWaterInteractor` to a moving body to emit splash/wake events into the
nearest water, or call `body.add_splash(pos, velocity, radius)` /
`body.add_wake_point(...)` directly. For oceans, route through the `FastWaterOcean`
node — it forwards to the interactive near tier (never query the render tiers
directly).

## 8. Weather (optional)

Feed an external weather system through `FastWaterWeatherAdapter.apply_environment_state(state)`
or the direct setters; Fast Water maps it to waves, foam, whitecaps, rain ripples,
and turbidity. It never owns weather, clouds, or time of day.

## Performance defaults worth knowing

- Open-world ocean: far tier skips foam-noise and high-cost detail (cheap), near tier
  keeps wakes/ripples/reflection.
- Distant water flattens normals and floors roughness past ~90 m to avoid specular
  shimmer (`specular_lod_*`, `distant_*` on `FastWaterSurface`).
- Floating-origin worlds: set `FastWaterOcean.set_wave_sample_offset(host_shift)` so
  wave math stays precise far from the world origin.
- Expensive helpers (planar reflection, bow wake, high particle counts) are opt-in and
  belong near the camera/hero only.
