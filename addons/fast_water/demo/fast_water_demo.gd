extends Node3D

const WATER_SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const WATER_SKY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_sky.gd")
const CAUSTICS_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_caustics.gd")
const WAKE_RIBBON_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_wake_ribbon.gd")
const BOW_WAKE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_bow_wake.gd")
const PLANAR_REFLECTION_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_planar_reflection.gd")
const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")
const WATER_PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const WATERFALL_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_waterfall.gd")
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const FLOW_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_flow_field.gd")
const SPLASH_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_splash_fx.gd")
const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const UNDERWATER_SCRIPT := preload("res://addons/fast_water/scripts/fast_underwater_controller.gd")
const WEATHER_ADAPTER_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_adapter.gd")
const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")
const RAIN_IMPACT_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_rain_impact_fx.gd")
const WEATHER_SEQUENCE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_sequence.gd")
const UNDERWATER_SHADER := "res://addons/fast_water/shaders/fast_underwater_overlay.gdshader"
const POOL_FLOOR_SHADER := "res://addons/fast_water/shaders/fast_demo_pool_floor.gdshader"
const LAYER_PROPS := 1
const LAYER_WATER := 2
const LAYER_FLOOR := 4
const LAYER_INTERACTORS := 8

var _water: Node
var _camera: Camera3D
var _markers: Array[MeshInstance3D] = []
var _last_positions: Array[Vector3] = []
var _smoothed_velocities: Array[Vector3] = []
var _screenshot_path := ""
var _capture_frames := 150
var _done_capture := false
var _splash_timer := 0.0
var _ripple_accum := 0.0
@export_enum("surface", "rain", "weather_sequence", "underwater", "underwater_turbid", "waterfall", "ocean", "full_proof") var default_mode := "surface"
var _mode := "surface"
var _sim_time := 0.0
var gate_external_capture := false

# Free-fly review camera. Stays dormant (scripted presentation camera runs) until
# the user touches WASD / right-mouse, so headless render gates are unaffected.
var _free_fly := false
var _look_active := false
var _cam_yaw := 0.0
var _cam_pitch := 0.0
var _move_speed := 9.0
var _look_sensitivity := 0.0032
var _fps_label: Label


func _ready() -> void:
	_mode = default_mode
	_parse_args()
	_setup_scene()
	if _screenshot_path != "" and not gate_external_capture:
		_capture_after_frames.call_deferred()
	elif not gate_external_capture:
		# Genuine live review (no headless gate capture): render 3D at a reduced
		# scale and FSR-upscale. The water fragment shader is the bottleneck, so
		# this is a large framerate win for a small sharpness cost. Gate renders
		# (gate_external_capture == true) keep full native resolution.
		print("FastWater review controls: right-mouse to look, WASD to move, Q/E down/up, Shift = faster.")
		var vp := get_viewport()
		if vp != null:
			# Native resolution for live review. FSR at 0.7 smears the thin surface
			# detail (sun glint, micro-normal shimmer, ripple highlights) into a soft
			# milky veil that reads like a missing/broken shader pass. Drop the scale
			# below 1.0 here only if a weaker GPU needs the framerate.
			vp.scaling_3d_mode = Viewport.SCALING_3D_MODE_BILINEAR
			vp.scaling_3d_scale = 1.0
		var hud := CanvasLayer.new()
		hud.name = "ReviewHud"
		add_child(hud)
		_fps_label = Label.new()
		_fps_label.position = Vector2(14.0, 10.0)
		_fps_label.add_theme_color_override("font_color", Color(1.0, 1.0, 1.0))
		_fps_label.add_theme_color_override("font_outline_color", Color(0.0, 0.0, 0.0))
		_fps_label.add_theme_constant_override("outline_size", 6)
		hud.add_child(_fps_label)


func _process(delta: float) -> void:
	_sim_time += delta
	if _free_fly:
		_update_free_fly(delta)
	else:
		_animate_camera(delta)
	_animate_interactors(delta)
	if _fps_label != null:
		_fps_label.text = "FPS %d   (right-mouse look, WASD move, Q/E up-down, Shift fast)" % Engine.get_frames_per_second()


func _unhandled_input(event: InputEvent) -> void:
	if _camera == null:
		return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT:
		if event.pressed:
			_begin_free_fly()
			_look_active = true
			Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
		else:
			_look_active = false
			Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	elif event is InputEventMouseMotion and _look_active:
		_cam_yaw -= event.relative.x * _look_sensitivity
		_cam_pitch = clamp(_cam_pitch - event.relative.y * _look_sensitivity, -1.5, 1.5)
	elif event is InputEventKey and event.pressed and not event.echo:
		if event.physical_keycode in [KEY_W, KEY_A, KEY_S, KEY_D, KEY_Q, KEY_E]:
			_begin_free_fly()


