# Fast Water Troubleshooting

## Water Looks Flat Or Milky

- Start from `FastWaterVisualProfile.gameplay_lake()` or `hero_pool_reference()` instead of raw defaults.
- Lower `refracted_scene_strength` if the floor bleeds through too much.
- Raise `depth_absorption_strength` and `absorption_density` for stronger depth color.
- Use debug view `1` for depth/thickness and debug view `7` for optics/glint.

## Wakes Do Not Appear

- Confirm `interactions_enabled` is true.
- Confirm a `target_camera` is assigned when interaction distance limiting is enabled.
- For continuous wakes, check that `auto_create_wake_map` or `wake_map_node` is set.
- Use debug view `3` to inspect wake-map channels.

## Square Or Rectangular Surface Artifacts

- First separate authored geometry from shader artifacts. In full-proof scenes, disable placeholder proof geometry, bow-wake helper meshes, caustics, reflection, and wake maps one at a time.
- Use debug view `3` for wake-map channels, `5` for flow, `6` for reflection mask, and `7` for optics/glint.
- If the artifact appears as a hard grey geometric hole while shader debug views are otherwise black/clean, check whether vertex displacement is letting submerged scene geometry show through. Hero/contact ripples should stay in the fragment normal/foam path; do not reintroduce thin ring vertex displacement on coarse water meshes.
- For finite lake/pool scenes, set `follow_camera = false` on `FastWaterSurface` and avoid passing a target camera to a finite wake map unless camera-relative water is intended.
- Check that wake-map sampling returns neutral values outside the configured `wake_map_world_size_m`; out-of-bounds wake UVs can show as square footprints.
- Check caustics and planar-reflection projection separately. A camera-following caustics plane or projected reflection texture can look like a surface-locked rectangle when too strong.
- Run `res://addons/fast_water/tools/diagnose_fast_water_surface_artifact.gd` with `--cases=baseline,no_planar,no_wake,no_hero_ripples` in a windowed D3D12 launch when SubViewport-backed features need a real window.
- Do not hide the issue only by lowering opacity. Capture before/after artifacts and identify the source path.

## Launched Scene Cannot Move

- The full-proof scene is currently a render/proof composition first. If it is launched for human review, verify that an orbit or free-fly camera controller is active and documented.
- If the visible window opens but input appears dead, confirm the Godot window has focus and that the scene is not running only a scripted presentation camera.
- Treat blocked navigation as a visual-review blocker, because fixed camera proof renders can miss artifacts that appear in motion.

## River Flow Looks Static

- Confirm `FastWaterPath.auto_create_flow_field` is enabled or assign a `FastWaterFlowField`.
- Use debug view `5` to inspect flow.
- Check per-point `point_flow_speeds_mps` and `point_turbulence_strengths`.
- Rebuild the path or call `rebuild_path_mesh()` after code-driven edits.

## Foam Is Missing Or Too Uniform

- Use `FastWaterFoamField.sample_source_at()` to confirm source kinds are being stamped.
- Use `FastWaterAuthoringOverlay` with foam source sampling enabled.
- Raise source-specific stamp intensity before increasing global `foam_intensity`.
- Check flow-field advection if foam should stretch downstream.

## Waterfall Spray Is Too Expensive

- Enable `lod_enabled` on `FastWaterWaterfall`.
- Lower `far_vertical_segments`.
- Keep `disable_spray_in_far_lod` enabled for distant waterfalls.
- Route persistent plume behavior through foam fields when continuous particles are too expensive.

## Ocean Is Too Expensive

- Use `FastWaterOceanProfile.performance()`.
- Lower `near_mesh_subdivisions`, `far_mesh_subdivisions`, and `local_wake_resolution`.
- Keep the far surface non-interactive.
- Disable planar reflections and use cheap distant color reflection.

## Import Creates Extra Sidecars

Godot can create `.uid` and `.import` files during import/render gates. Before packaging:

- remove `artifacts/fast_water*.import`
- remove `artifacts/fast_water*.log`
- remove unrelated project sidecars such as `scripts/water/WaterDeltaTexture.cs.uid`
- rerun parser/import gates after cleanup if source files changed
