@tool
extends Node3D
class_name FastWaterRainImpactFx

const PARTICLE_SHADER_PATH := "res://addons/fast_water/shaders/fast_splash_particle.gdshader"

@export var enabled := true:
	set(value):
		enabled = value
		_apply_emitting()
@export var auto_create_visuals := true

@export_category("Coverage")
@export_range(1.0, 512.0, 1.0) var area_size_m := 42.0:
	set(value):
		area_size_m = maxf(value, 1.0)
		_configure_emitters()
@export_range(-2.0, 4.0, 0.01) var water_y_offset_m := 0.05
@export_range(0.0, 8.0, 0.01) var mist_height_m := 0.62
@export_range(0.0, 2.0, 0.01) var wind_drift_scale := 0.055

@export_category("Impact Drops")
@export_range(0, 4096, 1) var impact_particle_cap := 420:
	set(value):
		impact_particle_cap = max(value, 0)
		_configure_emitters()
@export_range(0.05, 4.0, 0.01) var impact_lifetime_s := 0.38:
	set(value):
		impact_lifetime_s = maxf(value, 0.05)
		_configure_emitters()
@export_range(0.005, 0.25, 0.001) var impact_size_m := 0.040:
	set(value):
		impact_size_m = maxf(value, 0.005)
		_rebuild_meshes()

@export_category("Surface Mist")
@export_range(0, 4096, 1) var mist_particle_cap := 180:
	set(value):
		mist_particle_cap = max(value, 0)
		_configure_emitters()
@export_range(0.05, 8.0, 0.01) var mist_lifetime_s := 1.10:
	set(value):
		mist_lifetime_s = maxf(value, 0.05)
		_configure_emitters()
@export_range(0.005, 0.50, 0.001) var mist_size_m := 0.105:
	set(value):
		mist_size_m = maxf(value, 0.005)
		_rebuild_meshes()

@export_category("Response")
@export_range(0.0, 2.0, 0.01) var impact_visibility := 0.82
@export_range(0.0, 2.0, 0.01) var mist_visibility := 0.48
@export_range(0.0, 1.0, 0.01) var min_visible_intensity := 0.02

var last_intensity := 0.0
var last_area_size_m := 0.0
var last_wind_velocity := Vector3.ZERO
var last_emitting := false

var _impact_emitter: GPUParticles3D
var _mist_emitter: GPUParticles3D
var _impact_material: ShaderMaterial
var _mist_material: ShaderMaterial


func _ready() -> void:
	add_to_group("fast_water_rain_impact_fx")
	if auto_create_visuals:
		_ensure_emitters()
	_configure_emitters()
	_apply_emitting()


func apply_rain(intensity: float, world_center: Vector3, coverage_m: float, wind_velocity: Vector3 = Vector3.ZERO, response: Resource = null) -> void:
	last_intensity = clampf(intensity, 0.0, 1.0)
	last_area_size_m = maxf(coverage_m, 1.0)
	last_wind_velocity = wind_velocity
	area_size_m = last_area_size_m
	global_position = Vector3(world_center.x, world_center.y + water_y_offset_m, world_center.z)

	var impact_gain := _read_float(response, "rain_impact_visibility", impact_visibility)
	var mist_gain := _read_float(response, "rain_mist_visibility", mist_visibility)
	var visible := enabled and last_intensity >= min_visible_intensity
	last_emitting = visible

	_ensure_emitters()
	_configure_emitters()
	if _impact_emitter != null:
		_impact_emitter.amount_ratio = clampf(last_intensity * impact_gain, 0.0, 1.0) if visible else 0.0
		_impact_emitter.emitting = visible and impact_particle_cap > 0
	if _mist_emitter != null:
		_mist_emitter.amount_ratio = clampf(last_intensity * mist_gain, 0.0, 1.0) if visible else 0.0
		_mist_emitter.emitting = visible and mist_particle_cap > 0
	_update_wind_response(wind_velocity)


func stop() -> void:
	last_intensity = 0.0
	last_emitting = false
	_apply_emitting()


func _ensure_emitters() -> void:
	if _impact_emitter == null:
		_impact_emitter = _make_emitter("RainImpactDrops", impact_particle_cap, impact_lifetime_s, impact_size_m, Color(0.84, 0.96, 1.0, 0.42), false)
		add_child(_impact_emitter)
	if _mist_emitter == null:
		_mist_emitter = _make_emitter("RainSurfaceMist", mist_particle_cap, mist_lifetime_s, mist_size_m, Color(0.76, 0.92, 1.0, 0.24), true)
		add_child(_mist_emitter)