func _begin_free_fly() -> void:
	if _free_fly:
		return
	_free_fly = true
	var e := _camera.global_transform.basis.get_euler()
	_cam_yaw = e.y
	_cam_pitch = e.x


func _update_free_fly(delta: float) -> void:
	var basis := Basis.from_euler(Vector3(_cam_pitch, _cam_yaw, 0.0))
	_camera.global_transform.basis = basis
	var dir := Vector3.ZERO
	if Input.is_physical_key_pressed(KEY_W):
		dir -= basis.z
	if Input.is_physical_key_pressed(KEY_S):
		dir += basis.z
	if Input.is_physical_key_pressed(KEY_A):
		dir -= basis.x
	if Input.is_physical_key_pressed(KEY_D):
		dir += basis.x
	if Input.is_physical_key_pressed(KEY_E):
		dir += Vector3.UP
	if Input.is_physical_key_pressed(KEY_Q):
		dir -= Vector3.UP
	if dir != Vector3.ZERO:
		var speed := _move_speed
		if Input.is_physical_key_pressed(KEY_SHIFT):
			speed *= 4.0
		_camera.global_position += dir.normalized() * speed * delta


func _parse_args() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			_screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--frames="):
			_capture_frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--mode="):
			_mode = arg.trim_prefix("--mode=")


# True only for genuine interactive review (no headless screenshot / gate capture).
# Lets the live scene drop offstage proof-only weight while the contract and render
# gates (which set gate_external_capture) still build the full scene.
func _is_live_review() -> bool:
	return _screenshot_path == "" and not gate_external_capture


func _setup_scene() -> void:
	var sky := WATER_SKY_SCRIPT.new()
	sky.name = "ProceduralSky"
	sky.set("horizon_color", Color(0.62, 0.78, 0.88))
	sky.set("zenith_color", Color(0.20, 0.42, 0.68))
	sky.set("cloud_amount", 0.18)
	add_child(sky)

	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.light_energy = 2.20
	sun.rotation_degrees = Vector3(-31.0, 122.0, 0.0)
	add_child(sun)

	_camera = Camera3D.new()
	_camera.name = "Camera"
	_camera.fov = 63.0
	if _mode == "ocean":
		_camera.position = Vector3(0.0, 2.65, 14.0)
	elif _mode == "full_proof":
		_camera.position = Vector3(0.0, 1.35, 8.4)
	elif _mode == "waterfall":
		_camera.position = Vector3(0.0, 2.45, 9.2)
	elif _is_underwater_mode():
		_camera.position = Vector3(0.0, -0.58, 8.4)
	else:
		_camera.position = Vector3(0.0, 1.18, 8.0)
	add_child(_camera)
	if _mode == "ocean":
		_camera.look_at(Vector3(0.2, 0.02, -9.5), Vector3.UP)
	elif _mode == "full_proof":
		_camera.look_at(Vector3(-0.65, 0.05, -0.8), Vector3.UP)
	elif _mode == "waterfall":
		_camera.look_at(Vector3(0.0, -0.55, -3.8), Vector3.UP)
	elif _is_underwater_mode():
		_camera.look_at(Vector3(0.0, -0.45, -1.5), Vector3.UP)
	else:
		_camera.look_at(Vector3(0.15, 0.08, -2.5), Vector3.UP)
	_camera.current = true

	_add_ground()

	if _mode == "ocean":
		_add_ocean_demo()
	else:
		_water = WATER_SURFACE_SCRIPT.new()
		_water.name = "FastWaterSurface"
		_water.set("target_camera", _camera)
		_water.set("follow_camera", false)
		_water.set("visual_profile", VISUAL_PROFILE_SCRIPT.hero_quality())
		_water.set("mesh_size_m", 8000.0 if _mode == "full_proof" else 96.0)
		_water.set("mesh_subdivisions", 192 if _mode == "full_proof" else 176)
		_water.set("max_hero_ripples", 16)
		_water.set("wake_map_resolution", 384)
		_water.set("wake_map_world_size_m", 36.0)
		_water.set("wake_map_update_hz", 36.0)
		_water.set("wake_strength_scale", 0.26)
		_water.set("splash_strength_scale", 0.32)
		_water.set("splash_pool_size", 0)
		_water.set("auto_create_bubbles", false)
		_water.set("visual_layers", LAYER_WATER)
		add_child(_water)

	_add_planar_reflection()
	var caustics := CAUSTICS_SCRIPT.new()
	caustics.name = "Caustics"
	caustics.set("target_camera", _camera)
	caustics.set("follow_camera", _mode == "ocean")
	caustics.set("depth_below_water_m", -0.90)
	var weather_mode := _mode == "rain" or _mode == "weather_sequence" or _mode == "underwater_turbid" or _mode == "full_proof"
	var caustic_intensity := 0.68 if _is_underwater_mode() else (0.0 if _mode == "full_proof" else (0.06 if weather_mode else 0.08))
	if _mode == "underwater_turbid":
		caustic_intensity = 0.24
	caustics.set("intensity", caustic_intensity)
	caustics.set("caustic_scale", 11.0 if _mode == "full_proof" else 7.0)
	caustics.set("speed", 0.36 if _mode == "full_proof" else 0.72)
	caustics.set("distance_fade", 0.90 if _mode == "full_proof" else 0.78)
	caustics.set("size_m", 240.0 if _mode == "full_proof" else 96.0)
	caustics.layers = LAYER_FLOOR
	add_child(caustics)

	if _mode == "waterfall" or _mode == "full_proof":
		_add_waterfall_demo()
	if _mode == "full_proof":
		_add_inlet_river()
		# Ocean preview is parked 4000m offstage purely for the contract; it draws
		# two extra LOD water meshes plus per-frame updates. Skip it for live review.
		if not _is_live_review():
			_add_ocean_preview()
	var underwater_controller := _add_underwater_overlay()
	if _mode != "waterfall":
		_add_markers()
	# Full-proof weather starts calm and is proof-only; skip it for live review to
	# shed the rain FX / weather sim cost. Dedicated weather modes keep it.
	if weather_mode and not (_mode == "full_proof" and _is_live_review()):
		var adapter := _add_weather_adapter()
		if _mode == "underwater_turbid":
			_configure_turbid_underwater_weather(adapter, underwater_controller)
		elif _mode == "weather_sequence" or _mode == "full_proof":
			_add_weather_sequence(adapter)


