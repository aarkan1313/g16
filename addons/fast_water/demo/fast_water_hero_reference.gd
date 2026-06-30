extends Node3D

const WATER_SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const WATER_SKY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_sky.gd")
const CAUSTICS_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_caustics.gd")
const BOW_WAKE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_bow_wake.gd")
const PLANAR_REFLECTION_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_planar_reflection.gd")
const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")
const POOL_FLOOR_SHADER := "res://addons/fast_water/shaders/fast_demo_pool_floor.gdshader"
const LAYER_PROPS := 1
const LAYER_WATER := 2
const LAYER_FLOOR := 4

var _water: Node
var _camera: Camera3D
var _floaters: Array[MeshInstance3D] = []
var _last_positions: Array[Vector3] = []
var _screenshot_path := ""
var _capture_frames := 180
var _debug_view := 0
var _sim_time := 0.0
var _splash_timer := 0.0
var gate_external_capture := false
var external_debug_view := -1
var _orbit_focus := Vector3(0.0, -0.34, -3.4)
var _orbit_yaw := 0.322
var _orbit_pitch := 0.220
var _orbit_distance := 15.55
var _dragging_camera := false
var _manual_camera := false


func _ready() -> void:
	_parse_args()
	if external_debug_view >= 0:
		_debug_view = clampi(external_debug_view, 0, 7)
	_setup_scene()
	if _screenshot_path != "" and not gate_external_capture:
		_capture_after_frames.call_deferred()


func _process(delta: float) -> void:
	_sim_time += delta
	if gate_external_capture:
		_animate_camera()
	else:
		_update_camera_controls(delta)
		if not _manual_camera:
			_animate_camera()
	_animate_floaters(delta)


func _unhandled_input(event: InputEvent) -> void:
	if gate_external_capture:
		return
	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index == MOUSE_BUTTON_RIGHT:
			_dragging_camera = mouse_button.pressed
			_manual_camera = true
		elif mouse_button.pressed and mouse_button.button_index == MOUSE_BUTTON_WHEEL_UP:
			_orbit_distance = maxf(_orbit_distance - 0.75, 2.0)
			_manual_camera = true
			_apply_orbit_camera()
		elif mouse_button.pressed and mouse_button.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			_orbit_distance = minf(_orbit_distance + 0.75, 42.0)
			_manual_camera = true
			_apply_orbit_camera()
	elif event is InputEventMouseMotion and _dragging_camera:
		var motion := event as InputEventMouseMotion
		_orbit_yaw -= motion.relative.x * 0.006
		_orbit_pitch = clampf(_orbit_pitch - motion.relative.y * 0.004, -0.55, 1.05)
		_manual_camera = true
		_apply_orbit_camera()
	elif event is InputEventKey:
		var key := event as InputEventKey
		if key.pressed and not key.echo and key.keycode == KEY_R:
			_reset_camera()


func _parse_args() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			_screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--frames="):
			_capture_frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--debug-view="):
			_debug_view = clampi(int(arg.trim_prefix("--debug-view=")), 0, 7)


func _setup_scene() -> void:
	_add_sky()
	_add_lighting()
	_add_camera()
	_add_basin()
	_add_reference_props()
	_add_water()
	_add_planar_reflection()
	_add_caustics()
	_add_floaters()


func _add_sky() -> void:
	var sky := WATER_SKY_SCRIPT.new()
	sky.name = "HeroReferenceSky"
	sky.set("horizon_color", Color(0.62, 0.80, 0.91))
	sky.set("zenith_color", Color(0.11, 0.32, 0.58))
	sky.set("cloud_amount", 0.24)
	sky.set("cloud_speed", 0.08)
	add_child(sky)


