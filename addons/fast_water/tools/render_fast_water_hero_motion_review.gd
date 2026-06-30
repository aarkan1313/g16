extends SceneTree

const HERO_SCENE := "res://addons/fast_water/demo/fast_water_hero_reference.tscn"
const DEFAULT_STRIP := "res://artifacts/fast_water_hero_motion_strip.png"
const DEFAULT_METRICS := "res://artifacts/fast_water_hero_motion_metrics.json"
const TILE_WIDTH := 520
const TILE_HEIGHT := 292
const TILE_BAR_HEIGHT := 24
const TILE_GAP := 14
const SHEET_MARGIN := 20
const COLUMNS := 3

const DIGITS := {
	"0": ["111", "101", "101", "101", "111"],
	"1": ["010", "110", "010", "010", "111"],
	"2": ["111", "001", "111", "100", "111"],
	"3": ["111", "001", "111", "001", "111"],
	"4": ["101", "101", "111", "001", "001"],
	"5": ["111", "100", "111", "001", "111"],
	"6": ["111", "100", "111", "101", "111"],
	"7": ["111", "001", "001", "001", "001"],
	"8": ["111", "101", "111", "101", "111"],
	"9": ["111", "101", "111", "001", "111"],
}


func _initialize() -> void:
	var strip_path := DEFAULT_STRIP
	var metrics_path := DEFAULT_METRICS
	var warmup_frames := 60
	var sample_count := 6
	var interval_frames := 45
	var debug_view := 0
	var min_temporal_rgb_delta := 0.00025
	var max_pair_avg_luma_delta := 0.05

	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--strip="):
			strip_path = arg.trim_prefix("--strip=")
		elif arg.begins_with("--metrics="):
			metrics_path = arg.trim_prefix("--metrics=")
		elif arg.begins_with("--warmup="):
			warmup_frames = max(1, int(arg.trim_prefix("--warmup=")))
		elif arg.begins_with("--samples="):
			sample_count = clampi(int(arg.trim_prefix("--samples=")), 3, 12)
		elif arg.begins_with("--interval="):
			interval_frames = max(1, int(arg.trim_prefix("--interval=")))
		elif arg.begins_with("--debug-view="):
			debug_view = clampi(int(arg.trim_prefix("--debug-view=")), 0, 7)
		elif arg.begins_with("--min-temporal-rgb-delta="):
			min_temporal_rgb_delta = maxf(0.0, float(arg.trim_prefix("--min-temporal-rgb-delta=")))
		elif arg.begins_with("--max-pair-avg-luma-delta="):
			max_pair_avg_luma_delta = maxf(0.0, float(arg.trim_prefix("--max-pair-avg-luma-delta=")))
		elif arg.begins_with("--max-temporal-luma-delta="):
			max_pair_avg_luma_delta = maxf(0.0, float(arg.trim_prefix("--max-temporal-luma-delta=")))

	var packed := load(HERO_SCENE) as PackedScene
	if packed == null:
		push_error("FastWater hero motion gate could not load " + HERO_SCENE)
		quit(1)
		return

	var hero := packed.instantiate()
	var script := hero.get_script() as Script
	if script == null or not script.resource_path.ends_with("/fast_water_hero_reference.gd"):
		push_error("FastWater hero motion gate loaded scene without the expected hero script")
		quit(1)
		return
	if _has_property(hero, "gate_external_capture"):
		hero.set("gate_external_capture", true)
	if _has_property(hero, "external_debug_view"):
		hero.set("external_debug_view", debug_view)
	root.add_child(hero)
	print("FastWater hero motion gate loaded reference scene")
	_capture.call_deferred(
		strip_path,
		metrics_path,
		warmup_frames,
		sample_count,
		interval_frames,
		debug_view,
		min_temporal_rgb_delta,
		max_pair_avg_luma_delta
	)


