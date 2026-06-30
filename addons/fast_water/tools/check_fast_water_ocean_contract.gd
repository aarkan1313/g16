extends SceneTree

const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_ocean_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterOceanContract"
	root.add_child(scene)

	var camera := Camera3D.new()
	camera.name = "Camera"
	camera.position = Vector3(2.0, 3.0, 10.0)
	scene.add_child(camera)

	var hero := Node3D.new()
	hero.name = "HeroWakeFocus"
	hero.position = Vector3(0.0, 0.0, 0.0)
	scene.add_child(hero)

	var profile := OCEAN_PROFILE_SCRIPT.open_world()
	profile.set("near_mesh_size_m", 180.0)
	profile.set("near_mesh_subdivisions", 96)
	profile.set("far_mesh_size_m", 1420.0)
	profile.set("far_mesh_subdivisions", 40)
	profile.set("near_follow_snap_m", 5.0)
	profile.set("far_follow_snap_m", 80.0)
	profile.set("match_macro_appearance_across_lod", true)
	profile.set("high_quality_near_only", true)
	profile.set("far_micro_detail_enabled", false)
	profile.set("local_wake_enabled", true)
	profile.set("use_gpu_wake_map", false)
	profile.set("local_wake_resolution", 128)
	profile.set("local_wake_world_size_m", 64.0)
	profile.set("local_wake_update_hz", 120.0)
	profile.set("swell_height_m", 0.31)
	profile.set("swell_frequency", 0.052)
	profile.set("swell_speed", 0.74)
	profile.set("far_swell_height_scale", 1.40)
	profile.set("near_chop_normal_strength", 1.10)
	profile.set("far_chop_normal_strength", 0.44)
	profile.set("whitecap_strength", 0.36)
	profile.set("whitecap_scale", 0.27)
	profile.set("far_whitecap_scale", 0.50)
	profile.set("near_horizon_fade_strength", 0.08)
	profile.set("far_horizon_fade_strength", 0.70)
	profile.set("horizon_fade_start_m", 120.0)
	profile.set("horizon_fade_end_m", 620.0)
	profile.set("distant_reflection_strength", 0.33)
	profile.set("distant_grazing_reflection_strength", 0.19)

	var ocean := OCEAN_SCRIPT.new()
	ocean.name = "FastWaterOcean"
	ocean.set("target_camera", camera)
	ocean.set("wake_focus", hero)
	ocean.set("ocean_profile", profile)
	scene.add_child(ocean)

	await process_frame
	await process_frame

	var surfaces := ocean.call("get_surface_nodes") as Array
	var near := ocean.get_node_or_null("NearOceanSurface") as Node3D
	var far := ocean.get_node_or_null("FarOceanSurface") as Node3D
	var near_wake := near.get("wake_map_node") if near != null and _has_property(near, "wake_map_node") else null
	var far_wake := far.get("wake_map_node") if far != null and _has_property(far, "wake_map_node") else null

	hero.global_position = Vector3(57.0, 0.0, -43.0)
	await process_frame
	await process_frame

	var near_after := near.global_position if near != null else Vector3.ZERO
	var far_after := far.global_position if far != null else Vector3.ZERO
	var expected_near := Vector3(snapped(hero.global_position.x, 5.0), 0.0, snapped(hero.global_position.z, 5.0))
	var expected_far := Vector3(snapped(hero.global_position.x, 80.0), -0.018, snapped(hero.global_position.z, 80.0))
	var wake_origin := Vector2.ZERO
	if near_wake != null and _has_property(near_wake, "origin_xz"):
		wake_origin = near_wake.get("origin_xz")

	ocean.call("add_wake_point", hero.global_position, Vector3(3.0, 0.0, 0.4), 2.2)
	var wake_pending_count := _read_array_size(near_wake, "_pending")
	var active_ripple_count := _read_int(near, "_active_ripple_count", 0)
	if near_wake != null:
		near_wake.call("_update_texture", 0.016)
	for _i in range(6):
		await process_frame
	var wake_sample := _sample_wake_at(near_wake, hero.global_position)
	var debug_state := ocean.call("get_ocean_debug_state") as Dictionary
	var world_lod_profile := OCEAN_PROFILE_SCRIPT.world_lod()

	var checks := {
		"surface_pair_created": surfaces.size() == 2 and near != null and far != null,
		"world_lod_profile_available": world_lod_profile != null and bool(world_lod_profile.get("match_macro_appearance_across_lod")) and bool(world_lod_profile.get("high_quality_near_only")) and not bool(world_lod_profile.get("far_micro_detail_enabled")),
		"near_mesh_profile_applied": near != null and _near_equal(near, "mesh_size_m", 180.0) and int(near.get("mesh_subdivisions")) == 96,
		"far_mesh_profile_applied": far != null and _near_equal(far, "mesh_size_m", 1420.0) and int(far.get("mesh_subdivisions")) == 40,
		"wake_focus_recenters_near_surface": near != null and near_after.distance_to(expected_near) < 0.01,
		"far_surface_uses_coarse_snap": far != null and far_after.distance_to(expected_far) < 0.01 and far_after.distance_to(near_after) > 20.0,
		"near_wake_map_created": near_wake != null,
		"far_wake_map_disabled": far_wake == null,
		"wake_map_local_to_focus": wake_origin.distance_to(Vector2(expected_near.x, expected_near.z)) < 0.01,
		"near_interactive_far_noninteractive": near != null and far != null and bool(near.get("interactions_enabled")) and not bool(far.get("interactions_enabled")),
		"near_wake_stamped": wake_pending_count > 0 and active_ripple_count > 0,
		"profile_swell_chop_applied": near != null and far != null and _near_equal(near, "wave_height", 0.31) and _near_equal(far, "wave_height", 0.31) and _near_equal(near, "normal_strength", 1.10) and _near_equal(far, "normal_strength", 1.10),
		"profile_whitecaps_applied": near != null and far != null and _near_equal(near, "whitecap_strength", 0.36) and _near_equal(far, "whitecap_strength", 0.36) and _near_equal(near, "whitecap_scale", 0.27) and _near_equal(far, "whitecap_scale", 0.27),
		"world_lod_macro_appearance_matched": near != null and far != null and _color_equal(near.get("shallow_color"), far.get("shallow_color")) and _color_equal(near.get("deep_color"), far.get("deep_color")) and _near_equal(near, "wave_frequency", float(far.get("wave_frequency"))) and _near_equal(near, "wave_speed", float(far.get("wave_speed"))),
		"world_lod_far_detail_cost_removed": near != null and far != null and int(far.get("mesh_subdivisions")) < int(near.get("mesh_subdivisions")) and _near_equal(far, "micro_normal_strength", 0.0) and _near_equal(far, "detail_normal_strength", 0.0) and _near_equal(far, "refraction_strength", 0.0) and not bool(far.get("interactions_enabled")),
		"world_lod_edge_fade_applied": near != null and far != null and _near_equal(near, "mesh_edge_fade_width", float(profile.get("near_edge_fade_width"))) and _near_equal(far, "mesh_edge_fade_width", float(profile.get("far_edge_fade_width"))) and float(profile.get("far_edge_fade_width")) > 0.0,
		"far_foam_detail_skipped": near != null and far != null and not bool(far.get("foam_detail_enabled")) and bool(near.get("foam_detail_enabled")),
		"profile_horizon_fade_applied": near != null and far != null and _near_equal(near, "horizon_fade_strength", 0.08) and _near_equal(far, "horizon_fade_strength", 0.70) and _near_equal(far, "horizon_fade_end_m", 620.0),
		"cheap_distant_reflection_applied": far != null and _near_equal(far, "reflection_strength", 0.33) and _near_equal(far, "grazing_reflection_strength", 0.19) and _near_equal(far, "refraction_strength", 0.0),
		"debug_state_reports_ocean": debug_state.has("near_surface") and debug_state.has("far_surface") and bool(debug_state.get("near_wake_map_exists", false)),
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"surface_count": surfaces.size(),
			"near_position": _vector3_to_array(near_after),
			"far_position": _vector3_to_array(far_after),
			"expected_near": _vector3_to_array(expected_near),
			"expected_far": _vector3_to_array(expected_far),
			"wake_origin": [wake_origin.x, wake_origin.y],
			"wake_pending_count": wake_pending_count,
			"active_ripple_count": active_ripple_count,
			"wake_sample_center": [wake_sample.r, wake_sample.g, wake_sample.b, wake_sample.a],
			"debug": debug_state,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater ocean contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater ocean contract check saved: " + global_path)
	quit(0 if passed else 1)


func _sample_wake_at(wake: Object, world_position: Vector3) -> Color:
	if wake == null or not _has_property(wake, "texture"):
		return Color(0.5, 0.0, 0.5, 0.5)
	var texture := wake.get("texture") as Texture2D
	if texture == null:
		return Color(0.5, 0.0, 0.5, 0.5)
	var image := texture.get_image()
	if image == null or image.get_width() <= 0 or image.get_height() <= 0:
		return Color(0.5, 0.0, 0.5, 0.5)
	var origin := Vector2.ZERO
	if _has_property(wake, "origin_xz"):
		origin = wake.get("origin_xz")
	var world_size := 1.0
	if _has_property(wake, "world_size_m"):
		world_size = max(float(wake.get("world_size_m")), 0.001)
	var uv := (Vector2(world_position.x, world_position.z) - origin) / world_size + Vector2(0.5, 0.5)
	var px := clampi(int(round(uv.x * float(image.get_width() - 1))), 0, image.get_width() - 1)
	var py := clampi(int(round(uv.y * float(image.get_height() - 1))), 0, image.get_height() - 1)
	return image.get_pixel(px, py)


func _near_equal(object: Object, property_name: String, expected: float, epsilon: float = 0.001) -> bool:
	if object == null or not _has_property(object, property_name):
		return false
	return absf(float(object.get(property_name)) - expected) <= epsilon


func _color_equal(a: Variant, b: Variant, epsilon: float = 0.001) -> bool:
	if not (a is Color) or not (b is Color):
		return false
	var ca: Color = a
	var cb: Color = b
	return absf(ca.r - cb.r) <= epsilon and absf(ca.g - cb.g) <= epsilon and absf(ca.b - cb.b) <= epsilon and absf(ca.a - cb.a) <= epsilon


func _read_int(object: Object, property_name: String, fallback: int) -> int:
	if object == null:
		return fallback
	var value: Variant = object.get(property_name)
	if value == null:
		return fallback
	return int(value)


func _read_array_size(object: Object, property_name: String) -> int:
	if object == null:
		return 0
	var value: Variant = object.get(property_name)
	if value is Array:
		return value.size()
	return 0


func _vector3_to_array(value: Vector3) -> Array:
	return [value.x, value.y, value.z]


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
