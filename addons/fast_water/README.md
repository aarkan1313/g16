# Fast Water

Shader-first, event-driven water interactions for Godot 4.x projects.

This add-on is intentionally small. It is meant to be copied into any project as `res://addons/fast_water` and extended from there.

**Drop-in, tunable, and world-generator ready.** It is fully self-contained (references nothing outside `res://addons/fast_water`), every module is optional, tuning is data-driven through `.tres` presets in `presets/`, and gameplay/engine code queries water through one `FastWater` autoload (or `FastWaterBodyQuery`). To integrate: `docs/INTEGRATION_GUIDE.md` (10-minute path) and `docs/WORLD_ENGINE_INTEGRATION.md` (streaming/world-generator wiring + floating origin). `docs/PACKAGING.md` has the export checklist.

## Current Visual Status - 2026-06-30

Fast Water is mechanically broad and now has a clean live hero surface around floating objects. The hard grey buoy-locked polygon was traced to analytic hero ripples displacing coarse water vertices; hero ripples now stay in the fragment normal/foam path so they do not punch triangular depth holes through the water. The current D3D12 river and full-proof render gates pass, the live surface demo is clean around the buoy, and the full headless contract sweep includes an explicit world-integration contract for lake, stream/river, and open-ocean composition.

The remaining visual work is art-direction tuning, not package architecture: tune accepted hero references, biome-specific profiles, riverbank materials, and weather/ocean presets against project targets.

Use `addons/fast_water/HANDOFF_PROMPT_FIX_WEIRD_WATER_SURFACE_SHAPES_2026_06_29.md` as the current ownership handoff. Older 2026-06-29 handoff prompts are historical context and may describe pre-remediation failures.

## What It Includes

