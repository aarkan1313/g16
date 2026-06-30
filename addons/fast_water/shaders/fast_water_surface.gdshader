shader_type spatial;

render_mode blend_mix, depth_draw_never, cull_back, specular_schlick_ggx;

const int MAX_HERO_RIPPLES = 16;

uniform vec3 shallow_color : source_color = vec3(0.025, 0.22, 0.30);
uniform vec3 deep_color : source_color = vec3(0.0, 0.040, 0.095);
uniform vec3 foam_color : source_color = vec3(0.96, 0.98, 1.0);
uniform float water_alpha = 0.965;

uniform float wave_height = 0.045;
uniform float wave_frequency = 0.30;
uniform float wave_speed = 1.25;
// Optional Gerstner mode: adds horizontal displacement so crests peak and troughs
// broaden (real swell shape) instead of pure vertical sine. Default off keeps the
// proven look; the matching CPU height query (sample_wave_height) inverts it so
// buoyancy stays aligned. Vertical term is unchanged (== base_wave_height).
uniform bool gerstner_enabled = false;
uniform float gerstner_choppiness = 0.0;
uniform float normal_strength = 1.45;
uniform float micro_normal_strength = 0.82;
uniform float micro_normal_scale = 13.0;
uniform float fresnel_strength = 0.45;
uniform float refraction_strength = 0.0;
uniform float refracted_scene_strength = 0.18;
uniform vec3 reflection_color : source_color = vec3(0.10, 0.25, 0.36);
uniform float reflection_strength = 0.12;
uniform float grazing_reflection_strength = 0.32;
uniform float wake_highlight_strength = 1.15;
uniform float surface_gloss = 0.62;
uniform float detail_normal_strength = 0.0;
uniform float detail_normal_scale = 32.0;
uniform float foam_intensity = 1.0;
uniform float shoreline_foam_strength = 1.0;
uniform float wake_foam_strength = 1.0;
uniform float foam_breakup_strength = 0.0;
uniform float foam_noise_scale = 3.0;
// Distant/background ocean tiers have no shoreline, wake, flow, or foam-field
// sources, so the 3-call foam-noise stack below evaluates to ~0 yet still costs
// 3 organic_fbm() per pixel (the dominant fragment cost over the large far mesh).
// FastWaterOcean turns this off on the far tier to make far water actually cheap;
// whitecaps use a separate noise pair and are unaffected.
uniform bool foam_detail_enabled = true;
uniform float storm_whitecap_strength = 0.0;
uniform float storm_whitecap_scale = 0.32;
uniform float depth_absorption_strength = 0.85;
uniform float absorption_density = 0.28;
uniform vec3 sun_glint_color : source_color = vec3(1.0, 0.96, 0.82);
uniform float sun_glint_strength = 0.0;
uniform float sun_glint_sharpness = 28.0;
uniform vec2 sun_glint_direction = vec2(0.9, 0.25);
uniform float planar_reflection_grazing_power = 3.25;
uniform float planar_reflection_max_mix = 0.48;
uniform float final_color_gain = 1.08;
uniform vec3 horizon_color : source_color = vec3(0.34, 0.56, 0.66);
uniform float horizon_fade_strength = 0.0;
uniform float horizon_fade_start_m = 280.0;
uniform float horizon_fade_end_m = 920.0;
uniform float mesh_edge_fade_width = 0.0;
// Subtracted from world XZ before the procedural wave/normal/foam/whitecap math so a
// host with a floating origin can keep the sampled coordinate small and avoid fp32
// shimmer far from the world origin. Wake/flow/foam MAPS and hero ripples keep true
// world coords (they carry their own origins / are near-camera). Must be identical on
// the near and far ocean tiers or the macro swell will seam at the tier boundary.
// Default zero = sample at true world position (unchanged behaviour).
uniform vec2 wave_sample_offset = vec2(0.0);
// Distant specular LOD: distant water otherwise sparkle-aliases as the per-pixel
// wave normal undersamples. Past specular_lod_start_m the normal is flattened toward
// flat and roughness is floored up so far water reads as calm haze, not noise.
// Defaults leave near/hero water (< ~90 m) untouched.
uniform float specular_lod_start_m = 90.0;
uniform float specular_lod_end_m = 700.0;
uniform float distant_specular_roughness = 0.42;
uniform float distant_normal_flatten = 0.45;

uniform float foam_depth_m = 0.16;
uniform float deep_depth_m = 6.0;

