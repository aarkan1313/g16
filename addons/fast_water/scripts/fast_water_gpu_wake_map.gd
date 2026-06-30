extends Node
class_name FastWaterGpuWakeMap

const UPDATE_SHADER := "res://addons/fast_water/shaders/fast_wake_gpu_update.gdshader"
const MAX_GPU_STAMPS := 32

@export_range(64, 2048, 64) var resolution := 384
@export_range(8.0, 512.0, 1.0) var world_size_m := 96.0
@export var target_camera: Camera3D
@export_range(1.0, 120.0, 1.0) var update_hz := 36.0
@export_range(0.1, 12.0, 0.1) var height_return_rate := 3.6
@export_range(0.1, 12.0, 0.1) var foam_decay_rate := 3.2
@export_range(0.1, 12.0, 0.1) var normal_return_rate := 3.2
@export_range(1, 256, 1) var max_pending_stamps := 64
@export_range(0.0, 64.0, 0.5) var snap_m := 4.0
@export_range(0.25, 8.0, 0.05) var active_lifetime_s := 2.4

@export_category("Stamp Shape")
@export_range(0.04, 0.4, 0.01) var side_band_width := 0.11
@export_range(0.0, 2.0, 0.01) var side_band_strength := 0.5
@export_range(0.0, 1.0, 0.01) var center_trail_strength := 0.28
@export_range(0.0, 1.0, 0.01) var bow_band_strength := 0.38
@export_range(0.0, 1.0, 0.01) var ring_foam_strength := 0.34
@export_range(0.0, 1.0, 0.01) var core_foam_strength := 0.16
@export_range(0.0, 2.0, 0.01) var foam_gain := 0.62

var texture: Texture2D
var origin_xz := Vector2.ZERO

var _viewports: Array[SubViewport] = []
var _materials: Array[ShaderMaterial] = []
var _pending: Array[Dictionary] = []
var _neutral_texture: ImageTexture
var _read_index := 0
var _write_index := 1
var _update_accum := 0.0
var _active_time_left := 0.0
var _clear_requested := true
var _last_origin_xz := Vector2.INF
var _read_origin_xz := Vector2.ZERO
var _origin_changed := false


func _ready() -> void:
	_ensure_neutral_texture()
	_ensure_gpu_resources()
	_update_origin()
	clear()


func _process(delta: float) -> void:
	_update_origin()
	if update_hz <= 0.0:
		return

	_update_accum += delta
	var interval := 1.0 / update_hz
	if _update_accum < interval:
		return

	var step := _update_accum
	_update_accum = 0.0
	if _pending.is_empty() and _active_time_left <= 0.0 and not _clear_requested and not _origin_changed:
		return
	_active_time_left = max(_active_time_left - step, 0.0)
	_render_update(step)


func rebuild(new_resolution: int = resolution) -> void:
	resolution = max(64, new_resolution)
	_destroy_gpu_resources()
	_ensure_gpu_resources()
	clear()


func clear() -> void:
	_pending.clear()
	_active_time_left = 0.0
	_clear_requested = true
	_render_update(0.016)
	_render_update(0.016)


func add_stamp(world_pos: Vector3, velocity: Vector3, radius_m: float, strength: float, foam: float = 0.5) -> void:
	if _pending.size() >= max_pending_stamps:
		_pending.pop_front()
	_pending.append({
		"pos": Vector2(world_pos.x, world_pos.z),
		"vel": Vector2(velocity.x, velocity.z),
		"radius": max(radius_m, 0.05),
		"strength": strength,
		"foam": foam,
	})
	_active_time_left = active_lifetime_s


func _ensure_neutral_texture() -> void:
	if _neutral_texture != null:
		return
	var image := Image.create(1, 1, false, Image.FORMAT_RGBA8)
	image.set_pixel(0, 0, Color(0.5, 0.0, 0.5, 0.5))
	_neutral_texture = ImageTexture.create_from_image(image)