- `FastWaterSurface`: owns the water mesh, material, hero ripples, and optional splash pool.
- `FastWaterOcean`: optional open-world wrapper that composes near/far `FastWaterSurface` tiers around a camera or hero actor.
- `FastWaterOceanProfile`: resource for tuning ocean mesh cost, local wake coverage, swell, chop, whitecaps, horizon fade, and distant reflection.
- `FastWaterPath`: optional river/canal/path ribbon that uses the same water shader and exposes water height/flow queries.
- `FastWaterBodyProfile`: body-level semantics for lakes, pools, rivers, oceans, and future water types.
- `FastWaterBodyQuery`: shared lookup/query helper for height, altitude, depth, containment, flow, and active body selection.
- `FastWaterBodyDebugOverlay`: optional debug line overlay for body bounds, flow vectors, and altitude probes.
- `FastWaterAuthoringOverlay`: optional editor/runtime line overlay for path controls, river widths, flow arrows, waterfall lips, foam-field bounds, and foam source samples.
- `FastWaterFlowField`: optional generated flow/foam texture for rivers and other directed water.
- `FastWaterFoamField`: optional persistent source-aware foam texture for shoreline, wake, impact, rain, rapids, waterfall lip, plunge pool, and eddy sources.
- `FastWaterFoamReactiveFx`: optional bridge that turns foam source events into bubble and spray bursts without coupling the foam field to one particle implementation.
- `FastWaterWaterfall`: optional falling-sheet mesh producer that stamps waterfall lip and plunge-pool foam sources.
- `FastWaterWaterfallSprayFx`: optional continuous lip mist, shelf spray, and plunge mist emitter for waterfalls.
- `FastWaterInteractor`: attach to a `RigidBody3D`, `CharacterBody3D`, or moving `Node3D` to emit splash and wake events.
- `FastWaterBuoyant`: optional `RigidBody3D` helper that floats against any Fast Water surface/path query provider.
- `FastWaterVolume`: optional `Area3D` trigger that re-emits enter/exit as water events and answers depth queries inside an authored region.
- `FastWaterSwimmer`: optional `CharacterBody3D`/kinematic submersion helper (is-submerged state, enter/exit signals, optional vertical float).
- `FastWater`: optional autoload service for one-line queries (`FastWater.height_at(pos)`, `depth_at`, `flow_at`, `contains_point`, `nearest_body`).
- `presets/`: shipped data-driven `.tres` presets (visual / ocean / body / quality) — duplicate and tune in the inspector; see `docs/INTEGRATION_GUIDE.md`.
- `demo/fast_water_showcase.tscn`: performant proof scene — an island in the ocean with a river running down the mountainside, buoyant objects offshore, and a ball splashing into a hillside pool (free-fly: right-mouse + WASD). Render proof shots with `tools/render_fast_water_showcase.gd`.
- `FastWaterPlanarReflection`: optional SubViewport reflection helper for hero shots, with an `update_hz` throttle for GPU efficiency.
- `FastWaterBowWake`: optional close-up bow/contact wake mesh for player boats, swimmers, or hero props.
- `FastWaterVisualProfile`: optional resource for saving/applying a water look across surface, wake map, and reflection settings.
- `FastWaterEnvironmentState`: data-only resource for water-relevant weather inputs.
- `FastWaterWeatherResponse`: resource mapping wind, rain, storm, and turbidity inputs to water response.
- `FastWaterWeatherAdapter`: optional bridge node that applies external weather state to one or more water bodies.
- `FastWaterWeatherSequence`: optional demo/test driver for clear, drizzle, heavy-rain, storm, and calm-after-storm water states.
- `FastWaterRainImpactFx`: optional water-only rain impact and surface mist particle helper driven by the weather adapter.
- `FastUnderwaterController`: toggles a screen-space underwater overlay only when the camera is submerged.
- `FastWaterSplashFx`: optional pooled splash effect helper for crown meshes and `GPUParticles3D`.
- `FastWaterWakeMap`: small runtime texture for continuous wakes and foam disturbance.
- `FastWaterGpuWakeMap`: GPU ping-pong wake backend with the same stamp API as `FastWaterWakeMap`.
- `FastWaterBubblePool`: pooled GPU bubble/spray emitters.
- `FastWaterCaustics`: cheap camera-following procedural caustic plane.
- `FastWaterWakeRibbon`: optional close-up mesh streak for hero actors that need bright video-like wake trails.
- `FastWaterSky`: procedural sky helper for demos or standalone scenes.
- `FastWaterQuality`: low/medium/high presets for wake resolution, ripple caps, bubbles, mesh density, and refraction.
- `fast_water_surface.gdshader`: depth blend, foam, Fresnel, procedural waves, and capped hero ripples.
- `fast_underwater_overlay.gdshader`: cheap tint and distortion overlay.
- `fast_splash_crown.gdshader`: shader-animated splash crown material.

## Basic Setup

1. Copy `addons/fast_water` into a Godot 4.x project.
2. Enable **Fast Water** in **Project Settings > Plugins**.
3. Add a `FastWaterSurface` node to the scene.
4. Assign `target_camera`.
5. Add `FastWaterInteractor` as a child of bodies that should affect water.
6. Add `FastWaterPlanarReflection` only for shots that need real reflected geometry.
7. Optionally add a fullscreen `ColorRect` with `fast_underwater_overlay.gdshader`, then assign it to `FastUnderwaterController.underwater_overlay`.

## Demo Scene

Open:

```text
res://addons/fast_water/demo/fast_water_demo.tscn
```

The demo builds a complete portable scene: water surface, runtime wake map, moving interactors, splash pool, bubble pool, caustics, procedural sky, shoreline/seabed, and underwater overlay wiring.

The same demo supports `--mode=rain` to show a static rainy/storm water response, `--mode=weather_sequence` to cycle through clear, drizzle, heavy rain, storm, and calm-after-storm water states, `--mode=underwater_turbid` to preview weather-driven submerged visibility loss, `--mode=waterfall` to render the falling-sheet/plunge-foam module, `--mode=ocean` to render the open-world near/far ocean tier, and `--mode=full_proof` to load the combined proof scene composition.

