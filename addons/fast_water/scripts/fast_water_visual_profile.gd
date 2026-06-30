extends Resource
class_name FastWaterVisualProfile

@export_category("Surface Color")
@export var shallow_color := Color(0.025, 0.22, 0.30, 1.0)
@export var deep_color := Color(0.0, 0.040, 0.095, 1.0)
@export var foam_color := Color(0.96, 0.98, 1.0, 1.0)
@export_range(0.0, 1.0, 0.01) var alpha := 0.965

@export_category("Waves")
@export_range(0.0, 4.0, 0.01) var wave_height := 0.09
@export_range(0.001, 1.0, 0.001) var wave_frequency := 0.30
@export_range(0.0, 4.0, 0.01) var wave_speed := 1.25
@export_range(0.0, 2.0, 0.01) var normal_strength := 1.9
@export_range(0.0, 2.0, 0.01) var micro_normal_strength := 0.82
@export_range(0.1, 32.0, 0.01) var micro_normal_scale := 16.0
@export_range(0.0, 2.0, 0.01) var detail_normal_strength := 0.55
@export_range(1.0, 96.0, 0.1) var detail_normal_scale := 40.0

@export_category("Reflection")
@export var reflection_color := Color(0.10, 0.25, 0.36, 1.0)
@export_range(0.0, 1.0, 0.01) var reflection_strength := 0.12
@export_range(0.0, 1.0, 0.01) var grazing_reflection_strength := 0.32
@export_range(0.0, 1.0, 0.01) var planar_reflection_strength := 0.16
@export_range(0.0, 0.12, 0.001) var planar_reflection_distortion := 0.007
@export_range(0.0, 0.02, 0.0001) var planar_reflection_softness := 0.004
@export_range(0.0, 2.0, 0.01) var fresnel_strength := 0.45
@export_range(0.0, 1.0, 0.01) var surface_gloss := 0.62
@export var sun_glint_color := Color(1.0, 0.96, 0.82, 1.0)
@export_range(0.0, 4.0, 0.01) var sun_glint_strength := 0.42
@export_range(1.0, 96.0, 0.1) var sun_glint_sharpness := 10.0
@export var sun_glint_direction := Vector2(0.9, 0.25)
@export_range(0.25, 8.0, 0.01) var planar_reflection_grazing_power := 3.25
@export_range(0.0, 1.0, 0.01) var planar_reflection_max_mix := 0.48
@export_range(0.1, 2.0, 0.01) var final_color_gain := 1.08

@export_category("Depth And Foam")
@export_range(0.05, 8.0, 0.01) var foam_depth_m := 0.16
@export_range(0.5, 80.0, 0.1) var deep_depth_m := 6.0
@export_range(0.0, 4.0, 0.01) var wake_highlight_strength := 1.15
@export_range(0.0, 2.0, 0.01) var wake_map_strength := 0.54
@export_range(0.0, 3.0, 0.01) var foam_intensity := 1.0
@export_range(0.0, 3.0, 0.01) var shoreline_foam_strength := 1.0
@export_range(0.0, 3.0, 0.01) var wake_foam_strength := 1.0
@export_range(0.0, 1.0, 0.01) var foam_breakup_strength := 0.65
@export_range(0.25, 16.0, 0.01) var foam_noise_scale := 3.6
@export_range(0.0, 2.0, 0.01) var whitecap_strength := 0.0
@export_range(0.02, 4.0, 0.01) var whitecap_scale := 0.32
@export_range(0.0, 1.0, 0.01) var depth_absorption_strength := 0.85
@export_range(0.0, 2.0, 0.01) var absorption_density := 0.28

@export_category("Open Water")
@export var horizon_color := Color(0.34, 0.56, 0.66, 1.0)
@export_range(0.0, 1.0, 0.01) var horizon_fade_strength := 0.0
@export_range(8.0, 4096.0, 1.0) var horizon_fade_start_m := 280.0
@export_range(16.0, 8192.0, 1.0) var horizon_fade_end_m := 920.0

