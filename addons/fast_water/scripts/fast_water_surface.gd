@tool
extends Node3D
class_name FastWaterSurface

## Emitted when a splash is actually registered (passes interaction gates). Lets
## gameplay/audio react without polling. world_pos is where, strength is 0.15..2.5.
signal splashed(world_pos: Vector3, strength: float)
## Emitted when a moving-contact wake point is registered.
signal wake_added(world_pos: Vector3, velocity: Vector3)

const SHADER_PATH := "res://addons/fast_water/shaders/fast_water_surface.gdshader"
const WAKE_MAP_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_wake_map.gd")
const GPU_WAKE_MAP_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_gpu_wake_map.gd")
const SPLASH_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_splash_fx.gd")
const BUBBLE_POOL_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_bubble_pool.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const MAX_SHADER_RIPPLES := 16

@export_category("Mesh")
@export var target_camera: Camera3D
@export var follow_camera := true
@export_range(1.0, 64.0, 0.5) var follow_snap_m := 8.0
@export_range(16.0, 2048.0, 1.0) var mesh_size_m := 160.0
@export_range(1, 256, 1) var mesh_subdivisions := 96
@export var auto_create_mesh := true
@export_range(1, 1048575, 1) var visual_layers := 1
# Host floating-origin support: subtracted from world XZ before the procedural wave
# math (shader + sample_wave_height) so coordinates stay small far from the world
# origin. Keep identical across ocean tiers. Default zero = sample at true world XZ.
@export var wave_sample_offset_m := Vector2.ZERO

@export_category("Material")
@export var water_material: ShaderMaterial
@export var wake_map: Texture2D
@export var visual_profile: Resource

@export_category("Body Contract")
@export var body_profile: Resource

@export_category("Debug")
@export_range(0, 7, 1) var debug_view := 0

@export_category("Runtime Wake Map")
@export var auto_create_wake_map := true
@export var wake_map_node: Node
@export var use_gpu_wake_map := false
@export_range(64, 1024, 64) var wake_map_resolution := 256
@export_range(8.0, 512.0, 1.0) var wake_map_world_size_m := 96.0
@export_range(1.0, 120.0, 1.0) var wake_map_update_hz := 30.0
@export var route_wakes_to_wake_map := true

@export_category("Flow Field")
@export var flow_field_node: Node
@export var flow_field_map: Texture2D
@export var flow_field_enabled := true
@export_range(0.0, 4.0, 0.01) var flow_field_strength := 1.0
@export_range(0.0, 4.0, 0.01) var flow_foam_strength := 1.0
@export_range(0.0, 4.0, 0.01) var flow_normal_strength := 1.0
@export_range(0.0, 2.0, 0.01) var flow_advection_strength := 0.28
@export_range(0.1, 64.0, 0.1) var flow_encode_scale_mps := 8.0

@export_category("Foam Field")
@export var auto_create_foam_field := false
@export var foam_field_node: Node
@export var foam_field_map: Texture2D
@export var foam_field_enabled := true
@export_range(0.0, 4.0, 0.01) var foam_field_strength := 1.0

@export_category("Look")
@export var shallow_color := Color(0.025, 0.22, 0.30, 1.0)
@export var deep_color := Color(0.0, 0.040, 0.095, 1.0)
@export var foam_color := Color(0.85, 0.95, 1.0, 1.0)
@export_range(0.0, 1.0, 0.01) var alpha := 0.965
@export_range(0.0, 4.0, 0.01) var wave_height := 0.045
@export_range(0.001, 1.0, 0.001) var wave_frequency := 0.30
@export_range(0.0, 4.0, 0.01) var wave_speed := 1.25
## Optional Gerstner displacement (peaked crests / broad troughs). Off keeps the
## proven sine look; the height query inverts it so buoyancy stays aligned.
@export var gerstner_enabled := false
@export_range(0.0, 1.0, 0.01) var gerstner_choppiness := 0.0
@export_range(0.0, 2.0, 0.01) var normal_strength := 1.45
@export_range(0.0, 2.0, 0.01) var micro_normal_strength := 0.82
@export_range(0.1, 32.0, 0.01) var micro_normal_scale := 13.0
@export_range(0.0, 2.0, 0.01) var fresnel_strength := 0.45
@export_range(0.0, 0.12, 0.001) var refraction_strength := 0.0
@export_range(0.0, 1.0, 0.01) var refracted_scene_strength := 0.18
@export var reflection_color := Color(0.10, 0.25, 0.36, 1.0)
@export_range(0.0, 1.0, 0.01) var reflection_strength := 0.12
@export_range(0.0, 1.0, 0.01) var grazing_reflection_strength := 0.32
@export_range(0.0, 4.0, 0.01) var wake_highlight_strength := 1.15
@export_range(0.0, 1.0, 0.01) var surface_gloss := 0.62
@export_range(0.0, 2.0, 0.01) var detail_normal_strength := 0.0
@export_range(1.0, 96.0, 0.1) var detail_normal_scale := 32.0
@export_range(0.0, 3.0, 0.01) var foam_intensity := 1.0
@export_range(0.0, 3.0, 0.01) var shoreline_foam_strength := 1.0
@export_range(0.0, 3.0, 0.01) var wake_foam_strength := 1.0
@export_range(0.0, 1.0, 0.01) var foam_breakup_strength := 0.0
@export_range(0.25, 16.0, 0.01) var foam_noise_scale := 3.0
@export var foam_detail_enabled := true
@export_range(0.0, 2.0, 0.01) var whitecap_strength := 0.0
@export_range(0.02, 4.0, 0.01) var whitecap_scale := 0.32
@export_range(0.0, 1.0, 0.01) var depth_absorption_strength := 0.85
@export_range(0.0, 2.0, 0.01) var absorption_density := 0.28
@export var sun_glint_color := Color(1.0, 0.96, 0.82, 1.0)
@export_range(0.0, 4.0, 0.01) var sun_glint_strength := 0.0
@export_range(1.0, 96.0, 0.1) var sun_glint_sharpness := 28.0
@export var sun_glint_direction := Vector2(0.9, 0.25)
@export_range(0.0, 0.02, 0.0001) var planar_reflection_softness := 0.004
@export_range(0.25, 8.0, 0.01) var planar_reflection_grazing_power := 3.25
@export_range(0.0, 1.0, 0.01) var planar_reflection_max_mix := 0.48
@export_range(0.1, 2.0, 0.01) var final_color_gain := 1.08
@export var horizon_color := Color(0.34, 0.56, 0.66, 1.0)
@export_range(0.0, 1.0, 0.01) var horizon_fade_strength := 0.0
@export_range(8.0, 4096.0, 1.0) var horizon_fade_start_m := 280.0
@export_range(16.0, 8192.0, 1.0) var horizon_fade_end_m := 920.0
@export_range(0.0, 0.25, 0.001) var mesh_edge_fade_width := 0.0
@export_category("Distant Specular LOD")
@export_range(8.0, 4096.0, 1.0) var specular_lod_start_m := 90.0
@export_range(16.0, 8192.0, 1.0) var specular_lod_end_m := 700.0
@export_range(0.0, 1.0, 0.01) var distant_specular_roughness := 0.42
@export_range(0.0, 1.0, 0.01) var distant_normal_flatten := 0.45

