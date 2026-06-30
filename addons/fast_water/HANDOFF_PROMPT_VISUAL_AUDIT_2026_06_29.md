# Fast Water Visual Audit Handoff Prompt

Superseded current handoff: `addons/fast_water/HANDOFF_PROMPT_FIX_WEIRD_WATER_SURFACE_SHAPES_2026_06_29.md`.

This file is historical context from an earlier visual-audit pass. Prefer the current handoff for the latest Godot path, remediation state, proof artifacts, and remaining user blockers.

You are taking over a Godot 4.6 water add-on in `C:\Wg16\wg-16-project`. The add-on lives under:

`addons/fast_water`

The user wants a reusable, modular, high-performance water module inspired by modules like Paper Kitty's Godot dynamic ripples and the feature direction of Arnklit/Waterways, but do not copy external code. The goal is a portable add-on that any Godot project can drop in.

The current basis is technically useful, but the visual result is not good enough. The user inspected the live benchmark and still sees:

- Clear blocks/squares in the water.
- Reflections that are still not physically or spatially accurate.
- A mediocre base water shader.
- Foam/wake effects that look like overlays instead of convincing water interaction.

Do not treat the module as finished. Do not just tune colors. Audit the rendering chain and fix the actual causes.

## Current Repo State

Known unrelated dirty files already existed before this work and must not be reverted or folded into the water work:

- `scenes/terrain_lab.tscn`
- `scripts/lab/LightingComposer.cs`

The water add-on is currently untracked:

- `addons/fast_water/`

Generated visual artifacts are also untracked:

- `artifacts/fast_water_benchmark.png`
- `artifacts/fast_water_demo.png`
- `artifacts/fast_water_underwater_demo.png`
- `artifacts/fast_water_benchmark_wake.png`
- `artifacts/fast_water_benchmark_metrics.json`

## Godot Path

Use this executable:

```powershell
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
```

The live benchmark scene is:

```text
res://addons/fast_water/benchmark/fast_water_benchmark.tscn
```

