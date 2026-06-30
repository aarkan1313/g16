shader_type spatial;

render_mode cull_back, depth_draw_opaque;

uniform vec3 concrete_color : source_color = vec3(0.035, 0.075, 0.085);
uniform vec3 grout_color : source_color = vec3(0.012, 0.030, 0.038);
uniform float tile_count = 9.0;
uniform float line_width = 0.009;
uniform float line_visibility = 0.0;


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


mat2 rot2(float a) {
	float s = sin(a);
	float c = cos(a);
	return mat2(vec2(c, -s), vec2(s, c));
}


float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.52;
	mat2 r = rot2(0.67);
	for (int i = 0; i < 5; i++) {
		v += noise(p) * a;
		p = r * p * 2.03 + vec2(8.7, -4.1);
		a *= 0.48;
	}
	return v;
}


void fragment() {
	vec2 p = UV * max(tile_count, 1.0);
	float broad = fbm(rot2(0.19) * p * 0.72);
	float mid = fbm(rot2(-0.53) * p * 2.35 + vec2(12.4, 5.7));
	float fine = noise(p * 14.0 + vec2(3.2, 9.1));
	float shallow_mottle = (broad - 0.5) * 0.030 + (mid - 0.5) * 0.020 + (fine - 0.5) * 0.007;
	float soft_vein = smoothstep(0.68, 0.95, fbm(rot2(0.41) * p * 0.82 + vec2(2.0, -1.5))) * line_visibility * 0.22;
	vec3 floor_color = mix(concrete_color, grout_color, soft_vein);
	ALBEDO = clamp(floor_color + shallow_mottle, vec3(0.0), vec3(1.0));
	ROUGHNESS = 0.86;
	SPECULAR = 0.18;
}
