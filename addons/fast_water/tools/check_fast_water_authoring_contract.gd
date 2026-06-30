extends SceneTree

const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const FLOW_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_flow_field.gd")
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const WATERFALL_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_waterfall.gd")
const AUTHORING_OVERLAY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_authoring_overlay.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_authoring_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterAuthoringContract"
	root.add_child(scene)

	var path := PATH_SCRIPT.new()
	path.name = "AuthoringRiverPath"
	path.set("control_points", PackedVector3Array([
		Vector3(-8.0, 0.25, 0.0),
		Vector3(-2.5, 0.04, -2.0),
		Vector3(3.0, -0.22, 1.4),
		Vector3(8.0, -0.38, -0.6),
	]))
	path.set("point_widths_m", PackedFloat32Array([5.0, 7.5, 4.4, 6.2]))
	path.set("point_flow_speeds_mps", PackedFloat32Array([1.1, 2.2, 3.1, 2.4]))
	path.set("point_bank_foam_strengths", PackedFloat32Array([0.35, 0.75, 1.0, 0.55]))
	path.set("point_turbulence_strengths", PackedFloat32Array([0.2, 0.65, 0.95, 0.40]))
	path.set("flow_field_resolution", 64)
	scene.add_child(path)

	var foam_field := FOAM_FIELD_SCRIPT.new()
	foam_field.name = "AuthoringFoamField"
	foam_field.set("resolution", 64)
	foam_field.set("origin_xz", Vector2(0.0, -0.4))
	foam_field.set("world_size_m", 28.0)
	scene.add_child(foam_field)

	var waterfall := WATERFALL_SCRIPT.new()
	waterfall.name = "AuthoringWaterfall"
	waterfall.position = Vector3(0.0, 1.5, -5.5)
	waterfall.set("lip_points", PackedVector3Array([
		Vector3(-1.8, 0.0, 0.0),
		Vector3(0.0, 0.12, 0.0),
		Vector3(1.8, 0.0, 0.0),
	]))
	waterfall.set("shelf_points", PackedVector3Array([
		Vector3(-0.8, -1.25, 0.45),
		Vector3(0.8, -1.30, 0.50),
	]))
	waterfall.set("drop_height_m", 3.2)
	waterfall.set("downstream_offset_m", 1.2)
	waterfall.set("foam_field", foam_field)
	scene.add_child(waterfall)

	await process_frame
	await process_frame
	path.call("rebuild_path_mesh")
	waterfall.call("rebuild")
	foam_field.call("add_foam_stamp", Vector3(1.0, 0.0, -0.3), 2.4, 0.88, FOAM_FIELD_SCRIPT.SourceKind.RAPIDS, Vector3(2.0, 0.0, 0.5))

	var flow_field := path.get("flow_field_node") as Node
	var flow_export_path := "res://artifacts/fast_water_flow_field_export.png"
	var foam_export_path := "res://artifacts/fast_water_foam_field_export.png"
	var flow_export_err := flow_field.call("export_image", flow_export_path) if flow_field != null and flow_field.has_method("export_image") else ERR_UNAVAILABLE
	var foam_export_err: int = foam_field.call("export_image", foam_export_path)

	var flow_probe := Vector3(-2.5, 0.0, -2.0)
	var flow_before := flow_field.call("sample_flow_at", flow_probe) as Vector3 if flow_field != null else Vector3.ZERO
	var flow_import := FLOW_FIELD_SCRIPT.new()
	flow_import.name = "ImportedFlowField"
	scene.add_child(flow_import)
	var flow_import_err: int = flow_import.call(
		"import_image",
		flow_export_path,
		flow_field.get("origin_xz") if flow_field != null else Vector2.ZERO,
		float(flow_field.get("world_size_m")) if flow_field != null else 1.0,
		float(flow_field.get("flow_encode_scale_mps")) if flow_field != null else 8.0
	)
	var flow_after := flow_import.call("sample_flow_at", flow_probe) as Vector3

	var foam_probe := Vector3(1.0, 0.0, -0.3)
	var foam_before: float = foam_field.call("sample_foam_at", foam_probe)
	var foam_source_before: int = foam_field.call("sample_source_at", foam_probe)
	var foam_import := FOAM_FIELD_SCRIPT.new()
	foam_import.name = "ImportedFoamField"
	scene.add_child(foam_import)
	var foam_import_err: int = foam_import.call("import_image", foam_export_path, foam_field.get("origin_xz"), float(foam_field.get("world_size_m")))
	var foam_after: float = foam_import.call("sample_foam_at", foam_probe)
	var foam_source_after: int = foam_import.call("sample_source_at", foam_probe)

	var overlay := AUTHORING_OVERLAY_SCRIPT.new()
	overlay.name = "AuthoringOverlay"
	overlay.set("auto_discover", true)
	overlay.set("foam_sample_grid", 18)
	overlay.set("foam_source_threshold", 0.015)
	scene.add_child(overlay)
	var path_paths: Array[NodePath] = [overlay.get_path_to(path)]
	var waterfall_paths: Array[NodePath] = [overlay.get_path_to(waterfall)]
	var foam_field_paths: Array[NodePath] = [overlay.get_path_to(foam_field)]
	overlay.set("path_paths", path_paths)
	overlay.set("waterfall_paths", waterfall_paths)
	overlay.set("foam_field_paths", foam_field_paths)
	await process_frame
	overlay.call("rebuild_now")
	var overlay_state := overlay.call("get_debug_state") as Dictionary
	var flow_export_exists := FileAccess.file_exists(ProjectSettings.globalize_path(flow_export_path))
	var foam_export_exists := FileAccess.file_exists(ProjectSettings.globalize_path(foam_export_path))

	var checks := {
		"flow_export_succeeded": flow_export_err == OK and flow_export_exists,
		"flow_import_succeeded": flow_import_err == OK,
		"flow_round_trip_matches": flow_before.distance_to(flow_after) < 0.12 and flow_after.length() > 0.20,
		"foam_export_succeeded": foam_export_err == OK and foam_export_exists,
		"foam_import_succeeded": foam_import_err == OK,
		"foam_round_trip_matches": absf(foam_before - foam_after) < 0.08 and foam_after > 0.40,
		"foam_source_round_trip_matches": foam_source_before == FOAM_FIELD_SCRIPT.SourceKind.RAPIDS and foam_source_after == foam_source_before,
		"overlay_finds_path": int(overlay_state.get("path_count", 0)) == 1,
		"overlay_draws_path_controls": int(overlay_state.get("path_control_lines", 0)) > 0,
		"overlay_draws_path_widths": int(overlay_state.get("path_width_lines", 0)) > 0,
		"overlay_draws_flow_arrows": int(overlay_state.get("flow_arrows", 0)) > 0,
		"overlay_draws_waterfall_lips": int(overlay_state.get("waterfall_lines", 0)) > 0,
		"overlay_draws_foam_bounds": int(overlay_state.get("foam_bounds_lines", 0)) >= 4,
		"overlay_draws_foam_sources": int(overlay_state.get("foam_source_crosses", 0)) > 0,
		"overlay_has_line_mesh": int(overlay_state.get("line_vertex_count", 0)) > 16,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"flow_before": [flow_before.x, flow_before.y, flow_before.z],
			"flow_after": [flow_after.x, flow_after.y, flow_after.z],
			"foam_before": foam_before,
			"foam_after": foam_after,
			"foam_source_before": foam_source_before,
			"foam_source_after": foam_source_after,
			"overlay": overlay_state,
			"flow_export_path": ProjectSettings.globalize_path(flow_export_path),
			"foam_export_path": ProjectSettings.globalize_path(foam_export_path),
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater authoring contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater authoring contract check saved: " + global_path)
	quit(0 if passed else 1)
