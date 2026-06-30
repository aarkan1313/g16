extends Node
class_name FastWaterWakeMap

@export_range(64, 1024, 64) var resolution := 256
@export_range(8.0, 512.0, 1.0) var world_size_m := 96.0
@export var target_camera: Camera3D
@export_range(1.0, 120.0, 1.0) var update_hz := 30.0
@export_range(0.1, 12.0, 0.1) var height_return_rate := 3.6
@export_range(0.1, 12.0, 0.1) var foam_decay_rate := 3.2
@export_range(0.1, 12.0, 0.1) var normal_return_rate := 3.2
@export_range(1, 512, 1) var max_pending_stamps := 128
@export_range(0.0, 64.0, 0.5) var snap_m := 4.0
@export_range(0.25, 8.0, 0.05) var active_region_lifetime_s := 2.4
@export_range(0, 128, 1) var active_region_padding_px := 24

@export_category("Stamp Shape")
@export_range(0.04, 0.4, 0.01) var side_band_width := 0.11
@export_range(0.0, 2.0, 0.01) var side_band_strength := 0.5
@export_range(0.0, 1.0, 0.01) var center_trail_strength := 0.28
@export_range(0.0, 1.0, 0.01) var bow_band_strength := 0.38
@export_range(0.0, 1.0, 0.01) var ring_foam_strength := 0.34
@export_range(0.0, 1.0, 0.01) var core_foam_strength := 0.16
@export_range(0.0, 2.0, 0.01) var foam_gain := 0.62

var texture: ImageTexture
var origin_xz := Vector2.ZERO

var _image: Image
var _pending: Array[Dictionary] = []
var _update_accum := 0.0
var _has_active_region := false
var _active_min_x := 0
var _active_max_x := 0
var _active_min_y := 0
var _active_max_y := 0
var _active_age := 0.0
var _last_origin_xz := Vector2.INF


func _ready() -> void:
	_rebuild_image()
	_update_origin()


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
	_update_texture(step)


func rebuild(new_resolution: int = resolution) -> void:
	resolution = max(8, new_resolution)
	_rebuild_image()


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


func _rebuild_image() -> void:
	_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	_clear_image()
	texture = ImageTexture.create_from_image(_image)
	_reset_active_region()


func _clear_image() -> void:
	for y in range(resolution):
		for x in range(resolution):
			_image.set_pixel(x, y, Color(0.5, 0.0, 0.5, 0.5))


func _update_origin() -> void:
	var previous := origin_xz
	if target_camera == null:
		var parent_3d := get_parent() as Node3D
		if parent_3d != null:
			origin_xz = Vector2(parent_3d.global_position.x, parent_3d.global_position.z)
		else:
			origin_xz = Vector2.ZERO
	else:
		var p := target_camera.global_position
		origin_xz = Vector2(snapped(p.x, snap_m), snapped(p.z, snap_m))

	if _image != null and _last_origin_xz != Vector2.INF and previous != origin_xz:
		_clear_image()
		_reset_active_region()
		if texture != null:
			texture.update(_image)
	_last_origin_xz = origin_xz


func _update_texture(delta: float) -> void:
	if _image == null:
		_rebuild_image()
	if not _has_active_region and _pending.is_empty():
		return

	var height_t := clamp(delta * height_return_rate, 0.0, 1.0)
	var foam_t := clamp(delta * foam_decay_rate, 0.0, 1.0)
	var normal_t := clamp(delta * normal_return_rate, 0.0, 1.0)

	if _has_active_region:
		for y in range(_active_min_y, _active_max_y + 1):
			for x in range(_active_min_x, _active_max_x + 1):
				var c := _image.get_pixel(x, y)
				c.r = lerp(c.r, 0.5, height_t)
				c.g = lerp(c.g, 0.0, foam_t)
				c.b = lerp(c.b, 0.5, normal_t)
				c.a = lerp(c.a, 0.5, normal_t)
				_image.set_pixel(x, y, c)
		_active_age += delta
		if _active_age >= active_region_lifetime_s and _pending.is_empty():
			_clear_active_region()
			_reset_active_region()

	for stamp in _pending:
		_apply_stamp(stamp)
	_pending.clear()

	texture.update(_image)


func _clear_active_region() -> void:
	if not _has_active_region:
		return
	for y in range(_active_min_y, _active_max_y + 1):
		for x in range(_active_min_x, _active_max_x + 1):
			var c := _image.get_pixel(x, y)
			c.r = 0.5
			c.g = 0.0
			c.b = 0.5
			c.a = 0.5
			_image.set_pixel(x, y, c)


func _reset_active_region() -> void:
	_has_active_region = false
	_active_min_x = 0
	_active_max_x = 0
	_active_min_y = 0
	_active_max_y = 0
	_active_age = 0.0


