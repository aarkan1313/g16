@tool
extends Node3D
class_name FastWaterBuoyant

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")

@export var body_path: NodePath
@export var water_surface_path: NodePath
@export var water_surface_group := "fast_water_body"

@export_category("Probes")
@export var probe_offsets := PackedVector3Array([Vector3.ZERO])
@export_range(0.0, 2.0, 0.01) var waterline_offset_m := 0.0
@export_range(0.05, 10.0, 0.01) var max_probe_depth_m := 2.0

@export_category("Forces")
@export_range(0.0, 500.0, 0.1) var buoyancy_force := 58.0
@export_range(0.0, 80.0, 0.1) var flow_force := 12.0
@export_range(0.0, 30.0, 0.1) var linear_water_drag := 3.5
@export_range(0.0, 30.0, 0.1) var angular_water_drag := 3.0
@export_range(0.0, 200.0, 0.1) var upright_torque := 8.0

var _body: RigidBody3D
var _surface: Node


func _ready() -> void:
	_resolve_nodes()


func _physics_process(delta: float) -> void:
	if Engine.is_editor_hint():
		return
	if _body == null or _surface == null:
		_resolve_nodes()
	if _body == null or _surface == null:
		return

	var probes: int = max(probe_offsets.size(), 1)
	var submerged_count := 0
	var average_depth := 0.0
	var average_flow := Vector3.ZERO

	for offset in probe_offsets:
		var world_probe: Vector3 = global_transform * offset
		var altitude: float = _get_surface_altitude(world_probe) - waterline_offset_m
		if altitude < 0.0:
			var depth: float = clampf(-altitude, 0.0, max_probe_depth_m)
			var force: Vector3 = Vector3.UP * buoyancy_force * depth / float(probes)
			_body.apply_force(force, world_probe - _body.global_position)
			submerged_count += 1
			average_depth += depth
			average_flow += _get_surface_flow(world_probe)

	if submerged_count == 0:
		return

	average_depth /= float(submerged_count)
	average_flow /= float(submerged_count)
	_body.apply_central_force(average_flow * flow_force * average_depth)

	var drag: float = clampf(delta * linear_water_drag * average_depth, 0.0, 0.95)
	var angular_drag: float = clampf(delta * angular_water_drag * average_depth, 0.0, 0.95)
	_body.linear_velocity *= 1.0 - drag
	_body.angular_velocity *= 1.0 - angular_drag

	if upright_torque > 0.0:
		var up: Vector3 = _body.global_transform.basis.y.normalized()
		var axis: Vector3 = up.cross(Vector3.UP)
		if axis.length_squared() > 0.0001:
			_body.apply_torque(axis * upright_torque * average_depth)


func _resolve_nodes() -> void:
	_body = null
	_surface = null

	if String(body_path) != "":
		_body = get_node_or_null(body_path) as RigidBody3D
	if _body == null:
		_body = get_parent() as RigidBody3D

	if String(water_surface_path) != "":
		_surface = get_node_or_null(water_surface_path)
	if _surface == null and get_tree() != null:
		var query_position := _body.global_position if _body != null else global_position
		if water_surface_group == BODY_QUERY_SCRIPT.WATER_BODY_GROUP:
			_surface = BODY_QUERY_SCRIPT.find_best_body(get_tree(), query_position, true)
		else:
			var surfaces := get_tree().get_nodes_in_group(water_surface_group)
			if not surfaces.is_empty():
				_surface = surfaces[0]


func _get_surface_altitude(world_position: Vector3) -> float:
	if _surface == null:
		return INF
	return BODY_QUERY_SCRIPT.get_water_altitude(_surface, world_position)


func _get_surface_flow(world_position: Vector3) -> Vector3:
	if _surface != null:
		return BODY_QUERY_SCRIPT.get_flow_at(_surface, world_position)
	return Vector3.ZERO
