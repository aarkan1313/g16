@tool
extends Node3D
class_name FastWaterAuthoringOverlay

@export var enabled := true:
	set(value):
		enabled = value
		_request_rebuild()
@export var auto_discover := true:
	set(value):
		auto_discover = value
		_request_rebuild()
@export var path_paths: Array[NodePath] = []:
	set(value):
		path_paths = value
		_request_rebuild()
@export var waterfall_paths: Array[NodePath] = []:
	set(value):
		waterfall_paths = value
		_request_rebuild()
@export var foam_field_paths: Array[NodePath] = []:
	set(value):
		foam_field_paths = value
		_request_rebuild()
@export_range(0.5, 60.0, 0.5) var refresh_hz := 10.0

@export_category("Channels")
@export var show_path_controls := true:
	set(value):
		show_path_controls = value
		_request_rebuild()
@export var show_path_widths := true:
	set(value):
		show_path_widths = value
		_request_rebuild()
@export var show_flow_arrows := true:
	set(value):
		show_flow_arrows = value
		_request_rebuild()
@export var show_waterfall_lips := true:
	set(value):
		show_waterfall_lips = value
		_request_rebuild()
@export var show_foam_field_bounds := true:
	set(value):
		show_foam_field_bounds = value
		_request_rebuild()
@export var show_foam_source_samples := true:
	set(value):
		show_foam_source_samples = value
		_request_rebuild()

@export_category("Sampling")
@export_range(2, 64, 1) var max_path_samples := 16:
	set(value):
		max_path_samples = value
		_request_rebuild()
@export_range(2, 32, 1) var foam_sample_grid := 8:
	set(value):
		foam_sample_grid = value
		_request_rebuild()
@export_range(0.0, 1.0, 0.01) var foam_source_threshold := 0.08:
	set(value):
		foam_source_threshold = value
		_request_rebuild()
@export_range(0.0, 4.0, 0.01) var vertical_offset_m := 0.16:
	set(value):
		vertical_offset_m = value
		_request_rebuild()
@export_range(0.05, 8.0, 0.05) var flow_arrow_scale := 0.75:
	set(value):
		flow_arrow_scale = value
		_request_rebuild()

@export_category("Colors")
@export var path_color := Color(0.08, 0.78, 1.0, 0.95)
@export var width_color := Color(0.35, 1.0, 0.62, 0.86)
@export var flow_color := Color(1.0, 0.76, 0.16, 0.95)
@export var waterfall_color := Color(0.62, 0.92, 1.0, 0.95)
@export var foam_bounds_color := Color(0.78, 0.52, 1.0, 0.85)
@export var foam_source_color := Color(0.94, 0.96, 1.0, 0.92)

var _mesh_instance: MeshInstance3D
var _material: StandardMaterial3D
var _refresh_accum := 999.0
var _rebuild_requested := true
var _last_counts := {}


func _ready() -> void:
	add_to_group("fast_water_authoring_overlay")
	_ensure_nodes()
	_rebuild()


func _process(delta: float) -> void:
	if not enabled:
		if _mesh_instance != null:
			_mesh_instance.visible = false
		return

	_refresh_accum += delta
	var interval := 1.0 / maxf(refresh_hz, 0.001)
	if _rebuild_requested or _refresh_accum >= interval:
		_rebuild_requested = false
		_refresh_accum = 0.0
		_rebuild()


func rebuild_now() -> void:
	_rebuild_requested = false
	_refresh_accum = 0.0
	_rebuild()


func get_debug_state() -> Dictionary:
	return _last_counts.duplicate(true)


func _request_rebuild() -> void:
	_rebuild_requested = true


