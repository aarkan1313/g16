extends SceneTree

const HERO_SCENE := "res://addons/fast_water/demo/fast_water_hero_reference.tscn"


func _initialize() -> void:
	var screenshot_path := "res://artifacts/fast_water_hero_reference.png"
	var metrics_path := ""
	var reference_path := ""
	var reference_check := false
	var reference_thresholds := {
		"avg_rgb_delta_max": 0.06,
		"avg_luma_delta_max": 0.05,
		"max_luma_delta_max": 0.30,
	}
	var frames := 210
	var debug_view := 0

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--screenshot="):
			screenshot_path = arg.trim_prefix("--screenshot=")
		elif arg.begins_with("--metrics="):
			metrics_path = arg.trim_prefix("--metrics=")
		elif arg.begins_with("--reference="):
			reference_path = arg.trim_prefix("--reference=")
		elif arg == "--reference-check":
			reference_check = true
		elif arg.begins_with("--reference-max-avg-rgb-delta="):
			reference_thresholds["avg_rgb_delta_max"] = maxf(0.0, float(arg.trim_prefix("--reference-max-avg-rgb-delta=")))
		elif arg.begins_with("--reference-max-avg-luma-delta="):
			reference_thresholds["avg_luma_delta_max"] = maxf(0.0, float(arg.trim_prefix("--reference-max-avg-luma-delta=")))
		elif arg.begins_with("--reference-max-luma-delta="):
			reference_thresholds["max_luma_delta_max"] = maxf(0.0, float(arg.trim_prefix("--reference-max-luma-delta=")))
		elif arg.begins_with("--frames="):
			frames = max(1, int(arg.trim_prefix("--frames=")))
		elif arg.begins_with("--debug-view="):
			debug_view = clampi(int(arg.trim_prefix("--debug-view=")), 0, 7)

	var packed := load(HERO_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater hero render gate could not load " + HERO_SCENE)
		quit(1)
		return

	var hero := packed.instantiate()
	var script := hero.get_script() as Script
	if script == null or not script.resource_path.ends_with("/fast_water_hero_reference.gd"):
		push_error("FastWater hero render gate loaded scene without the expected hero script")
		quit(1)
		return
	if _has_property(hero, "gate_external_capture"):
		hero.set("gate_external_capture", true)
	if _has_property(hero, "external_debug_view"):
		hero.set("external_debug_view", debug_view)
	root.add_child(hero)
	print("FastWater hero render gate loaded reference scene")
	_capture.call_deferred(screenshot_path, metrics_path, reference_path, reference_check, reference_thresholds, frames, debug_view)


func _capture(
	screenshot_path: String,
	metrics_path: String,
	reference_path: String,
	reference_check: bool,
	reference_thresholds: Dictionary,
	frames: int,
	debug_view: int
) -> void:
	for _i in range(frames):
		await process_frame

	var image := root.get_texture().get_image()
	if image == null:
		push_error("FastWater hero render gate could not read root viewport")
		quit(1)
		return

	var global_path := ProjectSettings.globalize_path(screenshot_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)

	var err := image.save_png(global_path)
	if err != OK:
		push_error("FastWater hero render gate failed to save " + global_path)
		quit(1)
		return

	print("FastWater hero render gate screenshot saved: " + global_path)
	var passed := true
	if metrics_path != "":
		passed = _save_visual_metrics(metrics_path, image, screenshot_path, reference_path, reference_check, reference_thresholds, debug_view)
	quit(0 if passed else 1)


func _save_visual_metrics(
	path: String,
	image: Image,
	screenshot_path: String,
	reference_path: String,
	reference_check: bool,
	reference_thresholds: Dictionary,
	debug_view: int
) -> bool:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var metrics := _calculate_visual_metrics(image)
	metrics["screenshot_path"] = ProjectSettings.globalize_path(screenshot_path)
	metrics["debug_view"] = debug_view
	metrics["reference_check_required"] = reference_check
	metrics["reference_thresholds"] = reference_thresholds
	if reference_path != "":
		metrics["reference_path"] = ProjectSettings.globalize_path(reference_path)
		metrics["reference_comparison"] = _compare_to_reference(image, reference_path)
	elif reference_check:
		metrics["reference_comparison"] = {
			"available": false,
			"missing_reference_path": true,
		}
	metrics["base_passed"] = _base_metrics_pass(metrics)
	metrics["reference_passed"] = (not reference_check) or _reference_comparison_pass(
		metrics.get("reference_comparison", {}) as Dictionary,
		reference_thresholds
	)
	metrics["passed"] = bool(metrics["base_passed"]) and bool(metrics["reference_passed"])
	metrics["thresholds"] = {
		"nonblank_luma_min": 0.05,
		"nonblank_luma_max": 0.90,
		"contrast_p95_p05_min": 0.12,
		"water_avg_saturation_min": 0.035,
		"sky_water_luma_delta_min": 0.025,
		"highlight_ratio_min": 0.001,
	}
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater hero render gate failed to save metrics " + global_path)
		return false
	file.store_string(JSON.stringify(metrics, "\t"))
	file.close()
	print("FastWater hero render gate visual metrics saved: " + global_path)
	if not bool(metrics["passed"]):
		push_error("FastWater hero render gate visual metrics failed")
	return bool(metrics["passed"])


func _calculate_visual_metrics(image: Image) -> Dictionary:
	var width := image.get_width()
	var height := image.get_height()
	var step := maxi(int(max(width, height) / 320), 1)
	var all := _sample_region(image, 0, 0, width, height, step)
	var sky := _sample_region(image, 0, 0, width, maxi(int(height * 0.26), 1), step)
	var water := _sample_region(
		image,
		int(width * 0.04),
		int(height * 0.42),
		int(width * 0.92),
		int(height * 0.46),
		step
	)
	return {
		"width": width,
		"height": height,
		"sample_step": step,
		"overall": all,
		"sky_region": sky,
		"water_region": water,
		"sky_water_luma_delta": float(sky.get("avg_luma", 0.0)) - float(water.get("avg_luma", 0.0)),
		"contrast_p95_p05": float(all.get("p95_luma", 0.0)) - float(all.get("p05_luma", 0.0)),
	}


func _sample_region(image: Image, x0: int, y0: int, w: int, h: int, step: int) -> Dictionary:
	var width := image.get_width()
	var height := image.get_height()
	var min_x := clampi(x0, 0, width - 1)
	var min_y := clampi(y0, 0, height - 1)
	var max_x := clampi(x0 + maxi(w, 1), min_x + 1, width)
	var max_y := clampi(y0 + maxi(h, 1), min_y + 1, height)
	var lumas: Array[float] = []
	var luma_total := 0.0
	var saturation_total := 0.0
	var highlight_count := 0
	var dark_count := 0
	var count := 0
	for y in range(min_y, max_y, step):
		for x in range(min_x, max_x, step):
			var c := image.get_pixel(x, y)
			var luma := _luma(c)
			var saturation := _saturation(c)
			lumas.append(luma)
			luma_total += luma
			saturation_total += saturation
			if luma >= 0.78:
				highlight_count += 1
			if luma <= 0.18:
				dark_count += 1
			count += 1
	lumas.sort()
	var safe_count: int = maxi(count, 1)
	return {
		"sample_count": count,
		"avg_luma": luma_total / float(safe_count),
		"avg_saturation": saturation_total / float(safe_count),
		"p05_luma": _percentile(lumas, 0.05),
		"p50_luma": _percentile(lumas, 0.50),
		"p95_luma": _percentile(lumas, 0.95),
		"highlight_ratio": float(highlight_count) / float(safe_count),
		"dark_ratio": float(dark_count) / float(safe_count),
	}


func _base_metrics_pass(metrics: Dictionary) -> bool:
	var overall := metrics.get("overall", {}) as Dictionary
	var water := metrics.get("water_region", {}) as Dictionary
	var avg_luma := float(overall.get("avg_luma", 0.0))
	return (
		avg_luma >= 0.05
		and avg_luma <= 0.90
		and float(metrics.get("contrast_p95_p05", 0.0)) >= 0.12
		and float(water.get("avg_saturation", 0.0)) >= 0.035
		and float(metrics.get("sky_water_luma_delta", 0.0)) >= 0.025
		and float(water.get("highlight_ratio", 0.0)) >= 0.001
	)


func _reference_comparison_pass(comparison: Dictionary, thresholds: Dictionary) -> bool:
	if not bool(comparison.get("available", false)):
		return false
	return (
		float(comparison.get("avg_rgb_delta", 1.0)) <= float(thresholds.get("avg_rgb_delta_max", 0.06))
		and float(comparison.get("avg_luma_delta", 1.0)) <= float(thresholds.get("avg_luma_delta_max", 0.05))
		and float(comparison.get("max_luma_delta", 1.0)) <= float(thresholds.get("max_luma_delta_max", 0.30))
	)


func _compare_to_reference(image: Image, reference_path: String) -> Dictionary:
	var reference := Image.new()
	var err := reference.load(ProjectSettings.globalize_path(reference_path))
	if err != OK:
		return {
			"available": false,
			"load_error": err,
		}
	if reference.get_width() != image.get_width() or reference.get_height() != image.get_height():
		return {
			"available": false,
			"load_error": OK,
			"dimension_mismatch": true,
			"reference_size": [reference.get_width(), reference.get_height()],
			"image_size": [image.get_width(), image.get_height()],
		}
	reference.convert(Image.FORMAT_RGBA8)
	var width := image.get_width()
	var height := image.get_height()
	var step := maxi(int(max(width, height) / 360), 1)
	var count := 0
	var rgb_delta_total := 0.0
	var luma_delta_total := 0.0
	var max_luma_delta := 0.0
	for y in range(0, height, step):
		for x in range(0, width, step):
			var a := image.get_pixel(x, y)
			var b := reference.get_pixel(x, y)
			var rgb_delta := (absf(a.r - b.r) + absf(a.g - b.g) + absf(a.b - b.b)) / 3.0
			var luma_delta := absf(_luma(a) - _luma(b))
			rgb_delta_total += rgb_delta
			luma_delta_total += luma_delta
			max_luma_delta = maxf(max_luma_delta, luma_delta)
			count += 1
	var safe_count := maxi(count, 1)
	return {
		"available": true,
		"sample_count": count,
		"sample_step": step,
		"avg_rgb_delta": rgb_delta_total / float(safe_count),
		"avg_luma_delta": luma_delta_total / float(safe_count),
		"max_luma_delta": max_luma_delta,
	}


func _luma(color: Color) -> float:
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722


func _saturation(color: Color) -> float:
	var max_c: float = maxf(color.r, maxf(color.g, color.b))
	var min_c: float = minf(color.r, minf(color.g, color.b))
	if max_c <= 0.0001:
		return 0.0
	return (max_c - min_c) / max_c


func _percentile(values: Array[float], amount: float) -> float:
	if values.is_empty():
		return 0.0
	var index := clampi(roundi(float(values.size() - 1) * clampf(amount, 0.0, 1.0)), 0, values.size() - 1)
	return values[index]


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
