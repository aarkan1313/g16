extends Node3D

# Mountain stream demo. The terrain bed and banks are derived FROM the water
# centerline, so the river always sits IN its channel (depth at the centre tapering
# to the waterline at the banks) instead of floating on the ground. The stream
# descends a winding valley with rapids (narrow/steep), pools (wide/calm), surface-
# breaking rocks, banks, drift logs, and buoyant drifters carried by the flow field.

const WATER_PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const WATER_SKY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_sky.gd")

const X_MIN := -30.0
const X_MAX := 30.0
const LENGTH := 60.0
const TOP_Y := 14.0
const WALL_SLOPE := 0.55
const WALL_MAX := 7.5

var _river: Node
var _camera: Camera3D
var _drifters: Array[MeshInstance3D] = []
var _drifter_offsets := PackedFloat32Array()
# 6 control points, top (pool) -> rapids -> mouth (pool).
var _river_widths := PackedFloat32Array([6.5, 5.0, 3.6, 3.8, 5.6, 7.2])
var _river_depths := PackedFloat32Array([1.2, 1.0, 0.85, 1.0, 1.4, 1.9])
var _river_speeds := PackedFloat32Array([1.4, 2.4, 3.6, 3.9, 2.6, 2.0])
var _river_bank_foam := PackedFloat32Array([0.4, 0.7, 1.05, 1.05, 0.7, 0.6])
var _river_turbulence := PackedFloat32Array([0.3, 0.7, 1.35, 1.35, 0.7, 0.5])
var _screenshot_path := ""
var _flow_debug_path := ""
var _capture_frames := 180
var _debug_view := 0
var gate_external_capture := false
var _sim_time := 0.0


func _ready() -> void:
	_parse_args()
	_setup_scene()
	if _screenshot_path != "" and not gate_external_capture:
		_capture_after_frames.call_deferred()


func _process(delta: float) -> void:
	_sim_time += delta
	_animate_camera()
	_animate_drifters(delta)


func _parse_args() -> void:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			_screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--flow-debug="):
			_flow_debug_path = arg.trim_prefix("--flow-debug=")
		elif arg.begins_with("--frames="):
			_capture_frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--debug-view="):
			_debug_view = clampi(int(arg.trim_prefix("--debug-view=")), 0, 7)


func _setup_scene() -> void:
	var sky := WATER_SKY_SCRIPT.new()
	sky.name = "ProceduralSky"
	sky.set("horizon_color", Color(0.62, 0.74, 0.80))
	sky.set("zenith_color", Color(0.20, 0.36, 0.52))
	sky.set("cloud_amount", 0.30)
	add_child(sky)

	var sun := DirectionalLight3D.new()
	sun.name = "Sun"
	sun.light_energy = 1.7
	sun.rotation_degrees = Vector3(-42.0, 119.0, 0.0)
	sun.shadow_enabled = true
	add_child(sun)

	_camera = Camera3D.new()
	_camera.name = "Camera"
	_camera.fov = 62.0
	_camera.far = 4000.0
	_camera.current = true
	add_child(_camera)

	_add_ground()
	_add_river()
	_add_drifters()


# --- canonical stream centreline (water surface) -------------------------------

func _t_at_x(x: float) -> float:
	return clampf((x - X_MIN) / LENGTH, 0.0, 1.0)


func water_y_at_t(t: float) -> float:
	# Eased descent: flatter pools at top/bottom, steeper rapids in the middle.
	return TOP_Y * (1.0 - smoothstep(0.0, 1.0, t))


func center_z_at_t(t: float) -> float:
	return sin(t * PI * 2.4) * 7.0


func _width_at_t(t: float) -> float:
	return _sample_river_float(_river_widths, t, 5.0)


func _depth_at_t(t: float) -> float:
	return _sample_river_float(_river_depths, t, 1.1)


# --- terrain derived from the centreline ---------------------------------------

