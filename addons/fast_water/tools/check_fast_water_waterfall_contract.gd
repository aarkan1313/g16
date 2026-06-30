extends SceneTree

const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const FLOW_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_flow_field.gd")
const WATERFALL_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_waterfall.gd")


class BurstSpy:
	extends Node3D

	var events: Array[Dictionary] = []

	func burst(world_pos: Vector3, strength: float = 1.0, radius: float = 0.5) -> void:
		events.append({
			"method": "burst",
			"position": [world_pos.x, world_pos.y, world_pos.z],
			"strength": strength,
			"radius": radius,
		})


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_waterfall_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterWaterfallContract"
	root.add_child(scene)

	var foam_field := FOAM_FIELD_SCRIPT.new()
	foam_field.name = "FoamField"
	foam_field.set("resolution", 128)
	foam_field.set("world_size_m", 28.0)
	foam_field.set("origin_xz", Vector2(0.0, 1.1))
	scene.add_child(foam_field)

	var flow_field := FLOW_FIELD_SCRIPT.new()
	flow_field.name = "FlowField"
	flow_field.set("resolution", 128)
	flow_field.set("world_size_m", 28.0)
	flow_field.set("origin_xz", Vector2(0.0, 2.6))
	flow_field.set("auto_bake_from_path", false)
	flow_field.set("uniform_speed_mps", 0.0)
	scene.add_child(flow_field)

	var bubbles := BurstSpy.new()
	bubbles.name = "BubbleTarget"
	scene.add_child(bubbles)

	var spray := BurstSpy.new()
	spray.name = "SprayTarget"
	scene.add_child(spray)

	var waterfall := WATERFALL_SCRIPT.new()
	waterfall.name = "Waterfall"
	waterfall.set("lip_points", PackedVector3Array([
		Vector3(-2.0, 0.0, 0.0),
		Vector3(0.0, 0.22, 0.0),
		Vector3(2.0, 0.0, 0.0),
	]))
	waterfall.set("drop_height_m", 5.0)
	waterfall.set("downstream_offset_m", 2.0)
	waterfall.set("fall_direction_xz", Vector2(0.0, 1.0))
	waterfall.set("shelf_points", PackedVector3Array([
		Vector3(-0.85, -2.35, 0.86),
		Vector3(0.85, -2.25, 0.92),
	]))
	waterfall.set("vertical_segments", 10)
	waterfall.set("stamp_samples", 5)
	waterfall.set("lip_foam_radius_m", 0.55)
	waterfall.set("plunge_foam_radius_m", 1.25)
	waterfall.set("lip_foam_intensity", 0.50)
	waterfall.set("plunge_foam_intensity", 0.94)
	waterfall.set("downstream_velocity_mps", 3.6)
	waterfall.set("foam_field", foam_field)
	waterfall.set("flow_field", flow_field)
	waterfall.set("bubble_target", bubbles)
	waterfall.set("spray_target", spray)
	scene.add_child(waterfall)

	await process_frame

	waterfall.call("rebuild")
	var mesh := waterfall.get("mesh") as ArrayMesh
	var vertex_count := 0
	var index_count := 0
	if mesh != null and mesh.get_surface_count() > 0:
		var arrays := mesh.surface_get_arrays(0)
		var verts := arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array
		var indices := arrays[Mesh.ARRAY_INDEX] as PackedInt32Array
		vertex_count = verts.size()
		index_count = indices.size()

	var material := waterfall.get("material_override") as ShaderMaterial
	var lip_points: PackedVector3Array = waterfall.call("get_lip_world_points")
	var plunge_points: PackedVector3Array = waterfall.call("get_plunge_world_points")
	var lip_center := _average_points(lip_points)
	var plunge_center: Vector3 = waterfall.call("get_plunge_center")
	var downstream_probe := plunge_center + Vector3(0.0, 0.0, 1.55)
	var flow_before: Vector3 = flow_field.call("sample_flow_at", downstream_probe)
	var flow_foam_before: float = flow_field.call("sample_foam_at", downstream_probe)
	var stamp_count: int = waterfall.call("stamp_waterfall_response")
	var lip_foam: float = foam_field.call("sample_foam_at", lip_center)
	var plunge_foam: float = foam_field.call("sample_foam_at", plunge_center)
	var lip_source: int = foam_field.call("sample_source_at", lip_center)
	var plunge_source: int = foam_field.call("sample_source_at", plunge_center)
	var flow_after: Vector3 = flow_field.call("sample_flow_at", downstream_probe)
	var flow_foam_after: float = flow_field.call("sample_foam_at", downstream_probe)
	var flow_turbulence_after: float = flow_field.call("get_turbulence_at", downstream_probe)
	var waterfall_spray_fx := waterfall.get_node_or_null("WaterfallSprayFx")
	var spray_sample := waterfall_spray_fx.call("get_debug_sample") as Dictionary if waterfall_spray_fx != null and waterfall_spray_fx.has_method("get_debug_sample") else {}
	var lip_mist := spray_sample.get("lip", {}) as Dictionary
	var shelf_spray := spray_sample.get("shelf", {}) as Dictionary
	var plunge_mist := spray_sample.get("plunge", {}) as Dictionary
	var shelf_position: Array = shelf_spray.get("position", [])
	var plunge_position: Array = plunge_mist.get("position", [])
	var plunge_gravity: Array = plunge_mist.get("gravity", [])
	var expected_vertices := (10 + 1) * 3
	var expected_indices := 10 * (3 - 1) * 6
	var sheet_length: float = waterfall.call("get_sheet_length_m")
	var lip_length: float = waterfall.call("get_lip_length_m")
	var bubble_event := bubbles.events[0] if not bubbles.events.is_empty() else {}
	var spray_event := spray.events[0] if not spray.events.is_empty() else {}

	var lod_camera := Camera3D.new()
	lod_camera.name = "LodCamera"
	scene.add_child(lod_camera)
	waterfall.set("target_camera", lod_camera)
	waterfall.set("lod_enabled", true)
	waterfall.set("far_lod_distance_m", 8.0)
	waterfall.set("max_visible_distance_m", 12.0)
	waterfall.set("far_vertical_segments", 3)
	waterfall.set("disable_spray_in_far_lod", true)

	lod_camera.global_position = waterfall.global_position + Vector3(0.0, 0.0, 3.0)
	var near_lod: String = waterfall.call("update_lod")
	var near_lod_vertices := _mesh_vertex_count(waterfall.get("mesh") as ArrayMesh)
	var near_visible := waterfall.visible

	lod_camera.global_position = waterfall.global_position + Vector3(0.0, 0.0, 9.0)
	var far_lod: String = waterfall.call("update_lod")
	var far_lod_vertices := _mesh_vertex_count(waterfall.get("mesh") as ArrayMesh)
	var far_visible := waterfall.visible
	var far_spray_enabled := bool(waterfall_spray_fx.get("enabled")) if waterfall_spray_fx != null and _has_property(waterfall_spray_fx, "enabled") else true

	lod_camera.global_position = waterfall.global_position + Vector3(0.0, 0.0, 14.0)
	var culled_lod: String = waterfall.call("update_lod")
	var culled_visible := waterfall.visible

	var checks := {
		"mesh_created": mesh != null and mesh.get_surface_count() == 1,
		"vertex_count_matches_grid": vertex_count == expected_vertices,
		"index_count_matches_grid": index_count == expected_indices,
		"material_uses_sheet_shader": material != null and material.shader != null and material.shader.resource_path.ends_with("fast_waterfall_sheet.gdshader"),
		"sheet_length_uses_drop_and_offset": absf(sheet_length - sqrt(5.0 * 5.0 + 2.0 * 2.0)) < 0.05,
		"lip_length_positive": lip_length > 3.8,
		"plunge_points_below_lip": plunge_center.y < lip_center.y - 4.7,
		"plunge_points_downstream": plunge_center.z > lip_center.z + 1.8,
		"foam_stamps_expected_count": stamp_count == 10,
		"lip_foam_stamped": lip_foam > 0.18,
		"plunge_foam_stamped": plunge_foam > 0.40,
		"lip_source_kind": lip_source == FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP,
		"plunge_source_kind": plunge_source == FOAM_FIELD_SCRIPT.SourceKind.PLUNGE_POOL,
		"flow_field_starts_calm": flow_before.length() < 0.05 and flow_foam_before < 0.05,
		"flow_field_downstream_current": flow_after.z > 1.0,
		"flow_field_downstream_foam": flow_foam_after > 0.35,
		"flow_field_turbulence_query": flow_turbulence_after > 0.35,
		"bubble_target_called": bubbles.events.size() == 1 and float(bubble_event.get("radius", 0.0)) >= 1.20,
		"spray_target_called": spray.events.size() == 1 and float(spray_event.get("strength", 0.0)) > 0.75,
		"waterfall_spray_fx_created": waterfall_spray_fx != null,
		"lip_mist_emits": bool(lip_mist.get("emitting", false)) and float(lip_mist.get("amount_ratio", 0.0)) > 0.35,
		"shelf_spray_emits": bool(shelf_spray.get("emitting", false)) and int(spray_sample.get("shelf_point_count", 0)) == 2 and float(shelf_spray.get("sphere_radius", 0.0)) > 0.40,
		"plunge_mist_emits": bool(plunge_mist.get("emitting", false)) and float(plunge_mist.get("amount_ratio", 0.0)) > 0.65,
		"shelf_spray_between_lip_and_plunge": shelf_position.size() == 3 and float(shelf_position[1]) < lip_center.y - 2.0 and float(shelf_position[1]) > plunge_center.y + 2.0,
		"plunge_mist_uses_flow_drift": plunge_gravity.size() == 3 and float(plunge_gravity[2]) > 0.10,
		"near_lod_keeps_full_mesh": near_lod == "near" and near_visible and near_lod_vertices == expected_vertices,
		"far_lod_reduces_mesh": far_lod == "far" and far_visible and far_lod_vertices == (3 + 1) * 3,
		"far_lod_disables_spray": not far_spray_enabled,
		"culled_lod_hides_waterfall": culled_lod == "culled" and not culled_visible,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"vertex_count": vertex_count,
			"index_count": index_count,
			"sheet_length": sheet_length,
			"lip_length": lip_length,
			"stamp_count": stamp_count,
			"lip_foam": lip_foam,
			"plunge_foam": plunge_foam,
			"lip_source": lip_source,
			"plunge_source": plunge_source,
			"flow_before": [flow_before.x, flow_before.y, flow_before.z],
			"flow_after": [flow_after.x, flow_after.y, flow_after.z],
			"flow_foam_before": flow_foam_before,
			"flow_foam_after": flow_foam_after,
			"flow_turbulence_after": flow_turbulence_after,
			"bubble_event": bubble_event,
			"spray_event": spray_event,
			"waterfall_spray_fx": spray_sample,
			"lod": {
				"near": {"state": near_lod, "visible": near_visible, "vertices": near_lod_vertices},
				"far": {"state": far_lod, "visible": far_visible, "vertices": far_lod_vertices, "spray_enabled": far_spray_enabled},
				"culled": {"state": culled_lod, "visible": culled_visible},
			},
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater waterfall contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater waterfall contract check saved: " + global_path)
	quit(0 if passed else 1)


func _average_points(points: PackedVector3Array) -> Vector3:
	if points.is_empty():
		return Vector3.ZERO
	var total := Vector3.ZERO
	for p in points:
		total += p
	return total / float(points.size())


func _mesh_vertex_count(mesh: ArrayMesh) -> int:
	if mesh == null or mesh.get_surface_count() <= 0:
		return 0
	var arrays := mesh.surface_get_arrays(0)
	var verts := arrays[Mesh.ARRAY_VERTEX] as PackedVector3Array
	return verts.size()


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
