extends SceneTree
# Quantifies the hero-water planar-reflection throttle (Phase 4 GPU efficiency).
# Sets up a hero surface + planar reflection over reflectable geometry and measures
# real GPU frame time with the reflection rendered every frame vs throttled to 30 Hz.
# Run windowed:
#   Godot --path <proj> --rendering-driver d3d12 \
#     --script res://addons/fast_water/tools/benchmark_fast_water_hero_reflection.gd

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PLANAR_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_planar_reflection.gd")

const WARMUP := 60
const SAMPLE := 150


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_hero_reflection_benchmark.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)
	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50.0, 30.0, 0.0)
	scene.add_child(light)

	# Reflectable geometry so the reflection render does real work.
	for i in range(24):
		var box := MeshInstance3D.new()
		box.mesh = BoxMesh.new()
		box.position = Vector3(float(i % 6) * 4.0 - 10.0, 1.5, float(i / 6) * 4.0 - 6.0)
		box.scale = Vector3(1.0, 3.0, 1.0)
		scene.add_child(box)

	var camera := Camera3D.new()
	scene.add_child(camera)
	camera.position = Vector3(0.0, 2.0, 14.0)
	camera.rotation_degrees = Vector3(-8.0, 0.0, 0.0)

	var water := SURFACE_SCRIPT.new()
	water.set("follow_camera", false)
	water.set("mesh_size_m", 120.0)
	water.set("auto_create_wake_map", false)
	water.set("auto_create_bubbles", false)
	water.set("planar_reflection_max_mix", 0.5)
	scene.add_child(water)

	var planar := PLANAR_SCRIPT.new()
	# planar resolves the water via the fast_water_surface group; no path needed.
	planar.set("target_camera", camera)
	planar.set("reflection_strength", 0.4)
	planar.set("resolution_scale", 1.0)
	scene.add_child(planar)

	_run.call_deferred(output_path, planar)


func _run(output_path: String, planar: Node) -> void:
	# Warm up so the reflection SubViewport exists, then measure the main viewport and
	# the reflection SubViewport (the reflection is a full extra scene render with its
	# own RID/cost). update_hz=0 so the reflection renders every frame and we get its
	# true PER-RENDER cost. Throttling to N Hz amortizes that cost by N/fps -- per-frame
	# GPU APIs report the last render's value on skipped frames, so amortization is shown
	# via the render-skip fraction (see check_fast_water_reflection_throttle_contract.gd),
	# not measured here.
	planar.set("update_hz", 0.0)
	for _i in range(WARMUP):
		await process_frame
	var main_rid := root.get_viewport().get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(main_rid, true)
	var refl_vp := planar.get_node_or_null("ReflectionViewport") as SubViewport
	if refl_vp != null:
		RenderingServer.viewport_set_measure_render_time(refl_vp.get_viewport_rid(), true)

	var main_ms := 0.0
	var refl_ms := 0.0
	for _i in range(SAMPLE):
		await process_frame
		main_ms += RenderingServer.viewport_get_measured_render_time_gpu(main_rid)
		if refl_vp != null:
			refl_ms += RenderingServer.viewport_get_measured_render_time_gpu(refl_vp.get_viewport_rid())
	main_ms /= float(SAMPLE)
	refl_ms /= float(SAMPLE)

	# Worked example: at 120 fps a 30 Hz reflection renders 1/4 as often.
	var amortized_30hz_at_120 := refl_ms * 0.25
	var report := {
		"gpu_ms_main_viewport": main_ms,
		"gpu_ms_reflection_per_render": refl_ms,
		"reflection_share_of_main_pct": (refl_ms / main_ms * 100.0) if main_ms > 0.0001 else 0.0,
		"amortized_reflection_ms_30hz_at_120fps": amortized_30hz_at_120,
		"reflection_render_reduction_30hz_at_120fps_pct": 75.0,
		"sample_frames": SAMPLE,
		"note": "Throttle skips full-scene reflection renders; amortized cost = per_render * update_hz/fps.",
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var f := FileAccess.open(g, FileAccess.WRITE)
	f.store_string(JSON.stringify(report, "\t"))
	f.close()
	print("FastWater hero-reflection: main=%.3fms reflection/render=%.3fms (%.0f%% of main); 30Hz@120fps amortizes to %.3fms" % [main_ms, refl_ms, report["reflection_share_of_main_pct"], amortized_30hz_at_120])
	quit(0)
