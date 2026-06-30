@tool
extends Node3D
class_name FastWaterPlanarReflection

@export var water_surface_path: NodePath
@export var target_camera: Camera3D
@export var reflection_cull_mask := 1048575

@export_category("Quality")
@export var enabled := true
@export_range(0.25, 1.0, 0.05) var resolution_scale := 0.5
@export_range(64, 2048, 1) var max_resolution := 1024
@export var update_in_editor := false
## Reflection refresh rate. 0 = render the reflection scene every frame (highest
## cost). >0 re-renders the full reflection at this many Hz and reuses the last
## texture in between -- a major hero-water GPU saving since each refresh is a whole
## extra scene render. Water reflections are rough/distorted, so ~30 reads fine.
@export_range(0.0, 120.0, 1.0) var update_hz := 0.0

@export_category("Look")
@export_range(0.0, 1.0, 0.01) var reflection_strength := 0.16
@export_range(0.0, 0.12, 0.001) var distortion_strength := 0.004
@export var flip_y := false
@export var use_water_plane_near_clip := false
@export_range(0.0, 2.0, 0.01) var water_plane_bias_m := 0.05

var _viewport: SubViewport
var _reflection_clock := 0.0
var _reflection_camera: Camera3D
var _water_surface: Node
var _water_material: ShaderMaterial


func _ready() -> void:
	_ensure_nodes()
	_resolve_targets()
	_apply_material_params()


func _process(delta: float) -> void:
	if Engine.is_editor_hint() and not update_in_editor:
		return
	_ensure_nodes()
	_resolve_targets()
	_update_viewport_size()
	# Refresh the reflection scene every frame (update_hz == 0) or throttle to update_hz.
	# UPDATE_ONCE renders a single frame then reverts to disabled, so we re-arm it each
	# interval and reuse the last texture (and its matching camera matrix) in between.
	if update_hz <= 0.0:
		if _viewport != null:
			_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		_update_camera()
	else:
		_reflection_clock += delta
		if _reflection_clock >= 1.0 / update_hz:
			_reflection_clock = 0.0
			_update_camera()
			if _viewport != null:
				_viewport.render_target_update_mode = SubViewport.UPDATE_ONCE
	_apply_material_params()


func get_reflection_texture() -> Texture2D:
	if _viewport == null:
		return null
	return _viewport.get_texture()


func _ensure_nodes() -> void:
	if _viewport == null:
		_viewport = get_node_or_null("ReflectionViewport") as SubViewport
	if _viewport == null:
		_viewport = SubViewport.new()
		_viewport.name = "ReflectionViewport"
		_viewport.disable_3d = false
		_viewport.own_world_3d = false
		_viewport.transparent_bg = true
		_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		_viewport.render_target_clear_mode = SubViewport.CLEAR_MODE_ALWAYS
		add_child(_viewport)
	_viewport.transparent_bg = true
	if get_viewport() != null:
		_viewport.world_3d = get_viewport().world_3d

	if _reflection_camera == null:
		_reflection_camera = _viewport.get_node_or_null("ReflectionCamera") as Camera3D
	if _reflection_camera == null:
		_reflection_camera = Camera3D.new()
		_reflection_camera.name = "ReflectionCamera"
		_viewport.add_child(_reflection_camera)
	_reflection_camera.current = true


func _resolve_targets() -> void:
	if target_camera == null and get_viewport() != null:
		target_camera = get_viewport().get_camera_3d()

	if String(water_surface_path) != "":
		_water_surface = get_node_or_null(water_surface_path)
	if _water_surface == null and get_tree() != null:
		var surfaces := get_tree().get_nodes_in_group("fast_water_surface")
		if not surfaces.is_empty():
			_water_surface = surfaces[0]

	_water_material = null
	if _water_surface != null and _has_property(_water_surface, "water_material"):
		_water_material = _water_surface.get("water_material") as ShaderMaterial
	if _water_material == null and _water_surface is MeshInstance3D:
		_water_material = (_water_surface as MeshInstance3D).material_override as ShaderMaterial


func _update_viewport_size() -> void:
	if _viewport == null or get_viewport() == null:
		return
	var root_size := get_viewport().get_visible_rect().size
	var w := clampi(int(root_size.x * resolution_scale), 64, max_resolution)
	var h := clampi(int(root_size.y * resolution_scale), 64, max_resolution)
	var desired := Vector2i(w, h)
	if _viewport.size != desired:
		_viewport.size = desired


func _update_camera() -> void:
	if target_camera == null or _reflection_camera == null:
		return

	var water_y := global_position.y
	if _water_surface != null:
		water_y = _water_surface.global_position.y

	var source_transform := target_camera.global_transform
	var source_pos := source_transform.origin
	var reflected_pos := source_pos
	reflected_pos.y = water_y * 2.0 - reflected_pos.y

	var forward := -source_transform.basis.z
	var up := source_transform.basis.y
	forward.y = -forward.y
	up.y = -up.y
	if forward.length_squared() <= 0.0001:
		return
	forward = forward.normalized()
	if up.length_squared() <= 0.0001 or abs(up.normalized().dot(forward)) > 0.98:
		up = Vector3.UP
	else:
		up = up.normalized()

	_reflection_camera.global_position = reflected_pos
	_reflection_camera.look_at(reflected_pos + forward, up)
	_reflection_camera.projection = target_camera.projection
	_reflection_camera.fov = target_camera.fov
	_reflection_camera.size = target_camera.size
	var reflection_near := maxf(target_camera.near, 0.01)
	if use_water_plane_near_clip:
		# The regular near plane is view-facing, not an oblique water clip plane.
		# Keep this opt-in because it can slice waterline objects into hard
		# polygons in the alpha reflection texture.
		var water_distance := abs(target_camera.global_position.y - water_y)
		reflection_near = maxf(reflection_near, minf(water_distance + water_plane_bias_m, 2.0))
	_reflection_camera.near = clamp(reflection_near, 0.01, target_camera.far - 0.01)
	_reflection_camera.far = target_camera.far
	_reflection_camera.keep_aspect = target_camera.keep_aspect
	_reflection_camera.cull_mask = reflection_cull_mask


func _apply_material_params() -> void:
	if _water_material == null:
		return
	_water_material.set_shader_parameter("planar_reflection_enabled", enabled)
	_water_material.set_shader_parameter("planar_reflection_texture", get_reflection_texture())
	_water_material.set_shader_parameter("planar_reflection_strength", reflection_strength)
	_water_material.set_shader_parameter("planar_reflection_distortion", distortion_strength)
	_water_material.set_shader_parameter("planar_reflection_flip_y", flip_y)
	if _reflection_camera != null:
		var view_projection := _reflection_camera.get_camera_projection() * Projection(_reflection_camera.global_transform.affine_inverse())
		_water_material.set_shader_parameter("planar_reflection_view_projection", view_projection)


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
