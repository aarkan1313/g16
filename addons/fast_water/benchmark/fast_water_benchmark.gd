extends Node3D

const WATER_SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const WATER_SKY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_sky.gd")
const CAUSTICS_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_caustics.gd")
const BOW_WAKE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_bow_wake.gd")
const WAKE_RIBBON_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_wake_ribbon.gd")
const PLANAR_REFLECTION_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_planar_reflection.gd")
const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")
const CPU_WAKE_MAP_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_wake_map.gd")
const GPU_WAKE_MAP_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_gpu_wake_map.gd")
const POOL_FLOOR_SHADER := "res://addons/fast_water/shaders/fast_demo_pool_floor.gdshader"

const LAYER_PROPS := 1
const LAYER_WATER := 2
const LAYER_FLOOR := 4
const MAX_ACTORS := 24
const DEBUG_VIEW_COUNT := 8

var gate_external_capture := false
var external_debug_view := 0

var _water: Node
var _reflection: Node
var _camera: Camera3D
var _actors: Array[MeshInstance3D] = []
var _bow_wakes: Array[Node] = []
var _wake_ribbons: Array[Node] = []
var _last_positions: Array[Vector3] = []
var _last_position_valid: Array[bool] = []

var _profile_index := 2
var _actor_count := 8
var _use_gpu_backend := true
var _reflection_enabled := true
var _wake_debug_enabled := false
var _shader_debug_view := 0
var _paused := false

var _sim_time := 0.0
var _fps_accum := 0.0
var _orbit_yaw := 0.0
var _orbit_pitch := 0.12
var _orbit_distance := 5.9
var _orbit_focus := Vector3(0.0, 0.08, 0.90)
var _dragging_camera := false

var _status_label: Label
var _actor_label: Label
var _actor_slider: HSlider
var _gpu_button: Button
var _reflection_button: Button
var _debug_button: Button
var _shader_debug_button: Button
var _pause_button: Button
var _wake_debug_panel: Control
var _wake_debug_texture: TextureRect


func _ready() -> void:
	_setup_world()
	_build_ui()
	_set_profile(_profile_index)
	_set_shader_debug_view(external_debug_view)
	_set_actor_count(_actor_count)
	_apply_camera()


func _process(delta: float) -> void:
	if not _paused:
		_sim_time += delta
		_animate_actors(delta)
	_update_hud(delta)
	_update_wake_debug_texture()
	_apply_camera()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:
		var mouse_button := event as InputEventMouseButton
		if mouse_button.button_index == MOUSE_BUTTON_RIGHT:
			_dragging_camera = mouse_button.pressed
		elif mouse_button.pressed and mouse_button.button_index == MOUSE_BUTTON_WHEEL_UP:
			_orbit_distance = max(_orbit_distance - 0.45, 3.4)
		elif mouse_button.pressed and mouse_button.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			_orbit_distance = min(_orbit_distance + 0.45, 18.0)
	elif event is InputEventMouseMotion and _dragging_camera:
		var motion := event as InputEventMouseMotion
		_orbit_yaw -= motion.relative.x * 0.006
		_orbit_pitch = clamp(_orbit_pitch - motion.relative.y * 0.004, -0.18, 0.58)
	elif event is InputEventKey:
		var key := event as InputEventKey
		if not key.pressed or key.echo:
			return
		match key.keycode:
			KEY_1:
				_set_profile(0)
			KEY_2:
				_set_profile(1)
			KEY_3:
				_set_profile(2)
			KEY_G:
				_set_gpu_backend(not _use_gpu_backend)
			KEY_R:
				_set_reflection_enabled(not _reflection_enabled)
			KEY_D:
				_set_wake_debug_enabled(not _wake_debug_enabled)
			KEY_V:
				_set_shader_debug_view(_shader_debug_view + 1)
			KEY_SPACE:
				_set_paused(not _paused)
			KEY_BRACKETLEFT:
				_set_actor_count(_actor_count - 1)
			KEY_BRACKETRIGHT:
				_set_actor_count(_actor_count + 1)


