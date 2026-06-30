# Fast Water Full Implementation Audit Handoff - 2026-06-29

Superseded current handoff: `addons/fast_water/HANDOFF_PROMPT_FIX_WEIRD_WATER_SURFACE_SHAPES_2026_06_29.md`.

This file records the pre-remediation audit prompt. It is still useful historical context, but it does not include the later cleanup pass that removed obvious rectangular proof props, disabled finite-scene camera-follow behavior, guarded wake-map out-of-bounds sampling, and reran the current proof gates.

You are picking up `C:\Wg16\wg-16-project`, focused on `addons/fast_water`.

The user just launched `res://addons/fast_water/demo/fast_water_full_proof.tscn` and rejected the current visual result. Treat this as a failed visual acceptance, not as a minor polish issue.

## User-Reported Blockers

The user observed:

- Weird shifting of the whole world/water.
- Rectangles or weird lines are still visible.
- Very bad wake/foam/interaction around the objects/balls.

Do not claim the water is AAA-ready. Do not continue blindly tuning one shader parameter. Do a full implementation audit from scene composition through scripts, shader math, helper meshes, render gates, and proof artifacts.

## Scope Rules

- Work in `C:\Wg16\wg-16-project`.
- Keep edits scoped to `addons/fast_water` and `artifacts/fast_water*` unless the user explicitly authorizes broader WG16 edits.
- Do not touch unrelated dirty files:
  - `scenes/terrain_lab.tscn`
  - `scripts/lab/LightingComposer.cs`
- Before and after any launch/render work, check for Godot processes and stop only Fast Water launch/render processes you started.
- Prefer a read-only audit first. If you patch, patch only after proving the root cause.

## Current State

Recent work added or modified:

- `addons/fast_water/demo/fast_water_full_proof.tscn`
- `addons/fast_water/demo/fast_water_demo.gd`
- `addons/fast_water/demo/fast_water_hero_reference.gd`
- `addons/fast_water/shaders/fast_water_surface.gdshader`
- `addons/fast_water/shaders/fast_caustics.gdshader`
- `addons/fast_water/shaders/fast_demo_pool_floor.gdshader`
- `addons/fast_water/scripts/fast_water_planar_reflection.gd`
- `addons/fast_water/scripts/fast_water_visual_profile.gd`
- `addons/fast_water/tools/render_fast_water_full_proof.gd`
- `addons/fast_water/tools/check_fast_water_full_proof_contract.gd`
- `addons/fast_water/tools/check_fast_water_public_api_contract.gd`
- `addons/fast_water/tools/check_fast_water_release_audit.gd`
- `addons/fast_water/tools/build_fast_water_visual_review_packet.gd`
- Docs/checklist updates in `README.md`, `docs/MODULE_REFERENCE.md`, `docs/HERO_VISUAL_ACCEPTANCE.md`, and `CHECKLIST.md`.

Generated proof artifacts currently include:

- `artifacts/fast_water_full_proof.png`
- `artifacts/fast_water_full_proof_metrics.json`
- `artifacts/fast_water_full_proof_contract.json`
- `artifacts/fast_water_hero_reference.png`
- `artifacts/fast_water_debug_reflection.png`
- `artifacts/fast_water_visual_review_contact_sheet.png`
- `artifacts/fast_water_visual_review_manifest.json`
- `artifacts/fast_water_release_audit.json`
- `artifacts/fast_water_public_api_contract.json`

The gates can pass while the visual result is still unacceptable. Do not treat passing metrics/contracts as visual acceptance.

## Commands That Were Used

Godot console binary that blocks and prints errors:

```powershell
$godot = 'C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe'
```

Launch path the user saw:

```powershell
$godot = 'C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --scene 'res://addons/fast_water/demo/fast_water_full_proof.tscn'
```

Render full proof artifact:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_full_proof.gd' -- --screenshot='res://artifacts/fast_water_full_proof.png' --metrics='res://artifacts/fast_water_full_proof_metrics.json' --frames=210
```

Render hero reference:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_hero_reference.gd' -- --screenshot='res://artifacts/fast_water_hero_reference.png' --metrics='res://artifacts/fast_water_hero_visual_metrics.json' --frames=210
```

Render reflection debug:

```powershell
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --script 'res://addons/fast_water/tools/render_fast_water_hero_reference.gd' -- --screenshot='res://artifacts/fast_water_debug_reflection.png' --debug-view=6 --frames=210
```

Contract/audit gates:

```powershell
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/check_fast_water_full_proof_contract.gd' -- --output='res://artifacts/fast_water_full_proof_contract.json' --frames=24
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/check_fast_water_public_api_contract.gd' -- --output='res://artifacts/fast_water_public_api_contract.json'
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/check_fast_water_release_audit.gd' -- --output='res://artifacts/fast_water_release_audit.json'
& $godot --headless --path 'C:\Wg16\wg-16-project' --script 'res://addons/fast_water/tools/build_fast_water_visual_review_packet.gd'
```

## Likely Problem Areas To Audit First

These are hypotheses, not confirmed facts. Verify them in code and runtime.

### 1. Whole World / Water Shifting

Strong suspicion: camera-relative water behavior is being used inside finite/demo proof scenes where it should be disabled or hidden.

Audit:

- `addons/fast_water/scripts/fast_water_surface.gd`
  - `follow_camera`
  - `follow_snap_m`
  - `_process()`
  - `_sync_wake_map()`
  - `_sync_flow_field()`
  - `_sync_foam_field()`
- `addons/fast_water/demo/fast_water_demo.gd`
  - In `_setup_scene()`, main `_water` is created with `target_camera` but currently does not explicitly set `follow_camera = false`.
  - This may make the finite pool/full-proof water plane snap/recenter with camera, which can read as the world or water shifting.
