extends SceneTree
# Gates the gameplay/engine integration layer: surface outbound signals, the
# FastWater autoload service queries, FastWaterVolume signal re-emit + depth query,
# and FastWaterSwimmer submersion state.

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const VOLUME_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_volume.gd")
const SWIMMER_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_swimmer.gd")
const SERVICE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_service.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")

var _splashed := false
var _waked := false
var _volume_entered := false


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_integration_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)

	var lake := SURFACE_SCRIPT.new()
	lake.set("auto_create_mesh", false)
	lake.set("auto_create_wake_map", false)
	lake.set("auto_create_bubbles", false)
	lake.set("follow_camera", false)
	lake.set("mesh_size_m", 80.0)
	lake.set("body_profile", BODY_PROFILE_SCRIPT.lake())
	scene.add_child(lake)
	lake.connect("splashed", func(_p, _s): _splashed = true)
	lake.connect("wake_added", func(_p, _v): _waked = true)

	for _i in range(4):
		await process_frame

	# Surface signals.
	lake.call("add_splash", Vector3(0, 0, 0), Vector3(0, -3, 0), 0.5)
	lake.call("add_wake_point", Vector3(1, 0, 1), Vector3(4, 0, 0), 0.5)

	# Service queries.
	var service := SERVICE_SCRIPT.new()
	service.name = "FastWaterServiceTest"
	root.add_child(service)
	var over := Vector3(0, 5, 0)
	var under := Vector3(0, -1, 0)
	var svc_height := float(service.call("height_at", over))
	var svc_depth := float(service.call("depth_at", under))
	var svc_contains := bool(service.call("contains_point", under))
	var svc_body := service.call("nearest_body", under)

	# Volume signal re-emit + depth query.
	var volume := VOLUME_SCRIPT.new()
	scene.add_child(volume)
	volume.connect("body_entered_water", func(_b): _volume_entered = true)
	volume.call("_on_body_entered", scene)
	var vol_depth := float(volume.call("get_submersion_depth", under))

	# Swimmer submersion state (actor below the surface).
	var actor := Node3D.new()
	scene.add_child(actor)
	actor.global_position = Vector3(0, -0.6, 0)
	var swimmer := SWIMMER_SCRIPT.new()
	swimmer.set("apply_float", false)
	swimmer.set("actor_path", actor.get_path())
	scene.add_child(swimmer)
	for _i in range(6):
		await process_frame

	var checks := {
		"surface_emits_splashed": _splashed,
		"surface_emits_wake_added": _waked,
		"service_height_near_surface": absf(svc_height) < 1.0,
		"service_depth_positive": svc_depth > 0.5,
		"service_contains_submerged_point": svc_contains,
		"service_resolves_body": svc_body != null,
		"volume_reemits_entered_signal": _volume_entered,
		"volume_reports_submersion_depth": vol_depth > 0.5,
		"swimmer_detects_submersion": bool(swimmer.get("is_submerged")) and float(swimmer.get("submersion_depth")) > 0.0,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"svc_height": svc_height,
			"svc_depth": svc_depth,
			"vol_depth": vol_depth,
			"swimmer_depth": float(swimmer.get("submersion_depth")),
		},
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var file := FileAccess.open(g, FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater integration contract saved: " + g)
	quit(0 if passed else 1)
