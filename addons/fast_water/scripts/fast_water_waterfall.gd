@tool
extends MeshInstance3D
class_name FastWaterWaterfall

const SHEET_SHADER_PATH := "res://addons/fast_water/shaders/fast_waterfall_sheet.gdshader"
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const WATERFALL_SPRAY_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_waterfall_spray_fx.gd")

@export_category("Sheet")
@export var lip_points := PackedVector3Array([Vector3(-2.0, 0.0, 0.0), Vector3(2.0, 0.0, 0.0)]):
	set(value):
		lip_points = value
		if is_inside_tree():
			rebuild()
@export_range(0.25, 256.0, 0.05) var drop_height_m := 4.0:
	set(value):
		drop_height_m = maxf(value, 0.25)
		if is_inside_tree():
			rebuild()
@export_range(-128.0, 128.0, 0.05) var downstream_offset_m := 0.85:
	set(value):
		downstream_offset_m = value
		if is_inside_tree():
			rebuild()
@export var fall_direction_xz := Vector2(0.0, 1.0):
	set(value):
		fall_direction_xz = value
		if is_inside_tree():
			rebuild()
@export var shelf_points := PackedVector3Array([Vector3(0.0, -2.0, 0.45)]):
	set(value):
		shelf_points = value
		if is_inside_tree():
			_update_waterfall_spray_fx()
@export_range(1, 96, 1) var vertical_segments := 18:
	set(value):
		vertical_segments = maxi(value, 1)
		if is_inside_tree():
			rebuild()

@export_category("Material")
@export var sheet_material: ShaderMaterial:
	set(value):
		sheet_material = value
		if is_inside_tree():
			_apply_material()
@export var sheet_color := Color(0.58, 0.82, 0.92, 1.0):
	set(value):
		sheet_color = value
		_apply_material()
@export var foam_color := Color(0.92, 0.98, 1.0, 1.0):
	set(value):
		foam_color = value
		_apply_material()
@export_range(0.0, 1.0, 0.01) var sheet_alpha := 0.58:
	set(value):
		sheet_alpha = clampf(value, 0.0, 1.0)
		_apply_material()
@export_range(0.0, 4.0, 0.01) var flow_speed := 1.0:
	set(value):
		flow_speed = maxf(value, 0.0)
		_apply_material()
@export_range(1.0, 80.0, 0.1) var noise_scale := 18.0:
	set(value):
		noise_scale = maxf(value, 1.0)
		_apply_material()
@export_range(0.0, 1.0, 0.01) var breakup_strength := 0.36:
	set(value):
		breakup_strength = clampf(value, 0.0, 1.0)
		_apply_material()

@export_category("Foam And Spray")
@export var foam_field: Node
@export var foam_field_path: NodePath
@export var flow_field: Node
@export var flow_field_path: NodePath
@export var bubble_target: Node
@export var bubble_target_path: NodePath
@export var spray_target: Node
@export var spray_target_path: NodePath
@export var waterfall_spray_fx: Node
@export var waterfall_spray_fx_path: NodePath
@export var auto_create_waterfall_spray_fx := true
@export_range(0, 32, 1) var stamp_samples := 5
@export_range(0.0, 16.0, 0.01) var lip_foam_radius_m := 0.65
@export_range(0.0, 16.0, 0.01) var plunge_foam_radius_m := 1.35
@export_range(0.0, 1.0, 0.01) var lip_foam_intensity := 0.45
@export_range(0.0, 1.0, 0.01) var plunge_foam_intensity := 0.92
@export_range(0.0, 12.0, 0.01) var downstream_velocity_mps := 3.0
@export_range(0.0, 16.0, 0.01) var downstream_turbulence_radius_m := 1.35
@export_range(0.0, 48.0, 0.01) var downstream_turbulence_length_m := 3.2
@export_range(0.0, 1.0, 0.01) var downstream_turbulence_strength := 0.88
@export_range(0.0, 1.0, 0.01) var downstream_flow_foam := 0.78
@export_range(0.0, 1.0, 0.01) var bubble_strength := 0.70
@export_range(0.0, 1.0, 0.01) var spray_strength := 0.82
@export var trigger_effect_targets_on_stamp := true
@export var auto_stamp := false
@export_range(0.02, 10.0, 0.01) var stamp_interval_s := 0.35

@export_category("LOD And Culling")
@export var target_camera: Camera3D
@export var target_camera_path: NodePath
@export var lod_enabled := false
@export_range(0.0, 4096.0, 1.0) var far_lod_distance_m := 90.0
@export_range(0.0, 8192.0, 1.0) var max_visible_distance_m := 180.0
@export_range(1, 96, 1) var far_vertical_segments := 6
@export var disable_spray_in_far_lod := true

