shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never;

// Screen-space god rays — GPU Gems 3 Ch.13 radial light-scattering, additive. A clip-space fullscreen
// quad reads the rendered frame + DEPTH and radial-blurs an OCCLUSION buffer outward from the sun's
// screen position, adding the bright fan of crepuscular beams back.
//
// OCCLUSION = DEPTH-based (the standard, robust choice — confirmed against SIsilicon's plugin + the
// cross-check 2026-06-19). Sky sits at the FAR plane; under Godot 4.3+ Reverse-Z the far plane is
// depth ≈ 0.0, near = 1.0. So "light reaches this pixel" = depth ≈ 0 (sky); near geometry = occluder.
// (Earlier we masked by luminance, which is the WRONG choice for a scene with clouds in the sky:
// bright clouds became fake light sources and dark cloud undersides became occluders — backwards.
// occ_mode lets us A/B: 0 = depth, 1 = luminance.)
//
// DITHER: a per-pixel random offset on the first sample breaks the banding that a fixed-step radial
// blur produces at low sample counts (also from the cross-check / SIsilicon).

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex  : hint_depth_texture;

uniform vec2  sun_screen_uv = vec2(0.5, 0.5);          // sun's projected screen position (UV), from C#
uniform float sun_gate : hint_range(0.0, 1.0) = 0.0;   // 0 = sun off-screen/behind camera, 1 = on-screen & ahead
uniform vec3  ray_tint : source_color = vec3(1.0, 0.95, 0.85);   // warm beam color
// Tuned for SHAFTS, not a glow (research 2026-06-19): high decay (long reach) + LOW density (short
// steps so rays don't all dredge the bright disc) + modest weight/exposure. High density + high
// exposure = the "flashlight blob". Shafts only appear where occluders carve the rays.
uniform float density   : hint_range(0.0, 1.0) = 0.45; // sample spread / step length (LOWER = longer beams)
uniform float decay     : hint_range(0.0, 1.0) = 0.97; // per-step attenuation (→1 = longer shafts)
uniform float weight    : hint_range(0.0, 0.3) = 0.05; // per-sample intensity (small — summed NUM_SAMPLES times)
uniform float exposure  : hint_range(0.0, 2.0) = 0.5;  // overall strength (UI "god ray strength")
uniform float dither    : hint_range(0.0, 1.0) = 1.0;  // sample-start jitter to break banding
// occ_mode: 0 = depth only (terrain/solid geometry; used by the synthetic test scene),
//           2 = HYBRID (real scene): depth (terrain) OR cloud. Clouds are drawn in the sky at
//               far-plane depth so depth can't see them; instead, a sky pixel that is DARKER than the
//               bright sky AND lies near the sun's screen position is treated as cloud occlusion.
//               Restricting the luminance test to the sun's vicinity avoids the global luminance
//               fragility (bright snow/terrain elsewhere can't seed false rays).
uniform int   occ_mode = 0;
uniform float cloud_lum : hint_range(0.0, 2.0) = 0.78;   // sky brighter than this = open sky; below = cloud
uniform float cloud_radius : hint_range(0.05, 1.5) = 0.6; // only treat darkness as cloud within this UV radius of the sun
uniform int   debug_mode = 0;          // 0 = normal; 1 = show occlusion mask; 2 = mark sun_screen_uv + gate

const int NUM_SAMPLES = 64;   // more samples → smooth continuous beams (GPU Gems uses 100)

void vertex() {
	// Clip-space fullscreen quad (ignore camera transform). z=1 = far plane under Reverse-Z; harmless
	// with depth_test_disabled. (Godot issue #58337: VERTEX.xy form, not vec4(VERTEX,1).)
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

// 1.0 = light reaches here (open sky), 0.0 = occluder (terrain, or cloud near the sun).
float occlusion(vec2 uv) {
	// Terrain / solid geometry: Reverse-Z → sky/far ≈ 0.0. Near geometry (d above epsilon) = occluder.
	float d = texture(depth_tex, uv).r;
	if (d >= 0.0005) { return 0.0; }       // solid occluder in front of the sky
	if (occ_mode != 2) { return 1.0; }     // depth-only mode (test scene): open sky = light

	// HYBRID cloud term (real scene): this is sky (far plane). A sky pixel DARKER than open sky, and
	// within cloud_radius of the sun, is cloud → occluder. Far from the sun we don't judge (avoids the
	// global luminance trap). smoothstep gives soft cloud edges.
	float distToSun = distance(uv, sun_screen_uv);
	if (distToSun > cloud_radius) { return 1.0; }
	vec3 c = texture(screen_tex, uv).rgb;
	float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
	float lit = smoothstep(cloud_lum - 0.12, cloud_lum, lum);   // 1 = open sky (lit), 0 = dark cloud
	return lit;
}

// cheap per-pixel hash for dither.
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

void fragment() {
	vec2 uv = SCREEN_UV;
	vec3 scene = texture(screen_tex, uv).rgb;

	// radial scatter: step from this pixel toward the sun's screen position, NUM_SAMPLES times.
	vec2 delta = (uv - sun_screen_uv) * (1.0 / float(NUM_SAMPLES) * density);
	float jitter = mix(1.0, hash(uv), dither);     // jittered start kills banding
	vec2 coord = uv - delta * jitter;
	float illum = 1.0;
	float scatter = 0.0;
	for (int i = 0; i < NUM_SAMPLES; i++) {
		scatter += occlusion(coord) * (illum * weight);
		illum *= decay;
		coord -= delta;
	}
	scatter *= exposure * sun_gate;

	// normal output: scene + ONLY the scattered contribution (don't re-add scene — avoids blow-out).
	vec3 outc = scene + ray_tint * scatter;

	// DEBUG overlays (no early-return — GDShader forbids it; select with mix/step instead).
	if (debug_mode == 1) {
		outc = vec3(occlusion(uv));                 // white = light passes, black = occluder
	} else if (debug_mode == 2) {
		float dd = distance(uv, sun_screen_uv);
		vec3 marked = mix(scene, vec3(0.0, 1.0, 0.0), sun_gate * 0.4);   // green tint by gate
		outc = (dd < 0.012) ? vec3(1.0, 0.0, 0.0) : marked;             // red cross at sun UV
	}
	ALBEDO = outc;
}
