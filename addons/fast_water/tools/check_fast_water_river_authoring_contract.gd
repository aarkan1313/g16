extends SceneTree

const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_river_authoring_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterRiverAuthoringContractCheck"
	root.add_child(scene)

	var river := _add_authored_river(scene)
	await process_frame
	await process_frame

	var flow_field := river.get("flow_field_node") as Node
	if flow_field == null:
		flow_field = river.get_node_or_null("FlowField")
	if flow_field != null and flow_field.has_method("rebuild"):
		flow_field.call("rebuild")

	var start_probe := Vector3(-7.0, -0.2, 0.0)
	var mid_probe := Vector3(0.0, -0.2, 0.0)
	var end_probe := Vector3(7.0, -0.2, 0.0)
	var start_width: float = river.call("get_channel_width_at", start_probe)
	var mid_width: float = river.call("get_channel_width_at", mid_probe)
	var end_width: float = river.call("get_channel_width_at", end_probe)
	var start_depth: float = river.call("get_channel_depth_at", start_probe)
	var mid_depth: float = river.call("get_channel_depth_at", mid_probe)
	var start_speed: float = river.call("get_current_speed_at", start_probe)
	var end_speed: float = river.call("get_current_speed_at", end_probe)
	var start_turbulence: float = river.call("get_turbulence_at", start_probe)
	var mid_turbulence: float = river.call("get_turbulence_at", mid_probe)
	var start_bank_foam: float = river.call("get_bank_foam_at", start_probe)
	var mid_bank_foam: float = river.call("get_bank_foam_at", mid_probe)

	var start_flow := Vector3.ZERO
	var end_flow := Vector3.ZERO
	var center_foam := 0.0
	var bank_foam := 0.0
	if flow_field != null:
		if flow_field.has_method("sample_flow_at"):
			start_flow = flow_field.call("sample_flow_at", start_probe) as Vector3
			end_flow = flow_field.call("sample_flow_at", end_probe) as Vector3
		if flow_field.has_method("sample_foam_at"):
			center_foam = float(flow_field.call("sample_foam_at", mid_probe))
			bank_foam = float(flow_field.call("sample_foam_at", Vector3(0.0, -0.2, 2.25)))

	var checks := {
		"width_varies": end_width > start_width + 2.0 and mid_width > start_width + 1.2,
		"depth_varies": mid_depth > start_depth + 0.45,
		"current_varies": end_speed > start_speed + 1.8,
		"turbulence_varies": mid_turbulence > start_turbulence + 0.75,
		"bank_foam_varies": mid_bank_foam > start_bank_foam + 0.6,
		"narrow_start_rejects_bank_probe": not bool(river.call("contains_water_point", Vector3(-7.0, -0.1, 1.85))),
		"wide_end_accepts_bank_probe": bool(river.call("contains_water_point", Vector3(7.0, -0.1, 2.40))),
		"flow_field_exists": flow_field != null,
		"flow_field_current_varies": end_flow.x > start_flow.x + 1.2,
		"flow_field_bank_foam": bank_foam > center_foam + 0.12,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"widths": {"start": start_width, "mid": mid_width, "end": end_width},
		"depths": {"start": start_depth, "mid": mid_depth},
		"current_speeds": {"start": start_speed, "end": end_speed},
		"turbulence": {"start": start_turbulence, "mid": mid_turbulence},
		"bank_foam_authoring": {"start": start_bank_foam, "mid": mid_bank_foam},
		"flow_field": {
			"start_flow": [start_flow.x, start_flow.y, start_flow.z],
			"end_flow": [end_flow.x, end_flow.y, end_flow.z],
			"center_foam": center_foam,
			"bank_foam": bank_foam,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater river authoring contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater river authoring contract check saved: " + global_path)
	quit(0 if passed else 1)


func _add_authored_river(scene: Node3D) -> Node:
	var profile := BODY_PROFILE_SCRIPT.river()
	var river := PATH_SCRIPT.new()
	river.name = "AuthoredRiver"
	river.set("body_profile", profile)
	river.set("control_points", PackedVector3Array([
		Vector3(-8.0, 0.18, 0.0),
		Vector3(0.0, 0.0, 0.0),
		Vector3(8.0, -0.22, 0.0),
	]))
	river.set("width_m", 3.0)
	river.set("flow_speed_mps", 1.2)
	river.set("subdivisions_per_segment", 16)
	river.set("flow_field_resolution", 128)
	river.set("flow_field_margin_m", 3.0)
	river.set("point_widths_m", PackedFloat32Array([2.0, 5.2, 7.0]))
	river.set("point_depths_m", PackedFloat32Array([0.55, 1.25, 2.1]))
	river.set("point_flow_speeds_mps", PackedFloat32Array([0.65, 2.4, 4.2]))
	river.set("point_bank_foam_strengths", PackedFloat32Array([0.18, 1.25, 0.52]))
	river.set("point_turbulence_strengths", PackedFloat32Array([0.05, 1.35, 0.28]))
	scene.add_child(river)
	return river