uniform vec4 hero_ripples[MAX_HERO_RIPPLES];
uniform vec4 hero_ripple_data[MAX_HERO_RIPPLES];
uniform int hero_ripple_count = 0;
// Slope gain for ripple rings. hero_ripple_normal already carries an 8x gain from
// d/ddist of sin(phi*8), multiplied by per-ripple strength (~0.3), so its raw peak
// is ~2.4. The rest of the surface (base waves + micro detail) only reaches an nxy
// of ~0.13, so this MUST stay near that or the facet tilts toward horizontal,
// fresnel saturates, the sun glint blows out near-white, and the ripple band reads
// as a flat grey "broken shader" smear. 0.06 keeps a ripple's peak nxy (~0.14) on
// par with the surface so the rings read as gentle lit water, not a glitch.
uniform float ripple_normal_strength = 0.06;
// Script-driven clock (FastWaterSurface._ripple_clock). Ripple ages are measured
// against this, NOT the shader's TIME, so spawn stamps and current time share one
// epoch. Mixing TIME with a Time.get_ticks_msec() stamp desynced them and the
// rings rendered at the wrong radius / as a static grey smear.
uniform float hero_ripple_time = 0.0;

uniform sampler2D wake_map : repeat_disable, filter_linear;
uniform vec2 wake_map_origin_xz = vec2(0.0);
uniform float wake_map_world_size = 160.0;
uniform float wake_map_strength = 0.35;

uniform sampler2D flow_field_map : repeat_disable, filter_linear;
uniform bool flow_field_enabled = false;
uniform vec2 flow_field_origin_xz = vec2(0.0);
uniform float flow_field_world_size = 160.0;
uniform float flow_field_strength = 1.0;
uniform float flow_foam_strength = 1.0;
uniform float flow_normal_strength = 1.0;
uniform float flow_advection_strength = 0.28;
uniform float flow_encode_scale_mps = 8.0;

uniform sampler2D foam_field_map : repeat_disable, filter_linear;
uniform bool foam_field_enabled = false;
uniform vec2 foam_field_origin_xz = vec2(0.0);
uniform float foam_field_world_size = 160.0;
uniform float foam_field_strength = 1.0;

uniform sampler2D planar_reflection_texture : repeat_disable, filter_linear;
uniform bool planar_reflection_enabled = false;
uniform bool planar_reflection_flip_y = false;
uniform float planar_reflection_strength = 0.0;
uniform float planar_reflection_distortion = 0.018;
uniform float planar_reflection_softness = 0.004;
uniform mat4 planar_reflection_view_projection;

uniform sampler2D depth_texture : hint_depth_texture, repeat_disable, filter_nearest;
uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;

uniform int debug_view = 0;

varying vec3 v_world_pos;
varying float v_wave_height;


float wave_layer(vec2 p, vec2 dir, float freq, float speed, float amp) {
	return sin(dot(p, normalize(dir)) * freq + TIME * speed) * amp;
}


vec2 wave_slope_layer(vec2 p, vec2 dir, float freq, float speed, float amp) {
	vec2 ndir = normalize(dir);
	float phase = dot(p, ndir) * freq + TIME * speed;
	return ndir * cos(phase) * freq * amp;
}


float hash21(vec2 p) {
	// Dave Hoskins hash: well-distributed, no visible lattice/diagonal
	// correlation. The previous fract(p.x*p.y) hash produced a grid-aligned
	// structure that, once thresholded by glint/foam, read as a crystalline web.
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}


float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}


mat2 rot2(float a) {
	float s = sin(a);
	float c = cos(a);
	return mat2(vec2(c, -s), vec2(s, c));
}


float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 3; i++) {
		v += value_noise(p) * a;
		p = p * 2.03 + vec2(13.1, 7.7);
		a *= 0.5;
	}
	return v;
}


float organic_fbm(vec2 p) {
	float v = 0.0;
	float a = 0.52;
	mat2 r = rot2(0.71);
	for (int i = 0; i < 3; i++) {
		v += value_noise(p) * a;
		p = r * p * 2.07 + vec2(17.31, -9.17);
		a *= 0.48;
	}
	return v;
}


vec2 organic_gradient(vec2 p, float e) {
	float l = organic_fbm(p - vec2(e, 0.0));
	float r = organic_fbm(p + vec2(e, 0.0));
	float d = organic_fbm(p - vec2(0.0, e));
	float u = organic_fbm(p + vec2(0.0, e));
	return vec2(r - l, u - d) / max(e * 2.0, 0.0001);
}


float base_wave_height(vec2 xz) {
	float h = 0.0;
	h += wave_layer(xz, vec2(0.82, 0.34), wave_frequency * 0.58, wave_speed * 0.45, wave_height * 0.82);
	h += wave_layer(xz + vec2(3.1, -1.7), vec2(-0.48, 0.88), wave_frequency * 0.92, wave_speed * 0.62, wave_height * 0.40);
	h += wave_layer(xz + vec2(-5.7, 2.4), vec2(0.16, 0.99), wave_frequency * 1.45, wave_speed * 0.95, wave_height * 0.22);
	h += wave_layer(xz + vec2(1.8, 4.9), vec2(-0.94, -0.19), wave_frequency * 2.10, wave_speed * 1.35, wave_height * 0.11);
	h += wave_layer(xz + vec2(-2.6, -3.7), vec2(0.55, -0.83), wave_frequency * 2.85, wave_speed * 1.65, wave_height * 0.055);
	return h;
}