func _terrain_height(x: float, z: float) -> float:
	var t := _t_at_x(x)
	var wy := water_y_at_t(t)
	var cz := center_z_at_t(t)
	var hw: float = _width_at_t(t) * 0.5
	var dep := _depth_at_t(t)
	var d: float = abs(z - cz)
	if d <= hw:
		# Channel bed: depth `dep` at the centre, rising to the waterline at the bank.
		var edge := smoothstep(0.0, hw, d)
		var bed_noise := sin(x * 0.7) * cos(z * 0.6) * 0.05
		return wy - dep * (1.0 - edge) + bed_noise
	# Beyond the waterline: a low bank lip climbing into the valley walls.
	var over := d - hw
	var lip: float = smoothstep(0.0, 1.4, over) * 0.55
	var wall: float = clampf((over - 1.8) * WALL_SLOPE, 0.0, WALL_MAX)
	var rocky := sin(x * 0.4 + z * 0.3) * 0.35 + sin(x * -0.17 + z * 0.5) * 0.22
	return wy + lip + wall + rocky * smoothstep(0.0, 3.0, over)


func _terrain_normal(x: float, z: float, step: float) -> Vector3:
	var e: float = max(step, 0.05)
	var hx0 := _terrain_height(x - e, z)
	var hx1 := _terrain_height(x + e, z)
	var hz0 := _terrain_height(x, z - e)
	var hz1 := _terrain_height(x, z + e)
	return Vector3(hx0 - hx1, e * 2.0, hz0 - hz1).normalized()


func _terrain_color(x: float, z: float) -> Color:
	var t := _t_at_x(x)
	var cz := center_z_at_t(t)
	var hw: float = _width_at_t(t) * 0.5
	var d: float = abs(z - cz)
	var over: float = max(d - hw, 0.0)
	var noise := sin(x * 0.31 + z * 0.17) * 0.5 + sin(x * -0.11 + z * 0.43) * 0.5
	var wet := Color(0.20, 0.22, 0.20)
	var gravel := Color(0.45, 0.41, 0.33)
	var grass := Color(0.24, 0.34, 0.24)
	var rock := Color(0.34, 0.32, 0.30)
	var col := wet.lerp(gravel, smoothstep(0.0, 1.6, over))
	col = col.lerp(grass, smoothstep(2.0, 5.0, over) * (0.6 + noise * 0.2))
	col = col.lerp(rock, smoothstep(5.0, 11.0, over))
	return col


# --- ground / banks / rocks / logs ---------------------------------------------

func _add_ground() -> void:
	var ground := MeshInstance3D.new()
	ground.name = "RiverValley"
	ground.mesh = _make_ground_mesh(68.0, 56.0, 150, 124)
	ground.material_override = _make_ground_material()
	add_child(ground)

	_add_bank_strips()
	var rock_mat := _simple_mat(Color(0.30, 0.29, 0.27), 0.85)
	_add_bank_rocks(rock_mat)
	_add_rapid_rocks(rock_mat)
	_add_drift_logs()


