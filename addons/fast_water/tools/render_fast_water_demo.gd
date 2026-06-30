extends SceneTree

const DEMO_SCENE := "res://addons/fast_water/demo/fast_water_demo.tscn"


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_demo.png"
	var wake_debug_path := ""
	var frames := 180
	var mode := ""

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--wake-debug="):
			wake_debug_path = arg.trim_prefix("--wake-debug=")
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--mode="):
			mode = arg.trim_prefix("--mode=")

	var packed := load(DEMO_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater render gate could not load " + DEMO_SCENE)
		quit(1)
		return

	var demo := packed.instantiate()
	if _has_property(demo, "gate_external_capture"):
		demo.set("gate_external_capture", true)
	if mode != "" and _has_property(demo, "_mode"):
		demo.set("_mode", mode)
	root.add_child(demo)
	print("FastWater render gate loaded demo")
	_capture.call_deferred(screenshot_path, wake_debug_path, frames)


func _capture(screenshot_path: String, wake_debug_path: String, frames: int) -> void:
	for _i in range(frames):
		await process_frame

	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater render gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)

	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater render gate failed to save " + global_path)
		quit(1)
		return

	print("FastWater render gate screenshot saved: " + global_path)
	if wake_debug_path != "":
		_save_wake_debug(wake_debug_path)
	quit(0)


func _save_wake_debug(path: String) -> void:
	var wake := root.find_child("WakeMap", true, false)
	if wake == null or not _has_property(wake, "texture"):
		return
	var tex := wake.get("texture") as Texture2D
	if tex == null:
		return
	var image := tex.get_image()
	if image == null:
		return
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	image.save_png(global_path)
	print("FastWater render gate wake debug saved: " + global_path)


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
