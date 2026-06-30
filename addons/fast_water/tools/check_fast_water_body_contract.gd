extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_body_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterBodyContractCheck"
	root.add_child(scene)

	var lake := SURFACE_SCRIPT.new()
	lake.name = "ContractLake"
	lake.set("follow_camera", false)
	lake.set("auto_create_mesh", false)
	lake.set("auto_create_wake_map", false)
	lake.set("body_profile", BODY_PROFILE_SCRIPT.lake())
	lake.position = Vector3(0.0, 0.0, 0.0)
	scene.add_child(lake)

	var river_profile := BODY_PROFILE_SCRIPT.river()
	river_profile.set("query_priority", 40)
	var river := PATH_SCRIPT.new()
	river.name = "ContractRiver"
	river.set("auto_create_flow_field", false)
	river.set("body_profile", river_profile)
	river.set("width_m", 2.2)
	river.set("flow_speed_mps", 2.4)
	river.set("control_points", PackedVector3Array([
		Vector3(-5.0, 0.35, 0.0),
		Vector3(0.0, 0.05, 0.0),
		Vector3(5.0, -0.20, 0.0),
	]))
	scene.add_child(river)

	await process_frame
	await process_frame

	var lake_point := Vector3(8.0, -1.0, 0.0)
	var river_point := Vector3(0.0, -0.65, 0.12)
	var above_point := Vector3(0.0, 1.50, 0.0)
	var lake_query := BODY_QUERY_SCRIPT.find_best_body_query(self, lake_point, false)
	var river_query := BODY_QUERY_SCRIPT.find_best_body_query(self, river_point, false)
	var above_containing_query := BODY_QUERY_SCRIPT.find_best_body_query(self, above_point, false)
	var above_nearest_query := BODY_QUERY_SCRIPT.find_best_body_query(self, above_point, true)

	var checks := {
		"body_count": BODY_QUERY_SCRIPT.get_water_bodies(self).size() >= 2,
		"lake_selected": lake_query.get("body", null) == lake,
		"river_selected": river_query.get("body", null) == river,
		"river_contains": bool(river_query.get("contains", false)),
		"river_depth_positive": float(river_query.get("depth", 0.0)) > 0.1,
		"river_flow_positive": (river_query.get("flow", Vector3.ZERO) as Vector3).length() > 0.1,
		"above_not_containing": not bool(above_containing_query.get("valid", false)),
		"above_nearest_selected": above_nearest_query.get("body", null) == river,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"lake_query": _serialize_query(lake_query),
		"river_query": _serialize_query(river_query),
		"above_containing_query": _serialize_query(above_containing_query),
		"above_nearest_query": _serialize_query(above_nearest_query),
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater body contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater body contract check saved: " + global_path)
	quit(0 if passed else 1)


func _serialize_query(query: Dictionary) -> Dictionary:
	var body := query.get("body", null) as Node
	var flow := query.get("flow", Vector3.ZERO) as Vector3
	var height := float(query.get("height", INF))
	var altitude := float(query.get("altitude", INF))
	return {
		"valid": bool(query.get("valid", false)),
		"body_name": body.name if body != null else "",
		"body_kind": String(query.get("body_kind", "")),
		"priority": int(query.get("priority", 0)),
		"height": _finite_or_null(height),
		"altitude": _finite_or_null(altitude),
		"depth": float(query.get("depth", 0.0)),
		"flow": [flow.x, flow.y, flow.z],
		"contains": bool(query.get("contains", false)),
	}


func _finite_or_null(value: float) -> Variant:
	if value != value or absf(value) > 1.0e20:
		return null
	return value