var last_stamp_count := 0
var last_plunge_center := Vector3.ZERO

var _stamp_accum := 0.0
var _near_vertical_segments := -1
var _lod_state := "near"


func _ready() -> void:
	add_to_group("fast_water_waterfall")
	_near_vertical_segments = vertical_segments
	_ensure_material()
	_ensure_waterfall_spray_fx()
	rebuild()


func _process(delta: float) -> void:
	if Engine.is_editor_hint():
		return
	update_lod()
	if auto_stamp:
		advance(delta)


func advance(delta: float) -> void:
	if not auto_stamp:
		return
	_stamp_accum += maxf(delta, 0.0)
	if _stamp_accum < stamp_interval_s:
		return
	_stamp_accum = 0.0
	stamp_waterfall_response()


func update_lod() -> String:
	if not lod_enabled:
		_apply_lod_state("near")
		return _lod_state
	var camera := _resolve_target_camera()
	if camera == null:
		_apply_lod_state("near")
		return _lod_state
	var distance := global_position.distance_to(camera.global_position)
	if max_visible_distance_m > 0.0 and distance > max_visible_distance_m:
		_apply_lod_state("culled")
	elif far_lod_distance_m > 0.0 and distance > far_lod_distance_m:
		_apply_lod_state("far")
	else:
		_apply_lod_state("near")
	return _lod_state


func get_lod_state() -> Dictionary:
	var camera := _resolve_target_camera()
	var camera_distance := -1.0
	if camera != null:
		camera_distance = global_position.distance_to(camera.global_position)
	return {
		"state": _lod_state,
		"visible": visible,
		"lod_enabled": lod_enabled,
		"camera_distance_m": camera_distance,
		"far_lod_distance_m": far_lod_distance_m,
		"max_visible_distance_m": max_visible_distance_m,
		"vertical_segments": vertical_segments,
		"near_vertical_segments": _near_vertical_segments,
		"far_vertical_segments": far_vertical_segments,
		"spray_enabled": _spray_fx_enabled(),
	}


