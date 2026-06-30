extends Node
class_name FastWaterInteractor

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")

@export var water_surface: Node
@export var water_surface_path: NodePath
@export_range(0.01, 10.0, 0.01) var radius := 0.5
@export_range(0.1, 20.0, 0.01) var min_speed_for_splash := 1.5
@export_range(0.0, 20.0, 0.01) var min_speed_for_wake := 0.5
@export_range(0.01, 1.0, 0.01) var wake_interval_s := 0.06
@export_range(0.5, 8.0, 0.01) var surface_band_radius_scale := 1.5
@export_range(0.0, 1.0, 0.01) var entry_hysteresis_m := 0.02

var _previous_y := 0.0
var _last_position := Vector3.ZERO
var _has_last_position := false
var _wake_timer := 0.0


func _ready() -> void:
	_resolve_surface()
	var body := get_parent() as Node3D
	if body != null:
		_previous_y = body.global_position.y
		_last_position = body.global_position
		_has_last_position = true


func _physics_process(delta: float) -> void:
	if water_surface == null:
		_resolve_surface()
	if water_surface == null:
		return

	var body := get_parent() as Node3D
	if body == null:
		return

	var pos := body.global_position
	var velocity := _read_velocity(body, delta)
	var speed := velocity.length()
	var water_y: float = _surface_height(pos)

	if _previous_y > water_y + entry_hysteresis_m and pos.y <= water_y + entry_hysteresis_m:
		if speed >= min_speed_for_splash:
			_emit_splash(Vector3(pos.x, water_y, pos.z), velocity)

	_wake_timer += delta
	var near_surface: bool = abs(pos.y - water_y) <= radius * surface_band_radius_scale
	if near_surface and speed >= min_speed_for_wake and _wake_timer >= wake_interval_s:
		_wake_timer = 0.0
		_emit_wake(Vector3(pos.x, water_y, pos.z), velocity)

	_previous_y = pos.y
	_last_position = pos
	_has_last_position = true


func _resolve_surface() -> void:
	if water_surface_path != NodePath(""):
		water_surface = get_node_or_null(water_surface_path)
	if water_surface != null:
		return
	if get_tree() == null:
		return
	var body := get_parent() as Node3D
	var query_position := body.global_position if body != null else Vector3.ZERO
	water_surface = BODY_QUERY_SCRIPT.find_best_body(get_tree(), query_position, true)


func _surface_height(world_position: Vector3) -> float:
	if water_surface != null:
		return BODY_QUERY_SCRIPT.get_surface_height_at(water_surface, world_position)
	return 0.0


func _emit_splash(world_position: Vector3, velocity: Vector3) -> void:
	if water_surface != null and water_surface.has_method("add_splash"):
		water_surface.call("add_splash", world_position, velocity, radius)


func _emit_wake(world_position: Vector3, velocity: Vector3) -> void:
	if water_surface != null and water_surface.has_method("add_wake_point"):
		water_surface.call("add_wake_point", world_position, velocity, radius)


func _read_velocity(body: Node3D, delta: float) -> Vector3:
	if body is RigidBody3D:
		return (body as RigidBody3D).linear_velocity
	if body is CharacterBody3D:
		return (body as CharacterBody3D).velocity
	if _has_last_position and delta > 0.0:
		return (body.global_position - _last_position) / delta
	return Vector3.ZERO
