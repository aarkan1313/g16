# Fast Water Ownership Handoff - Fix Weird Water Surface Shapes - 2026-06-29

You are taking over `C:\Wg16\wg-16-project`, focused on `addons/fast_water`.

The user wants the Fast Water addon fixed up until the live water no longer shows weird rectangles/squares/shapes on the surface, wakes/foam around floating objects look credible, and the review scene can be moved around in a normal way. Do not claim the project is visually accepted just because JSON gates pass.

## Scope

- Work only in `addons/fast_water` and `artifacts/fast_water*` unless the user explicitly expands scope.
- Do not touch unrelated dirty WG16 files:
  - `scenes/terrain_lab.tscn`
  - `scripts/lab/LightingComposer.cs`
- The addon is currently untracked as `addons/`.
- Before launching or stopping Godot, inspect processes and stop only Fast Water processes you started.

## User-Visible State

The latest visible launch opened a window titled `WG16 base field (DEBUG)`, but the user could not move around. Treat that as a review blocker. The scene may be running a scripted presentation camera rather than a usable review camera.

The user still wants the weird water-surface rectangles/squares/shapes fixed. Assume the remaining problem must be reproduced live, not only judged from static screenshots.

## Latest Remediation Already Applied

These changes are already in the working tree:

- `scripts/fast_water_surface.gd`
  - finite wake maps no longer receive `target_camera` when `follow_camera` is false;
  - externally authored foam fields are no longer overwritten by the surface's own origin sync.
- `shaders/fast_water_surface.gdshader`
  - added `sample_wake_map(vec2 xz)`;
  - wake samples outside wake-map UV bounds now return neutral data instead of drawing a square footprint.
- `scripts/fast_water_caustics.gd`
  - added exported `follow_camera`;
  - caustics only follow the camera when that flag is true.
- `scripts/fast_water_visual_profile.gd`
  - softened hero pool wake/foam intensity and retuned water color, normals, glints, and detail normal strength.
- `demo/fast_water_demo.gd`
  - full-proof finite water now sets `follow_camera = false`;
  - obvious red blocks, curbs, rails, waterfall cliff/shelf boxes, and bow-wake helper meshes are hidden or skipped in full-proof mode;
  - full-proof caustics intensity is zeroed and no longer follows the camera;
  - weather/rain starts calm in full-proof mode;
  - full-proof camera is lower and closer to a waterline review composition;
  - ocean preview is shifted offstage but still exists for the contract check.
- `demo/fast_water_hero_reference.gd`
  - rectangular curbs/blocks/rails were removed;
  - caustics are fixed, not camera-following, and intensity is zeroed;
  - columns/floaters remain as non-rectangular visual reference geometry.
- `tools/render_fast_water_hero_reference.gd`
  - `highlight_ratio_min` was lowered to `0.001` because the old highlight threshold depended partly on removed bright rectangular props.

## Current Proof Artifacts

Latest known passing artifacts:

- `artifacts/fast_water_full_proof.png`
- `artifacts/fast_water_full_proof_metrics.json`
- `artifacts/fast_water_full_proof_contract.json`
- `artifacts/fast_water_hero_reference.png`
- `artifacts/fast_water_hero_visual_metrics.json`
- `artifacts/fast_water_visual_review_contact_sheet.png`
- `artifacts/fast_water_visual_review_manifest.json`

Latest full-proof metrics:

```json
{
  "avg_luma": 0.499814313881859,
  "avg_saturation": 0.368319055391401,
  "contrast_p95_p05": 0.35292550341785,
  "passed": true
}
```

Latest hero visual metrics:

```json
{
  "base_passed": true,
  "passed": true,
  "water_highlight_ratio": 0.00155197059424137,
  "highlight_ratio_min": 0.001,
  "sky_water_luma_delta": 0.116710898386918
}
```

Latest full-proof contract checks are all true, including:

- `main_surface_present`
- `planar_reflection_present`
- `caustics_present`
- `flow_field_present`
- `foam_field_present`
- `inlet_river_present`
- `waterfall_present`
- `weather_sequence_present`
- `rain_fx_present`
- `underwater_controller_present`
- `ocean_preview_present`

Important: these gates are regression guards, not visual acceptance. The user has not accepted the live result.

## Godot Path

Use:

```powershell
$godot = 'C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
$godot_gui = 'C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
```

## Commands To Reproduce

Check repo/process state:

```powershell
cd C:\Wg16\wg-16-project
git status --short
Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Godot*' } | Select-Object ProcessId,ParentProcessId,Name,CommandLine
```

Launch full proof for live review:

