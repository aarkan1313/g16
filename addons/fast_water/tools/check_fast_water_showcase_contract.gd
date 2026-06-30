extends SceneTree
# Headless build gate for the island showcase scene: instantiates it, lets it settle,
# and verifies all three vignettes are present and queryable (ocean facade offshore,
# river path, hillside pool, splash ball, ocean floaters). Catches composition
# breakage without needing a GPU.

const SHOWCASE := preload("res://addons/fast_water/demo/fast_water_showcase.tscn")
const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_showcase_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var showcase := SHOWCASE.instantiate()
	root.add_child(showcase)
	for _i in range(20):
		await process_frame

	var ocean := showcase.get_node_or_null("Ocean")
	var river := showcase.get_node_or_null("River")
	var pool := showcase.get_node_or_null("Pool")
	var ball := showcase.get_node_or_null("SplashBall")
	var floater := showcase.get_node_or_null("Floater0")

	# Offshore: the body query should resolve the ocean facade (not a render tier).
	var sea_query := BODY_QUERY_SCRIPT.find_best_body(root.get_tree(), Vector3(80.0, -2.0, 0.0), true)
	# At the pool: query should resolve the pool surface.
	var pool_query := BODY_QUERY_SCRIPT.find_best_body(root.get_tree(), Vector3(-34.0, 13.0, 26.0), true)

	var checks := {
		"ocean_present": ocean != null and ocean.is_in_group("fast_water_ocean"),
		"river_present": river != null and river.is_in_group("fast_water_path"),
		"pool_present": pool != null and pool.is_in_group("fast_water_surface"),
		"splash_ball_present": ball is RigidBody3D,
		"ocean_floaters_present": floater is RigidBody3D,
		"sea_query_resolves_ocean_facade": sea_query == ocean,
		"pool_query_resolves_pool": pool_query == pool,
		"river_flow_nonzero": river != null and (river.call("get_flow_at", Vector3(39.0, 13.0, -2.0)) as Vector3).length() > 0.1,
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {"passed": passed, "checks": checks}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var file := FileAccess.open(g, FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater showcase contract saved: " + g)
	quit(0 if passed else 1)