func _rebuild() -> void:
	_ensure_nodes()
	if _mesh_instance == null:
		return

	_mesh_instance.visible = enabled
	if not enabled:
		_mesh_instance.mesh = null
		_last_counts = {"enabled": false}
		return

	var vertices := PackedVector3Array()
	var colors := PackedColorArray()
	var counts := {
		"enabled": true,
		"path_count": 0,
		"waterfall_count": 0,
		"foam_field_count": 0,
		"path_control_lines": 0,
		"path_width_lines": 0,
		"flow_arrows": 0,
		"waterfall_lines": 0,
		"foam_bounds_lines": 0,
		"foam_source_crosses": 0,
	}

	for path in _get_paths():
		counts["path_count"] = int(counts["path_count"]) + 1
		_append_path_authoring(vertices, colors, path, counts)
	for waterfall in _get_waterfalls():
		counts["waterfall_count"] = int(counts["waterfall_count"]) + 1
		_append_waterfall_authoring(vertices, colors, waterfall, counts)
	for foam_field in _get_foam_fields():
		counts["foam_field_count"] = int(counts["foam_field_count"]) + 1
		_append_foam_field_authoring(vertices, colors, foam_field, counts)

	counts["line_vertex_count"] = vertices.size()
	_last_counts = counts
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
		_mesh_instance = get_node_or_null("AuthoringLines") as MeshInstance3D
	if _mesh_instance == null:
		_mesh_instance = MeshInstance3D.new()
		_mesh_instance.name = "AuthoringLines"
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


func _get_paths() -> Array[Node]:
	var nodes: Array[Node] = []
	if auto_discover and get_tree() != null:
		for node in get_tree().get_nodes_in_group("fast_water_path"):
			_append_unique(nodes, node)
	for path in path_paths:
		_append_unique(nodes, get_node_or_null(path))
	return nodes


func _get_waterfalls() -> Array[Node]:
	var nodes: Array[Node] = []
	if auto_discover and get_tree() != null:
		for node in get_tree().get_nodes_in_group("fast_water_waterfall"):
			_append_unique(nodes, node)
	for path in waterfall_paths:
		_append_unique(nodes, get_node_or_null(path))
	return nodes


func _get_foam_fields() -> Array[Node]:
	var nodes: Array[Node] = []
	if auto_discover and get_tree() != null:
		for node in get_tree().get_nodes_in_group("fast_water_foam_field"):
			_append_unique(nodes, node)
	for path in foam_field_paths:
		_append_unique(nodes, get_node_or_null(path))
	return nodes


func _append_unique(nodes: Array[Node], node: Node) -> void:
	if node != null and not nodes.has(node):
		nodes.append(node)


func _append_path_authoring(vertices: PackedVector3Array, colors: PackedColorArray, path: Node, counts: Dictionary) -> void:
	var points := _path_points(path)
	if points.size() < 2:
		return
	if show_path_controls:
		for i in range(points.size() - 1):
			_append_line(vertices, colors, points[i] + Vector3.UP * vertical_offset_m, points[i + 1] + Vector3.UP * vertical_offset_m, path_color)
			counts["path_control_lines"] = int(counts["path_control_lines"]) + 1
		for p in _control_points(path):
			_append_cross(vertices, colors, p + Vector3.UP * (vertical_offset_m + 0.05), 0.28, path_color)
			counts["path_control_lines"] = int(counts["path_control_lines"]) + 3
	if show_path_widths:
		_append_path_widths(vertices, colors, points, path, counts)
	if show_flow_arrows:
		_append_path_flow(vertices, colors, points, path, counts)


func _append_path_widths(vertices: PackedVector3Array, colors: PackedColorArray, points: PackedVector3Array, path: Node, counts: Dictionary) -> void:
	var widths := PackedFloat32Array()
	if path.has_method("get_sampled_widths"):
		widths = path.call("get_sampled_widths") as PackedFloat32Array
	var sample_indices := _sample_indices(points.size(), max_path_samples)
	for index in sample_indices:
		var tangent := _path_tangent(points, index)
		var side := Vector3(-tangent.z, 0.0, tangent.x).normalized()
		var width := _sample_float(widths, index, _read_float(path, "width_m", 4.0))
		var center := points[index] + Vector3.UP * (vertical_offset_m + 0.03)
		_append_line(vertices, colors, center - side * width * 0.5, center + side * width * 0.5, width_color)
		counts["path_width_lines"] = int(counts["path_width_lines"]) + 1


