extends SceneTree

const REQUIRED_TOP_LEVEL_FILES := [
	"res://addons/fast_water/plugin.cfg",
	"res://addons/fast_water/plugin.gd",
	"res://addons/fast_water/README.md",
	"res://addons/fast_water/ROADMAP.md",
	"res://addons/fast_water/CHECKLIST.md",
]

const REQUIRED_DIRECTORIES := [
	"res://addons/fast_water/scripts",
	"res://addons/fast_water/shaders",
	"res://addons/fast_water/demo",
	"res://addons/fast_water/benchmark",
	"res://addons/fast_water/tools",
	"res://addons/fast_water/docs",
]

const REQUIRED_DOCS := [
	"res://addons/fast_water/docs/MODULE_REFERENCE.md",
	"res://addons/fast_water/docs/PUBLIC_API.md",
	"res://addons/fast_water/docs/WORLD_ENGINE_INTEGRATION.md",
	"res://addons/fast_water/docs/COMPATIBILITY.md",
	"res://addons/fast_water/docs/HERO_VISUAL_ACCEPTANCE.md",
	"res://addons/fast_water/docs/PACKAGING.md",
	"res://addons/fast_water/docs/TROUBLESHOOTING.md",
]

const REQUIRED_SAMPLE_SCENES := [
	"res://addons/fast_water/demo/fast_water_lake_demo.tscn",
	"res://addons/fast_water/demo/fast_water_river_demo.tscn",
	"res://addons/fast_water/demo/fast_water_rain_demo.tscn",
	"res://addons/fast_water/demo/fast_water_underwater_demo.tscn",
	"res://addons/fast_water/demo/fast_water_waterfall_demo.tscn",
	"res://addons/fast_water/demo/fast_water_ocean_demo.tscn",
	"res://addons/fast_water/demo/fast_water_hero_reference.tscn",
	"res://addons/fast_water/demo/fast_water_full_proof.tscn",
	"res://addons/fast_water/benchmark/fast_water_benchmark.tscn",
]

const REQUIRED_SHADERS := [
	"res://addons/fast_water/shaders/fast_water_surface.gdshader",
	"res://addons/fast_water/shaders/fast_underwater_overlay.gdshader",
	"res://addons/fast_water/shaders/fast_waterfall_sheet.gdshader",
	"res://addons/fast_water/shaders/fast_wake_gpu_update.gdshader",
	"res://addons/fast_water/shaders/fast_caustics.gdshader",
	"res://addons/fast_water/shaders/fast_bubble.gdshader",
]

const PUBLIC_METHODS := [
	"add_splash",
	"add_wake_point",
	"get_surface_height_at",
	"get_water_altitude",
	"get_water_depth_at",
	"get_flow_at",
	"contains_water_point",
	"set_wake_map_texture",
	"set_flow_field_texture",
	"set_foam_field_texture",
	"export_image",
	"import_image",
	"sample_wave_height",
	"set_wave_sample_offset",
]

