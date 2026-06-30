extends SceneTree

const UNDERWATER_SCRIPT := preload("res://addons/fast_water/scripts/fast_underwater_controller.gd")
const WEATHER_ADAPTER_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_adapter.gd")
const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")
const UNDERWATER_SHADER := preload("res://addons/fast_water/shaders/fast_underwater_overlay.gdshader")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_underwater_turbidity_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterUnderwaterTurbidityContract"
	root.add_child(scene)

	var camera := Camera3D.new()
	camera.name = "Camera"
	camera.position = Vector3(0.0, -1.05, 0.0)
	scene.add_child(camera)

	var overlay := ColorRect.new()
	overlay.name = "UnderwaterOverlay"
	overlay.visible = false
	root.add_child(overlay)

	var material := ShaderMaterial.new()
	material.shader = UNDERWATER_SHADER
	overlay.material = material

	var controller := UNDERWATER_SCRIPT.new()
	controller.name = "UnderwaterController"
	controller.set("camera", camera)
	controller.set("underwater_overlay", overlay)
	controller.set("overlay_material", material)
	controller.set("fallback_water_y", 0.0)
	controller.set("distortion_strength", 0.020)
	controller.set("tint_strength", 0.40)
	controller.set("caustic_strength", 0.22)
	controller.set("light_shaft_strength", 0.24)
	controller.set("particulate_strength", 0.10)
	controller.set("vignette_strength", 0.22)
	scene.add_child(controller)

	var response := WEATHER_RESPONSE_SCRIPT.new()
	response.set("turbidity_underwater_visibility_loss", 0.82)
	response.set("turbidity_underwater_tint_boost", 0.34)
	response.set("turbidity_underwater_distortion_boost", 0.62)
	response.set("turbidity_underwater_particulate_boost", 0.54)
	response.set("turbidity_underwater_vignette_boost", 0.34)
	response.set("turbidity_underwater_caustic_loss", 0.84)
	response.set("turbidity_underwater_light_loss", 0.54)
	response.set("turbid_underwater_tint", Color(0.04, 0.15, 0.11, 1.0))
	response.set("suspended_silt_color", Color(0.20, 0.24, 0.15, 1.0))

	var adapter := WEATHER_ADAPTER_SCRIPT.new()
	adapter.name = "WeatherAdapter"
	adapter.set("weather_response", response)
	adapter.set("apply_every_frame", false)
	adapter.set("auto_discover_surfaces", false)
	adapter.set("auto_discover_underwater_controllers", false)
	scene.add_child(adapter)
	var underwater_paths: Array[NodePath] = [adapter.get_path_to(controller)]
	adapter.set("underwater_controller_paths", underwater_paths)

	await process_frame

	var clear := ENVIRONMENT_STATE_SCRIPT.new()
	clear.set("water_turbidity", 0.0)
	clear.set("rain_intensity", 0.0)
	clear.set("storm_intensity", 0.0)
	adapter.call("apply_environment_state", clear)
	controller.call("_process", 1.0 / 60.0)
	var clear_sample := controller.call("get_underwater_environment_sample") as Dictionary
	var clear_material := _sample_material(material)
	var clear_visible := overlay.visible

	var turbid := ENVIRONMENT_STATE_SCRIPT.new()
	turbid.set("water_turbidity", 0.84)
	turbid.set("rain_intensity", 0.18)
	turbid.set("storm_intensity", 0.24)
	adapter.call("apply_environment_state", turbid)
	controller.call("_process", 1.0 / 60.0)
	var turbid_sample := controller.call("get_underwater_environment_sample") as Dictionary
	var turbid_material := _sample_material(material)
	var routed_state := controller.get("environment_state") as Resource

	camera.position = Vector3(0.0, 0.45, 0.0)
	controller.call("_process", 1.0 / 60.0)
	var above_water_visible := overlay.visible

	camera.position = Vector3(0.0, -1.05, 0.0)
	adapter.call("apply_environment_state", clear)
	controller.call("_process", 1.0 / 60.0)
	var restored_sample := controller.call("get_underwater_environment_sample") as Dictionary
	var restored_material := _sample_material(material)

	var clear_tint: Color = clear_sample.get("underwater_tint", Color.BLACK)
	var turbid_tint: Color = turbid_sample.get("underwater_tint", Color.BLACK)
	var target_tint := Color(0.04, 0.15, 0.11, 1.0)
	var silt_color: Color = turbid_material.get("suspended_silt_color", Color.BLACK)

	var checks := {
		"clear_overlay_visible_when_submerged": clear_visible,
		"adapter_routes_turbid_state": routed_state == turbid and float(turbid_sample.get("turbidity", 0.0)) > 0.80,
		"turbidity_raises_visibility_loss": float(turbid_material.get("visibility_loss", 0.0)) > float(clear_material.get("visibility_loss", 0.0)) + 0.62,
		"turbidity_raises_tint": float(turbid_material.get("tint_strength", 0.0)) > float(clear_material.get("tint_strength", 0.0)) + 0.24,
		"turbidity_raises_distortion": float(turbid_material.get("distortion_strength", 0.0)) > float(clear_material.get("distortion_strength", 0.0)) * 1.45,
		"turbidity_raises_particulates": float(turbid_material.get("particulate_strength", 0.0)) > float(clear_material.get("particulate_strength", 0.0)) + 0.42,
		"turbidity_reduces_caustics": float(turbid_material.get("caustic_strength", 1.0)) < float(clear_material.get("caustic_strength", 1.0)) * 0.36,
		"turbidity_reduces_light_shafts": float(turbid_material.get("light_shaft_strength", 1.0)) < float(clear_material.get("light_shaft_strength", 1.0)) * 0.78,
		"turbidity_tint_moves_to_turbid_color": _color_distance(turbid_tint, target_tint) < _color_distance(clear_tint, target_tint),
		"shader_receives_silt_color": _color_distance(silt_color, Color(0.20, 0.24, 0.15, 1.0)) < 0.02,
		"above_water_hides_overlay": not above_water_visible,
		"clear_state_restores_visibility": float(restored_material.get("visibility_loss", 1.0)) < 0.02 and float(restored_sample.get("turbidity", 1.0)) < 0.02,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"clear": clear_material,
			"turbid": turbid_material,
			"restored": restored_material,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater underwater turbidity contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater underwater turbidity contract check saved: " + global_path)
	quit(0 if passed else 1)


func _sample_material(material: ShaderMaterial) -> Dictionary:
	if material == null:
		return {}
	return {
		"turbidity": float(material.get_shader_parameter("turbidity")),
		"visibility_loss": float(material.get_shader_parameter("visibility_loss")),
		"distortion_strength": float(material.get_shader_parameter("distortion_strength")),
		"tint_strength": float(material.get_shader_parameter("tint_strength")),
		"underwater_tint": material.get_shader_parameter("underwater_tint"),
		"surface_haze_strength": float(material.get_shader_parameter("surface_haze_strength")),
		"light_shaft_strength": float(material.get_shader_parameter("light_shaft_strength")),
		"caustic_strength": float(material.get_shader_parameter("caustic_strength")),
		"particulate_strength": float(material.get_shader_parameter("particulate_strength")),
		"vignette_strength": float(material.get_shader_parameter("vignette_strength")),
		"suspended_silt_color": material.get_shader_parameter("suspended_silt_color"),
	}


func _color_distance(a: Color, b: Color) -> float:
	var dr := a.r - b.r
	var dg := a.g - b.g
	var db := a.b - b.b
	return sqrt(dr * dr + dg * dg + db * db)
