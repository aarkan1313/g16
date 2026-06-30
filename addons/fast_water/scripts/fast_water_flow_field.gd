@tool
extends Node
class_name FastWaterFlowField

@export_range(16, 2048, 16) var resolution := 256:
	set(value):
		resolution = max(16, value)
		_request_rebuild()

@export_range(1.0, 2048.0, 1.0) var world_size_m := 96.0:
	set(value):
		world_size_m = max(value, 1.0)
		_request_rebuild()

@export var origin_xz := Vector2.ZERO:
	set(value):
		origin_xz = value
		_request_rebuild()

@export_range(0.1, 64.0, 0.1) var flow_encode_scale_mps := 8.0:
	set(value):
		flow_encode_scale_mps = max(value, 0.1)
		_request_rebuild()

@export_category("Path Bake")
@export var source_path: NodePath:
	set(value):
		source_path = value
		_request_rebuild()

@export var auto_bake_from_path := true:
	set(value):
		auto_bake_from_path = value
		_request_rebuild()

@export var auto_bounds_from_path := true:
	set(value):
		auto_bounds_from_path = value
		_request_rebuild()

@export_range(0.0, 128.0, 0.1) var path_margin_m := 8.0:
	set(value):
		path_margin_m = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 16.0, 0.01) var default_flow_speed_mps := 1.4:
	set(value):
		default_flow_speed_mps = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 3.0, 0.01) var slope_speed_boost := 0.8:
	set(value):
		slope_speed_boost = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 2.0, 0.01) var bank_foam_strength := 0.82:
	set(value):
		bank_foam_strength = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 2.0, 0.01) var slope_foam_strength := 0.55:
	set(value):
		slope_foam_strength = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 2.0, 0.01) var turbulence_strength := 0.18:
	set(value):
		turbulence_strength = max(value, 0.0)
		_request_rebuild()

@export_range(0.05, 16.0, 0.05) var edge_feather_m := 1.2:
	set(value):
		edge_feather_m = max(value, 0.05)
		_request_rebuild()

@export_category("Uniform Fallback")
@export var uniform_direction_xz := Vector2(0.0, -1.0):
	set(value):
		uniform_direction_xz = value
		_request_rebuild()

@export_range(0.0, 16.0, 0.01) var uniform_speed_mps := 0.0:
	set(value):
		uniform_speed_mps = max(value, 0.0)
		_request_rebuild()

@export_range(0.0, 1.0, 0.01) var uniform_foam := 0.0:
	set(value):
		uniform_foam = clamp(value, 0.0, 1.0)
		_request_rebuild()

var texture: ImageTexture

var _image: Image
var _rebuild_pending := false
var _is_rebuilding := false


func _ready() -> void:
	add_to_group("fast_water_flow_field")
	rebuild()


func _process(_delta: float) -> void:
	if _rebuild_pending:
		_rebuild_pending = false
		rebuild()


func rebuild(new_resolution: int = -1) -> void:
	_is_rebuilding = true
	if new_resolution > 0:
		resolution = max(16, new_resolution)

	_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	var path := _resolve_source_path()
	if auto_bake_from_path and path != null:
		bake_from_path(path)
	else:
		fill_uniform_flow(uniform_direction_xz, uniform_speed_mps, uniform_foam)
	_is_rebuilding = false


