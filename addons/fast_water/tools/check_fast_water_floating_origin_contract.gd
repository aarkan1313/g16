extends SceneTree
# Verifies the floating-origin wave_sample_offset path:
#  - default offset is zero (unchanged behaviour),
#  - set_wave_sample_offset drives BOTH ocean tiers identically (no macro-swell seam),
#  - the offset is pushed to the tier shader material,
#  - the height query honours the offset as a pure sampling-frame translation
#    (sample at P with offset O == sample at P-O with offset 0), which is what keeps
#    the fp32 shader and the height query agreeing on small coordinates far out.

const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_floating_origin_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)
	var camera := Camera3D.new()
	scene.add_child(camera)
	camera.position = Vector3(0.0, 8.0, 0.0)

	var ocean := OCEAN_SCRIPT.new()
	ocean.target_camera = camera
	ocean.ocean_profile = OCEAN_PROFILE_SCRIPT.open_world()
	scene.add_child(ocean)

	for _i in range(8):
		await process_frame

	var surfaces: Array = ocean.call("get_surface_nodes")
	var near: Node = surfaces[0] if surfaces.size() > 0 else null
	var far: Node = surfaces[1] if surfaces.size() > 1 else null

	var default_zero := near != null and far != null \
		and (near.get("wave_sample_offset_m") as Vector2) == Vector2.ZERO \
		and (far.get("wave_sample_offset_m") as Vector2) == Vector2.ZERO

	var offset := Vector2(5_000_000.0, 5_000_000.0)
	ocean.call("set_wave_sample_offset", offset)
	for _i in range(2):
		await process_frame

	var near_off: Vector2 = near.get("wave_sample_offset_m") if near != null else Vector2.INF
	var far_off: Vector2 = far.get("wave_sample_offset_m") if far != null else Vector2.INF
	var propagated_equally := near_off == offset and far_off == offset

	var near_mat := near.get("water_material") as ShaderMaterial if near != null else null
	var mat_offset = near_mat.get_shader_parameter("wave_sample_offset") if near_mat != null else null
	var shader_param_set := mat_offset != null and (mat_offset as Vector2) == offset

	# Offset is a pure translation of the sampling frame: height at P with offset O
	# must equal height at (P - O) with offset 0. Use two short-lived surfaces.
	var probe := Vector2(5_000_010.0, 5_000_007.0)
	var s_off := SURFACE_SCRIPT.new()
	s_off.set("wave_sample_offset_m", offset)
	s_off.set("wave_height", 0.4)
	scene.add_child(s_off)
	var s_zero := SURFACE_SCRIPT.new()
	s_zero.set("wave_sample_offset_m", Vector2.ZERO)
	s_zero.set("wave_height", 0.4)
	scene.add_child(s_zero)
	await process_frame
	var h_offset := float(s_off.call("sample_wave_height", probe.x, probe.y))
	var h_translated := float(s_zero.call("sample_wave_height", probe.x - offset.x, probe.y - offset.y))
	var translation_consistent := absf(h_offset - h_translated) <= 0.02

	# The translated sample uses small coordinates (10, 7) -- a meaningful, non-flat
	# wave value -- proving the offset keeps the sampled coordinate small far out.
	var nonflat := absf(h_translated) > 0.0001

	var checks := {
		"default_offset_zero_on_both_tiers": default_zero,
		"offset_propagates_equally_to_both_tiers": propagated_equally,
		"offset_pushed_to_tier_shader_material": shader_param_set,
		"offset_is_pure_frame_translation": translation_consistent,
		"translated_sample_is_nonflat_small_coord": nonflat,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"near_offset": [near_off.x, near_off.y],
			"far_offset": [far_off.x, far_off.y],
			"h_offset": h_offset,
			"h_translated": h_translated,
		},
	}
	var global_path := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(global_path.get_base_dir())
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("floating origin contract could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater floating origin contract saved: " + global_path)
	quit(0 if passed else 1)
