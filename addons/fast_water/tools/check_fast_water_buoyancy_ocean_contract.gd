extends SceneTree
# Proves the M1 fix end-to-end for buoyancy on the ocean: the surface that buoyancy
# queries has real wave relief (not a flat plane), FastWaterBodyQuery resolves the
# FastWaterOcean facade (not a render tier), and the altitude buoyancy integrates is
# self-consistent. Deterministic (spatial relief + wiring), no physics-sim timing.

const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_buoyancy_ocean_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)
	var camera := Camera3D.new()
	scene.add_child(camera)
	camera.position = Vector3(0.0, 8.0, 0.0)

	var ocean := OCEAN_SCRIPT.new()
	ocean.target_camera = camera
	ocean.ocean_profile = OCEAN_PROFILE_SCRIPT.world_lod()
	scene.add_child(ocean)

	for _i in range(8):
		await process_frame

	# Spatial relief: sample the queried surface across a grid. A flat plane (the old
	# behaviour) would return a constant; the swell must give measurable relief.
	var min_h := INF
	var max_h := -INF
	for ix in range(7):
		for iz in range(7):
			var p := Vector3(float(ix) * 7.0, -1.0, float(iz) * 7.0)
			var h := float(ocean.call("get_surface_height_at", p))
			min_h = minf(min_h, h)
			max_h = maxf(max_h, h)
	var relief := max_h - min_h
	var has_relief := relief > 0.02

	# Buoyancy resolves the facade, not a non-interactive tier.
	var query_pos := Vector3(120.0, -2.0, 40.0)
	var best: Node = BODY_QUERY_SCRIPT.find_best_body(root.get_tree(), query_pos, true)
	var resolves_facade := best == ocean

	# Altitude consistency: get_water_altitude == world_y - get_surface_height_at, and a
	# point below the surface reports negative altitude (buoyancy would push it up).
	var altitude := BODY_QUERY_SCRIPT.get_water_altitude(ocean, query_pos)
	var surface_h := float(ocean.call("get_surface_height_at", query_pos))
	var altitude_consistent := is_finite(altitude) and absf(altitude - (query_pos.y - surface_h)) <= 0.001
	var submerged_negative := altitude < 0.0

	var checks := {
		"queried_surface_has_wave_relief": has_relief,
		"buoyancy_resolves_ocean_facade": resolves_facade,
		"altitude_matches_surface_height": altitude_consistent,
		"submerged_point_reports_negative_altitude": submerged_negative,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"relief_m": relief,
			"min_h": min_h,
			"max_h": max_h,
			"resolved_body": best.name if best != null else "",
			"altitude": altitude,
			"surface_height": surface_h,
		},
	}
	var global_path := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(global_path.get_base_dir())
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("buoyancy ocean contract could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater buoyancy ocean contract saved: " + global_path)
	quit(0 if passed else 1)
