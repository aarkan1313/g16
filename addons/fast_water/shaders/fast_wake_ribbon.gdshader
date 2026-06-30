shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform vec3 wake_color : source_color = vec3(0.92, 0.98, 1.0);
uniform float brightness = 0.75;


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
	float age = UV2.x;
	float along = UV.x;
	float tail = smoothstep(0.0, 0.14, along) * (1.0 - smoothstep(0.78, 1.0, along));
	float side = abs(UV.y * 2.0 - 1.0);
	float feather = 1.0 - smoothstep(0.62, 1.0, side);
	float broken = mix(0.30, 1.0, smoothstep(0.28, 0.88, noise(vec2(along * 26.0 - TIME * 1.2, age * 8.0))));
	float fade = pow(1.0 - age, 1.9) * tail * feather * broken;
	vec3 tint = mix(wake_color * vec3(0.52, 0.76, 0.84), wake_color, smoothstep(0.15, 0.82, 1.0 - age));
	ALBEDO = tint * brightness;
	EMISSION = tint * brightness * 0.010;
	ALPHA = fade * 0.065;
}