// Gerstner horizontal displacement, layer-for-layer matched to base_wave_height (same
// directions / freq / speed / amp / offsets) so the horizontal and vertical terms
// share phase. Mirrored by FastWaterSurface._gerstner_horizontal for the height query.
vec2 gerstner_layer(vec2 p, vec2 dir, float freq, float speed, float amp) {
	vec2 nd = normalize(dir);
	return nd * amp * cos(dot(p, nd) * freq + TIME * speed);
}


vec2 gerstner_horizontal(vec2 xz) {
	vec2 d = vec2(0.0);
	d += gerstner_layer(xz, vec2(0.82, 0.34), wave_frequency * 0.58, wave_speed * 0.45, wave_height * 0.82);
	d += gerstner_layer(xz + vec2(3.1, -1.7), vec2(-0.48, 0.88), wave_frequency * 0.92, wave_speed * 0.62, wave_height * 0.40);
	d += gerstner_layer(xz + vec2(-5.7, 2.4), vec2(0.16, 0.99), wave_frequency * 1.45, wave_speed * 0.95, wave_height * 0.22);
	d += gerstner_layer(xz + vec2(1.8, 4.9), vec2(-0.94, -0.19), wave_frequency * 2.10, wave_speed * 1.35, wave_height * 0.11);
	d += gerstner_layer(xz + vec2(-2.6, -3.7), vec2(0.55, -0.83), wave_frequency * 2.85, wave_speed * 1.65, wave_height * 0.055);
	return d * gerstner_choppiness;
}


float hero_ripple_foam(vec2 xz) {
	float foam = 0.0;

	for (int i = 0; i < MAX_HERO_RIPPLES; i++) {
		if (i >= hero_ripple_count) {
			break;
		}

		vec4 r = hero_ripples[i];
		vec4 d = hero_ripple_data[i];
		float age = hero_ripple_time - r.z;
		if (age < 0.0 || age > d.z || r.w <= 0.0) {
			continue;
		}

		float dist_to_center = distance(xz, r.xy);
		float ring_pos = age * d.y;
		float ring_width = max(d.x * 0.65, 0.04);
		float ring = exp(-pow((dist_to_center - ring_pos) / ring_width, 2.0));
		float decay = 1.0 - smoothstep(0.0, d.z, age);
		foam = max(foam, ring * decay * clamp(r.w, 0.0, 1.0));
	}

	return foam;
}


// Analytic xy slope of the expanding rings so they actually catch light. Without
// this the rings only displace geometry (under-sampled by the mesh) and stay
// invisible. Returns the radial height-gradient summed over active ripples.
vec2 hero_ripple_normal(vec2 xz) {
	vec2 grad = vec2(0.0);

	for (int i = 0; i < MAX_HERO_RIPPLES; i++) {
		if (i >= hero_ripple_count) {
			break;
		}

		vec4 r = hero_ripples[i];
		vec4 d = hero_ripple_data[i];
		float age = hero_ripple_time - r.z;
		if (age < 0.0 || age > d.z || r.w <= 0.0) {
			continue;
		}

		vec2 delta = xz - r.xy;
		float dist = length(delta) + 1e-4;
		float ring_pos = age * d.y;
		float ring_width = max(d.x, 0.05);
		// Only fragments near the (thin) ring contribute; skip the rest before the
		// trig/exp work so the open surface stays cheap.
		if (abs(dist - ring_pos) > 3.0 * ring_width) {
			continue;
		}
		float xn = (dist - ring_pos) / ring_width;
		float ring = exp(-xn * xn);
		float decay = 1.0 - smoothstep(0.0, d.z, age);
		float phi = (dist - ring_pos) * 8.0;
		// d/ddist of [ sin(phi) * ring ]
		float dh = (cos(phi) * 8.0 * ring + sin(phi) * ring * (-2.0 * xn / ring_width)) * decay * r.w;
		grad += (delta / dist) * dh;
	}

	return grad;
}


vec2 wake_uv_from_world(vec2 xz) {
	return (xz - wake_map_origin_xz) / max(wake_map_world_size, 0.001) + vec2(0.5);
}


// The finite wake map covers a fixed square world region. A hard cutoff at the
// UV border draws a visible rectangle on the surface ("weird geometric shape").
// Feather the wake contribution to its neutral value across the outer band.
float map_edge_fade(vec2 uv) {
	vec2 d = min(uv, vec2(1.0) - uv);
	return smoothstep(0.0, 0.07, min(d.x, d.y));
}


