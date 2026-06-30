shader_type spatial;

render_mode blend_add, depth_draw_never, cull_disabled, unshaded;

uniform float intensity = 0.38;
uniform float caustic_scale = 4.5;
uniform float speed = 1.0;
uniform float distance_fade = 0.75;

varying vec3 v_world;


float caustic_pattern(vec2 p) {
	float a = sin(p.x * 7.7 + sin(p.y * 0.43 + TIME * 0.22 * speed) * 1.6 + TIME * 0.82 * speed);
	float b = sin((p.x * 0.62 + p.y * 1.14) * 8.9 - TIME * 1.05 * speed);
	float c = sin((p.x * -0.88 + p.y * 0.76) * 10.8 + TIME * 0.58 * speed);
	float d = sin((p.x + p.y) * 4.6 + sin(p.x * 1.9 - TIME * 0.34 * speed) * 1.2);
	float cellular = smoothstep(0.54, 0.93, (a + b + c + d) * 0.125 + 0.5);
	float filament = smoothstep(0.80, 0.98, sin(a * 1.7 + b * 1.2 + c * 0.8) * 0.5 + 0.5);
	return cellular * 0.68 + filament * 0.32;
}


void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}


void fragment() {
	vec2 p = v_world.xz / max(caustic_scale, 0.001);
	float caustic = caustic_pattern(p) + caustic_pattern(p * 1.63 + vec2(8.1, 2.3)) * 0.34;
	float radial = length(UV - vec2(0.5)) * 2.0;
	float fade_strength = clamp(distance_fade, 0.0, 1.0);
	float edge_fade = mix(1.0, 1.0 - smoothstep(0.66, 1.0, radial), fade_strength);
	float luma = caustic * intensity * edge_fade;
	ALBEDO = vec3(0.46, 0.82, 1.0) * luma;
	ALPHA = luma;
}
