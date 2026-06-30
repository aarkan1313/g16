@tool
extends Node3D
class_name FastWaterBodyDebugOverlay

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")

@export var enabled := true:
	set(value):
		enabled = value
		_request_rebuild()
@export var auto_discover_bodies := true:
	set(value):
		auto_discover_bodies = value
		_request_rebuild()
@export var body_paths: Array[NodePath] = []:
	set(value):
		body_paths = value
		_request_rebuild()
@export_range(0.5, 60.0, 0.5) var refresh_hz := 12.0

@export_category("Channels")
@export var show_bounds := true:
	set(value):
		show_bounds = value
		_request_rebuild()
@export var show_flow_vectors := true:
	set(value):
		show_flow_vectors = value
		_request_rebuild()
@export var show_altitude_probes := true:
	set(value):
		show_altitude_probes = value
		_request_rebuild()

@export_category("Sampling")
@export_range(2, 24, 1) var flow_samples_per_body := 10:
	set(value):
		flow_samples_per_body = value
		_request_rebuild()
@export_range(0.05, 10.0, 0.05) var flow_vector_scale := 0.75:
	set(value):
		flow_vector_scale = value
		_request_rebuild()
@export_range(0.0, 4.0, 0.01) var vertical_offset_m := 0.08:
	set(value):
		vertical_offset_m = value
		_request_rebuild()
@export var probe_points := PackedVector3Array():
	set(value):
		probe_points = value
		_request_rebuild()

@export_category("Colors")
@export var bounds_color := Color(0.10, 0.75, 1.0, 0.92)
@export var flow_color := Color(1.0, 0.76, 0.18, 0.95)
@export var contained_probe_color := Color(0.24, 1.0, 0.40, 0.95)
@export var above_probe_color := Color(1.0, 0.20, 0.12, 0.95)
@export var nearest_probe_color := Color(0.72, 0.52, 1.0, 0.85)

var _mesh_instance: MeshInstance3D
var _material: StandardMaterial3D
var _refresh_accum := 999.0
var _rebuild_requested := true


func _ready() -> void:
	_ensure_nodes()
	_rebuild()


func _process(delta: float) -> void:
	if not enabled:
		if _mesh_instance != null:
			_mesh_instance.visible = false
		return

	_refresh_accum += delta
	var interval: float = 1.0 / maxf(refresh_hz, 0.001)
	if _rebuild_requested or _refresh_accum >= interval:
		_refresh_accum = 0.0
		_rebuild_requested = false
		_rebuild()


func _request_rebuild() -> void:
	_rebuild_requested = true


func _rebuild() -> void:
	_ensure_nodes()
	if _mesh_instance == null:
		return

	_mesh_instance.visible = enabled
	if not enabled:
		return

	var vertices := PackedVector3Array()
	var colors := PackedColorArray()
	var bodies := _get_bodies()
	for body in bodies:
		if show_bounds:
			_append_body_bounds(vertices, colors, body)
		if show_flow_vectors:
			_append_flow_vectors(vertices, colors, body)
	if show_altitude_probes:
		_append_altitude_probes(vertices, colors, bodies)

	if vertices.is_empty():
		_mesh_instance.mesh = null
		return

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = vertices
	arrays[Mesh.ARRAY_COLOR] = colors
	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_LINES, arrays)
	_mesh_instance.mesh = mesh


func _ensure_nodes() -> void:
	if _mesh_instance == null:
		_mesh_instance = get_node_or_null("DebugLines") as MeshInstance3D
	if _mesh_instance == null:
		_mesh_instance = MeshInstance3D.new()
		_mesh_instance.name = "DebugLines"
		_mesh_instance.top_level = true
		_mesh_instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		add_child(_mesh_instance)
	_mesh_instance.global_transform = Transform3D.IDENTITY

	if _material == null:
		_material = StandardMaterial3D.new()
		_material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
		_material.vertex_color_use_as_albedo = true
		_material.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
		_material.no_depth_test = true
		_material.albedo_color = Color.WHITE
	_mesh_instance.material_override = _material


