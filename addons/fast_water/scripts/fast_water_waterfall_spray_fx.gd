@tool
extends Node3D
class_name FastWaterWaterfallSprayFx

const PARTICLE_SHADER_PATH := "res://addons/fast_water/shaders/fast_splash_particle.gdshader"

@export var enabled := true:
	set(value):
		enabled = value
		_apply_emitting()
@export var auto_create_visuals := true

@export_category("Lip Mist")
@export_range(0, 4096, 1) var lip_particle_cap := 220:
	set(value):
		lip_particle_cap = max(value, 0)
		_configure_emitters()
@export_range(0.05, 8.0, 0.01) var lip_lifetime_s := 1.15:
	set(value):
		lip_lifetime_s = maxf(value, 0.05)
		_configure_emitters()
@export_range(0.005, 0.60, 0.001) var lip_particle_size_m := 0.075:
	set(value):
		lip_particle_size_m = maxf(value, 0.005)
		_rebuild_meshes()

@export_category("Shelf Spray")
@export_range(0, 4096, 1) var shelf_particle_cap := 160:
	set(value):
		shelf_particle_cap = max(value, 0)
		_configure_emitters()
@export_range(0.05, 8.0, 0.01) var shelf_lifetime_s := 0.82:
	set(value):
		shelf_lifetime_s = maxf(value, 0.05)
		_configure_emitters()
@export_range(0.005, 0.60, 0.001) var shelf_particle_size_m := 0.060:
	set(value):
		shelf_particle_size_m = maxf(value, 0.005)
		_rebuild_meshes()

@export_category("Plunge Mist")
@export_range(0, 8192, 1) var plunge_particle_cap := 420:
	set(value):
		plunge_particle_cap = max(value, 0)
		_configure_emitters()
@export_range(0.05, 12.0, 0.01) var plunge_lifetime_s := 1.55:
	set(value):
		plunge_lifetime_s = maxf(value, 0.05)
		_configure_emitters()
@export_range(0.005, 0.80, 0.001) var plunge_particle_size_m := 0.115:
	set(value):
		plunge_particle_size_m = maxf(value, 0.005)
		_rebuild_meshes()

@export_category("Response")
@export_range(0.0, 1.0, 0.01) var lip_visibility := 0.52
@export_range(0.0, 1.0, 0.01) var shelf_visibility := 0.72
@export_range(0.0, 1.0, 0.01) var plunge_visibility := 0.88
@export_range(0.0, 2.0, 0.01) var lip_depth_m := 0.18
@export_range(0.0, 3.0, 0.01) var shelf_radius_m := 0.55
@export_range(0.0, 4.0, 0.01) var plunge_depth_m := 0.72
@export_range(0.0, 1.0, 0.01) var min_visible_strength := 0.02
@export_range(0.0, 0.50, 0.001) var wind_drift_scale := 0.045

var last_strength := 0.0
var last_flow_velocity := Vector3.ZERO
var last_lip_points := PackedVector3Array()
var last_shelf_points := PackedVector3Array()
var last_plunge_points := PackedVector3Array()

var _lip_emitter: GPUParticles3D
var _shelf_emitter: GPUParticles3D
var _plunge_emitter: GPUParticles3D


func _ready() -> void:
	add_to_group("fast_water_waterfall_spray_fx")
	if auto_create_visuals:
		_ensure_emitters()
	_configure_emitters()
	_apply_emitting()


func apply_waterfall(lip_points: PackedVector3Array, shelf_points: PackedVector3Array, plunge_points: PackedVector3Array, flow_velocity: Vector3 = Vector3.ZERO, strength: float = 1.0) -> void:
	last_lip_points = lip_points
	last_shelf_points = shelf_points
	last_plunge_points = plunge_points
	last_flow_velocity = flow_velocity
	last_strength = clampf(strength, 0.0, 1.0)

	_ensure_emitters()
	_configure_emitters()
	_place_line_emitter(_lip_emitter, last_lip_points, lip_depth_m, clampf(last_strength * lip_visibility, 0.0, 1.0), lip_particle_cap)
	_place_shelf_emitter(_shelf_emitter, last_shelf_points, clampf(last_strength * shelf_visibility, 0.0, 1.0), shelf_particle_cap)
	_place_line_emitter(_plunge_emitter, last_plunge_points, plunge_depth_m, clampf(last_strength * plunge_visibility, 0.0, 1.0), plunge_particle_cap)
	_update_flow_response(flow_velocity)