func bake_from_path(path: Node) -> void:
	if _image == null:
		_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)

	var points := _read_path_points(path)
	if points.size() < 2:
		fill_uniform_flow(uniform_direction_xz, uniform_speed_mps, uniform_foam)
		return

	var width_m: float = _read_float(path, "width_m", 8.0)
	var flow_speed_mps: float = _read_float(path, "flow_speed_mps", default_flow_speed_mps)
	var widths := _read_path_float_samples(path, "get_sampled_widths", points.size(), width_m)
	var flow_speeds := _read_path_float_samples(path, "get_sampled_flow_speeds", points.size(), flow_speed_mps)
	var bank_foam_values := _read_path_float_samples(path, "get_sampled_bank_foam", points.size(), 1.0)
	var turbulence_values := _read_path_float_samples(path, "get_sampled_turbulence", points.size(), 1.0)
	var max_half_width: float = maxf(_max_float(widths, width_m) * 0.5, 0.05)
	if auto_bounds_from_path:
		_fit_bounds_to_points(points, max_half_width + path_margin_m)

	for y in range(resolution):
		for x in range(resolution):
			var uv := Vector2(float(x) / float(resolution - 1), float(y) / float(resolution - 1))
			var world_xz := origin_xz + (uv - Vector2(0.5, 0.5)) * world_size_m
			var nearest := _nearest_segment(world_xz, points, widths, flow_speeds, bank_foam_values, turbulence_values)
			var distance_m := float(nearest["distance"])
			var half_width: float = maxf(float(nearest.get("width_m", width_m)) * 0.5, 0.05)
			var mask := 1.0 - _smoothstep(half_width, half_width + edge_feather_m, distance_m)
			if mask <= 0.001:
				_image.set_pixel(x, y, Color(0.5, 0.5, 0.0, 0.0))
				continue

			var tangent_xz: Vector2 = nearest["tangent_xz"]
			var slope := float(nearest["slope"])
			var foam_bank := _smoothstep(half_width * 0.55, half_width, distance_m) * bank_foam_strength * float(nearest.get("bank_foam", 1.0))
			var foam_slope := clamp(slope * slope_foam_strength * 5.0, 0.0, 1.0)
			var turbulence := (_hash21(world_xz * 0.37) - 0.5) * turbulence_strength * float(nearest.get("turbulence", 1.0)) * mask
			var flow_dir := tangent_xz.rotated(turbulence).normalized()
			var flow_speed: float = float(nearest.get("flow_speed_mps", flow_speed_mps)) * (1.0 + clamp(slope * slope_speed_boost * 4.0, 0.0, 2.0))
			var encoded := _encode_flow(flow_dir * flow_speed)
			var foam := clamp(max(foam_bank, foam_slope) * mask, 0.0, 1.0)
			_image.set_pixel(x, y, Color(encoded.x, encoded.y, foam, mask))

	_update_texture()


func fill_uniform_flow(direction_xz: Vector2, speed_mps: float, foam: float = 0.0) -> void:
	if _image == null:
		_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	var dir := direction_xz
	if dir.length_squared() <= 0.0001 or speed_mps <= 0.001:
		for y in range(resolution):
			for x in range(resolution):
				_image.set_pixel(x, y, Color(0.5, 0.5, 0.0, 0.0))
	else:
		dir = dir.normalized()
		var encoded := _encode_flow(dir * speed_mps)
		for y in range(resolution):
			for x in range(resolution):
				_image.set_pixel(x, y, Color(encoded.x, encoded.y, clamp(foam, 0.0, 1.0), 1.0))
	_update_texture()


func bind_to_material(material: ShaderMaterial, strength: float = 1.0, foam_strength: float = 1.0, normal_strength: float = 1.0) -> void:
	if material == null:
		return
	if texture == null:
		rebuild()
	material.set_shader_parameter("flow_field_enabled", texture != null)
	material.set_shader_parameter("flow_field_map", texture)
	material.set_shader_parameter("flow_field_origin_xz", origin_xz)
	material.set_shader_parameter("flow_field_world_size", max(world_size_m, 0.01))
	material.set_shader_parameter("flow_field_strength", strength)
	material.set_shader_parameter("flow_foam_strength", foam_strength)
	material.set_shader_parameter("flow_normal_strength", normal_strength)
	material.set_shader_parameter("flow_encode_scale_mps", flow_encode_scale_mps)


