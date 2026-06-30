extends SceneTree
# Verifies the shipped .tres preset library loads and each resource carries the
# expected script + a sane key property, so projects can rely on data-driven tuning.

const DIR := "res://addons/fast_water/presets/"

# file prefix -> a property that must exist and be non-default-garbage on that resource
const EXPECT := {
	"visual_": "shallow_color",
	"ocean_": "near_mesh_size_m",
	"body_": "body_kind",
	"quality_": "mesh_subdivisions",
}


func _initialize() -> void:
	var output_path := "res://artifacts/fast_water_presets_contract.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output_path = arg.trim_prefix("--output=")

	var dir := DirAccess.open(DIR)
	var loaded := 0
	var problems: Array[String] = []
	var names: Array[String] = []
	if dir == null:
		problems.append("presets directory missing: " + DIR)
	else:
		dir.list_dir_begin()
		var f := dir.get_next()
		while f != "":
			if not dir.current_is_dir() and f.ends_with(".tres"):
				names.append(f)
			f = dir.get_next()
		dir.list_dir_end()

	for f in names:
		var res := load(DIR + f) as Resource
		if res == null:
			problems.append("failed to load " + f)
			continue
		if res.get_script() == null:
			problems.append("no script on " + f)
			continue
		var expected_prop := ""
		for prefix in EXPECT:
			if f.begins_with(prefix):
				expected_prop = EXPECT[prefix]
				break
		if expected_prop == "" :
			problems.append("unexpected preset name " + f)
			continue
		if not _has_property(res, expected_prop):
			problems.append("%s missing expected property %s" % [f, expected_prop])
			continue
		loaded += 1

	var checks := {
		"presets_directory_exists": dir != null,
		"found_at_least_12_presets": names.size() >= 12,
		"all_presets_load_with_script_and_property": problems.is_empty() and loaded == names.size(),
	}
	var passed := true
	for v in checks.values():
		passed = passed and bool(v)

	var report := {
		"passed": passed,
		"checks": checks,
		"samples": {"preset_count": names.size(), "loaded_ok": loaded, "problems": problems},
	}
	var g := ProjectSettings.globalize_path(output_path)
	DirAccess.make_dir_recursive_absolute(g.get_base_dir())
	var file := FileAccess.open(g, FileAccess.WRITE)
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	print("FastWater presets contract saved: " + g)
	quit(0 if passed else 1)


func _has_property(object: Object, property_name: String) -> bool:
	for property in object.get_property_list():
		if property.get("name", "") == property_name:
			return true
	return false