vec4 sample_wake_map(vec2 xz) {
	vec2 uv = wake_uv_from_world(xz);
	vec4 neutral = vec4(0.5, 0.0, 0.5, 0.5);
	float fade = map_edge_fade(uv);
	if (fade <= 0.0) {
		return neutral;
	}
	return mix(neutral, texture(wake_map, clamp(uv, vec2(0.0), vec2(1.0))), fade);
}


vec2 flow_uv_from_world(vec2 xz) {
	return (xz - flow_field_origin_xz) / max(flow_field_world_size, 0.001) + vec2(0.5);
}


vec2 foam_field_uv_from_world(vec2 xz) {
	return (xz - foam_field_origin_xz) / max(foam_field_world_size, 0.001) + vec2(0.5);
}


vec4 sample_flow_field(vec2 xz) {
	if (!flow_field_enabled) {
		return vec4(0.5, 0.5, 0.0, 0.0);
	}
	vec2 uv = flow_uv_from_world(xz);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
		return vec4(0.5, 0.5, 0.0, 0.0);
	}
	return texture(flow_field_map, uv);
}


vec4 sample_foam_field(vec2 xz) {
	if (!foam_field_enabled) {
		return vec4(0.0);
	}
	vec2 uv = foam_field_uv_from_world(xz);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
		return vec4(0.0);
	}
	return texture(foam_field_map, uv);
}


vec2 decode_flow(vec4 flow_sample) {
	return (flow_sample.rg * 2.0 - vec2(1.0)) * flow_encode_scale_mps * flow_sample.a * flow_field_strength;
}


float linear_depth(mat4 inv_projection_matrix, float raw_depth, vec2 screen_uv) {
	vec4 view = inv_projection_matrix * vec4(screen_uv * 2.0 - 1.0, raw_depth, 1.0);
	view.xyz /= view.w;
	return -view.z;
}


vec2 wave_normal_xy(vec2 xz) {
	vec2 slope = vec2(0.0);
	slope += wave_slope_layer(xz, vec2(0.82, 0.34), wave_frequency * 0.58, wave_speed * 0.45, wave_height * 0.82);
	slope += wave_slope_layer(xz + vec2(3.1, -1.7), vec2(-0.48, 0.88), wave_frequency * 0.92, wave_speed * 0.62, wave_height * 0.40);
	slope += wave_slope_layer(xz + vec2(-5.7, 2.4), vec2(0.16, 0.99), wave_frequency * 1.45, wave_speed * 0.95, wave_height * 0.22);
	slope += wave_slope_layer(xz + vec2(1.8, 4.9), vec2(-0.94, -0.19), wave_frequency * 2.10, wave_speed * 1.35, wave_height * 0.11);
	slope += wave_slope_layer(xz + vec2(-2.6, -3.7), vec2(0.55, -0.83), wave_frequency * 2.85, wave_speed * 1.65, wave_height * 0.055);
	return -slope * normal_strength * 3.6;
}


vec2 micro_normal_xy(vec2 xz) {
	vec2 p = xz * max(micro_normal_scale * 0.32, 0.001);
	vec2 n = vec2(0.0);
	n.x += sin(dot(p, vec2(0.94, 0.33)) + TIME * 2.10) * 0.026;
	n.y += cos(dot(p, vec2(-0.27, 0.96)) - TIME * 1.74) * 0.024;
	n += vec2(
		sin(dot(p, vec2(0.66, -0.75)) - TIME * 2.52),
		cos(dot(p, vec2(0.78, 0.62)) + TIME * 2.78)
	) * 0.014;
	n += organic_gradient(rot2(0.38) * p + vec2(TIME * 0.14, -TIME * 0.09), 0.085) * 0.020;
	return n * micro_normal_strength;
}


vec2 detail_normal_xy(vec2 xz) {
	vec2 p = xz * max(detail_normal_scale * 0.26, 0.001);
	vec2 n = vec2(0.0);
	n += organic_gradient(rot2(-0.44) * p + vec2(-TIME * 0.22, TIME * 0.18), 0.070) * 0.014;
	n.x += sin(dot(p, vec2(1.0, 0.17)) + TIME * 3.4) * 0.012;
	n.y += cos(dot(p, vec2(-0.12, 1.0)) - TIME * 2.9) * 0.011;
	n += vec2(
		sin(dot(p, vec2(0.71, 0.70)) + TIME * 4.1),
		cos(dot(p, vec2(-0.74, 0.67)) - TIME * 3.6)
	) * 0.007;
	return n * detail_normal_strength;
}


