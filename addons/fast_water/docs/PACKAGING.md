# Fast Water Packaging

## Copy Layout

Copy this directory into a project:

```text
res://addons/fast_water
```

Then enable **Fast Water** in **Project Settings > Plugins**. Enabling registers the node types and the optional `FastWater` autoload service. The addon is fully self-contained — it references no files outside `res://addons/fast_water`, so it copies cleanly into any project or world-generator pipeline.

Required addon files:

- `plugin.cfg`
- `plugin.gd`
- `scripts/`
- `shaders/`
- `presets/` (data-driven `.tres` tuning resources)
- `demo/`
- `benchmark/`
- `tools/`
- `README.md`
- `ROADMAP.md`
- `CHECKLIST.md`
- `docs/`

## Sample Scenes

Sample scenes:

- pool/lake: `res://addons/fast_water/demo/fast_water_lake_demo.tscn`
- river: `res://addons/fast_water/demo/fast_water_river_demo.tscn`
- rain: `res://addons/fast_water/demo/fast_water_rain_demo.tscn`
- underwater: `res://addons/fast_water/demo/fast_water_underwater_demo.tscn`
- waterfall: `res://addons/fast_water/demo/fast_water_waterfall_demo.tscn`
- ocean: `res://addons/fast_water/demo/fast_water_ocean_demo.tscn`
- island showcase (ocean + river + pool splash, performant): `res://addons/fast_water/demo/fast_water_showcase.tscn`

The shared portable demo still accepts command-line modes through `--mode=rain`, `--mode=underwater`, `--mode=waterfall`, and `--mode=ocean`.

## Export / Integration Checklist

To drop Fast Water into a game or world generator:

1. Copy `res://addons/fast_water` and enable the plugin. No other files are needed; nothing references code outside the addon.
2. Pick or duplicate a preset from `presets/` per biome/zone and assign it (data-driven tuning, no code).
3. Instance the smallest body per feature (`FastWaterSurface` / `FastWaterPath` / `FastWaterOcean` / `FastWaterWaterfall`); see `docs/INTEGRATION_GUIDE.md`.
4. Query water via the `FastWater` autoload (or `FastWaterBodyQuery`); react via surface signals, `FastWaterVolume`, and `FastWaterSwimmer`/`FastWaterBuoyant`.
5. For infinite/streamed worlds, keep streaming ownership in the host and set `FastWaterOcean.set_wave_sample_offset` to the floating-origin shift; see `docs/WORLD_ENGINE_INTEGRATION.md`.

## Generated Files

Before release, audit generated files:

- Do not ship `artifacts/fast_water*.import`.
- Do not ship transient `artifacts/fast_water*.log`.
- Keep `.uid` sidecars only if the target Godot version expects them in the source package.
- Remove unrelated project-generated files such as `scripts/water/WaterDeltaTexture.cs.uid`.

## Release Gates

Minimum release gates:

```text
parser sweep over addons/fast_water/**/*.gd
Godot --import scan in the source project
ocean, waterfall, river, rain, underwater, hero, benchmark render gates
hero visual metrics artifact with passed=true
hero motion review strip with temporal metrics
visual review packet with contact sheet and manifest
accepted hero reference comparison with --reference-check when an approved target exists
public API contract under res://addons/fast_water/tools/check_fast_water_public_api_contract.gd
runtime contracts under res://addons/fast_water/tools/check_fast_water_*_contract.gd
world integration contract under res://addons/fast_water/tools/check_fast_water_world_integration_contract.gd
release audit under res://addons/fast_water/tools/check_fast_water_release_audit.gd
clean-copy verification in a temporary project
no Godot processes left running
```

The public API contract verifies that `docs/PUBLIC_API.md` still matches the implementation. The world integration contract verifies that lake, stream/river, and open-ocean bodies compose through shared query, profile, and wake APIs. The ocean contract verifies the world-LOD policy: matched macro appearance across near/far tiers, far-ocean non-interactivity, and high-cost detail kept near the viewer. The hero motion review is produced by `res://addons/fast_water/tools/render_fast_water_hero_motion_review.gd`. The visual review packet is produced by `res://addons/fast_water/tools/build_fast_water_visual_review_packet.gd` after render artifacts exist. The release audit verifies copy layout, sample scene loadability, release docs, plugin registration symmetry, public API documentation, public API contract tooling, world integration contract tooling, shader loadability, visual-review tooling, hero-motion tooling, and generated sidecar hygiene. The clean-copy check must create a temporary project, copy only `addons/fast_water`, run Godot import, and parse representative addon scripts from that copied project.

## Versioning

Current addon version is declared in `plugin.cfg`. Treat `docs/PUBLIC_API.md` as the stable API list for the first packaged release. Breaking API changes should bump the version and include migration notes.
