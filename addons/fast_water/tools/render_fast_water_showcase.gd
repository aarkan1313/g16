extends SceneTree
# Renders proof shots of the island showcase (ocean + island + river + pool splash)
# and a performance metrics readout. Run windowed for real GPU timing:
#   Godot --path <proj> --rendering-driver d3d12 \
#     --script res://addons/fast_water/tools/render_fast_water_showcase.gd

const SHOWCASE := preload("res://addons/fast_water/demo/fast_water_showcase.tscn")
const OUT := "res://artifacts/"


func _initialize() -> void:
	var showcase := SHOWCASE.instantiate()
	root.add_child(showcase)
	for _i in range(30):
		await process_frame

	var cam := showcase.call("get_camera") as Camera3D

	# Performance sample at the overview (the heaviest framing: ocean + island + river).
	showcase.call("frame_overview")
	var perf := await _measure_perf()

	# Shot 1: overview.
	for _i in range(40):
		await process_frame
	_save(showcase, "fast_water_showcase_overview.png")

	# Shot 2: river down the mountainside.
	showcase.call("frame_river")
	for _i in range(40):
		await process_frame
	_save(showcase, "fast_water_showcase_river.png")

	# Shot 3: ball splashing into the hillside pool. Drop a fresh ball and capture as it
	# breaks the surface.
	showcase.call("frame_pool")
	var ball := showcase.get_node_or_null("SplashBall") as RigidBody3D
	if ball != null:
		ball.linear_velocity = Vector3.ZERO
		ball.angular_velocity = Vector3.ZERO
		ball.global_position = Vector3(-34.0, 21.0, 26.0)
	var guard := 0
	while ball != null and ball.global_position.y > 15.4 and guard < 600:
		guard += 1
		await process_frame
	for _i in range(8): # let the splash crown / ripples develop
		await process_frame
	_save(showcase, "fast_water_showcase_pool_splash.png")

	var report := {
		"performance": perf,
		"shots": [
			"fast_water_showcase_overview.png",
			"fast_water_showcase_river.png",
			"fast_water_showcase_pool_splash.png",
		],
	}
	var g := ProjectSettings.globalize_path(OUT + "fast_water_showcase_metrics.json")
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var f := FileAccess.open(g, FileAccess.WRITE)
	f.store_string(JSON.stringify(report, "\t"))
	f.close()
	print("FastWater showcase: %.2f FPS  gpu=%.3fms cpu=%.3fms" % [perf["fps"], perf["gpu_ms"], perf["cpu_ms"]])
	print("showcase metrics saved: " + g)
	quit(0)


func _measure_perf() -> Dictionary:
	var vp_rid := root.get_viewport().get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(vp_rid, true)
	for _i in range(60): # warmup
		await process_frame
	var gpu := 0.0
	var cpu := 0.0
	var samples := 150
	for _i in range(samples):
		await process_frame
		gpu += RenderingServer.viewport_get_measured_render_time_gpu(vp_rid)
		cpu += RenderingServer.viewport_get_measured_render_time_cpu(vp_rid)
	gpu /= float(samples)
	cpu /= float(samples)
	var frame_ms := maxf(gpu, cpu)
	return {
		"gpu_ms": gpu,
		"cpu_ms": cpu,
		"fps": (1000.0 / frame_ms) if frame_ms > 0.001 else 0.0,
		"sample_frames": samples,
	}


func _save(showcase: Node, file_name: String) -> void:
	var img := root.get_viewport().get_texture().get_image()
	var g := ProjectSettings.globalize_path(OUT + file_name)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	img.save_png(g)
	print("saved " + file_name)
