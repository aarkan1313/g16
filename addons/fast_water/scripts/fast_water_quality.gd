extends Resource
class_name FastWaterQuality

@export_range(64, 1024, 64) var wake_map_resolution := 256
@export_range(0, 16, 1) var hero_ripples := 12
@export_range(16, 2200, 1) var bubble_cap := 800
@export_range(16, 256, 1) var mesh_subdivisions := 96
@export_range(0.0, 0.12, 0.001) var refraction_strength := 0.0
@export_range(0.0, 1.0, 0.01) var caustics_intensity := 0.38


static func low() -> Resource:
	var q: Resource = load("res://addons/fast_water/scripts/fast_water_quality.gd").new()
	q.wake_map_resolution = 128
	q.hero_ripples = 4
	q.bubble_cap = 240
	q.mesh_subdivisions = 48
	q.refraction_strength = 0.0
	q.caustics_intensity = 0.0
	return q


static func medium() -> Resource:
	var q: Resource = load("res://addons/fast_water/scripts/fast_water_quality.gd").new()
	q.wake_map_resolution = 256
	q.hero_ripples = 8
	q.bubble_cap = 640
	q.mesh_subdivisions = 80
	q.refraction_strength = 0.0
	q.caustics_intensity = 0.28
	return q


static func high() -> Resource:
	var q: Resource = load("res://addons/fast_water/scripts/fast_water_quality.gd").new()
	q.wake_map_resolution = 512
	q.hero_ripples = 12
	q.bubble_cap = 1200
	q.mesh_subdivisions = 128
	q.refraction_strength = 0.018
	q.caustics_intensity = 0.45
	return q
