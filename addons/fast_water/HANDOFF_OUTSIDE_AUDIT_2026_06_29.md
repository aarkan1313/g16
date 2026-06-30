# Fast Water Outside Audit Handoff

Date: 2026-06-29
Repo: `C:\Wg16\wg-16-project`
Branch: `water-export-phase2a`
Project: Godot 4.6, Forward Plus, D3D12, Jolt Physics

## Purpose

This handoff is for an outside audit of `addons/fast_water`.

The basis of the module is useful: it is modular, portable, event-driven, and has CPU/GPU wake backends. The current concern is visual quality. The water works and is inspectable, but it does not yet look good enough. It reads too gray/milky, the contact wake can look like an overlay instead of water interaction, and the shader needs a serious material/lighting pass.

The audit should be both technical and visual. Do not only look for parse errors. Judge whether the current rendering model can reach the target look with tuning, or whether specific shader/composition pieces should be replaced.

## Hard Constraints

- Do not copy code or assets from Paper Kitty, Waterways, or any other external package.
- It is fine to inspect external modules for feature comparison and design ideas.
- Keep the water system usable as a standalone add-on under `res://addons/fast_water`.
- Avoid coupling it to WG16 terrain/lab systems.
- Performance matters. The desired system is shader-first, event-driven, and cheap enough for game use.
- Keep expensive features optional: planar reflection, close hero wake meshes, splash FX, bubbles, caustics.

## Current User Read

The user thinks the architecture is probably a good base, but the water does not look very good. Treat that as the audit thesis.

Specifically, do not assume that a working benchmark means the module is visually acceptable. The benchmark exists so visual and performance problems can be seen and isolated.

## Main Files

Core surface:

- `addons/fast_water/scripts/fast_water_surface.gd`
- `addons/fast_water/shaders/fast_water_surface.gdshader`
- `addons/fast_water/scripts/fast_water_visual_profile.gd`

Wake systems:

- `addons/fast_water/scripts/fast_water_wake_map.gd`
- `addons/fast_water/scripts/fast_water_gpu_wake_map.gd`
- `addons/fast_water/shaders/fast_wake_gpu_update.gdshader`
- `addons/fast_water/scripts/fast_water_bow_wake.gd`
- `addons/fast_water/shaders/fast_bow_wake.gdshader`
- `addons/fast_water/scripts/fast_water_wake_ribbon.gd`
- `addons/fast_water/shaders/fast_wake_ribbon.gdshader`

Optional modules:

- `addons/fast_water/scripts/fast_water_planar_reflection.gd`
- `addons/fast_water/scripts/fast_water_path.gd`
- `addons/fast_water/scripts/fast_water_buoyant.gd`
- `addons/fast_water/scripts/fast_water_splash_fx.gd`
- `addons/fast_water/scripts/fast_water_bubble_pool.gd`
- `addons/fast_water/scripts/fast_water_caustics.gd`
- `addons/fast_water/scripts/fast_underwater_controller.gd`
- `addons/fast_water/scripts/fast_water_sky.gd`

Scenes and gates:

- `addons/fast_water/benchmark/fast_water_benchmark.tscn`
- `addons/fast_water/benchmark/fast_water_benchmark.gd`
- `addons/fast_water/demo/fast_water_demo.tscn`
- `addons/fast_water/demo/fast_water_demo.gd`
- `addons/fast_water/tools/render_fast_water_benchmark.gd`
- `addons/fast_water/tools/render_fast_water_demo.gd`

Reference notes:

- `addons/fast_water/README.md`
- `addons/fast_water/WATERWAYS_REVIEW.md`

## Start Here

Open this scene first:

```text
res://addons/fast_water/benchmark/fast_water_benchmark.tscn
```

It exposes:

- Mobile / Balanced / Hero profile buttons.
- CPU/GPU wake backend toggle.
- Planar reflection toggle.
- Actor count slider.
- Pause.
- Wake-map preview.
- FPS/frame-time readout.
- Right mouse orbit camera.

The latest screenshot from that scene is:

```text
artifacts/fast_water_benchmark.png
```

Also inspect:

```text
artifacts/fast_water_benchmark_wake.png
artifacts/fast_water_demo.png
artifacts/fast_water_underwater_demo.png
```

The benchmark currently makes the contact wake visible, but it may be too artificial. That is intentional for inspection, not necessarily the final look.

## Validation Commands

Godot executable used locally:

```powershell
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
```

Parser sweep:

```powershell
$ErrorActionPreference = 'Stop'
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
$scripts = @(rg --files addons/fast_water | Where-Object { $_ -like '*.gd' } | ForEach-Object { 'res://' + $_.Replace('\','/') })
$failed = $false
foreach ($script in $scripts) {
  $output = & $godot --headless --path 'C:\Wg16\wg-16-project' --check-only --script $script 2>&1
  if ($output -match 'SCRIPT ERROR|Parse Error|Compile Error|ERROR: Failed to load script') {
    Write-Output "FAILED $script"
    $output
    $failed = $true
    break
  }
}
if ($failed) { exit 1 }
Write-Output "Fast Water parser sweep passed: $($scripts.Count) scripts"
```

Expected recent result:

```text
Fast Water parser sweep passed: 21 scripts
```

Import pass:

```powershell
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
$output = & $godot --headless --path 'C:\Wg16\wg-16-project' --import 2>&1
$output | Select-Object -Last 80
if ($output -match 'SCRIPT ERROR|Parse Error|Compile Error|Shader compilation failed|ERROR: Failed to load script') { exit 1 }
```