func _setup_world() -> void:
	var sky := WATER_SKY_SCRIPT.new()
	sky.name = "ProceduralSky"
	sky.set("horizon_color", Color(0.60, 0.76, 0.86))
	sky.set("zenith_color", Color(0.18, 0.40, 0.66))
	sky.set("cloud_amount", 0.16)
	add_child(sky)

	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.light_energy = 1.95
	sun.rotation_degrees = Vector3(-28.0, 126.0, 0.0)
	add_child(sun)

	_camera = Camera3D.new()
	_camera.name = "Camera"
	_camera.fov = 61.0
	_camera.current = true
	add_child(_camera)

	_add_ground()

	_water = WATER_SURFACE_SCRIPT.new()
	_water.name = "FastWaterSurface"
	_water.set("target_camera", _camera)
	_water.set("visual_profile", VISUAL_PROFILE_SCRIPT.hero_quality())
	_water.set("mesh_size_m", 82.0)
	_water.set("visual_layers", LAYER_WATER)
	_water.set("max_hero_ripples", 16)
	_water.set("max_interaction_distance_m", 120.0)
	_water.set("wake_strength_scale", 0.13)
	_water.set("splash_strength_scale", 0.36)
	_water.set("splash_pool_size", 12)
	_water.set("auto_create_bubbles", false)
	add_child(_water)

	_reflection = PLANAR_REFLECTION_SCRIPT.new()
	_reflection.name = "PlanarReflection"
	_reflection.set("target_camera", _camera)
	_reflection.set("resolution_scale", 0.48)
	_reflection.set("max_resolution", 960)
	_reflection.set("reflection_cull_mask", LAYER_PROPS)
	add_child(_reflection)
	_reflection.set("water_surface_path", _reflection.get_path_to(_water))

	var caustics := CAUSTICS_SCRIPT.new()
	caustics.name = "Caustics"
	caustics.set("target_camera", _camera)
	caustics.set("depth_below_water_m", -0.88)
	caustics.set("intensity", 0.055)
	caustics.set("size_m", 72.0)
	caustics.layers = LAYER_FLOOR
	add_child(caustics)

	_build_actors()


func _add_ground() -> void:
	var ground := MeshInstance3D.new()
	ground.name = "BenchmarkPoolFloor"
	ground.mesh = _make_ground_mesh(68.0, 48)
	ground.material_override = _make_ground_material()
	ground.layers = LAYER_FLOOR
	add_child(ground)

	var red_mat := _simple_mat(Color(0.82, 0.02, 0.06), 0.42)
	var dark_mat := _simple_mat(Color(0.12, 0.0, 0.015), 0.55)
	var stone_mat := _simple_mat(Color(0.72, 0.66, 0.56), 0.62)

	_add_box("CenterBlock", Vector3(0.0, 0.16, -2.55), Vector3(2.7, 0.50, 1.10), red_mat)
	_add_box("RightBlock", Vector3(6.0, 0.15, -3.6), Vector3(2.6, 0.46, 0.95), red_mat)
	_add_box("LeftCurb", Vector3(-7.5, 0.10, -5.8), Vector3(9.0, 0.28, 0.90), stone_mat)
	_add_box("FarDarkCurb", Vector3(0.0, 0.13, -11.5), Vector3(34.0, 0.32, 0.42), dark_mat)

	var pillar := MeshInstance3D.new()
	pillar.name = "SlantedPillar"
	var cyl := CylinderMesh.new()
	cyl.top_radius = 0.32
	cyl.bottom_radius = 0.32
	cyl.height = 5.2
	cyl.radial_segments = 28
	pillar.mesh = cyl
	pillar.position = Vector3(0.05, 2.35, -2.45)
	pillar.rotation_degrees = Vector3(0.0, 0.0, -13.0)
	pillar.material_override = stone_mat
	pillar.layers = LAYER_PROPS
	add_child(pillar)


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
			verts.append(Vector3(px, _basin_height(px, pz, half), pz))
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
	var front_shelf: float = _smooth_range(-0.10, 0.72, z)
	var side_shelf: float = _smooth_range(0.66, 0.98, abs(x))
	var far_channel: float = _smooth_range(-0.08, -0.88, z)
	var y: float = lerp(-1.18, -0.58, front_shelf)
	y = lerp(y, -0.66, side_shelf * 0.55)
	y -= far_channel * 0.20
	y += sin(px * 0.18 + pz * 0.11) * 0.028
	y += sin(px * -0.07 + pz * 0.21) * 0.018
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
	return mat


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


