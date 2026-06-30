extends Node3D
class_name FastWaterBubblePool

const BUBBLE_SHADER_PATH := "res://addons/fast_water/shaders/fast_bubble.gdshader"

@export_range(0, 32, 1) var pool_size := 8
@export_range(16, 2200, 1) var particles_per_emitter := 160
@export_range(0.1, 12.0, 0.1) var lifetime_s := 2.6
@export_range(0.0, 8.0, 0.1) var rise_speed := 1.8
@export_range(0.01, 2.0, 0.01) var bubble_size := 0.08
@export var autostart_underwater_column := false

var _emitters: Array[GPUParticles3D] = []
var _head := 0
var _bubble_material: ShaderMaterial


func _ready() -> void:
	_ensure_material()
	_build_pool()


func burst(world_pos: Vector3, strength: float = 1.0, radius: float = 0.5) -> void:
	if _emitters.is_empty():
		return

	var emitter := _emitters[_head]
	_head = (_head + 1) % _emitters.size()
	emitter.global_position = world_pos
	emitter.amount_ratio = clamp(strength, 0.08, 1.0)
	emitter.emitting = false
	emitter.restart()

	var process := emitter.process_material as ParticleProcessMaterial
	if process != null:
		process.emission_sphere_radius = max(radius, 0.02)


func _build_pool() -> void:
	if not _emitters.is_empty():
		return

	for i in range(pool_size):
		var emitter := GPUParticles3D.new()
		emitter.name = "BubbleEmitter%d" % i
		emitter.amount = particles_per_emitter
		emitter.lifetime = lifetime_s
		emitter.one_shot = not autostart_underwater_column
		emitter.emitting = autostart_underwater_column
		emitter.draw_pass_1 = _make_quad_mesh()
		emitter.process_material = _make_process_material()
		emitter.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		add_child(emitter)
		_emitters.append(emitter)


func _ensure_material() -> void:
	if _bubble_material != null:
		return

	var shader := load(BUBBLE_SHADER_PATH) as Shader
	_bubble_material = ShaderMaterial.new()
	if shader != null:
		_bubble_material.shader = shader


func _make_quad_mesh() -> Mesh:
	var mesh := QuadMesh.new()
	mesh.size = Vector2.ONE * bubble_size
	mesh.material = _bubble_material
	return mesh


func _make_process_material() -> ParticleProcessMaterial:
	var mat := ParticleProcessMaterial.new()
	mat.direction = Vector3.UP
	mat.spread = 18.0
	mat.initial_velocity_min = rise_speed * 0.45
	mat.initial_velocity_max = rise_speed
	mat.gravity = Vector3(0.0, 0.18, 0.0)
	mat.scale_min = 0.45
	mat.scale_max = 1.35
	mat.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_SPHERE
	mat.emission_sphere_radius = 0.5
	mat.turbulence_enabled = false
	return mat
