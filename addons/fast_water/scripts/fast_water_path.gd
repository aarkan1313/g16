@tool
extends MeshInstance3D
class_name FastWaterPath

const SHADER_PATH := "res://addons/fast_water/shaders/fast_water_surface.gdshader"
const FLOW_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_flow_field.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const MAX_SHADER_RIPPLES := 16

@export_category("Path")
@export var control_points := PackedVector3Array([
	Vector3(-12.0, 0.0, 0.0),
	Vector3(-4.0, 0.0, -3.0),
	Vector3(4.0, 0.0, 2.0),
	Vector3(12.0, 0.0, 0.0),
]):
	set(value):
		control_points = value
		_request_rebuild()

@export_range(0.25, 128.0, 0.05) var width_m := 8.0:
	set(value):
		width_m = value
		_request_rebuild()

@export_range(1, 64, 1) var subdivisions_per_segment := 8:
	set(value):
		subdivisions_per_segment = value
		_request_rebuild()

@export_range(0.25, 64.0, 0.05) var uv_m_per_tile := 6.0:
	set(value):
		uv_m_per_tile = value
		_request_rebuild()

@export_range(0.0, 12.0, 0.01) var flow_speed_mps := 1.4
@export var auto_rebuild_in_editor := true

@export_category("Per-Point Authoring")
@export var point_widths_m := PackedFloat32Array():
	set(value):
		point_widths_m = value
		_request_rebuild()
@export var point_depths_m := PackedFloat32Array():
	set(value):
		point_depths_m = value
		_request_rebuild()
@export var point_flow_speeds_mps := PackedFloat32Array():
	set(value):
		point_flow_speeds_mps = value
		_request_rebuild()
@export var point_bank_foam_strengths := PackedFloat32Array():
	set(value):
		point_bank_foam_strengths = value
		_request_rebuild()
@export var point_turbulence_strengths := PackedFloat32Array():
	set(value):
		point_turbulence_strengths = value
		_request_rebuild()

@export_category("Body Contract")
@export var body_profile: Resource

@export_category("Material")
@export var path_material: ShaderMaterial
@export var use_default_water_material := true:
	set(value):
		use_default_water_material = value
		_apply_material()
@export_range(0, 7, 1) var debug_view := 0:
	set(value):
		debug_view = clampi(value, 0, 7)
		_apply_material()

@export_category("Flow Field")
@export var auto_create_flow_field := true
@export var flow_field_node: Node
@export_range(16, 1024, 16) var flow_field_resolution := 256
@export_range(0.0, 128.0, 0.1) var flow_field_margin_m := 8.0
@export_range(0.0, 4.0, 0.01) var flow_field_strength := 1.0
@export_range(0.0, 4.0, 0.01) var flow_foam_strength := 1.0
@export_range(0.0, 4.0, 0.01) var flow_normal_strength := 1.0

@export_category("Interactions")
@export var interactions_enabled := true
@export_range(0, 16, 1) var max_hero_ripples := 8
@export_range(0.0, 3.0, 0.01) var splash_strength_scale := 0.11
@export_range(0.0, 3.0, 0.01) var wake_strength_scale := 0.035
@export_range(0.25, 8.0, 0.01) var ripple_lifetime_s := 1.65
@export_range(0.5, 16.0, 0.01) var ripple_speed_mps := 5.2

var _sampled_points := PackedVector3Array()
var _sampled_lengths := PackedFloat32Array()
var _sampled_widths := PackedFloat32Array()
var _sampled_depths := PackedFloat32Array()
var _sampled_flow_speeds := PackedFloat32Array()
var _sampled_bank_foam := PackedFloat32Array()
var _sampled_turbulence := PackedFloat32Array()
var _rebuild_pending := false
var _fallback_wake_map: ImageTexture
var _fallback_flow_map: ImageTexture
var _ripple_pos_time := PackedVector4Array()
var _ripple_shape := PackedVector4Array()
var _ripple_head := 0
var _active_ripple_count := 0
var _ripple_clock := 0.0


func _ready() -> void:
	add_to_group("fast_water_surface")
	add_to_group("fast_water_body")
	add_to_group("fast_water_path")
	_ensure_body_profile()
	_reset_ripple_arrays()
	rebuild_path_mesh()


