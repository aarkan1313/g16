extends SceneTree

const DEMO_SCENE := "res://addons/fast_water/demo/fast_water_demo.tscn"

var _out_dir := "res://artifacts/fast_water_surface_artifact_diagnostic"
var _mode := "surface"
var _camera_mode := "topdown"
var _frames := 150
var _list_meshes := false
var _debug_views: Array[int] = [0, 1, 2, 3, 5, 6, 7]
var _cases: Array[String] = ["baseline", "no_planar", "no_wake", "no_flow", "no_foam", "no_caustics", "no_hero_ripples"]


func _initialize() -> void:
	root.size = Vector2i(1280, 720)
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--out-dir="):
			_out_dir = arg.trim_prefix("--out-dir=")
		elif arg.begins_with("--mode="):
			_mode = arg.trim_prefix("--mode=")
		elif arg.begins_with("--camera="):
			_camera_mode = arg.trim_prefix("--camera=")
		elif arg.begins_with("--frames="):
			_frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--debug-views="):
			_debug_views = _parse_int_list(arg.trim_prefix("--debug-views="))
		elif arg.begins_with("--cases="):
			_cases = _parse_string_list(arg.trim_prefix("--cases="))
		elif arg == "--list-meshes":
			_list_meshes = true

	_run.call_deferred()


