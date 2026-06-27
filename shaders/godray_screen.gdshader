shader_type spatial;
// blend_add is ESSENTIAL: this pass must ADD beam light to the framebuffer, never REPLACE it. The old
// `ALBEDO = scene + rays` (opaque) overwrote every pixel with a re-sampled copy of hint_screen_texture,
// which double-processes the frame (tonemap/atmosphere applied twice) → a hazy "filtered world" veil over
// the terrain even where scatter is 0. With blend_add we output ONLY ray_tint*scatter, so non-beam pixels
// (all terrain, open sky) get +0 and the world is left EXACTLY as the scene rendered it.
// fog_disabled is CRITICAL: this quad is a 3D mesh at the far plane, so the Environment's depth fog
// (fog_enabled + fog_light_color, a blue-grey) gets applied to it — and with blend_add that fog color is
// ADDED uniformly across the whole screen → a flat haze/"filter" that no god-ray knob can change (it's
// the fog, not the scatter). Disabling fog on the quad removes it.
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_add, fog_disabled;

// Screen-space god rays — GPU Gems 3 Ch.13 radial light-scattering, additive. A clip-space fullscreen
// quad reads the rendered frame + DEPTH and radial-blurs an OCCLUSION buffer outward from the sun's
// screen position, adding the bright fan of crepuscular beams back.
//
// OCCLUSION = depth gates out terrain, then occ_mode 2 uses on-screen cloud luminance near the sun.
// Sky sits at the FAR plane; under Godot 4.3+ Reverse-Z the far plane is depth near 0.0, near = 1.0.
// So depth first answers "is this sky"; the luminance split then answers "is this sky pixel cloud or gap."
//
// DITHER: a per-pixel random offset on the first sample breaks the banding that a fixed-step radial
// blur produces at low sample counts (also from the cross-check / SIsilicon).

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex  : hint_depth_texture;

// Sky vs geometry threshold (Reverse-Z): the sky clears to depth EXACTLY 0.0; ALL geometry is > 0. Under
// reverse-Z even distant terrain (mountains km away) has a TINY depth (~1e-6..1e-4) — the old 0.0005 cutoff
// was LARGER than that whole range, so it misclassified distant terrain as sky and washed it (the "filtered
// world"). 1e-6 sits safely between sky (0.0) and the farthest terrain (~1e-6..1e-4 under this projection).
uniform float sky_depth_eps = 0.000001;
uniform vec2  sun_screen_uv = vec2(0.5, 0.5);          // sun's projected screen position (UV), from C#
uniform float sun_gate : hint_range(0.0, 1.0) = 0.0;   // 0 = sun off-screen/behind camera, 1 = on-screen & ahead
uniform vec3  ray_tint : source_color = vec3(1.0, 0.95, 0.85);   // warm beam color
// Tuned for SHAFTS, not a glow (research 2026-06-19): high decay (long reach) + LOW density (short
// steps so rays don't all dredge the bright disc) + modest weight/exposure. High density + high
// exposure = the "flashlight blob". Shafts only appear where occluders carve the rays.
uniform float density   : hint_range(0.0, 1.0) = 0.45; // sample spread / step length (LOWER = longer beams)
uniform float decay     : hint_range(0.0, 1.0) = 0.97; // per-step attenuation (→1 = longer shafts)
uniform float decay_step : hint_range(0.0, 1.0) = 0.9409; // decay^(64/32), pushed with decay to avoid per-pixel pow()
uniform float weight    : hint_range(0.0, 0.3) = 0.05; // per-sample intensity (normalized to 64-sample reference)
uniform float exposure  : hint_range(0.0, 2.0) = 0.5;  // overall strength (UI "god ray strength")
uniform float dither    : hint_range(0.0, 1.0) = 1.0;  // sample-start jitter to break banding
// TANGENTIAL OCCLUSION HIGH-PASS (the fix for BOTH the "filter" wash AND the "ring", cheaply & cleanly).
// The raw radial scatter is purely additive: every ray toward the sun accrues a large near-constant
// floor (~0.7) from its open-sky samples → a milky full-screen veil whose radial falloff reads as a
// ring. The defining truth: a real shaft is the bright GAP between cloud SHADOWS — its brightness comes
// from how much MORE open this ray is than the rays beside it. So at each march step we subtract the
// occlusion's TANGENTIAL local mean (occlusion ± a small step perpendicular to the sun direction) and
// accumulate only the positive excess: `max(occ − tangential_mean, 0)`. Over uniform sky occ == mean →
// 0 (NO veil, NO ring). Alongside a cloud band the open ray exceeds its mean → a smooth radial shaft.
// Because we subtract a SMOOTH mean (not detect edges) the shafts are smooth — no cloud-edge halo. A
// truly-open rays integrate to ~0 on their own (occ == mean), so no veil. highpass 0 = raw additive (old
// wash+ring) for A/B, 1 = clean shafts (default).
uniform float highpass : hint_range(0.0, 1.0) = 1.0;
uniform float gate_width : hint_range(0.004, 0.08) = 0.02;    // tangential offset of the high-pass mean (UV); ~half a shaft width
uniform float aspect = 1.7777778;   // viewport width/height — keeps the tangential step isotropic on a wide screen
// occ_mode: 0 = depth only (terrain/solid geometry; used by the synthetic test scene),
//           2 = HYBRID (real scene): depth (terrain) OR cloud. Clouds are drawn in the sky at
//               far-plane depth so depth can't see them; instead, a sky pixel that is DARKER than the
//               bright sky AND lies near the sun's screen position is treated as cloud occlusion.
//               Restricting the luminance test to the sun's vicinity avoids the global luminance
//               fragility (bright snow/terrain elsewhere can't seed false rays).
uniform int   occ_mode = 0;
uniform float cloud_lum : hint_range(0.0, 2.0) = 0.78;   // sky brighter than this = open sky; below = cloud
uniform float cloud_radius : hint_range(0.05, 1.5) = 0.6; // only treat darkness as cloud within this UV radius of the sun
uniform float radius_soft : hint_range(0.05, 0.8) = 0.4;   // reach falloff width (WIDE = gradual vignette, no visible edge)
uniform float cloud_soft  : hint_range(0.02, 0.5) = 0.18;  // cloud/sky luminance softness (wider = smoother, less patchy)
uniform bool  cloud_invert = false;   // occ_mode 2: false = bright cloud is the occluder (daylit, default); true = dark cloud (backlit)
uniform int   debug_mode = 0;          // 0 = normal; 1 = show occlusion mask; 2 = mark sun_screen_uv + gate

