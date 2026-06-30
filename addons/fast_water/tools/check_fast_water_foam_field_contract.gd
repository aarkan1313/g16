extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")

class TestFlowProvider:
	extends Node

	func sample_flow_at(_world_position: Vector3) -> Vector3:
		return Vector3(2.0, 0.0, 0.0)

	func get_turbulence_at(_world_position: Vector3) -> float:
		return 1.0


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_foam_field_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterFoamFieldContractCheck"
	root.add_child(scene)

	var flow_provider := TestFlowProvider.new()
	flow_provider.name = "FoamTestFlowProvider"
	scene.add_child(flow_provider)

	var surface := SURFACE_SCRIPT.new()
	surface.name = "FoamContractSurface"
	surface.set("follow_camera", false)
	surface.set("auto_create_mesh", false)
	surface.set("auto_create_wake_map", false)
	surface.set("auto_create_bubbles", false)
	surface.set("auto_create_foam_field", false)
	scene.add_child(surface)

	var foam_field := FOAM_FIELD_SCRIPT.new()
	foam_field.name = "FoamField"
	foam_field.set("resolution", 64)
	foam_field.set("world_size_m", 24.0)
	foam_field.set("origin_xz", Vector2.ZERO)
	foam_field.set("dissipation_per_second", 0.35)
	foam_field.set("advection_strength", 1.0)
	foam_field.set("turbulence_thicken_strength", 0.30)
	foam_field.set("current_thicken_strength", 0.04)
	foam_field.set("flow_provider", flow_provider)
	scene.add_child(foam_field)

	var control_field := FOAM_FIELD_SCRIPT.new()
	control_field.name = "ControlFoamField"
	control_field.set("resolution", 64)
	control_field.set("world_size_m", 24.0)
	control_field.set("origin_xz", Vector2.ZERO)
	control_field.set("dissipation_per_second", 0.35)
	control_field.set("advection_strength", 1.0)
	control_field.set("turbulence_thicken_strength", 0.0)
	control_field.set("current_thicken_strength", 0.0)
	control_field.set("flow_provider", flow_provider)
	scene.add_child(control_field)

	await process_frame
	await process_frame

	surface.set("foam_field_node", foam_field)
	surface.set("foam_field_strength", 1.35)
	if surface.has_method("set_foam_field_texture"):
		surface.call("set_foam_field_texture", foam_field.get("texture"), foam_field.get("origin_xz"), foam_field.get("world_size_m"))
	if foam_field.has_method("bind_to_material"):
		foam_field.call("bind_to_material", surface.get("water_material") as ShaderMaterial, 1.35)

	var shoreline_pos := Vector3(-3.5, 0.0, -1.0)
	var wake_pos := Vector3(0.0, 0.0, 0.0)
	var rapids_pos := Vector3(3.0, 0.0, 1.0)
	var rapids_downstream_pos := rapids_pos + Vector3(2.0, 0.0, 0.0)
	foam_field.call("add_foam_stamp", shoreline_pos, 1.4, 0.72, FOAM_FIELD_SCRIPT.SourceKind.SHORELINE, Vector3.ZERO)
	foam_field.call("add_foam_stamp", wake_pos, 1.1, 0.58, FOAM_FIELD_SCRIPT.SourceKind.WAKE, Vector3(1.0, 0.0, 0.0))
	foam_field.call("add_foam_stamp", rapids_pos, 1.6, 0.94, FOAM_FIELD_SCRIPT.SourceKind.RAPIDS, Vector3(2.0, 0.0, 0.0))
	control_field.call("add_foam_stamp", rapids_pos, 1.6, 0.94, FOAM_FIELD_SCRIPT.SourceKind.RAPIDS, Vector3(2.0, 0.0, 0.0))

	var shoreline_foam: float = foam_field.call("sample_foam_at", shoreline_pos)
	var wake_foam: float = foam_field.call("sample_foam_at", wake_pos)
	var rapids_foam_before: float = foam_field.call("sample_foam_at", rapids_pos)
	var downstream_foam_before: float = foam_field.call("sample_foam_at", rapids_downstream_pos)
	var rapids_source: int = foam_field.call("sample_source_at", rapids_pos)
	foam_field.call("advance", 1.0)
	control_field.call("advance", 1.0)
	var rapids_foam_after: float = foam_field.call("sample_foam_at", rapids_pos)
	var downstream_foam_after: float = foam_field.call("sample_foam_at", rapids_downstream_pos)
	var control_downstream_after: float = control_field.call("sample_foam_at", rapids_downstream_pos)
	var material := surface.get("water_material") as ShaderMaterial

	var checks := {
		"texture_created": foam_field.get("texture") != null,
		"shoreline_source_stamped": shoreline_foam > 0.25,
		"wake_source_stamped": wake_foam > 0.20,
		"rapids_source_stamped": rapids_foam_before > 0.45,
		"rapids_source_id": rapids_source == FOAM_FIELD_SCRIPT.SourceKind.RAPIDS,
		"dissipates": rapids_foam_after < rapids_foam_before,
		"advects_downstream": downstream_foam_after > downstream_foam_before + 0.15,
		"thickens_from_turbulence_current": downstream_foam_after > control_downstream_after + 0.08,
		"material_bound": material != null and bool(material.get_shader_parameter("foam_field_enabled")),
		"material_strength": material != null and abs(float(material.get_shader_parameter("foam_field_strength")) - 1.35) < 0.01,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"shoreline": shoreline_foam,
			"wake": wake_foam,
			"rapids_before": rapids_foam_before,
			"rapids_after": rapids_foam_after,
			"downstream_before": downstream_foam_before,
			"downstream_after": downstream_foam_after,
			"control_downstream_after": control_downstream_after,
			"rapids_source": rapids_source,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater foam field contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater foam field contract check saved: " + global_path)
	quit(0 if passed else 1)