func _make_ground_mesh(size_x: float, size_z: float, cells_x: int, cells_z: int) -> Mesh:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var colors := PackedColorArray()
	var indices := PackedInt32Array()
	var half_x := size_x * 0.5
	var half_z := size_z * 0.5
	var step := size_x / float(cells_x)

	for z in range(cells_z + 1):
		for x in range(cells_x + 1):
			var u := float(x) / float(cells_x)
			var v := float(z) / float(cells_z)
			var px := lerp(-half_x, half_x, u)
			var pz := lerp(-half_z, half_z, v)
			verts.append(Vector3(px, _terrain_height(px, pz), pz))
			normals.append(_terrain_normal(px, pz, step))
			uvs.append(Vector2(u, v) * 8.0)
			colors.append(_terrain_color(px, pz))

	for z in range(cells_z):
		for x in range(cells_x):
			var a := z * (cells_x + 1) + x
			var b := a + 1
			var c := a + cells_x + 1
			var d := c + 1
			indices.append_array(PackedInt32Array([a, c, b, b, c, d]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_COLOR] = colors
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _make_ground_material() -> Material:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color.WHITE
	mat.vertex_color_use_as_albedo = true
	mat.roughness = 0.9
	mat.cull_mode = BaseMaterial3D.CULL_DISABLED
	return mat


func _simple_mat(color: Color, roughness: float) -> Material:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.roughness = roughness
	return mat


func _add_bank_strips() -> void:
	var wet_mat := _simple_mat(Color(0.22, 0.24, 0.21), 0.93)
	var gravel_mat := _simple_mat(Color(0.46, 0.42, 0.33), 0.88)
	for side in [-1.0, 1.0]:
		var wet := MeshInstance3D.new()
		wet.name = "RiverWetBank%s" % ("Left" if side < 0.0 else "Right")
		wet.mesh = _make_bank_strip_mesh(side, -0.15, 0.9, 80)
		wet.material_override = wet_mat
		add_child(wet)
		var gravel := MeshInstance3D.new()
		gravel.name = "RiverGravelBank%s" % ("Left" if side < 0.0 else "Right")
		gravel.mesh = _make_bank_strip_mesh(side, 0.85, 2.6, 80)
		gravel.material_override = gravel_mat
		add_child(gravel)


func _make_bank_strip_mesh(side: float, inner_extra_m: float, outer_extra_m: float, samples: int) -> Mesh:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()

	for i in range(samples + 1):
		var t: float = float(i) / float(samples)
		var center := _point_on_river(t)
		var side_vec := _river_side_at(t, side)
		var half_width: float = _river_width_at(t) * 0.5
		var inner := center + side_vec * (half_width + inner_extra_m)
		var outer := center + side_vec * (half_width + outer_extra_m)
		inner.y = _terrain_height(inner.x, inner.z) + 0.04
		outer.y = _terrain_height(outer.x, outer.z) + 0.05
		verts.append(inner)
		verts.append(outer)
		normals.append(Vector3.UP)
		normals.append(Vector3.UP)
		uvs.append(Vector2(0.0, t * 16.0))
		uvs.append(Vector2(1.0, t * 16.0))

	for i in range(samples):
		var a := i * 2
		var b := a + 1
		var c := a + 2
		var d := a + 3
		indices.append_array(PackedInt32Array([a, c, b, b, c, d]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _add_bank_rocks(rock_mat: Material) -> void:
	for i in range(46):
		var t: float = 0.02 + float(i) / 45.0 * 0.96
		var side := -1.0 if i % 2 == 0 else 1.0
		var jitter := sin(float(i) * 2.17) * 0.7
		var center := _point_on_river(t)
		var side_vec := _river_side_at(t, side)
		var bank_offset: float = _river_width_at(t) * 0.5 + 0.4 + abs(cos(float(i) * 0.73)) * 2.1
		var position := center + side_vec * (bank_offset + jitter)
		position.y = _terrain_height(position.x, position.z) + 0.12
		_add_rock("RiverBankRock%d" % i, position, 0.34 + float(i % 7) * 0.06, rock_mat, float(i) * 29.0)


func _add_rapid_rocks(rock_mat: Material) -> void:
	# Rocks sitting in the channel, breaking the surface through the rapids.
	var rapid_ts := PackedFloat32Array([0.30, 0.36, 0.42, 0.47, 0.52, 0.58, 0.63, 0.69, 0.74])
	for i in range(rapid_ts.size()):
		var t := rapid_ts[i]
		var center := _point_on_river(t)
		var side_vec := _river_side_at(t, -1.0 if i % 2 == 0 else 1.0)
		var lateral: float = _river_width_at(t) * (0.05 + 0.16 * float(i % 3))
		var position := center + side_vec * lateral
		var radius := 0.55 + float(i % 4) * 0.12
		# Sit on the bed and poke above the surface.
		position.y = water_y_at_t(t) - 0.25
		_add_rock("RiverRapidRock%d" % i, position, radius, rock_mat, float(i) * 41.0, true)


func _add_rock(rock_name: String, position: Vector3, radius: float, material: Material, yaw_degrees: float, tall := false) -> void:
	var rock := MeshInstance3D.new()
	rock.name = rock_name
	var sphere := SphereMesh.new()
	sphere.radius = radius
	sphere.height = radius * (1.7 if tall else 1.18)
	rock.mesh = sphere
	rock.position = position
	if tall:
		rock.scale = Vector3(1.0 + radius * 0.3, 1.1 + radius * 0.4, 0.85 + radius * 0.3)
	else:
		rock.scale = Vector3(1.0 + radius * 0.55, 0.42 + radius * 0.18, 0.70 + radius * 0.25)
	rock.rotation_degrees = Vector3(0.0, yaw_degrees, 0.0)
	rock.material_override = material
	add_child(rock)


func _add_drift_logs() -> void:
	var mat := _simple_mat(Color(0.32, 0.22, 0.13), 0.78)
	var log_specs: Array[Dictionary] = [
		{"t": 0.18, "side": -1.0, "angle": 22.0},
		{"t": 0.50, "side": 1.0, "angle": -28.0},
		{"t": 0.82, "side": -1.0, "angle": 36.0},
	]
	for i in range(log_specs.size()):
		var spec: Dictionary = log_specs[i]
		var t: float = float(spec["t"])
		var center := _point_on_river(t) + _river_side_at(t, float(spec["side"])) * (_river_width_at(t) * 0.5 + 1.1)
		center.y = _terrain_height(center.x, center.z) + 0.18
		var log_mesh := MeshInstance3D.new()
		log_mesh.name = "RiverDriftLog%d" % i
		var box := BoxMesh.new()
		box.size = Vector3(2.8, 0.18, 0.2)
		log_mesh.mesh = box
		log_mesh.position = center
		log_mesh.rotation_degrees = Vector3(0.0, rad_to_deg(atan2(_river_tangent_at(t).x, _river_tangent_at(t).z)) + float(spec["angle"]), 0.0)
		log_mesh.material_override = mat
		add_child(log_mesh)


# --- river water body ----------------------------------------------------------

func _add_river() -> void:
	_river = WATER_PATH_SCRIPT.new()
	_river.name = "DownhillRiver"
	_river.set("control_points", _river_control_points())
	_river.set("width_m", 5.2)
	_river.set("flow_speed_mps", 2.6)
	_river.set("point_widths_m", _river_widths)
	_river.set("point_depths_m", _river_depths)
	_river.set("point_flow_speeds_mps", _river_speeds)
	_river.set("point_bank_foam_strengths", _river_bank_foam)
	_river.set("point_turbulence_strengths", _river_turbulence)
	_river.set("subdivisions_per_segment", 22)
	_river.set("uv_m_per_tile", 4.6)
	_river.set("flow_field_resolution", 384)
	_river.set("flow_field_margin_m", 8.0)
	_river.set("flow_foam_strength", 1.0)
	_river.set("flow_normal_strength", 1.2)
	_river.set("debug_view", _debug_view)
	add_child(_river)


func _add_drifters() -> void:
	var mat := StandardMaterial3D.new()
	mat.albedo_color = Color(0.93, 0.55, 0.22)
	mat.roughness = 0.4
	for i in range(5):
		var drifter := MeshInstance3D.new()
		drifter.name = "FlowDrifter%d" % i
		var mesh := SphereMesh.new()
		mesh.radius = 0.34
		mesh.height = 0.68
		drifter.mesh = mesh
		drifter.material_override = mat
		add_child(drifter)
		_drifters.append(drifter)
		_drifter_offsets.append(float(i) * 0.16)


func _animate_camera() -> void:
	var t := _sim_time
	# Elevated 3/4 view from above the mouth, looking up the descending valley so the
	# whole winding stream, rapids and banks read at once.
	_camera.global_position = Vector3(44.0, 24.0, 13.0 + sin(t * 0.1) * 1.5)
	_camera.look_at(Vector3(-10.0, 7.0, -1.0), Vector3.UP)


func _animate_drifters(delta: float) -> void:
	if _river == null:
		return
	for i in range(_drifters.size()):
		var drifter := _drifters[i]
		if drifter.global_position == Vector3.ZERO:
			drifter.global_position = _point_on_river(_drifter_offsets[i])
		var flow := Vector3.ZERO
		if _river.has_method("get_flow_at"):
			flow = _river.call("get_flow_at", drifter.global_position) as Vector3
		if flow.length_squared() <= 0.0001:
			flow = _river_tangent_at(_t_at_x(drifter.global_position.x)) * 2.0
		drifter.global_position += flow * delta * 0.9
		var water_y := water_y_at_t(_t_at_x(drifter.global_position.x))
		if _river.has_method("get_surface_height_at"):
			water_y = float(_river.call("get_surface_height_at", drifter.global_position))
		drifter.global_position.y = water_y + 0.18 + sin(_sim_time * 4.0 + float(i)) * 0.03
		if drifter.global_position.x > X_MAX - 2.0:
			drifter.global_position = _point_on_river(0.02 + float(i) * 0.05)


# --- path sampling helpers (shared by terrain, banks, rocks, drifters) ----------

func _point_on_river(t: float) -> Vector3:
	var clamped_t: float = clamp(t, 0.0, 1.0)
	var x: float = lerp(X_MIN, X_MAX, clamped_t)
	return Vector3(x, water_y_at_t(clamped_t), center_z_at_t(clamped_t))


func _river_control_points() -> PackedVector3Array:
	var pts := PackedVector3Array()
	for i in range(6):
		var t := float(i) / 5.0
		pts.append(_point_on_river(t))
	return pts


func _river_width_at(t: float) -> float:
	return _width_at_t(t)


func _river_tangent_at(t: float) -> Vector3:
	var before := _point_on_river(maxf(t - 0.012, 0.0))
	var after := _point_on_river(minf(t + 0.012, 1.0))
	var tangent := after - before
	tangent.y = 0.0
	if tangent.length_squared() <= 0.0001:
		return Vector3.RIGHT
	return tangent.normalized()


func _river_side_at(t: float, side: float) -> Vector3:
	var tangent := _river_tangent_at(t)
	var side_vec := Vector3(-tangent.z, 0.0, tangent.x)
	if side_vec.length_squared() <= 0.0001:
		side_vec = Vector3.FORWARD
	return side_vec.normalized() * side


func _sample_river_float(values: PackedFloat32Array, t: float, fallback: float) -> float:
	if values.is_empty():
		return fallback
	var scaled: float = clampf(t, 0.0, 1.0) * float(values.size() - 1)
	var index := clampi(floori(scaled), 0, values.size() - 1)
	var next_index := clampi(index + 1, 0, values.size() - 1)
	return lerpf(values[index], values[next_index], scaled - float(index))


# --- capture / flow debug (unchanged infra) ------------------------------------

func _capture_after_frames() -> void:
	for _i in range(_capture_frames):
		await get_tree().process_frame
	var image := get_viewport().get_texture().get_image()
	if image == null:
		push_error("FastWaterRiverDemo: could not capture viewport image")
		get_tree().quit(1)
		return
	var dir := _screenshot_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(_screenshot_path)
	if err != OK:
		push_error("FastWaterRiverDemo: failed to save screenshot: %s err=%d" % [_screenshot_path, err])
		get_tree().quit(1)
		return
	print("FastWaterRiverDemo screenshot saved: " + _screenshot_path)
	if _flow_debug_path != "":
		save_flow_debug(_flow_debug_path)
	get_tree().quit(0)


func save_flow_debug(path: String) -> bool:
	if _river == null:
		return false
	var flow_field: Node = null
	if _has_property(_river, "flow_field_node"):
		flow_field = _river.get("flow_field_node") as Node
	if flow_field == null:
		flow_field = _river.get_node_or_null("FlowField")
	if flow_field == null:
		return false
	if flow_field.has_method("rebuild"):
		flow_field.call("rebuild")
	var tex := flow_field.get("texture") as Texture2D
	if tex == null:
		return false
	var image := tex.get_image()
	if image == null:
		return false
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	return image.save_png(global_path) == OK


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
