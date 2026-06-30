# Fast Water Roadmap

Fast Water should grow into a reusable Godot water add-on, not a one-off scene trick. The target is AAA-inspired water architecture with practical Godot budgets: modular nodes, tunable resources, optional expensive effects, and a clean contract for external weather and environment systems.

The rule for every phase is simple: the base water must remain useful without rivers, weather, waterfalls, reflections, bubbles, or editor baking enabled.

## Product Goals

- Drop-in add-on under `res://addons/fast_water`.
- Small public gameplay API for splashes, wakes, surface height, and flow queries.
- Tunable look through resources and presets, not scattered hardcoded shader constants.
- Optional visual tiers for hero shots, gameplay water, mobile water, rivers, waterfalls, and oceans.
- Weather-aware water response without Fast Water becoming the project's weather system.
- Debuggable channels for depth, foam, wake, flow, reflections, underwater state, and performance cost.
- No copied code, shaders, textures, or assets from external water packages.

## Non-Goals

- Do not own global weather simulation.
- Do not own project-wide lighting, clouds, time of day, seasons, or biomes.
- Do not require baked maps for the base water surface.
- Do not make the main shader loop over every actor or splash.
- Do not couple the add-on to WG16 terrain, lab lighting, or a specific game scene.

## Current Foundation

The add-on already has the right modular direction:

- `FastWaterSurface`: base water mesh, shader parameters, hero ripples, splash routing, wake map routing, and surface queries.
- `FastWaterPath`: authored path/ribbon water with height and flow queries.
- `FastWaterFlowField`: generated RG flow, B foam, A mask texture for directed water.
- `FastWaterWakeMap` and `FastWaterGpuWakeMap`: CPU/GPU disturbance maps with a shared stamp-style API.
- `FastWaterVisualProfile`: reusable look and performance profile.
- `FastWaterQuality`: coarse quality presets.
- `FastWaterEnvironmentState`, `FastWaterWeatherResponse`, and `FastWaterWeatherAdapter`: water-specific weather bridge.
- `FastWaterInteractor`: attachable actor-to-water event source.
- `FastWaterBuoyant`: optional physics consumer of water height and flow.
- `FastWaterPlanarReflection`: optional hero reflection tier.
- `FastUnderwaterController`: camera-submerged underwater overlay trigger.
- `FastWaterSplashFx`, `FastWaterBubblePool`, `FastWaterCaustics`, `FastWaterBowWake`, and `FastWaterWakeRibbon`: optional effect helpers.

The next work should preserve this shape and add missing systems around it instead of turning `FastWaterSurface` into a giant owner.

## Resolved Visual Blocker - 2026-06-29 (square/rectangle shapes)

The "weird square/rectangle shapes" were root-caused and fixed:

- The grey buoy-locked polygon was analytic hero ripples displacing coarse water vertices; hero ripples now drive the fragment normal/foam path only, so they no longer punch triangular depth holes (see README, 2026-06-30).
- Finite wake/flow/foam maps now feather to their neutral value across the outer band instead of drawing a hard square footprint (`map_edge_fade` in `fast_water_surface.gdshader`).
- Near/far ocean tiers alpha-fade at their mesh edges (`mesh_edge_fade_width` / `far_edge_fade_width`), so no rectangular LOD overlay or hard far-ocean edge shows.

Headless ocean, full-proof, and world-integration render gates render clean. Remaining Phase 0 items are art-direction tuning (hero profile in motion) and verifying review-camera controls in the launched scenes — tracked in `CHECKLIST.md`, not blockers for world-engine integration.

## World-LOD Hardening - 2026-06-30

Following an infinite-world audit, the ocean tier was hardened against the WG16/WG17 terrain principles:

