shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_disabled, specular_schlick_ggx;

instance uniform float start_time = 0.0;
instance uniform float strength = 1.0;
instance uniform float duration = 1.0;

uniform vec3 splash_color : source_color = vec3(0.85, 0.95, 1.0);
uniform float expand_scale = 1.8;
uniform float rise_scale = 0.75;


float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}


float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}


void vertex() {
	float age = clamp((TIME - start_time) / max(duration, 0.001), 0.0, 1.0);
	float radial = clamp(length(VERTEX.xz), 0.0, 1.0);
	float edge_noise = hash(normalize(VERTEX.xz + vec2(0.0001)) * 13.17);

	VERTEX.xz *= 1.0 + age * strength * expand_scale;
	VERTEX.y += sin(age * 3.14159265) * strength * rise_scale * (1.0 - radial * 0.45);
	VERTEX.y += (edge_noise - 0.5) * strength * 0.18 * (1.0 - age);
}


void fragment() {
	float age = clamp((TIME - start_time) / max(duration, 0.001), 0.0, 1.0);
	float height_feather = sin(UV.y * 3.14159265);
	float broken = mix(0.25, 1.0, smoothstep(0.28, 0.90, noise(vec2(UV.x * 24.0 - TIME * 1.6, UV.y * 6.0))));
	float alpha = (1.0 - smoothstep(0.20, 0.95, age)) * pow(clamp(height_feather, 0.0, 1.0), 1.35) * broken;

	ALBEDO = mix(splash_color * vec3(0.55, 0.78, 0.88), splash_color, smoothstep(0.35, 0.92, height_feather));
	ROUGHNESS = 0.42;
	SPECULAR = 0.30;
	ALPHA = alpha * 0.58;
}
