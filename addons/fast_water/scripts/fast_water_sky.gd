extends WorldEnvironment
class_name FastWaterSky

const SKY_SHADER_PATH := "res://addons/fast_water/shaders/fast_water_sky.gdshader"

@export var horizon_color := Color(0.55, 0.78, 0.92, 1.0)
@export var zenith_color := Color(0.04, 0.18, 0.42, 1.0)
@export var cloud_color := Color(0.9, 0.94, 1.0, 1.0)
@export_range(0.0, 1.0, 0.01) var cloud_amount := 0.36
@export_range(0.0, 4.0, 0.01) var cloud_speed := 0.12

var _sky_material: ShaderMaterial


func _ready() -> void:
	ensure_environment()


func _process(_delta: float) -> void:
	_apply_params()


func ensure_environment() -> void:
	if environment == null:
		environment = Environment.new()

	var sky := Sky.new()
	_sky_material = ShaderMaterial.new()
	var shader := load(SKY_SHADER_PATH) as Shader
	if shader != null:
		_sky_material.shader = shader
	sky.sky_material = _sky_material
	environment.background_mode = Environment.BG_SKY
	environment.sky = sky
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	environment.ambient_light_energy = 0.45
	environment.tonemap_mode = Environment.TONE_MAPPER_AGX
	environment.glow_enabled = true
	environment.glow_intensity = 0.08
	_apply_params()


func _apply_params() -> void:
	if _sky_material == null:
		return
	_sky_material.set_shader_parameter("horizon_color", horizon_color)
	_sky_material.set_shader_parameter("zenith_color", zenith_color)
	_sky_material.set_shader_parameter("cloud_color", cloud_color)
	_sky_material.set_shader_parameter("cloud_amount", cloud_amount)
	_sky_material.set_shader_parameter("cloud_speed", cloud_speed)
