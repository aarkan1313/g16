@tool
extends Node3D
class_name FastWaterSwimmer

## Optional submersion helper for a CharacterBody3D (or any Node3D actor). Each
## physics frame it queries the water surface at the actor and exposes submersion
## state + signals, and can apply a gentle vertical float to a CharacterBody3D.
## Buoyancy for RigidBody3D lives in FastWaterBuoyant; this is the kinematic case.

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")

## Fired when the actor crosses the surface (in or out of water).
signal submerged_changed(submerged: bool)
## Fired once when the actor first goes under.
signal entered_water()
## Fired once when the actor surfaces.
signal exited_water()

@export var actor_path: NodePath
## Probe offset from the actor origin used for the surface test (e.g. chest height).
@export var probe_offset := Vector3.ZERO
## Apply a vertical float toward the surface when the actor is a CharacterBody3D.
@export var apply_float := true
@export_range(0.0, 40.0, 0.1) var float_speed_mps := 4.0
## How deep below the surface the float aims to settle (waterline offset).
@export_range(-4.0, 4.0, 0.01) var waterline_offset_m := 0.0

var is_submerged := false
var submersion_depth := 0.0

var _actor: Node3D


func _ready() -> void:
	_resolve_actor()


func _physics_process(delta: float) -> void:
	if Engine.is_editor_hint():
		return
	if _actor == null:
		_resolve_actor()
	if _actor == null or get_tree() == null:
		return

	var probe := _actor.global_position + probe_offset
	var body := BODY_QUERY_SCRIPT.find_best_body(get_tree(), probe, true)
	var surface_h := BODY_QUERY_SCRIPT.get_surface_height_at(body, probe) if body != null else -INF
	var was := is_submerged
	submersion_depth = maxf(surface_h - (probe.y + waterline_offset_m), 0.0)
	is_submerged = submersion_depth > 0.0

	if is_submerged != was:
		submerged_changed.emit(is_submerged)
		if is_submerged:
			entered_water.emit()
		else:
			exited_water.emit()

	if apply_float and is_submerged and _actor is CharacterBody3D:
		var cb := _actor as CharacterBody3D
		var target_y := surface_h - waterline_offset_m
		var to_surface := target_y - _actor.global_position.y
		cb.velocity.y = clampf(to_surface, -float_speed_mps, float_speed_mps)


func get_water_flow() -> Vector3:
	if _actor == null or get_tree() == null:
		return Vector3.ZERO
	var probe := _actor.global_position + probe_offset
	var body := BODY_QUERY_SCRIPT.find_best_body(get_tree(), probe, true)
	return BODY_QUERY_SCRIPT.get_flow_at(body, probe) if body != null else Vector3.ZERO


func _resolve_actor() -> void:
	if String(actor_path) != "":
		_actor = get_node_or_null(actor_path) as Node3D
	if _actor == null:
		_actor = get_parent() as Node3D