@export_category("Performance")
@export_range(1, 256, 1) var mesh_subdivisions := 160
@export_range(64, 1024, 64) var wake_map_resolution := 512
@export_range(8.0, 512.0, 1.0) var wake_map_world_size_m := 36.0
@export_range(1.0, 120.0, 1.0) var wake_map_update_hz := 45.0
@export_range(0.0, 0.12, 0.001) var refraction_strength := 0.016
@export_range(0.0, 1.0, 0.01) var refracted_scene_strength := 0.18
@export var use_gpu_wake_map := false

@export_category("Wake Stamp")
@export_range(0.04, 0.4, 0.01) var stamp_side_band_width := 0.11
@export_range(0.0, 1.0, 0.01) var stamp_center_trail_strength := 0.28
@export_range(0.0, 1.0, 0.01) var stamp_bow_band_strength := 0.38
@export_range(0.0, 2.0, 0.01) var stamp_foam_gain := 0.54


func apply_to(surface: Object, wake_map: Object = null, reflection: Object = null) -> void:
	if surface != null:
		_set_if_has(surface, "shallow_color", shallow_color)
		_set_if_has(surface, "deep_color", deep_color)
		_set_if_has(surface, "foam_color", foam_color)
		_set_if_has(surface, "alpha", alpha)
		_set_if_has(surface, "wave_height", wave_height)
		_set_if_has(surface, "wave_frequency", wave_frequency)
		_set_if_has(surface, "wave_speed", wave_speed)
		_set_if_has(surface, "normal_strength", normal_strength)
		_set_if_has(surface, "micro_normal_strength", micro_normal_strength)
		_set_if_has(surface, "micro_normal_scale", micro_normal_scale)
		_set_if_has(surface, "detail_normal_strength", detail_normal_strength)
		_set_if_has(surface, "detail_normal_scale", detail_normal_scale)
		_set_if_has(surface, "reflection_color", reflection_color)
		_set_if_has(surface, "reflection_strength", reflection_strength)
		_set_if_has(surface, "grazing_reflection_strength", grazing_reflection_strength)
		_set_if_has(surface, "fresnel_strength", fresnel_strength)
		_set_if_has(surface, "surface_gloss", surface_gloss)
		_set_if_has(surface, "sun_glint_color", sun_glint_color)
		_set_if_has(surface, "sun_glint_strength", sun_glint_strength)
		_set_if_has(surface, "sun_glint_sharpness", sun_glint_sharpness)
		_set_if_has(surface, "sun_glint_direction", sun_glint_direction)
		_set_if_has(surface, "planar_reflection_softness", planar_reflection_softness)
		_set_if_has(surface, "planar_reflection_grazing_power", planar_reflection_grazing_power)
		_set_if_has(surface, "planar_reflection_max_mix", planar_reflection_max_mix)
		_set_if_has(surface, "final_color_gain", final_color_gain)
		_set_if_has(surface, "foam_depth_m", foam_depth_m)
		_set_if_has(surface, "deep_depth_m", deep_depth_m)
		_set_if_has(surface, "wake_highlight_strength", wake_highlight_strength)
		_set_if_has(surface, "wake_map_strength", wake_map_strength)
		_set_if_has(surface, "foam_intensity", foam_intensity)
		_set_if_has(surface, "shoreline_foam_strength", shoreline_foam_strength)
		_set_if_has(surface, "wake_foam_strength", wake_foam_strength)
		_set_if_has(surface, "foam_breakup_strength", foam_breakup_strength)
		_set_if_has(surface, "foam_noise_scale", foam_noise_scale)
		_set_if_has(surface, "whitecap_strength", whitecap_strength)
		_set_if_has(surface, "whitecap_scale", whitecap_scale)
		_set_if_has(surface, "depth_absorption_strength", depth_absorption_strength)
		_set_if_has(surface, "absorption_density", absorption_density)
		_set_if_has(surface, "horizon_color", horizon_color)
		_set_if_has(surface, "horizon_fade_strength", horizon_fade_strength)
		_set_if_has(surface, "horizon_fade_start_m", horizon_fade_start_m)
		_set_if_has(surface, "horizon_fade_end_m", horizon_fade_end_m)
		_set_if_has(surface, "mesh_subdivisions", mesh_subdivisions)
		_set_if_has(surface, "wake_map_resolution", wake_map_resolution)
		_set_if_has(surface, "wake_map_world_size_m", wake_map_world_size_m)
		_set_if_has(surface, "wake_map_update_hz", wake_map_update_hz)
		_set_if_has(surface, "refraction_strength", refraction_strength)
		_set_if_has(surface, "refracted_scene_strength", refracted_scene_strength)
		_set_if_has(surface, "use_gpu_wake_map", use_gpu_wake_map)

	if wake_map != null:
		_set_if_has(wake_map, "world_size_m", wake_map_world_size_m)
		_set_if_has(wake_map, "update_hz", wake_map_update_hz)
		_set_if_has(wake_map, "side_band_width", stamp_side_band_width)
		_set_if_has(wake_map, "center_trail_strength", stamp_center_trail_strength)
		_set_if_has(wake_map, "bow_band_strength", stamp_bow_band_strength)
		_set_if_has(wake_map, "foam_gain", stamp_foam_gain)

	if reflection != null:
		_set_if_has(reflection, "reflection_strength", planar_reflection_strength)
		_set_if_has(reflection, "distortion_strength", planar_reflection_distortion)
		_set_if_has(reflection, "softness", planar_reflection_softness)