Dedicated sample scenes:

- pool/lake: `res://addons/fast_water/demo/fast_water_lake_demo.tscn`
- river: `res://addons/fast_water/demo/fast_water_river_demo.tscn`
- rain: `res://addons/fast_water/demo/fast_water_rain_demo.tscn`
- underwater: `res://addons/fast_water/demo/fast_water_underwater_demo.tscn`
- waterfall: `res://addons/fast_water/demo/fast_water_waterfall_demo.tscn`
- ocean: `res://addons/fast_water/demo/fast_water_ocean_demo.tscn`
- full proof: `res://addons/fast_water/demo/fast_water_full_proof.tscn`

The full-proof scene combines the hero lake surface, stricter planar reflection, caustics, wake/ripple actors, source-aware foam/flow fields, a downhill inlet river, waterfall sheet/spray/plunge foam, weather sequence, rain impact FX, underwater controller, and a small ocean tier preview. Its contract gate is `res://addons/fast_water/tools/check_fast_water_full_proof_contract.gd`; its render gate is `res://addons/fast_water/tools/render_fast_water_full_proof.gd`.

Important: `fast_water_full_proof.tscn` is a systems proof scene, not the final visual acceptance scene. It should keep proving that modules coexist, but it must not be used to claim visual acceptance until the live surface-artifact and review-camera issues are fixed.

## Hero Reference Scene

Open:

```text
res://addons/fast_water/demo/fast_water_hero_reference.tscn
```

The hero reference scene is the stable visual target for close-up water tuning. It uses `FastWaterVisualProfile.hero_pool_reference()`, a finite pool basin, submerged reference geometry, moving floaters, bow wakes, caustics, and planar reflections.

The current hero reference has been simplified to avoid rectangular curbs/blocks/rails that could be mistaken for water artifacts. Treat it as the preferred visual tuning scene until full-proof composition is split into separate showcase and module-proof scenes.

## River Demo

Open:

```text
res://addons/fast_water/demo/fast_water_river_demo.tscn
```

The river demo uses `FastWaterPath` plus its generated `FastWaterFlowField`. It shows downhill path flow, bank/slope foam sources, flow-advection in the shader, and drifters that query `get_flow_at()`. The demo scene includes vertex-colored valley terrain, wet/gravel bank ribbons, bank rocks, rapid-zone rocks, drift logs, and scale drifters so the river is evaluated in a production-style context instead of on a flat test pad.

`FastWaterPath` supports optional per-control-point arrays for `point_widths_m`, `point_depths_m`, `point_flow_speeds_mps`, `point_bank_foam_strengths`, and `point_turbulence_strengths`. Missing entries fall back to the path-level defaults, so simple rivers can still use one width/current while authored rivers can vary channel shape, flow speed, bank foam, and turbulence along the same path.

## Benchmark Scene

Open:

```text
res://addons/fast_water/benchmark/fast_water_benchmark.tscn
```

The benchmark is the fastest way to judge the module visually and measure relative cost. It exposes mobile/balanced/hero profiles, CPU/GPU wake backends, planar reflection on/off, actor count, pause, camera orbit, and a live wake-map texture preview.

## Performance Model

The shader never loops over hundreds of object contacts. It only evaluates up to 16 hero ripples. Continuous wakes route through a small runtime texture, usually 256x256 around the camera.

Recommended starting budget:

- `max_hero_ripples`: 8-12
- `mesh_subdivisions`: 64-128 near camera
- `refraction_strength`: 0 unless the shot needs it
- `FastWaterPlanarReflection.resolution_scale`: 0.4-0.6 for hero water, disabled for background water
- `splash_pool_size`: 16-24
- wake map: 256x256, 60-120 m coverage

