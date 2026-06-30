@tool
extends Node
class_name FastWaterFoamReactiveFx

const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")

@export var enabled := true
@export var foam_field: Node:
	set(value):
		foam_field = value
		if is_inside_tree():
			call_deferred("refresh_connections")
@export var foam_field_path: NodePath:
	set(value):
		foam_field_path = value
		if is_inside_tree():
			call_deferred("refresh_connections")

@export_category("Effect Targets")
@export var bubble_pool: Node
@export var bubble_pool_path: NodePath
@export var spray_fx: Node
@export var spray_fx_path: NodePath

@export_category("Budget")
@export_range(0, 128, 1) var max_events_per_frame := 8
@export_range(0.0, 2.0, 0.01) var velocity_strength_gain := 0.08

@export_category("Bubbles")
@export_range(0.0, 2.0, 0.01) var bubble_min_intensity := 0.08
@export_range(0.0, 4.0, 0.01) var bubble_radius_scale := 0.72
@export_range(0.0, 4.0, 0.01) var wake_bubble_gain := 0.60
@export_range(0.0, 4.0, 0.01) var impact_bubble_gain := 1.00
@export_range(0.0, 4.0, 0.01) var rain_bubble_gain := 0.32
@export_range(0.0, 4.0, 0.01) var rapids_bubble_gain := 0.85
@export_range(0.0, 4.0, 0.01) var plunge_bubble_gain := 1.10
@export_range(-2.0, 2.0, 0.01) var bubble_y_offset_m := -0.05

@export_category("Spray")
@export_range(0.0, 2.0, 0.01) var spray_min_intensity := 0.20
@export_range(0.0, 4.0, 0.01) var spray_radius_scale := 0.52
@export_range(0.0, 4.0, 0.01) var impact_spray_gain := 0.75
@export_range(0.0, 4.0, 0.01) var rapids_spray_gain := 0.55
@export_range(0.0, 4.0, 0.01) var waterfall_spray_gain := 1.20
@export_range(0.0, 4.0, 0.01) var plunge_spray_gain := 1.05
@export_range(-2.0, 2.0, 0.01) var spray_y_offset_m := 0.08

var _connected_foam_field: Node
var _budget_frame := -1
var _events_this_frame := 0


func _ready() -> void:
	refresh_connections()


func _exit_tree() -> void:
	_disconnect_foam_field()


func refresh_connections() -> void:
	_connect_foam_field(_resolve_foam_field())


func _on_foam_source_added(world_pos: Vector3, radius_m: float, intensity: float, source_kind: int, flow_velocity: Vector3) -> void:
	if not enabled:
		return
	if not _consume_event_budget():
		return

	var speed_gain: float = 1.0 + flow_velocity.length() * velocity_strength_gain
	var bubble_strength: float = clampf(intensity * _bubble_gain_for_source(source_kind) * speed_gain, 0.0, 1.0)
	if bubble_strength >= bubble_min_intensity:
		_trigger_bubbles(world_pos + Vector3(0.0, bubble_y_offset_m, 0.0), bubble_strength, radius_m * bubble_radius_scale)

	var spray_strength: float = clampf(intensity * _spray_gain_for_source(source_kind) * speed_gain, 0.0, 1.0)
	if spray_strength >= spray_min_intensity:
		_trigger_spray(world_pos + Vector3(0.0, spray_y_offset_m, 0.0), spray_strength, radius_m * spray_radius_scale)


func _trigger_bubbles(world_pos: Vector3, strength: float, radius_m: float) -> void:
	var target := _resolve_bubble_pool()
	if target == null:
		return
	_call_effect_target(target, world_pos, strength, radius_m)


func _trigger_spray(world_pos: Vector3, strength: float, radius_m: float) -> void:
	var target := _resolve_spray_fx()
	if target == null:
		return
	_call_effect_target(target, world_pos, strength, radius_m)


func _call_effect_target(target: Node, world_pos: Vector3, strength: float, radius_m: float) -> void:
	if target is Node3D:
		(target as Node3D).global_position = world_pos
	if target.has_method("burst"):
		target.call("burst", world_pos, strength, maxf(radius_m, 0.01))
	elif target.has_method("restart"):
		target.call("restart", strength, maxf(radius_m, 0.01))


func _bubble_gain_for_source(source_kind: int) -> float:
	match source_kind:
		FOAM_FIELD_SCRIPT.SourceKind.WAKE:
			return wake_bubble_gain
		FOAM_FIELD_SCRIPT.SourceKind.IMPACT:
			return impact_bubble_gain
		FOAM_FIELD_SCRIPT.SourceKind.RAIN:
			return rain_bubble_gain
		FOAM_FIELD_SCRIPT.SourceKind.RAPIDS:
			return rapids_bubble_gain
		FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP:
			return plunge_bubble_gain
		FOAM_FIELD_SCRIPT.SourceKind.PLUNGE_POOL:
			return plunge_bubble_gain
		_:
			return 0.0


func _spray_gain_for_source(source_kind: int) -> float:
	match source_kind:
		FOAM_FIELD_SCRIPT.SourceKind.IMPACT:
			return impact_spray_gain
		FOAM_FIELD_SCRIPT.SourceKind.RAPIDS:
			return rapids_spray_gain
		FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP:
			return waterfall_spray_gain
		FOAM_FIELD_SCRIPT.SourceKind.PLUNGE_POOL:
			return plunge_spray_gain
		_:
			return 0.0


func _consume_event_budget() -> bool:
	if max_events_per_frame <= 0:
		return false
	var frame := Engine.get_process_frames()
	if frame != _budget_frame:
		_budget_frame = frame
		_events_this_frame = 0
	if _events_this_frame >= max_events_per_frame:
		return false
	_events_this_frame += 1
	return true


func _connect_foam_field(node: Node) -> void:
	if _connected_foam_field == node:
		return
	_disconnect_foam_field()
	if node == null or not node.has_signal("foam_source_added"):
		return
	var callback := Callable(self, "_on_foam_source_added")
	if not node.is_connected("foam_source_added", callback):
		node.connect("foam_source_added", callback)
	_connected_foam_field = node


func _disconnect_foam_field() -> void:
	if _connected_foam_field == null:
		return
	var callback := Callable(self, "_on_foam_source_added")
	if _connected_foam_field.is_connected("foam_source_added", callback):
		_connected_foam_field.disconnect("foam_source_added", callback)
	_connected_foam_field = null


func _resolve_foam_field() -> Node:
	if foam_field != null:
		return foam_field
	if foam_field_path != NodePath("") and is_inside_tree():
		var path_node := get_node_or_null(foam_field_path)
		if path_node != null:
			return path_node
	var parent := get_parent()
	if parent != null:
		return parent.get_node_or_null("FoamField")
	return null


func _resolve_bubble_pool() -> Node:
	if bubble_pool != null:
		return bubble_pool
	if bubble_pool_path != NodePath("") and is_inside_tree():
		return get_node_or_null(bubble_pool_path)
	return get_node_or_null("../BubblePool") if is_inside_tree() else null


func _resolve_spray_fx() -> Node:
	if spray_fx != null:
		return spray_fx
	if spray_fx_path != NodePath("") and is_inside_tree():
		return get_node_or_null(spray_fx_path)
	return get_node_or_null("../SprayFx") if is_inside_tree() else null
