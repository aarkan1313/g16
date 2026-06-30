@tool
extends Area3D
class_name FastWaterVolume

## Optional gameplay trigger for "is this actor in water". Author a CollisionShape3D
## child to define the region; bodies/areas that overlap raise water-specific signals.
## Stays modular: it only re-emits Area3D overlaps as water events and answers depth
## queries through FastWaterBodyQuery. It does not move, render, or own water.

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")

## A physics body entered the water volume.
signal body_entered_water(body: Node3D)
## A physics body left the water volume.
signal body_exited_water(body: Node3D)

## Optional: query water depth/height against registered Fast Water bodies, not just
## this trigger box. Lets the volume report real surface height inside the region.
@export var query_against_water_bodies := true


func _ready() -> void:
	add_to_group("fast_water_volume")
	if Engine.is_editor_hint():
		return
	if not body_entered.is_connected(_on_body_entered):
		body_entered.connect(_on_body_entered)
	if not body_exited.is_connected(_on_body_exited):
		body_exited.connect(_on_body_exited)


func _on_body_entered(body: Node3D) -> void:
	body_entered_water.emit(body)


func _on_body_exited(body: Node3D) -> void:
	body_exited_water.emit(body)


## Water surface height at a world position. Uses the best Fast Water body when
## query_against_water_bodies is on; otherwise falls back to this volume's Y.
func get_surface_height_at(world_position: Vector3) -> float:
	if query_against_water_bodies and get_tree() != null:
		var body := BODY_QUERY_SCRIPT.find_best_body(get_tree(), world_position, true)
		if body != null:
			return BODY_QUERY_SCRIPT.get_surface_height_at(body, world_position)
	return global_position.y


## Submersion depth (>0 when the point is below the water surface).
func get_submersion_depth(world_position: Vector3) -> float:
	return maxf(get_surface_height_at(world_position) - world_position.y, 0.0)


## True when the world point is below the water surface.
func is_point_submerged(world_position: Vector3) -> bool:
	return world_position.y < get_surface_height_at(world_position)