For open-world water, use `FastWaterOcean` instead of scaling one expensive plane to world size. The ocean tiering mirrors the terrain CDLOD lesson without copying the full quadtree: keep shader waves in stable world space, snap persistent near/far surfaces around the viewer or hero focus, match macro color/wave/whitecap appearance across tiers, and spend high-cost interaction only on the near tier. `FastWaterOceanProfile.performance()` lowers mesh density, wake resolution, and wake update cost for broad views while `FastWaterOceanProfile.world_lod()` keeps a larger near tier for high-quality open-world water.

High-quality hero shots can use `FastWaterVisualProfile.hero_quality()`, but the expensive pieces stay separate:

- wake map updates are dirty-region based, so active ripples do not fade the whole texture every tick.
- `FastWaterVisualProfile.hero_quality()` opts into the GPU wake backend.
- `FastWaterPlanarReflection` should be enabled only for nearby hero water.
- `FastWaterBowWake` is for close-up actors; use the wake map for background disturbance.
- `FastWaterWakeRibbon` and `FastWaterBowWake` are optional readability helpers; core wakes still route through hero ripples, wake maps, foam fields, and bubbles when those meshes are absent.

## Visual Tuning

`FastWaterVisualProfile` is the preferred place to save a reusable look. The most important high-level controls are:

- `foam_intensity`, `shoreline_foam_strength`, and `wake_foam_strength` for separating edge foam from moving wake foam.
- `depth_absorption_strength` and `absorption_density` for how quickly shallow water falls into deep color.
- `planar_reflection_strength`, `planar_reflection_grazing_power`, and `planar_reflection_max_mix` for keeping planar reflections anchored to grazing angles instead of looking like a screen-space overlay.
- `foam_depth_m` for matching edge foam to the depth scale of the basin, riverbed, or shoreline geometry.

Current reusable profile entry points:

- `FastWaterVisualProfile.hero_pool_reference()` for close-up hero water.
- `FastWaterVisualProfile.gameplay_lake()` for a cheaper but still polished lake/pool look.
- `FastWaterVisualProfile.cheap_ocean()` for broad low-cost water.
- `FastWaterVisualProfile.mobile_low()` for constrained targets.
- `FastWaterOceanProfile.open_world()` for a two-tier ocean wrapper with local wakes plus cheap distant water.
- `FastWaterOceanProfile.performance()` for lower-cost open-world water.
- `FastWaterOceanProfile.world_lod()` for matched macro appearance with high-quality near water and cheap far water.

## Ocean/Open-World Tier

Add `FastWaterOcean` when the player can see broad water. Assign `target_camera` for camera-relative recentering, or assign `wake_focus` to keep local wake detail around a hero boat/player instead of the camera. The node creates `NearOceanSurface` and `FarOceanSurface` children:

- near surface: interactive, local wake map, hero ripples, higher mesh density, optional micro/detail normals.
- far surface: non-interactive, coarse mesh, no wake map, no refraction, no bubbles, no hero ripples, and no micro/detail noise by default.

Use `FastWaterOceanProfile` to tune `near_mesh_size_m`, `far_mesh_size_m`, subdivisions, wake-map world size, follow snaps, edge fade, swell height/frequency/speed, chop normals, whitecap strength, horizon fade, and distant reflection. Keep `match_macro_appearance_across_lod` enabled when the same ocean should read consistently at all distances; keep `high_quality_near_only` enabled so far water drops local detail while retaining the same large-scale look. `near_edge_fade_width` feathers the high-quality near mesh into the cheaper far mesh so the LOD tier does not draw a rectangular boundary. The runtime gate `res://addons/fast_water/tools/check_fast_water_ocean_contract.gd` writes `artifacts/fast_water_ocean_contract.json` and verifies near/far tier creation, focus recentering, local wake-map routing, far wake disablement, matched macro LOD appearance, near-only detail cost, edge fade, profile-driven waves/whitecaps/horizon, and cheap distant reflection.