func _add_lighting() -> void:
	var sun := DirectionalLight3D.new()
	sun.name = "HeroSun"
	sun.light_energy = 2.55
	sun.rotation_degrees = Vector3(-36.0, 132.0, 0.0)
	add_child(sun)

	var rim := DirectionalLight3D.new()
	rim.name = "SoftSkyRim"
	rim.light_energy = 0.36
	rim.rotation_degrees = Vector3(-14.0, -42.0, 0.0)
	add_child(rim)


func _add_camera() -> void:
	_camera = Camera3D.new()
	_camera.name = "Camera"
	_camera.fov = 54.0
	_camera.current = true
	add_child(_camera)
	_apply_orbit_camera()


func _add_basin() -> void:
	var ground := MeshInstance3D.new()
	ground.name = "HeroPoolBasin"
	ground.mesh = _make_basin_mesh(72.0, 96)
	ground.material_override = _make_floor_material()
	ground.layers = LAYER_FLOOR
	add_child(ground)


func _add_reference_props() -> void:
	var column_mat := _simple_mat(Color(0.92, 0.96, 0.98), 0.24)
	for i in range(3):
		var column := MeshInstance3D.new()
		column.name = "ReflectionColumn%d" % i
		var cyl := CylinderMesh.new()
		cyl.top_radius = 0.22
		cyl.bottom_radius = 0.30
		cyl.height = 4.8
		cyl.radial_segments = 28
		column.mesh = cyl
		column.position = Vector3(-5.4 + float(i) * 5.5, 2.10, -5.8 - float(i) * 0.55)
		column.rotation_degrees = Vector3(0.0, 0.0, -7.0 + float(i) * 5.0)
		column.material_override = column_mat
		column.layers = LAYER_PROPS
		add_child(column)


func _add_water() -> void:
	_water = WATER_SURFACE_SCRIPT.new()
	_water.name = "HeroWaterSurface"
	_water.set("target_camera", _camera)
	_water.set("follow_camera", false)
	_water.set("visual_profile", VISUAL_PROFILE_SCRIPT.hero_pool_reference())
	_water.set("mesh_size_m", 50.0)
	_water.set("mesh_subdivisions", 192)
	_water.set("max_hero_ripples", 16)
	_water.set("wake_map_resolution", 512)
	_water.set("wake_map_world_size_m", 30.0)
	_water.set("wake_map_update_hz", 45.0)
	_water.set("wake_strength_scale", 0.082)
	_water.set("splash_strength_scale", 0.30)
	_water.set("splash_pool_size", 0)
	_water.set("auto_create_bubbles", false)
	_water.set("visual_layers", LAYER_WATER)
	_water.set("debug_view", _debug_view)
	add_child(_water)


func _add_planar_reflection() -> void:
	var reflection := PLANAR_REFLECTION_SCRIPT.new()
	reflection.name = "HeroPlanarReflection"
	reflection.set("target_camera", _camera)
	reflection.set("resolution_scale", 0.58)
	reflection.set("max_resolution", 1152)
	reflection.set("reflection_strength", 0.12)
	reflection.set("distortion_strength", 0.0024)
	reflection.set("reflection_cull_mask", LAYER_PROPS)
	add_child(reflection)
	reflection.set("water_surface_path", reflection.get_path_to(_water))

	var profile := _water.get("visual_profile") as Resource
	if profile != null and profile.has_method("apply_to"):
		profile.call("apply_to", _water, _water.get("wake_map_node"), reflection)


func _add_caustics() -> void:
	var caustics := CAUSTICS_SCRIPT.new()
	caustics.name = "HeroCaustics"
	caustics.set("target_camera", _camera)
	caustics.set("follow_camera", false)
	caustics.set("depth_below_water_m", -1.05)
	caustics.set("intensity", 0.0)
	caustics.set("intensity", 0.018)
	caustics.set("caustic_scale", 11.5)
	caustics.set("speed", 0.32)
	caustics.set("distance_fade", 0.92)
	caustics.set("size_m", 112.0)
	caustics.layers = LAYER_FLOOR
	add_child(caustics)