@export_category("Depth")
@export_range(0.05, 8.0, 0.01) var foam_depth_m := 0.55
@export_range(0.5, 80.0, 0.1) var deep_depth_m := 6.0

@export_category("Interactions")
@export var interactions_enabled := true
@export_range(0, 16, 1) var max_hero_ripples := 12
@export_range(1.0, 256.0, 1.0) var max_interaction_distance_m := 96.0
@export_range(0.0, 3.0, 0.01) var splash_strength_scale := 0.12
@export_range(0.0, 3.0, 0.01) var wake_strength_scale := 0.035
@export_range(0.25, 8.0, 0.01) var ripple_lifetime_s := 2.2
@export_range(0.5, 16.0, 0.01) var ripple_speed_mps := 5.5
@export_range(0.0, 2.0, 0.01) var wake_map_strength := 0.35

@export_category("Splash Pool")
@export var splash_scene: PackedScene
@export_range(0, 64, 1) var splash_pool_size := 24
@export_range(0, 16, 1) var max_splashes_per_physics_frame := 6

@export_category("Bubbles")
@export var auto_create_bubbles := true
@export var bubble_pool_node: Node
@export_range(0, 32, 1) var bubble_pool_size := 8
@export_range(16, 2200, 1) var bubbles_per_emitter := 160
@export_range(0.0, 1.0, 0.01) var splash_bubble_strength := 0.65
@export_range(0.0, 1.0, 0.01) var wake_bubble_strength := 0.18

var _water_mesh: MeshInstance3D
var _fallback_wake_map: ImageTexture
var _fallback_flow_map: ImageTexture
var _fallback_foam_field_map: ImageTexture
var _ripple_pos_time := PackedVector4Array()
var _ripple_shape := PackedVector4Array()
var _ripple_head := 0
var _active_ripple_count := 0
# Script-owned clock for hero ripples. The shader ages each ripple against this
# (uniform hero_ripple_time), NOT against the shader's own TIME -- spawn times are
# stamped from the same clock, so age is always consistent. Using TIME with a
# Time.get_ticks_msec() spawn stamp mismatched the two epochs and made ripples
# render at the wrong radius / as a static grey smear instead of clean rings.
var _ripple_clock := 0.0
var _splash_pool: Array[Node3D] = []
var _splash_head := 0
var _splash_frame := -1
var _splashes_this_frame := 0
var _flow_field_origin_xz := Vector2.ZERO
var _flow_field_world_size_m := 1.0
var _environment_wave_height_scale := 1.0
var _environment_wave_speed_scale := 1.0
var _environment_normal_boost := 0.0
var _environment_foam_boost := 0.0
var _environment_refraction_scale := 1.0
var _environment_deep_color_mix := 0.0
var _environment_deep_color := Color(0.02, 0.12, 0.12, 1.0)
var _environment_sun_glint_boost := 0.0
var _environment_whitecap_strength := 0.0
var _environment_whitecap_scale := 0.32
var _rain_rng := RandomNumberGenerator.new()
var _rain_stamp_remainder := 0.0


func _ready() -> void:
	add_to_group("fast_water_surface")
	add_to_group("fast_water_body")
	_ensure_body_profile()
	_rain_rng.randomize()
	_reset_ripple_arrays()
	_ensure_material()
	_ensure_wake_map()
	_ensure_flow_map()
	_ensure_foam_field_map()
	if visual_profile != null and visual_profile.has_method("apply_to"):
		visual_profile.call("apply_to", self, null, null)
	_ensure_wake_map_node()
	_ensure_foam_field_node()
	if visual_profile != null:
		apply_visual_profile(visual_profile)
	if auto_create_mesh:
		_ensure_mesh()
	_apply_shader_params()
	_push_ripples()
	if not Engine.is_editor_hint():
		_build_splash_pool()
		_ensure_bubble_pool()


func _process(delta: float) -> void:
	if follow_camera and target_camera != null:
		var p := target_camera.global_position
		global_position.x = snapped(p.x, follow_snap_m)
		global_position.z = snapped(p.z, follow_snap_m)
	_ripple_clock += delta
	if water_material != null:
		water_material.set_shader_parameter("hero_ripple_time", _ripple_clock)
	_sync_wake_map()
	_sync_flow_field()
	_sync_foam_field()
	_apply_shader_params()


func rebuild_water_mesh() -> void:
	_ensure_mesh()


func get_surface_height_at(world_position: Vector3) -> float:
	return global_position.y + sample_wave_height(world_position.x, world_position.z)