Launch it with:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --scene 'res://addons/fast_water/benchmark/fast_water_benchmark.tscn'
```

## Important Current Files

Core surface:

- `addons/fast_water/scripts/fast_water_surface.gd`
- `addons/fast_water/shaders/fast_water_surface.gdshader`
- `addons/fast_water/scripts/fast_water_visual_profile.gd`

Wake systems:

- `addons/fast_water/scripts/fast_water_wake_map.gd`
- `addons/fast_water/scripts/fast_water_gpu_wake_map.gd`
- `addons/fast_water/shaders/fast_wake_gpu_update.gdshader`

Close-up wake overlays:

- `addons/fast_water/scripts/fast_water_bow_wake.gd`
- `addons/fast_water/shaders/fast_bow_wake.gdshader`
- `addons/fast_water/scripts/fast_water_wake_ribbon.gd`
- `addons/fast_water/shaders/fast_wake_ribbon.gdshader`

Reflections:

- `addons/fast_water/scripts/fast_water_planar_reflection.gd`
- Reflection uniforms in `addons/fast_water/shaders/fast_water_surface.gdshader`

Demo/benchmark presentation:

- `addons/fast_water/benchmark/fast_water_benchmark.gd`
- `addons/fast_water/benchmark/fast_water_benchmark.tscn`
- `addons/fast_water/demo/fast_water_demo.gd`
- `addons/fast_water/shaders/fast_demo_pool_floor.gdshader`

## Recent Attempted Fixes Already In Place

These were attempted, but the user still sees problems:

1. The reflection shader no longer uses plain `SCREEN_UV`; it now receives `planar_reflection_view_projection` from `FastWaterPlanarReflection`.
2. `FastWaterPlanarReflection` sets a transparent `SubViewport` and passes the reflection texture to the water shader.
3. The water shader projects `v_world_pos` through `planar_reflection_view_projection` and samples the reflection texture from that projected UV.
4. The water shader uses reflection texture alpha plus a projection-edge fade to avoid a huge projected rectangle.
5. The demo floor shader was changed from hard `hash(floor(UV * tile_count))` blocks to smooth noise, but visible squares/blocks remain in the live view.

Treat these as suspect, not proven-correct.

## Likely Reflection Problems To Audit

The current planar reflection path may still be mathematically wrong. Audit all of this:

- Whether Godot shader clip-space Y/Z conventions match `Camera3D.get_camera_projection() * Projection(camera.global_transform.affine_inverse())`.
- Whether reflected camera basis is correct in `FastWaterPlanarReflection._update_camera()`.
- Whether the reflection camera needs an oblique clip plane or at least a correct near-water culling strategy.
- Whether `planar_reflection_flip_y` is wrong or unnecessary.
- Whether transparent viewport alpha is reliable for masking reflected props.
- Whether projected reflection should sample only above-water props and avoid the water mesh/floor entirely.
- Whether the reflection should be blended mostly at grazing angles and with roughness/normal-based blur rather than as a bright ghost rectangle.

Do not accept reflections that move relative to the camera instead of staying anchored to world geometry.

## Likely Square/Block Artifact Causes To Audit

The user still sees clear squares in the water. Do not assume the demo floor shader is the only source. Check:

- Mesh grid/subdivision visibility from the water plane itself.
- Large projected-reflection UV regions or reflection texture alpha edges.
- Refraction sampling of the screen/floor through a shallow transparent surface.
- Wake map resolution, UV snapping, dirty-region updates, or ping-pong texture clearing.
- The demo floor and caustics projection.
- Normal/detail noise using low-frequency value noise that may reveal cells.
- Godot import/viewport filtering and mip behavior.

The visible blockiness must be fixed at the source, not hidden by merely lowering alpha.

## Foam/Wake Visual Problems

The current foam/wake does not look premium. It reads like a white additive overlay in places.

Audit:

- `fast_water_surface.gdshader` foam composition around `edge_foam`, `wake_foam`, `ripple_foam`, `foam_feather`, and `foam_core`.
- How wake-map channels are used: current wake texture stores height-ish data, foam, and normals/flow. Verify channel meaning across CPU and GPU backends.
- Whether bow wake/ribbon meshes should stay. They may be useful for hero shots, but currently risk looking pasted on.
- Whether foam should drive color, alpha, roughness, normal flattening, and small height displacement as one coherent material response.
- Whether foam should be less uniformly white and more broken by directional flow/noise.

## Base Water Shader Quality Problems

The shader currently looks better than the first pass but still mediocre. Specifically audit:

- Water color/absorption model.
- Fresnel and grazing reflection.
- Normal composition: base waves, micro normals, detail normals, wake-map normals.
- Refraction strength and scene bleed.
- Roughness/specular response.
- Whether the shallow pool demo is a poor judge and needs a better production-like test scene.

The target is high-performance but visually convincing water, not a milky transparent blue plane.

## Performance Constraints

Keep the core module efficient:

- No baked flipbooks.
- No per-object loops over hundreds of contacts in the water shader.
- Keep expensive features optional: planar reflections, close-up wake meshes, bubbles, splash crowns, caustics.
- Preserve CPU and GPU wake backends unless you find a concrete reason to remove one.
- Maintain an easy API: project users should call `add_wake_point()` / `add_splash()` or attach interactors.

Recent benchmark metric after the latest fixes:

```json
{
  "avg_fps": 238.423069816235,
  "avg_ms": 4.194225,
  "measured_frames": 240
}
```

Performance is currently fine. Visual correctness is the blocker.

## Gates To Run

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

Render gate:

```powershell
$ErrorActionPreference = 'Stop'
$godot = 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
& $godot --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/render_fast_water_benchmark.gd' -- --screenshot='res://artifacts/fast_water_benchmark.png' --wake-debug='res://artifacts/fast_water_benchmark_wake.png' --metrics='res://artifacts/fast_water_benchmark_metrics.json' --frames=300
& $godot --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/render_fast_water_demo.gd' -- --screenshot='res://artifacts/fast_water_demo.png' --frames=220
```

Important: The headless renderer can return a null viewport texture on this machine. Use the normal renderer for screenshot gates.

## Expected Output From You

Do not just say "tuned water."

Deliver:

1. A visual diagnosis with root causes, not just symptoms.
2. A reflection correctness fix that remains stable during camera/mouse orbit.
3. A square/block artifact fix with before/after evidence.
4. A foam/wake material pass that looks integrated with water, not pasted on.
5. A better base water shader pass.
6. Parser and render gate results.
7. Updated screenshots/artifacts.
8. A short list of remaining limitations, if any.

## Non-Goals

- Do not copy code from Paper Kitty, Waterways, or other modules.
- Do not touch unrelated WG16 terrain/lab lighting files.
- Do not spend time on broad WG16 terrain/rendering cleanup.
- Do not hide visual defects by only changing the camera angle.