static func reflective_pool() -> FastWaterVisualProfile:
	var profile := FastWaterVisualProfile.new()
	return profile


static func gameplay_lake() -> FastWaterVisualProfile:
	var profile := reflective_pool()
	profile.shallow_color = Color(0.005, 0.24, 0.31, 1.0)
	profile.deep_color = Color(0.0, 0.030, 0.085, 1.0)
	profile.foam_color = Color(0.88, 0.96, 1.0, 1.0)
	profile.alpha = 0.985
	profile.wave_height = 0.10
	profile.normal_strength = 1.85
	profile.micro_normal_strength = 0.78
	profile.mesh_subdivisions = 128
	profile.wake_map_resolution = 256
	profile.wake_map_world_size_m = 48.0
	profile.wake_map_update_hz = 30.0
	profile.detail_normal_strength = 0.5
	profile.detail_normal_scale = 42.0
	profile.reflection_color = Color(0.13, 0.31, 0.42, 1.0)
	profile.reflection_strength = 0.16
	profile.grazing_reflection_strength = 0.34
	profile.fresnel_strength = 0.56
	profile.surface_gloss = 0.72
	profile.sun_glint_strength = 0.48
	profile.sun_glint_sharpness = 14.0
	profile.foam_breakup_strength = 0.84
	profile.foam_noise_scale = 5.0
	profile.foam_depth_m = 0.46
	profile.depth_absorption_strength = 0.84
	profile.absorption_density = 0.30
	profile.deep_depth_m = 6.5
	profile.planar_reflection_softness = 0.0035
	profile.planar_reflection_grazing_power = 3.20
	profile.planar_reflection_max_mix = 0.24
	profile.refraction_strength = 0.010
	profile.wake_map_strength = 0.56
	profile.foam_intensity = 0.92
	profile.shoreline_foam_strength = 1.08
	profile.wake_foam_strength = 0.82
	profile.wake_highlight_strength = 0.98
	profile.stamp_center_trail_strength = 0.34
	profile.stamp_bow_band_strength = 0.45
	profile.stamp_foam_gain = 0.54
	profile.planar_reflection_strength = 0.14
	profile.refracted_scene_strength = 0.08
	profile.final_color_gain = 1.04
	profile.use_gpu_wake_map = true
	return profile


