@tool
extends Node3D
class_name FastWaterOcean

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")

@export_category("Profile")
@export var ocean_profile: Resource
@export_range(-32.0, 32.0, 0.01) var water_y_offset_m := 0.0
@export_range(1, 1048575, 1) var visual_layers := 1
# Host floating-origin support. Set this (or call set_wave_sample_offset) to the
# host's accumulated origin shift so the wave math samples small coordinates far
# from the world origin. Driven identically onto both tiers so they never seam.
@export var wave_sample_offset_m := Vector2.ZERO

@export_category("Body Contract")
@export var body_profile: Resource

@export_category("Focus")
@export var target_camera: Camera3D
@export var target_camera_path: NodePath
@export var wake_focus: Node3D
@export var wake_focus_path: NodePath

@export_category("Surfaces")
@export var auto_create_surfaces := true
@export var near_surface_path: NodePath
@export var far_surface_path: NodePath

var _near_surface: Node3D
var _far_surface: Node3D
var _tier_body_profile: Resource


func _ready() -> void:
	add_to_group("fast_water_ocean")
	add_to_group("fast_water_body")
	_ensure_body_profile()
	_ensure_profile()
	_resolve_references()
	_ensure_surfaces()
	apply_ocean_profile(ocean_profile)
	_update_surface_positions(true)


func _process(_delta: float) -> void:
	_resolve_references()
	if auto_create_surfaces:
		_ensure_surfaces()
	_update_surface_positions(false)


func apply_ocean_profile(profile: Resource = ocean_profile) -> void:
	if profile == null:
		profile = OCEAN_PROFILE_SCRIPT.open_world()
	ocean_profile = profile
	if auto_create_surfaces:
		_ensure_surfaces()
	if _near_surface != null and profile.has_method("apply_to_surface"):
		profile.call("apply_to_surface", _near_surface, false)
		_configure_surface_common(_near_surface)
	if _far_surface != null and profile.has_method("apply_to_surface"):
		profile.call("apply_to_surface", _far_surface, true)
		_configure_surface_common(_far_surface)
	_update_surface_positions(true)


func set_target_camera(camera: Camera3D) -> void:
	target_camera = camera
	_update_surface_positions(true)


func set_wake_focus(focus: Node3D) -> void:
	wake_focus = focus
	_update_surface_positions(true)


func get_surface_nodes() -> Array[Node]:
	var surfaces: Array[Node] = []
	if _near_surface != null:
		surfaces.append(_near_surface)
	if _far_surface != null:
		surfaces.append(_far_surface)
	return surfaces


func get_ocean_debug_state() -> Dictionary:
	var profile := _profile()
	var near_wake := _near_surface.get("wake_map_node") if _near_surface != null and _has_property(_near_surface, "wake_map_node") else null
	var far_wake := _far_surface.get("wake_map_node") if _far_surface != null and _has_property(_far_surface, "wake_map_node") else null
	var near_wake_origin := Vector2.ZERO
	if near_wake != null and _has_property(near_wake, "origin_xz"):
		near_wake_origin = near_wake.get("origin_xz")
	return {
		"profile": profile.resource_path if profile != null else "",
		"focus_position": _vector3_to_array(_focus_position()),
		"near_surface": _surface_debug(_near_surface),
		"far_surface": _surface_debug(_far_surface),
		"near_wake_map_exists": near_wake != null,
		"far_wake_map_exists": far_wake != null,
		"near_wake_origin": [near_wake_origin.x, near_wake_origin.y],
		"local_wake_world_size_m": _read_float(profile, "local_wake_world_size_m", 0.0),
		"distant_reflection_mode": _read_int(profile, "distant_reflection_mode", 0),
		"whitecap_strength": _read_float(profile, "whitecap_strength", 0.0),
		"horizon_fade_end_m": _read_float(profile, "horizon_fade_end_m", 0.0),
	}


