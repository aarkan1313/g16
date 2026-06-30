extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const BODY_QUERY_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_query.gd")
const INTERACTOR_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_interactor.gd")
const BUOYANT_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_buoyant.gd")


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_interaction_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterInteractionContractCheck"
	root.add_child(scene)

	var lake := _add_lake(scene)
	var river := _add_river(scene)
	var lake_actor := _add_interactor_actor(scene, "LakeInteractorActor", Vector3(-6.0, 0.95, 4.0), lake)
	var river_actor := _add_interactor_actor(scene, "RiverInteractorActor", Vector3(0.0, 0.95, 0.0), river)
	var lake_body := _add_buoyant_body(scene, "LakeBuoyantBody", Vector3(-6.0, -0.65, 4.0), lake)
	var river_body := _add_buoyant_body(scene, "RiverBuoyantBody", Vector3(0.0, -0.65, 0.0), river)

	await process_frame
	await physics_frame

	for i in range(18):
		var t: float = float(i + 1) / 18.0
		lake_actor.global_position = Vector3(-6.0 + t * 0.9, lerpf(0.95, -0.18, t), 4.0)
		river_actor.global_position = Vector3(-0.8 + t * 1.6, lerpf(0.95, -0.18, t), 0.0)
		await physics_frame

	for _i in range(90):
		await physics_frame

	var lake_interaction_count: int = _shader_ripple_count(lake.get("water_material") as ShaderMaterial)
	var river_interaction_count: int = _shader_ripple_count(river.get("path_material") as ShaderMaterial)
	var lake_probe := Vector3(-6.0, -0.65, 4.0)
	var river_probe := Vector3(0.0, -0.65, 0.0)
	var lake_query := BODY_QUERY_SCRIPT.find_best_body_query(self, lake_probe, false)
	var river_query := BODY_QUERY_SCRIPT.find_best_body_query(self, river_probe, false)
	var river_flow := BODY_QUERY_SCRIPT.get_flow_at(river, river_probe)

	var checks := {
		"lake_interactor_emitted": lake_interaction_count > 0,
		"river_interactor_emitted": river_interaction_count > 0,
		"lake_buoyant_lifted": lake_body.linear_velocity.y > 0.15,
		"river_buoyant_lifted": river_body.linear_velocity.y > 0.15,
		"river_buoyant_drifted_with_flow": river_body.linear_velocity.x > 0.15,
		"lake_query_selected": lake_query.get("body", null) == lake,
		"river_query_selected": river_query.get("body", null) == river,
		"river_flow_positive": river_flow.x > 0.5,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"lake_interaction_count": lake_interaction_count,
		"river_interaction_count": river_interaction_count,
		"lake_body": _serialize_body(lake_body),
		"river_body": _serialize_body(river_body),
		"lake_query": _serialize_query(lake_query),
		"river_query": _serialize_query(river_query),
		"river_flow": [river_flow.x, river_flow.y, river_flow.z],
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater interaction contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater interaction contract check saved: " + global_path)
	quit(0 if passed else 1)


func _add_lake(scene: Node3D) -> Node:
	var lake := SURFACE_SCRIPT.new()
	lake.name = "InteractionLake"
	lake.set("follow_camera", false)
	lake.set("auto_create_mesh", false)
	lake.set("auto_create_wake_map", false)
	lake.set("auto_create_bubbles", false)
	lake.set("splash_pool_size", 0)
	lake.set("route_wakes_to_wake_map", false)
	lake.set("body_profile", BODY_PROFILE_SCRIPT.lake())
	lake.position = Vector3(-6.0, 0.0, 4.0)
	scene.add_child(lake)
	return lake


func _add_river(scene: Node3D) -> Node:
	var profile := BODY_PROFILE_SCRIPT.river()
	profile.set("query_priority", 30)
	var river := PATH_SCRIPT.new()
	river.name = "InteractionRiver"
	river.set("auto_create_flow_field", false)
	river.set("body_profile", profile)
	river.set("width_m", 2.4)
	river.set("flow_speed_mps", 2.3)
	river.set("control_points", PackedVector3Array([
		Vector3(-5.0, 0.0, 0.0),
		Vector3(0.0, 0.0, 0.0),
		Vector3(5.0, 0.0, 0.0),
	]))
	scene.add_child(river)
	return river


func _add_interactor_actor(scene: Node3D, actor_name: String, start_position: Vector3, water_body: Node) -> Node3D:
	var actor := Node3D.new()
	actor.name = actor_name
	actor.position = start_position
	scene.add_child(actor)

	var interactor := INTERACTOR_SCRIPT.new()
	interactor.name = "FastWaterInteractor"
	interactor.set("water_surface", water_body)
	interactor.set("radius", 0.45)
	interactor.set("min_speed_for_splash", 0.1)
	interactor.set("min_speed_for_wake", 0.1)
	interactor.set("wake_interval_s", 0.0)
	actor.add_child(interactor)
	return actor


func _add_buoyant_body(scene: Node3D, body_name: String, start_position: Vector3, water_body: Node) -> RigidBody3D:
	var body := RigidBody3D.new()
	body.name = body_name
	body.mass = 1.0
	body.gravity_scale = 0.0
	body.linear_velocity = Vector3.ZERO
	body.angular_velocity = Vector3.ZERO
	body.position = start_position
	scene.add_child(body)

	var buoyant := BUOYANT_SCRIPT.new()
	buoyant.name = "FastWaterBuoyant"
	buoyant.set("probe_offsets", PackedVector3Array([Vector3.ZERO]))
	buoyant.set("buoyancy_force", 34.0)
	buoyant.set("flow_force", 26.0)
	buoyant.set("linear_water_drag", 0.0)
	buoyant.set("angular_water_drag", 0.0)
	buoyant.set("upright_torque", 0.0)
	body.add_child(buoyant)
	buoyant.set("water_surface_path", buoyant.get_path_to(water_body))
	return body


func _shader_ripple_count(material: ShaderMaterial) -> int:
	if material == null:
		return 0
	return int(material.get_shader_parameter("hero_ripple_count"))


func _serialize_body(body: RigidBody3D) -> Dictionary:
	return {
		"name": body.name,
		"position": [body.global_position.x, body.global_position.y, body.global_position.z],
		"linear_velocity": [body.linear_velocity.x, body.linear_velocity.y, body.linear_velocity.z],
	}


func _serialize_query(query: Dictionary) -> Dictionary:
	var body := query.get("body", null) as Node
	var flow := query.get("flow", Vector3.ZERO) as Vector3
	return {
		"valid": bool(query.get("valid", false)),
		"body_name": body.name if body != null else "",
		"body_kind": String(query.get("body_kind", "")),
		"height": float(query.get("height", INF)),
		"altitude": float(query.get("altitude", INF)),
		"depth": float(query.get("depth", 0.0)),
		"flow": [flow.x, flow.y, flow.z],
		"contains": bool(query.get("contains", false)),
	}
