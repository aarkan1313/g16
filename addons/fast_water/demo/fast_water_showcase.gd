@tool
extends Node3D
class_name FastWaterShowcase

## Performant proof scene: an island in the ocean with a river running down the
## mountainside into the sea, buoyant objects bobbing offshore, and a ball that drops
## into a hillside pool and splashes. Built entirely from Fast Water public nodes and
## the shipped presets. Open the .tscn and free-fly (right-mouse look, WASD, Q/E,
## Shift), or render proof shots with tools/render_fast_water_showcase.gd.

const OCEAN_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean.gd")
const OCEAN_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_ocean_profile.gd")
const PATH_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_path.gd")
const SURFACE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_surface.gd")
const BUOYANT_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_buoyant.gd")
const INTERACTOR_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_interactor.gd")
const BODY_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_body_profile.gd")
const VISUAL_PROFILE_SCRIPT := preload("res://addons/fast_water/scripts/fast_water_visual_profile.gd")

const POOL_CENTER := Vector3(-34.0, 14.0, 26.0)
const POOL_SURFACE_Y := 14.0
const BALL_SPAWN := Vector3(-34.0, 22.0, 26.0)

var _camera: Camera3D
var _ocean: Node3D
var _pool: Node3D
var _ball: RigidBody3D
var _ball_timer := 0.0
var _free_fly := false
var _yaw := 0.0
var _pitch := -0.35
var _move_speed := 16.0


func _ready() -> void:
	_build_environment()
	_build_island()
	_build_ocean()
	_build_river()
	_build_pool()
	_build_camera()
	if not Engine.is_editor_hint():
		_build_ocean_floaters()
		_build_splash_ball()


func _process(delta: float) -> void:
	if Engine.is_editor_hint():
		return
	# Recycle the splash ball so the pool keeps splashing in the live scene.
	if _ball != null:
		_ball_timer += delta
		if _ball_timer > 4.0 or _ball.global_position.y < POOL_SURFACE_Y - 4.0:
			_reset_ball()
	if _free_fly:
		_update_free_fly(delta)


# --- world build ---------------------------------------------------------------

func _build_environment() -> void:
	var we := get_node_or_null("Sky") as WorldEnvironment
	if we == null:
		we = WorldEnvironment.new()
		we.name = "Sky"
		add_child(we)
		_set_owner(we)
	var env := Environment.new()
	env.background_mode = Environment.BG_SKY
	var sky := Sky.new()
	var sky_mat := ProceduralSkyMaterial.new()
	sky_mat.sky_top_color = Color(0.38, 0.56, 0.82)
	sky_mat.sky_horizon_color = Color(0.72, 0.80, 0.86)
	sky_mat.ground_horizon_color = Color(0.62, 0.70, 0.74)
	sky_mat.sun_angle_max = 25.0
	sky.sky_material = sky_mat
	env.sky = sky
	env.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	env.ambient_light_energy = 1.0
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.ssao_enabled = false
	env.ssr_enabled = false
	we.environment = env

	var sun := get_node_or_null("Sun") as DirectionalLight3D
	if sun == null:
		sun = DirectionalLight3D.new()
		sun.name = "Sun"
		add_child(sun)
		_set_owner(sun)
	sun.rotation_degrees = Vector3(-48.0, 38.0, 0.0)
	sun.light_energy = 1.15
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 120.0


func _build_island() -> void:
	if get_node_or_null("Island") != null:
		return
	var island := MeshInstance3D.new()
	island.name = "Island"
	var cone := CylinderMesh.new()
	cone.top_radius = 5.0
	cone.bottom_radius = 64.0
	cone.height = 70.0
	cone.radial_segments = 48
	cone.rings = 6
	island.mesh = cone
	island.position = Vector3(0.0, 10.0, 0.0) # base at y=-25, peak at y=45
	var rock := StandardMaterial3D.new()
	rock.albedo_color = Color(0.34, 0.31, 0.26)
	rock.roughness = 0.95
	island.material_override = rock
	add_child(island)
	_set_owner(island)


