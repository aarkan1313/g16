extends SceneTree
# Regenerates the shipped data-driven preset library under addons/fast_water/presets/
# from the code factory functions (single source of truth). Run:
#   Godot --headless --path <proj> \
#     --script res://addons/fast_water/tools/generate_fast_water_presets.gd
# Projects can then duplicate/edit/save these .tres in the inspector without code.

const VISUAL := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")
const OCEAN := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const BODY := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const QUALITY := preload("res://addons/fast_water/scripts/fast_water_quality.gd")

const DIR := "res://addons/fast_water/presets/"


func _initialize() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(DIR))
	var entries := {
		"visual_reflective_pool.tres": VISUAL.reflective_pool(),
		"visual_gameplay_lake.tres": VISUAL.gameplay_lake(),
		"visual_hero_pool_reference.tres": VISUAL.hero_pool_reference(),
		"visual_hero_quality.tres": VISUAL.hero_quality(),
		"visual_cheap_ocean.tres": VISUAL.cheap_ocean(),
		"visual_mobile_low.tres": VISUAL.mobile_low(),
		"ocean_open_world.tres": OCEAN.open_world(),
		"ocean_performance.tres": OCEAN.performance(),
		"ocean_world_lod.tres": OCEAN.world_lod(),
		"body_lake.tres": BODY.lake(),
		"body_pool.tres": BODY.pool(),
		"body_river.tres": BODY.river(),
		"body_ocean.tres": BODY.ocean(),
		"quality_low.tres": QUALITY.low(),
		"quality_medium.tres": QUALITY.medium(),
		"quality_high.tres": QUALITY.high(),
	}
	var saved := 0
	var failed: Array[String] = []
	for name in entries:
		var file_name := String(name)
		var res: Resource = entries[name]
		res.resource_name = file_name.get_basename()
		var path := DIR + file_name
		var rc := ResourceSaver.save(res, path)
		if rc == OK:
			saved += 1
		else:
			failed.append("%s (err %d)" % [name, rc])
	print("FastWater presets generated: %d saved, %d failed" % [saved, failed.size()])
	for f in failed:
		print("  FAILED ", f)
	quit(1 if failed.size() > 0 else 0)
