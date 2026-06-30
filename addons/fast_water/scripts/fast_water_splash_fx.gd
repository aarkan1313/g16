extends Node3D
class_name FastWaterSplashFx

const CROWN_SHADER_PATH := "res://addons/fast_water/shaders/fast_splash_crown.gdshader"
const PARTICLE_SHADER_PATH := "res://addons/fast_water/shaders/fast_splash_particle.gdshader"

@export_range(0.1, 5.0, 0.01) var duration_s := 1.1
@export var auto_hide := true
@export var auto_create_visuals := true

var _life := 0.0
var _running := false


func _ready() -> void:
	if auto_create_visuals and get_child_count() == 0:
		_create_default_visuals()


func restart(strength: float = 1.0, radius: float = 1.0) -> void:
	_life = 0.0
	_running = true
	visible = true
	scale = Vector3.ONE * max(radius, 0.01)

	for child in get_children():
		if child is MeshInstance3D:
			var mesh := child as MeshInstance3D
			mesh.set_instance_shader_parameter("start_time", Time.get_ticks_msec() * 0.001)
			mesh.set_instance_shader_parameter("strength", strength)
			mesh.set_instance_shader_parameter("duration", duration_s)
		elif child is GPUParticles3D:
			var particles := child as GPUParticles3D
			particles.amount_ratio = clamp(strength / 2.0, 0.1, 1.0)
			particles.restart()


func _process(delta: float) -> void:
	if not _running:
		return
	_life += delta
	if _life >= duration_s:
		_running = false
		if auto_hide:
			visible = false


func _create_default_visuals() -> void:
	var crown := MeshInstance3D.new()
	crown.name = "Crown"
	crown.mesh = _make_crown_mesh()
	crown.material_override = _make_crown_material()
	crown.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	add_child(crown)

	var spray := GPUParticles3D.new()
	spray.name = "Spray"
	spray.amount = 96
	spray.lifetime = 0.85
	spray.one_shot = true
	spray.explosiveness = 0.85
	spray.draw_pass_1 = _make_particle_mesh(Color(0.82, 0.95, 1.0, 0.62), 0.045)
	spray.process_material = _make_particle_process(2.6, 0.4, 24.0)
	add_child(spray)

	var foam := GPUParticles3D.new()
	foam.name = "Foam"
	foam.amount = 80
	foam.lifetime = 1.2
	foam.one_shot = true
	foam.explosiveness = 0.45
	foam.draw_pass_1 = _make_particle_mesh(Color(0.92, 0.98, 1.0, 0.48), 0.08)
	foam.process_material = _make_particle_process(0.55, 0.0, 80.0)
	add_child(foam)


func _make_crown_mesh() -> Mesh:
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)

	var verts := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()
	var segments := 64

	for i in range(segments):
		var a := TAU * float(i) / float(segments)
		var dir := Vector3(cos(a), 0.0, sin(a))
		verts.append(dir * 0.62)
		verts.append(dir * 1.0 + Vector3.UP * 0.08)
		uvs.append(Vector2(float(i) / float(segments), 0.0))
		uvs.append(Vector2(float(i) / float(segments), 1.0))

	for i in range(segments):
		var n := (i + 1) % segments
		var a0 := i * 2
		var a1 := i * 2 + 1
		var b0 := n * 2
		var b1 := n * 2 + 1
		indices.append_array(PackedInt32Array([a0, b0, a1, a1, b0, b1]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices

	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	return mesh


func _make_crown_material() -> ShaderMaterial:
	var mat := ShaderMaterial.new()
	var shader := load(CROWN_SHADER_PATH) as Shader
	if shader != null:
		mat.shader = shader
	return mat


func _make_particle_mesh(color: Color, size: float) -> Mesh:
	var quad := QuadMesh.new()
	quad.size = Vector2.ONE * size
	var mat := ShaderMaterial.new()
	var shader := load(PARTICLE_SHADER_PATH) as Shader
	if shader != null:
		mat.shader = shader
		mat.set_shader_parameter("particle_color", color)
	quad.material = mat
	return quad


func _make_particle_process(speed: float, gravity_y: float, spread: float) -> ParticleProcessMaterial:
	var mat := ParticleProcessMaterial.new()
	mat.direction = Vector3.UP
	mat.spread = spread
	mat.initial_velocity_min = speed * 0.4
	mat.initial_velocity_max = speed
	mat.gravity = Vector3(0.0, gravity_y, 0.0)
	mat.scale_min = 0.5
	mat.scale_max = 1.35
	mat.emission_shape = ParticleProcessMaterial.EMISSION_SHAPE_SPHERE
	mat.emission_sphere_radius = 0.28
	mat.turbulence_enabled = false
	return mat