## Debug Views

`FastWaterSurface.debug_view` and `FastWaterPath.debug_view` switch the water shader from beauty rendering to diagnostic output:

- `0`: beauty
- `1`: depth/thickness
- `2`: foam sources
- `3`: wake map channels
- `4`: normals
- `5`: flow field
- `6`: planar reflection mask
- `7`: optics/absorption/glint

The benchmark scene exposes this through the **View** button and the `V` key. The benchmark and hero reference render tools also accept `--debug-view=N`.

`FastWaterBodyDebugOverlay` is a separate scene/debug helper for checking gameplay water-body contracts. Add it to a scene, enable auto-discovery or assign `body_paths`, then toggle bounds, flow vectors, and altitude probes independently. The render gate `res://addons/fast_water/tools/render_fast_water_body_debug.gd` captures the standard artifact `artifacts/fast_water_body_debug_overlay.png`.

## Water Body Contract

Every gameplay-facing water body should implement the same small query shape:

```gdscript
body.get_surface_height_at(world_pos)
body.get_water_altitude(world_pos)
body.get_water_depth_at(world_pos)
body.get_flow_at(world_pos)
body.contains_water_point(world_pos)
```

`FastWaterSurface` and `FastWaterPath` are registered in both `fast_water_surface` and `fast_water_body` groups for compatibility. New systems should prefer `FastWaterBodyQuery`:

```gdscript
var query = FastWaterBodyQuery.find_best_body_query(get_tree(), world_pos)
var body = query.body
var depth = query.depth
var flow = query.flow
```

Use `FastWaterBodyProfile` on each body to define body kind, query priority, containment margin, default depth, flow scale, and tags. This lets gameplay code ask what the water is doing at a point without knowing whether the provider is a lake, river, ocean, or future waterfall plunge pool.

Use `FastWaterBodyDebugOverlay` while authoring to verify that body bounds, path flow direction, and above/below-surface probe results agree with the same contract.

## Foam Field

`FastWaterFoamField` is the persistent foam/whitewater module. It owns a small texture with foam intensity, age, source kind, and mask data. Use `add_foam_stamp()` or `add_source()` to inject foam from shoreline, wake, impact, rain, rapids, waterfall lip, plunge pool, and eddy sources. Assign `flow_provider` or `flow_provider_path` to a `FastWaterPath`, `FastWaterFlowField`, or compatible node so foam can advect downstream and thicken from turbulence/current while it ages and dissipates. `FastWaterSurface` can bind the field through `foam_field_node` or `set_foam_field_texture()`, and the main shader folds persistent foam into the same color, roughness, alpha, and normal-flattening response as shoreline, wake, ripple, and flow foam.

`FastWaterFoamReactiveFx` can subscribe to the foam field's `foam_source_added` signal and route wake, rain, impact, rapids, waterfall lip, and plunge-pool sources to optional bubble and spray targets. Targets only need to expose `burst(world_pos, strength, radius)` or `restart(strength, radius)`, so projects can replace the default `FastWaterBubblePool` and `FastWaterSplashFx` with their own particle systems.

`FastWaterFlowField.add_turbulence_stamp()` can inject a runtime downstream current/foam trail into a generated flow texture. Waterfalls use this to push plunge-pool turbulence into the same flow-map path as rivers, so `FastWaterSurface` receives directed flow, flow foam, normal response, and foam-field advection through existing bindings.

The runtime gate `res://addons/fast_water/tools/check_fast_water_foam_field_contract.gd` writes `artifacts/fast_water_foam_field_contract.json` and verifies source-aware stamps, source IDs, material binding, foam aging/dissipation, downstream advection, and turbulence/current thickening. The runtime gate `res://addons/fast_water/tools/check_fast_water_foam_reactive_fx_contract.gd` writes `artifacts/fast_water_foam_reactive_fx_contract.json` and verifies foam-driven bubble/spray routing, source filtering, per-frame event budgeting, radius scaling, and strength clamping.