func _build_actors() -> void:
	var hero_mat := StandardMaterial3D.new()
	hero_mat.albedo_color = Color(0.92, 0.90, 0.86)
	hero_mat.roughness = 0.22
	hero_mat.metallic = 0.0

	var actor_mat := StandardMaterial3D.new()
	actor_mat.albedo_color = Color(0.16, 0.58, 0.72)
	actor_mat.roughness = 0.36

	for i in range(MAX_ACTORS):
		var actor := MeshInstance3D.new()
		actor.name = "WakeActor%d" % i
		var sphere := SphereMesh.new()
		var radius := 0.58 if i == 0 else 0.26
		sphere.radius = radius
		sphere.height = radius * 2.0
		sphere.radial_segments = 32 if i == 0 else 16
		sphere.rings = 16 if i == 0 else 8
		actor.mesh = sphere
		actor.material_override = hero_mat if i == 0 else actor_mat
		actor.layers = LAYER_PROPS
		add_child(actor)

		_actors.append(actor)
		_last_positions.append(Vector3.ZERO)
		_last_position_valid.append(false)

		if i < 4:
			var wake := BOW_WAKE_SCRIPT.new()
			wake.name = "BowWake%d" % i
			wake.set("target", actor)
			wake.set("water_surface", _water)
			wake.set("brightness", 0.56 if i == 0 else 0.36)
			wake.set("alpha", 0.15 if i == 0 else 0.08)
			wake.set("inner_radius_m", 0.90 if i == 0 else 0.34)
			wake.set("outer_radius_m", 1.46 if i == 0 else 0.55)
			wake.set("lateral_scale", 1.62 if i == 0 else 1.30)
			wake.set("forward_scale", 0.58 if i == 0 else 0.42)
			wake.set("water_y_offset", 0.062)
			wake.set("full_strength_speed_mps", 0.82 if i == 0 else 1.35)
			wake.layers = LAYER_WATER
			add_child(wake)
			_bow_wakes.append(wake)

		if i == 0:
			var ribbon := WAKE_RIBBON_SCRIPT.new()
			ribbon.name = "HeroWakeRibbon"
			ribbon.set("target", actor)
			ribbon.set("water_y", 0.052)
			ribbon.set("width_m", 0.28)
			ribbon.set("max_points", 64)
			ribbon.set("lifetime_s", 1.9)
			ribbon.set("brightness", 0.30)
			ribbon.layers = LAYER_WATER
			add_child(ribbon)
			_wake_ribbons.append(ribbon)


func _build_ui() -> void:
	var layer := CanvasLayer.new()
	layer.name = "BenchmarkHud"
	add_child(layer)

	var panel := PanelContainer.new()
	panel.position = Vector2(14.0, 14.0)
	panel.custom_minimum_size = Vector2(570.0, 154.0)
	layer.add_child(panel)

	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 10)
	margin.add_theme_constant_override("margin_top", 8)
	margin.add_theme_constant_override("margin_right", 10)
	margin.add_theme_constant_override("margin_bottom", 8)
	panel.add_child(margin)

	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 6)
	margin.add_child(vbox)

	_status_label = Label.new()
	_status_label.text = ""
	vbox.add_child(_status_label)

	var profile_row := HBoxContainer.new()
	profile_row.add_theme_constant_override("separation", 6)
	vbox.add_child(profile_row)
	_add_button(profile_row, "Mobile", _set_profile.bind(0))
	_add_button(profile_row, "Balanced", _set_profile.bind(1))
	_add_button(profile_row, "Hero", _set_profile.bind(2))

	var toggle_row := HBoxContainer.new()
	toggle_row.add_theme_constant_override("separation", 6)
	vbox.add_child(toggle_row)
	_gpu_button = _add_button(toggle_row, "GPU wakes", _toggle_gpu_from_button, true)
	_reflection_button = _add_button(toggle_row, "Reflection", _toggle_reflection_from_button, true)
	_debug_button = _add_button(toggle_row, "Wake map", _toggle_debug_from_button, true)
	_shader_debug_button = _add_button(toggle_row, "View: beauty", _cycle_shader_debug_from_button)
	_pause_button = _add_button(toggle_row, "Pause", _toggle_pause_from_button, true)

	var actor_row := HBoxContainer.new()
	actor_row.add_theme_constant_override("separation", 8)
	vbox.add_child(actor_row)
	_actor_label = Label.new()
	_actor_label.custom_minimum_size = Vector2(86.0, 0.0)
	actor_row.add_child(_actor_label)
	_actor_slider = HSlider.new()
	_actor_slider.min_value = 1.0
	_actor_slider.max_value = MAX_ACTORS
	_actor_slider.step = 1.0
	_actor_slider.value = _actor_count
	_actor_slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_actor_slider.value_changed.connect(_on_actor_slider_changed)
	actor_row.add_child(_actor_slider)

	_wake_debug_panel = PanelContainer.new()
	_wake_debug_panel.visible = false
	_wake_debug_panel.custom_minimum_size = Vector2(236.0, 236.0)
	_wake_debug_panel.anchor_left = 1.0
	_wake_debug_panel.anchor_right = 1.0
	_wake_debug_panel.offset_left = -252.0
	_wake_debug_panel.offset_right = -16.0
	_wake_debug_panel.offset_top = 14.0
	_wake_debug_panel.offset_bottom = 250.0
	layer.add_child(_wake_debug_panel)

	_wake_debug_texture = TextureRect.new()
	_wake_debug_texture.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_wake_debug_texture.stretch_mode = TextureRect.STRETCH_SCALE
	_wake_debug_texture.custom_minimum_size = Vector2(224.0, 224.0)
	_wake_debug_panel.add_child(_wake_debug_texture)

	_sync_ui()