func rebuild() -> void:
	if lip_points.size() < 2:
		mesh = null
		return

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()
	var distances := _lip_distances()
	var total_length: float = maxf(float(distances[distances.size() - 1]), 0.001)
	var fall_vector := _fall_vector_local()
	var row_count := vertical_segments + 1
	var point_count := lip_points.size()

	for row in range(row_count):
		var v: float = float(row) / float(vertical_segments)
		for point_index in range(point_count):
			var top := lip_points[point_index]
			var p := top + fall_vector * v
			var tangent := _lip_tangent(point_index)
			var normal := tangent.cross(fall_vector).normalized()
			if normal.length_squared() <= 0.0001:
				normal = Vector3.FORWARD
			verts.append(p)
			normals.append(normal)
			uvs.append(Vector2(float(distances[point_index]) / total_length, v))

	for row in range(vertical_segments):
		for point_index in range(point_count - 1):
			var a := row * point_count + point_index
			var b := a + 1
			var c := a + point_count
			var d := c + 1
			indices.append_array(PackedInt32Array([a, c, b, b, c, d]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices

	var sheet_mesh := ArrayMesh.new()
	sheet_mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh = sheet_mesh
	_apply_material()
	_update_waterfall_spray_fx()


func stamp_waterfall_response() -> int:
	last_stamp_count = 0
	var foam := _resolve_foam_field()
	var flow_field_node := _resolve_flow_field()
	var lip_world := get_lip_world_points()
	var shelf_world := get_shelf_world_points()
	var plunge_world := get_plunge_world_points()
	last_plunge_center = _average_points(plunge_world)
	var flow_velocity := _flow_velocity_world()

	if foam != null and foam.has_method("add_foam_stamp"):
		for p in _sample_polyline(lip_world, stamp_samples):
			foam.call("add_foam_stamp", p, lip_foam_radius_m, lip_foam_intensity, FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP, flow_velocity)
			last_stamp_count += 1
		for p in _sample_polyline(plunge_world, stamp_samples):
			foam.call("add_foam_stamp", p, plunge_foam_radius_m, plunge_foam_intensity, FOAM_FIELD_SCRIPT.SourceKind.PLUNGE_POOL, flow_velocity)
			last_stamp_count += 1

	if flow_field_node != null and flow_field_node.has_method("add_turbulence_stamp"):
		for p in _sample_polyline(plunge_world, stamp_samples):
			flow_field_node.call("add_turbulence_stamp", p, downstream_turbulence_radius_m, flow_velocity, downstream_flow_foam, downstream_turbulence_strength, downstream_turbulence_length_m)

	_update_waterfall_spray_fx(lip_world, shelf_world, plunge_world, flow_velocity)
	if trigger_effect_targets_on_stamp:
		_call_effect_target(_resolve_bubble_target(), last_plunge_center, bubble_strength, plunge_foam_radius_m)
		_call_effect_target(_resolve_spray_target(), last_plunge_center + Vector3.UP * 0.12, spray_strength, plunge_foam_radius_m)

	return last_stamp_count


func get_lip_world_points() -> PackedVector3Array:
	var points := PackedVector3Array()
	for p in lip_points:
		points.append(global_transform * p)
	return points


func get_plunge_world_points() -> PackedVector3Array:
	var points := PackedVector3Array()
	var fall_vector := _fall_vector_local()
	for p in lip_points:
		points.append(global_transform * (p + fall_vector))
	return points


func get_shelf_world_points() -> PackedVector3Array:
	var points := PackedVector3Array()
	for p in shelf_points:
		points.append(global_transform * p)
	return points


func get_lip_length_m() -> float:
	return _polyline_length(get_lip_world_points())


func get_sheet_length_m() -> float:
	return (global_transform.basis * _fall_vector_local()).length()


func get_plunge_center() -> Vector3:
	return _average_points(get_plunge_world_points())


func _ensure_material() -> void:
	if sheet_material != null:
		return
	sheet_material = ShaderMaterial.new()
	var shader := load(SHEET_SHADER_PATH) as Shader
	if shader != null:
		sheet_material.shader = shader
	_apply_material()


func _apply_material() -> void:
	if sheet_material == null:
		return
	sheet_material.set_shader_parameter("sheet_color", sheet_color)
	sheet_material.set_shader_parameter("foam_color", foam_color)
	sheet_material.set_shader_parameter("alpha", sheet_alpha)
	sheet_material.set_shader_parameter("flow_speed", flow_speed)
	sheet_material.set_shader_parameter("noise_scale", noise_scale)
	sheet_material.set_shader_parameter("breakup_strength", breakup_strength)
	material_override = sheet_material


func _ensure_waterfall_spray_fx() -> void:
	if not auto_create_waterfall_spray_fx or waterfall_spray_fx != null:
		return
	var existing := get_node_or_null("WaterfallSprayFx")
	if existing != null:
		waterfall_spray_fx = existing
		return
	var fx := WATERFALL_SPRAY_FX_SCRIPT.new()
	fx.name = "WaterfallSprayFx"
	add_child(fx)
	waterfall_spray_fx = fx


func _apply_lod_state(state: String) -> void:
	if _lod_state == state:
		return
	_lod_state = state
	match state:
		"culled":
			visible = false
			_set_waterfall_spray_enabled(false)
		"far":
			visible = true
			_set_vertical_segments_for_lod(far_vertical_segments)
			_set_waterfall_spray_enabled(not disable_spray_in_far_lod)
		_:
			visible = true
			_set_vertical_segments_for_lod(_near_lod_segments())
			_set_waterfall_spray_enabled(true)


func _set_vertical_segments_for_lod(segments: int) -> void:
	var target_segments := maxi(segments, 1)
	if vertical_segments == target_segments:
		return
	vertical_segments = target_segments


func _near_lod_segments() -> int:
	if _near_vertical_segments <= 0:
		_near_vertical_segments = vertical_segments
	return maxi(_near_vertical_segments, 1)


func _set_waterfall_spray_enabled(enabled: bool) -> void:
	var fx := _resolve_waterfall_spray_fx()
	if fx != null and _has_property(fx, "enabled"):
		fx.set("enabled", enabled)


func _spray_fx_enabled() -> bool:
	var fx := _resolve_waterfall_spray_fx()
	if fx == null or not _has_property(fx, "enabled"):
		return true
	return bool(fx.get("enabled"))


func _update_waterfall_spray_fx(lip_world: PackedVector3Array = PackedVector3Array(), shelf_world: PackedVector3Array = PackedVector3Array(), plunge_world: PackedVector3Array = PackedVector3Array(), flow_velocity: Vector3 = Vector3.ZERO) -> void:
	var fx := _resolve_waterfall_spray_fx()
	if fx == null or not fx.has_method("apply_waterfall"):
		return
	if lip_world.is_empty():
		lip_world = get_lip_world_points()
	if shelf_world.is_empty():
		shelf_world = get_shelf_world_points()
	if plunge_world.is_empty():
		plunge_world = get_plunge_world_points()
	fx.call("apply_waterfall", lip_world, shelf_world, plunge_world, flow_velocity, spray_strength)


func _fall_vector_local() -> Vector3:
	var dir := fall_direction_xz
	if dir.length_squared() <= 0.0001:
		dir = Vector2(0.0, 1.0)
	dir = dir.normalized()
	return Vector3(dir.x * downstream_offset_m, -drop_height_m, dir.y * downstream_offset_m)


func _lip_distances() -> PackedFloat32Array:
	var distances := PackedFloat32Array()
	var total := 0.0
	distances.append(total)
	for i in range(1, lip_points.size()):
		total += lip_points[i - 1].distance_to(lip_points[i])
		distances.append(total)
	return distances


func _lip_tangent(index: int) -> Vector3:
	if lip_points.size() < 2:
		return Vector3.RIGHT
	if index <= 0:
		return (lip_points[1] - lip_points[0]).normalized()
	if index >= lip_points.size() - 1:
		return (lip_points[index] - lip_points[index - 1]).normalized()
	return (lip_points[index + 1] - lip_points[index - 1]).normalized()


func _sample_polyline(points: PackedVector3Array, samples: int) -> Array[Vector3]:
	var result: Array[Vector3] = []
	if points.is_empty():
		return result
	if points.size() == 1 or samples <= 1:
		result.append(points[0])
		return result

	var total := _polyline_length(points)
	if total <= 0.001:
		result.append(points[0])
		return result
	var count := maxi(samples, 2)
	for i in range(count):
		var target_distance := total * float(i) / float(count - 1)
		result.append(_point_at_distance(points, target_distance))
	return result


func _point_at_distance(points: PackedVector3Array, distance_m: float) -> Vector3:
	var walked := 0.0
	for i in range(1, points.size()):
		var a := points[i - 1]
		var b := points[i]
		var segment_length := a.distance_to(b)
		if segment_length <= 0.001:
			continue
		if walked + segment_length >= distance_m:
			var t: float = clampf((distance_m - walked) / segment_length, 0.0, 1.0)
			return a.lerp(b, t)
		walked += segment_length
	return points[points.size() - 1]


func _polyline_length(points: PackedVector3Array) -> float:
	var total := 0.0
	for i in range(1, points.size()):
		total += points[i - 1].distance_to(points[i])
	return total


func _average_points(points: PackedVector3Array) -> Vector3:
	if points.is_empty():
		return global_position
	var total := Vector3.ZERO
	for p in points:
		total += p
	return total / float(points.size())


func _flow_velocity_world() -> Vector3:
	var dir := fall_direction_xz
	if dir.length_squared() <= 0.0001:
		dir = Vector2(0.0, 1.0)
	dir = dir.normalized()
	return global_transform.basis * Vector3(dir.x * downstream_velocity_mps, 0.0, dir.y * downstream_velocity_mps)


func _resolve_foam_field() -> Node:
	if foam_field != null:
		return foam_field
	if foam_field_path != NodePath("") and is_inside_tree():
		return get_node_or_null(foam_field_path)
	var parent := get_parent()
	if parent != null:
		return parent.get_node_or_null("FoamField")
	return null


func _resolve_flow_field() -> Node:
	if flow_field != null:
		return flow_field
	if flow_field_path != NodePath("") and is_inside_tree():
		return get_node_or_null(flow_field_path)
	var parent := get_parent()
	if parent != null:
		return parent.get_node_or_null("FlowField")
	return null


func _resolve_target_camera() -> Camera3D:
	if target_camera != null:
		return target_camera
	if target_camera_path != NodePath("") and is_inside_tree():
		var node := get_node_or_null(target_camera_path)
		if node is Camera3D:
			target_camera = node
			return target_camera
	var viewport := get_viewport()
	if viewport != null:
		target_camera = viewport.get_camera_3d()
	return target_camera


func _resolve_bubble_target() -> Node:
	if bubble_target != null:
		return bubble_target
	if bubble_target_path != NodePath("") and is_inside_tree():
		return get_node_or_null(bubble_target_path)
	return null


func _resolve_spray_target() -> Node:
	if spray_target != null:
		return spray_target
	if spray_target_path != NodePath("") and is_inside_tree():
		return get_node_or_null(spray_target_path)
	return null


func _resolve_waterfall_spray_fx() -> Node:
	if waterfall_spray_fx != null:
		return waterfall_spray_fx
	if waterfall_spray_fx_path != NodePath("") and is_inside_tree():
		return get_node_or_null(waterfall_spray_fx_path)
	if auto_create_waterfall_spray_fx and is_inside_tree():
		_ensure_waterfall_spray_fx()
		return waterfall_spray_fx
	return null


func _call_effect_target(target: Node, world_pos: Vector3, strength: float, radius_m: float) -> void:
	if target == null:
		return
	if target is Node3D:
		(target as Node3D).global_position = world_pos
	if target.has_method("burst"):
		target.call("burst", world_pos, strength, maxf(radius_m, 0.01))
	elif target.has_method("restart"):
		target.call("restart", strength, maxf(radius_m, 0.01))


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