- `FastWaterBodyQuery` now resolves ocean positions to the `FastWaterOcean` facade only; render tiers are non-queryable (`enabled = false` profile) so wakes/splashes never route to the non-interactive far tier.
- Ocean `get_surface_height_at` tracks the analytic swell (shared with the shader); `get_flow_at` forwards to the near tier for host-assigned currents.
- Far tier dissolves its geometric boundary and completes the horizon fade inside its half-extent (no hard ocean edge from elevation).
- Far tier skips the 3-call foam-noise stack so far water is genuinely cheap, not just feature-reduced.
- `check_fast_water_ocean_contract.gd` and `check_fast_water_world_integration_contract.gd` gained assertions for facade-only ocean query, far-edge fade, and far foam-cost skip.

## Productionization - v0.3.0

Round focused on making the system data-tunable, easy to integrate, and more efficient:

- **Data-driven presets.** `presets/*.tres` (visual / ocean / body / quality), regenerated from the code factory functions via `tools/generate_fast_water_presets.gd`. Tuning is now inspector-first, not code-only. Gate: `check_fast_water_presets_contract.gd`.
- **Integration layer.** Outbound `splashed` / `wake_added` signals on `FastWaterSurface`; `FastWaterVolume` (Area3D enter/exit + depth); `FastWaterSwimmer` (CharacterBody3D submersion/float); and the `FastWater` autoload service for one-line world queries. Guide: `docs/INTEGRATION_GUIDE.md`. Gate: `check_fast_water_integration_contract.gd`.
- **Distant-specular LOD.** Per-pixel normal flattening + roughness floor past `specular_lod_start_m` removes far-water sparkle/aliasing without touching hero water.
- **Hero-water GPU efficiency.** Planar reflection `update_hz` throttle re-renders the reflection scene at a fixed rate instead of every frame (~75% fewer full-scene reflection renders at 30 Hz / 120 fps). Kept the portable SubViewport path rather than a RenderingDevice-compute rewrite, which would drop Compatibility support (GPU wake verified on Forward+/Mobile/Compatibility). Gates: `check_fast_water_reflection_throttle_contract.gd`, `benchmark_fast_water_hero_reflection.gd`.
- **Optional Gerstner displacement.** `gerstner_enabled` adds peaked-crest swell with a synchronized CPU height query (fixed-point inversion) so buoyancy stays aligned; default off preserves the proven look. Gate: `check_fast_water_gerstner_contract.gd`.
- **Proof scenes.** `demo/fast_water_showcase.tscn` (island: ocean + river + pool splash, ~1.1 ms GPU) with `check_fast_water_showcase_contract.gd`; the river/stream demo (`demo/fast_water_river_demo.gd`) was reworked so the terrain bed and banks are derived from the water centerline — the river sits IN its channel (full depth at centre tapering to the waterline) in a descending valley with rapids rocks, banks, drift logs, and flow-carried drifters.
- **Docs + export readiness.** `docs/INTEGRATION_GUIDE.md` and a world-generator wiring checklist in `docs/WORLD_ENGINE_INTEGRATION.md`; `MODULE_REFERENCE`/`PUBLIC_API`/`PACKAGING`/`COMPATIBILITY` updated for the new modules, signals, service, presets, and knobs. Verified self-contained (no references outside `res://addons/fast_water`).

Remaining for a future round: FFT ocean spectrum, screen-space reflection option, terrain-aware shoreline depth provider, and networked/deterministic wave clock.

## Architecture Principles

### Module Boundaries

- Core water: surface rendering, water body queries, wake texture binding, and visual profile application.
- Interaction: actor events, splash events, wake stamps, buoyancy, and gameplay queries.
- Authoring: paths, river widths, slopes, flow fields, masks, and editor helpers.
- Effects: foam, whitewater, spray, bubbles, caustics, underwater overlay, rain ripples, and waterfalls.
- Environment bridge: water-specific inputs from weather, wind, lighting, and season systems.
- Debug and QA: render gates, debug views, benchmark scenes, visual comparison captures, and performance metrics.

No module should require another optional module unless it explicitly declares that dependency.

### Runtime Contract

