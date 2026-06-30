extends Resource
class_name FastWaterOceanProfile

const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")

enum DistantReflectionMode {
	OFF,
	CHEAP_COLOR,
}

@export_category("Camera Relative Mesh")
@export_range(32.0, 1024.0, 1.0) var near_mesh_size_m := 220.0
@export_range(1, 256, 1) var near_mesh_subdivisions := 144
@export_range(128.0, 8192.0, 1.0) var far_mesh_size_m := 1850.0
@export_range(1, 256, 1) var far_mesh_subdivisions := 72
@export_range(1.0, 128.0, 0.5) var near_follow_snap_m := 6.0
@export_range(4.0, 512.0, 1.0) var far_follow_snap_m := 96.0

@export_category("World LOD Policy")
@export var match_macro_appearance_across_lod := true
@export var high_quality_near_only := true
@export var far_micro_detail_enabled := false
@export_range(0.0, 0.25, 0.001) var near_edge_fade_width := 0.105
# The far tier is a finite plane; without an alpha feather its geometric boundary
# reads as a hard "edge of the ocean" from elevated/aerial cameras. Dissolve the
# outer band so the far mesh meets the sky instead of cutting off. Keep in step
# with horizon_fade_end_m below (the tint should complete at/inside the far edge).
@export_range(0.0, 0.25, 0.001) var far_edge_fade_width := 0.06

@export_category("Local Wake Map")
@export var local_wake_enabled := true
@export var use_gpu_wake_map := true
@export_range(64, 2048, 64) var local_wake_resolution := 384
@export_range(8.0, 512.0, 1.0) var local_wake_world_size_m := 92.0
@export_range(1.0, 120.0, 1.0) var local_wake_update_hz := 36.0
@export_range(8.0, 512.0, 1.0) var local_interaction_radius_m := 118.0
@export_range(0, 16, 1) var near_hero_ripples := 14

@export_category("Swell And Chop")
@export_range(0.0, 4.0, 0.01) var swell_height_m := 0.20
@export_range(0.001, 1.0, 0.001) var swell_frequency := 0.070
@export_range(0.0, 4.0, 0.01) var swell_speed := 0.68
@export_range(0.0, 2.0, 0.01) var near_chop_normal_strength := 0.92
@export_range(0.0, 2.0, 0.01) var far_chop_normal_strength := 0.58
@export_range(0.0, 2.0, 0.01) var micro_chop_strength := 0.60
@export_range(0.1, 96.0, 0.1) var micro_chop_scale := 18.0
@export_range(0.0, 2.0, 0.01) var detail_chop_strength := 0.16
@export_range(1.0, 128.0, 0.1) var detail_chop_scale := 52.0
@export_range(0.25, 2.0, 0.01) var far_swell_height_scale := 1.25

@export_category("Whitecaps And Horizon")
@export_range(0.0, 2.0, 0.01) var whitecap_strength := 0.18
@export_range(0.02, 4.0, 0.01) var whitecap_scale := 0.22
@export_range(0.0, 2.0, 0.01) var far_whitecap_scale := 0.75
@export var horizon_color := Color(0.32, 0.55, 0.66, 1.0)
@export_range(0.0, 1.0, 0.01) var near_horizon_fade_strength := 0.12
@export_range(0.0, 1.0, 0.01) var far_horizon_fade_strength := 0.54
@export_range(8.0, 4096.0, 1.0) var horizon_fade_start_m := 260.0
# Must complete at or inside the far tier's half-extent (far_mesh_size_m * 0.5),
# else the horizon tint never finishes before the geometry ends and the mesh edge
# stays water-coloured. Default far_mesh_size_m=1850 -> half-extent 925, so 880
# completes the fade just inside the rim. world_lod() raises both together.
@export_range(16.0, 8192.0, 1.0) var horizon_fade_end_m := 880.0

