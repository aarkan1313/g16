extends SceneTree

const DEMO_SCRIPT := preload("res://addons/fast_water/demo/fast_water_river_demo.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_river_demo_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var demo := DEMO_SCRIPT.new() as Node3D
	demo.name = "FastWaterRiverDemoContract"
	demo.set("gate_external_capture", true)
	root.add_child(demo)

	await process_frame
	await process_frame
	await process_frame

	var river := _find_named(demo, "DownhillRiver")
	var flow_field := river.get_node_or_null("FlowField") if river != null else null
	var counts := {
		"wet_banks": _count_prefix(demo, "RiverWetBank"),
		"gravel_banks": _count_prefix(demo, "RiverGravelBank"),
		"bank_rocks": _count_prefix(demo, "RiverBankRock"),
		"rapid_rocks": _count_prefix(demo, "RiverRapidRock"),
		"drift_logs": _count_prefix(demo, "RiverDriftLog"),
		"flow_drifters": _count_prefix(demo, "FlowDrifter"),
	}
	var checks := {
		"terrain_exists": _find_named(demo, "RiverValley") != null,
		"river_exists": river != null,
		"flow_field_exists": flow_field != null,
		"wet_banks": int(counts["wet_banks"]) >= 2,
		"gravel_banks": int(counts["gravel_banks"]) >= 2,
		"bank_rock_density": int(counts["bank_rocks"]) >= 40,
		"rapid_zone_rocks": int(counts["rapid_rocks"]) >= 8,
		"drift_logs": int(counts["drift_logs"]) >= 3,
		"flow_drifters": int(counts["flow_drifters"]) >= 4,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"counts": counts,
		"river_has_flow_query": river != null and river.has_method("get_flow_at"),
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater river demo contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater river demo contract check saved: " + global_path)
	quit(0 if passed else 1)


func _find_named(node: Node, target_name: String) -> Node:
	if node.name == target_name:
		return node
	for child in node.get_children():
		var found := _find_named(child, target_name)
		if found != null:
			return found
	return null


func _count_prefix(node: Node, prefix: String) -> int:
	var count := 1 if node.name.begins_with(prefix) else 0
	for child in node.get_children():
		count += _count_prefix(child, prefix)
	return count