const CUSTOM_TYPES := [
	{"name": "FastWaterSurface", "script": "res://addons/fast_water/scripts/fast_water_surface.gd"},
	{"name": "FastWaterInteractor", "script": "res://addons/fast_water/scripts/fast_water_interactor.gd"},
	{"name": "FastWaterPath", "script": "res://addons/fast_water/scripts/fast_water_path.gd"},
	{"name": "FastWaterBuoyant", "script": "res://addons/fast_water/scripts/fast_water_buoyant.gd"},
	{"name": "FastWaterPlanarReflection", "script": "res://addons/fast_water/scripts/fast_water_planar_reflection.gd"},
	{"name": "FastWaterBowWake", "script": "res://addons/fast_water/scripts/fast_water_bow_wake.gd"},
	{"name": "FastWaterVisualProfile", "script": "res://addons/fast_water/scripts/fast_water_visual_profile.gd"},
	{"name": "FastWaterQuality", "script": "res://addons/fast_water/scripts/fast_water_quality.gd"},
	{"name": "FastWaterBodyProfile", "script": "res://addons/fast_water/scripts/fast_water_body_profile.gd"},
	{"name": "FastWaterOceanProfile", "script": "res://addons/fast_water/scripts/fast_water_ocean_profile.gd"},
	{"name": "FastWaterOcean", "script": "res://addons/fast_water/scripts/fast_water_ocean.gd"},
	{"name": "FastWaterBodyDebugOverlay", "script": "res://addons/fast_water/scripts/fast_water_body_debug_overlay.gd"},
	{"name": "FastWaterAuthoringOverlay", "script": "res://addons/fast_water/scripts/fast_water_authoring_overlay.gd"},
	{"name": "FastWaterEnvironmentState", "script": "res://addons/fast_water/scripts/fast_water_environment_state.gd"},
	{"name": "FastWaterWeatherResponse", "script": "res://addons/fast_water/scripts/fast_water_weather_response.gd"},
	{"name": "FastWaterWeatherAdapter", "script": "res://addons/fast_water/scripts/fast_water_weather_adapter.gd"},
	{"name": "FastWaterWakeMap", "script": "res://addons/fast_water/scripts/fast_water_wake_map.gd"},
	{"name": "FastWaterFlowField", "script": "res://addons/fast_water/scripts/fast_water_flow_field.gd"},
	{"name": "FastWaterFoamField", "script": "res://addons/fast_water/scripts/fast_water_foam_field.gd"},
	{"name": "FastWaterFoamReactiveFx", "script": "res://addons/fast_water/scripts/fast_water_foam_reactive_fx.gd"},
	{"name": "FastWaterGpuWakeMap", "script": "res://addons/fast_water/scripts/fast_water_gpu_wake_map.gd"},
	{"name": "FastWaterBubblePool", "script": "res://addons/fast_water/scripts/fast_water_bubble_pool.gd"},
	{"name": "FastWaterCaustics", "script": "res://addons/fast_water/scripts/fast_water_caustics.gd"},
	{"name": "FastWaterWakeRibbon", "script": "res://addons/fast_water/scripts/fast_water_wake_ribbon.gd"},
	{"name": "FastWaterSky", "script": "res://addons/fast_water/scripts/fast_water_sky.gd"},
	{"name": "FastUnderwaterController", "script": "res://addons/fast_water/scripts/fast_underwater_controller.gd"},
	{"name": "FastWaterWeatherSequence", "script": "res://addons/fast_water/scripts/fast_water_weather_sequence.gd"},
	{"name": "FastWaterRainImpactFx", "script": "res://addons/fast_water/scripts/fast_water_rain_impact_fx.gd"},
	{"name": "FastWaterSplashFx", "script": "res://addons/fast_water/scripts/fast_water_splash_fx.gd"},
	{"name": "FastWaterWaterfall", "script": "res://addons/fast_water/scripts/fast_water_waterfall.gd"},
	{"name": "FastWaterWaterfallSprayFx", "script": "res://addons/fast_water/scripts/fast_water_waterfall_spray_fx.gd"},
	{"name": "FastWaterVolume", "script": "res://addons/fast_water/scripts/fast_water_volume.gd"},
	{"name": "FastWaterSwimmer", "script": "res://addons/fast_water/scripts/fast_water_swimmer.gd"},
]


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_release_audit.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var plugin_text := _read_text("res://addons/fast_water/plugin.gd")
	var plugin_cfg_text := _read_text("res://addons/fast_water/plugin.cfg")
	var readme_text := _read_text("res://addons/fast_water/README.md")
	var public_api_text := _read_text("res://addons/fast_water/docs/PUBLIC_API.md")
	var packaging_text := _read_text("res://addons/fast_water/docs/PACKAGING.md")

	var file_results := _paths_exist(REQUIRED_TOP_LEVEL_FILES, false)
	var directory_results := _paths_exist(REQUIRED_DIRECTORIES, true)
	var doc_results := _docs_ready(REQUIRED_DOCS)
	var scene_results := _resources_load(REQUIRED_SAMPLE_SCENES, "PackedScene")
	var shader_results := _resources_load(REQUIRED_SHADERS, "Shader")
	var custom_type_results := _custom_types_ready(plugin_text)
	var public_api_results := _public_api_ready(public_api_text)
	var sidecar_result := _generated_sidecars_absent()

	var checks := {
		"top_level_files_exist": _all_values_true(file_results),
		"required_directories_exist": _all_values_true(directory_results),
		"release_docs_exist_and_have_content": _all_values_true(doc_results),
		"sample_scenes_load": _all_values_true(scene_results),
		"required_shaders_load": _all_values_true(shader_results),
		"plugin_cfg_has_required_metadata": _plugin_cfg_ready(plugin_cfg_text),
		"plugin_registers_and_unregisters_public_types": _all_values_true(custom_type_results),
		"public_api_mentions_required_methods": _all_values_true(public_api_results),
		"public_api_contract_tool_exists": FileAccess.file_exists("res://addons/fast_water/tools/check_fast_water_public_api_contract.gd"),
		"world_integration_contract_tool_exists": FileAccess.file_exists("res://addons/fast_water/tools/check_fast_water_world_integration_contract.gd"),
		"visual_review_packet_tool_exists": FileAccess.file_exists("res://addons/fast_water/tools/build_fast_water_visual_review_packet.gd"),
		"hero_motion_review_tool_exists": FileAccess.file_exists("res://addons/fast_water/tools/render_fast_water_hero_motion_review.gd"),
		"full_proof_contract_tool_exists": FileAccess.file_exists("res://addons/fast_water/tools/check_fast_water_full_proof_contract.gd"),
		"readme_links_release_docs": _readme_links_docs(readme_text),
		"packaging_lists_release_gates": _packaging_lists_gates(packaging_text),
		"generated_sidecars_absent": bool(sidecar_result.get("passed", false)),
		"water_delta_uid_absent": not FileAccess.file_exists("res://scripts/water/WaterDeltaTexture.cs.uid"),
	}
	var passed := _all_values_true(checks)
	var report := {
		"passed": passed,
		"checks": checks,
		"details": {
			"top_level_files": file_results,
			"directories": directory_results,
			"docs": doc_results,
			"sample_scenes": scene_results,
			"shaders": shader_results,
			"custom_types": custom_type_results,
			"public_api_methods": public_api_results,
			"generated_sidecars": sidecar_result,
			"custom_type_count": CUSTOM_TYPES.size(),
			"sample_scene_count": REQUIRED_SAMPLE_SCENES.size(),
			"doc_count": REQUIRED_DOCS.size(),
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater release audit could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater release audit saved: " + global_path)
	quit(0 if passed else 1)


func _paths_exist(paths: Array, directory: bool) -> Dictionary:
	var results := {}
	for path in paths:
		results[path] = _dir_exists(path) if directory else FileAccess.file_exists(path)
	return results


func _docs_ready(paths: Array) -> Dictionary:
	var results := {}
	for path in paths:
		var text := _read_text(path)
		results[path] = text.length() > 200 and text.find("#") >= 0
	return results


func _resources_load(paths: Array, expected_class: String) -> Dictionary:
	var results := {}
	for path in paths:
		var resource := load(path)
		results[path] = resource != null and resource.is_class(expected_class)
	return results


func _custom_types_ready(plugin_text: String) -> Dictionary:
	var results := {}
	for type_info in CUSTOM_TYPES:
		var name := str(type_info.get("name", ""))
		var script_path := str(type_info.get("script", ""))
		var add_ok := plugin_text.find("\"" + name + "\"") >= 0 and plugin_text.find(script_path) >= 0
		var remove_ok := plugin_text.find("remove_custom_type(\"" + name + "\")") >= 0
		results[name] = add_ok and remove_ok and FileAccess.file_exists(script_path)
	return results


func _public_api_ready(public_api_text: String) -> Dictionary:
	var results := {}
	for method_name in PUBLIC_METHODS:
		results[method_name] = public_api_text.find(method_name) >= 0
	return results


func _plugin_cfg_ready(plugin_cfg_text: String) -> bool:
	return (
		plugin_cfg_text.find("name=\"Fast Water\"") >= 0
		and plugin_cfg_text.find("version=\"") >= 0
		and plugin_cfg_text.find("script=\"plugin.gd\"") >= 0
	)


func _readme_links_docs(readme_text: String) -> bool:
	for path in REQUIRED_DOCS:
		var relative: String = str(path).trim_prefix("res://addons/fast_water/")
		if readme_text.find(relative) < 0:
			return false
	return true


func _packaging_lists_gates(packaging_text: String) -> bool:
	var required_terms := [
		"parser sweep",
		"Godot --import",
		"hero visual metrics",
		"hero motion review",
		"visual review packet",
		"--reference-check",
		"public API contract",
		"runtime contracts",
		"world integration contract",
		"clean-copy verification",
		"no Godot processes",
	]
	for term in required_terms:
		if packaging_text.find(term) < 0:
			return false
	return true


func _generated_sidecars_absent() -> Dictionary:
	var leftovers: Array[String] = []
	var artifacts := DirAccess.open("res://artifacts")
	if artifacts != null:
		artifacts.list_dir_begin()
		var file_name := artifacts.get_next()
		while file_name != "":
			if not artifacts.current_is_dir() and file_name.begins_with("fast_water"):
				var extension := file_name.get_extension()
				if extension == "import" or extension == "log" or extension == "err":
					leftovers.append("res://artifacts/" + file_name)
			file_name = artifacts.get_next()
		artifacts.list_dir_end()
	return {
		"passed": leftovers.is_empty(),
		"leftovers": leftovers,
	}


func _all_values_true(values: Variant) -> bool:
	if values is Dictionary:
		for value in values.values():
			if not bool(value):
				return false
		return true
	if values is Array:
		for value in values:
			if not bool(value):
				return false
		return true
	return bool(values)


func _read_text(path: String) -> String:
	if not FileAccess.file_exists(path):
		return ""
	return FileAccess.get_file_as_string(path)


func _dir_exists(path: String) -> bool:
	return DirAccess.dir_exists_absolute(ProjectSettings.globalize_path(path))
