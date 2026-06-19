shader_type spatial;
// Cloud-shadow CASTER. A large quad (oriented ⟂ to the sun, high above the scene) wears this and is
// set to cast_shadow = SHADOWS_ONLY on its MeshInstance3D (invisible to the camera, depth/shadow only).
// It is alpha-cut by the cloud transmittance map, so it casts the CLOUD SHAPE into the
// DirectionalLight's shadow atlas. Godot's volumetric fog samples that same atlas per froxel
// (volumetric_fog_process.glsl), so cloud gaps become bright shafts and cloud bodies stay dim —
// true 3D shafts on BOTH terrain and fog, with NO custom fog-light hack.
//
// Why this is the canonical path: the shaft IS a shadow (memory godray-emission-vs-albedo-rootcause).
// We put the clouds INTO the sun's shadow map instead of faking light in fog.
//
// Render modes (verified against the Godot 4.6 spatial shader reference):
//  - unshaded: no lighting math (we only care about the depth/alpha it writes).
//  - cull_disabled: double-sided, so orientation relative to the sun never culls it away.
//  - depth_prepass_alpha: makes the ALPHA cut apply in the depth/shadow pass (so the discard
//    actually carves the cast shadow — without this, alpha is ignored for shadows).
// NOTE: "shadows_only" is NOT a render_mode in Godot 4.6 — invisibility is the NODE's
// cast_shadow = ShadowsOnly setting (set in GodRaysVolumetric.cs), not a shader mode.
render_mode unshaded, cull_disabled, depth_prepass_alpha;

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;   // world metres across the cloud map (XZ, centered on origin)
uniform float cut_threshold = 0.5;      // transmittance below this = "cloud" = casts shadow
uniform float softness = 0.15;          // dither width around the threshold (ALPHA_HASH soft edges)

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Sample the cloud transmittance at this caster point's world XZ. The caster quad is large and
	// world-aligned in XZ, so world.xz maps directly into the cloud map (same convention as terrain).
	vec2 uv = v_world.xz / shadow_region + 0.5;
	float vis = 1.0;
	if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
		vis = texture(cloud_shadow_tex, uv).r;   // 1 = sun reaches (gap), 0 = under cloud
	}
	// Cloudy (low vis) → opaque (ALPHA≈1 → casts shadow). Gap (high vis) → ALPHA≈0 → discarded
	// (lets light through). The smooth ramp over `softness`, combined with ALPHA_HASH dithering and
	// the directional shadow blur + fog ESM term, yields a SOFT cloud-edge shadow, not a hard cutout.
	ALPHA = clamp((cut_threshold - vis) / max(softness, 1e-3) + 0.5, 0.0, 1.0);
	ALPHA_HASH_SCALE = 1.0;   // stochastic alpha → soft, dithered shadow silhouette
}