func _add_button(parent: Control, text: String, callable: Callable, toggle := false) -> Button:
	var button := Button.new()
	button.text = text
	button.toggle_mode = toggle
	button.focus_mode = Control.FOCUS_NONE
	button.pressed.connect(callable)
	parent.add_child(button)
	return button


func _animate_actors(delta: float) -> void:
	for i in range(_actors.size()):
		var actor := _actors[i]
		if i >= _actor_count:
			continue

		var p := _actor_position(i, _sim_time)
		var velocity := Vector3.ZERO
		if _last_position_valid[i] and delta > 0.0:
			velocity = (p - _last_positions[i]) / delta
		actor.global_position = p
		_last_positions[i] = p
		_last_position_valid[i] = true

		var radius := 0.78 if i == 0 else 0.34
		var wake_gain := 2.5 if i == 0 else 1.8
		if _water != null and _water.has_method("add_wake_point"):
			_water.call("add_wake_point", Vector3(p.x, 0.0, p.z), velocity * wake_gain, radius)

		if i < 8 and fmod(_sim_time + float(i) * 0.11, 0.34) < delta and _water != null and _water.has_method("add_splash"):
			_water.call("add_splash", Vector3(p.x, 0.0, p.z), velocity * 0.8 + Vector3(0.0, -4.8, 0.0), radius * 0.82)


func _actor_position(index: int, t: float) -> Vector3:
	if index == 0:
		return Vector3(sin(t * 0.92) * 1.25, 0.30 + sin(t * 3.1) * 0.035, 1.25 + cos(t * 0.72) * 0.48)

	var phase := float(index) * 1.71
	var lane := float(index % 6) - 2.5
	var row := float(index / 6)
	var x := lane * 0.68 + sin(t * (0.74 + float(index % 3) * 0.05) + phase) * 0.58
	var z := -0.55 - row * 0.72 + cos(t * (0.62 + float(index % 4) * 0.04) + phase) * 0.42
	var y := 0.15 + sin(t * 2.7 + phase) * 0.018
	return Vector3(x, y, z)


func _set_profile(index: int) -> void:
	_profile_index = clampi(index, 0, 2)
	if _water == null:
		return

	var profile := _make_profile(_profile_index)
	profile.set("use_gpu_wake_map", _use_gpu_backend)
	_water.set("visual_profile", profile)
	if _water.has_method("apply_visual_profile"):
		_water.call("apply_visual_profile", profile)

	_apply_benchmark_overrides(profile)
	_apply_reflection_state()
	_apply_shader_debug_view()
	_sync_ui()