func _ensure_gpu_resources() -> void:
	if _viewports.size() == 2:
		return

	var shader := load(UPDATE_SHADER) as Shader
	for i in range(2):
		var viewport := SubViewport.new()
		viewport.name = "WakeGpuViewport%d" % i
		viewport.disable_3d = true
		viewport.transparent_bg = false
		viewport.size = Vector2i(resolution, resolution)
		viewport.render_target_clear_mode = SubViewport.CLEAR_MODE_ALWAYS
		viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED
		add_child(viewport)

		var rect := ColorRect.new()
		rect.name = "UpdateRect"
		rect.set_anchors_preset(Control.PRESET_FULL_RECT)
		rect.offset_left = 0.0
		rect.offset_top = 0.0
		rect.offset_right = float(resolution)
		rect.offset_bottom = float(resolution)
		viewport.add_child(rect)

		var material := ShaderMaterial.new()
		material.shader = shader
		rect.material = material
		_viewports.append(viewport)
		_materials.append(material)

	texture = _viewports[_read_index].get_texture()
	_read_origin_xz = origin_xz


func _destroy_gpu_resources() -> void:
	for viewport in _viewports:
		if is_instance_valid(viewport):
			viewport.queue_free()
	_viewports.clear()
	_materials.clear()
	_read_index = 0
	_write_index = 1
	texture = null
	_read_origin_xz = origin_xz


func _update_origin() -> void:
	var previous := origin_xz
	if target_camera == null:
		var parent_3d := get_parent() as Node3D
		origin_xz = Vector2(parent_3d.global_position.x, parent_3d.global_position.z) if parent_3d != null else Vector2.ZERO
	else:
		var p := target_camera.global_position
		origin_xz = Vector2(snapped(p.x, snap_m), snapped(p.z, snap_m))

	if _last_origin_xz != Vector2.INF and previous != origin_xz:
		if previous.distance_to(origin_xz) > world_size_m * 0.45:
			clear()
		else:
			_origin_changed = true
			_active_time_left = max(_active_time_left, 1.0 / max(update_hz, 1.0))
	_last_origin_xz = origin_xz


func _render_update(delta: float) -> void:
	_ensure_gpu_resources()
	var material := _materials[_write_index]
	var previous_texture: Texture2D = _viewports[_read_index].get_texture()
	if previous_texture == null:
		previous_texture = _neutral_texture

	var stamp_pos_radius_strength := PackedVector4Array()
	var stamp_flow_foam := PackedVector4Array()
	stamp_pos_radius_strength.resize(MAX_GPU_STAMPS)
	stamp_flow_foam.resize(MAX_GPU_STAMPS)
	var count := min(_pending.size(), MAX_GPU_STAMPS)
	for i in range(count):
		var stamp := _pending[i]
		var pos: Vector2 = stamp["pos"]
		var vel: Vector2 = stamp["vel"]
		stamp_pos_radius_strength[i] = Vector4(pos.x, pos.y, float(stamp["radius"]), float(stamp["strength"]))
		stamp_flow_foam[i] = Vector4(vel.x, vel.y, float(stamp["foam"]), 0.0)

	material.set_shader_parameter("previous_map", previous_texture)
	material.set_shader_parameter("clear_map", _clear_requested)
	material.set_shader_parameter("delta_time", max(delta, 0.0))
	material.set_shader_parameter("height_return_rate", height_return_rate)
	material.set_shader_parameter("foam_decay_rate", foam_decay_rate)
	material.set_shader_parameter("normal_return_rate", normal_return_rate)
	material.set_shader_parameter("origin_xz", origin_xz)
	material.set_shader_parameter("previous_origin_xz", _read_origin_xz)
	material.set_shader_parameter("world_size_m", world_size_m)
	material.set_shader_parameter("stamp_count", count)
	material.set_shader_parameter("stamp_pos_radius_strength", stamp_pos_radius_strength)
	material.set_shader_parameter("stamp_flow_foam", stamp_flow_foam)
	material.set_shader_parameter("side_band_width", side_band_width)
	material.set_shader_parameter("side_band_strength", side_band_strength)
	material.set_shader_parameter("center_trail_strength", center_trail_strength)
	material.set_shader_parameter("bow_band_strength", bow_band_strength)
	material.set_shader_parameter("ring_foam_strength", ring_foam_strength)
	material.set_shader_parameter("core_foam_strength", core_foam_strength)
	material.set_shader_parameter("foam_gain", foam_gain)

	_viewports[_write_index].render_target_update_mode = SubViewport.UPDATE_ONCE
	texture = _viewports[_write_index].get_texture()
	var old_read := _read_index
	_read_index = _write_index
	_write_index = old_read
	_read_origin_xz = origin_xz
	_pending.clear()
	_clear_requested = false
	_origin_changed = false