`FastWaterFlowField.export_image(path)` and `FastWaterFoamField.export_image(path)` save their current RGBA maps as PNGs. `import_image(path, origin_xz, world_size_m, ...)` loads authored or baked maps back into matching runtime nodes. The authoring contract gate verifies that exported flow and foam maps round-trip through imported nodes without losing sampled current, foam intensity, or foam source kind.

## Waterfalls

`FastWaterWaterfall` is the first modular waterfall producer. Add it as a `MeshInstance3D`, author `lip_points` in local space, then tune `drop_height_m`, `downstream_offset_m`, `fall_direction_xz`, and `vertical_segments` to generate the falling sheet. Its default `fast_waterfall_sheet.gdshader` provides animated streak breakup, lip foam, base foam, alpha, and color controls.

Assign `foam_field` or `foam_field_path` to stamp `WATERFALL_LIP` and `PLUNGE_POOL` source kinds through the same `FastWaterFoamField` used by shoreline, wake, rain, and rapids foam. Assign `flow_field` or `flow_field_path` to stamp downstream plunge turbulence through `FastWaterFlowField.add_turbulence_stamp()`. Optional `bubble_target` and `spray_target` nodes can expose `burst(world_pos, strength, radius)` or `restart(strength, radius)` for immediate plunge effects; projects can also route the foam field into `FastWaterFoamReactiveFx` for source-aware bubbles and spray.

`FastWaterWaterfall` can auto-create `FastWaterWaterfallSprayFx` as `WaterfallSprayFx`. The spray FX uses three continuous GPU emitters: lip mist along the authored lip, shelf spray at `shelf_points`, and plunge mist along the base line. Disable `auto_create_waterfall_spray_fx` or assign `waterfall_spray_fx_path` to replace it.

For open-world scenes, enable `lod_enabled` and assign `target_camera` or `target_camera_path`. The waterfall switches between near, far, and culled states using `far_lod_distance_m` and `max_visible_distance_m`; far LOD can reduce `vertical_segments` and disable continuous spray through `disable_spray_in_far_lod`.

The runtime gate `res://addons/fast_water/tools/check_fast_water_waterfall_contract.gd` writes `artifacts/fast_water_waterfall_contract.json` and verifies generated sheet geometry, material wiring, lip/plunge placement, waterfall source stamps, downstream flow-field turbulence stamps, optional effect target calls, lip/shelf/plunge spray emitter routing, and near/far/culled LOD behavior.

## Underwater Tuning

`FastUnderwaterController` drives the screen-space underwater overlay only while the camera is submerged. The controller exposes the reusable tuning controls for:

- `distortion_strength` and `tint_strength` for the base submerged look.
- `max_effect_depth_m` for fading from near-surface to deeper water.
- `surface_haze_strength`, `light_shaft_strength`, and `caustic_strength` for light readability.
- `particulate_strength` and `particulate_scale` for suspended matter.
- `waterline_strength`, `waterline_width`, and `waterline_screen_y` for the soft near-surface transition band.

When driven by `FastWaterWeatherAdapter`, `FastUnderwaterController.apply_environment_state()` maps `water_turbidity`, rain, and storm state through `FastWaterWeatherResponse` into submerged visibility loss, extra particulates, stronger tint/vignette, lower caustic readability, and reduced light shafts. The controller remains usable without a weather system because the base overlay values are still direct exports.

## Feature Parity Target

- Dynamic ripples: yes, via hero rings plus live wake map.
- Splash crowns: yes, pooled default `FastWaterSplashFx`.
- Underwater post-process: yes, toggled only when submerged.
- Bubbles: yes, pooled `GPUParticles3D`.
- Caustics: yes, procedural camera-following plane.
- Edge foam: yes, depth-buffer based.
- Depth blending: yes.
- Procedural waves: yes.
- Sun shimmer/specular: yes, through spatial lighting, Fresnel, and animated normals.
- Procedural sky: yes.
- Demo scene: yes.