func _add_planar_reflection() -> void:
	if _is_underwater_mode() or _mode == "ocean":
		return
	var reflection := PLANAR_REFLECTION_SCRIPT.new()
	reflection.name = "PlanarReflection"
	reflection.set("target_camera", _camera)
	reflection.set("resolution_scale", 0.50)
	reflection.set("max_resolution", 960)
	reflection.set("reflection_strength", 0.11 if _mode == "full_proof" else 0.16)
	reflection.set("distortion_strength", 0.0025 if _mode == "full_proof" else 0.004)
	reflection.set("reflection_cull_mask", LAYER_PROPS)
	add_child(reflection)
	reflection.set("water_surface_path", reflection.get_path_to(_water))
	var profile := _water.get("visual_profile") as Resource
	if profile != null and profile.has_method("apply_to"):
		profile.call("apply_to", _water, _water.get("wake_map_node"), reflection)
	_tune_wake_for_review()
	if _mode == "full_proof":
		reflection.set("enabled", false)
		reflection.set("reflection_strength", 0.0)
		if _water != null:
			_water.set("planar_reflection_strength", 0.0)


func _add_ground() -> void:
	var ground := MeshInstance3D.new()
	ground.name = "DemoPoolFloor"
	if _mode == "full_proof":
		var plane := PlaneMesh.new()
		plane.size = Vector2(8000.0, 8000.0)
		plane.subdivide_width = 32
		plane.subdivide_depth = 32
		ground.mesh = plane
		ground.position.y = -1.45
	else:
		ground.mesh = _make_ground_mesh(72.0, 56)
	ground.material_override = _make_ground_material()
	ground.layers = LAYER_FLOOR
	add_child(ground)

	_add_pool_test_props()