func sample_flow_at(world_position: Vector3) -> Vector3:
	if _image == null:
		return Vector3.ZERO
	var uv := _world_to_uv(Vector2(world_position.x, world_position.z))
	if uv.x < 0.0 or uv.x > 1.0 or uv.y < 0.0 or uv.y > 1.0:
		return Vector3.ZERO
	var px := clampi(int(round(uv.x * float(resolution - 1))), 0, resolution - 1)
	var py := clampi(int(round(uv.y * float(resolution - 1))), 0, resolution - 1)
	var c := _image.get_pixel(px, py)
	var flow := _decode_flow(Vector2(c.r, c.g)) * c.a
	return Vector3(flow.x, 0.0, flow.y)


func sample_foam_at(world_position: Vector3) -> float:
	if _image == null:
		return 0.0
	var uv := _world_to_uv(Vector2(world_position.x, world_position.z))
	if uv.x < 0.0 or uv.x > 1.0 or uv.y < 0.0 or uv.y > 1.0:
		return 0.0
	var px := clampi(int(round(uv.x * float(resolution - 1))), 0, resolution - 1)
	var py := clampi(int(round(uv.y * float(resolution - 1))), 0, resolution - 1)
	var c := _image.get_pixel(px, py)
	return c.b * c.a


func get_turbulence_at(world_position: Vector3) -> float:
	return sample_foam_at(world_position)


func add_turbulence_stamp(world_pos: Vector3, radius_m: float, flow_velocity: Vector3 = Vector3.ZERO, foam: float = 1.0, turbulence: float = 1.0, downstream_length_m: float = 0.0) -> void:
	if _image == null:
		rebuild()
	if radius_m <= 0.001 or turbulence <= 0.001:
		return

	var flow_xz := Vector2(flow_velocity.x, flow_velocity.z)
	var speed := flow_xz.length()
	var dir := flow_xz.normalized() if speed > 0.001 else Vector2(0.0, 1.0)
	var side := Vector2(-dir.y, dir.x)
	var footprint_length: float = maxf(downstream_length_m, radius_m)
	var footprint_radius: float = maxf(radius_m, 0.001)
	var center_xz := Vector2(world_pos.x, world_pos.z)
	var bounds_radius := footprint_length + footprint_radius
	var min_uv := _world_to_uv(center_xz - Vector2.ONE * bounds_radius)
	var max_uv := _world_to_uv(center_xz + Vector2.ONE * bounds_radius)
	var min_x: int = clampi(floori(min_uv.x * float(resolution - 1)), 0, resolution - 1)
	var max_x: int = clampi(ceili(max_uv.x * float(resolution - 1)), 0, resolution - 1)
	var min_y: int = clampi(floori(min_uv.y * float(resolution - 1)), 0, resolution - 1)
	var max_y: int = clampi(ceili(max_uv.y * float(resolution - 1)), 0, resolution - 1)
	var encoded := _encode_flow(flow_xz)
	var stamp_foam := clampf(foam, 0.0, 1.0)
	var stamp_turbulence := clampf(turbulence, 0.0, 1.0)

	for y in range(min_y, max_y + 1):
		for x in range(min_x, max_x + 1):
			var pixel_xz := _pixel_to_world_xz(x, y)
			var delta := pixel_xz - center_xz
			var along := delta.dot(dir)
			var across := abs(delta.dot(side))
			var along_distance := 0.0
			if along < 0.0:
				along_distance = abs(along)
			elif along > footprint_length:
				along_distance = along - footprint_length
			var normalized_distance := sqrt(pow(across / footprint_radius, 2.0) + pow(along_distance / footprint_radius, 2.0))
			if normalized_distance > 1.0:
				continue
			var falloff: float = (1.0 - _smoothstep(0.20, 1.0, normalized_distance)) * stamp_turbulence
			var c := _image.get_pixel(x, y)
			c.r = lerpf(c.r, encoded.x, falloff)
			c.g = lerpf(c.g, encoded.y, falloff)
			c.b = clampf(maxf(c.b, stamp_foam * falloff), 0.0, 1.0)
			c.a = clampf(maxf(c.a, falloff), 0.0, 1.0)
			_image.set_pixel(x, y, c)
	_update_texture()