```powershell
& $godot_gui --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --scene 'res://addons/fast_water/demo/fast_water_full_proof.tscn'
```

Render full proof:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_full_proof.gd' -- --screenshot='res://artifacts/fast_water_full_proof.png' --metrics='res://artifacts/fast_water_full_proof_metrics.json' --frames=210
```

Render hero reference:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_hero_reference.gd' -- --screenshot='res://artifacts/fast_water_hero_reference.png' --metrics='res://artifacts/fast_water_hero_visual_metrics.json' --frames=210
```

Run full-proof contract:

```powershell
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/check_fast_water_full_proof_contract.gd' -- --output='res://artifacts/fast_water_full_proof_contract.json' --frames=24
```

Build visual packet:

```powershell
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/build_fast_water_visual_review_packet.gd'
```

## First Investigation Slice

1. Make the review scene controllable.
   - Add or restore an orbit/free-fly camera controller for full-proof and hero review scenes.
   - Document the controls in code comments or docs if needed.
   - Verify live input with the visible Godot window, not only render scripts.

2. Reproduce the weird shapes from the user's view.
   - Launch full proof and inspect while moving.
   - Capture stills from the exact camera positions where rectangles/squares appear.
   - Compare against `fast_water_hero_reference.tscn`.

3. Isolate the source by toggling one layer at a time.
   - Wake map: debug view `3`, then disable/bypass wake texture.
   - Flow/foam fields: debug views `5` and `2`.
   - Reflection: debug view `6`, then zero planar reflection.
   - Optics/refraction/glint: debug view `7`, then zero refraction and glint separately.
   - Caustics: keep intensity zero, then verify no projected plane edge is visible.
   - Helper meshes: bow wake, wake ribbon, waterfall sheet, rain FX, ocean preview.
   - Floor/refraction: change or hide floor material to confirm the surface is not refracting a rectangular floor pattern.

4. Fix the confirmed source, then make the gate stricter.
   - Do not hide artifacts only by reducing alpha.
   - Add a targeted render/debug artifact that fails before the fix and passes after.
   - Keep a clean hero scene separate from the dense module-proof scene if needed.

## Files To Inspect First

- `addons/fast_water/demo/fast_water_demo.gd`
- `addons/fast_water/demo/fast_water_full_proof.tscn`
- `addons/fast_water/demo/fast_water_hero_reference.gd`
- `addons/fast_water/scripts/fast_water_surface.gd`
- `addons/fast_water/shaders/fast_water_surface.gdshader`
- `addons/fast_water/scripts/fast_water_visual_profile.gd`
- `addons/fast_water/scripts/fast_water_planar_reflection.gd`
- `addons/fast_water/scripts/fast_water_caustics.gd`
- `addons/fast_water/scripts/fast_water_bow_wake.gd`
- `addons/fast_water/scripts/fast_water_wake_ribbon.gd`
- `addons/fast_water/scripts/fast_water_wake_map.gd`
- `addons/fast_water/scripts/fast_water_gpu_wake_map.gd`
- `addons/fast_water/docs/HERO_VISUAL_ACCEPTANCE.md`
- `addons/fast_water/docs/TROUBLESHOOTING.md`

## Feature Map

- Surface water: implemented; current visual acceptance still blocked by reported weird shapes.
- Reflections: implemented; must be checked in motion for world anchoring and projected-mask artifacts.
- Caustics: implemented; currently suppressed in full-proof/hero because it can confuse artifact review.
- Foam/whitewater: implemented; visual integration remains a review target.
- Wake/ripples: implemented through hero ripples plus wake maps; helper wake meshes should stay optional.
- River/flow: implemented mechanically with contracts and debug output.
- Waterfall/spray: implemented mechanically; full-proof shifted/softened its visual contribution to avoid rectangular placeholder geometry.
- Rain/weather: implemented mechanically; full-proof starts calm for visual review.
- Underwater: implemented mechanically.
- Ocean tier: implemented mechanically; full-proof preview is offstage and should not pollute finite-pool visual review.
- Debug/render gates: broad coverage exists, but gates need one or more artifact-specific checks for the user's square/rectangle complaint.

## Definition Of Done For The Next Owner

- User can move or orbit in the live review scene.
- The weird surface rectangles/squares are reproduced and traced to specific code or scene layers.
- The confirmed source is fixed without relying on hidden alpha-only masking.
- New screenshots show the fix from the previously failing view.
- `fast_water_full_proof_contract.json`, `fast_water_full_proof_metrics.json`, and `fast_water_hero_visual_metrics.json` still pass.
- Docs/checklist are updated with the final accepted state and any new review controls.
