extends SceneTree

const DEMO_SCENE := "res://addons/fast_water/demo/fast_water_river_demo.tscn"

var _demo: Node


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_river_demo.png"
	var flow_debug_path := ""
	var frames := 180

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--flow-debug="):
			flow_debug_path = arg.trim_prefix("--flow-debug=")
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))

	var packed := load(DEMO_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater river render gate could not load " + DEMO_SCENE)
		quit(1)
		return

	_demo = packed.instantiate()
	if _has_property(_demo, "gate_external_capture"):
		_demo.set("gate_external_capture", true)
	root.add_child(_demo)
	print("FastWater river render gate loaded demo")
	_capture.call_deferred(screenshot_path, flow_debug_path, frames)


func _capture(screenshot_path: String, flow_debug_path: String, frames: int) -> void:
	for _i in range(frames):
		await process_frame

	if _find_named(_demo, "DownhillRiver") == null:
		push_error("FastWater river render gate did not find DownhillRiver; demo script likely failed to load")
		quit(1)
		return

	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater river render gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)

	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater river render gate failed to save " + global_path)
		quit(1)
		return

	print("FastWater river render gate screenshot saved: " + global_path)
	if flow_debug_path != "":
		if not _save_flow_debug(flow_debug_path):
			quit(1)
			return
	quit(0)


func _save_flow_debug(path: String) -> bool:
	if _demo != null and _demo.has_method("save_flow_debug"):
		if bool(_demo.call("save_flow_debug", path)):
			print("FastWater river render gate flow debug saved: " + ProjectSettings.globalize_path(path))
			return true
	var flow_field := _find_flow_field(root)
	if flow_field == null:
		push_warning("FastWater river render gate could not find FlowField for debug capture")
		return false
	if flow_field.has_method("rebuild"):
		flow_field.call("rebuild")
	var tex := flow_field.get("texture") as Texture2D
	if tex == null:
		push_warning("FastWater river render gate found FlowField without texture")
		return false
	var image := tex.get_image()
	if image == null:
		push_warning("FastWater river render gate could not read FlowField texture image")
		return false
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(global_path)
	if err != OK:
		push_warning("FastWater river render gate failed to save flow debug image")
		return false
	print("FastWater river render gate flow debug saved: " + global_path)
	return true


func _find_named(node: Node, target_name: String) -> Node:
	if node == null:
		return null
	if node.name == target_name:
		return node
	for child in node.get_children():
		var found := _find_named(child, target_name)
		if found != null:
			return found
	return null


func _find_flow_field(node: Node) -> Node:
	if _is_flow_field(node):
		return node
	for child in node.get_children():
		var found := _find_flow_field(child)
		if found != null:
			return found
	return null


func _is_flow_field(node: Node) -> bool:
	if node.name == "FlowField":
		return true
	var script := node.get_script() as Script
	if script != null and script.resource_path.ends_with("/fast_water_flow_field.gd"):
		return true
	return false


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