## Extension Points

- Replace hero wake events with a runtime wake map.
- Add `GPUParticles3D` bubbles to a pooled splash scene.
- Add a distance-limited caustic decal/projector.
- Add quality presets that adjust mesh density, ripple cap, particle counts, and wake-map resolution.

The public interaction API is deliberately small:

```gdscript
water_surface.add_splash(world_pos, velocity, radius)
water_surface.add_wake_point(world_pos, velocity, radius)
water_surface.set_wake_map_texture(texture, origin_xz, world_size_m)
water_surface.set_flow_field_texture(texture, origin_xz, world_size_m, encode_scale_mps)
water_surface.set_foam_field_texture(texture, origin_xz, world_size_m)
water_surface.get_surface_height_at(world_pos)
water_surface.get_water_altitude(world_pos)
water_surface.get_water_depth_at(world_pos)
water_surface.get_flow_at(world_pos)
water_surface.contains_water_point(world_pos)
```

`FastWaterSurface` and `FastWaterPath` both accept `add_splash()` and `add_wake_point()` from `FastWaterInteractor`. Surface water can route those events into a wake map; path water keeps the same API and renders local hero ripples on the path material. `FastWaterBuoyant` uses `FastWaterBodyQuery`, so the same buoyancy component can float against either a plane/lake surface or a river/path body and receive authored path flow.

`FastWaterOcean` also accepts `add_splash()` and `add_wake_point()` and forwards them to its near surface so existing interaction code can drive ocean wakes without knowing about the internal far tier.

The runtime gate `res://addons/fast_water/tools/check_fast_water_interaction_contract.gd` writes `artifacts/fast_water_interaction_contract.json` and verifies lake/path interactor emissions, lake/path buoyant lift, river current drift, and body query selection.

The runtime gate `res://addons/fast_water/tools/check_fast_water_optional_helpers_contract.gd` writes `artifacts/fast_water_optional_helpers_contract.json` and verifies that core wake readability survives without `FastWaterBowWake` or `FastWaterWakeRibbon` helper nodes by checking hero ripples, wake-map channels, foam-field stamps, and bubble routing.

The runtime gate `res://addons/fast_water/tools/check_fast_water_river_authoring_contract.gd` writes `artifacts/fast_water_river_authoring_contract.json` and verifies per-point river width, depth, current, bank foam, turbulence, containment, and flow-field bake behavior.

The runtime gate `res://addons/fast_water/tools/check_fast_water_river_demo_contract.gd` writes `artifacts/fast_water_river_demo_contract.json` and verifies that the river demo contains the expected terrain, bank ribbons, rock density, rapid rocks, drift logs, flow drifters, river body, and flow field. The render gate `res://addons/fast_water/tools/render_fast_water_river_demo.gd` captures `artifacts/fast_water_river_demo.png` and can also capture `artifacts/fast_water_river_flow_debug_texture.png`.

## Weather Boundary

Fast Water is weather-aware, but it is not a weather system. External weather should own clouds, seasons, time of day, precipitation scheduling, fog, lightning, and global lighting. Fast Water only consumes water-relevant state.

Use `FastWaterWeatherAdapter` when a real weather system exists:

```gdscript
adapter.apply_environment_state(state)
adapter.set_wind(direction_xz, speed_mps, gust_strength)
adapter.set_rain_intensity(rain_intensity)
adapter.set_storm_intensity(storm_intensity)
adapter.set_turbidity(water_turbidity)
```

The adapter applies transient runtime modifiers to water surfaces, rain impact FX, and assigned or auto-discovered `FastUnderwaterController` nodes. It can also emit rain ripple stamps into the wake map. Removing the adapter returns the water to profile-driven behavior.

