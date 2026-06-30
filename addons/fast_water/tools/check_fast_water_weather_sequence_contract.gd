extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const WEATHER_ADAPTER_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_adapter.gd")
const RAIN_IMPACT_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_rain_impact_fx.gd")
const WEATHER_SEQUENCE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_sequence.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_weather_sequence_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterWeatherSequenceContract"
	root.add_child(scene)

	var surface := SURFACE_SCRIPT.new()
	surface.name = "SequenceWaterSurface"
	surface.set("auto_create_mesh", false)
	surface.set("auto_create_wake_map", false)
	surface.set("auto_create_bubbles", false)
	scene.add_child(surface)

	var rain_fx := RAIN_IMPACT_FX_SCRIPT.new()
	rain_fx.name = "RainImpactFx"
	rain_fx.set("impact_particle_cap", 120)
	rain_fx.set("mist_particle_cap", 80)
	surface.add_child(rain_fx)

	var adapter := WEATHER_ADAPTER_SCRIPT.new()
	adapter.name = "WeatherAdapter"
	adapter.set("apply_every_frame", false)
	adapter.set("auto_discover_surfaces", false)
	adapter.set("auto_discover_rain_impact_fx", false)
	adapter.set("rain_area_m", 30.0)
	scene.add_child(adapter)
	var surface_paths: Array[NodePath] = [adapter.get_path_to(surface)]
	var rain_fx_paths: Array[NodePath] = [adapter.get_path_to(rain_fx)]
	adapter.set("water_surface_paths", surface_paths)
	adapter.set("rain_impact_fx_paths", rain_fx_paths)

	var sequence := WEATHER_SEQUENCE_SCRIPT.new()
	sequence.name = "WeatherSequence"
	sequence.set("adapter", adapter)
	sequence.set("playing", false)
	sequence.set("loop", true)
	scene.add_child(sequence)

	await process_frame

	var names := Array(sequence.call("get_stage_names"))
	var total_duration := float(sequence.call("get_total_duration"))
	var clear := sequence.call("sample_at", 1.0) as Dictionary
	var drizzle := sequence.call("sample_at", 6.0) as Dictionary
	var heavy_rain := sequence.call("sample_at", 11.5) as Dictionary
	var storm := sequence.call("sample_at", 17.0) as Dictionary
	var calm_after := sequence.call("sample_at", 22.0) as Dictionary
	var wrapped := sequence.call("sample_at", total_duration + 1.0) as Dictionary

	sequence.call("set_time", 17.0)
	adapter.call("_apply_to_surfaces", 1.0 / 30.0)
	await process_frame
	var material := surface.get("water_material") as ShaderMaterial
	var storm_state := adapter.get("environment_state") as Resource
	var storm_state_rain := float(storm_state.get("rain_intensity")) if storm_state != null else -1.0
	var storm_state_storm := float(storm_state.get("storm_intensity")) if storm_state != null else -1.0
	var storm_impact := rain_fx.get_node_or_null("RainImpactDrops") as GPUParticles3D
	var storm_mist := rain_fx.get_node_or_null("RainSurfaceMist") as GPUParticles3D
	var storm_impact_ratio := storm_impact.amount_ratio if storm_impact != null else -1.0
	var storm_mist_ratio := storm_mist.amount_ratio if storm_mist != null else -1.0
	var storm_whitecap := float(material.get_shader_parameter("storm_whitecap_strength")) if material != null else -1.0

	sequence.call("set_time", 22.0)
	adapter.call("_apply_to_surfaces", 1.0 / 30.0)
	await process_frame
	var calm_state := adapter.get("environment_state") as Resource
	var calm_impact_ratio := storm_impact.amount_ratio if storm_impact != null else -1.0
	var calm_whitecap := float(material.get_shader_parameter("storm_whitecap_strength")) if material != null else -1.0

	var expected_names := ["clear", "drizzle", "heavy_rain", "storm", "calm_after_storm"]
	var checks := {
		"has_all_stage_names": _contains_names(names, expected_names),
		"total_duration_positive": total_duration >= 24.0,
		"clear_stage_is_dry": clear.get("stage_name", "") == "clear" and float(clear.get("rain_intensity", -1.0)) <= 0.01,
		"drizzle_stage_has_light_rain": drizzle.get("stage_name", "") == "drizzle" and float(drizzle.get("rain_intensity", 0.0)) > 0.10 and float(drizzle.get("rain_intensity", 1.0)) < 0.35,
		"heavy_rain_exceeds_drizzle": heavy_rain.get("stage_name", "") == "heavy_rain" and float(heavy_rain.get("rain_intensity", 0.0)) > float(drizzle.get("rain_intensity", 0.0)) + 0.35,
		"storm_has_wind_gusts_and_storm": storm.get("stage_name", "") == "storm" and float(storm.get("wind_speed_mps", 0.0)) >= 14.0 and float(storm.get("gust_strength", 0.0)) > 0.75 and float(storm.get("storm_intensity", 0.0)) > 0.85,
		"calm_after_storm_relaxes_rain": calm_after.get("stage_name", "") == "calm_after_storm" and float(calm_after.get("rain_intensity", 1.0)) < 0.10 and float(calm_after.get("water_turbidity", 0.0)) > 0.15,
		"loop_wraps_to_clear": wrapped.get("stage_name", "") == "clear",
		"adapter_received_storm_state": storm_state_storm > 0.85 and storm_state_rain > 0.90,
		"storm_surface_whitecaps": storm_whitecap > 1.0,
		"storm_rain_fx_visible": storm_impact_ratio > 0.70 and storm_mist_ratio > 0.40,
		"calm_state_reduces_rain": calm_state != null and float(calm_state.get("rain_intensity")) < 0.10,
		"calm_reduces_rain_fx": calm_impact_ratio < storm_impact_ratio * 0.20,
		"calm_reduces_whitecaps": calm_whitecap < storm_whitecap * 0.20,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"stages": {
			"names": names,
			"total_duration": total_duration,
			"clear": _stage_summary(clear),
			"drizzle": _stage_summary(drizzle),
			"heavy_rain": _stage_summary(heavy_rain),
			"storm": _stage_summary(storm),
			"calm_after_storm": _stage_summary(calm_after),
			"wrapped": _stage_summary(wrapped),
		},
		"samples": {
			"storm_whitecap": storm_whitecap,
			"storm_state_rain": storm_state_rain,
			"storm_state_storm": storm_state_storm,
			"calm_whitecap": calm_whitecap,
			"storm_impact_amount_ratio": storm_impact_ratio,
			"storm_mist_amount_ratio": storm_mist_ratio,
			"calm_impact_amount_ratio": calm_impact_ratio,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater weather sequence contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater weather sequence contract check saved: " + global_path)
	quit(0 if passed else 1)


func _contains_names(names: Array, expected: Array) -> bool:
	for name in expected:
		if not names.has(name):
			return false
	return true


func _stage_summary(stage: Dictionary) -> Dictionary:
	return {
		"name": stage.get("stage_name", ""),
		"rain": float(stage.get("rain_intensity", 0.0)),
		"storm": float(stage.get("storm_intensity", 0.0)),
		"wind": float(stage.get("wind_speed_mps", 0.0)),
		"gust": float(stage.get("gust_strength", 0.0)),
		"turbidity": float(stage.get("water_turbidity", 0.0)),
	}
