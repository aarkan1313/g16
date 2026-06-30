extends Resource
class_name FastWaterBodyProfile

enum BodyKind {
	GENERIC,
	LAKE,
	POOL,
	RIVER,
	RAPIDS,
	WATERFALL,
	OCEAN,
	SWAMP,
}

@export var enabled := true
@export var display_name := ""
@export var body_kind := BodyKind.GENERIC
@export_range(-1024, 1024, 1) var query_priority := 0
@export_range(0.0, 128.0, 0.01) var containment_margin_m := 0.05
@export_range(0.0, 64.0, 0.01) var default_depth_m := 2.0
@export_range(0.0, 4.0, 0.01) var flow_strength_scale := 1.0
@export_range(0.0, 4.0, 0.01) var foam_bias := 0.0
@export var tags := PackedStringArray()


static func lake() -> Resource:
	var profile := _new_profile()
	profile.body_kind = BodyKind.LAKE
	profile.display_name = "Lake"
	profile.default_depth_m = 4.0
	return profile


static func pool() -> Resource:
	var profile := _new_profile()
	profile.body_kind = BodyKind.POOL
	profile.display_name = "Pool"
	profile.default_depth_m = 2.2
	return profile


static func river() -> Resource:
	var profile := _new_profile()
	profile.body_kind = BodyKind.RIVER
	profile.display_name = "River"
	profile.query_priority = 10
	profile.containment_margin_m = 0.18
	profile.default_depth_m = 1.2
	profile.flow_strength_scale = 1.0
	return profile


static func ocean() -> Resource:
	var profile := _new_profile()
	profile.body_kind = BodyKind.OCEAN
	profile.display_name = "Ocean"
	profile.query_priority = -20
	profile.default_depth_m = 24.0
	return profile


static func _new_profile() -> Resource:
	var script := load("res://addons/fast_water/scripts/fast_water_body_profile.gd") as Script
	return script.new() as Resource


func body_kind_name() -> String:
	match body_kind:
		BodyKind.LAKE:
			return "lake"
		BodyKind.POOL:
			return "pool"
		BodyKind.RIVER:
			return "river"
		BodyKind.RAPIDS:
			return "rapids"
		BodyKind.WATERFALL:
			return "waterfall"
		BodyKind.OCEAN:
			return "ocean"
		BodyKind.SWAMP:
			return "swamp"
		_:
			return "generic"