func stop() -> void:
	last_strength = 0.0
	_apply_emitting()


func get_debug_sample() -> Dictionary:
	return {
		"lip": _emitter_sample(_lip_emitter),
		"shelf": _emitter_sample(_shelf_emitter),
		"plunge": _emitter_sample(_plunge_emitter),
		"strength": last_strength,
		"flow_velocity": [last_flow_velocity.x, last_flow_velocity.y, last_flow_velocity.z],
		"lip_point_count": last_lip_points.size(),
		"shelf_point_count": last_shelf_points.size(),
		"plunge_point_count": last_plunge_points.size(),
	}


func _ensure_emitters() -> void:
	if _lip_emitter == null:
		_lip_emitter = _make_emitter("LipMist", lip_particle_cap, lip_lifetime_s, lip_particle_size_m, Color(0.78, 0.93, 1.0, 0.28), false)
		add_child(_lip_emitter)
	if _shelf_emitter == null:
		_shelf_emitter = _make_emitter("ShelfSpray", shelf_particle_cap, shelf_lifetime_s, shelf_particle_size_m, Color(0.86, 0.96, 1.0, 0.46), true)
		add_child(_shelf_emitter)
	if _plunge_emitter == null:
		_plunge_emitter = _make_emitter("PlungeMist", plunge_particle_cap, plunge_lifetime_s, plunge_particle_size_m, Color(0.78, 0.92, 1.0, 0.32), false)
		add_child(_plunge_emitter)


func _make_emitter(emitter_name: String, amount: int, lifetime: float, size_m: float, color: Color, energetic: bool) -> GPUParticles3D:
	var emitter := GPUParticles3D.new()
	emitter.name = emitter_name
	emitter.amount = maxi(amount, 1)
	emitter.lifetime = maxf(lifetime, 0.05)
	emitter.one_shot = false
	emitter.emitting = false
	emitter.amount_ratio = 0.0
	emitter.explosiveness = 0.0
	emitter.randomness = 0.68
	emitter.draw_pass_1 = _make_particle_mesh(color, size_m)
	emitter.process_material = _make_process_material(energetic)
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
	return quad


func _make_process_material(energetic: bool) -> ParticleProcessMaterial:
	var material := ParticleProcessMaterial.new()
	material.direction = Vector3.UP
	material.spread = 72.0 if energetic else 42.0
	material.initial_velocity_min = 0.42 if energetic else 0.12
	material.initial_velocity_max = 1.65 if energetic else 0.62
	material.gravity = Vector3(0.0, -0.18 if energetic else 0.05, 0.0)
	material.scale_min = 0.45
	material.scale_max = 1.75 if energetic else 1.45
	material.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_BOX
	material.emission_box_extents = Vector3(0.5, 0.08, 0.15)
	material.turbulence_enabled = false
	return material


func _configure_emitters() -> void:
	_configure_emitter(_lip_emitter, lip_particle_cap, lip_lifetime_s)
	_configure_emitter(_shelf_emitter, shelf_particle_cap, shelf_lifetime_s)
	_configure_emitter(_plunge_emitter, plunge_particle_cap, plunge_lifetime_s)


func _configure_emitter(emitter: GPUParticles3D, amount: int, lifetime: float) -> void:
	if emitter == null:
		return
	emitter.amount = maxi(amount, 1)
	emitter.lifetime = maxf(lifetime, 0.05)


func _rebuild_meshes() -> void:
	if _lip_emitter != null:
		_lip_emitter.draw_pass_1 = _make_particle_mesh(Color(0.78, 0.93, 1.0, 0.28), lip_particle_size_m)
	if _shelf_emitter != null:
		_shelf_emitter.draw_pass_1 = _make_particle_mesh(Color(0.86, 0.96, 1.0, 0.46), shelf_particle_size_m)
	if _plunge_emitter != null:
		_plunge_emitter.draw_pass_1 = _make_particle_mesh(Color(0.78, 0.92, 1.0, 0.32), plunge_particle_size_m)