# Analytic swell/chop height. Mirrors base_wave_height() in
# shaders/fast_water_surface.gdshader so gameplay/buoyancy queries return the
# DISPLACED surface (objects bob with the visible swell) instead of a flat plane.
# Keep the two in sync if either wave stack changes. Uses Time.get_ticks_msec(),
# which equals the shader's global TIME at the default Engine.time_scale of 1.0,
# and the same _environment_* wind/storm scaling applied to the shader uniforms.
#
# Scope: this returns the BASE swell only. The shader's vertex() additionally
# advects the sample by the local flow field (advected_xz) and adds tiny
# wake/foam-field displacement. Those are intentionally omitted here:
#   - Open ocean (FastWaterOcean tiers) has no flow field, so flow advection is
#     exactly zero and this query matches the rendered surface bit-for-bit.
#   - On a surface with a mapped flow field, the rendered swell phase drifts with
#     the current; the height query does not follow that drift (flow maps can be
#     GPU-only with no cheap CPU read, and flow varies per-pixel). Buoyant drift on
#     flowing water comes from get_flow_at() force, not wave phase, so this is a
#     negligible visual-vs-physics offset, not a gameplay correctness issue.
func sample_wave_height(world_x: float, world_z: float) -> float:
	# fmod by the shader TIME rollover window (project default 3600 s) so the query
	# stays phase-aligned with the rendered swell across long sessions.
	var t := fmod(Time.get_ticks_msec() / 1000.0, 3600.0)
	var freq := wave_frequency
	var amp := wave_height * _environment_wave_height_scale
	var speed := wave_speed * _environment_wave_speed_scale
	# Mirror the shader's wave_sample_offset so the query stays aligned with the
	# rendered swell under a host floating origin.
	var p := Vector2(world_x, world_z) - wave_sample_offset_m
	# Gerstner displaces XZ, so the surface point at this world XZ came from a different
	# rest position. Invert with a few fixed-point steps so the height matches the
	# rendered crest, then evaluate the (unchanged) vertical sine stack at the rest point.
	var eval_p := p
	if gerstner_enabled:
		var rest := p
		for _k in range(3):
			rest = p - _gerstner_horizontal(rest, freq, speed, amp, t)
		eval_p = rest
	var h := 0.0
	h += _wave_layer(eval_p, Vector2(0.82, 0.34), freq * 0.58, speed * 0.45, amp * 0.82, t)
	h += _wave_layer(eval_p + Vector2(3.1, -1.7), Vector2(-0.48, 0.88), freq * 0.92, speed * 0.62, amp * 0.40, t)
	h += _wave_layer(eval_p + Vector2(-5.7, 2.4), Vector2(0.16, 0.99), freq * 1.45, speed * 0.95, amp * 0.22, t)
	h += _wave_layer(eval_p + Vector2(1.8, 4.9), Vector2(-0.94, -0.19), freq * 2.10, speed * 1.35, amp * 0.11, t)
	h += _wave_layer(eval_p + Vector2(-2.6, -3.7), Vector2(0.55, -0.83), freq * 2.85, speed * 1.65, amp * 0.055, t)
	return h


func _wave_layer(p: Vector2, dir: Vector2, freq: float, speed: float, amp: float, t: float) -> float:
	return sin(p.dot(dir.normalized()) * freq + t * speed) * amp


# Mirrors gerstner_horizontal() in the shader (same layers/offsets) for height inversion.
func _gerstner_horizontal(p: Vector2, freq: float, speed: float, amp: float, t: float) -> Vector2:
	var d := Vector2.ZERO
	d += _gerstner_layer(p, Vector2(0.82, 0.34), freq * 0.58, speed * 0.45, amp * 0.82, t)
	d += _gerstner_layer(p + Vector2(3.1, -1.7), Vector2(-0.48, 0.88), freq * 0.92, speed * 0.62, amp * 0.40, t)
	d += _gerstner_layer(p + Vector2(-5.7, 2.4), Vector2(0.16, 0.99), freq * 1.45, speed * 0.95, amp * 0.22, t)
	d += _gerstner_layer(p + Vector2(1.8, 4.9), Vector2(-0.94, -0.19), freq * 2.10, speed * 1.35, amp * 0.11, t)
	d += _gerstner_layer(p + Vector2(-2.6, -3.7), Vector2(0.55, -0.83), freq * 2.85, speed * 1.65, amp * 0.055, t)
	return d * gerstner_choppiness


func _gerstner_layer(p: Vector2, dir: Vector2, freq: float, speed: float, amp: float, t: float) -> Vector2:
	var nd := dir.normalized()
	return nd * amp * cos(p.dot(nd) * freq + t * speed)


func get_water_altitude(world_position: Vector3) -> float:
	return world_position.y - get_surface_height_at(world_position)


func get_water_depth_at(world_position: Vector3) -> float:
	if not _contains_xz(world_position, 0.0):
		return 0.0
	return max(-get_water_altitude(world_position), 0.0)


func contains_water_point(world_position: Vector3) -> bool:
	var margin := _read_float(body_profile, "containment_margin_m", 0.05)
	return _contains_xz(world_position, margin) and get_water_altitude(world_position) <= margin


func get_flow_at(world_position: Vector3) -> Vector3:
	if flow_field_node != null and flow_field_node.has_method("sample_flow_at"):
		return flow_field_node.call("sample_flow_at", world_position) as Vector3
	return Vector3.ZERO


func _contains_xz(world_position: Vector3, extra_margin_m: float = 0.0) -> bool:
	var half_size := mesh_size_m * 0.5 + maxf(extra_margin_m, 0.0)
	if half_size <= 0.0:
		return true
	var local := to_local(world_position)
	return absf(local.x) <= half_size and absf(local.z) <= half_size