func _append_path_flow(vertices: PackedVector3Array, colors: PackedColorArray, points: PackedVector3Array, path: Node, counts: Dictionary) -> void:
	var sample_indices := _sample_indices(points.size(), max_path_samples)
	for index in sample_indices:
		var p := points[index]
		var flow := Vector3.ZERO
		if path.has_method("get_flow_at"):
			flow = path.call("get_flow_at", p) as Vector3
		flow.y = 0.0
		if flow.length_squared() <= 0.0001:
			flow = _path_tangent(points, index) * _read_float(path, "flow_speed_mps", 1.0)
		if flow.length_squared() <= 0.0001:
			continue
		var length: float = clampf(flow.length() * flow_arrow_scale, 0.25, 5.0)
		_append_arrow(vertices, colors, p + Vector3.UP * (vertical_offset_m + 0.09), flow.normalized() * length, flow_color)
		counts["flow_arrows"] = int(counts["flow_arrows"]) + 3


func _append_waterfall_authoring(vertices: PackedVector3Array, colors: PackedColorArray, waterfall: Node, counts: Dictionary) -> void:
	if not show_waterfall_lips:
		return
	var lip := _call_points(waterfall, "get_lip_world_points")
	var plunge := _call_points(waterfall, "get_plunge_world_points")
	var shelf := _call_points(waterfall, "get_shelf_world_points")
	_append_polyline(vertices, colors, lip, waterfall_color, counts, "waterfall_lines")
	_append_polyline(vertices, colors, plunge, waterfall_color * Color(1.0, 1.0, 1.0, 0.70), counts, "waterfall_lines")
	_append_polyline(vertices, colors, shelf, waterfall_color * Color(1.0, 0.92, 0.75, 0.85), counts, "waterfall_lines")
	var point_count := mini(lip.size(), plunge.size())
	for i in range(point_count):
		_append_line(vertices, colors, lip[i] + Vector3.UP * vertical_offset_m, plunge[i] + Vector3.UP * vertical_offset_m, waterfall_color * Color(1.0, 1.0, 1.0, 0.45))
		counts["waterfall_lines"] = int(counts["waterfall_lines"]) + 1


func _append_foam_field_authoring(vertices: PackedVector3Array, colors: PackedColorArray, foam_field: Node, counts: Dictionary) -> void:
	var origin := _read_vector2(foam_field, "origin_xz", Vector2.ZERO)
	var size := _read_float(foam_field, "world_size_m", 1.0)
	var half := size * 0.5
	var y := _node_y(foam_field) + vertical_offset_m
	if show_foam_field_bounds:
		var corners: Array[Vector3] = [
			Vector3(origin.x - half, y, origin.y - half),
			Vector3(origin.x + half, y, origin.y - half),
			Vector3(origin.x + half, y, origin.y + half),
			Vector3(origin.x - half, y, origin.y + half),
		]
		for i in range(4):
			_append_line(vertices, colors, corners[i], corners[(i + 1) % 4], foam_bounds_color)
			counts["foam_bounds_lines"] = int(counts["foam_bounds_lines"]) + 1
	if show_foam_source_samples and foam_field.has_method("sample_foam_at"):
		var grid := maxi(foam_sample_grid, 2)
		for z in range(grid):
			for x in range(grid):
				var u := float(x) / float(grid - 1)
				var v := float(z) / float(grid - 1)
				var p := Vector3(lerp(origin.x - half, origin.x + half, u), y + 0.06, lerp(origin.y - half, origin.y + half, v))
				var foam := float(foam_field.call("sample_foam_at", p))
				if foam < foam_source_threshold:
					continue
				_append_cross(vertices, colors, p, 0.18 + foam * 0.32, foam_source_color)
				counts["foam_source_crosses"] = int(counts["foam_source_crosses"]) + 3