func _make_profile(index: int) -> Resource:
	if index == 0:
		var mobile := VISUAL_PROFILE_SCRIPT.mobile_low()
		mobile.set("wake_map_update_hz", 20.0)
		mobile.set("wake_map_world_size_m", 42.0)
		return mobile
	if index == 1:
		var balanced := VISUAL_PROFILE_SCRIPT.gameplay_lake()
		return balanced

	var hero := VISUAL_PROFILE_SCRIPT.hero_pool_reference()
	hero.set("wake_map_strength", 0.72)
	hero.set("wake_highlight_strength", 1.24)
	hero.set("stamp_center_trail_strength", 0.44)
	hero.set("stamp_bow_band_strength", 0.60)
	hero.set("stamp_foam_gain", 0.64)
	hero.set("planar_reflection_strength", 0.24)
	hero.set("planar_reflection_distortion", 0.007)
	hero.set("final_color_gain", 1.10)
	return hero


func _apply_benchmark_overrides(profile: Resource) -> void:
	_set_if_has(_water, "mesh_size_m", 82.0)
	_set_if_has(_water, "max_hero_ripples", 16)
	_set_if_has(_water, "max_interaction_distance_m", 120.0)
	_set_if_has(_water, "wake_strength_scale", 0.10 if _profile_index == 0 else 0.15)
	_set_if_has(_water, "splash_strength_scale", 0.30 if _profile_index == 0 else 0.42)
	_set_if_has(_water, "splash_pool_size", 8 if _profile_index == 0 else 12)
	_set_if_has(_water, "auto_create_bubbles", false)
	_set_if_has(_water, "visual_layers", LAYER_WATER)
	if _reflection != null and profile.has_method("apply_to"):
		profile.call("apply_to", _water, _water.get("wake_map_node"), _reflection)
		_reflection.set("resolution_scale", 0.36 if _profile_index == 0 else 0.48)
		_reflection.set("max_resolution", 720 if _profile_index == 0 else 960)


func _set_gpu_backend(enabled: bool) -> void:
	if _use_gpu_backend == enabled and _water != null and _water.get("wake_map_node") != null:
		_sync_ui()
		return
	_use_gpu_backend = enabled
	_replace_wake_node()
	_set_profile(_profile_index)


func _replace_wake_node() -> void:
	if _water == null:
		return

	var old_wake := _water.get("wake_map_node") as Node
	if old_wake != null:
		if old_wake.get_parent() != null:
			old_wake.get_parent().remove_child(old_wake)
		old_wake.queue_free()

	var wake := GPU_WAKE_MAP_SCRIPT.new() if _use_gpu_backend else CPU_WAKE_MAP_SCRIPT.new()
	wake.name = "WakeMap"
	_water.add_child(wake)
	_water.set("wake_map_node", wake)
	_water.set("use_gpu_wake_map", _use_gpu_backend)
	_set_if_has(wake, "target_camera", _camera)
	_set_if_has(wake, "resolution", int(_water.get("wake_map_resolution")))
	_set_if_has(wake, "world_size_m", float(_water.get("wake_map_world_size_m")))
	_set_if_has(wake, "update_hz", float(_water.get("wake_map_update_hz")))
	if wake.has_method("rebuild"):
		wake.call("rebuild", int(_water.get("wake_map_resolution")))


func _set_reflection_enabled(enabled: bool) -> void:
	_reflection_enabled = enabled
	_apply_reflection_state()
	_sync_ui()


func _apply_reflection_state() -> void:
	if _reflection == null:
		return
	_reflection.set("enabled", _reflection_enabled)
	_reflection.set_process(_reflection_enabled)
	var material := _water.get("water_material") as ShaderMaterial if _water != null else null
	if material != null and not _reflection_enabled:
		material.set_shader_parameter("planar_reflection_enabled", false)


func _set_wake_debug_enabled(enabled: bool) -> void:
	_wake_debug_enabled = enabled
	if _wake_debug_panel != null:
		_wake_debug_panel.visible = _wake_debug_enabled
	_sync_ui()


func _set_paused(enabled: bool) -> void:
	_paused = enabled
	_sync_ui()


func _set_actor_count(count: int) -> void:
	_actor_count = clampi(count, 1, MAX_ACTORS)
	for i in range(_actors.size()):
		var active := i < _actor_count
		_actors[i].visible = active
		_last_position_valid[i] = false
		if i < _bow_wakes.size():
			_bow_wakes[i].visible = active
			_bow_wakes[i].set_process(active)
		if i < _wake_ribbons.size():
			_wake_ribbons[i].visible = active
			_wake_ribbons[i].set_process(active)
			if active and _wake_ribbons[i].has_method("clear"):
				_wake_ribbons[i].call("clear")
	if _actor_slider != null and int(_actor_slider.value) != _actor_count:
		_actor_slider.value = _actor_count
	_sync_ui()


