extends SceneTree
# Quantifies the P1 far-foam-skip win. Fills the view with a single large water
# surface and measures real GPU frame time (D3D12) with foam_detail_enabled on vs
# off -- the only difference is the 3-call foam-noise fbm stack. Run windowed:
#   Godot --path <proj> --rendering-driver d3d12 \
#     --script res://addons/fast_water/tools/benchmark_fast_water_far_foam.gd
# Headless cannot measure GPU time; this is a perf tool, not a headless contract.

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")

const WARMUP := 60
const SAMPLE := 150


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_far_foam_benchmark.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	root.add_child(scene)

	var light := DirectionalLight3D.new()
	light.rotation_degrees = Vector3(-50.0, 30.0, 0.0)
	scene.add_child(light)

	var camera := Camera3D.new()
	camera.far = 8000.0
	scene.add_child(camera)
	# Grazing view so water fills the whole frame (maximises foam fragment work).
	camera.position = Vector3(0.0, 2.0, 0.0)
	camera.rotation_degrees = Vector3(-3.0, 0.0, 0.0)

	var water := SURFACE_SCRIPT.new()
	water.set("follow_camera", false)
	water.set("mesh_size_m", 4000.0)
	water.set("mesh_subdivisions", 64)
	water.set("auto_create_wake_map", false)
	water.set("auto_create_bubbles", false)
	water.set("interactions_enabled", false)
	water.set("micro_normal_strength", 0.0)
	water.set("detail_normal_strength", 0.0)
	water.set("whitecap_strength", 0.0)
	water.set("foam_breakup_strength", 1.0)
	water.set("shoreline_foam_strength", 1.0)
	scene.add_child(water)

	_run.call_deferred(output_path)


func _run(output_path: String) -> void:
	var vp := root.get_viewport()
	var vp_rid := vp.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp_rid, true)

	var on_ms := await _measure(true)
	var off_ms := await _measure(false)

	var delta := on_ms - off_ms
	var pct := (delta / on_ms * 100.0) if on_ms > 0.0001 else 0.0
	var report := {
		"gpu_ms_foam_on": on_ms,
		"gpu_ms_foam_off": off_ms,
		"gpu_ms_saved": delta,
		"pct_saved": pct,
		"viewport_size": [vp.get_visible_rect().size.x, vp.get_visible_rect().size.y],
		"warmup_frames": WARMUP,
		"sample_frames": SAMPLE,
		"note": "Only difference is foam_detail_enabled (3x organic_fbm). GPU time via viewport measure.",
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var f := FileAccess.open(g, FileAccess.WRITE)
	f.store_string(JSON.stringify(report, "\t"))
	f.close()
	print("FastWater far-foam benchmark: foam_on=%.3fms foam_off=%.3fms saved=%.3fms (%.1f%%)" % [on_ms, off_ms, delta, pct])
	print("saved: " + g)
	quit(0)


func _measure(foam_on: bool) -> float:
	for water in root.get_tree().get_nodes_in_group("fast_water_surface"):
		water.set("foam_detail_enabled", foam_on)
	var vp_rid := root.get_viewport().get_viewport_rid()
	for _i in range(WARMUP):
		await process_frame
	var total := 0.0
	for _i in range(SAMPLE):
		await process_frame
		total += RenderingServer.viewport_get_measured_render_time_gpu(vp_rid)
	return total / float(SAMPLE)
