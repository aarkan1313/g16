shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform vec3 bubble_tint : source_color = vec3(0.72, 0.92, 1.0);
uniform float rim_power = 2.6;
uniform float alpha = 0.42;


void fragment() {
	vec2 uv = UV * 2.0 - 1.0;
	float r = length(uv);
	float circle = 1.0 - smoothstep(0.78, 1.0, r);
	float rim = smoothstep(0.35, 0.96, r);
	float sparkle = pow(max(0.0, 1.0 - distance(uv, vec2(-0.28, -0.32)) * 3.0), 6.0);

	ALBEDO = bubble_tint + sparkle * 0.8;
	ALPHA = circle * (pow(rim, rim_power) * alpha + sparkle * 0.35);
}
