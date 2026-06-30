extends MeshInstance3D
class_name FastWaterWakeRibbon

const WAKE_RIBBON_SHADER := "res://addons/fast_water/shaders/fast_wake_ribbon.gdshader"

@export var target: Node3D
@export var water_y := 0.018
@export_range(0.02, 2.0, 0.01) var width_m := 0.18
@export_range(4, 128, 1) var max_points := 56
@export_range(0.01, 2.0, 0.01) var min_point_distance_m := 0.08
@export_range(0.1, 8.0, 0.01) var lifetime_s := 2.8
@export_range(0.0, 3.0, 0.01) var brightness := 1.15

var _points := PackedVector3Array()
var _ages := PackedFloat32Array()
var _material: ShaderMaterial


func _ready() -> void:
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_material = ShaderMaterial.new()
	_material.render_priority = 4
	var shader := load(WAKE_RIBBON_SHADER) as Shader
	if shader != null:
		_material.shader = shader
	material_override = _material


func _process(delta: float) -> void:
	if target != null:
		var p := target.global_position
		_add_point(Vector3(p.x, water_y, p.z))

	_age_points(delta)
	_rebuild_mesh()
	if _material != null:
		_material.set_shader_parameter("brightness", brightness)


func clear() -> void:
	_points.clear()
	_ages.clear()
	mesh = null


func _add_point(p: Vector3) -> void:
	if _points.size() > 0 and _points[_points.size() - 1].distance_to(p) < min_point_distance_m:
		return

	_points.append(p)
	_ages.append(0.0)

	while _points.size() > max_points:
		_points.remove_at(0)
		_ages.remove_at(0)


func _age_points(delta: float) -> void:
	var i := _ages.size() - 1
	while i >= 0:
		_ages[i] += delta
		if _ages[i] > lifetime_s:
			_points.remove_at(i)
			_ages.remove_at(i)
		i -= 1


func _rebuild_mesh() -> void:
	if _points.size() < 2:
		mesh = null
		return

	var verts := PackedVector3Array()
	var uvs := PackedVector2Array()
	var uv2s := PackedVector2Array()
	var indices := PackedInt32Array()
	var n := _points.size()

	for i in range(n):
		var prev := _points[max(i - 1, 0)]
		var next := _points[min(i + 1, n - 1)]
		var dir := (next - prev)
		if dir.length_squared() < 0.0001:
			dir = Vector3.FORWARD
		dir = dir.normalized()
		var side := Vector3(-dir.z, 0.0, dir.x)
		var age_n := clamp(_ages[i] / max(lifetime_s, 0.001), 0.0, 1.0)
		var width: float = width_m * (1.0 - age_n * 0.72)
		var along := float(i) / float(max(n - 1, 1))
		var p := _points[i]
		verts.append(p - side * width)
		verts.append(p + side * width)
		uvs.append(Vector2(along, 0.0))
		uvs.append(Vector2(along, 1.0))
		uv2s.append(Vector2(age_n, 0.0))
		uv2s.append(Vector2(age_n, 0.0))

	for i in range(n - 1):
		var a := i * 2
		var b := a + 1
		var c := a + 2
		var d := a + 3
		indices.append_array(PackedInt32Array([a, c, b, b, c, d]))

	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_TEX_UV2] = uv2s
	arrays[Mesh.ARRAY_INDEX] = indices

	var m := ArrayMesh.new()
	m.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh = m
