@tool
extends Node
class_name FastWaterWeatherSequence

const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")

@export var adapter: Node:
	set(value):
		adapter = value
		if is_inside_tree():
			call_deferred("apply_current")
@export var adapter_path: NodePath:
	set(value):
		adapter_path = value
		if is_inside_tree():
			call_deferred("apply_current")

@export var playing := true
@export var loop := true
@export_range(0.01, 24.0, 0.01) var time_scale := 1.0
@export var stages: Array[Dictionary] = []

var current_stage_name := ""
var sequence_time_s := 0.0
var _state: Resource


func _ready() -> void:
	if stages.is_empty():
		stages = default_weather_stages()
	_ensure_state()
	apply_current()


func _process(delta: float) -> void:
	if not playing:
		return
	advance(delta)


func advance(delta: float) -> Dictionary:
	sequence_time_s += maxf(delta, 0.0) * time_scale
	_normalize_time()
	return apply_current()


func set_time(seconds: float) -> Dictionary:
	sequence_time_s = maxf(seconds, 0.0)
	_normalize_time()
	return apply_current()


func apply_current() -> Dictionary:
	var sample := sample_at(sequence_time_s)
	_apply_sample(sample)
	return sample


func sample_at(seconds: float) -> Dictionary:
	var active_stages := _active_stages()
	if active_stages.is_empty():
		return {}

	var total := get_total_duration()
	var t := maxf(seconds, 0.0)
	if loop and total > 0.001:
		t = fmod(t, total)
	else:
		t = minf(t, maxf(total - 0.001, 0.0))

	var cursor := 0.0
	for i in range(active_stages.size()):
		var stage := active_stages[i]
		var duration := _stage_float(stage, "duration_s", 1.0)
		var next_stage := active_stages[(i + 1) % active_stages.size()] if loop or i < active_stages.size() - 1 else stage
		if t <= cursor + duration or i == active_stages.size() - 1:
			var local_t := clampf((t - cursor) / maxf(duration, 0.001), 0.0, 1.0)
			var transition_s := minf(_stage_float(stage, "transition_s", 1.0), duration)
			var blend := 0.0
			if transition_s > 0.001 and t >= cursor + duration - transition_s:
				blend = smoothstep(cursor + duration - transition_s, cursor + duration, t)
			var sample := _blend_stage(stage, next_stage, blend)
			sample["stage_name"] = str(stage.get("name", "stage_%d" % i))
			sample["stage_index"] = i
			sample["stage_blend_to_next"] = blend
			sample["sequence_time_s"] = t
			return sample
		cursor += duration
	return _blend_stage(active_stages[active_stages.size() - 1], active_stages[active_stages.size() - 1], 0.0)


func get_total_duration() -> float:
	var total := 0.0
	for stage in _active_stages():
		total += _stage_float(stage, "duration_s", 1.0)
	return total


func get_stage_names() -> PackedStringArray:
	var names := PackedStringArray()
	for i in range(_active_stages().size()):
		var stage := _active_stages()[i]
		names.append(str(stage.get("name", "stage_%d" % i)))
	return names


func default_weather_stages() -> Array[Dictionary]:
	return [
		{
			"name": "clear",
			"duration_s": 4.0,
			"transition_s": 1.2,
			"wind_direction_xz": Vector2(0.85, -0.20),
			"wind_speed_mps": 2.0,
			"gust_strength": 0.0,
			"rain_intensity": 0.0,
			"storm_intensity": 0.0,
			"water_turbidity": 0.02,
			"sun_color": Color(1.0, 0.96, 0.82, 1.0),
		},
		{
			"name": "drizzle",
			"duration_s": 5.0,
			"transition_s": 1.4,
			"wind_direction_xz": Vector2(0.80, -0.26),
			"wind_speed_mps": 4.5,
			"gust_strength": 0.10,
			"rain_intensity": 0.22,
			"storm_intensity": 0.0,
			"water_turbidity": 0.07,
			"sun_color": Color(0.86, 0.92, 0.96, 1.0),
		},
		{
			"name": "heavy_rain",
			"duration_s": 5.0,
			"transition_s": 1.5,
			"wind_direction_xz": Vector2(0.72, -0.42),
			"wind_speed_mps": 8.0,
			"gust_strength": 0.36,
			"rain_intensity": 0.72,
			"storm_intensity": 0.22,
			"water_turbidity": 0.18,
			"sun_color": Color(0.70, 0.82, 0.90, 1.0),
		},
		{
			"name": "storm",
			"duration_s": 6.0,
			"transition_s": 1.8,
			"wind_direction_xz": Vector2(0.58, -0.82),
			"wind_speed_mps": 15.0,
			"gust_strength": 0.82,
			"rain_intensity": 0.96,
			"storm_intensity": 0.92,
			"water_turbidity": 0.36,
			"sun_color": Color(0.58, 0.68, 0.78, 1.0),
		},
		{
			"name": "calm_after_storm",
			"duration_s": 5.0,
			"transition_s": 1.6,
			"wind_direction_xz": Vector2(0.74, -0.34),
			"wind_speed_mps": 5.2,
			"gust_strength": 0.12,
			"rain_intensity": 0.05,
			"storm_intensity": 0.04,
			"water_turbidity": 0.22,
			"sun_color": Color(0.88, 0.94, 0.98, 1.0),
		},
	]


