@tool
extends Node
class_name FastWaterFoamField

signal foam_source_added(world_pos: Vector3, radius_m: float, intensity: float, source_kind: int, flow_velocity: Vector3)

enum SourceKind {
	GENERIC,
	SHORELINE,
	WAKE,
	IMPACT,
	RAIN,
	RAPIDS,
	WATERFALL_LIP,
	PLUNGE_POOL,
	EDDY,
}

@export_range(16, 1024, 16) var resolution := 256:
	set(value):
		resolution = max(value, 16)
		rebuild()

@export_range(1.0, 2048.0, 1.0) var world_size_m := 64.0:
	set(value):
		world_size_m = max(value, 1.0)

@export var origin_xz := Vector2.ZERO
@export_range(0.0, 4.0, 0.01) var intensity_gain := 1.0
@export_range(0.0, 4.0, 0.01) var dissipation_per_second := 0.18
@export_range(0.0, 4.0, 0.01) var age_rate := 0.20
@export_range(1.0, 60.0, 1.0) var update_hz := 20.0

@export_category("Flow Response")
@export var flow_provider: Node
@export var flow_provider_path: NodePath
@export_range(0.0, 4.0, 0.01) var advection_strength := 1.0
@export_range(0.0, 4.0, 0.01) var turbulence_thicken_strength := 0.22
@export_range(0.0, 1.0, 0.01) var current_thicken_strength := 0.04
@export_range(0.01, 8.0, 0.01) var max_advect_m_per_step := 2.0

var texture: ImageTexture

var _image: Image
var _update_accum := 0.0


func _ready() -> void:
	add_to_group("fast_water_foam_field")
	if _image == null:
		rebuild()


func _process(delta: float) -> void:
	if _image == null:
		return
	if dissipation_per_second <= 0.0 and age_rate <= 0.0 and advection_strength <= 0.0 and turbulence_thicken_strength <= 0.0 and current_thicken_strength <= 0.0:
		return
	_update_accum += delta
	var interval: float = 1.0 / maxf(update_hz, 1.0)
	if _update_accum < interval:
		return
	var step_delta := _update_accum
	_update_accum = 0.0
	_simulate(step_delta)


func rebuild(new_resolution: int = -1) -> void:
	if new_resolution > 0:
		resolution = max(new_resolution, 16)
	_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	clear()


func clear() -> void:
	if _image == null:
		_image = Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	for y in range(resolution):
		for x in range(resolution):
			_image.set_pixel(x, y, Color(0.0, 0.0, 0.0, 0.0))
	_update_texture()


func advance(delta: float) -> void:
	_simulate(maxf(delta, 0.0))


func add_source(world_pos: Vector3, radius_m: float, intensity: float, source_kind: int = SourceKind.GENERIC, flow_velocity: Vector3 = Vector3.ZERO) -> void:
	add_foam_stamp(world_pos, radius_m, intensity, source_kind, flow_velocity)


func add_foam_stamp(world_pos: Vector3, radius_m: float, intensity: float, source_kind: int = SourceKind.GENERIC, _flow_velocity: Vector3 = Vector3.ZERO) -> void:
	if _image == null:
		rebuild()
	if radius_m <= 0.001 or intensity <= 0.001:
		return

	var center_uv := _world_to_uv(Vector2(world_pos.x, world_pos.z))
	var radius_px: int = maxi(1, ceili(radius_m / maxf(world_size_m, 0.001) * float(resolution)))
	var center_x: int = roundi(center_uv.x * float(resolution - 1))
	var center_y: int = roundi(center_uv.y * float(resolution - 1))
	var min_x: int = clampi(center_x - radius_px, 0, resolution - 1)
	var max_x: int = clampi(center_x + radius_px, 0, resolution - 1)
	var min_y: int = clampi(center_y - radius_px, 0, resolution - 1)
	var max_y: int = clampi(center_y + radius_px, 0, resolution - 1)
	var source_value: float = clampf(float(source_kind) / float(SourceKind.EDDY), 0.0, 1.0)
	var scaled_intensity: float = clampf(intensity * intensity_gain, 0.0, 1.0)

	for y in range(min_y, max_y + 1):
		for x in range(min_x, max_x + 1):
			var pixel_uv := Vector2(float(x) / float(resolution - 1), float(y) / float(resolution - 1))
			var world_xz := origin_xz + (pixel_uv - Vector2(0.5, 0.5)) * world_size_m
			var distance_m := world_xz.distance_to(Vector2(world_pos.x, world_pos.z))
			if distance_m > radius_m:
				continue
			var falloff: float = 1.0 - smoothstep(radius_m * 0.18, radius_m, distance_m)
			var c := _image.get_pixel(x, y)
			var foam: float = clampf(c.r + scaled_intensity * falloff, 0.0, 1.0)
			var age: float = minf(c.g, 0.25) if c.r > 0.001 else 0.0
			var source_mix: float = source_value if foam >= c.r else c.b
			_image.set_pixel(x, y, Color(foam, age, source_mix, clampf(foam, 0.0, 1.0)))
	_update_texture()
	foam_source_added.emit(world_pos, radius_m, scaled_intensity, source_kind, _flow_velocity)