Keep the public API small and stable:

```gdscript
water_surface.add_splash(world_pos, velocity, radius)
water_surface.add_wake_point(world_pos, velocity, radius)
water_surface.set_wake_map_texture(texture, origin_xz, world_size_m)
water_surface.get_surface_height_at(world_pos)
water_surface.get_water_altitude(world_pos)
water_surface.get_flow_at(world_pos)
```

Future water bodies should implement the same query shape so gameplay code can ask "what is the water doing here?" without knowing whether it is a lake, river, ocean, pool, or waterfall plunge zone.

### Tuning Model

Use layered resources instead of hardcoded scene values:

- `FastWaterVisualProfile`: material and interaction look.
- `FastWaterQuality`: resolution, mesh density, particle caps, and update rates.
- `FastWaterBodyProfile`: future body semantics such as lake, river, rapids, waterfall, swamp, ocean, or stylized pool.
- `FastWaterWeatherResponse`: future mapping from weather state to waves, foam, rain ripples, clarity, and spray.
- Per-node overrides for unusual shots, but default projects should work from profiles.

The editor should make common knobs visible, but the runtime should also support applying these profiles from code.

## Weather Integration Boundary

Fast Water should be weather-aware, not weather-owned.

An external weather system should own:

- Forecast and state transitions.
- Cloud coverage and cloud rendering.
- Rain, snow, lightning, fog, seasons, and regional weather logic.
- Global wind model.
- Sun/moon direction, color, and exposure.
- World wetness, puddle spawning, and terrain/material wetness outside water.

Fast Water should own only water response:

- Wind-driven wave amplitude, direction, speed, chop, roughness, and glints.
- Rain ripple stamps on water surfaces.
- Rain impact mist and surface bubble intensity.
- Storm-driven foam, whitecaps, spray, and roughness.
- Water clarity/turbidity response for heavy rain, mud, snowmelt, or biome hints.
- Flow turbulence response in rivers and waterfalls.
- Underwater tint, distortion, particulate density, and visibility response.

### Proposed Weather Adapter

Add a data resource and a small adapter node:

- `FastWaterEnvironmentState`: data-only resource with water-relevant inputs.
- `FastWaterWeatherAdapter`: optional node that receives state from a real weather system and applies it to one or more Fast Water bodies.

Initial state fields:

```gdscript
wind_direction_xz: Vector2
wind_speed_mps: float
gust_strength: float
rain_intensity: float
snow_intensity: float
storm_intensity: float
air_temperature_c: float
water_turbidity: float
sun_direction: Vector3
sun_color: Color
sun_intensity: float
```

All fields are optional hints. If no adapter exists, Fast Water uses its own profile defaults.

### Weather Acceptance Rule

A project weather system must be able to drive Fast Water with either:

```gdscript
adapter.apply_environment_state(state)
```

or direct setters:

```gdscript
adapter.set_wind(direction_xz, speed_mps, gust_strength)
adapter.set_rain_intensity(rain_intensity)
adapter.set_storm_intensity(storm_intensity)
adapter.set_turbidity(water_turbidity)
```

Fast Water should not require the weather system to use Fast Water classes internally.

## Roadmap Phases

### Phase 0: Baseline Hardening

Goal: make the current water visually credible before adding more feature surface.

Tasks:

- Keep eliminating square/block artifacts at the shader, floor, wake-map, reflection, and refraction sources.
- Root-cause the remaining live water-surface rectangles/squares instead of only lowering alpha or hiding props.
- Add a usable free-fly or orbit review camera path for launched visual scenes.
- Stabilize planar reflections so they remain anchored during camera orbit.
- Make foam affect color, alpha, roughness, normals, and breakup as one material response.
- Split `hero_pool_reference`, `gameplay_lake`, `cheap_ocean`, and `mobile_low` profiles.
- Add debug views for depth blend, foam, wake-map channels, reflection mask, and normals.
- Keep parser and render gates as the minimum proof set.