func export_image(path: String) -> int:
	if _image == null:
		rebuild()
	if _image == null:
		return ERR_UNCONFIGURED
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	return _image.save_png(global_path)


func import_image(path: String, imported_origin_xz: Vector2 = Vector2.INF, imported_world_size_m: float = -1.0, imported_encode_scale_mps: float = -1.0) -> int:
	var image := Image.new()
	var err := image.load(ProjectSettings.globalize_path(path))
	if err != OK:
		return err
	if image.get_width() <= 0 or image.get_height() <= 0:
		return ERR_INVALID_DATA
	if image.get_width() != image.get_height():
		return ERR_INVALID_DATA
	image.convert(Image.FORMAT_RGBA8)
	resolution = image.get_width()
	_image = image
	if imported_origin_xz != Vector2.INF:
		origin_xz = imported_origin_xz
	if imported_world_size_m > 0.0:
		world_size_m = imported_world_size_m
	if imported_encode_scale_mps > 0.0:
		flow_encode_scale_mps = imported_encode_scale_mps
	auto_bake_from_path = false
	_rebuild_pending = false
	_update_texture()
	return OK


func _resolve_source_path() -> Node:
	if source_path != NodePath("") and is_inside_tree():
		return get_node_or_null(source_path)
	var parent := get_parent()
	if parent != null and parent.has_method("get_sampled_world_points"):
		return parent
	return null


func _read_path_points(path: Node) -> PackedVector3Array:
	if path.has_method("get_sampled_world_points"):
		return path.call("get_sampled_world_points") as PackedVector3Array
	var points := PackedVector3Array()
	var control_points: Variant = path.get("control_points") if _has_property(path, "control_points") else null
	if control_points is PackedVector3Array:
		for p in control_points:
			if path is Node3D:
				points.append((path as Node3D).to_global(p))
			else:
				points.append(p)
	return points


func _fit_bounds_to_points(points: PackedVector3Array, margin_m: float) -> void:
	var min_x := INF
	var min_z := INF
	var max_x := -INF
	var max_z := -INF
	for p in points:
		min_x = min(min_x, p.x)
		min_z = min(min_z, p.z)
		max_x = max(max_x, p.x)
		max_z = max(max_z, p.z)
	min_x -= margin_m
	min_z -= margin_m
	max_x += margin_m
	max_z += margin_m
	origin_xz = Vector2((min_x + max_x) * 0.5, (min_z + max_z) * 0.5)
	world_size_m = max(max(max_x - min_x, max_z - min_z), 1.0)


func _nearest_segment(
	world_xz: Vector2,
	points: PackedVector3Array,
	widths: PackedFloat32Array,
	flow_speeds: PackedFloat32Array,
	bank_foam_values: PackedFloat32Array,
	turbulence_values: PackedFloat32Array
) -> Dictionary:
	var best_distance_sq: float = INF
	var best_tangent_xz := Vector2(0.0, -1.0)
	var best_slope := 0.0
	var best_width := 8.0
	var best_flow_speed := default_flow_speed_mps
	var best_bank_foam := 1.0
	var best_turbulence := 1.0

	for i in range(points.size() - 1):
		var a: Vector3 = points[i]
		var b: Vector3 = points[i + 1]
		var a2 := Vector2(a.x, a.z)
		var b2 := Vector2(b.x, b.z)
		var ab := b2 - a2
		var len_sq: float = ab.length_squared()
		if len_sq <= 0.0001:
			continue
		var t: float = clampf((world_xz - a2).dot(ab) / len_sq, 0.0, 1.0)
		var closest: Vector2 = a2 + ab * t
		var distance_sq: float = world_xz.distance_squared_to(closest)
		if distance_sq < best_distance_sq:
			best_distance_sq = distance_sq
			best_tangent_xz = ab.normalized()
			var horizontal_len := sqrt(len_sq)
			best_slope = abs(b.y - a.y) / max(horizontal_len, 0.001)
			best_width = lerpf(_sampled_float(widths, i, best_width), _sampled_float(widths, i + 1, best_width), t)
			best_flow_speed = lerpf(_sampled_float(flow_speeds, i, best_flow_speed), _sampled_float(flow_speeds, i + 1, best_flow_speed), t)
			best_bank_foam = lerpf(_sampled_float(bank_foam_values, i, best_bank_foam), _sampled_float(bank_foam_values, i + 1, best_bank_foam), t)
			best_turbulence = lerpf(_sampled_float(turbulence_values, i, best_turbulence), _sampled_float(turbulence_values, i + 1, best_turbulence), t)

	return {
		"distance": sqrt(best_distance_sq),
		"tangent_xz": best_tangent_xz,
		"slope": best_slope,
		"width_m": maxf(best_width, 0.05),
		"flow_speed_mps": maxf(best_flow_speed, 0.0),
		"bank_foam": maxf(best_bank_foam, 0.0),
		"turbulence": maxf(best_turbulence, 0.0),
	}


