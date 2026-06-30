extends SceneTree

const FOAM_FIELD_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_field.gd")
const REACTIVE_FX_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_foam_reactive_fx.gd")


class BurstSpy:
	extends Node3D

	var events: Array[Dictionary] = []

	func burst(world_pos: Vector3, strength: float = 1.0, radius: float = 0.5) -> void:
		events.append({
			"method": "burst",
			"position": [world_pos.x, world_pos.y, world_pos.z],
			"strength": strength,
			"radius": radius,
		})


class RestartSpy:
	extends Node3D

	var events: Array[Dictionary] = []

	func restart(strength: float = 1.0, radius: float = 1.0) -> void:
		events.append({
			"method": "restart",
			"position": [global_position.x, global_position.y, global_position.z],
			"strength": strength,
			"radius": radius,
		})


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_foam_reactive_fx_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterFoamReactiveFxContract"
	root.add_child(scene)

	var foam_field := FOAM_FIELD_SCRIPT.new()
	foam_field.name = "FoamField"
	foam_field.set("resolution", 32)
	foam_field.set("world_size_m", 24.0)
	foam_field.set("origin_xz", Vector2.ZERO)
	foam_field.set("dissipation_per_second", 0.0)
	scene.add_child(foam_field)

	var bubbles := BurstSpy.new()
	bubbles.name = "BubblePool"
	scene.add_child(bubbles)

	var spray := RestartSpy.new()
	spray.name = "SprayFx"
	scene.add_child(spray)

	var reactive_fx := REACTIVE_FX_SCRIPT.new()
	reactive_fx.name = "FoamReactiveFx"
	reactive_fx.set("foam_field", foam_field)
	reactive_fx.set("bubble_pool", bubbles)
	reactive_fx.set("spray_fx", spray)
	reactive_fx.set("max_events_per_frame", 2)
	reactive_fx.set("bubble_min_intensity", 0.05)
	reactive_fx.set("spray_min_intensity", 0.10)
	scene.add_child(reactive_fx)

	await process_frame
	reactive_fx.call("refresh_connections")
	var callback := Callable(reactive_fx, "_on_foam_source_added")

	foam_field.call("add_foam_stamp", Vector3(-2.0, 0.0, 0.0), 1.0, 0.70, FOAM_FIELD_SCRIPT.SourceKind.WAKE, Vector3(1.0, 0.0, 0.0))
	foam_field.call("add_foam_stamp", Vector3(0.0, 0.0, 0.0), 1.2, 0.75, FOAM_FIELD_SCRIPT.SourceKind.RAPIDS, Vector3(2.0, 0.0, 0.0))
	foam_field.call("add_foam_stamp", Vector3(2.0, 0.0, 0.0), 1.4, 0.80, FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP, Vector3(0.0, 0.0, 2.0))

	var first_frame_bubble_events := bubbles.events.size()
	var first_frame_spray_events := spray.events.size()

	await process_frame
	foam_field.call("add_foam_stamp", Vector3(2.0, 0.0, 0.0), 1.4, 0.80, FOAM_FIELD_SCRIPT.SourceKind.WATERFALL_LIP, Vector3(0.0, 0.0, 2.0))
	var after_waterfall_bubble_events := bubbles.events.size()
	var after_waterfall_spray_events := spray.events.size()
	var pre_filtered_events := bubbles.events.size() + spray.events.size()
	foam_field.call("add_foam_stamp", Vector3(4.0, 0.0, 0.0), 1.0, 0.80, FOAM_FIELD_SCRIPT.SourceKind.SHORELINE, Vector3.ZERO)
	var post_filtered_events := bubbles.events.size() + spray.events.size()

	var first_bubble := bubbles.events[0] if not bubbles.events.is_empty() else {}
	var first_spray := spray.events[0] if not spray.events.is_empty() else {}
	var last_spray := spray.events[spray.events.size() - 1] if not spray.events.is_empty() else {}
	var last_spray_position: Array = last_spray.get("position", [])
	var checks := {
		"signal_connected": foam_field.is_connected("foam_source_added", callback),
		"wake_triggers_bubbles": first_frame_bubble_events >= 1,
		"rapids_triggers_bubbles_and_spray": first_frame_bubble_events >= 2 and first_frame_spray_events >= 1,
		"event_budget_limited_first_frame": first_frame_bubble_events == 2 and first_frame_spray_events == 1,
		"budget_resets_next_frame": after_waterfall_bubble_events > first_frame_bubble_events and after_waterfall_spray_events > first_frame_spray_events,
		"shoreline_filtered": post_filtered_events == pre_filtered_events,
		"bubble_radius_scaled": first_bubble.has("radius") and absf(float(first_bubble["radius"]) - 0.72) < 0.02,
		"spray_restart_positioned": last_spray_position.size() == 3 and absf(float(last_spray_position[1]) - 0.08) < 0.01,
		"strengths_clamped": _events_are_clamped(bubbles.events) and _events_are_clamped(spray.events),
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"counts": {
			"first_frame_bubbles": first_frame_bubble_events,
			"first_frame_spray": first_frame_spray_events,
			"after_waterfall_bubbles": after_waterfall_bubble_events,
			"after_waterfall_spray": after_waterfall_spray_events,
		},
		"samples": {
			"first_bubble": first_bubble,
			"first_spray": first_spray,
			"last_spray": last_spray,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater foam reactive FX contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater foam reactive FX contract check saved: " + global_path)
	quit(0 if passed else 1)


func _events_are_clamped(events: Array[Dictionary]) -> bool:
	for event in events:
		var strength := float(event.get("strength", -1.0))
		if strength < 0.0 or strength > 1.0:
			return false
	return true
