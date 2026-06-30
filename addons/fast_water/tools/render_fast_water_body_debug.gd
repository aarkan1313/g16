extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const BODY_DEBUG_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_debug_overlay.gd")
const SKY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_sky.gd")


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_body_debug_overlay.png"
	var frames := 120
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--frames="):
			frames = maxi(1, int(arg.trim_prefix("--frames=")))

	var scene := Node3D.new()
	scene.name = "FastWaterBodyDebugRender"
	root.add_child(scene)

	_add_sky(scene)
	var camera := _add_camera(scene)
	_add_light(scene)
	_add_ground(scene)
	_add_lake(scene, camera)
	_add_river(scene)
	_add_overlay(scene)

	print("FastWater body debug render gate loaded scene")
	_capture.call_deferred(screenshot_path, frames)


func _capture(screenshot_path: String, frames: int) -> void:
	for _i in range(frames):
		await process_frame

	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater body debug render gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)

	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater body debug render gate failed to save " + global_path)
		quit(1)
		return
	print("FastWater body debug render gate screenshot saved: " + global_path)
	quit(0)


func _add_sky(scene: Node3D) -> void:
	var sky := SKY_SCRIPT.new()
	sky.name = "DebugSky"
	sky.set("horizon_color", Color(0.58, 0.72, 0.80))
	sky.set("zenith_color", Color(0.13, 0.28, 0.46))
	sky.set("cloud_amount", 0.18)
	scene.add_child(sky)


func _add_camera(scene: Node3D) -> Camera3D:
	var camera := Camera3D.new()
	camera.name = "Camera"
	camera.fov = 53.0
	scene.add_child(camera)
	camera.look_at_from_position(Vector3(10.5, 8.5, 15.0), Vector3(0.0, 0.0, 0.0), Vector3.UP)
	camera.current = true
	return camera


func _add_light(scene: Node3D) -> void:
	var sun := DirectionalLight3D.new()
	sun.name = "DebugSun"
	sun.light_energy = 2.1
	sun.rotation_degrees = Vector3(-46.0, 138.0, 0.0)
	scene.add_child(sun)


func _add_ground(scene: Node3D) -> void:
	var ground := MeshInstance3D.new()
	ground.name = "DebugGround"
	var plane := PlaneMesh.new()
	plane.size = Vector2(34.0, 24.0)
	ground.mesh = plane
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.18, 0.27, 0.22)
	mat.roughness = 0.85
	ground.material_override = mat
	ground.position = Vector3(0.0, -0.08, 0.0)
	scene.add_child(ground)


func _add_lake(scene: Node3D, camera: Camera3D) -> void:
	var lake := SURFACE_SCRIPT.new()
	lake.name = "DebugLake"
	lake.set("target_camera", camera)
	lake.set("follow_camera", false)
	lake.set("mesh_size_m", 18.0)
	lake.set("mesh_subdivisions", 48)
	lake.set("body_profile", BODY_PROFILE_SCRIPT.lake())
	lake.position = Vector3(-5.5, 0.0, 1.0)
	scene.add_child(lake)


func _add_river(scene: Node3D) -> void:
	var river_profile := BODY_PROFILE_SCRIPT.river()
	river_profile.set("query_priority", 25)
	var river := PATH_SCRIPT.new()
	river.name = "DebugRiver"
	river.set("body_profile", river_profile)
	river.set("width_m", 2.8)
	river.set("flow_speed_mps", 2.2)
	river.set("flow_field_resolution", 128)
	river.set("control_points", PackedVector3Array([
		Vector3(-11.0, 0.40, -3.8),
		Vector3(-5.8, 0.20, -1.5),
		Vector3(-1.0, 0.04, 0.4),
		Vector3(4.8, -0.14, -1.0),
		Vector3(10.5, -0.32, 1.6),
	]))
	scene.add_child(river)


func _add_overlay(scene: Node3D) -> void:
	var overlay := BODY_DEBUG_SCRIPT.new()
	overlay.name = "FastWaterBodyDebugOverlay"
	overlay.set("flow_samples_per_body", 12)
	overlay.set("flow_vector_scale", 0.75)
	overlay.set("vertical_offset_m", 0.14)
	overlay.set("probe_points", PackedVector3Array([
		Vector3(-5.5, 1.1, 1.0),
		Vector3(-5.5, -0.8, 1.0),
		Vector3(-0.5, 1.0, 0.1),
		Vector3(-0.5, -0.65, 0.1),
		Vector3(5.0, 0.9, -0.7),
	]))
	scene.add_child(overlay)
