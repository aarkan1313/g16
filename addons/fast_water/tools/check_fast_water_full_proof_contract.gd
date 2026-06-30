extends SceneTree

const FULL_PROOF_SCENE := "res://addons/fast_water/demo/fast_water_full_proof.tscn"

var _scene: Node


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_full_proof_contract.json"
	var frames := 12

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))

	var packed := load(FULL_PROOF_SCENE) as PackedScene
	if packed == null:
		_write_report(output_path, false, {"scene_loads": false}, {})
		quit(1)
		return

	_scene = packed.instantiate()
	if _has_property(_scene, "gate_external_capture"):
		_scene.set("gate_external_capture", true)
	root.add_child(_scene)
	_check.call_deferred(output_path, frames)


func _check(output_path: String, frames: int) -> void:
	for _i in range(frames):
		await process_frame

	var main_surface := _find_named(root, "FastWaterSurface")
	var river := _find_named(root, "FullProofInletRiver")
	var waterfall := _find_named(root, "FastWaterWaterfall")
	var ocean := _find_named(root, "FullProofOceanPreview")
	var reflection := _find_named(root, "PlanarReflection")
	var caustics := _find_named(root, "Caustics")
	var weather_adapter := _find_named(root, "WeatherAdapter")
	var weather_sequence := _find_named(root, "WeatherSequence")
	var rain_fx := _find_named(root, "RainImpactFx")
	var underwater := _find_named(root, "UnderwaterController")
	var foam_field := _find_named(root, "WaterfallFoamField")
	var flow_field := _find_named(root, "FlowField")

	var ocean_surface_count := 0
	if ocean != null and ocean.has_method("get_surface_nodes"):
		ocean_surface_count = (ocean.call("get_surface_nodes") as Array).size()

	var checks := {
		"scene_loads": _scene != null,
		"main_surface_present": _has_methods(main_surface, ["add_splash", "add_wake_point", "get_surface_height_at"]),
		"inlet_river_present": _has_methods(river, ["get_flow_at", "get_sampled_world_points"]),
		"waterfall_present": _has_methods(waterfall, ["stamp_waterfall_response", "get_lod_state"]),
		"ocean_preview_present": ocean != null and ocean_surface_count >= 2,
		"planar_reflection_present": reflection != null,
		"caustics_present": caustics != null,
		"weather_adapter_present": weather_adapter != null and _has_property(weather_adapter, "environment_state"),
		"weather_sequence_present": weather_sequence != null,
		"rain_fx_present": rain_fx != null,
		"underwater_controller_present": underwater != null,
		"foam_field_present": _has_methods(foam_field, ["add_foam_stamp", "sample_foam_at"]),
		"flow_field_present": flow_field != null,
	}
	var passed := _all_values_true(checks)
	var details := {
		"scene": FULL_PROOF_SCENE,
		"ocean_surface_count": ocean_surface_count,
		"nodes": {
			"main_surface": _node_path_or_empty(main_surface),
			"inlet_river": _node_path_or_empty(river),
			"waterfall": _node_path_or_empty(waterfall),
			"ocean_preview": _node_path_or_empty(ocean),
			"reflection": _node_path_or_empty(reflection),
			"caustics": _node_path_or_empty(caustics),
			"weather_adapter": _node_path_or_empty(weather_adapter),
			"weather_sequence": _node_path_or_empty(weather_sequence),
			"rain_fx": _node_path_or_empty(rain_fx),
			"underwater": _node_path_or_empty(underwater),
			"foam_field": _node_path_or_empty(foam_field),
			"flow_field": _node_path_or_empty(flow_field),
		},
	}
	_write_report(output_path, passed, checks, details)
	quit(0 if passed else 1)


func _write_report(path: String, passed: bool, checks: Dictionary, details: Dictionary) -> void:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater full proof contract could not write " + global_path)
		return
	file.store_string(JSON.stringify({
		"passed": passed,
		"checks": checks,
		"details": details,
	}, "\t"))
	file.close()
	print("FastWater full proof contract saved: " + global_path)


func _find_named(node: Node, target_name: String) -> Node:
	if node == null:
		return null
	if node.name == target_name:
		return node
	for child in node.get_children():
		var found := _find_named(child, target_name)
		if found != null:
			return found
	return null


func _has_methods(node: Node, method_names: Array[String]) -> bool:
	if node == null:
		return false
	for method_name in method_names:
		if not node.has_method(method_name):
			return false
	return true


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false


func _all_values_true(values: Dictionary) -> bool:
	for value in values.values():
		if not bool(value):
			return false
	return true


func _node_path_or_empty(node: Node) -> String:
	return str(node.get_path()) if node != null else ""