func _add_floaters() -> void:
	var colors := [
		Color(0.96, 0.72, 0.30),
		Color(0.78, 0.88, 0.93),
		Color(0.10, 0.42, 0.54),
	]
	for i in range(3):
		var floater := MeshInstance3D.new()
		floater.name = "HeroFloater%d" % i
		var sphere := SphereMesh.new()
		sphere.radius = 0.34 + float(i) * 0.08
		sphere.height = sphere.radius * 2.0
		sphere.radial_segments = 32
		sphere.rings = 16
		floater.mesh = sphere
		floater.material_override = _simple_mat(colors[i], 0.36)
		floater.layers = LAYER_PROPS
		add_child(floater)
		_floaters.append(floater)
		_last_positions.append(Vector3.ZERO)

		var wake := BOW_WAKE_SCRIPT.new()
		wake.name = "HeroBowWake%d" % i
		wake.set("target", floater)
		wake.set("water_surface", _water)
		wake.set("brightness", 0.46)
		wake.set("alpha", 0.10)
		wake.set("full_strength_speed_mps", 1.65)
		wake.layers = LAYER_WATER
		add_child(wake)


func _animate_camera() -> void:
	var t := _sim_time
	_camera.global_position = Vector3(4.8 + sin(t * 0.10) * 0.42, 3.05 + sin(t * 0.16) * 0.06, 11.0 + cos(t * 0.13) * 0.24)
	_camera.look_at(Vector3(0.0, -0.34, -3.4), Vector3.UP)


func _update_camera_controls(delta: float) -> void:
	if _camera == null:
		return
	var move := Vector3.ZERO
	var forward := -_camera.global_transform.basis.z
	var right := _camera.global_transform.basis.x
	forward.y = 0.0
	right.y = 0.0
	forward = forward.normalized() if forward.length_squared() > 0.0001 else Vector3.FORWARD
	right = right.normalized() if right.length_squared() > 0.0001 else Vector3.RIGHT
	if Input.is_key_pressed(KEY_W):
		move += forward
	if Input.is_key_pressed(KEY_S):
		move -= forward
	if Input.is_key_pressed(KEY_D):
		move += right
	if Input.is_key_pressed(KEY_A):
		move -= right
	if Input.is_key_pressed(KEY_E):
		move += Vector3.UP
	if Input.is_key_pressed(KEY_Q):
		move -= Vector3.UP
	if move.length_squared() > 0.0001:
		var speed := 5.5
		if Input.is_key_pressed(KEY_SHIFT):
			speed *= 2.4
		if Input.is_key_pressed(KEY_CTRL):
			speed *= 0.35
		_orbit_focus += move.normalized() * speed * delta
		_manual_camera = true
		_apply_orbit_camera()


func _apply_orbit_camera() -> void:
	if _camera == null:
		return
	var cp := cos(_orbit_pitch)
	var offset := Vector3(sin(_orbit_yaw) * cp, sin(_orbit_pitch), cos(_orbit_yaw) * cp) * _orbit_distance
	_camera.global_position = _orbit_focus + offset
	_camera.look_at(_orbit_focus, Vector3.UP)


func _reset_camera() -> void:
	_orbit_focus = Vector3(0.0, -0.34, -3.4)
	_orbit_yaw = 0.322
	_orbit_pitch = 0.220
	_orbit_distance = 15.55
	_manual_camera = false
	_dragging_camera = false
	_apply_orbit_camera()


