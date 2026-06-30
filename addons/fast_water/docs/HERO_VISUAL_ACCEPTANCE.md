# Hero Visual Acceptance

Fast Water has a dedicated hero reference scene for visual tuning:

- Scene: `res://addons/fast_water/demo/fast_water_hero_reference.tscn`
- Gate script: `res://addons/fast_water/tools/render_fast_water_hero_reference.gd`
- Default screenshot: `res://artifacts/fast_water_hero_reference.png`
- Default metrics artifact: `res://artifacts/fast_water_hero_visual_metrics.json`

The metrics gate is a regression guard for obvious visual failures. It checks that
the hero capture is nonblank, has measurable contrast, has saturated water color,
keeps sky and water visually separated, and contains water highlights. It does not
replace final visual judgment.

## Current Status - 2026-06-29

Latest generated artifacts:

- `res://artifacts/fast_water_full_proof.png`
- `res://artifacts/fast_water_full_proof_metrics.json`
- `res://artifacts/fast_water_full_proof_contract.json`
- `res://artifacts/fast_water_hero_reference.png`
- `res://artifacts/fast_water_hero_visual_metrics.json`

The current JSON gates pass, but the user has not accepted the live visual result. The remaining blocker is to reproduce and remove any visible square/rectangle shapes on the water surface, then make the launched review scene controllable enough for inspection.

## Metrics Gate

Run from the project root:

```text
Godot --path C:\Wg16\wg-16-project --rendering-driver d3d12 --script res://addons/fast_water/tools/render_fast_water_hero_reference.gd -- --screenshot=res://artifacts/fast_water_hero_reference.png --metrics=res://artifacts/fast_water_hero_visual_metrics.json --frames=210
```

The gate currently records these thresholds in the JSON artifact:

```text
nonblank_luma_min = 0.05
nonblank_luma_max = 0.90
contrast_p95_p05_min = 0.12
water_avg_saturation_min = 0.035
sky_water_luma_delta_min = 0.025
highlight_ratio_min = 0.002
```

The latest cleaned hero-reference pass uses `highlight_ratio_min = 0.001` because bright rectangular props were removed from the review scene. Do not raise the threshold by adding artificial bright geometry. The JSON artifact should report `"passed": true` before a release candidate.

## Visual Review Packet

After the standard render gates have produced screenshots, generate the visual
review packet:

```text
Godot --headless --path C:\Wg16\wg-16-project --script res://addons/fast_water/tools/build_fast_water_visual_review_packet.gd
```

This writes:

```text
res://artifacts/fast_water_visual_review_contact_sheet.png
res://artifacts/fast_water_visual_review_manifest.json
```

The contact sheet is numbered, and the manifest maps each tile number to its
source artifact, category, dimensions, byte size, and basic image statistics.
Use the packet as the review surface for the accepted-screenshot decision.

## Full Proof Scene

The combined proof scene is:

```text
res://addons/fast_water/demo/fast_water_full_proof.tscn
```

It is intended as a single scene-level proof that the modular pieces can coexist:
hero lake water, path/river flow, waterfall sheet/spray/plunge foam, weather and
rain response, underwater controller, caustics, planar reflection, foam/flow
fields, and a small ocean tier preview.

Render it with:

```text
Godot --path C:\Wg16\wg-16-project --rendering-driver d3d12 --script res://addons/fast_water/tools/render_fast_water_full_proof.gd -- --screenshot=res://artifacts/fast_water_full_proof.png --metrics=res://artifacts/fast_water_full_proof_metrics.json --frames=210
```

Verify composition wiring with:

```text
Godot --headless --path C:\Wg16\wg-16-project --script res://addons/fast_water/tools/check_fast_water_full_proof_contract.gd
```

Passing the full-proof contract only proves that the modules are present and wired. It does not prove that the scene is a good visual showcase. Keep visual acceptance tied to human inspection plus focused render captures.

## Motion Review Gate

The hero acceptance item also depends on how the water reads in motion. Generate
a short frame strip and temporal metrics from the hero scene:

```text
Godot --path C:\Wg16\wg-16-project --rendering-driver d3d12 --script res://addons/fast_water/tools/render_fast_water_hero_motion_review.gd -- --strip=res://artifacts/fast_water_hero_motion_strip.png --metrics=res://artifacts/fast_water_hero_motion_metrics.json
```

This writes:

```text
res://artifacts/fast_water_hero_motion_strip.png
res://artifacts/fast_water_hero_motion_metrics.json
```

The metrics gate checks that sampled frames are nonblank, remain visually stable,
and have enough temporal RGB delta to prove the hero water is not frozen. It is a
motion regression guard; final acceptance still needs human review.

Optional motion thresholds:

```text
--min-temporal-rgb-delta=0.00025
--max-pair-avg-luma-delta=0.05
```

## Accepted Reference Workflow

When a human-approved hero capture exists, store it as:

```text
res://artifacts/fast_water_hero_reference_accepted.png
```

For an advisory comparison, run the gate with:

```text
--reference=res://artifacts/fast_water_hero_reference_accepted.png
```

The metrics artifact will include `reference_comparison` with sampled RGB and luma
delta values.

For an enforceable accepted-image check, add `--reference-check`:

```text
Godot --path C:\Wg16\wg-16-project --rendering-driver d3d12 --script res://addons/fast_water/tools/render_fast_water_hero_reference.gd -- --screenshot=res://artifacts/fast_water_hero_reference_candidate.png --metrics=res://artifacts/fast_water_hero_visual_metrics.json --reference=res://artifacts/fast_water_hero_reference_accepted.png --reference-check --frames=210
```

Strict comparison defaults:

```text
reference_avg_rgb_delta_max = 0.06
reference_avg_luma_delta_max = 0.05
reference_max_luma_delta_max = 0.30
```

Override them per run when the accepted art direction needs a tighter or looser
envelope:

```text
--reference-max-avg-rgb-delta=0.04
--reference-max-avg-luma-delta=0.03
--reference-max-luma-delta=0.20
```

When `--reference-check` is present, the JSON artifact reports both
`base_passed` and `reference_passed`, and overall `passed` requires both.

## Manual Review

Before marking the hero tuning item complete, inspect the screenshot in motion and
confirm:

- No square, grid, or obvious screen-space artifacts.
- Water shape reads clearly against the floor, props, and sky.
- Foam, wakes, reflections, and shimmer reinforce the same wave direction.
- Debug views still expose depth, foam, wake, normals, flow, reflection, and optics.