@export_category("Color And Reflection")
@export var near_shallow_color := Color(0.014, 0.25, 0.31, 1.0)
@export var near_deep_color := Color(0.0, 0.034, 0.095, 1.0)
@export var far_shallow_color := Color(0.038, 0.24, 0.31, 1.0)
@export var far_deep_color := Color(0.0, 0.045, 0.12, 1.0)
@export var foam_color := Color(0.88, 0.97, 1.0, 1.0)
@export_range(0.0, 1.0, 0.01) var near_alpha := 0.975
@export_range(0.0, 1.0, 0.01) var far_alpha := 0.945
@export_range(0.0, 1.0, 0.01) var near_reflection_strength := 0.18
@export_range(0.0, 1.0, 0.01) var near_grazing_reflection_strength := 0.36
@export var distant_reflection_color := Color(0.12, 0.30, 0.42, 1.0)
@export_enum("Off", "Cheap Color") var distant_reflection_mode: int = DistantReflectionMode.CHEAP_COLOR
@export_range(0.0, 1.0, 0.01) var distant_reflection_strength := 0.24
@export_range(0.0, 1.0, 0.01) var distant_grazing_reflection_strength := 0.20
@export_range(0.0, 1.0, 0.01) var distant_surface_gloss := 0.42


func apply_to_surface(surface: Object, far_surface: bool) -> void:
	if surface == null:
		return

	var visual := make_far_visual_profile() if far_surface else make_near_visual_profile()
	_set_if_has(surface, "visual_profile", visual)
	_set_if_has(surface, "follow_camera", false)
	_set_if_has(surface, "target_camera", null)
	_set_if_has(surface, "auto_create_wake_map", local_wake_enabled and not far_surface)
	_set_if_has(surface, "route_wakes_to_wake_map", local_wake_enabled and not far_surface)
	_set_if_has(surface, "interactions_enabled", not far_surface)
	_set_if_has(surface, "use_gpu_wake_map", use_gpu_wake_map)
	_set_if_has(surface, "wake_map_resolution", local_wake_resolution)
	_set_if_has(surface, "wake_map_world_size_m", local_wake_world_size_m)
	_set_if_has(surface, "wake_map_update_hz", local_wake_update_hz)
	_set_if_has(surface, "max_interaction_distance_m", local_interaction_radius_m)
	_set_if_has(surface, "max_hero_ripples", 0 if far_surface else near_hero_ripples)
	# Far/background ocean has no foam sources; skip the 3-call foam-noise stack there
	# to make far water genuinely cheap. Keep it on the near tier and whenever the
	# profile opts the far tier into near-grade detail.
	var far_foam_detail := far_micro_detail_enabled or not high_quality_near_only
	_set_if_has(surface, "foam_detail_enabled", far_foam_detail if far_surface else true)
	_set_if_has(surface, "wake_map_strength", 0.0 if far_surface else 0.52)
	_set_if_has(surface, "splash_pool_size", 0 if far_surface else 18)
	_set_if_has(surface, "auto_create_bubbles", not far_surface)
	_set_if_has(surface, "mesh_edge_fade_width", far_edge_fade_width if far_surface else near_edge_fade_width)
	if surface.has_method("apply_visual_profile"):
		surface.call("apply_visual_profile", visual)
	_set_if_has(surface, "mesh_size_m", far_mesh_size_m if far_surface else near_mesh_size_m)
	_set_if_has(surface, "mesh_subdivisions", far_mesh_subdivisions if far_surface else near_mesh_subdivisions)
	if surface.has_method("rebuild_water_mesh"):
		surface.call("rebuild_water_mesh")


func make_near_visual_profile() -> Resource:
	var profile := VISUAL_PROFILE_SCRIPT.cheap_ocean()
	_apply_common_visual_profile(profile, false)
	return profile


func make_far_visual_profile() -> Resource:
	var profile := VISUAL_PROFILE_SCRIPT.cheap_ocean()
	_apply_common_visual_profile(profile, true)
	return profile


static func open_world() -> Resource:
	return _new_profile()


static func performance() -> Resource:
	var profile := _new_profile()
	profile.near_mesh_subdivisions = 88
	profile.far_mesh_subdivisions = 36
	profile.local_wake_resolution = 256
	profile.local_wake_update_hz = 24.0
	profile.swell_height_m = 0.16
	profile.micro_chop_strength = 0.46
	profile.detail_chop_strength = 0.0
	profile.whitecap_strength = 0.08
	profile.match_macro_appearance_across_lod = true
	profile.high_quality_near_only = true
	profile.far_micro_detail_enabled = false
	profile.far_horizon_fade_strength = 0.62
	profile.distant_reflection_strength = 0.16
	return profile


