extends SceneTree

const BENCHMARK_SCENE := "res://addons/fast_water/benchmark/fast_water_benchmark.tscn"


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_benchmark.png"
	var wake_debug_path := ""
	var metrics_path := ""
	var frames := 210
	var debug_view := 0

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--wake-debug="):
			wake_debug_path = arg.trim_prefix("--wake-debug=")
		elif arg.begins_with("--metrics="):
			metrics_path = arg.trim_prefix("--metrics=")
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--debug-view="):
			debug_view = max(0, int(arg.trim_prefix("--debug-view=")))

	var packed := load(BENCHMARK_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater benchmark gate could not load " + BENCHMARK_SCENE)
		quit(1)
		return

	var benchmark := packed.instantiate()
	if _has_property(benchmark, "gate_external_capture"):
		benchmark.set("gate_external_capture", true)
	if _has_property(benchmark, "external_debug_view"):
		benchmark.set("external_debug_view", debug_view)
	root.add_child(benchmark)
	print("FastWater benchmark gate loaded scene")
	_capture.call_deferred(screenshot_path, wake_debug_path, metrics_path, frames)


func _capture(screenshot_path: String, wake_debug_path: String, metrics_path: String, frames: int) -> void:
	var warmup_frames := mini(60, max(0, frames / 3))
	for _i in range(warmup_frames):
		await process_frame

	var measured_frames := max(frames - warmup_frames, 1)
	var start_usec := Time.get_ticks_usec()
	for _i in range(measured_frames):
		await process_frame
	var elapsed_ms: float = float(Time.get_ticks_usec() - start_usec) / 1000.0
	var avg_ms: float = elapsed_ms / float(measured_frames)
	var avg_fps: float = 1000.0 / max(avg_ms, 0.001)
	print("FastWater benchmark average after warmup: %.2f ms %.1f FPS over %d frames" % [avg_ms, avg_fps, measured_frames])
	if metrics_path != "":
		_save_metrics(metrics_path, avg_ms, avg_fps, measured_frames)

	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater benchmark gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)

	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater benchmark gate failed to save " + global_path)
		quit(1)
		return

	print("FastWater benchmark gate screenshot saved: " + global_path)
	if wake_debug_path != "":
		_save_wake_debug(wake_debug_path)
	quit(0)


func _save_metrics(path: String, avg_ms: float, avg_fps: float, measured_frames: int) -> void:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater benchmark gate failed to save metrics " + global_path)
		return
	file.store_string(JSON.stringify({
		"avg_ms": avg_ms,
		"avg_fps": avg_fps,
		"measured_frames": measured_frames,
	}, "\t"))
	file.close()
	print("FastWater benchmark gate metrics saved: " + global_path)


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
	print("FastWater benchmark gate wake debug saved: " + global_path)


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