static func hero_pool_reference() -> FastWaterVisualProfile:
	var profile := reflective_pool()
	profile.shallow_color = Color(0.0, 0.205, 0.30, 1.0)
	profile.deep_color = Color(0.0, 0.020, 0.075, 1.0)
	profile.foam_color = Color(0.72, 0.90, 0.98, 1.0)
	profile.alpha = 0.985
	profile.wave_height = 0.085
	profile.normal_strength = 1.95
	profile.micro_normal_strength = 0.78
	profile.micro_normal_scale = 16.0
	profile.mesh_subdivisions = 192
	profile.wake_map_resolution = 512
	profile.wake_map_world_size_m = 36.0
	profile.wake_map_update_hz = 45.0
	profile.detail_normal_strength = 0.60
	profile.detail_normal_scale = 40.0
	profile.reflection_color = Color(0.055, 0.20, 0.30, 1.0)
	profile.reflection_strength = 0.13
	profile.grazing_reflection_strength = 0.28
	profile.fresnel_strength = 0.58
	profile.surface_gloss = 0.86
	# 1.80 was a grey-out outlier (every other profile uses 0.42-0.48): the glint is
	# an additive near-white term gated by fresnel, so wherever a ripple lifts fresnel
	# it bloomed to ~1.26 of near-white over near-black water and washed the surface
	# grey. Back in the normal range.
	profile.sun_glint_strength = 0.55
	profile.sun_glint_sharpness = 8.0
	profile.foam_breakup_strength = 0.78
	profile.foam_noise_scale = 5.4
	profile.foam_depth_m = 0.28
	profile.depth_absorption_strength = 0.88
	profile.absorption_density = 0.36
	profile.deep_depth_m = 7.2
	profile.planar_reflection_softness = 0.0020
	profile.planar_reflection_grazing_power = 4.65
	profile.planar_reflection_max_mix = 0.16
	profile.refraction_strength = 0.009
	profile.wake_map_strength = 0.42
	profile.foam_intensity = 0.46
	profile.shoreline_foam_strength = 0.74
	profile.wake_foam_strength = 0.42
	profile.wake_highlight_strength = 0.38
	profile.stamp_center_trail_strength = 0.24
	profile.stamp_bow_band_strength = 0.26
	profile.stamp_foam_gain = 0.30
	profile.planar_reflection_strength = 0.08
	profile.planar_reflection_distortion = 0.0018
	profile.refracted_scene_strength = 0.055
	profile.final_color_gain = 1.02
	profile.use_gpu_wake_map = true
	return profile


static func hero_quality() -> FastWaterVisualProfile:
	return hero_pool_reference()


static func cheap_ocean() -> FastWaterVisualProfile:
	var profile := FastWaterVisualProfile.new()
	profile.wave_height = 0.18
	profile.wave_frequency = 0.08
	profile.wave_speed = 0.72
	profile.normal_strength = 0.85
	profile.micro_normal_strength = 0.55
	profile.detail_normal_strength = 0.0
	profile.refraction_strength = 0.0
	profile.refracted_scene_strength = 0.05
	profile.reflection_strength = 0.22
	profile.planar_reflection_strength = 0.0
	profile.sun_glint_strength = 0.0
	profile.grazing_reflection_strength = 0.18
	profile.foam_breakup_strength = 0.0
	profile.whitecap_strength = 0.10
	profile.whitecap_scale = 0.18
	profile.horizon_color = Color(0.28, 0.52, 0.64, 1.0)
	profile.horizon_fade_strength = 0.36
	profile.horizon_fade_start_m = 340.0
	profile.horizon_fade_end_m = 1180.0
	profile.depth_absorption_strength = 0.0
	profile.absorption_density = 0.0
	profile.final_color_gain = 1.0
	profile.mesh_subdivisions = 80
	profile.wake_map_resolution = 256
	profile.wake_map_update_hz = 30.0
	return profile


static func mobile_low() -> FastWaterVisualProfile:
	var profile := cheap_ocean()
	profile.mesh_subdivisions = 48
	profile.wake_map_resolution = 128
	profile.wake_map_update_hz = 20.0
	profile.micro_normal_strength = 0.35
	profile.detail_normal_strength = 0.0
	profile.wake_highlight_strength = 0.9
	profile.final_color_gain = 1.0
	return profile


static func _set_if_has(object: Object, property_name: String, value: Variant) -> void:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			object.set(property_name, value)
			return