Acceptance:

- Benchmark and demo screenshots have no obvious grid/square artifacts.
- Underwater screenshot is nonblank and correctly toggles only while submerged.
- Hero profile remains close to current measured budget, with expensive features optional.

### Phase 1: Stable Water Body Contracts

Goal: make every water type queryable and interchangeable.

Tasks:

- Define a shared query convention for height, altitude, flow, depth, and containment.
- Add `FastWaterBodyProfile` resource for body-level semantics.
- Ensure `FastWaterSurface` and `FastWaterPath` both satisfy the same query behavior.
- Add a body registry or group helper for "nearest/active water body" lookups.
- Add debug overlays for bounds, flow vectors, and altitude probes.

Acceptance:

- Buoyancy and interactors can work against either a plane/lake surface or a path/river body.
- Gameplay code does not need to know which concrete node supplies water.

### Phase 2: Flow Fields And Downhill Rivers

Goal: move from path ribbons to believable rivers that know current, slope, bank foam, and turbulence.

Tasks:

- Add `FastWaterFlowField` as an optional module.
- Use flow map channels as RG velocity, B foam source, A mask/reserved.
- Extend `FastWaterPath` with per-point width, depth, slope/current, bank foam, and turbulence.
- Generate a local flow field from authored river paths.
- Drive shader normal advection, foam streaking, and wake-map distortion from flow.
- Add a river/downhill demo with stones, banks, bends, and changing slope.

Acceptance:

- River foam stretches downstream instead of sitting in screen space.
- Buoyant objects drift with the authored current.
- Slow river, fast river, and rapids are profile changes, not separate hardcoded scenes.

### Phase 3: Unified Foam And Whitewater

Goal: replace pasted-on white overlays with source-aware foam and whitewater.

Tasks:

- Introduce a foam source model: shoreline, wake, impact, rain, rapid, waterfall lip, plunge pool, and persistent eddies.
- Add `FastWaterFoamField` or fold foam into the flow field when practical.
- Make foam age, advect, dissipate, and thicken based on current and turbulence.
- Make whitewater drive material response: color, opacity, roughness, normal flattening, small displacement, and bubble/spray triggers.
- Keep close hero meshes only as optional readability helpers.

Acceptance:

- Foam has direction, age, breakup, and physical source context.
- Bow wake and ribbon helpers can be disabled without losing the main interaction read.

### Phase 4: Waterfalls, Spray, And Plunge Pools

Goal: support vertical and near-vertical water without bloating the base surface.

Tasks:

- Add `FastWaterWaterfall` as a separate effect/body node.
- Generate falling sheet/ribbon meshes from top lip curves.
- Use world-space flow noise and thickness masks for falling sheets.
- Add mist/spray emitters at lips, collision shelves, and plunge pools.
- Stamp plunge foam and downstream turbulence into the foam/flow fields.
- Provide culling, LOD, and cheap distant waterfall mode.

Acceptance:

- A waterfall can be placed beside a lake or river without changing the base lake shader.
- Plunge pool foam and downstream turbulence are connected to the waterfall source.
- Spray can be disabled separately from falling sheet rendering.

### Phase 5: Rain And Weather Response

Goal: make water respond to weather inputs while remaining decoupled from the real weather system.

Tasks:

- Add `FastWaterEnvironmentState` and `FastWaterWeatherAdapter`.
- Add water-only rain ripple emission into the wake/foam system.
- Add wind-to-wave response mapping through `FastWaterWeatherResponse`.
- Add storm whitecaps and roughness response.
- Add rain impact bubbles, mist, and optional surface dimpling.
- Add underwater turbidity and visibility response.
- Add a weather demo with scripted clear, drizzle, heavy rain, storm, and calm-after-storm states.

Acceptance:

- A test script can drive water weather using direct setters without any global weather dependency.
- Real weather can plug in later by sending the same data.
- Disabling the adapter returns water to profile-controlled behavior.

