extends Node
class_name FastWaterWeatherAdapter

const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")

@export var water_surface_paths: Array[NodePath] = []
@export var environment_state: Resource
@export var weather_response: Resource
@export var target_camera: Camera3D
@export var auto_discover_surfaces := true
@export var apply_every_frame := true
@export var rain_impact_fx_paths: Array[NodePath] = []
@export var auto_discover_rain_impact_fx := true
@export var underwater_controller_paths: Array[NodePath] = []
@export var auto_discover_underwater_controllers := true

@export_category("Rain Area")
@export_range(1.0, 512.0, 1.0) var rain_area_m := 42.0
@export_range(1.0, 120.0, 1.0) var rain_update_hz := 30.0
@export_range(0, 512, 1) var max_rain_stamps_per_tick := 24

var _rain_accum := 0.0


func _ready() -> void:
	if environment_state == null:
		environment_state = ENVIRONMENT_STATE_SCRIPT.new()
	if weather_response == null:
		weather_response = WEATHER_RESPONSE_SCRIPT.new()
	_apply_to_surfaces(0.0)


func _process(delta: float) -> void:
	if apply_every_frame:
		_apply_to_surfaces(delta)


func apply_environment_state(state: Resource) -> void:
	environment_state = state
	_apply_to_surfaces(0.0)


func set_wind(direction_xz: Vector2, speed_mps: float, gust_strength: float = 0.0) -> void:
	_ensure_state()
	environment_state.set("wind_direction_xz", direction_xz)
	environment_state.set("wind_speed_mps", max(speed_mps, 0.0))
	environment_state.set("gust_strength", clamp(gust_strength, 0.0, 1.0))
	_apply_to_surfaces(0.0)


func set_rain_intensity(rain_intensity: float) -> void:
	_ensure_state()
	environment_state.set("rain_intensity", clamp(rain_intensity, 0.0, 1.0))


func set_storm_intensity(storm_intensity: float) -> void:
	_ensure_state()
	environment_state.set("storm_intensity", clamp(storm_intensity, 0.0, 1.0))


func set_turbidity(water_turbidity: float) -> void:
	_ensure_state()
	environment_state.set("water_turbidity", clamp(water_turbidity, 0.0, 1.0))
	_apply_to_surfaces(0.0)


func _apply_to_surfaces(delta: float) -> void:
	_ensure_state()
	_ensure_response()
	var surfaces := _resolve_surfaces()
	for surface in surfaces:
		if surface == null:
			continue
		if surface.has_method("apply_environment_state"):
			surface.call("apply_environment_state", environment_state, weather_response)
		if delta > 0.0:
			_update_rain_impact_fx(surface)
			_emit_rain(surface, delta)
	for controller in _resolve_underwater_controllers():
		if controller == null:
			continue
		if controller.has_method("apply_environment_state"):
			controller.call("apply_environment_state", environment_state, weather_response)


func _emit_rain(surface: Node, delta: float) -> void:
	var rain_intensity := _read_float(environment_state, "rain_intensity", 0.0)
	if rain_intensity <= 0.001:
		return
	if not surface.has_method("emit_rain_ripples"):
		return
	_rain_accum += delta
	var interval: float = 1.0 / max(rain_update_hz, 0.001)
	if _rain_accum < interval:
		return
	var step: float = _rain_accum
	_rain_accum = 0.0
	var center := Vector3.ZERO
	if target_camera != null:
		center = target_camera.global_position
	elif surface is Node3D:
		center = (surface as Node3D).global_position
	var wind := _wind_velocity()
	surface.call("emit_rain_ripples", step, rain_intensity, center, rain_area_m, wind, weather_response, max_rain_stamps_per_tick)


func _update_rain_impact_fx(surface: Node) -> void:
	var rain_intensity := _read_float(environment_state, "rain_intensity", 0.0)
	var center := _rain_center(surface)
	var wind := _wind_velocity()
	for fx in _resolve_rain_impact_fx(surface):
		if fx == null:
			continue
		if fx.has_method("apply_rain"):
			fx.call("apply_rain", rain_intensity, center, rain_area_m, wind, weather_response)
		elif rain_intensity <= 0.001 and fx.has_method("stop"):
			fx.call("stop")


func _resolve_surfaces() -> Array[Node]:
	var surfaces: Array[Node] = []
	for path in water_surface_paths:
		if path == NodePath(""):
			continue
		var surface := get_node_or_null(path)
		if surface != null:
			surfaces.append(surface)
	if surfaces.is_empty() and auto_discover_surfaces and get_tree() != null:
		for node in get_tree().get_nodes_in_group("fast_water_surface"):
			if node is Node:
				surfaces.append(node)
	return surfaces


func _resolve_rain_impact_fx(surface: Node) -> Array[Node]:
	var effects: Array[Node] = []
	for path in rain_impact_fx_paths:
		if path == NodePath(""):
			continue
		var effect := get_node_or_null(path)
		if effect != null and not effects.has(effect):
			effects.append(effect)
	if auto_discover_rain_impact_fx:
		if surface != null:
			var child_fx := surface.get_node_or_null("RainImpactFx")
			if child_fx != null and not effects.has(child_fx):
				effects.append(child_fx)
		if effects.is_empty() and get_tree() != null:
			for node in get_tree().get_nodes_in_group("fast_water_rain_impact_fx"):
				if node is Node and not effects.has(node):
					effects.append(node)
	return effects


func _resolve_underwater_controllers() -> Array[Node]:
	var controllers: Array[Node] = []
	for path in underwater_controller_paths:
		if path == NodePath(""):
			continue
		var controller := get_node_or_null(path)
		if controller != null and not controllers.has(controller):
			controllers.append(controller)
	if auto_discover_underwater_controllers and get_tree() != null:
		for node in get_tree().get_nodes_in_group("fast_water_underwater_controller"):
			if node is Node and not controllers.has(node):
				controllers.append(node)
	return controllers


func _rain_center(surface: Node) -> Vector3:
	if target_camera != null:
		return target_camera.global_position
	if surface is Node3D:
		return (surface as Node3D).global_position
	return Vector3.ZERO


func _wind_velocity() -> Vector3:
	var dir := _read_vector2(environment_state, "wind_direction_xz", Vector2.ZERO)
	if dir.length_squared() > 0.0001:
		dir = dir.normalized()
	var speed := _read_float(environment_state, "wind_speed_mps", 0.0)
	return Vector3(dir.x * speed, 0.0, dir.y * speed)


func _ensure_state() -> void:
	if environment_state == null:
		environment_state = ENVIRONMENT_STATE_SCRIPT.new()


func _ensure_response() -> void:
	if weather_response == null:
		weather_response = WEATHER_RESPONSE_SCRIPT.new()


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