func add_splash(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if not interactions_enabled:
		return
	if not _is_near_camera(world_pos):
		return
	if not _claim_splash_budget():
		return

	var speed := velocity.length()
	var strength := clamp(speed * splash_strength_scale, 0.15, 2.5)
	_add_hero_ripple(world_pos, strength, max(radius, 0.05), ripple_lifetime_s, ripple_speed_mps)
	_stamp_wake_map(world_pos, velocity, radius * 2.2, strength, 0.9)
	_stamp_foam_field(world_pos, velocity, radius * 2.0, strength * 0.52, FOAM_FIELD_SCRIPT.SourceKind.IMPACT)
	_play_splash_fx(world_pos, strength, radius)
	_play_bubbles(world_pos, strength * splash_bubble_strength, radius)
	splashed.emit(world_pos, strength)


func add_wake_point(world_pos: Vector3, velocity: Vector3, radius: float = 0.5) -> void:
	if not interactions_enabled:
		return
	if not _is_near_camera(world_pos):
		return

	var speed := velocity.length()
	if speed <= 0.01:
		return

	var strength := clamp(speed * wake_strength_scale, 0.025, 0.7)
	if route_wakes_to_wake_map:
		_stamp_wake_map(world_pos, velocity, radius * 1.5, strength, 0.95)
		_add_hero_ripple(world_pos, strength * 0.85, max(radius * 0.42, 0.05), ripple_lifetime_s * 0.36, ripple_speed_mps * 0.72)
	else:
		_add_hero_ripple(world_pos, strength, max(radius * 0.65, 0.05), ripple_lifetime_s * 0.75, ripple_speed_mps)
	_stamp_foam_field(world_pos, velocity, radius * 1.35, strength * 0.65, FOAM_FIELD_SCRIPT.SourceKind.WAKE)
	_play_bubbles(world_pos, strength * wake_bubble_strength, radius)
	wake_added.emit(world_pos, velocity)


func set_wake_map_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float) -> void:
	wake_map = texture
	if water_material == null:
		return
	water_material.set_shader_parameter("wake_map", wake_map if wake_map != null else _fallback_wake_map)
	water_material.set_shader_parameter("wake_map_origin_xz", origin_xz)
	water_material.set_shader_parameter("wake_map_world_size", max(world_size_m, 0.01))


func set_flow_field_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float, encode_scale_mps: float = flow_encode_scale_mps) -> void:
	flow_field_map = texture
	_flow_field_origin_xz = origin_xz
	_flow_field_world_size_m = max(world_size_m, 0.01)
	flow_encode_scale_mps = max(encode_scale_mps, 0.1)
	if water_material == null:
		return
	water_material.set_shader_parameter("flow_field_map", flow_field_map if flow_field_map != null else _fallback_flow_map)
	water_material.set_shader_parameter("flow_field_origin_xz", _flow_field_origin_xz)
	water_material.set_shader_parameter("flow_field_world_size", _flow_field_world_size_m)
	water_material.set_shader_parameter("flow_encode_scale_mps", flow_encode_scale_mps)


func set_foam_field_texture(texture: Texture2D, origin_xz: Vector2, world_size_m: float) -> void:
	foam_field_map = texture
	if water_material == null:
		return
	water_material.set_shader_parameter("foam_field_enabled", foam_field_enabled and foam_field_map != null)
	water_material.set_shader_parameter("foam_field_map", foam_field_map if foam_field_map != null else _fallback_foam_field_map)
	water_material.set_shader_parameter("foam_field_origin_xz", origin_xz)
	water_material.set_shader_parameter("foam_field_world_size", max(world_size_m, 0.01))
	water_material.set_shader_parameter("foam_field_strength", foam_field_strength)


func apply_quality(quality: Resource) -> void:
	if quality == null:
		return
	if _has_property(quality, "wake_map_resolution"):
		wake_map_resolution = int(quality.get("wake_map_resolution"))
	if _has_property(quality, "hero_ripples"):
		max_hero_ripples = int(quality.get("hero_ripples"))
	if _has_property(quality, "mesh_subdivisions"):
		mesh_subdivisions = int(quality.get("mesh_subdivisions"))
	if _has_property(quality, "refraction_strength"):
		refraction_strength = float(quality.get("refraction_strength"))
	if _has_property(quality, "bubble_cap"):
		bubbles_per_emitter = int(max(16, int(quality.get("bubble_cap")) / max(bubble_pool_size, 1)))
	if wake_map_node != null and wake_map_node.has_method("rebuild"):
		wake_map_node.call("rebuild", wake_map_resolution)
	if auto_create_mesh:
		_ensure_mesh()
	_apply_shader_params()


func apply_visual_profile(profile: Resource = visual_profile) -> void:
	if profile == null or not profile.has_method("apply_to"):
		return
	profile.call("apply_to", self, wake_map_node, null)
	_configure_wake_map_node()
	if wake_map_node != null and wake_map_node.has_method("rebuild"):
		wake_map_node.call("rebuild", wake_map_resolution)
	if auto_create_mesh:
		_ensure_mesh()
	_apply_shader_params()