func _mark_active_region(min_x: int, max_x: int, min_y: int, max_y: int) -> void:
	var pad := active_region_padding_px
	min_x = clampi(min_x - pad, 0, resolution - 1)
	max_x = clampi(max_x + pad, 0, resolution - 1)
	min_y = clampi(min_y - pad, 0, resolution - 1)
	max_y = clampi(max_y + pad, 0, resolution - 1)
	if not _has_active_region:
		_active_min_x = min_x
		_active_max_x = max_x
		_active_min_y = min_y
		_active_max_y = max_y
		_has_active_region = true
	else:
		_active_min_x = min(_active_min_x, min_x)
		_active_max_x = max(_active_max_x, max_x)
		_active_min_y = min(_active_min_y, min_y)
		_active_max_y = max(_active_max_y, max_y)
	_active_age = 0.0


func _apply_stamp(stamp: Dictionary) -> void:
	var pos: Vector2 = stamp["pos"]
	var vel: Vector2 = stamp["vel"]
	var radius_m: float = stamp["radius"]
	var strength: float = stamp["strength"]
	var foam: float = stamp["foam"]

	var uv := _world_to_uv(pos)
	if uv.x < -0.25 or uv.x > 1.25 or uv.y < -0.25 or uv.y > 1.25:
		return

	var center := Vector2(uv.x * float(resolution - 1), uv.y * float(resolution - 1))
	var radius_px := max(radius_m / max(world_size_m, 0.001) * float(resolution), 1.0)
	var min_x := clampi(int(floor(center.x - radius_px * 3.0)), 0, resolution - 1)
	var max_x := clampi(int(ceil(center.x + radius_px * 3.0)), 0, resolution - 1)
	var min_y := clampi(int(floor(center.y - radius_px * 3.0)), 0, resolution - 1)
	var max_y := clampi(int(ceil(center.y + radius_px * 3.0)), 0, resolution - 1)
	_mark_active_region(min_x, max_x, min_y, max_y)
	var speed := vel.length()
	var flow := vel / speed if speed > 0.0001 else Vector2.ZERO
	var side_axis := Vector2(-flow.y, flow.x) if speed > 0.0001 else Vector2.RIGHT

	for y in range(min_y, max_y + 1):
		for x in range(min_x, max_x + 1):
			var p := Vector2(float(x), float(y))
			var rel := p - center
			var d: float = rel.length() / radius_px
			if d > 3.0:
				continue

			# Same convention as fast_wake_gpu_update.gdshader: along>0 ahead, along<0
			# behind; all trailing foam grows with distance BEHIND (back) and decays
			# further back. Keep this kernel in sync with the GPU shader.
			var moving: float = 1.0 if speed > 0.0001 else 0.0
			var along: float = rel.dot(flow) / radius_px if speed > 0.0001 else 0.0
			var side: float = rel.dot(side_axis) / radius_px if speed > 0.0001 else d
			var back: float = max(-along, 0.0)
			var ring: float = exp(-pow(d - 1.0, 2.0) * 3.2)
			var core: float = exp(-d * d * 2.0)
			var behind_gate: float = smoothstep(0.20, -0.05, along) * moving
			var center_trail: float = exp(-side * side * 5.5) * behind_gate * exp(-back * 0.45)
			var side_target: float = 0.30 + back * 0.55
			var side_band: float = exp(-pow((abs(side) - side_target) / max(side_band_width, 0.001), 2.0)) * behind_gate * exp(-back * 0.40)
			var bow_lip: float = exp(-pow(d - 0.95, 2.0) * 6.0) * smoothstep(-0.05, 0.45, along) * moving
			var streak: float = clamp(center_trail * center_trail_strength + side_band * side_band_strength + bow_lip * bow_band_strength, 0.0, 1.0)
			var signed_height: float = (center_trail * 0.32 + bow_lip * 0.45 - side_band * 0.14) * strength * 0.16
			var c := _image.get_pixel(x, y)
			c.r = clamp(c.r + signed_height, 0.0, 1.0)
			c.g = clamp(max(c.g, max(max(ring * ring_foam_strength, core * core_foam_strength), streak) * foam * foam_gain), 0.0, 1.0)
			c.b = clamp(lerp(c.b, flow.x * 0.5 + 0.5, streak * clamp(abs(strength) * 1.3, 0.0, 1.0)), 0.0, 1.0)
			c.a = clamp(lerp(c.a, flow.y * 0.5 + 0.5, streak * clamp(abs(strength) * 1.3, 0.0, 1.0)), 0.0, 1.0)
			_image.set_pixel(x, y, c)


func _world_to_uv(world_xz: Vector2) -> Vector2:
	return (world_xz - origin_xz) / max(world_size_m, 0.001) + Vector2(0.5, 0.5)