func add_splash(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if _near_surface != null and _near_surface.has_method("add_splash"):
		_near_surface.call("add_splash", world_pos, velocity, radius)


func add_wake_point(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if _near_surface != null and _near_surface.has_method("add_wake_point"):
		_near_surface.call("add_wake_point", world_pos, velocity, radius)


func get_surface_height_at(world_position: Vector3) -> float:
	# Delegate to the near tier so the height query tracks the visible swell. The
	# near surface already incorporates its own y (= ocean y + water_y_offset_m).
	if _near_surface != null and _near_surface.has_method("get_surface_height_at"):
		return float(_near_surface.call("get_surface_height_at", world_position))
	return global_position.y + water_y_offset_m


func get_water_altitude(world_position: Vector3) -> float:
	return world_position.y - get_surface_height_at(world_position)


func get_water_depth_at(world_position: Vector3) -> float:
	return max(-get_water_altitude(world_position), 0.0)


func contains_water_point(world_position: Vector3) -> bool:
	return get_water_altitude(world_position) <= 0.05


func get_flow_at(world_position: Vector3) -> Vector3:
	# Open ocean has no current by default, but forward to the near tier so a
	# host-assigned flow field (e.g. a tidal/current map) is honored.
	if _near_surface != null and _near_surface.has_method("get_flow_at"):
		return _near_surface.call("get_flow_at", world_position) as Vector3
	return Vector3.ZERO


func _ensure_profile() -> void:
	if ocean_profile == null:
		ocean_profile = OCEAN_PROFILE_SCRIPT.open_world()


func _ensure_body_profile() -> void:
	if body_profile == null:
		body_profile = BODY_PROFILE_SCRIPT.ocean()


func _ensure_surfaces() -> void:
	_near_surface = _resolve_surface(near_surface_path, "NearOceanSurface", false)
	_far_surface = _resolve_surface(far_surface_path, "FarOceanSurface", true)


func _resolve_surface(path: NodePath, fallback_name: String, far_surface: bool) -> Node3D:
	var surface: Node3D = null
	if path != NodePath():
		surface = get_node_or_null(path) as Node3D
	if surface == null:
		surface = get_node_or_null(fallback_name) as Node3D
	if surface == null and auto_create_surfaces:
		surface = SURFACE_SCRIPT.new() as Node3D
		surface.name = fallback_name
		if far_surface:
			_set_if_has(surface, "auto_create_wake_map", false)
			_set_if_has(surface, "auto_create_bubbles", false)
			_set_if_has(surface, "splash_pool_size", 0)
			_set_if_has(surface, "interactions_enabled", false)
		add_child(surface)
		if Engine.is_editor_hint() and get_tree() != null:
			surface.owner = get_tree().edited_scene_root
		# Mark the tier non-queryable at creation so it is never briefly returned by
		# FastWaterBodyQuery with its default (enabled) lake profile before
		# apply_ocean_profile runs.
		_configure_surface_common(surface)
	return surface


func set_wave_sample_offset(offset: Vector2) -> void:
	wave_sample_offset_m = offset
	_set_if_has(_near_surface, "wave_sample_offset_m", offset)
	_set_if_has(_far_surface, "wave_sample_offset_m", offset)


func _configure_surface_common(surface: Node3D) -> void:
	_set_if_has(surface, "visual_layers", visual_layers)
	# Drive the SAME floating-origin offset onto both tiers so the macro swell stays
	# phase-identical across the near/far boundary (a mismatch would reintroduce a seam).
	_set_if_has(surface, "wave_sample_offset_m", wave_sample_offset_m)
	# The near/far meshes are RENDER tiers, not query bodies. Only the FastWaterOcean
	# facade should answer FastWaterBodyQuery and forward add_splash/add_wake_point to
	# the near tier. Give the tiers a non-queryable profile (enabled=false) so the body
	# query skips them -- otherwise the query can return the non-interactive far tier
	# and silently drop wakes. Tiers stay in the water groups so weather/planar/effect
	# fan-out still reaches them.
	_set_if_has(surface, "body_profile", _ensure_tier_body_profile())


func _ensure_tier_body_profile() -> Resource:
	if _tier_body_profile == null:
		var profile := BODY_PROFILE_SCRIPT.ocean()
		profile.enabled = false
		profile.display_name = "Ocean Render Tier (non-queryable)"
		_tier_body_profile = profile
	return _tier_body_profile


func _resolve_references() -> void:
	if target_camera == null and target_camera_path != NodePath():
		target_camera = get_node_or_null(target_camera_path) as Camera3D
	if wake_focus == null and wake_focus_path != NodePath():
		wake_focus = get_node_or_null(wake_focus_path) as Node3D


func _update_surface_positions(_force: bool) -> void:
	var profile := _profile()
	var focus := _focus_position()
	var y := global_position.y + water_y_offset_m
	if _near_surface != null:
		var near_snap := _read_float(profile, "near_follow_snap_m", 6.0)
		_near_surface.global_position = Vector3(snapped(focus.x, near_snap), y, snapped(focus.z, near_snap))
	if _far_surface != null:
		var far_snap := _read_float(profile, "far_follow_snap_m", 96.0)
		_far_surface.global_position = Vector3(snapped(focus.x, far_snap), y - 0.018, snapped(focus.z, far_snap))


func _focus_position() -> Vector3:
	if wake_focus != null:
		return wake_focus.global_position
	if target_camera != null:
		return target_camera.global_position
	return global_position


func _profile() -> Resource:
	if ocean_profile == null:
		ocean_profile = OCEAN_PROFILE_SCRIPT.open_world()
	return ocean_profile


func _surface_debug(surface: Node3D) -> Dictionary:
	if surface == null:
		return {"exists": false}
	return {
		"exists": true,
		"name": surface.name,
		"position": _vector3_to_array(surface.global_position),
		"mesh_size_m": _read_float(surface, "mesh_size_m", 0.0),
		"mesh_subdivisions": _read_int(surface, "mesh_subdivisions", 0),
		"interactions_enabled": _read_bool(surface, "interactions_enabled", false),
		"auto_create_wake_map": _read_bool(surface, "auto_create_wake_map", false),
		"wake_map_world_size_m": _read_float(surface, "wake_map_world_size_m", 0.0),
		"reflection_strength": _read_float(surface, "reflection_strength", 0.0),
		"grazing_reflection_strength": _read_float(surface, "grazing_reflection_strength", 0.0),
		"whitecap_strength": _read_float(surface, "whitecap_strength", 0.0),
		"horizon_fade_strength": _read_float(surface, "horizon_fade_strength", 0.0),
	}


func _vector3_to_array(value: Vector3) -> Array:
	return [value.x, value.y, value.z]


func _set_if_has(object: Object, property_name: String, value: Variant) -> void:
	if object == null:
		return
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			object.set(property_name, value)
			return


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null and _has_property(object, property_name):
		return float(object.get(property_name))
	return fallback


func _read_int(object: Object, property_name: String, fallback: int) -> int:
	if object != null and _has_property(object, property_name):
		return int(object.get(property_name))
	return fallback


func _read_bool(object: Object, property_name: String, fallback: bool) -> bool:
	if object != null and _has_property(object, property_name):
		return bool(object.get(property_name))
	return fallback


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