func apply_environment_state(state: Resource, response: Resource = null) -> void:
	if state == null:
		_reset_environment_modifiers()
		return
	if response == null:
		response = WEATHER_RESPONSE_SCRIPT.new()

	var wind_speed := _read_float(state, "wind_speed_mps", 0.0)
	var gust := _read_float(state, "gust_strength", 0.0)
	var rain := _read_float(state, "rain_intensity", 0.0)
	var storm := _read_float(state, "storm_intensity", 0.0)
	var turbidity := _read_float(state, "water_turbidity", 0.0)
	var wind_wave_height := _read_float(response, "wind_wave_height_per_mps", 0.018)
	var wind_wave_cap := _read_float(response, "max_wind_wave_height_boost", 0.55)
	var wind_wave_speed := _read_float(response, "wind_wave_speed_per_mps", 0.018)
	var gust_wave_boost := _read_float(response, "gust_wave_boost", 0.18)
	var storm_normal_boost := _read_float(response, "storm_normal_boost", 0.32)
	var rain_foam_boost := _read_float(response, "rain_foam_boost", 0.20)
	var storm_foam_boost := _read_float(response, "storm_foam_boost", 0.80)
	var turbidity_refraction_loss := _read_float(response, "turbidity_refraction_loss", 0.55)
	var turbidity_deep_color_mix := _read_float(response, "turbidity_deep_color_mix", 0.36)
	var wind_glint_boost := _read_float(response, "wind_glint_boost", 0.35)
	var whitecap_threshold := _read_float(response, "whitecap_wind_threshold_mps", 6.0)
	var whitecap_per_mps := _read_float(response, "whitecap_wind_strength_per_mps", 0.045)
	var gust_whitecap_boost := _read_float(response, "gust_whitecap_boost", 0.22)
	var storm_whitecap_boost := _read_float(response, "storm_whitecap_boost", 0.72)
	var whitecap_noise_scale := _read_float(response, "whitecap_noise_scale", 0.32)

	_environment_wave_height_scale = 1.0 + clamp(wind_speed * wind_wave_height + gust * gust_wave_boost + storm * 0.35, 0.0, wind_wave_cap + storm * 0.65)
	_environment_wave_speed_scale = 1.0 + clamp(wind_speed * wind_wave_speed + storm * 0.32, 0.0, 1.35)
	_environment_normal_boost = clamp(storm * storm_normal_boost + gust * 0.12, 0.0, 1.2)
	_environment_foam_boost = clamp(rain * rain_foam_boost + storm * storm_foam_boost, 0.0, 2.0)
	_environment_refraction_scale = 1.0 - clamp(turbidity * turbidity_refraction_loss, 0.0, 0.95)
	_environment_deep_color_mix = clamp(turbidity * turbidity_deep_color_mix, 0.0, 1.0)
	_environment_sun_glint_boost = clamp(wind_speed * wind_glint_boost * 0.06 + storm * 0.18, 0.0, 1.2)
	_environment_whitecap_strength = clamp(maxf(wind_speed - whitecap_threshold, 0.0) * whitecap_per_mps + gust * gust_whitecap_boost + storm * storm_whitecap_boost, 0.0, 1.35)
	_environment_whitecap_scale = maxf(whitecap_noise_scale, 0.02)
	if _has_property(response, "turbid_deep_color"):
		_environment_deep_color = response.get("turbid_deep_color") as Color

	var wind_dir := _read_vector2(state, "wind_direction_xz", Vector2.ZERO)
	if wind_dir.length_squared() > 0.0001:
		sun_glint_direction = wind_dir.normalized()
	if _has_property(state, "sun_color"):
		sun_glint_color = state.get("sun_color") as Color


func emit_rain_ripples(delta: float, intensity: float, world_center: Vector3, area_size_m: float, wind_velocity: Vector3 = Vector3.ZERO, response: Resource = null, max_stamps: int = 24) -> void:
	if wake_map_node == null or not wake_map_node.has_method("add_stamp"):
		return
	if intensity <= 0.001 or delta <= 0.0 or max_stamps <= 0:
		return
	if response == null:
		response = WEATHER_RESPONSE_SCRIPT.new()

	var rate := _read_float(response, "rain_ripple_rate", 80.0)
	var radius_m := _read_float(response, "rain_ripple_radius_m", 0.16)
	var strength := _read_float(response, "rain_ripple_strength", 0.055)
	_rain_stamp_remainder += rate * clamp(intensity, 0.0, 1.0) * delta
	var stamp_count := min(int(_rain_stamp_remainder), max_stamps)
	_rain_stamp_remainder -= float(stamp_count)
	var half_area: float = max(area_size_m * 0.5, 0.1)
	for _i in range(stamp_count):
		var offset: Vector3 = Vector3(
			_rain_rng.randf_range(-half_area, half_area),
			0.0,
			_rain_rng.randf_range(-half_area, half_area)
		)
		var pos: Vector3 = Vector3(world_center.x + offset.x, 0.0, world_center.z + offset.z)
		pos.y = get_surface_height_at(pos)
		var jitter: Vector3 = Vector3(_rain_rng.randf_range(-0.25, 0.25), 0.0, _rain_rng.randf_range(-0.25, 0.25))
		var velocity: Vector3 = wind_velocity * 0.08 + jitter
		var local_radius: float = radius_m * _rain_rng.randf_range(0.65, 1.45)
		var local_strength: float = strength * _rain_rng.randf_range(0.65, 1.25) * clamp(intensity, 0.0, 1.0)
		_stamp_wake_map(pos, velocity, local_radius, local_strength, 0.18 + intensity * 0.18)
		_stamp_foam_field(pos, velocity, local_radius * 0.9, local_strength * 0.7, FOAM_FIELD_SCRIPT.SourceKind.RAIN)


func _ensure_mesh() -> void:
	_water_mesh = get_node_or_null("WaterMesh") as MeshInstance3D
	if _water_mesh == null:
		_water_mesh = MeshInstance3D.new()
		_water_mesh.name = "WaterMesh"
		add_child(_water_mesh)
		if Engine.is_editor_hint() and get_tree() != null:
			_water_mesh.owner = get_tree().edited_scene_root

	var plane := PlaneMesh.new()
	plane.size = Vector2(mesh_size_m, mesh_size_m)
	plane.subdivide_width = mesh_subdivisions
	plane.subdivide_depth = mesh_subdivisions
	_water_mesh.mesh = plane
	_water_mesh.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_water_mesh.layers = visual_layers
	_water_mesh.material_override = water_material


func _ensure_material() -> void:
	if water_material != null:
		return

	var shader := load(SHADER_PATH) as Shader
	if shader == null:
		push_error("FastWaterSurface could not load shader: " + SHADER_PATH)
		return

	water_material = ShaderMaterial.new()
	water_material.shader = shader


