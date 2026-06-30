extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")
const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const QUALITY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_quality.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_world_integration_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterWorldIntegrationContract"
	root.add_child(scene)

	var camera := Camera3D.new()
	camera.name = "WorldCamera"
	scene.add_child(camera)
	camera.position = Vector3(64.0, 8.0, 64.0)

	var focus := Node3D.new()
	focus.name = "WorldWakeFocus"
	scene.add_child(focus)
	focus.position = Vector3(64.0, 0.0, 64.0)

	var lake := _make_lake()
	scene.add_child(lake)

	var stream := _make_stream()
	scene.add_child(stream)

	var ocean := _make_ocean(camera, focus)
	scene.add_child(ocean)

	for _i in range(8):
		await process_frame

	var quality := QUALITY_SCRIPT.low()
	lake.call("apply_visual_profile", VISUAL_PROFILE_SCRIPT.gameplay_lake())
	lake.call("apply_quality", quality)
	ocean.call("apply_ocean_profile", OCEAN_PROFILE_SCRIPT.performance())

	for _i in range(4):
		await process_frame

	var lake_pos := Vector3(0.0, -0.25, 0.0)
	var stream_pos := Vector3(0.0, -0.15, 24.0)
	var ocean_pos := Vector3(220.0, -2.0, 220.0)
	var lake_query: Dictionary = BODY_QUERY_SCRIPT.find_best_body_query(root.get_tree(), lake_pos)
	var stream_query: Dictionary = BODY_QUERY_SCRIPT.find_best_body_query(root.get_tree(), stream_pos)
	var ocean_query: Dictionary = BODY_QUERY_SCRIPT.find_best_body_query(root.get_tree(), ocean_pos)

	lake.call("add_wake_point", lake_pos, Vector3(4.0, 0.0, 0.0), 0.8)
	stream.call("add_wake_point", stream_pos, Vector3(2.0, 0.0, 0.0), 0.55)
	ocean.call("add_wake_point", focus.global_position, Vector3(6.0, 0.0, 0.0), 0.9)

	for _i in range(3):
		await process_frame

	var lake_material := lake.get("water_material") as ShaderMaterial
	var stream_material := stream.get("path_material") as ShaderMaterial
	var ocean_surfaces: Array = ocean.call("get_surface_nodes")
	var near_ocean: Node = ocean_surfaces[0] if ocean_surfaces.size() > 0 else null
	var far_ocean: Node = ocean_surfaces[1] if ocean_surfaces.size() > 1 else null
	var near_material := near_ocean.get("water_material") as ShaderMaterial if near_ocean != null and _has_property(near_ocean, "water_material") else null
	var ocean_debug: Dictionary = ocean.call("get_ocean_debug_state")

	var stream_width_a := float(stream.call("get_channel_width_at", Vector3(-22.0, 0.0, 24.0)))
	var stream_width_b := float(stream.call("get_channel_width_at", Vector3(22.0, 0.0, 24.0)))
	var stream_speed_a := float(stream.call("get_current_speed_at", Vector3(-22.0, 0.0, 24.0)))
	var stream_speed_b := float(stream.call("get_current_speed_at", Vector3(22.0, 0.0, 24.0)))
	var stream_flow: Vector3 = stream.call("get_flow_at", stream_pos)
	var stream_flow_field := stream.get("flow_field_node") if _has_property(stream, "flow_field_node") else null
	var stream_clock_ok := _ripple_clock_matches_material(stream_material)

	var checks := {
		"lake_query_selects_lake": _query_kind(lake_query) == "lake",
		"stream_query_selects_river": _query_kind(stream_query) == "river",
		"ocean_query_selects_ocean": _query_kind(ocean_query) == "ocean",
		"ocean_query_returns_interactive_facade": ocean_query.get("body") == ocean and ocean.has_method("add_wake_point"),
		"stream_per_point_tuning_varies": not is_equal_approx(stream_width_a, stream_width_b) and not is_equal_approx(stream_speed_a, stream_speed_b),
		"stream_flow_field_created": stream_flow_field != null,
		"stream_flow_query_nonzero": stream_flow.length() > 0.1,
		"lake_quality_profile_applied": int(lake.get("mesh_subdivisions")) == int(quality.mesh_subdivisions) and int(lake.get("max_hero_ripples")) == int(quality.hero_ripples),
		"lake_wake_public_api_routes": _hero_ripple_count(lake_material) > 0,
		"stream_wake_public_api_routes": _hero_ripple_count(stream_material) > 0 and stream_clock_ok,
		"ocean_near_wake_public_api_routes": _hero_ripple_count(near_material) > 0,
		"ocean_near_far_surfaces_exist": near_ocean != null and far_ocean != null,
		"ocean_far_surface_noninteractive": far_ocean != null and _has_property(far_ocean, "interactions_enabled") and not bool(far_ocean.get("interactions_enabled")),
		"ocean_macro_lod_matched": near_ocean != null and far_ocean != null and _near_prop(near_ocean, "wave_height", float(far_ocean.get("wave_height"))) and _near_prop(near_ocean, "wave_frequency", float(far_ocean.get("wave_frequency"))) and _near_prop(near_ocean, "whitecap_strength", float(far_ocean.get("whitecap_strength"))),
		"ocean_far_detail_cost_removed": near_ocean != null and far_ocean != null and int(far_ocean.get("mesh_subdivisions")) < int(near_ocean.get("mesh_subdivisions")) and _near_prop(far_ocean, "micro_normal_strength", 0.0) and _near_prop(far_ocean, "detail_normal_strength", 0.0) and _near_prop(far_ocean, "refraction_strength", 0.0),
		"ocean_near_edge_fade_enabled": near_ocean != null and _near_prop(near_ocean, "mesh_edge_fade_width", float(OCEAN_PROFILE_SCRIPT.performance().get("near_edge_fade_width"))),
		"ocean_focus_recenters_near_surface": bool(ocean_debug.get("near_surface", {}).get("exists", false)) and float(ocean_debug.get("local_wake_world_size_m", 0.0)) > 0.0,
	}
	var passed := _all_values_true(checks)
	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"lake_query": _query_sample(lake_query),
			"stream_query": _query_sample(stream_query),
			"ocean_query": _query_sample(ocean_query),
			"stream_widths": [stream_width_a, stream_width_b],
			"stream_speeds": [stream_speed_a, stream_speed_b],
			"stream_flow": [stream_flow.x, stream_flow.y, stream_flow.z],
			"lake_hero_ripples": _hero_ripple_count(lake_material),
			"stream_hero_ripples": _hero_ripple_count(stream_material),
			"stream_ripple_clock_ok": stream_clock_ok,
			"near_ocean_hero_ripples": _hero_ripple_count(near_material),
			"ocean_debug": ocean_debug,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater world integration contract could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater world integration contract saved: " + global_path)
	quit(0 if passed else 1)


