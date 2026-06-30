shader_type spatial;

render_mode blend_mix, depth_prepass_alpha, cull_disabled, specular_schlick_ggx;

uniform vec3 sheet_color : source_color = vec3(0.58, 0.82, 0.92);
uniform vec3 foam_color : source_color = vec3(0.92, 0.98, 1.0);
uniform float alpha = 0.58;
uniform float flow_speed = 1.0;
uniform float noise_scale = 18.0;
uniform float breakup_strength = 0.36;
uniform float edge_feather = 0.08;
uniform float normal_brightness = 0.18;


float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}


float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}


float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.55;
	for (int i = 0; i < 4; i++) {
		v += noise(p) * a;
		p = p * 2.03 + vec2(7.1, -3.8);
		a *= 0.50;
	}
	return v;
}


void vertex() {
	float streak = fbm(vec2(UV.x * noise_scale, UV.y * 6.0 - TIME * flow_speed * 2.0));
	float edge = smoothstep(0.0, max(edge_feather, 0.001), UV.x) * (1.0 - smoothstep(1.0 - max(edge_feather, 0.001), 1.0, UV.x));
	VERTEX.x += (streak - 0.5) * breakup_strength * 0.08 * edge;
}


void fragment() {
	float fall = fract(UV.y * 7.0 - TIME * flow_speed * 2.6);
	float streak_a = fbm(vec2(UV.x * noise_scale, UV.y * 8.0 - TIME * flow_speed * 2.4));
	float streak_b = noise(vec2(UV.x * noise_scale * 2.2, fall * 8.0));
	float edge = smoothstep(0.0, max(edge_feather, 0.001), UV.x) * (1.0 - smoothstep(1.0 - max(edge_feather, 0.001), 1.0, UV.x));
	float lip_foam = 1.0 - smoothstep(0.0, 0.16, UV.y);
	float base_foam = smoothstep(0.70, 1.0, UV.y);
	float strands = smoothstep(0.36, 0.92, streak_a * 0.78 + streak_b * 0.22);
	float broken = mix(1.0, strands, breakup_strength);
	float sheet_alpha = alpha * edge * mix(0.46, 1.0, broken) * (0.72 + lip_foam * 0.28 + base_foam * 0.18);

	vec3 color = mix(sheet_color, foam_color, clamp(lip_foam * 0.55 + base_foam * 0.42 + strands * 0.20, 0.0, 1.0));
	ALBEDO = color;
	ROUGHNESS = 0.28;
	SPECULAR = 0.36;
	EMISSION = color * normal_brightness * (lip_foam + base_foam * 0.65);
	ALPHA = clamp(sheet_alpha, 0.0, 1.0);
}
