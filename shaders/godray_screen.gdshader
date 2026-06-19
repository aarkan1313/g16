shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;

void vertex() {
	// Fullscreen quad in clip space (ignore camera transform). z=1 = far plane under Reverse-Z;
	// harmless with depth_test_disabled. (Godot issue #58337: use VERTEX.xy form, not vec4(VERTEX,1).)
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	// PASSTHROUGH for the Task-1 experiment: just show what the screen texture contains.
	// If we see sky + sun disc + clouds → hint_screen_texture works (cheap spatial path, Task 2A).
	// If sky/clouds are MISSING (black/garbage where sky should be) → need the SubViewport path (2B).
	ALBEDO = texture(screen_tex, SCREEN_UV).rgb;
}