func _path_points(path: Node) -> PackedVector3Array:
	if path.has_method("get_sampled_world_points"):
		return path.call("get_sampled_world_points") as PackedVector3Array
	return _control_points(path)


func _control_points(path: Node) -> PackedVector3Array:
	var points := PackedVector3Array()
	if not _has_property(path, "control_points"):
		return points
	var local_points: Variant = path.get("control_points")
	if not (local_points is PackedVector3Array):
		return points
	for p in local_points:
		points.append((path as Node3D).to_global(p) if path is Node3D else p)
	return points


func _call_points(node: Node, method_name: String) -> PackedVector3Array:
	if node != null and node.has_method(method_name):
		return node.call(method_name) as PackedVector3Array
	return PackedVector3Array()


func _append_polyline(vertices: PackedVector3Array, colors: PackedColorArray, points: PackedVector3Array, color: Color, counts: Dictionary, count_key: String) -> void:
	if points.size() < 2:
		return
	for i in range(points.size() - 1):
		_append_line(vertices, colors, points[i] + Vector3.UP * vertical_offset_m, points[i + 1] + Vector3.UP * vertical_offset_m, color)
		counts[count_key] = int(counts[count_key]) + 1


func _append_line(vertices: PackedVector3Array, colors: PackedColorArray, a: Vector3, b: Vector3, color: Color) -> void:
	vertices.append(a)
	vertices.append(b)
	colors.append(color)
	colors.append(color)


func _append_cross(vertices: PackedVector3Array, colors: PackedColorArray, center: Vector3, size: float, color: Color) -> void:
	_append_line(vertices, colors, center - Vector3.RIGHT * size, center + Vector3.RIGHT * size, color)
	_append_line(vertices, colors, center - Vector3.FORWARD * size, center + Vector3.FORWARD * size, color)
	_append_line(vertices, colors, center - Vector3.UP * size, center + Vector3.UP * size, color)


func _append_arrow(vertices: PackedVector3Array, colors: PackedColorArray, start: Vector3, direction: Vector3, color: Color) -> void:
	var end := start + direction
	_append_line(vertices, colors, start, end, color)
	var dir := direction.normalized()
	var side := Vector3(-dir.z, 0.0, dir.x).normalized()
	var head := clampf(direction.length() * 0.25, 0.12, 0.55)
	_append_line(vertices, colors, end, end - dir * head + side * head * 0.55, color)
	_append_line(vertices, colors, end, end - dir * head - side * head * 0.55, color)


func _sample_indices(count: int, max_count: int) -> PackedInt32Array:
	var result := PackedInt32Array()
	if count <= 0:
		return result
	var desired := mini(maxi(max_count, 1), count)
	if desired == 1:
		result.append(0)
		return result
	for i in range(desired):
		result.append(clampi(roundi(float(i) / float(desired - 1) * float(count - 1)), 0, count - 1))
	return result


func _path_tangent(points: PackedVector3Array, index: int) -> Vector3:
	if points.size() < 2:
		return Vector3.FORWARD
	var prev := points[maxi(index - 1, 0)]
	var next := points[mini(index + 1, points.size() - 1)]
	var tangent := next - prev
	tangent.y = 0.0
	return tangent.normalized() if tangent.length_squared() > 0.0001 else Vector3.FORWARD


func _sample_float(values: PackedFloat32Array, index: int, fallback: float) -> float:
	if values.is_empty():
		return fallback
	return values[clampi(index, 0, values.size() - 1)]


func _node_y(node: Node) -> float:
	if node is Node3D:
		return (node as Node3D).global_position.y
	var parent := node.get_parent()
	if parent is Node3D:
		return (parent as Node3D).global_position.y
	return global_position.y


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null and _has_property(object, property_name):
		return float(object.get(property_name))
	return fallback


func _read_vector2(object: Object, property_name: String, fallback: Vector2) -> Vector2:
	if object != null and _has_property(object, property_name):
		var value: Variant = object.get(property_name)
		if value is Vector2:
			return value
	return fallback


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