Benchmark render gate:

```powershell
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_benchmark.gd' -- --screenshot='res://artifacts/fast_water_benchmark.png' --wake-debug='res://artifacts/fast_water_benchmark_wake.png' --frames=210
```

Demo render gate:

```powershell
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_demo.gd' -- --screenshot='res://artifacts/fast_water_demo.png' --frames=180
```

Recent benchmark HUD readout reported roughly `168 FPS / 6.0 ms` with GPU wakes, hero profile, reflection on, and 8 actors. Treat this as a rough local visual-gate number, not a formal benchmark.

## Current Design

`FastWaterSurface` owns the mesh, material, hero ripple uniforms, and routing into the wake map. It exposes a deliberately small runtime API:

```gdscript
water_surface.add_splash(world_pos, velocity, radius)
water_surface.add_wake_point(world_pos, velocity, radius)
water_surface.set_wake_map_texture(texture, origin_xz, world_size_m)
water_surface.get_surface_height_at(world_pos)
```

The water shader does not loop over hundreds of contacts. It evaluates up to 16 hero ripples and samples a wake texture for continuous disturbance.

The GPU wake backend uses ping-pong `SubViewport`s and `fast_wake_gpu_update.gdshader`. It has the same stamp-style API as the CPU backend.

Close-up contact highlights are currently separate additive meshes:

- `FastWaterBowWake`
- `FastWaterWakeRibbon`

These are pragmatic readability helpers, but they are also a visual risk because they can look pasted on.

## Known Visual Problems

These are the main issues to audit.

1. The water color/material reads too pale, gray, and milky.
2. The surface can look like noisy glass instead of water.
3. The near wake highlight is more readable now, but it looks like an additive overlay rather than true deformation/foam.
4. Surface normals show repetitive directional patterns, especially at low viewing angles.
5. Far water has a tiled/ridged look that distracts from the interaction.
6. Reflections are present, but they do not yet sell the reference look.
7. The planar reflection path may be too weak, too blurred, or badly color-balanced.
8. Caustic streaks can steal attention and may read as surface artifacts.
9. Depth blending and absorption are not visually convincing yet.
10. Underwater mode exists, but it is not polished.
11. Foam/contact bands need better breakup, thickness, and blending into the base shader.
12. Hero wake, wake map foam, and base wave normals are not visually unified.

## Technical Risks To Inspect

Transparent rendering:

- `fast_water_surface.gdshader` uses `blend_mix` and `depth_draw_never`.
- `fast_bow_wake.gdshader` and `fast_wake_ribbon.gdshader` use additive transparent overlays with depth testing disabled.
- This improves readability, but may cause wrong overlap, haloing, or non-physical sorting.

GPU wake backend:

- `FastWaterGpuWakeMap` updates by rendering into ping-pong `SubViewport`s.
- It avoids CPU image edits, but it is not true RenderingDevice compute.
- Audit whether this is good enough for Godot 4.6 portability and ease of use.

Wake origin:

- Wake textures follow the camera and clear on origin jumps.
- Inspect whether this causes visible popping or lost trails.

Profile application:

- `FastWaterVisualProfile` applies values to surface, wake map, and reflection.
- Check whether profile timing or wake node replacement can leave stale values.

Benchmark validity:

- The benchmark is intentionally synthetic.
- It is useful for seeing wakes and toggles, but should not be mistaken for a production scene.

## Requested Audit Output

Please produce a practical audit with:

1. A visual diagnosis of why the water currently looks weak.
2. A prioritized fix list, separating shader tuning from architecture changes.
3. Specific file/function/shader references for each recommendation.
4. A performance risk callout for each proposed improvement.
5. A decision on whether the current GPU wake approach is sufficient.
6. A recommendation on whether additive wake meshes should stay, be toned down, or be replaced.
7. A better hero-quality target profile, ideally with concrete parameter values.
8. A minimal acceptance gate: what screenshots or runtime toggles should prove the water is actually better.

## Likely Best Next Improvements

These are hypotheses, not conclusions.

- Rework the main water shader color pipeline before adding more features.
- Reduce the milky final color and make absorption/depth response more intentional.
- Make foam/wake texture affect normals, roughness, alpha, and color in one coherent block.
- Improve local wake deformation around actors so the overlay mesh is less necessary.
- Replace repetitive procedural normals with a better multi-scale pattern.
- Make planar reflection contribution more legible only at grazing angles.
- Lower or gate caustics in the surface demo so they do not read as water-surface streaks.
- Create two target profiles: `hero_pool_reference` and `cheap_gameplay`, instead of trying to make one profile do both.

## Dirty Tree Notes

At handoff time, the repo has unrelated modified files outside this add-on:

```text
M scenes/terrain_lab.tscn
M scripts/lab/LightingComposer.cs
```

Do not revert or fold those into the water audit unless the task explicitly expands into WG16 scene lighting.

The water work is currently under:

```text
?? addons/
?? artifacts/fast_water_benchmark.png
?? artifacts/fast_water_benchmark_wake.png
?? artifacts/fast_water_demo.png
?? artifacts/fast_water_underwater_demo.png
```

Godot may create `.import` files or `.uid` sidecars while opening/rendering. Clean generated sidecars before reporting final status unless they are intentionally part of the add-on.