func _build_ocean() -> void:
	_ocean = get_node_or_null("Ocean") as Node3D
	if _ocean == null:
		_ocean = OCEAN_SCRIPT.new()
		_ocean.name = "Ocean"
		# Set the profile BEFORE entering the tree so _ready applies it (not the default).
		_ocean.set("ocean_profile", OCEAN_PROFILE_SCRIPT.performance())
		add_child(_ocean)
		_set_owner(_ocean)


func _build_river() -> void:
	if get_node_or_null("River") != null:
		return
	var river := PATH_SCRIPT.new()
	river.name = "River"
	# Descend the +x face of the cone from near the peak down into the sea.
	river.set("control_points", PackedVector3Array([
		Vector3(7.0, 40.0, 1.0),
		Vector3(17.0, 31.0, 3.0),
		Vector3(28.0, 22.0, 2.0),
		Vector3(39.0, 13.0, -2.0),
		Vector3(49.0, 5.0, -1.0),
		Vector3(58.0, -1.5, 0.0),
	]))
	river.set("point_widths_m", PackedFloat32Array([2.2, 2.8, 3.6, 4.6, 5.4, 6.4]))
	river.set("point_depths_m", PackedFloat32Array([0.5, 0.7, 0.9, 1.1, 1.3, 1.6]))
	river.set("point_flow_speeds_mps", PackedFloat32Array([1.6, 2.4, 3.2, 3.8, 4.2, 3.0]))
	river.set("point_turbulence_strengths", PackedFloat32Array([0.3, 0.5, 0.7, 0.85, 0.9, 0.5]))
	river.set("point_bank_foam_strengths", PackedFloat32Array([0.3, 0.45, 0.6, 0.8, 0.9, 0.7]))
	river.set("flow_field_resolution", 96)
	river.set("auto_create_flow_field", true)
	add_child(river)
	_set_owner(river)


func _build_pool() -> void:
	# Rock shelf the pool sits in, with a floor so depth colour reads.
	if get_node_or_null("PoolShelf") == null:
		var shelf := MeshInstance3D.new()
		shelf.name = "PoolShelf"
		var box := BoxMesh.new()
		box.size = Vector3(20.0, 8.0, 20.0)
		shelf.mesh = box
		shelf.position = POOL_CENTER + Vector3(0.0, -4.2, 0.0)
		var rock := StandardMaterial3D.new()
		rock.albedo_color = Color(0.30, 0.28, 0.24)
		rock.roughness = 0.95
		shelf.material_override = rock
		add_child(shelf)
		_set_owner(shelf)

	_pool = get_node_or_null("Pool") as Node3D
	if _pool == null:
		_pool = SURFACE_SCRIPT.new()
		_pool.name = "Pool"
		# Configure BEFORE entering the tree so _ready builds the right mesh + visual.
		_pool.set("follow_camera", false)
		_pool.set("mesh_size_m", 14.0)
		_pool.set("mesh_subdivisions", 64)
		_pool.set("use_gpu_wake_map", false)
		_pool.set("wake_map_world_size_m", 16.0)
		_pool.set("body_profile", BODY_PROFILE_SCRIPT.pool())
		_pool.set("visual_profile", VISUAL_PROFILE_SCRIPT.gameplay_lake())
		add_child(_pool)
		_set_owner(_pool)
	_pool.global_position = Vector3(POOL_CENTER.x, POOL_SURFACE_Y, POOL_CENTER.z)


func _build_camera() -> void:
	_camera = get_node_or_null("ShowcaseCamera") as Camera3D
	if _camera == null:
		_camera = Camera3D.new()
		_camera.name = "ShowcaseCamera"
		add_child(_camera)
		_set_owner(_camera)
	_camera.far = 6000.0
	frame_overview()
	if _ocean != null:
		_ocean.set("target_camera", _camera)
	if not Engine.is_editor_hint():
		_camera.current = true


# --- dynamic actors (runtime only) ---------------------------------------------

func _build_ocean_floaters() -> void:
	var colors := [Color(0.9, 0.3, 0.25), Color(0.95, 0.85, 0.3), Color(0.3, 0.7, 0.9)]
	var spots := [Vector3(70.0, 3.0, 18.0), Vector3(78.0, 4.0, 30.0), Vector3(64.0, 3.0, 36.0)]
	for i in range(spots.size()):
		var floater := _make_rigid_sphere(1.6, colors[i % colors.size()])
		floater.name = "Floater%d" % i
		floater.position = spots[i]
		add_child(floater)
		var buoy := BUOYANT_SCRIPT.new()
		buoy.set("buoyancy_force", 90.0)
		floater.add_child(buoy)