func export_image(path: String) -> int:
	if _image == null:
		rebuild()
	if _image == null:
		return ERR_UNCONFIGURED
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	return _image.save_png(global_path)


func import_image(path: String, imported_origin_xz: Vector2 = Vector2.INF, imported_world_size_m: float = -1.0) -> int:
	var image := Image.new()
	var err := image.load(ProjectSettings.globalize_path(path))
	if err != OK:
		return err
	if image.get_width() <= 0 or image.get_height() <= 0:
		return ERR_INVALID_DATA
	if image.get_width() != image.get_height():
		return ERR_INVALID_DATA
	image.convert(Image.FORMAT_RGBA8)
	resolution = image.get_width()
	_image = image
	if imported_origin_xz != Vector2.INF:
		origin_xz = imported_origin_xz
	if imported_world_size_m > 0.0:
		world_size_m = imported_world_size_m
	_update_texture()
	return OK


func sample_foam_at(world_position: Vector3) -> float:
	if _image == null:
		return 0.0
	var uv := _world_to_uv(Vector2(world_position.x, world_position.z))
	if uv.x < 0.0 or uv.x > 1.0 or uv.y < 0.0 or uv.y > 1.0:
		return 0.0
	var px: int = clampi(roundi(uv.x * float(resolution - 1)), 0, resolution - 1)
	var py: int = clampi(roundi(uv.y * float(resolution - 1)), 0, resolution - 1)
	var c := _image.get_pixel(px, py)
	return c.r * c.a


func sample_source_at(world_position: Vector3) -> int:
	if _image == null:
		return SourceKind.GENERIC
	var uv := _world_to_uv(Vector2(world_position.x, world_position.z))
	if uv.x < 0.0 or uv.x > 1.0 or uv.y < 0.0 or uv.y > 1.0:
		return SourceKind.GENERIC
	var px: int = clampi(roundi(uv.x * float(resolution - 1)), 0, resolution - 1)
	var py: int = clampi(roundi(uv.y * float(resolution - 1)), 0, resolution - 1)
	var c := _image.get_pixel(px, py)
	return clampi(roundi(c.b * float(SourceKind.EDDY)), SourceKind.GENERIC, SourceKind.EDDY)


func bind_to_material(material: ShaderMaterial, strength: float = 1.0) -> void:
	if material == null:
		return
	if texture == null:
		rebuild()
	material.set_shader_parameter("foam_field_enabled", texture != null)
	material.set_shader_parameter("foam_field_map", texture)
	material.set_shader_parameter("foam_field_origin_xz", origin_xz)
	material.set_shader_parameter("foam_field_world_size", maxf(world_size_m, 0.01))
	material.set_shader_parameter("foam_field_strength", strength)


