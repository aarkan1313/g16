extends MeshInstance3D
class_name FastWaterCaustics

const CAUSTICS_SHADER_PATH := "res://addons/fast_water/shaders/fast_caustics.gdshader"

@export var target_camera: Camera3D
@export var follow_camera := true
@export_range(8.0, 512.0, 1.0) var size_m := 96.0
@export_range(-64.0, 4.0, 0.1) var depth_below_water_m := -1.8
@export_range(0.0, 3.0, 0.01) var intensity := 0.38
@export_range(0.0, 20.0, 0.01) var caustic_scale := 4.5
@export_range(0.0, 8.0, 0.01) var speed := 1.0
@export_range(0.0, 1.0, 0.01) var distance_fade := 0.75

var _material: ShaderMaterial


func _ready() -> void:
	cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	var plane := PlaneMesh.new()
	plane.size = Vector2(size_m, size_m)
	plane.subdivide_width = 1
	plane.subdivide_depth = 1
	mesh = plane

	var shader := load(CAUSTICS_SHADER_PATH) as Shader
	_material = ShaderMaterial.new()
	if shader != null:
		_material.shader = shader
	material_override = _material
	_apply_params()


func _process(_delta: float) -> void:
	if follow_camera and target_camera != null:
		global_position.x = target_camera.global_position.x
		global_position.z = target_camera.global_position.z
	_apply_params()


func _apply_params() -> void:
	global_position.y = depth_below_water_m
	if _material == null:
		return
	_material.set_shader_parameter("intensity", intensity)
	_material.set_shader_parameter("caustic_scale", caustic_scale)
	_material.set_shader_parameter("speed", speed)
	_material.set_shader_parameter("distance_fade", distance_fade)