func _animate_floaters(delta: float) -> void:
	if _water == null:
		return
	var t := _sim_time
	for i in range(_floaters.size()):
		var phase := float(i) * 2.24
		var p := Vector3(-4.4 + float(i) * 3.8 + sin(t * 0.48 + phase) * 0.55, 0.22, -1.2 - float(i) * 1.85 + cos(t * 0.58 + phase) * 0.72)
		p.y += sin(t * 3.2 + phase) * 0.022
		var velocity := Vector3.ZERO
		if _last_positions[i] != Vector3.ZERO:
			velocity = (p - _last_positions[i]) / max(delta, 0.001)
		_floaters[i].global_position = p
		_last_positions[i] = p
		if _water.has_method("add_wake_point"):
			_water.call("add_wake_point", Vector3(p.x, 0.0, p.z), velocity * 1.65, 0.72)

	_splash_timer += delta
	if _splash_timer >= 0.64:
		_splash_timer = 0.0
		var splash_pos := Vector3(-1.0 + sin(t * 0.7) * 1.9, 0.0, -3.3 + cos(t * 0.41) * 1.1)
		if _water.has_method("add_splash"):
			_water.call("add_splash", splash_pos, Vector3(0.5, -3.6, -0.2), 0.72)


func _make_basin_mesh(size_m: float, cells: int) -> Mesh:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)

	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()
	var half := size_m * 0.5
	var step := size_m / float(cells)

	for z in range(cells + 1):
		for x in range(cells + 1):
			var u := float(x) / float(cells)
			var v := float(z) / float(cells)
			var px := lerp(-half, half, u)
			var pz := lerp(-half, half, v)
			verts.append(Vector3(px, _basin_height(px, pz, half), pz))
			normals.append(_basin_normal(px, pz, half, step))
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
	var x: float = px / max(half, 0.001)
	var z: float = pz / max(half, 0.001)
	var radial: float = Vector2(x * 0.82, z * 1.05).length()
	var shelf: float = smoothstep(0.46, 0.94, radial)
	var near_shelf: float = smoothstep(0.18, 0.80, z)
	var side_lift: float = smoothstep(0.68, 0.98, abs(x)) * 0.40
	var floor_wave: float = sin(px * 0.16 + pz * 0.10) * 0.026 + sin(px * -0.07 + pz * 0.21) * 0.018
	return lerp(-1.42, -0.38, shelf) + near_shelf * 0.22 + side_lift + floor_wave


func _basin_normal(px: float, pz: float, half: float, step: float) -> Vector3:
	var e := max(step, 0.02)
	var hx0 := _basin_height(px - e, pz, half)
	var hx1 := _basin_height(px + e, pz, half)
	var hz0 := _basin_height(px, pz - e, half)
	var hz1 := _basin_height(px, pz + e, half)
	return Vector3(hx0 - hx1, e * 2.0, hz0 - hz1).normalized()


func _make_floor_material() -> Material:
	var mat := ShaderMaterial.new()
	var shader := load(POOL_FLOOR_SHADER) as Shader
	if shader != null:
		mat.shader = shader
		mat.set_shader_parameter("concrete_color", Color(0.050, 0.115, 0.120))
		mat.set_shader_parameter("grout_color", Color(0.018, 0.046, 0.052))
		mat.set_shader_parameter("tile_count", 12.0)
		mat.set_shader_parameter("line_visibility", 0.0)
	return mat


func _add_box(name: String, pos: Vector3, size: Vector3, mat: Material, layer_mask: int) -> void:
	var node := MeshInstance3D.new()
	node.name = name
	var mesh := BoxMesh.new()
	mesh.size = size
	node.mesh = mesh
	node.position = pos
	node.material_override = mat
	node.layers = layer_mask
	add_child(node)


func _simple_mat(color: Color, roughness: float) -> Material:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.roughness = roughness
	return mat


func _capture_after_frames() -> void:
	for _i in range(_capture_frames):
		await get_tree().process_frame

	var image := get_viewport().get_texture().get_image()
	if image == null:
		push_error("FastWaterHeroReference: could not capture viewport image")
		get_tree().quit(1)
		return

	var global_path := ProjectSettings.globalize_path(_screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWaterHeroReference: failed to save screenshot: %s err=%d" % [global_path, err])
		get_tree().quit(1)
		return

	print("FastWaterHeroReference screenshot saved: " + global_path)
	get_tree().quit(0)