const int NUM_SAMPLES = 32;   // static loop for compiler optimization; normalized against prior 64-sample look
const float SAMPLE_SCALE = 2.0; // 64.0 / 32.0

void vertex() {
	// Clip-space fullscreen quad (ignore camera transform). z=1 = far plane under Reverse-Z; harmless
	// with depth_test_disabled. (Godot issue #58337: VERTEX.xy form, not vec4(VERTEX,1).)
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

// 1.0 = light reaches here (open sky), 0.0 = occluder (terrain, or cloud).
float occlusion(vec2 uv) {
	float d = texture(depth_tex, uv).r;
	bool isSky = d < sky_depth_eps;     // Reverse-Z: sky clears to EXACTLY 0.0; any geometry is > eps
	if (!isSky) { return 0.0; }         // solid geometry in front of the sky = hard occluder
	if (occ_mode == 0) { return 1.0; }  // depth-only (test scene): open sky = light

	// occ_mode == 2: luminance heuristic near the sun. CRITICAL: in a DAYLIT scene the clouds are BRIGHTER
	// than the blue sky, so the OCCLUDER is the BRIGHT pixel (cloud blocks the sun) and the clear sky is
	// OPEN — shafts stream through the GAPS. (The old code had this inverted: it treated bright clouds as
	// "open" and the dim blue sky as the occluder, so shafts smeared through the clouds AND the blue sky's
	// own brightness gradient became varying "occlusion" → the milky full-sky veil.) `cloud_invert` keeps
	// the old behaviour available for backlit-cloud scenes (sun behind cloud → cloud is the DARK pixel).
	// Only SKY pixels reach here (terrain returned 0 above), so the only bright things are clouds + the sky
	// gradient — no need for a distToSun cutoff (that hard `cloud_radius` boundary was the flat ring/top-line;
	// radial reach is now a smooth falloff on the final scatter instead). cloud_soft = how soft the cloud/sky
	// split is: a narrow band crushed soft cloud edges into a hard binary mask, and the radial blur smeared
	// those aliased blobs into the "patchy" look — a wider band makes occlusion a smooth gradient.
	vec3 c = texture(screen_tex, uv).rgb;
	float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
	float lit = smoothstep(cloud_lum - cloud_soft, cloud_lum + cloud_soft, lum);   // 1 = bright (cloud), 0 = clear sky
	return cloud_invert ? lit : (1.0 - lit);   // default: bright cloud = occluder (0), clear sky = open (1)
}

// cheap per-pixel hash for dither.
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

// One radial-scatter march: step from start_uv toward the sun NUM_SAMPLES times, accumulating
// occlusion with the GPU Gems decay/weight. Factored out so the high-pass can re-run it at rotated
// angles (the angular-mean reference) using the EXACT same accumulation — required for the clear-sky
// terms to cancel to zero.
float march_scatter(vec2 start_uv) {
	float invN = 1.0 / float(NUM_SAMPLES);
	vec2 delta = (start_uv - sun_screen_uv) * (invN * density);
	float jitter = mix(1.0, hash(start_uv), dither);   // jittered start kills banding
	vec2 coord = start_uv - delta * jitter;
	float illum = 1.0;
	float s = 0.0;
	for (int i = 0; i < NUM_SAMPLES; i++) {
		s += occlusion(coord) * (illum * weight * SAMPLE_SCALE);
		illum *= decay_step;
		coord -= delta;
	}
	return s;
}

// The tangential-high-pass march: smooth radial shafts with no DC veil and no edge halo. At each step
// toward the sun, subtract the occlusion's tangential local mean (perpendicular to the sun direction,
// aspect-corrected) and accumulate only the positive excess. Uniform sky → 0; alongside a cloud band →
// a smooth shaft. 3 occlusion taps/step (center + two tangential); runs on every sky pixel within sun_gate.
float march_beam(vec2 start_uv) {
	float invN = 1.0 / float(NUM_SAMPLES);
	vec2 delta = (start_uv - sun_screen_uv) * (invN * density);
	vec2 to_sun = sun_screen_uv - start_uv;
	float to_sun_len = length(to_sun);
	vec2 tang = (to_sun_len > 1e-4) ? vec2(-to_sun.y, to_sun.x) / to_sun_len : vec2(0.0, 1.0);
	vec2 toff = vec2(tang.x / aspect, tang.y) * gate_width;
	float jitter = mix(1.0, hash(start_uv), dither);
	vec2 coord = start_uv - delta * jitter;
	float illum = 1.0;
	float s = 0.0;
	for (int i = 0; i < NUM_SAMPLES; i++) {
		float oc = occlusion(coord);
		float om = 0.5 * (occlusion(coord + toff) + occlusion(coord - toff));   // tangential local mean
		s += max(oc - om, 0.0) * (illum * weight * SAMPLE_SCALE);              // positive excess only → smooth bright shaft
		illum *= decay_step;
		coord -= delta;
	}
	return s;
}

void fragment() {
	vec2 uv = SCREEN_UV;

	// God rays live in the SKY (cloud-gap contrast), so only march sky pixels — terrain stays clean with no
	// march cost. Skip open sky far from any cloud (no shaft there) → the perf win. highpass 1 = clean
	// tangential-high-pass shafts (default), 0 = raw additive (old wash+ring) for A/B.
	float scatter = 0.0;
	if (sun_gate > 0.002) {
		// Smooth radial REACH: rays are strongest near the sun and fade out gently with distance — a soft
		// vignette, never a hard ring. cloud_radius = reach, radius_soft = how gradual (wide → no visible edge).
		float reach = 1.0 - smoothstep(cloud_radius, cloud_radius + radius_soft, distance(uv, sun_screen_uv));
		if (reach > 0.001) {
			float d_here = texture(depth_tex, uv).r;                              // Reverse-Z: sky clears to 0.0
			bool isSky = d_here < sky_depth_eps;
			if (isSky) {
				float s = (highpass >= 0.999) ? march_beam(uv)
						: (highpass <= 0.001) ? march_scatter(uv)
						: mix(march_scatter(uv), march_beam(uv), highpass);
				scatter = s * exposure * sun_gate * reach;
			}
		}
	}
	// ADDITIVE (blend_add): output ONLY the beam light. Non-beam pixels get vec3(0) → framebuffer unchanged.
	vec3 outc = ray_tint * scatter;

	// DEBUG overlays. Under blend_add the framebuffer already holds the scene, so to REPLACE it with a
	// diagnostic we output (target − scene): scene + (target − scene) = target.
	if (debug_mode == 1) {
		vec3 scene = texture(screen_tex, uv).rgb;
		outc = vec3(occlusion(uv)) - scene;                 // white = light passes, black = occluder
	} else if (debug_mode == 2) {
		vec3 scene = texture(screen_tex, uv).rgb;
		float dd = distance(uv, sun_screen_uv);
		vec3 marked = mix(scene, vec3(0.0, 1.0, 0.0), sun_gate * 0.4);   // green tint by gate
		vec3 target = (dd < 0.012) ? vec3(1.0, 0.0, 0.0) : marked;       // red cross at sun UV
		outc = target - scene;
	} else if (debug_mode == 3) {
		// isSky gate: green = sky (god rays allowed here), red = solid geometry (excluded). Confirms the
		// depth gate correctly excludes terrain. (blend_add: output target−scene so scene+that = target.)
		vec3 scene = texture(screen_tex, uv).rgb;
		float d_dbg = texture(depth_tex, uv).r;
		vec3 target = (d_dbg < sky_depth_eps) ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
		outc = target - scene;
	} else if (debug_mode == 5) {
		// SCATTER VISUALIZER: the god-ray contribution alone (amplified) on black — shows where shafts land.
		vec3 scene = texture(screen_tex, uv).rgb;
		outc = vec3(scatter * 8.0) - scene;
	}
	ALBEDO = outc;
}
