extends SceneTree

const FULL_PROOF_SCENE := "res://addons/fast_water/demo/fast_water_full_proof.tscn"


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_full_proof.png"
	var metrics_path := "res://artifacts/fast_water_full_proof_metrics.json"
	var frames := 210

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--metrics="):
			metrics_path = arg.trim_prefix("--metrics=")
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))

	var packed := load(FULL_PROOF_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater full proof render gate could not load " + FULL_PROOF_SCENE)
		quit(1)
		return

	var proof := packed.instantiate()
	if _has_property(proof, "gate_external_capture"):
		proof.set("gate_external_capture", true)
	root.add_child(proof)
	print("FastWater full proof render gate loaded scene")
	_capture.call_deferred(screenshot_path, metrics_path, frames)


func _capture(screenshot_path: String, metrics_path: String, frames: int) -> void:
	for _i in range(frames):
		await process_frame
	print("FastWater full proof render gate captured frames: %d" % frames)

	if _find_named(root, "FullProofInletRiver") == null or _find_named(root, "FastWaterWaterfall") == null:
		push_error("FastWater full proof render gate did not find required combined-scene modules")
		quit(1)
		return

	var image := root.get_texture().get_image()
	print("FastWater full proof render gate read viewport image: %s" % str(image != null))
	if image == null:
		push_error("FastWater full proof render gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater full proof render gate failed to save " + global_path)
		quit(1)
		return

	var passed := _save_metrics(metrics_path, image, screenshot_path)
	print("FastWater full proof render gate screenshot saved: " + global_path)
	quit(0 if passed else 1)


func _save_metrics(path: String, image: Image, screenshot_path: String) -> bool:
	var metrics := _calculate_visual_metrics(image)
	metrics["screenshot_path"] = ProjectSettings.globalize_path(screenshot_path)
	metrics["thresholds"] = {
		"avg_luma_min": 0.05,
		"avg_luma_max": 0.90,
		"contrast_p95_p05_min": 0.10,
		"avg_saturation_min": 0.025,
	}
	metrics["passed"] = (
		float(metrics.get("avg_luma", 0.0)) >= 0.05
		and float(metrics.get("avg_luma", 0.0)) <= 0.90
		and float(metrics.get("contrast_p95_p05", 0.0)) >= 0.10
		and float(metrics.get("avg_saturation", 0.0)) >= 0.025
	)

	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater full proof render gate failed to save metrics " + global_path)
		return false
	file.store_string(JSON.stringify(metrics, "\t"))
	file.close()
	print("FastWater full proof render gate metrics saved: " + global_path)
	if not bool(metrics["passed"]):
		push_error("FastWater full proof render gate visual metrics failed")
	return bool(metrics["passed"])


func _calculate_visual_metrics(image: Image) -> Dictionary:
	var width := image.get_width()
	var height := image.get_height()
	var step := maxi(int(max(width, height) / 320), 1)
	var lumas: Array[float] = []
	var luma_total := 0.0
	var saturation_total := 0.0
	var count := 0
	for y in range(0, height, step):
		for x in range(0, width, step):
			var c := image.get_pixel(x, y)
			var luma := _luma(c)
			lumas.append(luma)
			luma_total += luma
			saturation_total += _saturation(c)
			count += 1
	lumas.sort()
	var safe_count := maxi(count, 1)
	var p05 := _percentile(lumas, 0.05)
	var p95 := _percentile(lumas, 0.95)
	return {
		"width": width,
		"height": height,
		"sample_step": step,
		"sample_count": count,
		"avg_luma": luma_total / float(safe_count),
		"avg_saturation": saturation_total / float(safe_count),
		"p05_luma": p05,
		"p95_luma": p95,
		"contrast_p95_p05": p95 - p05,
	}


func _luma(c: Color) -> float:
	return c.r * 0.299 + c.g * 0.587 + c.b * 0.114


func _saturation(c: Color) -> float:
	var max_c: float = max(c.r, max(c.g, c.b))
	var min_c: float = min(c.r, min(c.g, c.b))
	if max_c <= 0.0001:
		return 0.0
	return (max_c - min_c) / max_c


func _percentile(values: Array[float], amount: float) -> float:
	if values.is_empty():
		return 0.0
	var index := clampi(roundi(float(values.size() - 1) * clampf(amount, 0.0, 1.0)), 0, values.size() - 1)
	return values[index]


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


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
