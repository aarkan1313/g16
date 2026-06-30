extends SceneTree
# Proves the hero planar-reflection throttle (Phase 4 GPU efficiency) deterministically:
# driving _process with a fixed delta for one simulated second, update_hz=0 re-renders
# the reflection every frame, while update_hz=30 re-renders ~30x -- i.e. it skips ~75%
# of the full-scene reflection renders at 120 fps. (Per-frame GPU timing APIs report the
# last render's cost on skipped frames, so a behavioural count is the honest proof.)

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PLANAR_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_planar_reflection.gd")

const FRAMES := 120
const DT := 1.0 / 120.0


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_reflection_throttle_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)
	var cam := Camera3D.new()
	scene.add_child(cam)
	cam.position = Vector3(0, 2, 12)
	var water := SURFACE_SCRIPT.new()
	water.set("follow_camera", false)
	water.set("auto_create_wake_map", false)
	water.set("auto_create_bubbles", false)
	scene.add_child(water)
	var planar := PLANAR_SCRIPT.new()
	planar.set("target_camera", cam)
	scene.add_child(planar)

	for _i in range(4):
		await process_frame

	var rv := planar.get_node_or_null("ReflectionViewport") as SubViewport
	var every := _count_renders(planar, rv, 0.0)
	var throttled := _count_renders(planar, rv, 30.0)

	var checks := {
		"reflection_viewport_exists": rv != null,
		"every_frame_renders_all": every >= FRAMES - 1,
		"throttled_30hz_renders_about_30": throttled >= 25 and throttled <= 35,
		"throttle_skips_majority": throttled < every / 2,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"renders_every_frame": every,
			"renders_throttled_30hz": throttled,
			"simulated_frames": FRAMES,
			"skipped_fraction": 1.0 - (float(throttled) / float(maxi(every, 1))),
		},
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var file := FileAccess.open(g, FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater reflection throttle contract saved: " + g)
	quit(0 if passed else 1)


# Drives planar._process with a fixed delta and counts frames where it (re)armed a
# reflection render. Pre-set DISABLED each frame so a render this frame is detectable
# as UPDATE_ONCE (throttled) or UPDATE_ALWAYS (every-frame).
func _count_renders(planar: Node, rv: SubViewport, hz: float) -> int:
	planar.set("update_hz", hz)
	var renders := 0
	for _i in range(FRAMES):
		if rv != null:
			rv.render_target_update_mode = SubViewport.UPDATE_DISABLED
		planar._process(DT)
		if rv != null and rv.render_target_update_mode != SubViewport.UPDATE_DISABLED:
			renders += 1
	return renders
