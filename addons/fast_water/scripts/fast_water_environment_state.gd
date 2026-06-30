extends Resource
class_name FastWaterEnvironmentState

@export var wind_direction_xz := Vector2(0.8, 0.2)
@export_range(0.0, 80.0, 0.1) var wind_speed_mps := 0.0
@export_range(0.0, 1.0, 0.01) var gust_strength := 0.0
@export_range(0.0, 1.0, 0.01) var rain_intensity := 0.0
@export_range(0.0, 1.0, 0.01) var snow_intensity := 0.0
@export_range(0.0, 1.0, 0.01) var storm_intensity := 0.0
@export_range(-80.0, 80.0, 0.1) var air_temperature_c := 18.0
@export_range(0.0, 1.0, 0.01) var water_turbidity := 0.0
@export var sun_direction := Vector3(0.4, -0.8, 0.25)
@export var sun_color := Color(1.0, 0.96, 0.82, 1.0)
@export_range(0.0, 16.0, 0.01) var sun_intensity := 1.0


func duplicate_state() -> Resource:
	var state := FastWaterEnvironmentState.new()
	state.wind_direction_xz = wind_direction_xz
	state.wind_speed_mps = wind_speed_mps
	state.gust_strength = gust_strength
	state.rain_intensity = rain_intensity
	state.snow_intensity = snow_intensity
	state.storm_intensity = storm_intensity
	state.air_temperature_c = air_temperature_c
	state.water_turbidity = water_turbidity
	state.sun_direction = sun_direction
	state.sun_color = sun_color
	state.sun_intensity = sun_intensity
	return state