func _toggle_gpu_from_button() -> void:
	_set_gpu_backend(_gpu_button.button_pressed)


func _toggle_reflection_from_button() -> void:
	_set_reflection_enabled(_reflection_button.button_pressed)


func _toggle_debug_from_button() -> void:
	_set_wake_debug_enabled(_debug_button.button_pressed)


func _cycle_shader_debug_from_button() -> void:
	_set_shader_debug_view(_shader_debug_view + 1)


func _toggle_pause_from_button() -> void:
	_set_paused(_pause_button.button_pressed)


func _on_actor_slider_changed(value: float) -> void:
	_set_actor_count(int(round(value)))


func _update_hud(delta: float) -> void:
	_fps_accum += delta
	if _fps_accum < 0.18:
		return
	_fps_accum = 0.0
	_sync_ui()


func _sync_ui() -> void:
	if _status_label != null:
		var backend := "GPU" if _use_gpu_backend else "CPU"
		var profile := _profile_name()
		var fps: int = Engine.get_frames_per_second()
		var frame_ms: float = 1000.0 / max(float(fps), 1.0)
		var wake_resolution := int(_water.get("wake_map_resolution")) if _water != null else 0
		var mesh_subdivisions := int(_water.get("mesh_subdivisions")) if _water != null else 0
		_status_label.text = "%s wakes | %s/%s | reflection %s | %d actors | %d wake | %d mesh | %d FPS %.1f ms\n1/2/3 quality  G backend  R reflection  D wake map  V view  [ ] actors  RMB orbit"
		_status_label.text = _status_label.text % [
			backend,
			profile,
			_debug_view_name(_shader_debug_view),
			"on" if _reflection_enabled else "off",
			_actor_count,
			wake_resolution,
			mesh_subdivisions,
			fps,
			frame_ms,
		]
	if _actor_label != null:
		_actor_label.text = "Actors %d" % _actor_count
	if _gpu_button != null:
		_gpu_button.button_pressed = _use_gpu_backend
	if _reflection_button != null:
		_reflection_button.button_pressed = _reflection_enabled
	if _debug_button != null:
		_debug_button.button_pressed = _wake_debug_enabled
	if _pause_button != null:
		_pause_button.button_pressed = _paused
	if _shader_debug_button != null:
		_shader_debug_button.text = "View: %s" % _debug_view_name(_shader_debug_view)


func _profile_name() -> String:
	match _profile_index:
		0:
			return "mobile"
		1:
			return "gameplay"
		_:
			return "hero"


func _set_shader_debug_view(index: int) -> void:
	_shader_debug_view = wrapi(index, 0, DEBUG_VIEW_COUNT)
	_apply_shader_debug_view()
	_sync_ui()


func _apply_shader_debug_view() -> void:
	_set_if_has(_water, "debug_view", _shader_debug_view)


func _debug_view_name(index: int) -> String:
	match index:
		1:
			return "depth"
		2:
			return "foam"
		3:
			return "wake"
		4:
			return "normals"
		5:
			return "flow"
		6:
			return "reflection"
		7:
			return "optics"
		_:
			return "beauty"


func _update_wake_debug_texture() -> void:
	if not _wake_debug_enabled or _wake_debug_texture == null or _water == null:
		return
	var wake := _water.get("wake_map_node") as Object
	if wake == null or not _has_property(wake, "texture"):
		return
	_wake_debug_texture.texture = wake.get("texture") as Texture2D


func _apply_camera() -> void:
	if _camera == null:
		return
	var cp := cos(_orbit_pitch)
	var offset := Vector3(sin(_orbit_yaw) * cp, sin(_orbit_pitch), cos(_orbit_yaw) * cp) * _orbit_distance
	_camera.global_position = _orbit_focus + offset
	_camera.look_at(_orbit_focus, Vector3.UP)


func _set_if_has(object: Object, property_name: String, value: Variant) -> void:
	if object == null:
		return
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			object.set(property_name, value)
			return


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