func _make_lake() -> Node:
	var lake := SURFACE_SCRIPT.new()
	lake.name = "IntegrationLake"
	lake.set("auto_create_mesh", false)
	lake.set("auto_create_wake_map", true)
	lake.set("use_gpu_wake_map", false)
	lake.set("auto_create_bubbles", false)
	lake.set("mesh_size_m", 84.0)
	lake.set("wake_map_world_size_m", 48.0)
	lake.set("follow_camera", false)
	var profile := BODY_PROFILE_SCRIPT.lake()
	profile.query_priority = 5
	lake.set("body_profile", profile)
	return lake


func _make_stream() -> Node:
	var stream := PATH_SCRIPT.new()
	stream.name = "IntegrationStream"
	stream.control_points = PackedVector3Array([
		Vector3(-28.0, 0.0, 24.0),
		Vector3(-12.0, -0.08, 20.0),
		Vector3(8.0, -0.20, 28.0),
		Vector3(28.0, -0.35, 23.0),
	])
	stream.point_widths_m = PackedFloat32Array([3.2, 4.6, 6.4, 4.0])
	stream.point_depths_m = PackedFloat32Array([0.6, 0.9, 1.35, 0.8])
	stream.point_flow_speeds_mps = PackedFloat32Array([0.9, 1.35, 2.2, 1.15])
	stream.point_bank_foam_strengths = PackedFloat32Array([0.15, 0.35, 0.85, 0.28])
	stream.point_turbulence_strengths = PackedFloat32Array([0.12, 0.28, 0.78, 0.20])
	stream.flow_field_resolution = 128
	stream.flow_field_margin_m = 5.0
	stream.auto_create_flow_field = true
	return stream


func _make_ocean(camera: Camera3D, focus: Node3D) -> Node:
	var ocean := OCEAN_SCRIPT.new()
	ocean.name = "IntegrationOcean"
	ocean.target_camera = camera
	ocean.wake_focus = focus
	ocean.water_y_offset_m = -1.0
	ocean.ocean_profile = OCEAN_PROFILE_SCRIPT.performance()
	return ocean


func _hero_ripple_count(material: ShaderMaterial) -> int:
	if material == null:
		return 0
	var value = material.get_shader_parameter("hero_ripple_count")
	return int(value) if value != null else 0


func _ripple_clock_matches_material(material: ShaderMaterial) -> bool:
	if material == null:
		return false
	var ripples = material.get_shader_parameter("hero_ripples")
	if not (ripples is PackedVector4Array) or ripples.size() == 0:
		return false
	var now_value = material.get_shader_parameter("hero_ripple_time")
	if now_value == null:
		return false
	var now := float(now_value)
	var newest_spawn := -INF
	for ripple in ripples:
		if ripple.w > 0.0:
			newest_spawn = maxf(newest_spawn, ripple.z)
	return is_finite(newest_spawn) and now >= newest_spawn


func _query_kind(query: Dictionary) -> String:
	return str(query.get("body_kind", ""))


func _query_sample(query: Dictionary) -> Dictionary:
	var body := query.get("body", null) as Node
	var flow: Vector3 = query.get("flow", Vector3.ZERO)
	return {
		"valid": bool(query.get("valid", false)),
		"body": body.name if body != null else "",
		"body_kind": _query_kind(query),
		"priority": int(query.get("priority", 0)),
		"contains": bool(query.get("contains", false)),
		"height": float(query.get("height", 0.0)),
		"depth": float(query.get("depth", 0.0)),
		"flow": [flow.x, flow.y, flow.z],
	}


func _all_values_true(values: Dictionary) -> bool:
	for value in values.values():
		if not bool(value):
			return false
	return true


func _near_prop(object: Object, property_name: String, expected: float, epsilon: float = 0.001) -> bool:
	if object == null or not _has_property(object, property_name):
		return false
	return absf(float(object.get(property_name)) - expected) <= epsilon


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
