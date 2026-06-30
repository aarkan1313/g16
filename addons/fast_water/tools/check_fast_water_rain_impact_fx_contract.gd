extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const WEATHER_ADAPTER_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_adapter.gd")
const ENVIRONMENT_STATE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_environment_state.gd")
const WEATHER_RESPONSE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_weather_response.gd")
const RAIN_IMPACT_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_rain_impact_fx.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_rain_impact_fx_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterRainImpactFxContract"
	root.add_child(scene)

	var surface := SURFACE_SCRIPT.new()
	surface.name = "RainWaterSurface"
	surface.set("auto_create_mesh", false)
	surface.set("auto_create_wake_map", false)
	surface.set("auto_create_bubbles", false)
	scene.add_child(surface)

	var rain_fx := RAIN_IMPACT_FX_SCRIPT.new()
	rain_fx.name = "RainImpactFx"
	rain_fx.set("impact_particle_cap", 160)
	rain_fx.set("mist_particle_cap", 80)
	surface.add_child(rain_fx)

	var state := ENVIRONMENT_STATE_SCRIPT.new()
	state.set("wind_direction_xz", Vector2(1.0, 0.0))
	state.set("wind_speed_mps", 10.0)
	state.set("rain_intensity", 0.70)
	state.set("storm_intensity", 0.20)

	var response := WEATHER_RESPONSE_SCRIPT.new()
	response.set("rain_impact_visibility", 0.90)
	response.set("rain_mist_visibility", 0.55)

	var adapter := WEATHER_ADAPTER_SCRIPT.new()
	adapter.name = "WeatherAdapter"
	adapter.set("environment_state", state)
	adapter.set("weather_response", response)
	adapter.set("rain_area_m", 28.0)
	adapter.set("auto_discover_surfaces", false)
	adapter.set("auto_discover_rain_impact_fx", false)
	scene.add_child(adapter)
	var surface_paths: Array[NodePath] = [adapter.get_path_to(surface)]
	var rain_fx_paths: Array[NodePath] = [adapter.get_path_to(rain_fx)]
	adapter.set("water_surface_paths", surface_paths)
	adapter.set("rain_impact_fx_paths", rain_fx_paths)

	await process_frame
	adapter.call("_apply_to_surfaces", 1.0 / 30.0)
	await process_frame

	var impact := rain_fx.get_node_or_null("RainImpactDrops") as GPUParticles3D
	var mist := rain_fx.get_node_or_null("RainSurfaceMist") as GPUParticles3D
	var impact_process := impact.process_material as ParticleProcessMaterial if impact != null else null
	var mist_process := mist.process_material as ParticleProcessMaterial if mist != null else null
	var active_checks := {
		"impact_emitter_exists": impact != null,
		"mist_emitter_exists": mist != null,
		"fx_receives_intensity": absf(float(rain_fx.get("last_intensity")) - 0.70) < 0.01,
		"fx_receives_area": absf(float(rain_fx.get("last_area_size_m")) - 28.0) < 0.01,
		"impact_emitting": impact != null and impact.emitting and impact.amount_ratio > 0.55,
		"mist_emitting": mist != null and mist.emitting and mist.amount_ratio > 0.30,
		"area_bound_to_impact": impact_process != null and absf(impact_process.emission_box_extents.x - 14.0) < 0.01,
		"area_bound_to_mist": mist_process != null and absf(mist_process.emission_box_extents.z - 14.0) < 0.01,
		"wind_moves_particles": impact_process != null and impact_process.gravity.x > 0.05,
		"group_registered": rain_fx.is_in_group("fast_water_rain_impact_fx"),
	}
	var active_impact_amount_ratio := impact.amount_ratio if impact != null else -1.0
	var active_mist_amount_ratio := mist.amount_ratio if mist != null else -1.0

	state.set("rain_intensity", 0.0)
	adapter.call("_apply_to_surfaces", 1.0 / 30.0)
	await process_frame
	var stopped_checks := {
		"fx_stops_when_rain_stops": not bool(rain_fx.get("last_emitting")),
		"impact_amount_zero": impact != null and impact.amount_ratio <= 0.001,
		"mist_amount_zero": mist != null and mist.amount_ratio <= 0.001,
	}

	var checks := {}
	for key in active_checks.keys():
		checks[key] = active_checks[key]
	for key in stopped_checks.keys():
		checks[key] = stopped_checks[key]
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"active_impact_amount_ratio": active_impact_amount_ratio,
			"active_mist_amount_ratio": active_mist_amount_ratio,
			"stopped_impact_amount_ratio": impact.amount_ratio if impact != null else -1.0,
			"stopped_mist_amount_ratio": mist.amount_ratio if mist != null else -1.0,
			"impact_gravity": _vec3_to_array(impact_process.gravity) if impact_process != null else [],
			"mist_gravity": _vec3_to_array(mist_process.gravity) if mist_process != null else [],
			"fx_position": _vec3_to_array(rain_fx.global_position),
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater rain impact FX contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater rain impact FX contract check saved: " + global_path)
	quit(0 if passed else 1)


func _vec3_to_array(value: Vector3) -> Array[float]:
	return [value.x, value.y, value.z]