func _process(delta: float) -> void:
	if _rebuild_pending and (not Engine.is_editor_hint() or auto_rebuild_in_editor):
		_rebuild_pending = false
		rebuild_path_mesh()
	_ripple_clock += delta
	if path_material != null:
		path_material.set_shader_parameter("hero_ripple_time", _ripple_clock)


func rebuild_path_mesh() -> void:
	_sample_path()
	_build_mesh()
	_apply_material()
	_ensure_flow_field()
	_bind_flow_field_to_material()
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF


func get_surface_height_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	if sample.get("valid", false):
		var p: Vector3 = sample["position"]
		return to_global(Vector3(p.x, p.y, p.z)).y
	return global_position.y


func get_water_altitude(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	if not sample.get("valid", false):
		return INF
	if float(sample["distance_xz"]) > float(sample.get("width_m", width_m)) * 0.5:
		return INF
	var p: Vector3 = sample["position"]
	var water_y := to_global(Vector3(p.x, p.y, p.z)).y
	return world_position.y - water_y


func get_water_depth_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	if not sample.get("valid", false):
		return 0.0
	if float(sample["distance_xz"]) > float(sample.get("width_m", width_m)) * 0.5:
		return 0.0
	var altitude := world_position.y - to_global(sample["position"] as Vector3).y
	return minf(maxf(-altitude, 0.0), maxf(float(sample.get("depth_m", _default_depth_m())), 0.0))


func contains_water_point(world_position: Vector3) -> bool:
	var altitude := get_water_altitude(world_position)
	if not is_finite(altitude):
		return false
	var margin := _read_profile_float(body_profile, "containment_margin_m", 0.05)
	return altitude <= margin


func get_flow_at(world_position: Vector3) -> Vector3:
	if flow_field_node != null and flow_field_node.has_method("sample_flow_at"):
		return flow_field_node.call("sample_flow_at", world_position) as Vector3
	var sample := _nearest_sample(to_local(world_position))
	if not sample.get("valid", false):
		return Vector3.ZERO
	if float(sample["distance_xz"]) > float(sample.get("width_m", width_m)) * 0.5:
		return Vector3.ZERO
	var tangent: Vector3 = sample["tangent"]
	var flow := global_transform.basis * tangent
	flow.y = 0.0
	if flow.length_squared() <= 0.0001:
		return Vector3.ZERO
	return flow.normalized() * maxf(float(sample.get("flow_speed_mps", flow_speed_mps)), 0.0)


func get_sampled_world_points() -> PackedVector3Array:
	var points := PackedVector3Array()
	for p in _sampled_points:
		points.append(to_global(p))
	return points


func get_sampled_lengths() -> PackedFloat32Array:
	return _sampled_lengths.duplicate()


func get_sampled_widths() -> PackedFloat32Array:
	return _sampled_widths.duplicate()


func get_sampled_depths() -> PackedFloat32Array:
	return _sampled_depths.duplicate()


func get_sampled_flow_speeds() -> PackedFloat32Array:
	return _sampled_flow_speeds.duplicate()


func get_sampled_bank_foam() -> PackedFloat32Array:
	return _sampled_bank_foam.duplicate()


func get_sampled_turbulence() -> PackedFloat32Array:
	return _sampled_turbulence.duplicate()


func get_channel_width_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	return float(sample.get("width_m", width_m)) if bool(sample.get("valid", false)) else width_m


func get_channel_depth_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	return float(sample.get("depth_m", _default_depth_m())) if bool(sample.get("valid", false)) else _default_depth_m()


func get_current_speed_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	return float(sample.get("flow_speed_mps", flow_speed_mps)) if bool(sample.get("valid", false)) else flow_speed_mps


func get_bank_foam_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	return float(sample.get("bank_foam", 1.0)) if bool(sample.get("valid", false)) else 1.0


func get_turbulence_at(world_position: Vector3) -> float:
	var sample := _nearest_sample(to_local(world_position))
	return float(sample.get("turbulence", 1.0)) if bool(sample.get("valid", false)) else 1.0


func add_splash(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if not interactions_enabled:
		return
	var speed := velocity.length()
	var strength: float = clampf(speed * splash_strength_scale, 0.12, 1.8)
	_add_hero_ripple(world_pos, strength, maxf(radius, 0.05), ripple_lifetime_s, ripple_speed_mps)


func add_wake_point(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if not interactions_enabled:
		return
	var speed := velocity.length()
	if speed <= 0.01:
		return
	var strength: float = clampf(speed * wake_strength_scale, 0.025, 0.55)
	_add_hero_ripple(world_pos, strength, maxf(radius * 0.55, 0.05), ripple_lifetime_s * 0.58, ripple_speed_mps * 0.75)


func _ensure_body_profile() -> void:
	if body_profile != null:
		return
	body_profile = BODY_PROFILE_SCRIPT.river()


func _request_rebuild() -> void:
	if not is_inside_tree():
		return
	_rebuild_pending = true


func _sample_path() -> void:
	_sampled_points.clear()
	_sampled_lengths.clear()
	_sampled_widths.clear()
	_sampled_depths.clear()
	_sampled_flow_speeds.clear()
	_sampled_bank_foam.clear()
	_sampled_turbulence.clear()

	if control_points.size() < 2:
		return

	var steps := max(subdivisions_per_segment, 1)
	var distance := 0.0
	var previous := _sample_curve(0, 0.0)

	for segment in range(control_points.size() - 1):
		for step in range(steps):
			var t := float(step) / float(steps)
			var p := _sample_curve(segment, t)
			if _sampled_points.is_empty():
				_append_sample(p, distance, segment, t)
			else:
				distance += previous.distance_to(p)
				_append_sample(p, distance, segment, t)
			previous = p

	var end_point := control_points[control_points.size() - 1]
	distance += previous.distance_to(end_point)
	_append_sample(end_point, distance, control_points.size() - 2, 1.0)


func _append_sample(point: Vector3, distance: float, segment: int, t: float) -> void:
	_sampled_points.append(point)
	_sampled_lengths.append(distance)
	_sampled_widths.append(maxf(_sample_control_float(point_widths_m, segment, t, width_m), 0.05))
	_sampled_depths.append(maxf(_sample_control_float(point_depths_m, segment, t, _default_depth_m()), 0.0))
	_sampled_flow_speeds.append(maxf(_sample_control_float(point_flow_speeds_mps, segment, t, flow_speed_mps), 0.0))
	_sampled_bank_foam.append(maxf(_sample_control_float(point_bank_foam_strengths, segment, t, 1.0), 0.0))
	_sampled_turbulence.append(maxf(_sample_control_float(point_turbulence_strengths, segment, t, 1.0), 0.0))


func _sample_curve(segment: int, t: float) -> Vector3:
	var i0 := max(segment - 1, 0)
	var i1 := segment
	var i2 := min(segment + 1, control_points.size() - 1)
	var i3 := min(segment + 2, control_points.size() - 1)
	var p0 := control_points[i0]
	var p1 := control_points[i1]
	var p2 := control_points[i2]
	var p3 := control_points[i3]
	var tt := t * t
	var ttt := tt * t
	return (
		(p1 * 2.0)
		+ (-p0 + p2) * t
		+ (p0 * 2.0 - p1 * 5.0 + p2 * 4.0 - p3) * tt
		+ (-p0 + p1 * 3.0 - p2 * 3.0 + p3) * ttt
	) * 0.5


func _build_mesh() -> void:
	if _sampled_points.size() < 2:
		mesh = null
		return

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)

	var verts := PackedVector3Array()
	var normals := PackedVector3Array()
	var uvs := PackedVector2Array()
	var uvs2 := PackedVector2Array()
	var indices := PackedInt32Array()
	for i in range(_sampled_points.size()):
		var p := _sampled_points[i]
		var tangent := _sample_tangent(i)
		var side := Vector3(-tangent.z, 0.0, tangent.x)
		if side.length_squared() <= 0.0001:
			side = Vector3.RIGHT
		side = side.normalized()
		var half_width: float = maxf(_sampled_float(_sampled_widths, i, width_m) * 0.5, 0.01)

		var v: float = float(_sampled_lengths[i]) / max(uv_m_per_tile, 0.001)
		verts.append(p - side * half_width)
		verts.append(p + side * half_width)
		normals.append(Vector3.UP)
		normals.append(Vector3.UP)
		uvs.append(Vector2(0.0, v))
		uvs.append(Vector2(1.0, v))
		uvs2.append(Vector2(0.0, _sampled_lengths[i]))
		uvs2.append(Vector2(1.0, _sampled_lengths[i]))

	for i in range(_sampled_points.size() - 1):
		var a0 := i * 2
		var a1 := a0 + 1
		var b0 := a0 + 2
		var b1 := a0 + 3
		indices.append_array(PackedInt32Array([a0, b0, a1, a1, b0, b1]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_NORMAL] = normals
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_TEX_UV2] = uvs2
	arrays[Mesh.ARRAY_INDEX] = indices

	var built_mesh := ArrayMesh.new()
	built_mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh = built_mesh


func _sample_tangent(index: int) -> Vector3:
	var before := _sampled_points[max(index - 1, 0)]
	var after := _sampled_points[min(index + 1, _sampled_points.size() - 1)]
	var tangent := after - before
	tangent.y = 0.0
	if tangent.length_squared() <= 0.0001:
		return Vector3.FORWARD
	return tangent.normalized()


func _nearest_sample(local_position: Vector3) -> Dictionary:
	if _sampled_points.size() < 2:
		return {"valid": false}

	var query := Vector2(local_position.x, local_position.z)
	var best_distance_sq := INF
	var best_position := _sampled_points[0]
	var best_tangent := _sample_tangent(0)
	var best_width := width_m
	var best_depth := _default_depth_m()
	var best_flow_speed := flow_speed_mps
	var best_bank_foam := 1.0
	var best_turbulence := 1.0

	for i in range(_sampled_points.size() - 1):
		var a := _sampled_points[i]
		var b := _sampled_points[i + 1]
		var a2 := Vector2(a.x, a.z)
		var b2 := Vector2(b.x, b.z)
		var ab := b2 - a2
		var segment_len_sq := ab.length_squared()
		var t := 0.0
		if segment_len_sq > 0.0001:
			t = clamp((query - a2).dot(ab) / segment_len_sq, 0.0, 1.0)
		var closest2 := a2 + ab * t
		var distance_sq := query.distance_squared_to(closest2)
		if distance_sq < best_distance_sq:
			best_distance_sq = distance_sq
			best_position = a.lerp(b, t)
			best_tangent = (b - a).normalized()
			best_width = lerpf(_sampled_float(_sampled_widths, i, width_m), _sampled_float(_sampled_widths, i + 1, width_m), t)
			best_depth = lerpf(_sampled_float(_sampled_depths, i, _default_depth_m()), _sampled_float(_sampled_depths, i + 1, _default_depth_m()), t)
			best_flow_speed = lerpf(_sampled_float(_sampled_flow_speeds, i, flow_speed_mps), _sampled_float(_sampled_flow_speeds, i + 1, flow_speed_mps), t)
			best_bank_foam = lerpf(_sampled_float(_sampled_bank_foam, i, 1.0), _sampled_float(_sampled_bank_foam, i + 1, 1.0), t)
			best_turbulence = lerpf(_sampled_float(_sampled_turbulence, i, 1.0), _sampled_float(_sampled_turbulence, i + 1, 1.0), t)

	return {
		"valid": true,
		"position": best_position,
		"tangent": best_tangent,
		"distance_xz": sqrt(best_distance_sq),
		"width_m": maxf(best_width, 0.05),
		"depth_m": maxf(best_depth, 0.0),
		"flow_speed_mps": maxf(best_flow_speed, 0.0),
		"bank_foam": maxf(best_bank_foam, 0.0),
		"turbulence": maxf(best_turbulence, 0.0),
	}


func _apply_material() -> void:
	if path_material == null and use_default_water_material:
		var shader := load(SHADER_PATH) as Shader
		if shader != null:
			path_material = ShaderMaterial.new()
			path_material.shader = shader
	if path_material != null:
		_ensure_fallback_textures()
		var wake_origin := Vector2(position.x, position.z)
		if is_inside_tree():
			wake_origin = Vector2(global_position.x, global_position.z)
		if use_default_water_material:
			_apply_default_path_visuals()
		path_material.set_shader_parameter("wake_map", _fallback_wake_map)
		path_material.set_shader_parameter("wake_map_origin_xz", wake_origin)
		path_material.set_shader_parameter("wake_map_world_size", 1.0)
		path_material.set_shader_parameter("flow_field_map", _fallback_flow_map)
		path_material.set_shader_parameter("flow_field_enabled", false)
		path_material.set_shader_parameter("debug_view", debug_view)
		path_material.set_shader_parameter("hero_ripple_time", _ripple_clock)
		_push_ripples()
	material_override = path_material


func _apply_default_path_visuals() -> void:
	if path_material == null:
		return
	path_material.set_shader_parameter("shallow_color", Color(0.004, 0.19, 0.25, 1.0))
	path_material.set_shader_parameter("deep_color", Color(0.0, 0.022, 0.070, 1.0))
	path_material.set_shader_parameter("foam_color", Color(0.88, 0.97, 1.0, 1.0))
	path_material.set_shader_parameter("water_alpha", 0.975)
	path_material.set_shader_parameter("wave_height", 0.072)
	path_material.set_shader_parameter("wave_frequency", 0.36)
	path_material.set_shader_parameter("wave_speed", 1.42)
	path_material.set_shader_parameter("normal_strength", 1.38)
	path_material.set_shader_parameter("micro_normal_strength", 0.78)
	path_material.set_shader_parameter("micro_normal_scale", 16.0)
	path_material.set_shader_parameter("detail_normal_strength", 0.34)
	path_material.set_shader_parameter("detail_normal_scale", 58.0)
	path_material.set_shader_parameter("reflection_color", Color(0.07, 0.22, 0.31, 1.0))
	path_material.set_shader_parameter("reflection_strength", 0.15)
	path_material.set_shader_parameter("grazing_reflection_strength", 0.28)
	path_material.set_shader_parameter("fresnel_strength", 0.52)
	path_material.set_shader_parameter("surface_gloss", 0.72)
	path_material.set_shader_parameter("sun_glint_strength", 0.42)
	path_material.set_shader_parameter("sun_glint_sharpness", 15.0)
	path_material.set_shader_parameter("foam_intensity", 0.68)
	path_material.set_shader_parameter("shoreline_foam_strength", 0.82)
	path_material.set_shader_parameter("wake_foam_strength", 0.70)
	path_material.set_shader_parameter("foam_breakup_strength", 0.92)
	path_material.set_shader_parameter("foam_noise_scale", 5.8)
	path_material.set_shader_parameter("depth_absorption_strength", 0.90)
	path_material.set_shader_parameter("absorption_density", 0.38)
	path_material.set_shader_parameter("foam_depth_m", 0.42)
	path_material.set_shader_parameter("deep_depth_m", 5.6)
	path_material.set_shader_parameter("final_color_gain", 0.98)


func _ensure_flow_field() -> void:
	if not auto_create_flow_field:
		return
	if flow_field_node == null:
		var existing := get_node_or_null("FlowField")
		if existing != null:
			flow_field_node = existing
	if flow_field_node == null:
		flow_field_node = FLOW_FIELD_SCRIPT.new()
		flow_field_node.name = "FlowField"
		add_child(flow_field_node)
		if Engine.is_editor_hint() and get_tree() != null:
			flow_field_node.owner = get_tree().edited_scene_root
	if _has_property(flow_field_node, "source_path"):
		flow_field_node.set("source_path", flow_field_node.get_path_to(self))
	if _has_property(flow_field_node, "resolution"):
		flow_field_node.set("resolution", flow_field_resolution)
	if _has_property(flow_field_node, "path_margin_m"):
		flow_field_node.set("path_margin_m", flow_field_margin_m)
	if _has_property(flow_field_node, "default_flow_speed_mps"):
		flow_field_node.set("default_flow_speed_mps", flow_speed_mps)
	if flow_field_node.has_method("rebuild"):
		flow_field_node.call("rebuild", flow_field_resolution)


func _bind_flow_field_to_material() -> void:
	if path_material == null:
		return
	_ensure_fallback_textures()
	if flow_field_node != null and flow_field_node.has_method("bind_to_material"):
		flow_field_node.call("bind_to_material", path_material, flow_field_strength, flow_foam_strength, flow_normal_strength)
		path_material.set_shader_parameter("flow_advection_strength", 0.36)
		path_material.set_shader_parameter("debug_view", debug_view)
		return
	path_material.set_shader_parameter("flow_field_map", _fallback_flow_map)
	path_material.set_shader_parameter("flow_field_enabled", false)
	path_material.set_shader_parameter("debug_view", debug_view)


func _ensure_fallback_textures() -> void:
	if _fallback_wake_map == null:
		var wake_image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
		wake_image.set_pixel(0, 0, Color(0.5, 0.0, 0.5, 0.5))
		_fallback_wake_map = ImageTexture.create_from_image(wake_image)
	if _fallback_flow_map == null:
		var flow_image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
		flow_image.set_pixel(0, 0, Color(0.5, 0.5, 0.0, 0.0))
		_fallback_flow_map = ImageTexture.create_from_image(flow_image)


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false


func _read_profile_float(profile: Resource, property_name: String, fallback: float) -> float:
	if profile != null and _has_property(profile, property_name):
		return float(profile.get(property_name))
	return fallback


func _default_depth_m() -> float:
	return _read_profile_float(body_profile, "default_depth_m", 1.2)


func _sample_control_float(values: PackedFloat32Array, segment: int, t: float, fallback: float) -> float:
	if values.is_empty():
		return fallback
	var last_index := values.size() - 1
	var a := values[clampi(segment, 0, last_index)]
	var b := values[clampi(segment + 1, 0, last_index)]
	return lerpf(a, b, clampf(t, 0.0, 1.0))


func _sampled_float(values: PackedFloat32Array, index: int, fallback: float) -> float:
	if values.is_empty():
		return fallback
	return values[clampi(index, 0, values.size() - 1)]


func _reset_ripple_arrays() -> void:
	_ripple_pos_time.clear()
	_ripple_shape.clear()
	_ripple_pos_time.resize(MAX_SHADER_RIPPLES)
	_ripple_shape.resize(MAX_SHADER_RIPPLES)
	for i in range(MAX_SHADER_RIPPLES):
		_ripple_pos_time[i] = Vector4(0.0, 0.0, -9999.0, 0.0)
		_ripple_shape[i] = Vector4(0.5, 4.0, 0.1, 0.0)
	_ripple_head = 0
	_active_ripple_count = 0


func _add_hero_ripple(world_pos: Vector3, strength: float, radius: float, lifetime: float, speed: float) -> void:
	if path_material == null:
		_apply_material()
	if path_material == null:
		return
	if _ripple_pos_time.size() != MAX_SHADER_RIPPLES or _ripple_shape.size() != MAX_SHADER_RIPPLES:
		_reset_ripple_arrays()

	var now := _ripple_clock
	_ripple_pos_time[_ripple_head] = Vector4(world_pos.x, world_pos.z, now, strength)
	_ripple_shape[_ripple_head] = Vector4(radius, speed, lifetime, 0.0)
	_ripple_head = (_ripple_head + 1) % MAX_SHADER_RIPPLES
	_active_ripple_count = mini(_active_ripple_count + 1, mini(max_hero_ripples, MAX_SHADER_RIPPLES))
	_push_ripples()


func _push_ripples() -> void:
	if path_material == null:
		return
	if _ripple_pos_time.size() != MAX_SHADER_RIPPLES or _ripple_shape.size() != MAX_SHADER_RIPPLES:
		_reset_ripple_arrays()
	path_material.set_shader_parameter("hero_ripples", _ripple_pos_time)
	path_material.set_shader_parameter("hero_ripple_data", _ripple_shape)
	path_material.set_shader_parameter("hero_ripple_count", mini(max_hero_ripples, _active_ripple_count))