func _make_ground_mesh(size_m: float, cells: int) -> Mesh:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)

	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()
	var half := size_m * 0.5

	for z in range(cells + 1):
		for x in range(cells + 1):
			var u := float(x) / float(cells)
			var v := float(z) / float(cells)
			var px := lerp(-half, half, u)
			var pz := lerp(-half, half, v)
			var y := _basin_height(px, pz, half)
			verts.append(Vector3(px, y, pz))
			normals.append(_basin_normal(px, pz, half, size_m / float(cells)))
			uvs.append(Vector2(u, v))

	for z in range(cells):
		for x in range(cells):
			var a := z * (cells + 1) + x
			var b := a + 1
			var c := a + cells + 1
			var d := c + 1
			indices.append_array(PackedInt32Array([a, c, b, b, c, d]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices

	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _basin_height(px: float, pz: float, half: float) -> float:
	var safe_half: float = half if half > 0.001 else 0.001
	var x: float = px / safe_half
	var z: float = pz / safe_half
	var front_shelf: float = _smooth_range(-0.12, 0.76, z)
	var side_shelf: float = _smooth_range(0.66, 0.98, abs(x))
	var far_channel: float = _smooth_range(-0.10, -0.90, z)
	var y: float = lerp(-1.24, -0.56, front_shelf)
	y = lerp(y, -0.64, side_shelf * 0.58)
	y -= far_channel * 0.24
	y += sin(px * 0.16 + pz * 0.12) * 0.030
	y += sin(px * -0.08 + pz * 0.19) * 0.020
	return y


func _basin_normal(px: float, pz: float, half: float, step: float) -> Vector3:
	var e := max(step, 0.01)
	var hx0 := _basin_height(px - e, pz, half)
	var hx1 := _basin_height(px + e, pz, half)
	var hz0 := _basin_height(px, pz - e, half)
	var hz1 := _basin_height(px, pz + e, half)
	return Vector3(hx0 - hx1, e * 2.0, hz0 - hz1).normalized()


func _smooth_range(edge0: float, edge1: float, value: float) -> float:
	var span := edge1 - edge0
	if abs(span) <= 0.0001:
		return 0.0
	var t := clamp((value - edge0) / span, 0.0, 1.0)
	return t * t * (3.0 - 2.0 * t)


func _make_ground_material() -> Material:
	var mat := ShaderMaterial.new()
	var shader := load(POOL_FLOOR_SHADER) as Shader
	if shader != null:
		mat.shader = shader
		mat.set_shader_parameter("tile_count", 2.0 if _mode == "full_proof" else 8.0)
		mat.set_shader_parameter("line_visibility", 0.0)
	return mat


func _add_pool_test_props() -> void:
	if _mode == "ocean":
		_add_ocean_test_props()
		return
	if _mode == "full_proof":
		return

	# Visual reference geometry only. The rectangular curbs/rails/red blocks that
	# used to sit at the waterline read as "weird geometric shapes in the water",
	# so the pool test scene now matches the hero scene principle: clean vertical
	# columns plus the floating wake markers, and nothing box-shaped on the surface.
	if _mode == "waterfall":
		return

	var pillar := MeshInstance3D.new()
	pillar.name = "PalePillar"
	var cyl := CylinderMesh.new()
	cyl.top_radius = 0.34
	cyl.bottom_radius = 0.34
	cyl.height = 5.6
	cyl.radial_segments = 24
	pillar.mesh = cyl
	pillar.position = Vector3(0.15, 2.55, -2.25)
	pillar.rotation_degrees = Vector3.ZERO
	pillar.material_override = _simple_mat(Color(0.82, 0.72, 0.58), 0.48)
	pillar.layers = LAYER_PROPS
	add_child(pillar)


func _add_ocean_demo() -> void:
	var profile := OCEAN_PROFILE_SCRIPT.open_world()
	profile.set("near_mesh_size_m", 190.0)
	profile.set("near_mesh_subdivisions", 128)
	profile.set("far_mesh_size_m", 960.0)
	profile.set("far_mesh_subdivisions", 52)
	profile.set("local_wake_world_size_m", 70.0)
	profile.set("horizon_fade_start_m", 95.0)
	profile.set("horizon_fade_end_m", 420.0)
	profile.set("far_horizon_fade_strength", 0.66)
	profile.set("whitecap_strength", 0.24)
	profile.set("distant_reflection_strength", 0.28)

	_water = OCEAN_SCRIPT.new()
	_water.name = "FastWaterOcean"
	_water.set("target_camera", _camera)
	_water.set("ocean_profile", profile)
	_water.set("visual_layers", LAYER_WATER)
	add_child(_water)


func _add_ocean_test_props() -> void:
	_add_box("WetMarkerPost", Vector3(2.8, 0.38, 8.4), Vector3(0.24, 1.2, 0.24), _simple_mat(Color(0.72, 0.52, 0.34), 0.62))


func _add_box(name: String, pos: Vector3, size: Vector3, mat: Material) -> void:
	var node := MeshInstance3D.new()
	node.name = name
	var mesh := BoxMesh.new()
	mesh.size = size
	node.mesh = mesh
	node.position = pos
	node.material_override = mat
	node.layers = LAYER_PROPS
	add_child(node)


func _simple_mat(color: Color, roughness: float) -> Material:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.roughness = roughness
	return mat


func _add_underwater_overlay() -> Node:
	var layer := CanvasLayer.new()
	layer.name = "UnderwaterLayer"
	add_child(layer)

	var rect := ColorRect.new()
	rect.name = "UnderwaterOverlay"
	rect.visible = false
	rect.set_anchors_preset(Control.PRESET_FULL_RECT)
	rect.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var mat := ShaderMaterial.new()
	var shader := load(UNDERWATER_SHADER) as Shader
	if shader != null:
		mat.shader = shader
	rect.material = mat
	layer.add_child(rect)

	var controller := UNDERWATER_SCRIPT.new()
	controller.name = "UnderwaterController"
	controller.set("camera", _camera)
	controller.set("water_surface", _water)
	controller.set("underwater_overlay", rect)
	controller.set("overlay_material", mat)
	if _is_underwater_mode():
		controller.set("distortion_strength", 0.022)
		controller.set("tint_strength", 0.50)
		controller.set("underwater_tint", Color(0.025, 0.24, 0.30, 1.0))
		controller.set("surface_haze_strength", 0.24)
		controller.set("light_shaft_strength", 0.24)
		controller.set("caustic_strength", 0.22)
		controller.set("particulate_strength", 0.13)
		controller.set("waterline_strength", 0.28)
		controller.set("waterline_screen_y", 0.50)
	if _mode == "underwater_turbid":
		controller.set("particulate_strength", 0.18)
		controller.set("waterline_strength", 0.18)
	add_child(controller)
	return controller


func _add_waterfall_demo() -> void:
	var proof_mode := _mode == "full_proof"
	if not proof_mode:
		var cliff_mat := _simple_mat(Color(0.19, 0.18, 0.17), 0.82)
		_add_box("WaterfallCliff", Vector3(0.0, 1.05, -7.25), Vector3(7.8, 3.2, 0.64), cliff_mat)
		_add_box("WaterfallShelf", Vector3(0.0, 1.95, -6.74), Vector3(7.2, 0.34, 1.15), _simple_mat(Color(0.26, 0.25, 0.22), 0.78))

	var foam_field := FOAM_FIELD_SCRIPT.new()
	foam_field.name = "WaterfallFoamField"
	foam_field.set("resolution", 192)
	foam_field.set("world_size_m", 24.0)
	foam_field.set("origin_xz", Vector2(0.0, -4.4))
	add_child(foam_field)

	var flow_field := FLOW_FIELD_SCRIPT.new()
	flow_field.name = "FlowField"
	flow_field.set("resolution", 192)
	flow_field.set("world_size_m", 24.0)
	flow_field.set("origin_xz", Vector2(0.0, -4.4))
	flow_field.set("auto_bake_from_path", false)
	flow_field.set("uniform_speed_mps", 0.0)
	add_child(flow_field)
	if _water != null:
		_water.set("foam_field_node", foam_field)
		_water.set("foam_field_enabled", true)
		_water.set("foam_field_strength", 1.15)
		_water.set("flow_field_node", flow_field)
		_water.set("flow_field_enabled", true)
		_water.set("flow_field_strength", 0.70)
		_water.set("flow_foam_strength", 1.20)
		_water.set("flow_normal_strength", 0.85)

	var plume := SPLASH_FX_SCRIPT.new() as Node3D
	plume.name = "WaterfallPlungeFx"
	plume.set("duration_s", 0.72)
	plume.position = Vector3(0.0, 0.06, -4.6)
	add_child(plume)

	var waterfall := WATERFALL_SCRIPT.new()
	waterfall.name = "FastWaterWaterfall"
	waterfall.position = Vector3(-10.5, 1.45, -11.5) if proof_mode else Vector3(0.0, 1.88, -6.25)
	waterfall.set("lip_points", PackedVector3Array([
		Vector3(-2.7, 0.0, 0.0),
		Vector3(-0.9, 0.10, 0.0),
		Vector3(0.9, 0.06, 0.0),
		Vector3(2.7, 0.0, 0.0),
	]))
	waterfall.set("shelf_points", PackedVector3Array([
		Vector3(-1.35, -1.12, 0.70),
		Vector3(0.0, -1.34, 0.78),
		Vector3(1.35, -1.10, 0.70),
	]))
	waterfall.set("drop_height_m", 1.25 if proof_mode else 2.36)
	waterfall.set("downstream_offset_m", 1.10 if proof_mode else 1.48)
	waterfall.set("vertical_segments", 24)
	waterfall.set("stamp_samples", 6)
	waterfall.set("plunge_foam_radius_m", 1.65)
	waterfall.set("lip_foam_radius_m", 0.70)
	waterfall.set("spray_strength", 0.22 if proof_mode else 1.0)
	waterfall.set("foam_field", foam_field)
	waterfall.set("flow_field", flow_field)
	waterfall.set("spray_target", plume)
	waterfall.set("auto_stamp", true)
	waterfall.set("stamp_interval_s", 0.24)
	add_child(waterfall)


func _add_inlet_river() -> void:
	var river := WATER_PATH_SCRIPT.new()
	river.name = "FullProofInletRiver"
	river.set("control_points", PackedVector3Array([
		Vector3(-18.0, 0.10, -12.4),
		Vector3(-11.0, 0.08, -10.2),
		Vector3(-7.2, 0.04, -7.0),
		Vector3(-3.6, 0.02, -4.8),
	]))
	river.set("width_m", 3.4)
	river.set("point_widths_m", PackedFloat32Array([3.1, 3.8, 3.2, 4.4]))
	river.set("point_depths_m", PackedFloat32Array([0.52, 0.70, 0.62, 0.84]))
	river.set("point_flow_speeds_mps", PackedFloat32Array([1.4, 1.9, 2.2, 2.8]))
	river.set("point_bank_foam_strengths", PackedFloat32Array([0.38, 0.58, 0.74, 0.86]))
	river.set("point_turbulence_strengths", PackedFloat32Array([0.18, 0.38, 0.66, 0.92]))
	river.set("subdivisions_per_segment", 14)
	river.set("uv_m_per_tile", 5.8)
	river.set("flow_field_resolution", 192)
	river.set("flow_field_margin_m", 5.5)
	river.set("flow_foam_strength", 0.66)
	river.set("flow_normal_strength", 0.88)
	add_child(river)


func _add_ocean_preview() -> void:
	var profile := OCEAN_PROFILE_SCRIPT.performance()
	profile.set("near_mesh_size_m", 76.0)
	profile.set("near_mesh_subdivisions", 64)
	profile.set("far_mesh_size_m", 210.0)
	profile.set("far_mesh_subdivisions", 28)
	profile.set("local_wake_enabled", false)
	profile.set("whitecap_strength", 0.16)
	profile.set("distant_reflection_strength", 0.12)
	profile.set("horizon_fade_start_m", 48.0)
	profile.set("horizon_fade_end_m", 180.0)

	var focus := Node3D.new()
	focus.name = "FullProofOceanFocus"
	focus.position = Vector3(4000.0, -0.18, 4000.0)
	add_child(focus)

	var ocean := OCEAN_SCRIPT.new()
	ocean.name = "FullProofOceanPreview"
	ocean.position = Vector3(4000.0, -0.18, 4000.0)
	ocean.set("wake_focus", focus)
	ocean.set("ocean_profile", profile)
	ocean.set("visual_layers", LAYER_WATER)
	add_child(ocean)


func _add_weather_adapter() -> Node:
	var rain_fx := _add_rain_impact_fx()
	var proof_mode := _mode == "full_proof"

	var state := ENVIRONMENT_STATE_SCRIPT.new()
	state.set("wind_direction_xz", Vector2(0.78, -0.28))
	state.set("wind_speed_mps", 3.0 if proof_mode else 9.0)
	state.set("gust_strength", 0.10 if proof_mode else 0.55)
	state.set("rain_intensity", 0.0 if proof_mode else 0.82)
	state.set("storm_intensity", 0.0 if proof_mode else 0.42)
	state.set("water_turbidity", 0.08 if proof_mode else 0.26)
	state.set("sun_color", Color(0.72, 0.82, 0.88, 1.0))

	var response := WEATHER_RESPONSE_SCRIPT.new()
	response.set("rain_ripple_rate", 115.0)
	response.set("rain_ripple_strength", 0.070)
	response.set("rain_ripple_radius_m", 0.18)
	response.set("rain_foam_boost", 0.16)
	response.set("storm_foam_boost", 0.38)
	response.set("rain_impact_visibility", 0.92)
	response.set("rain_mist_visibility", 0.58)
	response.set("whitecap_wind_threshold_mps", 5.0)
	response.set("whitecap_wind_strength_per_mps", 0.055)
	response.set("gust_whitecap_boost", 0.24)
	response.set("storm_whitecap_boost", 0.78)
	response.set("whitecap_noise_scale", 0.36)
	response.set("turbidity_underwater_visibility_loss", 0.74)
	response.set("turbidity_underwater_particulate_boost", 0.46)
	response.set("turbidity_underwater_caustic_loss", 0.72)
	response.set("turbidity_underwater_light_loss", 0.42)

	var adapter := WEATHER_ADAPTER_SCRIPT.new()
	adapter.name = "WeatherAdapter"
	adapter.set("target_camera", _camera)
	adapter.set("environment_state", state)
	adapter.set("weather_response", response)
	adapter.set("rain_area_m", 34.0)
	adapter.set("max_rain_stamps_per_tick", 0 if proof_mode else 18)
	add_child(adapter)
	var surface_paths: Array[NodePath] = [adapter.get_path_to(_water)]
	adapter.set("water_surface_paths", surface_paths)
	var rain_fx_paths: Array[NodePath] = [adapter.get_path_to(rain_fx)]
	adapter.set("rain_impact_fx_paths", rain_fx_paths)
	return adapter


func _configure_turbid_underwater_weather(adapter: Node, underwater_controller: Node) -> void:
	var state := adapter.get("environment_state") as Resource
	if state != null:
		state.set("wind_direction_xz", Vector2(0.56, -0.82))
		state.set("wind_speed_mps", 6.5)
		state.set("gust_strength", 0.18)
		state.set("rain_intensity", 0.10)
		state.set("storm_intensity", 0.12)
		state.set("water_turbidity", 0.82)
		state.set("sun_color", Color(0.58, 0.68, 0.64, 1.0))
	var response := adapter.get("weather_response") as Resource
	if response != null:
		response.set("turbidity_underwater_visibility_loss", 0.82)
		response.set("turbidity_underwater_tint_boost", 0.34)
		response.set("turbidity_underwater_distortion_boost", 0.62)
		response.set("turbidity_underwater_particulate_boost", 0.54)
		response.set("turbidity_underwater_vignette_boost", 0.34)
		response.set("turbidity_underwater_caustic_loss", 0.84)
		response.set("turbidity_underwater_light_loss", 0.54)
		response.set("turbid_underwater_tint", Color(0.04, 0.15, 0.11, 1.0))
		response.set("suspended_silt_color", Color(0.20, 0.24, 0.15, 1.0))
	if underwater_controller != null:
		var underwater_paths: Array[NodePath] = [adapter.get_path_to(underwater_controller)]
		adapter.set("underwater_controller_paths", underwater_paths)
		adapter.set("auto_discover_underwater_controllers", false)
	if state != null:
		adapter.call("apply_environment_state", state)


func _add_weather_sequence(adapter: Node) -> void:
	var sequence := WEATHER_SEQUENCE_SCRIPT.new()
	sequence.name = "WeatherSequence"
	sequence.set("adapter", adapter)
	sequence.set("time_scale", 1.0 if _mode == "full_proof" else 5.0)
	sequence.set("loop", true)
	sequence.set("playing", _mode != "full_proof")
	add_child(sequence)


func _is_underwater_mode() -> bool:
	return _mode == "underwater" or _mode == "underwater_turbid"


func _add_rain_impact_fx() -> Node3D:
	var rain_fx := RAIN_IMPACT_FX_SCRIPT.new() as Node3D
	rain_fx.name = "RainImpactFx"
	rain_fx.set("area_size_m", 34.0)
	rain_fx.set("water_y_offset_m", 0.065)
	rain_fx.set("impact_particle_cap", 720)
	rain_fx.set("mist_particle_cap", 260)
	rain_fx.set("mist_height_m", 0.48)
	rain_fx.set("wind_drift_scale", 0.060)
	_water.add_child(rain_fx)
	return rain_fx


func _add_markers() -> void:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.92, 0.88, 0.82)
	mat.emission_enabled = true
	mat.emission = Color(0.08, 0.07, 0.06)
	mat.roughness = 0.26

	for i in range(1):
		var marker := MeshInstance3D.new()
		marker.name = "WakeMarker%d" % i
		var sphere := SphereMesh.new()
		sphere.radius = 0.42 if _mode == "full_proof" else 0.72
		sphere.height = sphere.radius * 2.0
		marker.mesh = sphere
		marker.material_override = mat
		# Keep moving contact markers out of the planar reflection pass so the
		# reflection texture only contains stable review props.
		marker.layers = LAYER_INTERACTORS
		add_child(marker)
		_markers.append(marker)
		_last_positions.append(Vector3.ZERO)
		_smoothed_velocities.append(Vector3.ZERO)


# Tear out the legacy directional GPU wake map for the review scene. Moving-object
# disturbance is carried by analytic hero ripples (emit_surface_ripple), while the
# wake texture is disabled so it cannot add low-res footprints or per-frame
# SubViewport/compute cost. Runs after the visual profile's apply_to(), which
# would otherwise re-enable the GPU wake map.
func _tune_wake_for_review() -> void:
	if _water == null:
		return
	# With these at 0 the sampled wake texture has no effect on color, foam,
	# highlight, or vertex height regardless of what (if anything) still writes it.
	_water.set("wake_map_strength", 0.0)
	_water.set("wake_foam_strength", 0.0)
	_water.set("wake_highlight_strength", 0.0)
	# Stop the wake map from being recreated and drop any node the profile made.
	_water.set("auto_create_wake_map", false)
	_water.set("use_gpu_wake_map", false)
	var node = _water.get("wake_map_node")
	if node != null:
		_water.set("wake_map_node", null)
		node.queue_free()


func _add_bow_wake_visual(marker: Node3D) -> void:
	var wake := BOW_WAKE_SCRIPT.new()
	wake.name = "HeroBowWake"
	wake.set("target", marker)
	wake.set("water_surface", _water)
	wake.set("brightness", 0.85)
	wake.set("alpha", 0.24)
	wake.set("full_strength_speed_mps", 1.4)
	wake.layers = LAYER_WATER
	add_child(wake)


func _animate_camera(_delta: float) -> void:
	var t := _sim_time
	if _mode == "underwater":
		_camera.global_position = Vector3(sin(t * 0.2) * 1.2, -0.58 + sin(t * 0.3) * 0.08, 8.0 + cos(t * 0.17) * 0.5)
		_camera.look_at(Vector3(0.0, -0.5, -1.8), Vector3.UP)
		return
	if _mode == "ocean":
		_camera.global_position = Vector3(sin(t * 0.16) * 1.8, 2.55 + sin(t * 0.18) * 0.08, 14.0 + cos(t * 0.13) * 0.55)
		_camera.look_at(Vector3(0.25, 0.02, -9.5), Vector3.UP)
		return
	if _mode == "full_proof":
		_camera.global_position = Vector3(sin(t * 0.10) * 0.28, 1.35 + sin(t * 0.13) * 0.035, 8.4 + cos(t * 0.12) * 0.16)
		_camera.look_at(Vector3(-0.65, 0.05, -0.8), Vector3.UP)
		return

	_camera.global_position = Vector3(sin(t * 0.12) * 0.55, 1.15 + sin(t * 0.2) * 0.05, 7.5 + cos(t * 0.16) * 0.22)
	_camera.look_at(Vector3(0.1, 0.02, -2.6), Vector3.UP)


func _animate_interactors(delta: float) -> void:
	var t := _sim_time
	# The floating object leaves a trail of expanding ring ripples. Rings are
	# omnidirectional, so the disturbance always reads correctly from any angle --
	# no directional wake that can look reversed, blocky, or invisible.
	_ripple_accum += delta
	var emit_ripple := _ripple_accum >= 0.30
	if emit_ripple:
		_ripple_accum -= 0.30
	for i in range(_markers.size()):
		var phase := float(i) * 2.1
		# Gentle lateral drift across the camera + a vertical bob.
		var p := Vector3(sin(t * 0.22 + phase) * 2.6, 0.26, 2.3 + cos(t * 0.22 + phase) * 0.5)
		if _mode == "ocean":
			p = Vector3(sin(t * 0.18 + phase) * 3.4, 0.25, 8.2 + cos(t * 0.18 + phase) * 1.4)
		elif _mode == "full_proof":
			p = Vector3(-1.4 + sin(t * 0.26 + phase) * 1.6, 0.20, 1.0 + cos(t * 0.26 + phase) * 0.9)
		var bob := sin(t * 1.7 + phase)
		p.y += bob * 0.05
		_markers[i].global_position = p
		_last_positions[i] = p
		if _water != null and emit_ripple and _water.has_method("emit_surface_ripple"):
			var rp := Vector3(p.x, 0.0, p.z)
			# Steady chain of small rings traces the path of travel. Kept small and
			# short-lived (speed*lifetime ~= max radius) so they stay proportionate
			# to the float instead of ballooning across the whole surface.
			_water.call("emit_surface_ripple", rp, 0.30, 0.22, 1.5, 1.1)
			# A slightly larger ring when the float bobs deepest, for life.
			if bob < -0.9:
				_water.call("emit_surface_ripple", rp, 0.42, 0.28, 1.8, 1.3)


func _capture_after_frames() -> void:
	for _i in range(_capture_frames):
		await get_tree().process_frame

	var image := get_viewport().get_texture().get_image()
	if image == null:
		push_error("FastWaterDemo: could not capture viewport image")
		get_tree().quit(1)
		return

	var dir := _screenshot_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(_screenshot_path)
	if err != OK:
		push_error("FastWaterDemo: failed to save screenshot: %s err=%d" % [_screenshot_path, err])
		get_tree().quit(1)
		return

	print("FastWaterDemo screenshot saved: " + _screenshot_path)
	_done_capture = true
	get_tree().quit(0)
