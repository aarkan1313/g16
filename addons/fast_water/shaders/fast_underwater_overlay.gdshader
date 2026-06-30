shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform vec3 underwater_tint : source_color = vec3(0.02, 0.22, 0.28);
uniform float tint_strength = 0.42;
uniform float distortion_strength = 0.018;
uniform float vignette_strength = 0.22;
uniform vec3 surface_light_color : source_color = vec3(0.24, 0.58, 0.68);
uniform float surface_haze_strength = 0.14;
uniform float submerge_depth_m = 0.0;
uniform float submerge_fade = 0.0;
uniform float light_shaft_strength = 0.18;
uniform float caustic_strength = 0.16;
uniform float particulate_strength = 0.10;
uniform float particulate_scale = 180.0;
uniform float waterline_strength = 0.22;
uniform float waterline_width = 0.055;
uniform float waterline_screen_y = 0.55;
uniform float turbidity = 0.0;
uniform float visibility_loss = 0.0;
uniform vec3 suspended_silt_color : source_color = vec3(0.17, 0.22, 0.15);


float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}


float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}


float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.54;
	mat2 r = mat2(vec2(0.82, -0.57), vec2(0.57, 0.82));
	for (int i = 0; i < 4; i++) {
		v += noise(p) * a;
		p = r * p * 2.08 + vec2(4.7, -2.9);
		a *= 0.50;
	}
	return v;
}


void fragment() {
	vec2 centered = SCREEN_UV - vec2(0.5);
	float depth_fade = clamp(submerge_fade, 0.0, 1.0);
	float near_surface = 1.0 - depth_fade;
	float visibility = clamp(visibility_loss, 0.0, 1.0);
	float light_transmission = 1.0 - visibility * 0.72;
	float wobble_a = noise(SCREEN_UV * 18.0 + vec2(TIME * 0.45, TIME * 0.12));
	float wobble_b = noise(SCREEN_UV * 11.0 + vec2(-TIME * 0.2, TIME * 0.35));
	float waterline_noise = fbm(vec2(SCREEN_UV.x * 7.0 + TIME * 0.16, TIME * 0.11));
	float waterline_y = clamp(waterline_screen_y + (waterline_noise - 0.5) * 0.050, 0.05, 0.95);
	float waterline_band = 1.0 - smoothstep(0.0, max(waterline_width, 0.001), abs(SCREEN_UV.y - waterline_y));
	vec2 offset = vec2(wobble_a - 0.5, wobble_b - 0.5) * distortion_strength * (0.60 + depth_fade * 0.80 + waterline_band * 1.4);

	vec3 scene = texture(screen_texture, SCREEN_UV + offset).rgb;
	float effective_tint = tint_strength * (0.42 + depth_fade * 0.58);
	scene = mix(scene, underwater_tint, effective_tint);

	float upper_water = smoothstep(0.78, 0.05, SCREEN_UV.y);
	float shaft_noise = fbm(SCREEN_UV * vec2(7.0, 2.2) + vec2(TIME * 0.10, -TIME * 0.06));
	float shaft = smoothstep(0.50, 0.92, shaft_noise) * upper_water;
	scene += surface_light_color * surface_haze_strength * upper_water * (0.28 + shaft * light_shaft_strength * 2.2) * (0.45 + near_surface * 0.55) * light_transmission;

	float caustic_a = sin((SCREEN_UV.x + SCREEN_UV.y * 0.36) * 55.0 + TIME * 1.55);
	float caustic_b = sin((SCREEN_UV.x * -0.42 + SCREEN_UV.y) * 43.0 - TIME * 1.10);
	float caustic_noise = fbm(SCREEN_UV * vec2(11.0, 6.0) + vec2(TIME * 0.08, -TIME * 0.05));
	float caustics = smoothstep(0.72, 0.98, (caustic_a * caustic_b) * 0.24 + caustic_noise);
	float lower_water = smoothstep(0.25, 1.0, SCREEN_UV.y);
	scene += surface_light_color * caustics * caustic_strength * lower_water * (0.30 + depth_fade * 0.70) * light_transmission;

	vec2 particle_uv = SCREEN_UV + vec2(TIME * 0.012, -TIME * 0.030);
	vec2 cell = floor(particle_uv * particulate_scale);
	float particle_seed = hash(cell);
	float particle_core = smoothstep(0.984, 1.0, particle_seed);
	vec2 cell_uv = fract(particle_uv * particulate_scale) - vec2(0.5);
	float particle_shape = 1.0 - smoothstep(0.035, 0.42, length(cell_uv));
	float particles = particle_core * particle_shape * (0.30 + depth_fade * 0.70);
	vec3 particle_color = mix(vec3(0.70, 0.92, 0.95), suspended_silt_color + vec3(0.10, 0.10, 0.06), clamp(turbidity * 1.25, 0.0, 1.0));
	scene += particle_color * particles * particulate_strength;

	float vertical_fog = smoothstep(0.08, 1.0, SCREEN_UV.y);
	float turbidity_fog = visibility * (0.35 + depth_fade * 0.65) * (0.60 + vertical_fog * 0.40);
	scene = mix(scene, suspended_silt_color, clamp(turbidity_fog, 0.0, 0.82));

	vec3 waterline_color = mix(surface_light_color, vec3(0.92, 0.98, 1.0), 0.35);
	scene += waterline_color * waterline_band * waterline_strength * (0.55 + near_surface * 0.45);
	vec3 depth_fog_color = mix(underwater_tint * 0.72, suspended_silt_color, clamp(turbidity * 1.15, 0.0, 1.0));
	scene = mix(scene, depth_fog_color, clamp(submerge_depth_m * (0.035 + visibility * 0.075), 0.0, 0.18 + visibility * 0.42));

	float vignette = smoothstep(0.2, 0.82, length(centered));
	scene *= 1.0 - vignette * vignette_strength * (0.55 + depth_fade * 0.45);

	COLOR = vec4(scene, 1.0);
}