- `addons/fast_water/scripts/fast_water_ocean.gd`
  - Near/far ocean tiers recenter around focus by design. That is correct for ocean, but visually confusing if composed beside a finite pool proof scene.
- `addons/fast_water/scripts/fast_water_caustics.gd`
  - Caustics plane follows the target camera in XZ every frame. That can make under-water light feel camera-locked if too visible.
- `addons/fast_water/scripts/fast_water_planar_reflection.gd`
  - Reflection camera and texture update every frame; verify it is not introducing view-space/projection sliding.

Expected audit output:

- Identify every camera-relative node in the full-proof scene.
- Decide which should be camera-relative in an open-world/ocean scene and which should be fixed in finite/pool/hero scenes.
- Propose the smallest robust rule, likely profile/scene-driven, not ad hoc per demo.

### 2. Rectangles / Weird Lines

The previous pass disabled explicit tile line visibility in `fast_demo_pool_floor.gdshader`, but the launched scene still shows rectangular/linear artifacts.

Audit all possible sources:

- Physical box props in `fast_water_demo.gd`:
  - pool curbs
  - red blocks
  - dark rails
  - waterfall cliff/shelf boxes
  - ocean preview boxes
- Planar reflections in `fast_water_surface.gdshader`.
- Caustics plane in `fast_water_caustics.gdshader` and `FastWaterCaustics`.
- Floor/seabed UVs and line/vein material.
- Waterfall sheet rectangle and foam-field texture projection.
- Wake ribbon or bow wake helper meshes.
- Debug/proof scene composition: the full-proof scene may be trying to show too many modules in one view and accidentally exposing placeholders/rectangular primitives.

Expected audit output:

- Separate real shader artifacts from intentionally placed rectangular test props.
- Remove or replace placeholder props from any scene intended for visual proof.
- If boxes are needed for reflection proof, make them visually honest, small, and not confused with artifacts.

### 3. Bad Wake/Foam Around Balls

Audit both simulation and helper visuals:

- `addons/fast_water/demo/fast_water_demo.gd`
  - `_add_markers()`
  - `_add_bow_wake_visual()`
  - `_animate_interactors()`
  - current marker radius, velocity, wake radius, splash radius, and frequency.
- `addons/fast_water/scripts/fast_water_bow_wake.gd`
- `addons/fast_water/scripts/fast_water_wake_ribbon.gd`
- `addons/fast_water/scripts/fast_water_wake_map.gd`
- `addons/fast_water/scripts/fast_water_gpu_wake_map.gd`
- `addons/fast_water/shaders/fast_water_surface.gdshader`
  - wake foam sampling
  - `wake_highlight`
  - `foam_threads`
  - `ripple_foam`
  - normal flattening under foam
- `FastWaterVisualProfile.hero_pool_reference()` and `gameplay_lake()`
  - wake/foam/stamp gains may still be too high or shaped wrong.

Expected audit output:

- Determine whether the ugly wake is from the helper mesh, the wake map, shader interpretation of wake channels, or the scene's actor motion/radius.
- Disable helper visuals and render wake-map-only, then helper-only, then combined, to isolate.
- Use artifacts, not verbal impressions.

## Full-Proof Scene Concern

`fast_water_full_proof.tscn` currently uses `fast_water_demo.gd` with `default_mode = "full_proof"`.

This scene proves that systems can exist together, but it is probably a poor visual showcase:

- Too many placeholder props.
- Ocean preview plus finite pool may confuse camera-relative behavior.
- Waterfall boxes create hard rectangles.
- The scene may be proving modules mechanically while making the product look worse.

Audit whether to split:

- A clean hero visual scene for judging water/foam/reflection.
- A module proof scene for showing all systems exist.
- A debug/proof scene that intentionally labels or isolates modules.

Do not force one scene to be both an attractive hero and a dense systems proof if that makes the water look bad.

## Required Audit Deliverable

Produce a hard, code-grounded audit with:

1. Confirmed root causes for the three user blockers.
2. File and line references for each cause.
3. A feature-by-feature implementation map:
   - surface water
   - reflections
   - caustics
   - foam/whitewater
   - wake/ripples
   - river/flow
   - waterfall/spray
   - rain/weather
   - underwater
   - ocean tier
   - debug/render gates
4. A distinction between:
   - implemented and visually acceptable
   - implemented but visually failing
   - mechanically stubbed/proof-only
   - missing or unsafe
5. A concrete remediation plan in small slices.
6. Which gates should be kept, rewritten, or made stricter.

## Suggested First Commands

```powershell
cd C:\Wg16\wg-16-project
git status --short
Get-Process | Where-Object { $_.ProcessName -like '*Godot*' }
rg -n "follow_camera|target_camera|global_position|PlanarReflection|BowWake|WakeRibbon|add_wake_point|add_splash|foam|caustic|full_proof|FullProof" addons/fast_water
```

Open these artifacts first:

- `artifacts/fast_water_full_proof.png`
- `artifacts/fast_water_hero_reference.png`
- `artifacts/fast_water_debug_reflection.png`
- `artifacts/fast_water_visual_review_contact_sheet.png`

Then launch the full-proof scene and observe the user's reported issues directly:

```powershell
$godot = 'C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe'
& $godot --path 'C:\Wg16\wg-16-project' --rendering-driver d3d12 --scene 'res://addons/fast_water/demo/fast_water_full_proof.tscn'
```

Stop Godot after inspection:

```powershell
Get-Process | Where-Object { $_.ProcessName -like '*Godot*' } | Stop-Process -Force
```

## Final Warning

The user is right to be frustrated. The current system has broad module coverage and passing contract gates, but passing gates did not prevent obvious visual failure in live inspection. The next pass must audit architecture and scene composition end-to-end before making more shader tweaks.
