extends Node
class_name FastUnderwaterController

const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")

@export var camera: Camera3D
@export var water_surface: Node
@export var water_surface_path: NodePath
@export var underwater_overlay: CanvasItem
@export var overlay_material: ShaderMaterial
@export var environment_state: Resource
@export var weather_response: Resource
@export var fallback_water_y := 0.0
@export_range(0.0, 2.0, 0.01) var submerge_margin_m := 0.05
@export_range(0.0, 2.0, 0.01) var distortion_strength := 0.018
@export_range(0.0, 1.0, 0.01) var tint_strength := 0.42
@export var underwater_tint := Color(0.02, 0.22, 0.28, 1.0)
@export_range(0.1, 16.0, 0.1) var max_effect_depth_m := 3.5
@export_range(0.0, 1.0, 0.01) var vignette_strength := 0.28
@export var surface_light_color := Color(0.24, 0.58, 0.68, 1.0)
@export_range(0.0, 1.0, 0.01) var surface_haze_strength := 0.20
@export_range(0.0, 1.0, 0.01) var light_shaft_strength := 0.18
@export_range(0.0, 1.0, 0.01) var caustic_strength := 0.16
@export_range(0.0, 1.0, 0.01) var particulate_strength := 0.10
@export_range(16.0, 512.0, 1.0) var particulate_scale := 180.0
@export_range(0.0, 1.0, 0.01) var waterline_strength := 0.22
@export_range(0.005, 0.25, 0.001) var waterline_width := 0.055
@export_range(0.0, 1.0, 0.01) var waterline_screen_y := 0.55

var is_underwater := false
var _environment_turbidity := 0.0
var _environment_visibility_loss := 0.0
var _environment_tint_mix := 0.0
var _environment_tint_boost := 0.0
var _environment_distortion_boost := 0.0
var _environment_particulate_boost := 0.0
var _environment_vignette_boost := 0.0
var _environment_caustic_loss := 0.0
var _environment_light_loss := 0.0
var _environment_turbid_tint := Color(0.04, 0.16, 0.12, 1.0)
var _environment_silt_color := Color(0.17, 0.22, 0.15, 1.0)


func _ready() -> void:
	add_to_group("fast_water_underwater_controller")
	_resolve_surface()
	_apply_overlay_state(false)


func _process(_delta: float) -> void:
	if water_surface == null:
		_resolve_surface()
	if camera == null:
		return

	var water_y := fallback_water_y
	if water_surface != null and water_surface.has_method("get_surface_height_at"):
		water_y = float(water_surface.call("get_surface_height_at", camera.global_position))

	var submerge_depth: float = max(water_y - camera.global_position.y, 0.0)
	var underwater := submerge_depth > submerge_margin_m
	if underwater != is_underwater:
		is_underwater = underwater
		_apply_overlay_state(is_underwater)

	if overlay_material != null:
		if environment_state != null:
			_compute_environment_modifiers(environment_state, weather_response)
		var values := get_underwater_environment_sample()
		var submerge_fade: float = smoothstep(0.0, max(max_effect_depth_m, 0.001), submerge_depth)
		overlay_material.set_shader_parameter("distortion_strength", values.get("distortion_strength", distortion_strength))
		overlay_material.set_shader_parameter("tint_strength", values.get("tint_strength", tint_strength))
		overlay_material.set_shader_parameter("underwater_tint", values.get("underwater_tint", underwater_tint))
		overlay_material.set_shader_parameter("submerge_depth_m", submerge_depth)
		overlay_material.set_shader_parameter("submerge_fade", submerge_fade)
		overlay_material.set_shader_parameter("vignette_strength", values.get("vignette_strength", vignette_strength))
		overlay_material.set_shader_parameter("surface_light_color", surface_light_color)
		overlay_material.set_shader_parameter("surface_haze_strength", values.get("surface_haze_strength", surface_haze_strength))
		overlay_material.set_shader_parameter("light_shaft_strength", values.get("light_shaft_strength", light_shaft_strength))
		overlay_material.set_shader_parameter("caustic_strength", values.get("caustic_strength", caustic_strength))
		overlay_material.set_shader_parameter("particulate_strength", values.get("particulate_strength", particulate_strength))
		overlay_material.set_shader_parameter("particulate_scale", particulate_scale)
		overlay_material.set_shader_parameter("waterline_strength", waterline_strength)
		overlay_material.set_shader_parameter("waterline_width", waterline_width)
		overlay_material.set_shader_parameter("waterline_screen_y", waterline_screen_y)
		overlay_material.set_shader_parameter("turbidity", values.get("turbidity", 0.0))
		overlay_material.set_shader_parameter("visibility_loss", values.get("visibility_loss", 0.0))
		overlay_material.set_shader_parameter("suspended_silt_color", values.get("suspended_silt_color", _environment_silt_color))


func apply_environment_state(state: Resource, response: Resource = null) -> void:
	environment_state = state
	if response != null:
		weather_response = response
	if environment_state == null:
		_reset_environment_modifiers()
	else:
		_compute_environment_modifiers(environment_state, weather_response)