func _capture(
	strip_path: String,
	metrics_path: String,
	warmup_frames: int,
	sample_count: int,
	interval_frames: int,
	debug_view: int,
	min_temporal_rgb_delta: float,
	max_pair_avg_luma_delta: float
) -> void:
	for _i in range(warmup_frames):
		await process_frame

	var samples: Array[Image] = []
	var sample_frames: Array[int] = []
	for sample_index in range(sample_count):
		var image := root.get_texture().get_image()
		if image == null:
			push_error("FastWater hero motion gate could not read root viewport")
			quit(1)
			return
		image.convert(Image.FORMAT_RGBA8)
		samples.append(image.duplicate())
		sample_frames.append(warmup_frames + sample_index * interval_frames)
		for _i in range(interval_frames):
			await process_frame

	var metrics := _build_metrics(
		samples,
		sample_frames,
		debug_view,
		min_temporal_rgb_delta,
		max_pair_avg_luma_delta
	)
	var strip_err := _save_strip(samples, sample_frames, strip_path)
	metrics["strip_path"] = ProjectSettings.globalize_path(strip_path)
	metrics["strip_saved"] = strip_err == OK
	metrics["metrics_path"] = ProjectSettings.globalize_path(metrics_path)
	metrics["passed"] = bool(metrics.get("passed", false)) and strip_err == OK
	var metrics_err := _save_metrics(metrics, metrics_path)
	if metrics_err != OK:
		push_error("FastWater hero motion gate failed to save metrics")
		quit(1)
		return
	print("FastWater hero motion gate saved: " + ProjectSettings.globalize_path(strip_path))
	quit(0 if bool(metrics["passed"]) else 1)


func _build_metrics(
	samples: Array[Image],
	sample_frames: Array[int],
	debug_view: int,
	min_temporal_rgb_delta: float,
	max_pair_avg_luma_delta: float
) -> Dictionary:
	var frame_stats: Array[Dictionary] = []
	var deltas: Array[Dictionary] = []
	for index in range(samples.size()):
		frame_stats.append(_visual_stats(samples[index], sample_frames[index]))
		if index > 0:
			deltas.append(_frame_delta(samples[index - 1], samples[index], sample_frames[index - 1], sample_frames[index]))

	var avg_rgb_delta := 0.0
	var avg_luma_delta := 0.0
	var max_luma_delta := 0.0
	var max_pair_avg_luma_delta_observed := 0.0
	var min_rgb_delta := 1.0
	for delta in deltas:
		avg_rgb_delta += float(delta.get("avg_rgb_delta", 0.0))
		avg_luma_delta += float(delta.get("avg_luma_delta", 0.0))
		max_luma_delta = maxf(max_luma_delta, float(delta.get("max_luma_delta", 0.0)))
		max_pair_avg_luma_delta_observed = maxf(max_pair_avg_luma_delta_observed, float(delta.get("avg_luma_delta", 0.0)))
		min_rgb_delta = minf(min_rgb_delta, float(delta.get("avg_rgb_delta", 1.0)))
	var safe_delta_count := maxi(deltas.size(), 1)
	avg_rgb_delta /= float(safe_delta_count)
	avg_luma_delta /= float(safe_delta_count)

	var base_frames_pass := true
	for stats in frame_stats:
		base_frames_pass = base_frames_pass and bool(stats.get("base_passed", false))
	var temporal_pass := (
		deltas.size() > 0
		and avg_rgb_delta >= min_temporal_rgb_delta
		and max_pair_avg_luma_delta_observed <= max_pair_avg_luma_delta
	)
	return {
		"passed": base_frames_pass and temporal_pass,
		"debug_view": debug_view,
		"sample_count": samples.size(),
		"sample_frames": sample_frames,
		"base_frames_passed": base_frames_pass,
		"temporal_passed": temporal_pass,
		"thresholds": {
			"min_avg_temporal_rgb_delta": min_temporal_rgb_delta,
			"max_pair_avg_luma_delta": max_pair_avg_luma_delta,
			"nonblank_luma_min": 0.05,
			"nonblank_luma_max": 0.90,
			"water_avg_saturation_min": 0.035,
		},
		"temporal_summary": {
			"avg_rgb_delta": avg_rgb_delta,
			"min_pair_avg_rgb_delta": min_rgb_delta,
			"avg_luma_delta": avg_luma_delta,
			"max_pair_avg_luma_delta": max_pair_avg_luma_delta_observed,
			"max_luma_delta": max_luma_delta,
		},
		"frames": frame_stats,
		"pair_deltas": deltas,
	}