func _place_line_emitter(emitter: GPUParticles3D, points: PackedVector3Array, depth_m: float, amount_ratio: float, particle_cap: int) -> void:
	if emitter == null:
		return
	var visible := enabled and amount_ratio >= min_visible_strength and particle_cap > 0 and points.size() >= 2
	emitter.emitting = visible
	emitter.amount_ratio = amount_ratio if visible else 0.0
	if not visible:
		return
	var center := _average_points(points)
	var length := _polyline_length(points)
	emitter.global_transform = Transform3D(_line_basis(points), center)
	var process := emitter.process_material as ParticleProcessMaterial
	if process != null:
		process.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_BOX
		process.emission_box_extents = Vector3(maxf(length * 0.5, 0.05), maxf(depth_m, 0.01), maxf(depth_m * 0.55, 0.03))


func _place_shelf_emitter(emitter: GPUParticles3D, points: PackedVector3Array, amount_ratio: float, particle_cap: int) -> void:
	if emitter == null:
		return
	var visible := enabled and amount_ratio >= min_visible_strength and particle_cap > 0 and not points.is_empty()
	emitter.emitting = visible
	emitter.amount_ratio = amount_ratio if visible else 0.0
	if not visible:
		return
	emitter.global_position = _average_points(points)
	var process := emitter.process_material as ParticleProcessMaterial
	if process != null:
		process.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_SPHERE
		process.emission_sphere_radius = maxf(shelf_radius_m, 0.02)


func _update_flow_response(flow_velocity: Vector3) -> void:
	var drift := Vector3(flow_velocity.x * wind_drift_scale, 0.0, flow_velocity.z * wind_drift_scale)
	_set_gravity(_lip_emitter, drift + Vector3(0.0, 0.05, 0.0))
	_set_gravity(_shelf_emitter, drift + Vector3(0.0, -0.22, 0.0))
	_set_gravity(_plunge_emitter, drift + Vector3(0.0, 0.08, 0.0))


func _set_gravity(emitter: GPUParticles3D, gravity: Vector3) -> void:
	if emitter == null:
		return
	var process := emitter.process_material as ParticleProcessMaterial
	if process != null:
		process.gravity = gravity


func _apply_emitting() -> void:
	var should_emit := enabled and last_strength >= min_visible_strength
	for emitter in [_lip_emitter, _shelf_emitter, _plunge_emitter]:
		if emitter == null:
			continue
		if not should_emit:
			emitter.emitting = false
			emitter.amount_ratio = 0.0


func _line_basis(points: PackedVector3Array) -> Basis:
	var x_axis := Vector3.RIGHT
	if points.size() >= 2:
		x_axis = (points[points.size() - 1] - points[0]).normalized()
	if x_axis.length_squared() <= 0.0001:
		x_axis = Vector3.RIGHT
	var y_axis := Vector3.UP
	var z_axis := x_axis.cross(y_axis).normalized()
	if z_axis.length_squared() <= 0.0001:
		z_axis = Vector3.FORWARD
	y_axis = z_axis.cross(x_axis).normalized()
	return Basis(x_axis, y_axis, z_axis).orthonormalized()


func _average_points(points: PackedVector3Array) -> Vector3:
	if points.is_empty():
		return global_position
	var total := Vector3.ZERO
	for p in points:
		total += p
	return total / float(points.size())


func _polyline_length(points: PackedVector3Array) -> float:
	var total := 0.0
	for i in range(1, points.size()):
		total += points[i - 1].distance_to(points[i])
	return total


func _emitter_sample(emitter: GPUParticles3D) -> Dictionary:
	if emitter == null:
		return {
			"exists": false,
		}
	var process := emitter.process_material as ParticleProcessMaterial
	var extents := Vector3.ZERO
	var radius := 0.0
	var gravity := Vector3.ZERO
	var shape := -1
	if process != null:
		extents = process.emission_box_extents
		radius = process.emission_sphere_radius
		gravity = process.gravity
		shape = process.emission_shape
	return {
		"exists": true,
		"emitting": emitter.emitting,
		"amount": emitter.amount,
		"amount_ratio": emitter.amount_ratio,
		"position": [emitter.global_position.x, emitter.global_position.y, emitter.global_position.z],
		"box_extents": [extents.x, extents.y, extents.z],
		"sphere_radius": radius,
		"gravity": [gravity.x, gravity.y, gravity.z],
		"emission_shape": shape,
	}