func get_underwater_environment_sample() -> Dictionary:
	var tint_mix: float = clamp(_environment_tint_mix, 0.0, 1.0)
	var light_transmission: float = 1.0 - clamp(_environment_light_loss, 0.0, 0.95)
	return {
		"turbidity": _environment_turbidity,
		"visibility_loss": _environment_visibility_loss,
		"distortion_strength": distortion_strength * (1.0 + _environment_distortion_boost),
		"tint_strength": clamp(tint_strength + _environment_tint_boost, 0.0, 1.0),
		"underwater_tint": underwater_tint.lerp(_environment_turbid_tint, tint_mix),
		"surface_haze_strength": clamp(surface_haze_strength + _environment_visibility_loss * 0.18, 0.0, 1.0),
		"light_shaft_strength": clamp(light_shaft_strength * light_transmission, 0.0, 1.0),
		"caustic_strength": clamp(caustic_strength * (1.0 - _environment_caustic_loss), 0.0, 1.0),
		"particulate_strength": clamp(particulate_strength + _environment_particulate_boost, 0.0, 1.0),
		"vignette_strength": clamp(vignette_strength + _environment_vignette_boost, 0.0, 1.0),
		"suspended_silt_color": _environment_silt_color,
	}


func _apply_overlay_state(enabled: bool) -> void:
	if underwater_overlay != null:
		underwater_overlay.visible = enabled


func _resolve_surface() -> void:
	if water_surface_path != NodePath(""):
		water_surface = get_node_or_null(water_surface_path)
	if water_surface != null:
		return
	if get_tree() == null:
		return
	var surfaces := get_tree().get_nodes_in_group("fast_water_surface")
	if not surfaces.is_empty():
		water_surface = surfaces[0] as Node


func _compute_environment_modifiers(state: Resource, response: Resource) -> void:
	if state == null:
		_reset_environment_modifiers()
		return
	if response == null:
		if weather_response == null:
			weather_response = WEATHER_RESPONSE_SCRIPT.new()
		response = weather_response

	var turbidity: float = clamp(_read_float(state, "water_turbidity", 0.0), 0.0, 1.0)
	var rain: float = clamp(_read_float(state, "rain_intensity", 0.0), 0.0, 1.0)
	var storm: float = clamp(_read_float(state, "storm_intensity", 0.0), 0.0, 1.0)
	var visibility_driver: float = clamp(turbidity + rain * 0.05 + storm * 0.10, 0.0, 1.0)

	_environment_turbidity = turbidity
	_environment_visibility_loss = clamp(visibility_driver * _read_float(response, "turbidity_underwater_visibility_loss", 0.72), 0.0, 0.95)
	_environment_tint_mix = clamp(turbidity * 0.92 + rain * 0.05 + storm * 0.08, 0.0, 1.0)
	_environment_tint_boost = clamp(turbidity * _read_float(response, "turbidity_underwater_tint_boost", 0.28), 0.0, 1.0)
	_environment_distortion_boost = clamp(turbidity * _read_float(response, "turbidity_underwater_distortion_boost", 0.55) + storm * 0.12, 0.0, 2.0)
	_environment_particulate_boost = clamp(turbidity * _read_float(response, "turbidity_underwater_particulate_boost", 0.42) + rain * 0.08, 0.0, 1.0)
	_environment_vignette_boost = clamp(visibility_driver * _read_float(response, "turbidity_underwater_vignette_boost", 0.24), 0.0, 1.0)
	_environment_caustic_loss = clamp(visibility_driver * _read_float(response, "turbidity_underwater_caustic_loss", 0.68), 0.0, 1.0)
	_environment_light_loss = clamp(visibility_driver * _read_float(response, "turbidity_underwater_light_loss", 0.36), 0.0, 1.0)
	_environment_turbid_tint = _read_color(response, "turbid_underwater_tint", Color(0.04, 0.16, 0.12, 1.0))
	_environment_silt_color = _read_color(response, "suspended_silt_color", Color(0.17, 0.22, 0.15, 1.0))


func _reset_environment_modifiers() -> void:
	_environment_turbidity = 0.0
	_environment_visibility_loss = 0.0
	_environment_tint_mix = 0.0
	_environment_tint_boost = 0.0
	_environment_distortion_boost = 0.0
	_environment_particulate_boost = 0.0
	_environment_vignette_boost = 0.0
	_environment_caustic_loss = 0.0
	_environment_light_loss = 0.0
	_environment_turbid_tint = Color(0.04, 0.16, 0.12, 1.0)
	_environment_silt_color = Color(0.17, 0.22, 0.15, 1.0)


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null and _has_property(object, property_name):
		return float(object.get(property_name))
	return fallback


func _read_color(object: Object, property_name: String, fallback: Color) -> Color:
	if object != null and _has_property(object, property_name):
		var value: Variant = object.get(property_name)
		if value is Color:
			return value
	return fallback


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
