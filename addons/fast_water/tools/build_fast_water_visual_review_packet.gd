extends SceneTree

const DEFAULT_CONTACT_SHEET := "res://artifacts/fast_water_visual_review_contact_sheet.png"
const DEFAULT_MANIFEST := "res://artifacts/fast_water_visual_review_manifest.json"
const TILE_WIDTH := 480
const TILE_HEIGHT := 300
const TILE_BAR_HEIGHT := 28
const TILE_GAP := 16
const SHEET_MARGIN := 24
const COLUMNS := 4

const REVIEW_IMAGES := [
	{"label": "hero_reference", "category": "hero", "path": "res://artifacts/fast_water_hero_reference.png"},
	{"label": "hero_depth_debug", "category": "hero", "path": "res://artifacts/fast_water_hero_reference_depth.png"},
	{"label": "hero_optics_debug", "category": "hero", "path": "res://artifacts/fast_water_hero_reference_optics.png"},
	{"label": "full_proof_scene", "category": "proof", "path": "res://artifacts/fast_water_full_proof.png"},
	{"label": "benchmark", "category": "baseline", "path": "res://artifacts/fast_water_benchmark.png"},
	{"label": "body_debug_overlay", "category": "debug", "path": "res://artifacts/fast_water_body_debug_overlay.png"},
	{"label": "debug_depth", "category": "debug", "path": "res://artifacts/fast_water_debug_depth.png"},
	{"label": "debug_foam", "category": "debug", "path": "res://artifacts/fast_water_debug_foam.png"},
	{"label": "debug_wake", "category": "debug", "path": "res://artifacts/fast_water_debug_wake.png"},
	{"label": "debug_normals", "category": "debug", "path": "res://artifacts/fast_water_debug_normals.png"},
	{"label": "debug_flow", "category": "debug", "path": "res://artifacts/fast_water_debug_flow.png"},
	{"label": "debug_reflection", "category": "debug", "path": "res://artifacts/fast_water_debug_reflection.png"},
	{"label": "debug_optics", "category": "debug", "path": "res://artifacts/fast_water_debug_optics.png"},
	{"label": "lake_demo", "category": "demo", "path": "res://artifacts/fast_water_demo.png"},
	{"label": "river_demo", "category": "river", "path": "res://artifacts/fast_water_river_demo.png"},
	{"label": "river_flow_debug", "category": "river", "path": "res://artifacts/fast_water_river_debug_flow.png"},
	{"label": "rain_demo", "category": "weather", "path": "res://artifacts/fast_water_rain_demo.png"},
	{"label": "weather_sequence", "category": "weather", "path": "res://artifacts/fast_water_weather_sequence_demo.png"},
	{"label": "underwater_demo", "category": "underwater", "path": "res://artifacts/fast_water_underwater_demo.png"},
	{"label": "underwater_turbid", "category": "underwater", "path": "res://artifacts/fast_water_underwater_turbid_demo.png"},
	{"label": "waterfall_demo", "category": "waterfall", "path": "res://artifacts/fast_water_waterfall_demo.png"},
	{"label": "ocean_demo", "category": "ocean", "path": "res://artifacts/fast_water_ocean_demo.png"},
]

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
	var contact_sheet_path := DEFAULT_CONTACT_SHEET
	var manifest_path := DEFAULT_MANIFEST
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--contact-sheet="):
			contact_sheet_path = arg.trim_prefix("--contact-sheet=")
		elif arg.begins_with("--manifest="):
			manifest_path = arg.trim_prefix("--manifest=")

	var rows := ceili(float(REVIEW_IMAGES.size()) / float(COLUMNS))
	var sheet_width := SHEET_MARGIN * 2 + COLUMNS * TILE_WIDTH + (COLUMNS - 1) * TILE_GAP
	var sheet_height := SHEET_MARGIN * 2 + rows * TILE_HEIGHT + (rows - 1) * TILE_GAP
	var sheet := Image.create(sheet_width, sheet_height, false, Image.FORMAT_RGBA8)
	sheet.fill(Color(0.035, 0.045, 0.052, 1.0))

	var entries: Array[Dictionary] = []
	var checks := {
		"all_images_exist": true,
		"all_images_load": true,
		"all_images_have_pixels": true,
		"contact_sheet_saved": false,
		"manifest_saved": false,
	}

	for index in range(REVIEW_IMAGES.size()):
		var item := REVIEW_IMAGES[index] as Dictionary
		var path := str(item.get("path", ""))
		var label := str(item.get("label", ""))
		var category := str(item.get("category", ""))
		var column := index % COLUMNS
		var row := int(index / COLUMNS)
		var tile_x := SHEET_MARGIN + column * (TILE_WIDTH + TILE_GAP)
		var tile_y := SHEET_MARGIN + row * (TILE_HEIGHT + TILE_GAP)
		var result := _place_tile(sheet, index + 1, tile_x, tile_y, label, category, path)
		entries.append(result)
		checks["all_images_exist"] = bool(checks["all_images_exist"]) and bool(result.get("exists", false))
		checks["all_images_load"] = bool(checks["all_images_load"]) and bool(result.get("loaded", false))
		checks["all_images_have_pixels"] = bool(checks["all_images_have_pixels"]) and int(result.get("width", 0)) > 0 and int(result.get("height", 0)) > 0

	checks["contact_sheet_saved"] = _save_image(sheet, contact_sheet_path) == OK
	var manifest := {
		"passed": false,
		"contact_sheet_path": ProjectSettings.globalize_path(contact_sheet_path),
		"entry_count": entries.size(),
		"columns": COLUMNS,
		"tile_size": [TILE_WIDTH, TILE_HEIGHT],
		"checks": checks,
		"entries": entries,
		"manual_acceptance_note": "Use this contact sheet to inspect the current Fast Water visual packet before marking the accepted-screenshot checklist item complete.",
	}
	manifest["passed"] = _all_values_true(checks)
	checks["manifest_saved"] = _save_manifest(manifest, manifest_path) == OK
	manifest["checks"] = checks
	manifest["passed"] = _all_values_true(checks)
	if bool(checks["manifest_saved"]):
		_save_manifest(manifest, manifest_path)
	print("FastWater visual review packet saved: " + ProjectSettings.globalize_path(contact_sheet_path))
	quit(0 if bool(manifest["passed"]) else 1)