`FastWaterRainImpactFx` is the optional visible precipitation response for water surfaces. Add it as a child named `RainImpactFx`, put it in the `fast_water_rain_impact_fx` group, or assign it through `FastWaterWeatherAdapter.rain_impact_fx_paths`. The adapter calls `apply_rain(intensity, world_center, area_size_m, wind_velocity, response)` so projects can replace the default particle helper with their own water-only rain impact system.

`FastWaterWeatherResponse` also maps wind, gust, and storm inputs to procedural whitecap strength. The main water shader folds those storm whitecaps into the existing foam material response, so roughness, opacity, color, and normal flattening remain coherent with the other foam sources.

For submerged views, `FastWaterWeatherResponse` exposes underwater turbidity controls for visibility loss, tint boost, distortion boost, particulates, vignette, caustic loss, light loss, turbid tint, and suspended-silt color.

`FastWaterWeatherSequence` is only a deterministic demo/test driver. It interpolates named water-relevant states and sends them to `FastWaterWeatherAdapter` through `FastWaterEnvironmentState`, matching the same integration path a real weather system would use.

The runtime gate `res://addons/fast_water/tools/check_fast_water_rain_impact_fx_contract.gd` writes `artifacts/fast_water_rain_impact_fx_contract.json` and verifies weather-adapter routing, impact/mist emitter activation, coverage sizing, wind drift, group discovery, and clean shutdown when rain stops. The runtime gate `res://addons/fast_water/tools/check_fast_water_weather_whitecap_contract.gd` writes `artifacts/fast_water_weather_whitecap_contract.json` and verifies calm/storm whitecap strength, wind direction, wave/normal/foam/glint response, and reset behavior. The runtime gate `res://addons/fast_water/tools/check_fast_water_weather_sequence_contract.gd` writes `artifacts/fast_water_weather_sequence_contract.json` and verifies the clear, drizzle, heavy-rain, storm, and calm-after-storm sequence stages plus adapter, surface, and rain-FX response. The runtime gate `res://addons/fast_water/tools/check_fast_water_underwater_turbidity_contract.gd` writes `artifacts/fast_water_underwater_turbidity_contract.json` and verifies adapter routing into underwater controllers plus clear/turbid changes to visibility, tint, distortion, particulates, caustics, and light shafts.

## Waterways-Inspired Modules

See `WATERWAYS_REVIEW.md` for the feature review. The current integration keeps the pieces modular:

- Finite plane/lake water stays `FastWaterSurface`; open-world ocean water uses `FastWaterOcean` as a wrapper around near/far surfaces.
- Authored shaped water uses `FastWaterPath`.
- Directed current/river foam uses `FastWaterFlowField`.
- Physics uses `FastWaterBuoyant`.
- Expensive reflected geometry uses `FastWaterPlanarReflection`.
- Close hero contact highlights use `FastWaterBowWake`.
- Saved/tunable looks use `FastWaterVisualProfile`.
- Weather response is a bridge through `FastWaterWeatherAdapter`, not a replacement for a real weather system.
- Future flow/foam baking should live in a separate authoring module, not in the base shader.

## Roadmap

See `ROADMAP.md` for the modular AAA water plan, including flow fields, downhill rivers, unified foam/whitewater, waterfalls, rain response, large-water/ocean tiers, and the weather integration boundary. Fast Water should react to weather inputs, but it should not own the project's weather, clouds, seasons, time of day, or global lighting systems.

See `CHECKLIST.md` for the current phase-by-phase completion checklist.

Release docs:

- `docs/MODULE_REFERENCE.md`
- `docs/PUBLIC_API.md`
- `docs/WORLD_ENGINE_INTEGRATION.md`
- `docs/COMPATIBILITY.md`
- `docs/HERO_VISUAL_ACCEPTANCE.md`
- `docs/PACKAGING.md`
- `docs/TROUBLESHOOTING.md`
