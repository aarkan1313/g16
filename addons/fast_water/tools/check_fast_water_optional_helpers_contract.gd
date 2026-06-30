extends SceneTree

const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")


class BubbleSpy:
	extends Node3D

	var events: Array[Dictionary] = []

	func burst(world_pos: Vector3, strength: float = 1.0, radius: float = 0.5) -> void:
		events.append({
			"position": [world_pos.x, world_pos.y, world_pos.z],
			"strength": strength,
			"radius": radius,
		})


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_optional_helpers_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var scene := Node3D.new()
	scene.name = "FastWaterOptionalHelpersContract"
	root.add_child(scene)

	var surface := SURFACE_SCRIPT.new()
	surface.name = "CoreReadableWater"
	surface.set("auto_create_mesh", false)
	surface.set("auto_create_wake_map", true)
	surface.set("use_gpu_wake_map", false)
	surface.set("auto_create_foam_field", true)
	surface.set("auto_create_bubbles", false)
	surface.set("wake_map_resolution", 128)
	surface.set("wake_map_world_size_m", 32.0)
	surface.set("mesh_size_m", 32.0)
	surface.set("route_wakes_to_wake_map", true)
	surface.set("wake_strength_scale", 0.08)
	surface.set("wake_map_strength", 0.65)
	surface.set("wake_bubble_strength", 0.25)
	surface.set("foam_field_strength", 1.0)
	scene.add_child(surface)

	var bubbles := BubbleSpy.new()
	bubbles.name = "BubbleSpy"
	surface.add_child(bubbles)
	surface.set("bubble_pool_node", bubbles)

	await process_frame
	await process_frame

	var wake_pos := Vector3(0.0, 0.0, 0.0)
	surface.call("add_wake_point", wake_pos, Vector3(12.0, 0.0, 0.0), 0.9)
	var wake_map_after_stamp := surface.get("wake_map_node") as Node
	var wake_pending_after_stamp := _pending_count(wake_map_after_stamp)
	if wake_map_after_stamp != null and _has_property(wake_map_after_stamp, "update_hz"):
		wake_map_after_stamp.set("update_hz", 0.0)
	var wake_has_update_texture := wake_map_after_stamp != null and wake_map_after_stamp.has_method("_update_texture")
	if wake_map_after_stamp != null and wake_map_after_stamp.has_method("_update_texture"):
		wake_map_after_stamp.call("_update_texture", 1.0 / 30.0)

	for _i in range(8):
		await process_frame

	var material := surface.get("water_material") as ShaderMaterial
	var wake_map := surface.get("wake_map_node") as Node
	var foam_field := surface.get("foam_field_node") as Node
	var wake_sample := _sample_wake_map(wake_map, wake_pos)
	var foam_sample := 0.0
	if foam_field != null and foam_field.has_method("sample_foam_at"):
		foam_sample = float(foam_field.call("sample_foam_at", wake_pos))

	var checks := {
		"no_bow_wake_nodes": _count_name_contains(scene, "BowWake") == 0,
		"no_wake_ribbon_nodes": _count_name_contains(scene, "WakeRibbon") == 0,
		"hero_ripple_still_added": material != null and int(material.get_shader_parameter("hero_ripple_count")) > 0,
		"wake_map_created": wake_map != null and wake_map.get("texture") != null,
		"wake_map_core_channels_changed": float(wake_sample.get("max_height_delta", 0.0)) > 0.002 or float(wake_sample.get("max_flow_delta", 0.0)) > 0.002,
		"foam_field_wake_stamped": foam_sample > 0.04,
		"bubble_signal_still_routes": bubbles.events.size() > 0,
	}
	var passed := true
	for value in checks.values():
		passed = passed and bool(value)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {
			"wake_map": wake_sample,
			"wake_map_script": _script_path(wake_map),
			"wake_pending_after_stamp": wake_pending_after_stamp,
			"wake_has_update_texture": wake_has_update_texture,
			"surface_route_wakes_to_wake_map": bool(surface.get("route_wakes_to_wake_map")),
			"foam": foam_sample,
			"bubble_events": bubbles.events,
			"hero_ripple_count": int(material.get_shader_parameter("hero_ripple_count")) if material != null else 0,
		},
	}

	var global_path := ProjectSettings.globalize_path(output_path)
	var dir := global_path.get_base_dir()
	if dir != "":
		DirAccess.make_dir_recursive_absolute(dir)
	var file := FileAccess.open(global_path, FileAccess.WRITE)
	if file == null:
		push_error("FastWater optional helper contract check could not write " + global_path)
		quit(1)
		return
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater optional helper contract check saved: " + global_path)
	quit(0 if passed else 1)