func _get_bodies() -> Array[Node]:
	var bodies: Array[Node] = []
	if auto_discover_bodies and get_tree() != null:
		for body in BODY_QUERY_SCRIPT.get_water_bodies(get_tree()):
			if not bodies.has(body):
				bodies.append(body)
	for path in body_paths:
		var body := get_node_or_null(path)
		if body != null and not bodies.has(body):
			bodies.append(body)
	return bodies


func _append_body_bounds(vertices: PackedVector3Array, colors: PackedColorArray, body: Node) -> void:
	if body == null:
		return
	if body.has_method("get_sampled_world_points"):
		_append_path_bounds(vertices, colors, body)
		return
	if body is Node3D and _has_property(body, "mesh_size_m"):
		_append_surface_bounds(vertices, colors, body as Node3D)


func _append_surface_bounds(vertices: PackedVector3Array, colors: PackedColorArray, body: Node3D) -> void:
	var size: float = _read_float(body, "mesh_size_m", 16.0)
	var half: float = size * 0.5
	var y: float = BODY_QUERY_SCRIPT.get_surface_height_at(body, body.global_position) + vertical_offset_m
	var center: Vector3 = body.global_position
	var corners: Array[Vector3] = [
		Vector3(center.x - half, y, center.z - half),
		Vector3(center.x + half, y, center.z - half),
		Vector3(center.x + half, y, center.z + half),
		Vector3(center.x - half, y, center.z + half),
	]
	for i in range(4):
		_append_line(vertices, colors, corners[i], corners[(i + 1) % 4], bounds_color)
	_append_line(vertices, colors, Vector3(center.x - half, y, center.z), Vector3(center.x + half, y, center.z), bounds_color * Color(1.0, 1.0, 1.0, 0.55))
	_append_line(vertices, colors, Vector3(center.x, y, center.z - half), Vector3(center.x, y, center.z + half), bounds_color * Color(1.0, 1.0, 1.0, 0.55))


func _append_path_bounds(vertices: PackedVector3Array, colors: PackedColorArray, body: Node) -> void:
	var points := body.call("get_sampled_world_points") as PackedVector3Array
	if points.size() < 2:
		return
	var half_width: float = _read_float(body, "width_m", 2.0) * 0.5
	var left := PackedVector3Array()
	var right := PackedVector3Array()
	for i in range(points.size()):
		var tangent := _path_tangent(points, i)
		var side := Vector3(-tangent.z, 0.0, tangent.x).normalized()
		var p := points[i] + Vector3.UP * vertical_offset_m
		left.append(p + side * half_width)
		right.append(p - side * half_width)
	for i in range(points.size() - 1):
		_append_line(vertices, colors, left[i], left[i + 1], bounds_color)
		_append_line(vertices, colors, right[i], right[i + 1], bounds_color)
		_append_line(vertices, colors, points[i] + Vector3.UP * vertical_offset_m, points[i + 1] + Vector3.UP * vertical_offset_m, bounds_color * Color(1.0, 1.0, 1.0, 0.50))
	var stride: int = maxi(points.size() / 8, 1)
	for i in range(0, points.size(), stride):
		_append_line(vertices, colors, left[i], right[i], bounds_color * Color(1.0, 1.0, 1.0, 0.45))


func _append_flow_vectors(vertices: PackedVector3Array, colors: PackedColorArray, body: Node) -> void:
	var samples := _sample_points_for_body(body)
	for p in samples:
		var query := BODY_QUERY_SCRIPT.query_body(body, p)
		if not bool(query.get("valid", false)):
			continue
		var flow := query.get("flow", Vector3.ZERO) as Vector3
		flow.y = 0.0
		if flow.length_squared() <= 0.0001:
			continue
		var height := float(query.get("height", p.y))
		var start := Vector3(p.x, height + vertical_offset_m + 0.04, p.z)
		var length: float = clampf(flow.length() * flow_vector_scale, 0.18, 3.5)
		_append_arrow(vertices, colors, start, flow.normalized() * length, flow_color)