static func world_lod() -> Resource:
	var profile := _new_profile()
	profile.near_mesh_size_m = 240.0
	profile.near_mesh_subdivisions = 128
	profile.far_mesh_size_m = 2400.0
	profile.far_mesh_subdivisions = 40
	profile.local_wake_resolution = 320
	profile.local_wake_update_hz = 30.0
	profile.local_wake_world_size_m = 104.0
	profile.near_follow_snap_m = 6.0
	profile.far_follow_snap_m = 128.0
	# far_mesh_size_m=2400 -> half-extent 1200; push the horizon fade out to match so
	# the broad-water profile keeps ocean character farther before meeting the sky.
	profile.horizon_fade_end_m = 1150.0
	profile.match_macro_appearance_across_lod = true
	profile.high_quality_near_only = true
	profile.far_micro_detail_enabled = false
	return profile


static func _new_profile() -> Resource:
	var script := load("res://addons/fast_water/scripts/fast_water_ocean_profile.gd") as Script
	return script.new() as Resource


func _apply_common_visual_profile(profile: Resource, far_surface: bool) -> void:
	var macro_matched := far_surface and match_macro_appearance_across_lod
	var far_uses_near_detail := far_surface and not high_quality_near_only
	_set_if_has(profile, "shallow_color", near_shallow_color if macro_matched else (far_shallow_color if far_surface else near_shallow_color))
	_set_if_has(profile, "deep_color", near_deep_color if macro_matched else (far_deep_color if far_surface else near_deep_color))
	_set_if_has(profile, "foam_color", foam_color)
	_set_if_has(profile, "alpha", far_alpha if far_surface else near_alpha)
	_set_if_has(profile, "wave_height", swell_height_m if macro_matched else swell_height_m * (far_swell_height_scale if far_surface else 1.0))
	_set_if_has(profile, "wave_frequency", swell_frequency)
	_set_if_has(profile, "wave_speed", swell_speed)
	_set_if_has(profile, "normal_strength", near_chop_normal_strength if macro_matched else (far_chop_normal_strength if far_surface else near_chop_normal_strength))
	var micro_strength := micro_chop_strength
	if far_surface:
		micro_strength = micro_chop_strength if far_uses_near_detail else (micro_chop_strength * 0.62 if far_micro_detail_enabled else 0.0)
	_set_if_has(profile, "micro_normal_strength", micro_strength)
	_set_if_has(profile, "micro_normal_scale", micro_chop_scale)
	_set_if_has(profile, "detail_normal_strength", detail_chop_strength if (not far_surface or far_uses_near_detail) else 0.0)
	_set_if_has(profile, "detail_normal_scale", detail_chop_scale)
	_set_if_has(profile, "whitecap_strength", whitecap_strength if macro_matched else whitecap_strength * (far_whitecap_scale if far_surface else 1.0))
	_set_if_has(profile, "whitecap_scale", whitecap_scale)
	_set_if_has(profile, "horizon_color", horizon_color)
	_set_if_has(profile, "horizon_fade_strength", far_horizon_fade_strength if far_surface else near_horizon_fade_strength)
	_set_if_has(profile, "horizon_fade_start_m", horizon_fade_start_m)
	_set_if_has(profile, "horizon_fade_end_m", horizon_fade_end_m)
	_set_if_has(profile, "wake_map_resolution", local_wake_resolution)
	_set_if_has(profile, "wake_map_world_size_m", local_wake_world_size_m)
	_set_if_has(profile, "wake_map_update_hz", local_wake_update_hz)
	_set_if_has(profile, "use_gpu_wake_map", use_gpu_wake_map)
	if far_surface:
		_set_if_has(profile, "reflection_color", distant_reflection_color)
		_set_if_has(profile, "reflection_strength", distant_reflection_strength if distant_reflection_mode == DistantReflectionMode.CHEAP_COLOR else 0.0)
		_set_if_has(profile, "grazing_reflection_strength", distant_grazing_reflection_strength if distant_reflection_mode == DistantReflectionMode.CHEAP_COLOR else 0.0)
		_set_if_has(profile, "planar_reflection_strength", 0.0)
		_set_if_has(profile, "surface_gloss", distant_surface_gloss)
		_set_if_has(profile, "refraction_strength", 0.0)
		_set_if_has(profile, "refracted_scene_strength", 0.0)
	else:
		_set_if_has(profile, "reflection_color", distant_reflection_color)
		_set_if_has(profile, "reflection_strength", near_reflection_strength)
		_set_if_has(profile, "grazing_reflection_strength", near_grazing_reflection_strength)
		_set_if_has(profile, "surface_gloss", 0.66)


func _set_if_has(object: Object, property_name: String, value: Variant) -> void:
	if object == null:
		return
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			object.set(property_name, value)
			return