func _ensure_body_profile() -> void:
	if body_profile != null:
		return
	body_profile = BODY_PROFILE_SCRIPT.lake()


func _ensure_wake_map() -> void:
	if _fallback_wake_map != null:
		return

	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, Color(0.5, 0.0, 0.5, 1.0))
	_fallback_wake_map = ImageTexture.create_from_image(image)


func _ensure_flow_map() -> void:
	if _fallback_flow_map != null:
		return

	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, Color(0.5, 0.5, 0.0, 0.0))
	_fallback_flow_map = ImageTexture.create_from_image(image)


func _ensure_foam_field_map() -> void:
	if _fallback_foam_field_map != null:
		return

	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, Color(0.0, 0.0, 0.0, 0.0))
	_fallback_foam_field_map = ImageTexture.create_from_image(image)


func _ensure_wake_map_node() -> void:
	if not auto_create_wake_map:
		return
	if wake_map_node != null:
		return

	var existing := get_node_or_null("WakeMap")
	if existing != null:
		wake_map_node = existing
		return

	wake_map_node = GPU_WAKE_MAP_SCRIPT.new() if use_gpu_wake_map else WAKE_MAP_SCRIPT.new()
	wake_map_node.name = "WakeMap"
	add_child(wake_map_node)
	if Engine.is_editor_hint() and get_tree() != null:
		wake_map_node.owner = get_tree().edited_scene_root
	_configure_wake_map_node()
	if wake_map_node.has_method("rebuild"):
		wake_map_node.call("rebuild", wake_map_resolution)


func _ensure_foam_field_node() -> void:
	if not auto_create_foam_field:
		return
	if foam_field_node != null:
		return

	var existing := get_node_or_null("FoamField")
	if existing != null:
		foam_field_node = existing
		return

	foam_field_node = FOAM_FIELD_SCRIPT.new()
	foam_field_node.name = "FoamField"
	add_child(foam_field_node)
	if Engine.is_editor_hint() and get_tree() != null:
		foam_field_node.owner = get_tree().edited_scene_root
	_configure_foam_field_node()
	if foam_field_node.has_method("rebuild"):
		foam_field_node.call("rebuild")


func _configure_wake_map_node() -> void:
	if wake_map_node == null:
		return
	if _has_property(wake_map_node, "target_camera"):
		wake_map_node.set("target_camera", target_camera if follow_camera else null)
	if _has_property(wake_map_node, "resolution"):
		wake_map_node.set("resolution", wake_map_resolution)
	if _has_property(wake_map_node, "world_size_m"):
		wake_map_node.set("world_size_m", wake_map_world_size_m)
	if _has_property(wake_map_node, "update_hz"):
		wake_map_node.set("update_hz", wake_map_update_hz)


func _configure_foam_field_node() -> void:
	if foam_field_node == null:
		return
	if foam_field_node.get_parent() != self:
		return
	if _has_property(foam_field_node, "origin_xz"):
		foam_field_node.set("origin_xz", Vector2(global_position.x, global_position.z))
	if _has_property(foam_field_node, "world_size_m"):
		foam_field_node.set("world_size_m", mesh_size_m)


func _sync_wake_map() -> void:
	if wake_map_node == null:
		return
	_configure_wake_map_node()
	if _has_property(wake_map_node, "texture") and _has_property(wake_map_node, "origin_xz") and _has_property(wake_map_node, "world_size_m"):
		var tex := wake_map_node.get("texture") as Texture2D
		var origin: Vector2 = wake_map_node.get("origin_xz")
		var size := float(wake_map_node.get("world_size_m"))
		if tex != null:
			set_wake_map_texture(tex, origin, size)


func _sync_foam_field() -> void:
	if foam_field_node == null:
		var existing := get_node_or_null("FoamField")
		if existing != null:
			foam_field_node = existing
	if foam_field_node == null:
		return
	_configure_foam_field_node()
	if foam_field_node.has_method("bind_to_material"):
		foam_field_node.call("bind_to_material", water_material, foam_field_strength)
	if _has_property(foam_field_node, "texture") and _has_property(foam_field_node, "origin_xz") and _has_property(foam_field_node, "world_size_m"):
		var tex := foam_field_node.get("texture") as Texture2D
		var origin: Vector2 = foam_field_node.get("origin_xz")
		var size := float(foam_field_node.get("world_size_m"))
		if tex != null:
			set_foam_field_texture(tex, origin, size)


func _sync_flow_field() -> void:
	if flow_field_node == null:
		var existing := get_node_or_null("FlowField")
		if existing != null:
			flow_field_node = existing
	if flow_field_node == null:
		return
	if _has_property(flow_field_node, "texture") and _has_property(flow_field_node, "origin_xz") and _has_property(flow_field_node, "world_size_m"):
		var tex := flow_field_node.get("texture") as Texture2D
		var origin: Vector2 = flow_field_node.get("origin_xz")
		var size := float(flow_field_node.get("world_size_m"))
		var encode_scale := flow_encode_scale_mps
		if _has_property(flow_field_node, "flow_encode_scale_mps"):
			encode_scale = float(flow_field_node.get("flow_encode_scale_mps"))
		if tex != null:
			set_flow_field_texture(tex, origin, size, encode_scale)


