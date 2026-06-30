extends Node

## Optional autoload service ("FastWater") that wraps FastWaterBodyQuery with the
## scene tree, so gameplay/engine code can ask "what is the water doing here?" in one
## line without resolving nodes:
##   FastWater.height_at(pos), FastWater.depth_at(pos), FastWater.flow_at(pos),
##   FastWater.contains_point(pos), FastWater.nearest_body(pos), FastWater.query(pos)
## It owns nothing and holds no state -- every call resolves the current bodies, so it
## works with streamed/created/destroyed water. Registered as an autoload by plugin.gd;
## projects that prefer no autoload can call FastWaterBodyQuery statically instead.

const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")


## Best water body at a world position (nearest containing, else nearest by altitude
## when include_non_containing is true). Returns null if there is no water.
func nearest_body(world_position: Vector3, include_non_containing: bool = true) -> Node:
	return BODY_QUERY_SCRIPT.find_best_body(get_tree(), world_position, include_non_containing)


## Full query dictionary (valid, body, body_kind, height, altitude, depth, flow,
## contains, priority) for the best body at the position.
func query(world_position: Vector3, include_non_containing: bool = true) -> Dictionary:
	return BODY_QUERY_SCRIPT.find_best_body_query(get_tree(), world_position, include_non_containing)


## Water surface height (Y) at a world position, or -INF if there is no water there.
func height_at(world_position: Vector3) -> float:
	var body := nearest_body(world_position)
	return BODY_QUERY_SCRIPT.get_surface_height_at(body, world_position) if body != null else -INF


## Depth of water at/under a world position (0 when above the surface or no water).
func depth_at(world_position: Vector3) -> float:
	var body := nearest_body(world_position)
	return BODY_QUERY_SCRIPT.get_water_depth_at(body, world_position) if body != null else 0.0


## Current/flow vector at a world position (zero when no water or no flow field).
func flow_at(world_position: Vector3) -> Vector3:
	var body := nearest_body(world_position)
	return BODY_QUERY_SCRIPT.get_flow_at(body, world_position) if body != null else Vector3.ZERO


## True when the world point is inside water (below the surface within margin).
func contains_point(world_position: Vector3) -> bool:
	var result := query(world_position, false)
	return bool(result.get("contains", false))
