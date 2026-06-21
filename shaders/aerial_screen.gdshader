shader_type spatial;
// AT-2 screen-space AERIAL PERSPECTIVE (physical distance haze). A clip-space fullscreen quad (mirrors
// GodRaysScreen) reads the rendered frame + DEPTH, reconstructs per-pixel world distance, samples the
// camera-aligned aerial froxel LUT (AtmosphereCompute, rgb in-scatter + a = transmittance), and composites
// color*T + inscatter on GEOMETRY only. The sky is gated out (it already carries AT-1 sky-view scatter —
// re-fogging it would double-count).
//
// blend_mix (NOT blend_add like godray): aerial must MULTIPLY the scene by transmittance (extinction) AND
// add inscatter — a replace, not a pure add. ALPHA=1 fully writes the fogged pixel; sky pixels discard
// (framebuffer untouched). fog_disabled is CRITICAL (same reason as godray_screen): this quad is a 3D mesh
// at the far plane, so the Environment's depth fog would otherwise tint it a flat grey across the screen.
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, fog_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex  : hint_depth_texture;          // no linear filter (point-sample depth, like godray)
uniform sampler3D aerial_tex : filter_linear;               // froxel: rgb in-scatter, a = transmittance
uniform mat4  inv_view_proj;                                // inverse(projection*view) — depth→world (godray convention)
uniform vec3  cam_world = vec3(0.0);                        // camera world pos (m)
uniform float aerial_far = 32000.0;                         // metres — MUST match the LUT's cam_pos_far.w
uniform float aerial_strength = 10.0;                       // in-scatter gain (matches atmo_exposure; LUT is radiance)
uniform bool  aerial_on = false;
// Reverse-Z (Godot 4.3+): the sky clears to depth EXACTLY 0.0; ALL geometry is > 0 (distant terrain ~1e-6..1e-4).
// Gate the sky OUT with d < eps (same constant as godray_screen's sky_depth_eps). If the sky gets haze, this
// gate is inverted — flip the test.
uniform float sky_depth_eps = 0.000001;

void vertex() {
	// Clip-space fullscreen quad (Godot issue #58337: VERTEX.xy form). z=1 = far plane under Reverse-Z;
	// harmless with depth_test_disabled. Covers the screen regardless of the node's world transform.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	float d = texture(depth_tex, uv).r;
	// Sky gate: far plane (no geometry) → leave the AT-1 sky untouched (no double-count).
	if (!aerial_on || d < sky_depth_eps) { ALPHA = 0.0; discard; }

	vec3 col = texture(screen_tex, uv).rgb;
	// Reconstruct world distance from depth (same reconstruction as godray_screen).
	vec4 ndc = vec4(uv * 2.0 - 1.0, d, 1.0);
	vec4 wp = inv_view_proj * ndc; wp /= wp.w;
	float dist = length(wp.xyz - cam_world);                 // metres
	// Froxel z = inverse of the LUT's quadratic near-bias (t = far * frac^2 ⇒ frac = sqrt(dist/far)).
	float zf = clamp(sqrt(clamp(dist / aerial_far, 0.0, 1.0)), 0.0, 1.0);
	vec4 aer = texture(aerial_tex, vec3(uv, zf));            // rgb in-scatter, a = transmittance
	vec3 outc = col * aer.a + aer.rgb * aerial_strength;     // extinction + in-scatter
	// spatial shader: write ALBEDO/ALPHA (NOT COLOR). unshaded + blend_mix + ALPHA=1 → full replace with the
	// fogged pixel; screen_tex is pre-tonemap linear, so this composites in linear and tonemaps once after.
	ALBEDO = outc;
	ALPHA = 1.0;
}
