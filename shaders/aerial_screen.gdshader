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
// HAZE / path-radiance floor (the energy-conserving fix for fade-to-BLACK). The froxel LUT's in-scatter is
// SUN-directional and caps at ~0.14, so looking ANTI-sun it is ≈0 — it cannot replace the surface light that
// `aer.a` (transmittance, down to ~0.47) extincts away, so distant terrain multiplied toward BLACK (the
// "anti-sun darkness/line"). Real aerial perspective replaces extincted surface light with PATH RADIANCE —
// the scattered sky/horizon glow along the ray — which is roughly isotropic (sky fill), not sun-only. So add
// `(1-aer.a)*aerial_haze`: as transmittance → 0 the pixel fades to the haze colour, never black. LINEAR space
// (screen_tex is pre-tonemap). Pushed per-frame from C# to track the time-of-day sky; this default is a calm
// midday horizon blue-grey. aerial_haze_str scales it (0 = old fade-to-black behaviour, for A/B).
uniform vec3  aerial_haze : source_color = vec3(0.20, 0.26, 0.34);
uniform float aerial_haze_str : hint_range(0.0, 2.0) = 1.0;
uniform bool  aerial_on = false;
// Reverse-Z (Godot 4.3+): the sky clears to depth EXACTLY 0.0; ALL geometry is > 0 (distant terrain ~1e-6..1e-4).
// Gate the sky OUT with d < eps (same constant as godray_screen's sky_depth_eps). If the sky gets haze, this
// gate is inverted — flip the test.
uniform float sky_depth_eps = 0.000001;
// ISOLATION DEBUG (--aerialdbg / key U): replace each geometry pixel with ONE channel of the aerial composite
// so the "anti-sun darkness/line" can be attributed to a single term in a single look. Sky still discards.
//   0 = normal · 1 = EXTINCTION darkening (red, 1-aer.a: how much the multiply dims this pixel) ·
//   2 = INSCATTER added back (green luminance of aer.rgb*strength) · 3 = FROXEL Z (grayscale ramp of zf:
//   a SMOOTH ramp = healthy aerial; a HARD BAND here = a froxel-slice/LUT discontinuity = the line's source) ·
//   4 = DISTANCE (grayscale of dist/aerial_far: confirms the line sits at a constant camera distance = world-locked).
uniform int debug_mode = 0;

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
	// extinction (col*T) + sun in-scatter + isotropic haze path-radiance (replaces the extincted light so
	// anti-sun distant terrain fades to the sky tint, not to black). The (1-aer.a) weight makes haze grow
	// exactly as extinction removes surface light → energy-conserving, no fade-to-black.
	vec3 outc = col * aer.a + aer.rgb * aerial_strength + (1.0 - aer.a) * aerial_haze * aerial_haze_str;

	// ISOLATION DEBUG: replace this geometry pixel with one channel (sky already discarded above).
	if (debug_mode == 1) {            // EXTINCTION darkening (red): 0 = untouched, 1 = fully dimmed to black.
		outc = vec3(1.0 - aer.a, 0.0, 0.0);
	} else if (debug_mode == 2) {     // INSCATTER added back (green): the energy aerial puts in.
		outc = vec3(0.0, dot(aer.rgb * aerial_strength, vec3(0.3333)), 0.0);
	} else if (debug_mode == 3) {     // FROXEL Z ramp: smooth = healthy; a hard band = slice/LUT discontinuity.
		outc = vec3(zf);
	} else if (debug_mode == 4) {     // DISTANCE ramp: a line at constant grey = world-locked at a fixed range.
		outc = vec3(clamp(dist / aerial_far, 0.0, 1.0));
	}

	// spatial shader: write ALBEDO/ALPHA (NOT COLOR). unshaded + blend_mix + ALPHA=1 → full replace with the
	// fogged pixel; screen_tex is pre-tonemap linear, so this composites in linear and tonemaps once after.
	ALBEDO = outc;
	ALPHA = 1.0;
}
