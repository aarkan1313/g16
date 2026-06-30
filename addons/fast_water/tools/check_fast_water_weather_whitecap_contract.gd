extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_weather_whitecap_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterWeatherWhitecapContract"
	root.add_child(scene)

	var surface := SURFACE_SCRIPT.new()
	surface.name = "WhitecapWaterSurface"
	surface.set("auto_create_mesh", false)
	surface.set("auto_create_wake_map", false)
	surface.set("auto_create_bubbles", false)
	surface.set("wave_height", 0.05)
	surface.set("wave_speed", 1.10)
	surface.set("normal_strength", 1.20)
	surface.set("foam_intensity", 0.80)
	scene.add_child(surface)

	await process_frame

	var response := WEATHER_RESPONSE_SCRIPT.new()
	response.set("whitecap_wind_threshold_mps", 5.0)
	response.set("whitecap_wind_strength_per_mps", 0.060)
	response.set("gust_whitecap_boost", 0.30)
	response.set("storm_whitecap_boost", 0.82)
	response.set("whitecap_noise_scale", 0.41)
	response.set("wind_wave_height_per_mps", 0.020)
	response.set("wind_wave_speed_per_mps", 0.020)
	response.set("gust_wave_boost", 0.24)
	response.set("storm_normal_boost", 0.44)
	response.set("storm_foam_boost", 0.90)

	var calm := ENVIRONMENT_STATE_SCRIPT.new()
	calm.set("wind_direction_xz", Vector2(1.0, 0.0))
	calm.set("wind_speed_mps", 2.0)
	calm.set("gust_strength", 0.0)
	calm.set("storm_intensity", 0.0)
	surface.call("apply_environment_state", calm, response)
	surface.call("_apply_shader_params")
	var material := surface.get("water_material") as ShaderMaterial
	var calm_sample := _sample_material(material)

	var storm := ENVIRONMENT_STATE_SCRIPT.new()
	storm.set("wind_direction_xz", Vector2(0.6, -0.8))
	storm.set("wind_speed_mps", 15.0)
	storm.set("gust_strength", 0.70)
	storm.set("storm_intensity", 0.80)
	storm.set("rain_intensity", 0.35)
	surface.call("apply_environment_state", storm, response)
	surface.call("_apply_shader_params")
	var storm_sample := _sample_material(material)

	surface.call("apply_environment_state", null, response)
	surface.call("_apply_shader_params")
	var reset_sample := _sample_material(material)
	var wind_dir: Vector2 = material.get_shader_parameter("sun_glint_direction")

	var checks := {
		"calm_has_no_whitecaps": float(calm_sample.get("whitecap", -1.0)) <= 0.001,
		"storm_adds_whitecaps": float(storm_sample.get("whitecap", 0.0)) > float(calm_sample.get("whitecap", 0.0)) + 0.65,
		"whitecap_scale_applied": absf(float(storm_sample.get("whitecap_scale", 0.0)) - 0.41) < 0.01,
		"gust_storm_raise_wave_height": float(storm_sample.get("wave_height", 0.0)) > float(calm_sample.get("wave_height", 0.0)) + 0.03,
		"gust_storm_raise_wave_speed": float(storm_sample.get("wave_speed", 0.0)) > float(calm_sample.get("wave_speed", 0.0)) + 0.15,
		"storm_raises_normals": float(storm_sample.get("normal_strength", 0.0)) > float(calm_sample.get("normal_strength", 0.0)) + 0.25,
		"storm_raises_foam": float(storm_sample.get("foam_intensity", 0.0)) > float(calm_sample.get("foam_intensity", 0.0)) + 0.70,
		"storm_raises_glint": float(storm_sample.get("sun_glint_strength", 0.0)) > float(calm_sample.get("sun_glint_strength", 0.0)) + 0.25,
		"wind_direction_applied": wind_dir.distance_to(Vector2(0.6, -0.8).normalized()) < 0.01,
		"reset_clears_whitecaps": float(reset_sample.get("whitecap", -1.0)) <= 0.001,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"calm": calm_sample,
			"storm": storm_sample,
			"reset": reset_sample,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater weather whitecap contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater weather whitecap contract check saved: " + global_path)
	quit(0 if passed else 1)


func _sample_material(material: ShaderMaterial) -> Dictionary:
	if material == null:
		return {}
	return {
		"whitecap": float(material.get_shader_parameter("storm_whitecap_strength")),
		"whitecap_scale": float(material.get_shader_parameter("storm_whitecap_scale")),
		"wave_height": float(material.get_shader_parameter("wave_height")),
		"wave_speed": float(material.get_shader_parameter("wave_speed")),
		"normal_strength": float(material.get_shader_parameter("normal_strength")),
		"foam_intensity": float(material.get_shader_parameter("foam_intensity")),
		"sun_glint_strength": float(material.get_shader_parameter("sun_glint_strength")),
	}
