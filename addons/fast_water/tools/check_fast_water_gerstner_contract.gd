extends SceneTree
# Gates the optional Gerstner displacement mode and its height-query inversion:
#  - with Gerstner enabled but choppiness 0, the height query is identical to the sine
#    stack (inversion degrades exactly to the proven base case),
#  - with choppiness > 0 the surface differs (displacement is active) yet the height
#    stays finite and bounded (the fixed-point inversion converges, no blow-up),
#  - the shader compiles and renders with Gerstner on (separate demo render).

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_gerstner_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)

	var off := _make_surface(scene, false, 0.0)
	var on_zero := _make_surface(scene, true, 0.0)
	var on_chop := _make_surface(scene, true, 0.7)

	for _i in range(2):
		await process_frame

	var pts := [Vector2(0, 0), Vector2(7.3, -4.1), Vector2(-12.0, 9.5), Vector2(31.2, 18.7)]
	var max_off_vs_onzero := 0.0
	var max_diff_chop := 0.0
	var max_abs := 0.0
	var all_finite := true
	var amp_bound := 0.6 * (0.82 + 0.40 + 0.22 + 0.11 + 0.055) * 1.5
	for pt in pts:
		var h_off := float(off.call("sample_wave_height", pt.x, pt.y))
		var h_on0 := float(on_zero.call("sample_wave_height", pt.x, pt.y))
		var h_chop := float(on_chop.call("sample_wave_height", pt.x, pt.y))
		all_finite = all_finite and is_finite(h_off) and is_finite(h_on0) and is_finite(h_chop)
		max_off_vs_onzero = maxf(max_off_vs_onzero, absf(h_off - h_on0))
		max_diff_chop = maxf(max_diff_chop, absf(h_chop - h_off))
		max_abs = maxf(max_abs, absf(h_chop))

	var checks := {
		"choppiness_zero_matches_sine": max_off_vs_onzero < 0.02,
		"choppiness_changes_surface": max_diff_chop > 0.001,
		"height_stays_finite": all_finite,
		"height_stays_bounded": max_abs < amp_bound,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"max_off_vs_onzero": max_off_vs_onzero,
			"max_diff_chop": max_diff_chop,
			"max_abs_height": max_abs,
			"amp_bound": amp_bound,
		},
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var file := FileAccess.open(g, FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater gerstner contract saved: " + g)
	quit(0 if passed else 1)


func _make_surface(parent: Node, gerstner: bool, chop: float) -> Node:
	var s := SURFACE_SCRIPT.new()
	s.set("auto_create_mesh", false)
	s.set("auto_create_wake_map", false)
	s.set("auto_create_bubbles", false)
	s.set("follow_camera", false)
	s.set("wave_height", 0.6)
	s.set("gerstner_enabled", gerstner)
	s.set("gerstner_choppiness", chop)
	parent.add_child(s)
	return s