func _run() -> void:
	var packed := load(DEMO_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater artifact diagnostic could not load " + DEMO_SCENE)
		quit(1)
		return

	print("FastWater artifact diagnostic: mode=%s camera=%s frames=%d" % [_mode, _camera_mode, _frames])
	for case_name in _cases:
		var demo := packed.instantiate()
		if _has_property(demo, "default_mode"):
			demo.set("default_mode", _mode)
		root.add_child(demo)

		for _i in range(max(8, int(_frames * 0.35))):
			await process_frame

		var water := root.find_child("FastWaterSurface", true, false)
		var camera := demo.get_node_or_null("Camera") as Camera3D
		if water == null or camera == null:
			push_error("FastWater artifact diagnostic could not find water/camera for case " + case_name)
			_cleanup_demo(demo)
			quit(1)
			return

		if _camera_mode != "default":
			demo.set_process(false)
		_apply_case(case_name, water)
		for _i in range(max(8, int(_frames * 0.20))):
			_configure_camera(camera, demo)
			await process_frame

		if _list_meshes:
			_print_meshes(case_name)

		for debug_view in _debug_views:
			_set_debug_view(water, debug_view)
			for _i in range(4):
				_configure_camera(camera, demo)
				await process_frame
			var path := _join_res_path(_out_dir, "%s_debug_%d.png" % [case_name, debug_view])
			if not _save_root_image(path):
				_cleanup_demo(demo)
				quit(1)
				return
			print("FastWater artifact diagnostic saved: " + ProjectSettings.globalize_path(path))

		_cleanup_demo(demo)
		await process_frame

	quit(0)


func _configure_camera(camera: Camera3D, demo: Node) -> void:
	if _camera_mode == "default":
		return

	var focus := Vector3(0.0, 0.0, 2.3)
	var marker := demo.get_node_or_null("WakeMarker0") as Node3D
	if marker != null:
		focus = marker.global_position
		focus.y = 0.02

	camera.current = true
	camera.fov = 42.0
	if _camera_mode == "oblique":
		camera.look_at_from_position(focus + Vector3(0.35, 5.2, 5.0), focus, Vector3.UP)
	else:
		camera.look_at_from_position(focus + Vector3(0.02, 8.2, 0.02), focus, Vector3.FORWARD)


func _apply_case(case_name: String, water: Node) -> void:
	match case_name:
		"baseline":
			pass
		"no_planar":
			var reflection := root.find_child("PlanarReflection", true, false)
			if reflection != null:
				_set_if_has(reflection, "enabled", false)
				_set_if_has(reflection, "reflection_strength", 0.0)
				reflection.set_process(false)
			_set_material_param(water, "planar_reflection_enabled", false)
			_set_material_param(water, "planar_reflection_strength", 0.0)
		"loose_reflection_near":
			var reflection := root.find_child("PlanarReflection", true, false)
			if reflection != null:
				_set_if_has(reflection, "water_plane_bias_m", -16.0)
		"no_interactor_reflection":
			for node in root.find_children("WakeMarker*", "MeshInstance3D", true, false):
				(node as MeshInstance3D).layers = 8
		"no_wake":
			_set_if_has(water, "wake_map_strength", 0.0)
			_set_if_has(water, "wake_foam_strength", 0.0)
			_set_if_has(water, "wake_highlight_strength", 0.0)
			_set_if_has(water, "route_wakes_to_wake_map", false)
			_set_if_has(water, "auto_create_wake_map", false)
			_set_if_has(water, "use_gpu_wake_map", false)
			var wake := water.get("wake_map_node") if _has_property(water, "wake_map_node") else null
			if wake is Node:
				water.set("wake_map_node", null)
				(wake as Node).queue_free()
		"no_flow":
			_set_if_has(water, "flow_field_enabled", false)
			_set_if_has(water, "flow_field_strength", 0.0)
			_set_if_has(water, "flow_foam_strength", 0.0)
			_set_if_has(water, "flow_normal_strength", 0.0)
			_set_material_param(water, "flow_field_enabled", false)
		"no_foam":
			_set_if_has(water, "foam_field_enabled", false)
			_set_if_has(water, "foam_field_strength", 0.0)
			_set_if_has(water, "foam_intensity", 0.0)
			_set_if_has(water, "shoreline_foam_strength", 0.0)
			_set_if_has(water, "wake_foam_strength", 0.0)
			_set_material_param(water, "foam_field_enabled", false)
		"no_caustics":
			var caustics := root.find_child("Caustics", true, false)
			if caustics != null:
				_set_if_has(caustics, "intensity", 0.0)
				if caustics is Node3D:
					(caustics as Node3D).visible = false
		"no_floor":
			var floor := root.find_child("DemoPoolFloor", true, false)
			if floor is Node3D:
				(floor as Node3D).visible = false
		"no_static_props":
			for node in root.find_children("*", "MeshInstance3D", true, false):
				var mesh_node := node as MeshInstance3D
				if mesh_node.layers & 1 != 0:
					mesh_node.visible = false
		"no_markers":
			for node in root.find_children("WakeMarker*", "MeshInstance3D", true, false):
				(node as MeshInstance3D).visible = false
		"no_shadows":
			for node in root.find_children("*", "GeometryInstance3D", true, false):
				(node as GeometryInstance3D).cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		"no_hero_ripples":
			_set_if_has(water, "interactions_enabled", false)
			_set_if_has(water, "max_hero_ripples", 0)
			_set_material_param(water, "hero_ripple_count", 0)
		_:
			push_warning("FastWater artifact diagnostic: unknown case " + case_name)


func _set_debug_view(water: Node, debug_view: int) -> void:
	_set_if_has(water, "debug_view", clampi(debug_view, 0, 7))
	_set_material_param(water, "debug_view", clampi(debug_view, 0, 7))


func _set_material_param(water: Node, param_name: String, value: Variant) -> void:
	var material := water.get("water_material") if _has_property(water, "water_material") else null
	if material is ShaderMaterial:
		(material as ShaderMaterial).set_shader_parameter(param_name, value)


func _print_meshes(case_name: String) -> void:
	print("--- MeshInstance3D nodes for case %s ---" % case_name)
	for node in root.find_children("*", "MeshInstance3D", true, false):
		var mesh_node := node as MeshInstance3D
		var material := mesh_node.material_override
		var mesh_name := "<null>"
		if mesh_node.mesh != null:
			mesh_name = mesh_node.mesh.get_class()
		var material_name := "<null>"
		if material != null:
			material_name = material.get_class()
		print("%s visible=%s layers=%d pos=%s mesh=%s material=%s" % [
			mesh_node.get_path(),
			str(mesh_node.visible),
			mesh_node.layers,
			str(mesh_node.global_position),
			mesh_name,
			material_name,
		])


func _save_root_image(path: String) -> bool:
	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater artifact diagnostic could not read root viewport")
		return false
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater artifact diagnostic failed to save %s err=%d" % [global_path, err])
		return false
	return true


func _cleanup_demo(demo: Node) -> void:
	root.remove_child(demo)
	demo.queue_free()


func _join_res_path(dir: String, file_name: String) -> String:
	return dir.path_join(file_name)


func _parse_int_list(value: String) -> Array[int]:
	var result: Array[int] = []
	for part in value.split(",", false):
		result.append(clampi(int(part.strip_edges()), 0, 7))
	return result


func _parse_string_list(value: String) -> Array[String]:
	var result: Array[String] = []
	for part in value.split(",", false):
		var trimmed := part.strip_edges()
		if trimmed != "":
			result.append(trimmed)
	return result


func _set_if_has(object: Object, property_name: String, value: Variant) -> void:
	if object == null:
		return
	if _has_property(object, property_name):
		object.set(property_name, value)


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