### Phase 6: Large Water And Ocean Tier

Goal: support open-world views without making small lakes expensive.

Tasks:

- Add camera-relative large-water mesh or clipmapped mesh tier.
- Add far-water material behavior separate from close hero water.
- Add profile-driven swell, chop, whitecaps, and horizon fade.
- Keep wake maps local around the camera or active hero actors.
- Add optional cheaper reflection mode for distant water.

Acceptance:

- A large water body can render around the player without losing local wake detail.
- Ocean cost scales by quality tier and distance, not by world size.

Current implementation:

- `FastWaterOcean` composes camera/hero-relative near and far `FastWaterSurface` tiers.
- `FastWaterOceanProfile` owns near/far mesh cost, local wake coverage, swell, chop, whitecaps, horizon fade, and cheap distant reflection.
- `check_fast_water_ocean_contract.gd` verifies tier creation, focus recentering, local wake routing, far wake disablement, profile-driven material state, and cheap distant reflection.
- `fast_water_demo.gd --mode=ocean` renders the open-world ocean tier through the shared demo render gate.

### Phase 7: Authoring Tools And Debug Views

Goal: make the system usable by real projects, not only by the person who wrote it.

Tasks:

- Add editor gizmos for paths, widths, flow direction, fall lips, and foam sources.
- Add import/export for flow and foam maps where baking is wanted.
- Add debug view switching from the benchmark UI.
- Add docs for each module and common setup paths.
- Add sample scenes for pool/lake, river, waterfall, rain, underwater, and ocean.

Current implementation:

- `docs/MODULE_REFERENCE.md`, `docs/PUBLIC_API.md`, `docs/COMPATIBILITY.md`, and `docs/PACKAGING.md` document the module map, stable API, renderer/version target, sample scenes, sidecar policy, and release gates.
- Dedicated sample wrappers cover lake/pool, rain, underwater, waterfall, and ocean; the river demo remains its own scene.
- `FastWaterAuthoringOverlay` provides editor/runtime lines for path controls, river widths, flow arrows, waterfall lips/plunge/shelves, foam-field bounds, and foam source samples.
- `FastWaterFlowField` and `FastWaterFoamField` support PNG `export_image()` and `import_image()` map round-trips for baked/hand-authored maps.
- `check_fast_water_authoring_contract.gd` verifies authoring overlay channels plus flow/foam export/import round-trips.
- Clean-copy verification has been run against a temporary project by copying only `addons/fast_water`, running Godot import, and parsing all copied addon scripts.

Acceptance:

- A developer can create a lake, river, and waterfall from documented nodes and profiles.
- Every hidden map or generated field has an inspectable debug view.

### Phase 8: Packaging And Compatibility

Goal: ship as a clean reusable addon.

Tasks:

- Keep plugin startup clean in fresh Godot projects.
- Audit sidecar `.uid` and generated files before release.
- Add minimal examples and troubleshooting notes.
- Add a compatibility matrix for renderer, Godot version, mobile/desktop, Forward Plus/Mobile.
- Keep public APIs stable and versioned after first release.

Acceptance:

- Copying `addons/fast_water` into a clean project works.
- Existing users can upgrade profiles and nodes without scene rewrites.

Current implementation:

- `docs/COMPATIBILITY.md` contains the current renderer/Godot/platform matrix.
- `docs/TROUBLESHOOTING.md` covers common visual, wake, river, foam, waterfall, ocean, and sidecar issues.
- `docs/PUBLIC_API.md` documents the stable public methods for interactions, surface queries, texture bindings, map baking, profiles, weather, and debug inspection.
- `docs/PACKAGING.md` documents copy layout, sample scenes, generated-file policy, release gates, and versioning.

## Priority Implementation Plan

The best next build slice is:

1. Add `FastWaterFlowField` resource/node and wire it into surface/path material parameters.
2. Extend `FastWaterPath` enough to generate a simple downhill river flow field.
3. Add a river/downhill demo that shows current, bank foam, object drift, and wake advection.
4. Add `FastWaterEnvironmentState` and `FastWaterWeatherAdapter` as data-only integration scaffolding.
5. Add rain ripple response as the first weather-driven effect.

This sequence matters because river flow and foam advection are foundational for waterfalls, rapids, rain runoff, and storm water. Weather integration should come in early as a boundary, but the first visible weather feature can stay small.

Current status: this first slice is implemented as an initial version. Phase 0 now has shader debug views, clearer hero/gameplay lake profiles, a dedicated hero pool reference scene/render gate, a hero visual metrics artifact, a hero motion review strip with temporal metrics, an enforceable accepted-reference comparison mode for approved hero targets, a visual review packet that builds a numbered contact sheet/manifest from render artifacts, and a tunable underwater overlay with depth fade, particles, light shafts, caustic haze, and waterline shimmer. Phase 1 now has `FastWaterBodyProfile`, shared query semantics, `FastWaterBodyQuery` for nearest/active body selection, `FastWaterBodyDebugOverlay` for body bounds, flow vectors, and altitude probes, plus runtime contract gates proving the documented public API, `FastWaterInteractor`, and `FastWaterBuoyant` against both `FastWaterSurface` and `FastWaterPath`. Phase 2 now has per-control-point river width, depth, current, bank foam, and turbulence authoring wired into path queries, mesh width, containment, fallback flow, and flow-field baking, plus runtime proofs for authored-current buoyancy drift, river authoring behavior, and the production-style river demo composition. Phase 3 now has `FastWaterFoamField` for persistent source-aware foam stamps that bind into the main shader's foam material response and can age, advect, dissipate, and thicken from turbulence/current through a compatible flow provider. `FastWaterFoamReactiveFx` now routes source-aware foam events into modular bubble and spray targets with filtering, scaling, clamping, and per-frame budgeting. The optional-helper contract now proves bow wake and wake-ribbon meshes can be absent while core hero ripples, wake-map channels, foam-field stamps, and bubble routing still update. Phase 4 now has `FastWaterRainImpactFx`, an optional water-only particle helper for visible rain impacts and surface mist driven by the weather adapter, procedural storm whitecaps driven by wind, gust, and storm state inside the main foam material response, `FastWaterWeatherSequence` for the demo/test clear-to-storm-to-calm weather progression, and weather-driven underwater turbidity/visibility response routed into `FastUnderwaterController`. Phase 5 now has `FastWaterWaterfall`, a lip-authored falling-sheet mesh/material, contract-proven waterfall lip/plunge foam source stamping, optional bubble/spray target calls, `FastWaterWaterfallSprayFx` for continuous lip mist, shelf spray, and plunge mist, downstream waterfall current/foam/turbulence stamps through `FastWaterFlowField`, plus distance-based near/far/culled LOD controls. Phase 8 now has a release audit contract that verifies copy layout, docs, plugin registration symmetry, sample scenes, shader resources, public API coverage and contract tooling, visual-review tooling, hero-motion tooling, and generated sidecar hygiene. Phase 3/4/5 still need visual iteration and editor ergonomics before final visual acceptance.

## AAA Quality Bar

Use these checks before calling any phase done:

- Visual: water reads as a material, not a transparent blue plane with overlays.
- Motion: wakes, foam, ripples, and current move in coherent directions.
- Lighting: reflection, Fresnel, sun glint, roughness, and depth color agree with the same normal field.
- Interaction: bodies create believable disturbance without shader-side per-object loops.
- Modularity: disabling optional modules degrades gracefully.
- Tuning: a project can save the look in resources and apply it across scenes.
- Weather: external weather can drive water response without depending on Fast Water internals.
- Performance: each optional feature has a visible cost control and can be disabled.
- Debuggability: important hidden fields can be inspected in benchmark/demo scenes.