func _visual_stats(image: Image, frame_number: int) -> Dictionary:
	var overall := _sample_region(image, 0, 0, image.get_width(), image.get_height())
	var water := _sample_region(
		image,
		int(image.get_width() * 0.04),
		int(image.get_height() * 0.42),
		int(image.get_width() * 0.92),
		int(image.get_height() * 0.46)
	)
	var avg_luma := float(overall.get("avg_luma", 0.0))
	return {
		"frame": frame_number,
		"width": image.get_width(),
		"height": image.get_height(),
		"overall": overall,
		"water_region": water,
		"base_passed": (
			avg_luma >= 0.05
			and avg_luma <= 0.90
			and float(water.get("avg_saturation", 0.0)) >= 0.035
		),
	}


func _sample_region(image: Image, x0: int, y0: int, w: int, h: int) -> Dictionary:
	var step := maxi(int(max(image.get_width(), image.get_height()) / 320), 1)
	var min_x := clampi(x0, 0, image.get_width() - 1)
	var min_y := clampi(y0, 0, image.get_height() - 1)
	var max_x := clampi(x0 + maxi(w, 1), min_x + 1, image.get_width())
	var max_y := clampi(y0 + maxi(h, 1), min_y + 1, image.get_height())
	var count := 0
	var luma_total := 0.0
	var saturation_total := 0.0
	for y in range(min_y, max_y, step):
		for x in range(min_x, max_x, step):
			var color := image.get_pixel(x, y)
			luma_total += _luma(color)
			saturation_total += _saturation(color)
			count += 1
	var safe_count := maxi(count, 1)
	return {
		"sample_count": count,
		"avg_luma": luma_total / float(safe_count),
		"avg_saturation": saturation_total / float(safe_count),
	}


func _frame_delta(previous: Image, current: Image, previous_frame: int, current_frame: int) -> Dictionary:
	var width := mini(previous.get_width(), current.get_width())
	var height := mini(previous.get_height(), current.get_height())
	var step := maxi(int(max(width, height) / 360), 1)
	var count := 0
	var rgb_delta_total := 0.0
	var luma_delta_total := 0.0
	var max_luma_delta := 0.0
	for y in range(0, height, step):
		for x in range(0, width, step):
			var a := previous.get_pixel(x, y)
			var b := current.get_pixel(x, y)
			var rgb_delta := (absf(a.r - b.r) + absf(a.g - b.g) + absf(a.b - b.b)) / 3.0
			var luma_delta := absf(_luma(a) - _luma(b))
			rgb_delta_total += rgb_delta
			luma_delta_total += luma_delta
			max_luma_delta = maxf(max_luma_delta, luma_delta)
			count += 1
	var safe_count := maxi(count, 1)
	return {
		"from_frame": previous_frame,
		"to_frame": current_frame,
		"sample_count": count,
		"sample_step": step,
		"avg_rgb_delta": rgb_delta_total / float(safe_count),
		"avg_luma_delta": luma_delta_total / float(safe_count),
		"max_luma_delta": max_luma_delta,
	}


func _save_strip(samples: Array[Image], sample_frames: Array[int], path: String) -> int:
	var rows := ceili(float(samples.size()) / float(COLUMNS))
	var sheet_width := SHEET_MARGIN * 2 + COLUMNS * TILE_WIDTH + (COLUMNS - 1) * TILE_GAP
	var sheet_height := SHEET_MARGIN * 2 + rows * TILE_HEIGHT + (rows - 1) * TILE_GAP
	var sheet := Image.create(sheet_width, sheet_height, false, Image.FORMAT_RGBA8)
	sheet.fill(Color(0.032, 0.040, 0.047, 1.0))
	for index in range(samples.size()):
		var column := index % COLUMNS
		var row := int(index / COLUMNS)
		var tile_x := SHEET_MARGIN + column * (TILE_WIDTH + TILE_GAP)
		var tile_y := SHEET_MARGIN + row * (TILE_HEIGHT + TILE_GAP)
		_place_tile(sheet, samples[index], index + 1, sample_frames[index], tile_x, tile_y)
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	return sheet.save_png(global_path)