func _apply_shader_params() -> void:
	if water_material == null:
		return
	if _water_mesh != null:
		_water_mesh.layers = visual_layers

	water_material.set_shader_parameter("shallow_color", shallow_color)
	water_material.set_shader_parameter("deep_color", deep_color)
	water_material.set_shader_parameter("foam_color", foam_color)
	water_material.set_shader_parameter("water_alpha", alpha)
	var effective_deep_color := deep_color.lerp(_environment_deep_color, _environment_deep_color_mix)
	water_material.set_shader_parameter("deep_color", effective_deep_color)
	water_material.set_shader_parameter("wave_height", wave_height * _environment_wave_height_scale)
	water_material.set_shader_parameter("wave_frequency", wave_frequency)
	water_material.set_shader_parameter("wave_speed", wave_speed * _environment_wave_speed_scale)
	water_material.set_shader_parameter("gerstner_enabled", gerstner_enabled)
	water_material.set_shader_parameter("gerstner_choppiness", gerstner_choppiness)
	water_material.set_shader_parameter("normal_strength", normal_strength + _environment_normal_boost)
	water_material.set_shader_parameter("micro_normal_strength", micro_normal_strength)
	water_material.set_shader_parameter("micro_normal_scale", micro_normal_scale)
	water_material.set_shader_parameter("fresnel_strength", fresnel_strength)
	water_material.set_shader_parameter("refraction_strength", refraction_strength * _environment_refraction_scale)
	water_material.set_shader_parameter("refracted_scene_strength", refracted_scene_strength)
	water_material.set_shader_parameter("reflection_color", reflection_color)
	water_material.set_shader_parameter("reflection_strength", reflection_strength)
	water_material.set_shader_parameter("grazing_reflection_strength", grazing_reflection_strength)
	water_material.set_shader_parameter("wake_highlight_strength", wake_highlight_strength)
	water_material.set_shader_parameter("surface_gloss", surface_gloss)
	water_material.set_shader_parameter("detail_normal_strength", detail_normal_strength)
	water_material.set_shader_parameter("detail_normal_scale", detail_normal_scale)
	water_material.set_shader_parameter("foam_intensity", foam_intensity + _environment_foam_boost)
	water_material.set_shader_parameter("shoreline_foam_strength", shoreline_foam_strength)
	water_material.set_shader_parameter("wake_foam_strength", wake_foam_strength)
	water_material.set_shader_parameter("foam_breakup_strength", foam_breakup_strength)
	water_material.set_shader_parameter("foam_noise_scale", foam_noise_scale)
	water_material.set_shader_parameter("foam_detail_enabled", foam_detail_enabled)
	water_material.set_shader_parameter("storm_whitecap_strength", clamp(whitecap_strength + _environment_whitecap_strength, 0.0, 2.0))
	water_material.set_shader_parameter("storm_whitecap_scale", maxf(whitecap_scale, _environment_whitecap_scale))
	water_material.set_shader_parameter("depth_absorption_strength", depth_absorption_strength)
	water_material.set_shader_parameter("absorption_density", absorption_density)
	water_material.set_shader_parameter("sun_glint_color", sun_glint_color)
	water_material.set_shader_parameter("sun_glint_strength", sun_glint_strength + _environment_sun_glint_boost)
	water_material.set_shader_parameter("sun_glint_sharpness", sun_glint_sharpness)
	water_material.set_shader_parameter("sun_glint_direction", sun_glint_direction)
	water_material.set_shader_parameter("planar_reflection_softness", planar_reflection_softness)
	water_material.set_shader_parameter("planar_reflection_grazing_power", planar_reflection_grazing_power)
	water_material.set_shader_parameter("planar_reflection_max_mix", planar_reflection_max_mix)
	water_material.set_shader_parameter("final_color_gain", final_color_gain)
	water_material.set_shader_parameter("horizon_color", horizon_color)
	water_material.set_shader_parameter("horizon_fade_strength", horizon_fade_strength)
	water_material.set_shader_parameter("horizon_fade_start_m", horizon_fade_start_m)
	water_material.set_shader_parameter("horizon_fade_end_m", horizon_fade_end_m)
	water_material.set_shader_parameter("mesh_edge_fade_width", mesh_edge_fade_width)
	water_material.set_shader_parameter("wave_sample_offset", wave_sample_offset_m)
	water_material.set_shader_parameter("specular_lod_start_m", specular_lod_start_m)
	water_material.set_shader_parameter("specular_lod_end_m", specular_lod_end_m)
	water_material.set_shader_parameter("distant_specular_roughness", distant_specular_roughness)
	water_material.set_shader_parameter("distant_normal_flatten", distant_normal_flatten)
	water_material.set_shader_parameter("foam_depth_m", foam_depth_m)
	water_material.set_shader_parameter("deep_depth_m", deep_depth_m)
	water_material.set_shader_parameter("wake_map_strength", wake_map_strength)
	water_material.set_shader_parameter("wake_map", wake_map if wake_map != null else _fallback_wake_map)
	water_material.set_shader_parameter("wake_map_origin_xz", Vector2(global_position.x, global_position.z))
	water_material.set_shader_parameter("wake_map_world_size", max(mesh_size_m, 0.01))
	water_material.set_shader_parameter("flow_field_enabled", flow_field_enabled and flow_field_map != null)
	water_material.set_shader_parameter("flow_field_map", flow_field_map if flow_field_map != null else _fallback_flow_map)
	water_material.set_shader_parameter("flow_field_origin_xz", _flow_field_origin_xz)
	water_material.set_shader_parameter("flow_field_world_size", max(_flow_field_world_size_m, 0.01))
	water_material.set_shader_parameter("flow_field_strength", flow_field_strength)
	water_material.set_shader_parameter("flow_foam_strength", flow_foam_strength)
	water_material.set_shader_parameter("flow_normal_strength", flow_normal_strength)
	water_material.set_shader_parameter("flow_advection_strength", flow_advection_strength)
	water_material.set_shader_parameter("flow_encode_scale_mps", flow_encode_scale_mps)
	water_material.set_shader_parameter("foam_field_enabled", foam_field_enabled and foam_field_map != null)
	water_material.set_shader_parameter("foam_field_map", foam_field_map if foam_field_map != null else _fallback_foam_field_map)
	water_material.set_shader_parameter("foam_field_origin_xz", Vector2(global_position.x, global_position.z))
	water_material.set_shader_parameter("foam_field_world_size", max(mesh_size_m, 0.01))
	water_material.set_shader_parameter("foam_field_strength", foam_field_strength)
	water_material.set_shader_parameter("debug_view", debug_view)
	water_material.set_shader_parameter("hero_ripple_count", min(max_hero_ripples, _active_ripple_count))
	_sync_wake_map()
	_sync_flow_field()
	_sync_foam_field()


