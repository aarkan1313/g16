shader_type fog;

// God rays — Component A: in-scene, CLOUD-OCCLUDED volumetric shafts. CANONICAL rewrite
// (2026-06-18, round 2) after research into the actual Godot froxel light model.
//
// THE FIX (why the old version was uniform fog): god-ray shafts are NOT emission. The engine's
// fog light pass (volumetric_fog_process.glsl) computes, per froxel:
//     in_scatter = ( Σlights  shadow_atten · lightColor · energy · HG(view·sun,g) · vol_fog_energy )
//                  · (ALBEDO · DENSITY)  +  EMISSION
// i.e. the DIRECTIONAL LIGHT (with its SHADOW + Henyey-Greenstein phase) is multiplied by our
// froxel's ALBEDO·DENSITY. The OLD shader set ALBEDO=0 and faked shafts with EMISSION — which is
// "added flat", bypassing the sun, its shadow, and the phase entirely → uniform glow, no shafts.
//
// So here we WRITE ALBEDO (gated by the cloud transmittance) and let the ENGINE light it. The sun's
// real in-scatter — already phase-weighted and shadowed — is gated by our cloud_vis, so the air
// blazes in cloud GAPS (cloud_vis≈1) and stays dim under clouds (cloud_vis≈0). The cloud gating is
// a gentle 0..1 MULTIPLY on ALBEDO — never a subtractive collision with the sun's shadow map — so it
// does NOT reintroduce the old black-wedge bug (that was a subtractive shadow collision).
//
// Requires (set by GodRays.cs / the env): Environment volumetric fog ON, low base density, anisotropy
// ~0.85, ambient_inject ~0; DirectionalLight3D shadow ON + a high light_volumetric_fog_energy.

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;   // CloudVolume.RegionSize (world metres across the map, XZ centered on 0)
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // direction TOWARD the sun (normalized, world) — sun_dir.y > 0
uniform float cloud_height = 1500.0;     // altitude of the cloud deck we project gaps from (CloudVolume.AltitudeM)

uniform vec3 fog_tint : source_color = vec3(1.0, 0.97, 0.92);   // slight warm — single-scatter ALBEDO of the shaft air
uniform float base_density = 0.02;       // LOW: fog exists everywhere but thin enough to see through (only shafts pop)
uniform float height_base = 120.0;       // altitude where haze is densest (valley floor / terrain mid)
uniform float height_falloff = 1600.0;   // metres over which haze thins upward (tall → shafts have vertical reach)
uniform float strength = 1.0;            // master (UI: god ray strength) — scales ALBEDO contrast of the shafts
uniform float gap_boost = 1.0;           // extra density in gaps so clear columns also read as cleaner air (0=off)

void fog() {
	// Height profile: fog hugs the ground and thins with altitude (canonical height falloff). This
	// breaks the "uniform medium in all directions" trap — the base of a shaft is in thicker air,
	// the top in thin air, giving the column a vertical gradient instead of a flat wall.
	float h = clamp(1.0 - max(WORLD_POSITION.y - height_base, 0.0) / height_falloff, 0.0, 1.0);

	// Project this froxel UP THE SUN RAY to the cloud deck, sample the cloud transmittance THERE.
	// This is what makes the lit columns SLANT with the sun and align with the cloud gaps (and the
	// ground cloud-shadows), instead of being vertical. toSun.y>0 (sun above horizon).
	vec3 toSun = normalize(sun_dir);
	float denom = max(toSun.y, 0.15);                 // clamp: a low sun mustn't shoot the sample to infinity
	float t = (cloud_height - WORLD_POSITION.y) / denom;
	vec2 hit = WORLD_POSITION.xz + toSun.xz * t;      // +toSun: march TOWARD the sun, up to the cloud deck
	vec2 uv = hit / shadow_region + 0.5;

	// Off the finite map → no data → treat as open sky (lit). Inside → real cloud transmittance.
	float cloud_vis = 1.0;
	if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
		cloud_vis = texture(cloud_shadow_tex, uv).r;  // 1 = sun reaches (gap), 0 = under cloud
	}

	// DENSITY: thin base medium, hugging the ground (h). Optionally a touch denser in gaps so the lit
	// column also reads as clearer air. Kept LOW so the scene stays visible THROUGH the fog.
	DENSITY = base_density * h * (1.0 + gap_boost * cloud_vis);

	// ALBEDO is the lever. The ENGINE multiplies (sun · sun-shadow · HG phase · vol_fog_energy) by this.
	// Gating ALBEDO by cloud_vis → the sun's own in-scatter is bright in gaps, dim under cloud = SHAFTS.
	// strength sharpens the gap/cloud contrast. Clamp ≤1.5 so albedo stays near-physical (no blow-out).
	ALBEDO = fog_tint * clamp(cloud_vis * strength, 0.0, 1.5);

	// EMISSION: a whisper of self-lit ambient floor so deep-shadow air isn't pure black (matches the
	// canonical "sky/GI fills shadowed froxels"). NOT the shaft source — that's ALBEDO above.
	EMISSION = fog_tint * (0.02 * h);
}