func _build_splash_ball() -> void:
	_ball = _make_rigid_sphere(1.1, Color(0.95, 0.97, 1.0))
	_ball.name = "SplashBall"
	_ball.position = BALL_SPAWN
	add_child(_ball)
	var interactor := INTERACTOR_SCRIPT.new()
	interactor.set("water_surface", _pool)
	interactor.set("radius", 1.1)
	interactor.set("min_speed_for_splash", 1.0)
	_ball.add_child(interactor)
	var buoy := BUOYANT_SCRIPT.new()
	buoy.set("water_surface_path", _ball.get_path_to(_pool))
	buoy.set("buoyancy_force", 70.0)
	_ball.add_child(buoy)


func _reset_ball() -> void:
	_ball_timer = 0.0
	_ball.linear_velocity = Vector3.ZERO
	_ball.angular_velocity = Vector3.ZERO
	_ball.global_position = BALL_SPAWN


func _make_rigid_sphere(radius: float, color: Color) -> RigidBody3D:
	var body := RigidBody3D.new()
	var mesh := MeshInstance3D.new()
	var sphere := SphereMesh.new()
	sphere.radius = radius
	sphere.height = radius * 2.0
	mesh.mesh = sphere
	var mat := StandardMaterial3D.new()
	mat.albedo_color = color
	mat.roughness = 0.4
	mesh.material_override = mat
	body.add_child(mesh)
	var col := CollisionShape3D.new()
	var shape := SphereShape3D.new()
	shape.radius = radius
	col.shape = shape
	body.add_child(col)
	return body


# --- camera framing (used by the render tool) ----------------------------------

func get_camera() -> Camera3D:
	return _camera


func frame_overview() -> void:
	_place_camera(Vector3(96.0, 56.0, 96.0), Vector3(10.0, 8.0, 6.0))


func frame_river() -> void:
	_place_camera(Vector3(74.0, 20.0, 34.0), Vector3(34.0, 12.0, 0.0))


func frame_pool() -> void:
	_place_camera(POOL_CENTER + Vector3(9.0, 6.5, 9.0), POOL_CENTER + Vector3(0.0, 0.5, 0.0))


func _place_camera(pos: Vector3, look: Vector3) -> void:
	if _camera == null:
		return
	_camera.global_position = pos
	_camera.look_at(look, Vector3.UP)
	var e := _camera.rotation
	_yaw = e.y
	_pitch = e.x


# --- free-fly (interactive) ----------------------------------------------------

func _unhandled_input(event: InputEvent) -> void:
	if Engine.is_editor_hint():
		return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT:
		_free_fly = event.pressed
		Input.mouse_mode = Input.MOUSE_MODE_CAPTURED if event.pressed else Input.MOUSE_MODE_VISIBLE
	elif event is InputEventMouseMotion and _free_fly:
		_yaw -= event.relative.x * 0.005
		_pitch = clamp(_pitch - event.relative.y * 0.005, -1.4, 1.4)


func _update_free_fly(delta: float) -> void:
	if _camera == null:
		return
	_camera.rotation = Vector3(_pitch, _yaw, 0.0)
	var dir := Vector3.ZERO
	var b := _camera.global_transform.basis
	if Input.is_physical_key_pressed(KEY_W): dir -= b.z
	if Input.is_physical_key_pressed(KEY_S): dir += b.z
	if Input.is_physical_key_pressed(KEY_A): dir -= b.x
	if Input.is_physical_key_pressed(KEY_D): dir += b.x
	if Input.is_physical_key_pressed(KEY_E): dir += Vector3.UP
	if Input.is_physical_key_pressed(KEY_Q): dir -= Vector3.UP
	var speed := _move_speed * (3.0 if Input.is_physical_key_pressed(KEY_SHIFT) else 1.0)
	_camera.global_position += dir.normalized() * speed * delta


func _set_owner(node: Node) -> void:
	if Engine.is_editor_hint() and get_tree() != null and get_tree().edited_scene_root != null:
		node.owner = get_tree().edited_scene_root
