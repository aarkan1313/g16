shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform vec3 wake_color : source_color = vec3(0.93, 0.98, 1.0);
uniform float brightness = 0.65;
uniform float alpha = 0.18;


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


void fragment() {
	float along = smoothstep(0.0, 0.08, UV.x) * (1.0 - smoothstep(0.88, 1.0, UV.x));
	float width = sin(UV.y * 3.14159265);
	float feather = pow(clamp(width, 0.0, 1.0), 2.35);
	float streaks = noise(vec2(UV.x * 18.0 - TIME * 1.4, UV.y * 5.0));
	float breakup = mix(0.34, 1.0, smoothstep(0.26, 0.86, streaks));
	float core = smoothstep(0.46, 0.96, width);
	vec3 tint = mix(wake_color * vec3(0.50, 0.76, 0.84), wake_color, core);
	ALBEDO = tint * brightness;
	EMISSION = tint * brightness * 0.025;
	ALPHA = along * feather * breakup * alpha;
}