func _append_altitude_probes(vertices: PackedVector3Array, colors: PackedColorArray, bodies: Array[Node]) -> void:
	var points := probe_points
	if points.is_empty():
		points = _make_default_probe_points(bodies)
	for p in points:
		var query := BODY_QUERY_SCRIPT.find_best_body_query(get_tree(), p, true)
		if not bool(query.get("valid", false)):
			continue
		var height := float(query.get("height", p.y))
		var surface := Vector3(p.x, height + vertical_offset_m + 0.02, p.z)
		var color := contained_probe_color if bool(query.get("contains", false)) else above_probe_color
		if not bool(query.get("contains", false)) and float(query.get("altitude", INF)) > 0.0:
			color = nearest_probe_color
		_append_line(vertices, colors, p, surface, color)
		_append_cross(vertices, colors, p, 0.18, color)
		_append_cross(vertices, colors, surface, 0.26, color)


func _sample_points_for_body(body: Node) -> PackedVector3Array:
	if body == null:
		return PackedVector3Array()
	if body.has_method("get_sampled_world_points"):
		var points := body.call("get_sampled_world_points") as PackedVector3Array
		return _thin_points(points, flow_samples_per_body)
	if body is Node3D and _has_property(body, "mesh_size_m"):
		var center := (body as Node3D).global_position
		var size: float = minf(_read_float(body, "mesh_size_m", 16.0), 32.0)
		var half: float = size * 0.5
		var samples := PackedVector3Array()
		var grid: int = maxi(int(sqrt(float(flow_samples_per_body))), 2)
		for z in range(grid):
			for x in range(grid):
				var u: float = float(x) / float(maxi(grid - 1, 1))
				var v: float = float(z) / float(maxi(grid - 1, 1))
				samples.append(Vector3(lerp(center.x - half, center.x + half, u), center.y, lerp(center.z - half, center.z + half, v)))
		return samples
	return PackedVector3Array()


func _thin_points(points: PackedVector3Array, max_count: int) -> PackedVector3Array:
	if points.size() <= max_count:
		return points
	var thinned := PackedVector3Array()
	var last_index := points.size() - 1
	for i in range(max_count):
		var t: float = float(i) / float(maxi(max_count - 1, 1))
		thinned.append(points[roundi(t * float(last_index))])
	return thinned


func _make_default_probe_points(bodies: Array[Node]) -> PackedVector3Array:
	var points := PackedVector3Array()
	for body in bodies:
		if body is Node3D:
			var center := (body as Node3D).global_position
			var height := BODY_QUERY_SCRIPT.get_surface_height_at(body, center)
			if is_finite(height):
				points.append(Vector3(center.x, height + 1.0, center.z))
				points.append(Vector3(center.x, height - 0.7, center.z))
	return points


func _append_arrow(vertices: PackedVector3Array, colors: PackedColorArray, start: Vector3, vector: Vector3, color: Color) -> void:
	var end := start + vector
	_append_line(vertices, colors, start, end, color)
	var dir := vector.normalized()
	var side := Vector3(-dir.z, 0.0, dir.x).normalized()
	var back: float = minf(vector.length() * 0.28, 0.42)
	_append_line(vertices, colors, end, end - dir * back + side * back * 0.45, color)
	_append_line(vertices, colors, end, end - dir * back - side * back * 0.45, color)


func _append_cross(vertices: PackedVector3Array, colors: PackedColorArray, center: Vector3, size: float, color: Color) -> void:
	_append_line(vertices, colors, center + Vector3.LEFT * size, center + Vector3.RIGHT * size, color)
	_append_line(vertices, colors, center + Vector3.FORWARD * size, center + Vector3.BACK * size, color)
	_append_line(vertices, colors, center + Vector3.DOWN * size, center + Vector3.UP * size, color)


func _append_line(vertices: PackedVector3Array, colors: PackedColorArray, a: Vector3, b: Vector3, color: Color) -> void:
	vertices.append(a)
	vertices.append(b)
	colors.append(color)
	colors.append(color)


func _path_tangent(points: PackedVector3Array, index: int) -> Vector3:
	var before := points[maxi(index - 1, 0)]
	var after := points[mini(index + 1, points.size() - 1)]
	var tangent := after - before
	tangent.y = 0.0
	if tangent.length_squared() <= 0.0001:
		return Vector3.FORWARD
	return tangent.normalized()


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null and _has_property(object, property_name):
		return float(object.get(property_name))
	return fallback


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