vec4 sample_planar_reflection(vec2 uv, vec2 normal_offset) {
	if (planar_reflection_softness <= 0.001) {
		return texture(planar_reflection_texture, clamp(uv, vec2(0.001), vec2(0.999)));
	}
	vec2 axis = normal_offset;
	if (length(axis) < 0.0001) {
		axis = vec2(1.0, 0.0);
	}
	axis = normalize(axis) * planar_reflection_softness;
	vec2 side = vec2(-axis.y, axis.x) * 0.72;
	vec4 c = texture(planar_reflection_texture, clamp(uv, vec2(0.001), vec2(0.999))) * 0.40;
	c += texture(planar_reflection_texture, clamp(uv + axis, vec2(0.001), vec2(0.999))) * 0.18;
	c += texture(planar_reflection_texture, clamp(uv - axis, vec2(0.001), vec2(0.999))) * 0.18;
	c += texture(planar_reflection_texture, clamp(uv + side, vec2(0.001), vec2(0.999))) * 0.12;
	c += texture(planar_reflection_texture, clamp(uv - side, vec2(0.001), vec2(0.999))) * 0.12;
	return c;
}


vec3 project_planar_reflection_uv(vec3 world_pos, vec2 normal_offset) {
	vec4 clip = planar_reflection_view_projection * vec4(world_pos, 1.0);
	if (clip.w <= 0.0001) {
		return vec3(0.0, 0.0, 0.0);
	}

	vec3 ndc = clip.xyz / clip.w;
	vec2 uv = ndc.xy * 0.5 + 0.5;
	uv += normal_offset * planar_reflection_distortion;
	if (planar_reflection_flip_y) {
		uv.y = 1.0 - uv.y;
	}

	float edge_distance = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
	float edge_fade = smoothstep(0.025, 0.145, edge_distance);
	float depth_fade = 1.0 - smoothstep(0.98, 1.05, abs(ndc.z));
	return vec3(uv, edge_fade * depth_fade);
}


void vertex() {
	vec3 world_before = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec2 xz = world_before.xz;
	vec4 flow_sample = sample_flow_field(xz);
	vec2 flow_vec = decode_flow(flow_sample);
	vec2 advected_xz = (xz - wave_sample_offset) - flow_vec * TIME * flow_advection_strength;
	vec4 foam_field = sample_foam_field(xz);

	float h = base_wave_height(advected_xz);
	// Moving-object ripples are normal/foam-only in fragment(). Sampling thin
	// rings as vertex displacement on this mesh can punch hard triangle-shaped
	// depth holes against submerged geometry.
	h += flow_sample.b * flow_sample.a * flow_foam_strength * wave_height * 0.08;
	h += foam_field.r * foam_field.a * foam_field_strength * wave_height * 0.04;

	vec4 wake = sample_wake_map(xz);
	h += (wake.r - 0.5) * wake_map_strength * 0.22;

	if (gerstner_enabled) {
		vec2 g = gerstner_horizontal(advected_xz);
		VERTEX.x += g.x;
		VERTEX.z += g.y;
	}
	VERTEX.y += h;
	v_wave_height = h;
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}


