extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const FLOW_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_flow_field.gd")
const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const AUTHORING_OVERLAY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_authoring_overlay.gd")
const WATERFALL_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_waterfall.gd")

const PUBLIC_API_DOC := "res://addons/fast_water/docs/PUBLIC_API.md"

const INTERACTION_METHODS := ["add_splash", "add_wake_point"]
const QUERY_METHODS := [
	"get_surface_height_at",
	"get_water_altitude",
	"get_water_depth_at",
	"get_flow_at",
	"contains_water_point",
]
const SURFACE_TEXTURE_METHODS := [
	"set_wake_map_texture",
	"set_flow_field_texture",
	"set_foam_field_texture",
]
const SURFACE_PROFILE_METHODS := [
	"apply_visual_profile",
	"apply_quality",
	"apply_environment_state",
	"emit_rain_ripples",
]
const OCEAN_METHODS := ["apply_ocean_profile", "get_surface_nodes", "get_ocean_debug_state"]
const MAP_BAKE_METHODS := ["export_image", "import_image"]
const AUTHORING_METHODS := ["rebuild_now", "get_debug_state"]
const WATERFALL_METHODS := ["get_lod_state", "stamp_waterfall_response"]


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_public_api_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")
	_run.call_deferred(output_path)


func _run(output_path: String) -> void:
	var surface := SURFACE_SCRIPT.new()
	var path := PATH_SCRIPT.new()
	var ocean := OCEAN_SCRIPT.new()
	var flow_field := FLOW_FIELD_SCRIPT.new()
	var foam_field := FOAM_FIELD_SCRIPT.new()
	var authoring_overlay := AUTHORING_OVERLAY_SCRIPT.new()
	var waterfall := WATERFALL_SCRIPT.new()
	var harness := Node3D.new()
	harness.name = "FastWaterPublicApiContractHarness"
	root.add_child(harness)
	for node in [surface, path, ocean, flow_field, foam_field, authoring_overlay, waterfall]:
		harness.add_child(node)
	await process_frame
	if path.has_method("rebuild_path_mesh"):
		path.call("rebuild_path_mesh")
	if waterfall.has_method("rebuild"):
		waterfall.call("rebuild")
	await process_frame

	var checks := {
		"public_api_doc_exists": FileAccess.file_exists(PUBLIC_API_DOC),
		"public_api_doc_mentions_methods": _public_api_doc_mentions_methods(),
		"surface_interaction_methods": _has_methods(surface, INTERACTION_METHODS),
		"path_interaction_methods": _has_methods(path, INTERACTION_METHODS),
		"ocean_interaction_methods": _has_methods(ocean, INTERACTION_METHODS),
		"surface_query_methods": _has_methods(surface, QUERY_METHODS),
		"path_query_methods": _has_methods(path, QUERY_METHODS),
		"ocean_query_methods": _has_methods(ocean, QUERY_METHODS),
		"surface_texture_binding_methods": _has_methods(surface, SURFACE_TEXTURE_METHODS),
		"surface_profile_weather_methods": _has_methods(surface, SURFACE_PROFILE_METHODS),
		"ocean_profile_debug_methods": _has_methods(ocean, OCEAN_METHODS),
		"flow_field_map_bake_methods": _has_methods(flow_field, MAP_BAKE_METHODS),
		"foam_field_map_bake_methods": _has_methods(foam_field, MAP_BAKE_METHODS),
		"authoring_overlay_debug_methods": _has_methods(authoring_overlay, AUTHORING_METHODS),
		"waterfall_debug_methods": _has_methods(waterfall, WATERFALL_METHODS),
		"surface_query_return_types": _water_body_query_returns(surface),
		"path_query_return_types": _water_body_query_returns(path),
		"ocean_query_return_types": _water_body_query_returns(ocean),
		"ocean_surface_nodes_returns_array": ocean.call("get_surface_nodes") is Array,
		"ocean_debug_state_returns_dictionary": ocean.call("get_ocean_debug_state") is Dictionary,
		"authoring_debug_state_returns_dictionary": authoring_overlay.call("get_debug_state") is Dictionary,
		"waterfall_lod_state_returns_dictionary": waterfall.call("get_lod_state") is Dictionary,
		"waterfall_stamp_returns_int": waterfall.call("stamp_waterfall_response") is int,
	}
	var waterfall_lod_state := waterfall.call("get_lod_state") as Dictionary
	checks["waterfall_lod_state_has_public_fields"] = (
		waterfall_lod_state.has("state")
		and waterfall_lod_state.has("visible")
		and waterfall_lod_state.has("lod_enabled")
		and waterfall_lod_state.has("far_lod_distance_m")
		and waterfall_lod_state.has("max_visible_distance_m")
	)

	var passed := _all_values_true(checks)
	var report := {
		"passed": passed,
		"checks": checks,
		"details": {
			"surface_methods": INTERACTION_METHODS + QUERY_METHODS + SURFACE_TEXTURE_METHODS + SURFACE_PROFILE_METHODS,
			"path_methods": INTERACTION_METHODS + QUERY_METHODS,
			"ocean_methods": INTERACTION_METHODS + QUERY_METHODS + OCEAN_METHODS,
			"map_bake_methods": MAP_BAKE_METHODS,
			"authoring_methods": AUTHORING_METHODS,
			"waterfall_methods": WATERFALL_METHODS,
			"waterfall_lod_state": waterfall_lod_state,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater public API contract could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater public API contract saved: " + global_path)
	quit(0 if passed else 1)


func _has_methods(object: Object, methods: Array) -> bool:
	for method_name in methods:
		if not object.has_method(str(method_name)):
			return false
	return true


func _water_body_query_returns(body: Object) -> bool:
	return (
		body.call("get_surface_height_at", Vector3.ZERO) is float
		and body.call("get_water_altitude", Vector3.ZERO) is float
		and body.call("get_water_depth_at", Vector3.ZERO) is float
		and body.call("get_flow_at", Vector3.ZERO) is Vector3
		and body.call("contains_water_point", Vector3.ZERO) is bool
	)


func _public_api_doc_mentions_methods() -> bool:
	var text := FileAccess.get_file_as_string(PUBLIC_API_DOC)
	for method_name in (
		INTERACTION_METHODS
		+ QUERY_METHODS
		+ SURFACE_TEXTURE_METHODS
		+ SURFACE_PROFILE_METHODS
		+ OCEAN_METHODS
		+ MAP_BAKE_METHODS
		+ AUTHORING_METHODS
		+ WATERFALL_METHODS
	):
		if text.find(str(method_name)) < 0:
			return false
	return true


func _all_values_true(values: Dictionary) -> bool:
	for value in values.values():
		if not bool(value):
			return false
	return true
