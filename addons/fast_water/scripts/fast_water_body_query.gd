extends RefCounted
class_name FastWaterBodyQuery

const WATER_BODY_GROUP := "fast_water_body"
const LEGACY_WATER_GROUP := "fast_water_surface"


static func query_body(body: Node, world_position: Vector3) -> Dictionary:
	if body == null:
		return {"valid": false}

	var altitude := get_water_altitude(body, world_position)
	var has_altitude := is_finite(altitude)
	var profile := get_body_profile(body)
	var margin := _read_profile_float(profile, "containment_margin_m", 0.05)
	var enabled := _read_profile_bool(profile, "enabled", true)
	var contains := false
	if enabled and has_altitude:
		if body.has_method("contains_water_point"):
			contains = bool(body.call("contains_water_point", world_position))
		else:
			contains = altitude <= margin

	var flow := get_flow_at(body, world_position)
	var flow_scale := _read_profile_float(profile, "flow_strength_scale", 1.0)
	return {
		"valid": enabled and has_altitude,
		"body": body,
		"profile": profile,
		"body_kind": body_kind_name(body),
		"priority": get_query_priority(body),
		"height": get_surface_height_at(body, world_position),
		"altitude": altitude,
		"depth": get_water_depth_at(body, world_position),
		"flow": flow * flow_scale,
		"contains": contains,
		"distance_score": abs(altitude) if has_altitude else INF,
	}


static func find_best_body(tree: SceneTree, world_position: Vector3, include_non_containing: bool = false) -> Node:
	var result := find_best_body_query(tree, world_position, include_non_containing)
	return result.get("body", null) as Node


static func find_best_body_query(tree: SceneTree, world_position: Vector3, include_non_containing: bool = false) -> Dictionary:
	var best := {"valid": false}
	for body in get_water_bodies(tree):
		var query := query_body(body, world_position)
		if not bool(query.get("valid", false)):
			continue
		if not include_non_containing and not bool(query.get("contains", false)):
			continue
		if not bool(best.get("valid", false)) or _is_better_query(query, best):
			best = query
	return best


static func get_water_bodies(tree: SceneTree) -> Array[Node]:
	var bodies: Array[Node] = []
	if tree == null:
		return bodies
	_append_unique_nodes(bodies, tree.get_nodes_in_group(WATER_BODY_GROUP))
	_append_unique_nodes(bodies, tree.get_nodes_in_group(LEGACY_WATER_GROUP))
	return bodies


static func get_surface_height_at(body: Node, world_position: Vector3) -> float:
	if body != null and body.has_method("get_surface_height_at"):
		return float(body.call("get_surface_height_at", world_position))
	return INF


static func get_water_altitude(body: Node, world_position: Vector3) -> float:
	if body == null:
		return INF
	if body.has_method("get_water_altitude"):
		return float(body.call("get_water_altitude", world_position))
	if body.has_method("get_surface_height_at"):
		return world_position.y - float(body.call("get_surface_height_at", world_position))
	return INF


static func get_water_depth_at(body: Node, world_position: Vector3) -> float:
	if body == null:
		return 0.0
	if body.has_method("get_water_depth_at"):
		return float(body.call("get_water_depth_at", world_position))
	var altitude := get_water_altitude(body, world_position)
	if not is_finite(altitude):
		return 0.0
	return max(-altitude, 0.0)


static func get_flow_at(body: Node, world_position: Vector3) -> Vector3:
	if body != null and body.has_method("get_flow_at"):
		return body.call("get_flow_at", world_position) as Vector3
	return Vector3.ZERO


static func contains_water_point(body: Node, world_position: Vector3) -> bool:
	return bool(query_body(body, world_position).get("contains", false))


static func get_body_profile(body: Node) -> Resource:
	if _has_property(body, "body_profile"):
		return body.get("body_profile") as Resource
	return null


static func get_query_priority(body: Node) -> int:
	var profile := get_body_profile(body)
	if profile != null and _has_property(profile, "query_priority"):
		return int(profile.get("query_priority"))
	return 0


static func body_kind_name(body: Node) -> String:
	var profile := get_body_profile(body)
	if profile != null and profile.has_method("body_kind_name"):
		return String(profile.call("body_kind_name"))
	return "generic"


static func _is_better_query(candidate: Dictionary, current: Dictionary) -> bool:
	var candidate_contains := bool(candidate.get("contains", false))
	var current_contains := bool(current.get("contains", false))
	if candidate_contains != current_contains:
		return candidate_contains
	var candidate_priority := int(candidate.get("priority", 0))
	var current_priority := int(current.get("priority", 0))
	if candidate_priority != current_priority:
		return candidate_priority > current_priority
	return float(candidate.get("distance_score", INF)) < float(current.get("distance_score", INF))


static func _append_unique_nodes(target: Array[Node], candidates: Array) -> void:
	for candidate in candidates:
		var node := candidate as Node
		if node == null:
			continue
		if not target.has(node):
			target.append(node)


static func _read_profile_float(profile: Resource, property_name: String, fallback: float) -> float:
	if profile != null and _has_property(profile, property_name):
		return float(profile.get(property_name))
	return fallback


static func _read_profile_bool(profile: Resource, property_name: String, fallback: bool) -> bool:
	if profile != null and _has_property(profile, property_name):
		return bool(profile.get(property_name))
	return fallback


static func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