void fragment() {
	float scene_raw = texture(depth_texture, SCREEN_UV).r;
	float scene_depth = linear_depth(INV_PROJECTION_MATRIX, scene_raw, SCREEN_UV);
	float water_depth = linear_depth(INV_PROJECTION_MATRIX, FRAGCOORD.z, SCREEN_UV);
	float thickness = max(scene_depth - water_depth, 0.0);

	vec4 wake = sample_wake_map(v_world_pos.xz);
	vec4 flow_sample = sample_flow_field(v_world_pos.xz);
	vec4 foam_field = sample_foam_field(v_world_pos.xz);
	vec2 flow_vec = decode_flow(flow_sample);
	vec2 flow_dir = normalize(flow_vec + vec2(0.001));
	vec2 sample_xz = v_world_pos.xz - wave_sample_offset;
	vec2 advected_xz = sample_xz - flow_vec * TIME * flow_advection_strength;

	vec2 nxy = wave_normal_xy(advected_xz);
	if (micro_normal_strength > 0.001) {
		nxy += micro_normal_xy(advected_xz);
	}
	if (detail_normal_strength > 0.001) {
		nxy += detail_normal_xy(advected_xz);
	}
	// Expanding ring ripples drive the surface normal analytically (full-res), so
	// the rings catch light and read as a clean disturbance trailing a moving float.
	nxy += -hero_ripple_normal(v_world_pos.xz) * ripple_normal_strength;
	nxy += flow_dir * flow_sample.b * flow_sample.a * flow_normal_strength * 0.035;

	float depth_blend = smoothstep(0.05, deep_depth_m, thickness);
	vec3 water_color = mix(shallow_color, deep_color, depth_blend);
	float absorption_amount = 1.0 - exp(-thickness * max(depth_absorption_strength, 0.0) * max(absorption_density, 0.0));
	water_color = mix(water_color, deep_color * 0.82, clamp(absorption_amount * 0.58, 0.0, 1.0));

	vec2 refract_uv = SCREEN_UV + nxy * refraction_strength;
	vec3 refracted = texture(screen_texture, refract_uv).rgb;
	vec3 refracted_tint = mix(refracted, deep_color, clamp(absorption_amount, 0.0, 0.86));

	vec2 wake_flow = wake.ba - vec2(0.5);
	vec2 combined_flow = normalize(wake_flow + flow_dir * flow_sample.a * 0.45 + vec2(0.001));
	float foam_noise = 0.0;
	float foam_thread_noise = 0.0;
	float foam_erosion_noise = 0.0;
	if (foam_detail_enabled) {
		foam_noise = organic_fbm(rot2(0.31) * advected_xz * foam_noise_scale + vec2(TIME * 0.08, -TIME * 0.05));
		foam_thread_noise = organic_fbm(advected_xz * foam_noise_scale * 2.15 + combined_flow * TIME * 0.18);
		foam_erosion_noise = organic_fbm(rot2(-0.58) * advected_xz * foam_noise_scale * 3.25 - combined_flow * TIME * 0.12);
	}
	float breakup_amount = clamp(foam_breakup_strength, 0.0, 1.0);
	float foam_breakup = mix(1.0, smoothstep(0.34, 0.90, foam_noise), breakup_amount);
	float foam_thread_mask = smoothstep(0.42, 0.96, foam_thread_noise);
	float foam_lace = mix(1.0, mix(0.12, 1.08, foam_thread_mask * smoothstep(0.24, 0.84, foam_erosion_noise)), breakup_amount);
	float foam_threads = mix(0.10, 1.0, foam_thread_mask);
	float wave_steepness = clamp(length(nxy) * 0.17 + abs(v_wave_height) * 3.2, 0.0, 1.0);
	// Whitecaps cost two fbm samples per pixel; skip them entirely when the
	// effect is disabled (calm pools/lakes) instead of multiplying by zero.
	float storm_whitecap = 0.0;
	if (storm_whitecap_strength > 0.001) {
		vec2 whitecap_dir = normalize(sun_glint_direction + vec2(0.001));
		float whitecap_noise = organic_fbm(sample_xz * max(storm_whitecap_scale, 0.001) + whitecap_dir * TIME * (0.18 + storm_whitecap_strength * 0.16));
		float whitecap_threads = organic_fbm(rot2(0.73) * sample_xz * max(storm_whitecap_scale * 2.55, 0.001) - vec2(-whitecap_dir.y, whitecap_dir.x) * TIME * 0.11);
		storm_whitecap = smoothstep(0.58, 0.94, whitecap_noise) * smoothstep(0.46, 0.92, whitecap_threads) * smoothstep(0.05, 0.34, wave_steepness) * storm_whitecap_strength;
	}
	float edge_lace = mix(1.0, mix(0.45, 1.0, smoothstep(0.38, 0.86, foam_noise)), breakup_amount);
	float edge_foam = (1.0 - smoothstep(0.02, foam_depth_m, thickness)) * 0.19 * foam_breakup * edge_lace * shoreline_foam_strength;
	float wake_foam = smoothstep(0.05, 0.45, wake.g) * mix(0.65, 1.0, foam_breakup * foam_thread_mask) * wake_foam_strength;
	float flow_source = pow(smoothstep(0.08, 0.98, flow_sample.b), 1.22);
	float persistent_source = pow(smoothstep(0.08, 0.98, foam_field.r), 1.16);
	float flow_foam = flow_source * flow_sample.a * flow_foam_strength * foam_breakup * foam_threads * foam_lace;
	float persistent_foam = persistent_source * foam_field.a * foam_field_strength * foam_breakup * foam_threads * foam_lace;
	// Keep ripple foam light: a moving float should read as clean ring deformation
	// catching the sky (driven by hero_ripple_normal), not a grey foam smear. Only
	// the strongest ring crests deposit a touch of foam.
	float ripple_foam = smoothstep(0.55, 1.0, hero_ripple_foam(v_world_pos.xz)) * 0.5;
	// Ambient foam (shoreline, whitecap, flow/persistent fields, ripples) is scaled
	// by the global foam_intensity, which calm profiles keep low. The directional
	// WAKE foam must NOT be capped by that, or a moving object leaves only a faint
	// blue-grey smudge instead of a readable white trail -- composite it at full
	// strength so it reads without making the whole surface foamy.
	float ambient_foam = max(max(edge_foam, storm_whitecap), max(persistent_foam, max(flow_foam, ripple_foam))) * foam_intensity;
	float foam = clamp(max(ambient_foam, wake_foam), 0.0, 1.0);
	float foam_feather = smoothstep(0.06, 0.74, foam);
	float foam_core = smoothstep(0.60, 0.98, foam);

	float specular_lod = clamp(smoothstep(specular_lod_start_m, specular_lod_end_m, distance(CAMERA_POSITION_WORLD.xz, v_world_pos.xz)), 0.0, 1.0);
	vec2 foam_normal_xy = mix(nxy, nxy * 0.22, foam_feather * 0.74);
	// Flatten the surface normal with distance to stop far-water specular shimmer.
	foam_normal_xy *= mix(1.0, 1.0 - distant_normal_flatten, specular_lod);
	NORMAL_MAP = normalize(vec3(foam_normal_xy, 1.0)) * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = clamp(normal_strength, 0.0, 2.0);

	vec3 pseudo_normal = normalize(vec3(-foam_normal_xy.x, 1.0, -foam_normal_xy.y));
	vec3 view_world = normalize(CAMERA_POSITION_WORLD - v_world_pos);
	float view_dot = clamp(dot(pseudo_normal, view_world), 0.0, 1.0);
	float fresnel = pow(1.0 - view_dot, 4.0);
	float soft_fresnel = pow(1.0 - view_dot, 2.0);
	float scene_visibility = clamp((1.0 - depth_blend) * refracted_scene_strength * (1.0 - soft_fresnel * 0.70), 0.0, 0.42);
	water_color = mix(water_color, refracted_tint, scene_visibility);
	float wake_highlight = pow(smoothstep(0.08, 0.60, wake.g) * mix(0.5, 1.0, foam_thread_mask), 1.3);
	float sparkle_noise = 0.0;
	float micro_glint = 0.0;
	if (micro_normal_strength > 0.001) {
		sparkle_noise = organic_fbm(sample_xz * max(micro_normal_scale * 0.22, 0.001) + vec2(TIME * 0.34, -TIME * 0.22));
		micro_glint = pow(smoothstep(0.64, 0.98, sparkle_noise), 2.4);
	}
	vec2 glint_dir = normalize(sun_glint_direction);
	vec2 slope_dir = normalize(foam_normal_xy + vec2(0.001));
	float glint_alignment = pow(clamp(dot(slope_dir, glint_dir) * 0.5 + 0.5, 0.0, 1.0), 4.0);
	float glint_mask = 0.0;
	if (sun_glint_strength > 0.001 && micro_normal_strength > 0.001) {
		glint_mask = pow(smoothstep(0.58, 0.96, sparkle_noise) * glint_alignment, max(sun_glint_sharpness * 0.34, 1.0));
	}
	float sun_glint = glint_mask * (0.18 + fresnel * 0.52) * (1.0 - foam_feather * 0.82) * sun_glint_strength;
	float crest_glint = pow(micro_glint * smoothstep(0.08, 0.42, wave_steepness), 1.15) * (0.24 + soft_fresnel * 0.62) * (1.0 - foam_feather * 0.75);
	vec3 foam_tint = mix(foam_color * vec3(0.46, 0.70, 0.78), foam_color * vec3(0.96, 0.99, 1.0), foam_core);
	vec3 color = mix(water_color, foam_tint, foam_feather * (0.25 + foam_core * 0.48));
	vec3 grazing_sky = reflection_color + vec3(0.13, 0.18, 0.20);
	vec3 grazing_color = mix(reflection_color * 0.74, grazing_sky, clamp(soft_fresnel * 0.55, 0.0, 1.0));
	color = mix(color, grazing_color, clamp(reflection_strength * (0.10 + soft_fresnel * 0.56) + grazing_reflection_strength * soft_fresnel * 0.22, 0.0, 0.68));
	float planar_debug_mask = 0.0;
	if (planar_reflection_enabled && planar_reflection_strength > 0.001) {
		vec3 projected = project_planar_reflection_uv(v_world_pos, nxy);
		vec4 planar_reflection = sample_planar_reflection(projected.xy, nxy);
		float reflected_luma = dot(planar_reflection.rgb, vec3(0.299, 0.587, 0.114));
		float reflected_object_mask = smoothstep(0.58, 0.94, planar_reflection.a) * smoothstep(0.035, 0.18, reflected_luma);
		float planar_grazing = pow(1.0 - view_dot, max(planar_reflection_grazing_power, 0.25));
		float rough_reflection_loss = 1.0 - clamp(length(foam_normal_xy) * 0.11 + foam_feather * 0.58, 0.0, 0.82);
		float waterline_fade = smoothstep(0.055, 0.34, thickness);
		float planar_mask = projected.z * reflected_object_mask * planar_reflection_strength * planar_grazing * rough_reflection_loss * waterline_fade * smoothstep(0.20, 0.90, soft_fresnel);
		planar_debug_mask = planar_mask;
		vec3 reflected_tint = mix(planar_reflection.rgb, reflection_color, clamp(0.10 + depth_blend * 0.22 + foam_feather * 0.20, 0.0, 0.55));
		float planar_mix_limit = clamp(planar_reflection_max_mix, 0.0, 1.0);
		color = mix(color, reflected_tint, clamp(planar_mask, 0.0, planar_mix_limit));
	}
	float ripple_shade = dot(pseudo_normal, normalize(vec3(-0.35, 0.82, 0.45))) * 0.5 + 0.5;
	color *= mix(0.82, 1.16, ripple_shade);
	float wake_trough = smoothstep(0.03, 0.30, max(0.5 - wake.r, 0.0) * 2.0) * (1.0 - foam_core);
	color = mix(color, deep_color * 0.92, wake_trough * wake_map_strength * 0.20);
	float optical_darkening = mix(1.0, 0.46 + fresnel * 0.22 + foam * 0.20, clamp(depth_absorption_strength, 0.0, 1.0));
	color *= optical_darkening;
	color += reflection_color * fresnel * fresnel_strength * 0.08;
	color += foam_tint * wake_highlight * wake_highlight_strength * 0.105;
	color += reflection_color * micro_glint * surface_gloss * 0.040;
	color += sun_glint_color * sun_glint;
	color += sun_glint_color * crest_glint * sun_glint_strength * 0.036;
	float horizon_span = max(horizon_fade_end_m - horizon_fade_start_m, 1.0);
	float horizon_t = smoothstep(0.0, 1.0, (distance(CAMERA_POSITION_WORLD.xz, v_world_pos.xz) - horizon_fade_start_m) / horizon_span);
	color = mix(color, horizon_color, clamp(horizon_t * horizon_fade_strength, 0.0, 1.0));
	float color_luma = dot(color, vec3(0.299, 0.587, 0.114));
	color = mix(vec3(color_luma), color, 1.14);

	float output_gain = final_color_gain;
	bool debug_enabled = debug_view > 0;
	if (debug_view == 1) {
		float depth_value = clamp(thickness / max(deep_depth_m, 0.001), 0.0, 1.0);
		color = mix(vec3(0.02, 0.06, 0.12), vec3(0.04, 0.78, 1.0), depth_blend);
		color += vec3(0.95, 0.98, 1.0) * (1.0 - smoothstep(0.0, foam_depth_m, thickness)) * 0.35;
		color = mix(color, vec3(depth_value), 0.22);
		output_gain = 1.0;
	} else if (debug_view == 2) {
		color = vec3(clamp(max(edge_foam * 5.0, storm_whitecap), 0.0, 1.0), clamp((wake_foam + ripple_foam) * 1.4, 0.0, 1.0), clamp(max(flow_foam, persistent_foam), 0.0, 1.0));
		output_gain = 1.0;
	} else if (debug_view == 3) {
		color = vec3(wake.r, wake.g, clamp(length(wake.ba - vec2(0.5)) * 2.0, 0.0, 1.0));
		output_gain = 1.0;
	} else if (debug_view == 4) {
		color = normalize(vec3(foam_normal_xy, 1.0)) * 0.5 + 0.5;
		output_gain = 1.0;
	} else if (debug_view == 5) {
		color = mix(vec3(0.03), vec3(flow_sample.r, flow_sample.g, flow_sample.b), flow_sample.a);
		color += vec3(flow_sample.a) * 0.18;
		output_gain = 1.0;
	} else if (debug_view == 6) {
		color = vec3(clamp(planar_debug_mask * 4.0, 0.0, 1.0), clamp(soft_fresnel, 0.0, 1.0), clamp(planar_reflection_strength, 0.0, 1.0));
		output_gain = 1.0;
	} else if (debug_view == 7) {
		color = vec3(clamp(absorption_amount, 0.0, 1.0), clamp(scene_visibility * 3.0, 0.0, 1.0), clamp(sun_glint * 2.0 + micro_glint * 0.25, 0.0, 1.0));
		output_gain = 1.0;
	}

	ALBEDO = color * output_gain;
	float mesh_edge_alpha = 1.0;
	if (mesh_edge_fade_width > 0.0001) {
		float mesh_edge_distance = min(min(UV.x, 1.0 - UV.x), min(UV.y, 1.0 - UV.y));
		mesh_edge_alpha = smoothstep(0.0, mesh_edge_fade_width, mesh_edge_distance);
	}
	float slope_roughness = clamp(length(foam_normal_xy) * 0.12, 0.0, 0.14);
	float base_roughness = mix(0.16, 0.07, clamp(surface_gloss, 0.0, 1.0));
	METALLIC = 0.0;
	if (debug_enabled) {
		ROUGHNESS = 1.0;
		SPECULAR = 0.0;
		EMISSION = vec3(0.0);
		ALPHA = 1.0;
	} else {
		float computed_roughness = clamp(base_roughness + slope_roughness + foam_feather * 0.36 + wake_highlight * 0.08, 0.055, 0.66);
		ROUGHNESS = mix(computed_roughness, max(computed_roughness, distant_specular_roughness), specular_lod);
		SPECULAR = mix(0.42, 0.72, clamp(surface_gloss, 0.0, 1.0));
		EMISSION = foam_tint * wake_highlight * wake_highlight_strength * 0.018;
		ALPHA = clamp(water_alpha + fresnel * 0.055 + foam_feather * 0.055, 0.0, 1.0) * mesh_edge_alpha;
	}
}