func _sample_wake_map(wake_map: Node, world_position: Vector3) -> Dictionary:
	if wake_map == null:
		return {}
	var texture := wake_map.get("texture") as Texture2D
	var image := wake_map.get("_image") as Image
	if image == null and texture != null:
		image = texture.get_image()
	if image == null:
		return {}
	var origin: Vector2 = wake_map.get("origin_xz")
	var world_size := float(wake_map.get("world_size_m"))
	var uv := (Vector2(world_position.x, world_position.z) - origin) / maxf(world_size, 0.001) + Vector2(0.5, 0.5)
	var px: int = clampi(roundi(uv.x * float(image.get_width() - 1)), 0, image.get_width() - 1)
	var py: int = clampi(roundi(uv.y * float(image.get_height() - 1)), 0, image.get_height() - 1)
	var c := image.get_pixel(px, py)
	var max_foam := c.g
	var max_height_delta := absf(c.r - 0.5)
	var max_flow_delta := Vector2(c.b - 0.5, c.a - 0.5).length()
	var local_max_foam := max_foam
	var local_max_height_delta := max_height_delta
	var local_max_flow_delta := max_flow_delta
	var radius_px := 14
	for y in range(maxi(py - radius_px, 0), mini(py + radius_px, image.get_height() - 1) + 1):
		for x in range(maxi(px - radius_px, 0), mini(px + radius_px, image.get_width() - 1) + 1):
			var n := image.get_pixel(x, y)
			local_max_foam = maxf(local_max_foam, n.g)
			local_max_height_delta = maxf(local_max_height_delta, absf(n.r - 0.5))
			local_max_flow_delta = maxf(local_max_flow_delta, Vector2(n.b - 0.5, n.a - 0.5).length())
	for y in range(image.get_height()):
		for x in range(image.get_width()):
			var n := image.get_pixel(x, y)
			max_foam = maxf(max_foam, n.g)
			max_height_delta = maxf(max_height_delta, absf(n.r - 0.5))
			max_flow_delta = maxf(max_flow_delta, Vector2(n.b - 0.5, n.a - 0.5).length())
	return {
		"rgba": [c.r, c.g, c.b, c.a],
		"foam": c.g,
		"height_delta": absf(c.r - 0.5),
		"flow_delta": Vector2(c.b - 0.5, c.a - 0.5).length(),
		"local_max_foam": local_max_foam,
		"local_max_height_delta": local_max_height_delta,
		"local_max_flow_delta": local_max_flow_delta,
		"max_foam": max_foam,
		"max_height_delta": max_height_delta,
		"max_flow_delta": max_flow_delta,
	}


func _count_name_contains(node: Node, needle: String) -> int:
	var count := 1 if node.name.contains(needle) else 0
	for child in node.get_children():
		count += _count_name_contains(child, needle)
	return count


func _pending_count(wake_map: Node) -> int:
	if wake_map == null:
		return -1
	var pending = wake_map.get("_pending")
	if pending is Array:
		return pending.size()
	return -1


func _has_property(object: Object, property_name: String) -> bool:
	if object == null:
		return false
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false


func _script_path(node: Node) -> String:
	if node == null:
		return ""
	var script := node.get_script() as Script
	return script.resource_path if script != null else ""