func _place_tile(sheet: Image, source_image: Image, index: int, frame_number: int, tile_x: int, tile_y: int) -> void:
	_fill_rect(sheet, tile_x, tile_y, TILE_WIDTH, TILE_HEIGHT, Color(0.055, 0.064, 0.073, 1.0))
	_fill_rect(sheet, tile_x, tile_y, TILE_WIDTH, TILE_BAR_HEIGHT, Color(0.14, 0.62, 0.95, 1.0))
	_draw_number(sheet, index, tile_x + 10, tile_y + 4, Color(0.98, 1.0, 1.0, 1.0))
	_draw_number(sheet, frame_number, tile_x + 68, tile_y + 4, Color(0.92, 0.98, 1.0, 1.0))
	var source := source_image.duplicate()
	var max_w := TILE_WIDTH - 24
	var max_h := TILE_HEIGHT - TILE_BAR_HEIGHT - 20
	var scale: float = minf(float(max_w) / float(source.get_width()), float(max_h) / float(source.get_height()))
	var target_w := maxi(1, int(round(float(source.get_width()) * scale)))
	var target_h := maxi(1, int(round(float(source.get_height()) * scale)))
	source.resize(target_w, target_h, Image.INTERPOLATE_LANCZOS)
	var dest_x := tile_x + int((TILE_WIDTH - target_w) / 2)
	var dest_y := tile_y + TILE_BAR_HEIGHT + int((TILE_HEIGHT - TILE_BAR_HEIGHT - target_h) / 2)
	sheet.blit_rect(source, Rect2i(Vector2i.ZERO, Vector2i(target_w, target_h)), Vector2i(dest_x, dest_y))
	_draw_border(sheet, tile_x, tile_y, TILE_WIDTH, TILE_HEIGHT, Color(0.14, 0.62, 0.95, 1.0))


func _save_metrics(metrics: Dictionary, path: String) -> int:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		return ERR_CANT_CREATE
	file.store_string(JSON.stringify(metrics, "\t"))
	file.close()
	return OK


func _draw_number(image: Image, number: int, x: int, y: int, color: Color) -> void:
	var text := str(number).pad_zeros(2)
	var cursor := x
	for digit in text:
		_draw_digit(image, str(digit), cursor, y, color)
		cursor += 18


func _draw_digit(image: Image, digit: String, x: int, y: int, color: Color) -> void:
	var pattern: Array = DIGITS.get(digit, []) as Array
	var block := 4
	for row in range(pattern.size()):
		var line := str(pattern[row])
		for column in range(line.length()):
			if line.substr(column, 1) == "1":
				_fill_rect(image, x + column * block, y + row * block, block - 1, block - 1, color)


func _draw_border(image: Image, x: int, y: int, width: int, height: int, color: Color) -> void:
	_fill_rect(image, x, y, width, 2, color)
	_fill_rect(image, x, y + height - 2, width, 2, color)
	_fill_rect(image, x, y, 2, height, color)
	_fill_rect(image, x + width - 2, y, 2, height, color)


func _fill_rect(image: Image, x: int, y: int, width: int, height: int, color: Color) -> void:
	var min_x := clampi(x, 0, image.get_width())
	var min_y := clampi(y, 0, image.get_height())
	var max_x := clampi(x + maxi(width, 0), min_x, image.get_width())
	var max_y := clampi(y + maxi(height, 0), min_y, image.get_height())
	for py in range(min_y, max_y):
		for px in range(min_x, max_x):
			image.set_pixel(px, py, color)


func _luma(color: Color) -> float:
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722


func _saturation(color: Color) -> float:
	var max_c: float = maxf(color.r, maxf(color.g, color.b))
	var min_c: float = minf(color.r, minf(color.g, color.b))
	if max_c <= 0.0001:
		return 0.0
	return (max_c - min_c) / max_c


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
