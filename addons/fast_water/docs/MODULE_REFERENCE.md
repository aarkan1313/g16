# Fast Water Module Reference

This document is the editor-facing map of the addon. All modules are optional unless noted.

## Core Water

- `FastWaterSurface`: finite plane/lake/pool water. Owns the mesh, shader material, hero ripples, optional wake map, optional foam field, optional splash pool, and body-query methods.
- `FastWaterOcean`: open-world wrapper. Creates matched macro-appearance `NearOceanSurface` and `FarOceanSurface` child surfaces around a camera or hero focus.
- `FastWaterOceanProfile`: tuning resource for near/far mesh cost, follow snaps, local wake map coverage, swell, chop, whitecaps, horizon fade, distant reflection, and high-quality-near-only world LOD policy.
- `FastWaterVisualProfile`: reusable water look for color, waves, foam, reflections, refraction, wake-map cost, whitecaps, and horizon fade.
- `FastWaterQuality`: coarse performance preset resource for wake resolution, ripple caps, mesh density, refraction, and bubble count.

## Bodies And Gameplay Queries

- `FastWaterBodyProfile`: body semantics for lake, pool, river, rapids, waterfall, ocean, swamp, and generic water.
- `FastWaterBodyQuery`: helper that chooses the active water body and queries height, altitude, depth, containment, and flow.
- `FastWaterBodyDebugOverlay`: editor/debug overlay for water-body bounds, flow vectors, and altitude probes.
- `FastWaterAuthoringOverlay`: editor/runtime authoring overlay for path control lines, channel widths, flow arrows, waterfall lip/plunge/shelf lines, foam-field bounds, and foam source samples.
- `FastWaterBuoyant`: `RigidBody3D` helper that applies buoyancy against any body implementing the shared query contract.
- `FastWaterSwimmer`: submersion helper for `CharacterBody3D`/kinematic actors — exposes `is_submerged`/`submersion_depth`, emits `entered_water`/`exited_water`, and can apply a vertical float toward the surface.
- `FastWaterVolume`: optional `Area3D` trigger that re-emits body enter/exit as `body_entered_water`/`body_exited_water` and answers surface-height/depth queries inside an authored region.
- `FastWaterInteractor`: emits splash and wake events from moving `RigidBody3D`, `CharacterBody3D`, or `Node3D` targets.
- `FastWater`: optional autoload service (registered by `plugin.gd`) for one-line world queries — `height_at`, `depth_at`, `flow_at`, `contains_point`, `nearest_body`, `query`. Resolves the active bodies each call, so it works with streamed/created/destroyed water.

## Data-Driven Presets

`res://addons/fast_water/presets/` ships `.tres` resources for every tuning type — `visual_*`, `ocean_*`, `body_*`, `quality_*`. Duplicate one, edit it in the inspector, and assign it; no code required. Regenerate the shipped set from the code factory functions with `tools/generate_fast_water_presets.gd`.

## Rivers, Flow, Foam, And Whitewater

- `FastWaterPath`: authored river/canal/path ribbon with per-point width, depth, flow speed, bank foam, and turbulence controls.
- `FastWaterFlowField`: generated flow/foam texture for directed current, river advection, waterfall plunge turbulence, and shader normal response.
- `FastWaterFoamField`: persistent source-aware foam texture for shoreline, wake, impact, rain, rapids, waterfall lip, plunge pool, eddy, and manual foam sources.
- `FastWaterFoamReactiveFx`: bridge from foam source events into bubble/spray effect targets without hard-coding one particle implementation.
- `FastWaterWaterfall`: falling-sheet producer that stamps lip foam, plunge foam, and downstream flow turbulence.
- `FastWaterWaterfallSprayFx`: continuous lip mist, shelf spray, and plunge mist emitters for waterfalls.

## Visual Helpers

- `FastWaterPlanarReflection`: optional SubViewport reflection helper for nearby hero water only.
- `FastWaterBowWake`: optional close-up bow/contact highlight mesh for hero boats, swimmers, or props.
- `FastWaterWakeRibbon`: optional close-up trail mesh for readable wake streaks.
- `FastWaterSplashFx`: optional pooled splash crown and particle helper.
- `FastWaterBubblePool`: pooled bubble/spray emitters for splash and wake events.
- `FastWaterCaustics`: camera-following procedural caustic plane.
- `FastWaterSky`: procedural sky helper for demos and standalone review scenes.

## Weather And Underwater

- `FastWaterEnvironmentState`: data-only resource for water-relevant weather inputs.
- `FastWaterWeatherResponse`: maps wind, rain, storm, and turbidity into water response.
- `FastWaterWeatherAdapter`: bridge from external weather into water surfaces, rain FX, and underwater controllers.
- `FastWaterWeatherSequence`: deterministic demo/test driver for weather transitions.
- `FastWaterRainImpactFx`: water-only rain impact and mist particles.
- `FastUnderwaterController`: screen-space underwater overlay that activates only while submerged.

## Performance And Fidelity Knobs

All optional, all tunable per node or via presets:

- Open-world ocean cost scales by `FastWaterOceanProfile` tier (far tier skips foam-noise and high-cost detail; near tier keeps wakes/ripples/reflection).
- `FastWaterSurface` distant-specular LOD (`specular_lod_start_m`, `specular_lod_end_m`, `distant_specular_roughness`, `distant_normal_flatten`) flattens far normals and floors roughness to remove far-water shimmer.
- `FastWaterSurface.gerstner_enabled` / `gerstner_choppiness`: optional peaked-crest displacement swell with a synchronized CPU height query (`sample_wave_height`). Off by default.
- `FastWaterSurface.foam_detail_enabled`: gate the 3-call foam-noise stack (off on far ocean tiers).
- `FastWaterPlanarReflection.update_hz`: throttle the full-scene reflection render (0 = every frame; >0 re-renders at that rate and reuses the texture between).
- `FastWaterOcean.set_wave_sample_offset(offset)` / `FastWaterSurface.wave_sample_offset_m`: floating-origin support so wave math stays precise far from the world origin.

## Debug Views

`FastWaterSurface.debug_view` and `FastWaterPath.debug_view`:

- `0`: beauty
- `1`: depth/thickness
- `2`: foam sources
- `3`: wake map channels
- `4`: normals
- `5`: flow field
- `6`: planar reflection mask
- `7`: optics/absorption/glint

Runtime contract tools live in `res://addons/fast_water/tools`. Render gates write `artifacts/fast_water*.png`. The combined scene proof is `res://addons/fast_water/demo/fast_water_full_proof.tscn`, with `check_fast_water_full_proof_contract.gd` verifying the lake, path/river, waterfall, ocean preview, rain/weather, underwater, caustics, reflection, foam, and flow modules are present together.

`res://addons/fast_water/tools/check_fast_water_authoring_contract.gd` verifies authoring overlay line generation plus flow/foam map export/import.
