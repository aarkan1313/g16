extends Resource
class_name FastWaterWeatherResponse

@export_category("Wind")
@export_range(0.0, 0.5, 0.001) var wind_wave_height_per_mps := 0.018
@export_range(0.0, 3.0, 0.01) var max_wind_wave_height_boost := 0.55
@export_range(0.0, 0.5, 0.001) var wind_wave_speed_per_mps := 0.018
@export_range(0.0, 1.0, 0.01) var gust_wave_boost := 0.18
@export_range(0.0, 2.0, 0.01) var wind_glint_boost := 0.35

@export_category("Rain And Storm")
@export_range(0.0, 4.0, 0.01) var rain_ripple_rate := 80.0
@export_range(0.01, 2.0, 0.01) var rain_ripple_radius_m := 0.16
@export_range(0.0, 1.0, 0.001) var rain_ripple_strength := 0.055
@export_range(0.0, 2.0, 0.01) var rain_foam_boost := 0.20
@export_range(0.0, 3.0, 0.01) var storm_foam_boost := 0.80
@export_range(0.0, 2.0, 0.01) var storm_normal_boost := 0.32
@export_range(0.0, 2.0, 0.01) var rain_impact_visibility := 0.82
@export_range(0.0, 2.0, 0.01) var rain_mist_visibility := 0.48
@export_range(0.0, 40.0, 0.1) var whitecap_wind_threshold_mps := 6.0
@export_range(0.0, 0.25, 0.001) var whitecap_wind_strength_per_mps := 0.045
@export_range(0.0, 2.0, 0.01) var gust_whitecap_boost := 0.22
@export_range(0.0, 2.0, 0.01) var storm_whitecap_boost := 0.72
@export_range(0.02, 4.0, 0.01) var whitecap_noise_scale := 0.32

@export_category("Optics")
@export_range(0.0, 1.0, 0.01) var turbidity_deep_color_mix := 0.36
@export_range(0.0, 1.0, 0.01) var turbidity_refraction_loss := 0.55
@export var turbid_deep_color := Color(0.02, 0.12, 0.12, 1.0)
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_visibility_loss := 0.72
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_tint_boost := 0.28
@export_range(0.0, 2.0, 0.01) var turbidity_underwater_distortion_boost := 0.55
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_particulate_boost := 0.42
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_vignette_boost := 0.24
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_caustic_loss := 0.68
@export_range(0.0, 1.0, 0.01) var turbidity_underwater_light_loss := 0.36
@export var turbid_underwater_tint := Color(0.04, 0.16, 0.12, 1.0)
@export var suspended_silt_color := Color(0.17, 0.22, 0.15, 1.0)