func _place_tile(sheet: Image, index: int, tile_x: int, tile_y: int, label: String, category: String, path: String) -> Dictionary:
	var background := Color(0.055, 0.064, 0.073, 1.0)
	var border := _category_color(category)
	_fill_rect(sheet, tile_x, tile_y, TILE_WIDTH, TILE_HEIGHT, background)
	_fill_rect(sheet, tile_x, tile_y, TILE_WIDTH, TILE_BAR_HEIGHT, border)
	_draw_number(sheet, index, tile_x + 10, tile_y + 5, Color(0.98, 1.0, 1.0, 1.0))

	var global_path := ProjectSettings.globalize_path(path)
	var exists := FileAccess.file_exists(path)
	var image := Image.new()
	var loaded := false
	var load_error := ERR_DOES_NOT_EXIST
	if exists:
		load_error = image.load(global_path)
		loaded = load_error == OK

	var result := {
		"index": index,
		"label": label,
		"category": category,
		"path": global_path,
		"exists": exists,
		"loaded": loaded,
		"load_error": load_error,
		"file_bytes": _file_size(global_path),
		"tile": [tile_x, tile_y, TILE_WIDTH, TILE_HEIGHT],
		"width": 0,
		"height": 0,
		"avg_luma": 0.0,
		"avg_saturation": 0.0,
	}
	if not loaded:
		return result

	image.convert(Image.FORMAT_RGBA8)
	result["width"] = image.get_width()
	result["height"] = image.get_height()
	var stats := _image_stats(image)
	result["avg_luma"] = stats.get("avg_luma", 0.0)
	result["avg_saturation"] = stats.get("avg_saturation", 0.0)
	var source := image.duplicate()
	var max_w := TILE_WIDTH - 24
	var max_h := TILE_HEIGHT - TILE_BAR_HEIGHT - 24
	var scale: float = minf(float(max_w) / float(source.get_width()), float(max_h) / float(source.get_height()))
	var target_w := maxi(1, int(round(float(source.get_width()) * scale)))
	var target_h := maxi(1, int(round(float(source.get_height()) * scale)))
	source.resize(target_w, target_h, Image.INTERPOLATE_LANCZOS)
	var dest_x := tile_x + int((TILE_WIDTH - target_w) / 2)
	var dest_y := tile_y + TILE_BAR_HEIGHT + int((TILE_HEIGHT - TILE_BAR_HEIGHT - target_h) / 2)
	sheet.blit_rect(source, Rect2i(Vector2i.ZERO, Vector2i(target_w, target_h)), Vector2i(dest_x, dest_y))
	_draw_border(sheet, tile_x, tile_y, TILE_WIDTH, TILE_HEIGHT, border)
	return result


func _save_image(image: Image, path: String) -> int:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	return image.save_png(global_path)


func _save_manifest(manifest: Dictionary, path: String) -> int:
	var global_path := ProjectSettings.globalize_path(path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		return ERR_CANT_CREATE
	file.store_string(JSON.stringify(manifest, "\t"))
	file.close()
	return OK


func _image_stats(image: Image) -> Dictionary:
	var step := maxi(int(max(image.get_width(), image.get_height()) / 256), 1)
	var count := 0
	var luma_total := 0.0
	var saturation_total := 0.0
	for y in range(0, image.get_height(), step):
		for x in range(0, image.get_width(), step):
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


func _category_color(category: String) -> Color:
	match category:
		"hero":
			return Color(0.18, 0.62, 0.95, 1.0)
		"debug":
			return Color(0.76, 0.54, 0.98, 1.0)
		"river":
			return Color(0.22, 0.76, 0.58, 1.0)
		"weather":
			return Color(0.62, 0.75, 0.92, 1.0)
		"underwater":
			return Color(0.08, 0.70, 0.82, 1.0)
		"waterfall":
			return Color(0.80, 0.88, 0.95, 1.0)
		"ocean":
			return Color(0.12, 0.38, 0.90, 1.0)
		_:
			return Color(0.72, 0.82, 0.92, 1.0)


func _file_size(global_path: String) -> int:
	var file := FileAccess.open(global_path, FileAccess.READ)
	if file == null:
		return 0
	var size := file.get_length()
	file.close()
	return size


func _luma(color: Color) -> float:
	return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722


func _saturation(color: Color) -> float:
	var max_c: float = maxf(color.r, maxf(color.g, color.b))
	var min_c: float = minf(color.r, minf(color.g, color.b))
	if max_c <= 0.0001:
		return 0.0
	return (max_c - min_c) / max_c


func _all_values_true(values: Dictionary) -> bool:
	for value in values.values():
		if not bool(value):
			return false
	return true