func _world_to_uv(world_xz: Vector2) -> Vector2:
	return (world_xz - origin_xz) / max(world_size_m, 0.001) + Vector2(0.5, 0.5)


func _pixel_to_world_xz(x: int, y: int) -> Vector2:
	var uv := Vector2(float(x) / float(resolution - 1), float(y) / float(resolution - 1))
	return origin_xz + (uv - Vector2(0.5, 0.5)) * max(world_size_m, 0.001)


func _encode_flow(flow: Vector2) -> Vector2:
	var scale := max(flow_encode_scale_mps, 0.001)
	return Vector2(
		clamp(flow.x / scale * 0.5 + 0.5, 0.0, 1.0),
		clamp(flow.y / scale * 0.5 + 0.5, 0.0, 1.0)
	)


func _decode_flow(encoded: Vector2) -> Vector2:
	return (encoded * 2.0 - Vector2.ONE) * max(flow_encode_scale_mps, 0.001)


func _update_texture() -> void:
	if _image == null:
		return
	if texture == null or texture.get_width() != _image.get_width() or texture.get_height() != _image.get_height():
		texture = ImageTexture.create_from_image(_image)
	else:
		texture.update(_image)


func _request_rebuild() -> void:
	if _is_rebuilding:
		return
	if not is_inside_tree():
		return
	_rebuild_pending = true


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null and _has_property(object, property_name):
		return float(object.get(property_name))
	return fallback


func _read_path_float_samples(path: Node, method_name: String, expected_size: int, fallback: float) -> PackedFloat32Array:
	var values := PackedFloat32Array()
	if path != null and path.has_method(method_name):
		values = path.call(method_name) as PackedFloat32Array
	if values.is_empty():
		for _i in range(expected_size):
			values.append(fallback)
		return values
	if values.size() >= expected_size:
		return values
	var last := values[values.size() - 1]
	while values.size() < expected_size:
		values.append(last)
	return values


func _max_float(values: PackedFloat32Array, fallback: float) -> float:
	if values.is_empty():
		return fallback
	var result := values[0]
	for value in values:
		result = maxf(result, value)
	return result


func _sampled_float(values: PackedFloat32Array, index: int, fallback: float) -> float:
	if values.is_empty():
		return fallback
	return values[clampi(index, 0, values.size() - 1)]


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false


func _smoothstep(edge0: float, edge1: float, value: float) -> float:
	var span := edge1 - edge0
	if abs(span) <= 0.0001:
		return 0.0
	var t := clamp((value - edge0) / span, 0.0, 1.0)
	return t * t * (3.0 - 2.0 * t)


func _hash21(p: Vector2) -> float:
	var q := Vector2(_fract(p.x * 123.34), _fract(p.y * 456.21))
	q += Vector2.ONE * q.dot(q + Vector2(45.32, 45.32))
	return _fract(q.x * q.y)


func _fract(value: float) -> float:
	return value - floor(value)
