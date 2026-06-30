@tool
extends MeshInstance3D
class_name FastWaterBowWake

const SHADER_PATH := "res://addons/fast_water/shaders/fast_bow_wake.gdshader"

@export var target: Node3D
@export var target_path: NodePath
@export var water_surface: Node
@export var water_surface_path: NodePath

@export_category("Shape")
@export_range(0.05, 8.0, 0.01) var inner_radius_m := 0.78:
	set(value):
		inner_radius_m = value
		_mesh_dirty = true

@export_range(0.05, 8.0, 0.01) var outer_radius_m := 1.08:
	set(value):
		outer_radius_m = value
		_mesh_dirty = true

@export_range(0.1, 4.0, 0.01) var lateral_scale := 1.34:
	set(value):
		lateral_scale = value
		_mesh_dirty = true

@export_range(0.1, 4.0, 0.01) var forward_scale := 0.42:
	set(value):
		forward_scale = value
		_mesh_dirty = true

@export_range(0.0, 360.0, 0.1) var arc_start_degrees := 202.0:
	set(value):
		arc_start_degrees = value
		_mesh_dirty = true

@export_range(0.0, 360.0, 0.1) var arc_end_degrees := 338.0:
	set(value):
		arc_end_degrees = value
		_mesh_dirty = true

@export_range(8, 192, 1) var segments := 72:
	set(value):
		segments = value
		_mesh_dirty = true

@export_range(-1.0, 1.0, 0.001) var water_y_offset := 0.035

@export_category("Motion")
@export_range(0.0, 20.0, 0.01) var min_visible_speed_mps := 0.15
@export_range(0.0, 20.0, 0.01) var full_strength_speed_mps := 2.8
@export_range(0.0, 0.5, 0.001) var heading_smoothing := 0.12
@export var align_to_velocity := true

@export_category("Look")
@export var wake_color := Color(0.93, 0.98, 1.0, 1.0)
@export_range(0.0, 6.0, 0.01) var brightness := 1.25
@export_range(0.0, 1.0, 0.01) var alpha := 0.28

var _material: ShaderMaterial
var _last_position := Vector3.ZERO
var _has_last_position := false
var _heading_y := 0.0
var _mesh_dirty := true


func _ready() -> void:
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	_ensure_material()
	_resolve_nodes()
	rebuild_mesh()


func _process(delta: float) -> void:
	_resolve_nodes()
	if _mesh_dirty:
		rebuild_mesh()
	_update_transform(delta)
	_update_material()


func rebuild_mesh() -> void:
	_mesh_dirty = false
	var arrays := []
	arrays.resize(Mesh.ARRAY_MAX)

	var verts := PackedVector3Array()
	var uvs := PackedVector2Array()
	var indices := PackedInt32Array()
	var count := max(segments, 3)
	var start_a := deg_to_rad(arc_start_degrees)
	var end_a := deg_to_rad(arc_end_degrees)
	var inner := min(inner_radius_m, outer_radius_m)
	var outer := max(inner_radius_m, outer_radius_m)

	for i in range(count + 1):
		var t := float(i) / float(count)
		var a := lerp(start_a, end_a, t)
		var dir := Vector2(cos(a), sin(a))
		verts.append(Vector3(dir.x * inner * lateral_scale, 0.0, dir.y * inner * forward_scale))
		verts.append(Vector3(dir.x * outer * lateral_scale, 0.0, dir.y * outer * forward_scale))
		uvs.append(Vector2(t, 0.0))
		uvs.append(Vector2(t, 1.0))

	for i in range(count):
		var a0 := i * 2
		var a1 := a0 + 1
		var b0 := a0 + 2
		var b1 := a0 + 3
		indices.append_array(PackedInt32Array([a0, b0, a1, a1, b0, b1]))

	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_TEX_UV] = uvs
	arrays[Mesh.ARRAY_INDEX] = indices

	var built_mesh := ArrayMesh.new()
	built_mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
	mesh = built_mesh


func _resolve_nodes() -> void:
	if target == null and target_path != NodePath(""):
		target = get_node_or_null(target_path) as Node3D
	if water_surface == null and water_surface_path != NodePath(""):
		water_surface = get_node_or_null(water_surface_path)
	if water_surface == null and get_tree() != null:
		var surfaces := get_tree().get_nodes_in_group("fast_water_surface")
		if not surfaces.is_empty():
			water_surface = surfaces[0]


func _update_transform(delta: float) -> void:
	if target == null:
		visible = false
		return

	var target_pos := target.global_position
	var velocity := Vector3.ZERO
	if _has_last_position and delta > 0.0:
		velocity = (target_pos - _last_position) / delta
	_last_position = target_pos
	_has_last_position = true

	var surface_y := _surface_height(target_pos)
	global_position = Vector3(target_pos.x, surface_y + water_y_offset, target_pos.z)

	var horizontal_velocity := Vector2(velocity.x, velocity.z)
	var speed := horizontal_velocity.length()
	visible = speed >= min_visible_speed_mps or Engine.is_editor_hint()

	if align_to_velocity and speed > 0.001:
		var target_heading := atan2(velocity.x, velocity.z)
		if heading_smoothing <= 0.0:
			_heading_y = target_heading
		else:
			var blend := clamp(delta / max(heading_smoothing, 0.001), 0.0, 1.0)
			_heading_y = lerp_angle(_heading_y, target_heading, blend)
	rotation = Vector3(0.0, _heading_y, 0.0)

	var strength := clamp(speed / max(full_strength_speed_mps, 0.001), 0.0, 1.0)
	transparency = 1.0 - strength


func _surface_height(world_position: Vector3) -> float:
	if water_surface != null and water_surface.has_method("get_surface_height_at"):
		return float(water_surface.call("get_surface_height_at", world_position))
	return global_position.y


func _ensure_material() -> void:
	if _material != null:
		return
	_material = ShaderMaterial.new()
	_material.render_priority = 3
	var shader := load(SHADER_PATH) as Shader
	if shader != null:
		_material.shader = shader
	material_override = _material


func _update_material() -> void:
	_ensure_material()
	if _material == null:
		return
	_material.set_shader_parameter("wake_color", wake_color)
	_material.set_shader_parameter("brightness", brightness)
	_material.set_shader_parameter("alpha", alpha)