func _make_emitter(emitter_name: String, amount: int, lifetime: float, size_m: float, color: Color, mist: bool) -> GPUParticles3D:
	var emitter := GPUParticles3D.new()
	emitter.name = emitter_name
	emitter.amount = maxi(amount, 1)
	emitter.lifetime = maxf(lifetime, 0.05)
	emitter.one_shot = false
	emitter.emitting = false
	emitter.amount_ratio = 0.0
	emitter.explosiveness = 0.0
	emitter.randomness = 0.62
	emitter.draw_pass_1 = _make_particle_mesh(color, size_m)
	emitter.process_material = _make_process_material(mist)
	emitter.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	return emitter


func _make_particle_mesh(color: Color, size_m: float) -> Mesh:
	var quad := QuadMesh.new()
	quad.size = Vector2.ONE * maxf(size_m, 0.005)
	var material := ShaderMaterial.new()
	var shader := load(PARTICLE_SHADER_PATH) as Shader
	if shader != null:
		material.shader = shader
		material.set_shader_parameter("particle_color", color)
	quad.material = material
	if color.a > 0.30:
		_impact_material = material
	else:
		_mist_material = material
	return quad


func _make_process_material(mist: bool) -> ParticleProcessMaterial:
	var material := ParticleProcessMaterial.new()
	material.direction = Vector3.UP
	material.spread = 52.0 if mist else 82.0
	material.initial_velocity_min = 0.06 if mist else 0.18
	material.initial_velocity_max = 0.34 if mist else 0.95
	material.gravity = Vector3(0.0, 0.07 if mist else -0.42, 0.0)
	material.scale_min = 0.55 if mist else 0.40
	material.scale_max = 1.65 if mist else 1.18
	material.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_BOX
	material.emission_box_extents = Vector3(area_size_m * 0.5, mist_height_m if mist else 0.02, area_size_m * 0.5)
	material.turbulence_enabled = false
	return material


func _configure_emitters() -> void:
	if _impact_emitter != null:
		_impact_emitter.amount = maxi(impact_particle_cap, 1)
		_impact_emitter.lifetime = maxf(impact_lifetime_s, 0.05)
		var process := _impact_emitter.process_material as ParticleProcessMaterial
		if process != null:
			process.emission_box_extents = Vector3(area_size_m * 0.5, 0.02, area_size_m * 0.5)
	if _mist_emitter != null:
		_mist_emitter.amount = maxi(mist_particle_cap, 1)
		_mist_emitter.lifetime = maxf(mist_lifetime_s, 0.05)
		var process := _mist_emitter.process_material as ParticleProcessMaterial
		if process != null:
			process.emission_box_extents = Vector3(area_size_m * 0.5, mist_height_m, area_size_m * 0.5)


func _rebuild_meshes() -> void:
	if _impact_emitter != null:
		_impact_emitter.draw_pass_1 = _make_particle_mesh(Color(0.84, 0.96, 1.0, 0.42), impact_size_m)
	if _mist_emitter != null:
		_mist_emitter.draw_pass_1 = _make_particle_mesh(Color(0.76, 0.92, 1.0, 0.24), mist_size_m)


func _update_wind_response(wind_velocity: Vector3) -> void:
	var drift := Vector3(wind_velocity.x * wind_drift_scale, 0.0, wind_velocity.z * wind_drift_scale)
	if _impact_emitter != null:
		var impact_process := _impact_emitter.process_material as ParticleProcessMaterial
		if impact_process != null:
			impact_process.gravity = drift + Vector3(0.0, -0.42, 0.0)
	if _mist_emitter != null:
		var mist_process := _mist_emitter.process_material as ParticleProcessMaterial
		if mist_process != null:
			mist_process.gravity = drift + Vector3(0.0, 0.07, 0.0)


func _apply_emitting() -> void:
	var should_emit := enabled and last_intensity >= min_visible_intensity
	last_emitting = should_emit
	if _impact_emitter != null:
		_impact_emitter.emitting = should_emit
		if not should_emit:
			_impact_emitter.amount_ratio = 0.0
	if _mist_emitter != null:
		_mist_emitter.emitting = should_emit
		if not should_emit:
			_mist_emitter.amount_ratio = 0.0


func _read_float(object: Object, property_name: String, fallback: float) -> float:
	if object != null:
		for property in object.get_property_list():
			if property.get("name", "") == property_name:
				return float(object.get(property_name))
	return fallback