func _reset_ripple_arrays() -> void:
	_ripple_pos_time.clear()
	_ripple_shape.clear()
	_ripple_pos_time.resize(MAX_SHADER_RIPPLES)
	_ripple_shape.resize(MAX_SHADER_RIPPLES)
	for _i in range(MAX_SHADER_RIPPLES):
		_ripple_pos_time[_i] = Vector4(0.0, 0.0, -9999.0, 0.0)
		_ripple_shape[_i] = Vector4(0.5, 4.0, 0.1, 0.0)
	_ripple_head = 0
	_active_ripple_count = 0


func _add_hero_ripple(world_pos: Vector3, strength: float, radius: float, lifetime: float, speed: float) -> void:
	if water_material == null:
		return

	# Stamp spawn time from the same clock the shader ages ripples against.
	var now := _ripple_clock
	_ripple_pos_time[_ripple_head] = Vector4(world_pos.x, world_pos.z, now, strength)
	_ripple_shape[_ripple_head] = Vector4(radius, speed, lifetime, 0.0)
	_ripple_head = (_ripple_head + 1) % MAX_SHADER_RIPPLES
	_active_ripple_count = min(_active_ripple_count + 1, min(max_hero_ripples, MAX_SHADER_RIPPLES))
	_push_ripples()


# Public: emit a clean expanding-ring ripple at a world position. This is the
# robust, omnidirectional disturbance for moving/floating objects -- it can never
# read "reversed" or blocky the way the directional wake map did.
func emit_surface_ripple(world_pos: Vector3, strength: float = 0.3, ring_width: float = 0.28, lifetime: float = 2.4, speed: float = 2.4) -> void:
	if not interactions_enabled:
		return
	_add_hero_ripple(world_pos, strength, max(ring_width, 0.05), lifetime, speed)


func _push_ripples() -> void:
	if water_material == null:
		return
	water_material.set_shader_parameter("hero_ripples", _ripple_pos_time)
	water_material.set_shader_parameter("hero_ripple_data", _ripple_shape)
	water_material.set_shader_parameter("hero_ripple_count", min(max_hero_ripples, _active_ripple_count))


func _is_near_camera(world_pos: Vector3) -> bool:
	if target_camera == null:
		return true
	return target_camera.global_position.distance_squared_to(world_pos) <= max_interaction_distance_m * max_interaction_distance_m


func _claim_splash_budget() -> bool:
	var frame := Engine.get_physics_frames()
	if frame != _splash_frame:
		_splash_frame = frame
		_splashes_this_frame = 0
	if _splashes_this_frame >= max_splashes_per_physics_frame:
		return false
	_splashes_this_frame += 1
	return true


func _build_splash_pool() -> void:
	if splash_pool_size <= 0 or not _splash_pool.is_empty():
		return

	for _i in range(splash_pool_size):
		var fx: Node3D
		if splash_scene != null:
			fx = splash_scene.instantiate() as Node3D
		else:
			fx = SPLASH_FX_SCRIPT.new() as Node3D
		if fx == null:
			continue
		fx.visible = false
		add_child(fx)
		_splash_pool.append(fx)


func _play_splash_fx(world_pos: Vector3, strength: float, radius: float) -> void:
	if _splash_pool.is_empty():
		return

	var fx := _splash_pool[_splash_head]
	_splash_head = (_splash_head + 1) % _splash_pool.size()
	fx.global_position = Vector3(world_pos.x, get_surface_height_at(world_pos), world_pos.z)
	fx.visible = true

	if fx.has_method("restart"):
		fx.call("restart", strength, radius)


func _ensure_bubble_pool() -> void:
	if not auto_create_bubbles:
		return
	if bubble_pool_node != null:
		return

	var existing := get_node_or_null("BubblePool")
	if existing != null:
		bubble_pool_node = existing
		return

	bubble_pool_node = BUBBLE_POOL_SCRIPT.new()
	bubble_pool_node.name = "BubblePool"
	if _has_property(bubble_pool_node, "pool_size"):
		bubble_pool_node.set("pool_size", bubble_pool_size)
	if _has_property(bubble_pool_node, "particles_per_emitter"):
		bubble_pool_node.set("particles_per_emitter", bubbles_per_emitter)
	add_child(bubble_pool_node)


func _play_bubbles(world_pos: Vector3, strength: float, radius: float) -> void:
	if bubble_pool_node == null or strength <= 0.0:
		return
	if bubble_pool_node.has_method("burst"):
		bubble_pool_node.call("burst", Vector3(world_pos.x, get_surface_height_at(world_pos) - 0.05, world_pos.z), strength, radius)


func _stamp_wake_map(world_pos: Vector3, velocity: Vector3, radius: float, strength: float, foam: float) -> void:
	if wake_map_node != null and wake_map_node.has_method("add_stamp"):
		wake_map_node.call("add_stamp", world_pos, velocity, radius, strength, foam)


func _stamp_foam_field(world_pos: Vector3, velocity: Vector3, radius: float, strength: float, source_kind: int) -> void:
	if foam_field_node != null and foam_field_node.has_method("add_foam_stamp"):
		foam_field_node.call("add_foam_stamp", world_pos, radius, strength, source_kind, velocity)


func _reset_environment_modifiers() -> void:
	_environment_wave_height_scale = 1.0
	_environment_wave_speed_scale = 1.0
	_environment_normal_boost = 0.0
	_environment_foam_boost = 0.0
	_environment_refraction_scale = 1.0
	_environment_deep_color_mix = 0.0
	_environment_sun_glint_boost = 0.0
	_environment_whitecap_strength = 0.0
	_environment_whitecap_scale = 0.32


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