func _active_stages() -> Array[Dictionary]:
	if stages.is_empty():
		stages = default_weather_stages()
	return stages


func _apply_sample(sample: Dictionary) -> void:
	_ensure_state()
	if sample.is_empty():
		return
	current_stage_name = str(sample.get("stage_name", ""))
	_state.set("wind_direction_xz", sample.get("wind_direction_xz", Vector2.ZERO))
	_state.set("wind_speed_mps", float(sample.get("wind_speed_mps", 0.0)))
	_state.set("gust_strength", float(sample.get("gust_strength", 0.0)))
	_state.set("rain_intensity", float(sample.get("rain_intensity", 0.0)))
	_state.set("storm_intensity", float(sample.get("storm_intensity", 0.0)))
	_state.set("water_turbidity", float(sample.get("water_turbidity", 0.0)))
	_state.set("sun_color", sample.get("sun_color", Color.WHITE))

	var target := _resolve_adapter()
	if target == null:
		return
	if target.has_method("apply_environment_state"):
		target.call("apply_environment_state", _state)
	elif _has_property(target, "environment_state"):
		target.set("environment_state", _state)


func _ensure_state() -> void:
	if _state == null:
		_state = ENVIRONMENT_STATE_SCRIPT.new()


func _resolve_adapter() -> Node:
	if adapter != null:
		return adapter
	if adapter_path != NodePath("") and is_inside_tree():
		return get_node_or_null(adapter_path)
	return get_parent().get_node_or_null("WeatherAdapter") if get_parent() != null else null


func _normalize_time() -> void:
	var total := get_total_duration()
	if total <= 0.001:
		sequence_time_s = 0.0
	elif loop:
		sequence_time_s = fmod(maxf(sequence_time_s, 0.0), total)
	else:
		sequence_time_s = minf(maxf(sequence_time_s, 0.0), total)


func _blend_stage(a: Dictionary, b: Dictionary, blend: float) -> Dictionary:
	var t := clampf(blend, 0.0, 1.0)
	return {
		"wind_direction_xz": _blend_vector2(a, b, "wind_direction_xz", t, Vector2.ZERO),
		"wind_speed_mps": lerpf(_stage_float(a, "wind_speed_mps", 0.0), _stage_float(b, "wind_speed_mps", 0.0), t),
		"gust_strength": lerpf(_stage_float(a, "gust_strength", 0.0), _stage_float(b, "gust_strength", 0.0), t),
		"rain_intensity": lerpf(_stage_float(a, "rain_intensity", 0.0), _stage_float(b, "rain_intensity", 0.0), t),
		"storm_intensity": lerpf(_stage_float(a, "storm_intensity", 0.0), _stage_float(b, "storm_intensity", 0.0), t),
		"water_turbidity": lerpf(_stage_float(a, "water_turbidity", 0.0), _stage_float(b, "water_turbidity", 0.0), t),
		"sun_color": _blend_color(a, b, "sun_color", t, Color.WHITE),
	}


func _blend_vector2(a: Dictionary, b: Dictionary, key: String, t: float, fallback: Vector2) -> Vector2:
	var av: Variant = a.get(key, fallback)
	var bv: Variant = b.get(key, av)
	var va := av as Vector2 if av is Vector2 else fallback
	var vb := bv as Vector2 if bv is Vector2 else va
	return va.lerp(vb, t)


func _blend_color(a: Dictionary, b: Dictionary, key: String, t: float, fallback: Color) -> Color:
	var av: Variant = a.get(key, fallback)
	var bv: Variant = b.get(key, av)
	var ca := av as Color if av is Color else fallback
	var cb := bv as Color if bv is Color else ca
	return ca.lerp(cb, t)


func _stage_float(stage: Dictionary, key: String, fallback: float) -> float:
	return float(stage.get(key, fallback))


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