func _simulate(delta: float) -> void:
	if _image == null:
		return
	var fade: float = maxf(dissipation_per_second * delta, 0.0)
	var age_step: float = maxf(age_rate * delta, 0.0)
	var previous := _image.duplicate() as Image
	var next := Image.create(resolution, resolution, false, Image.FORMAT_RGBA8)
	var any_changed := false
	for y in range(resolution):
		for x in range(resolution):
			var world_xz := _pixel_to_world_xz(x, y)
			var flow := _sample_flow_at(world_xz)
			var advect := Vector2(flow.x, flow.z) * advection_strength * delta
			if advect.length() > max_advect_m_per_step:
				advect = advect.normalized() * max_advect_m_per_step
			var c := _sample_image_at_world(previous, world_xz - advect)
			if c.r <= 0.0 and c.a <= 0.0:
				next.set_pixel(x, y, Color(0.0, 0.0, c.b, 0.0))
				continue
			var turbulence: float = _sample_turbulence_at(world_xz, flow)
			var thicken: float = (turbulence * turbulence_thicken_strength + flow.length() * current_thicken_strength) * delta * clampf(c.r, 0.0, 1.0)
			var foam: float = clampf(c.r - fade + thicken, 0.0, 1.0)
			var age: float = clampf(c.g + age_step, 0.0, 1.0)
			var alpha: float = foam
			next.set_pixel(x, y, Color(foam, age, c.b, alpha))
			any_changed = true
	if any_changed:
		_image = next
		_update_texture()


func _world_to_uv(world_xz: Vector2) -> Vector2:
	return (world_xz - origin_xz) / maxf(world_size_m, 0.001) + Vector2(0.5, 0.5)


func _pixel_to_world_xz(x: int, y: int) -> Vector2:
	var uv := Vector2(float(x) / float(resolution - 1), float(y) / float(resolution - 1))
	return origin_xz + (uv - Vector2(0.5, 0.5)) * world_size_m


func _sample_image_at_world(image: Image, world_xz: Vector2) -> Color:
	var uv := _world_to_uv(world_xz)
	if uv.x < 0.0 or uv.x > 1.0 or uv.y < 0.0 or uv.y > 1.0:
		return Color(0.0, 0.0, 0.0, 0.0)
	var px: float = uv.x * float(image.get_width() - 1)
	var py: float = uv.y * float(image.get_height() - 1)
	var x0: int = clampi(floori(px), 0, image.get_width() - 1)
	var y0: int = clampi(floori(py), 0, image.get_height() - 1)
	var x1: int = clampi(x0 + 1, 0, image.get_width() - 1)
	var y1: int = clampi(y0 + 1, 0, image.get_height() - 1)
	var tx: float = px - float(x0)
	var ty: float = py - float(y0)
	var a := image.get_pixel(x0, y0).lerp(image.get_pixel(x1, y0), tx)
	var b := image.get_pixel(x0, y1).lerp(image.get_pixel(x1, y1), tx)
	return a.lerp(b, ty)


func _sample_flow_at(world_xz: Vector2) -> Vector3:
	var provider := _resolve_flow_provider()
	if provider == null:
		return Vector3.ZERO
	var world_pos := Vector3(world_xz.x, 0.0, world_xz.y)
	if provider.has_method("sample_flow_at"):
		return provider.call("sample_flow_at", world_pos) as Vector3
	if provider.has_method("get_flow_at"):
		return provider.call("get_flow_at", world_pos) as Vector3
	return Vector3.ZERO


func _sample_turbulence_at(world_xz: Vector2, flow: Vector3) -> float:
	var provider := _resolve_flow_provider()
	if provider == null:
		return clampf(flow.length() * 0.05, 0.0, 1.0)
	var world_pos := Vector3(world_xz.x, 0.0, world_xz.y)
	if provider.has_method("get_turbulence_at"):
		return clampf(float(provider.call("get_turbulence_at", world_pos)), 0.0, 4.0)
	if provider.has_method("sample_foam_at"):
		return clampf(float(provider.call("sample_foam_at", world_pos)), 0.0, 4.0)
	return clampf(flow.length() * 0.05, 0.0, 1.0)


func _resolve_flow_provider() -> Node:
	if flow_provider != null:
		return flow_provider
	if flow_provider_path != NodePath("") and is_inside_tree():
		var node := get_node_or_null(flow_provider_path)
		if node != null:
			return node
	var parent := get_parent()
	if parent != null:
		if parent.has_method("get_flow_at") or parent.has_method("sample_flow_at"):
			return parent
		var flow_field := parent.get_node_or_null("FlowField")
		if flow_field != null:
			return flow_field
	return null


func _update_texture() -> void:
	if _image == null:
		return
	if texture == null or texture.get_width() != _image.get_width() or texture.get_height() != _image.get_height():
		texture = ImageTexture.create_from_image(_image)
	else:
		texture.update(_image)
