shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform vec4 particle_color : source_color = vec4(0.86, 0.96, 1.0, 0.5);


void fragment() {
	vec2 centered = UV * 2.0 - 1.0;
	float d = length(centered);
	float disc = 1.0 - smoothstep(0.48, 1.0, d);
	float core = 1.0 - smoothstep(0.0, 0.55, d);
	vec3 tint = mix(particle_color.rgb * vec3(0.62, 0.82, 0.90), particle_color.rgb, core);
	ALBEDO = tint;
	EMISSION = tint * 0.015;
	ALPHA = particle_color.a * disc;
}
