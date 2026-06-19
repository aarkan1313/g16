shader_type fog;

// God rays — CANONICAL volumetric base (rebuild 2026-06-19, Option C: per-froxel cloud-shadow).
// A WORLD-shape FogVolume runs this fog() per froxel over the whole Environment volumetric-fog grid.
// Each froxel asks the cloud shadow map "is the GROUND POINT below me (along the sun ray) lit?" and
// modulates its scattering accordingly → bright shafts in cloud gaps, dim air under clouds. Because
// the gating is the SAME map + the SAME slant the terrain uses, the shafts ALIGN with the ground
// cloud-shadows at every altitude (no caster-quad projection offset — see the 2026-06-19 audit).
//
// THE LIGHT MODEL (memory godray-emission-vs-albedo-rootcause): Godot's fog light pass computes
//   in_scatter = ( sun · sun_shadow · HG(view·sun) · light_volumetric_fog_energy ) · (ALBEDO·DENSITY) + EMISSION
// so the SHADOWED, phase-weighted directional sun is multiplied by our ALBEDO·DENSITY. We gate ALBEDO
// (and DENSITY) by the cloud transmittance → the sun's own in-scatter is bright in gaps, dim under
// cloud = SHAFTS. EMISSION is "added flat" (no shadow/phase) so it's only a faint ambient floor, NOT
// the shaft source. (Earlier versions wrongly used EMISSION with ALBEDO=0 → flat fog, no shafts.)
//
// THE GEOMETRY (the 2026-06-19 fix): the cloud map S(x,z) is the sun transmittance for the GROUND
// column at (x,z) (baked marching from ground_y up the sun ray). A froxel at world p is shadowed iff
// the ground point it sits above ALONG THE SUN RAY is shadowed. That ground point is
//   g.xz = p.xz − toSun.xz · (p.y − ground_y) / toSun.y
// so we sample S(g.xz). This injects the correct 3D shadow at EVERY altitude (a flat caster could
// only do one slice, ~10 km off at low sun).

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;   // CloudVolume.RegionSize (world metres across the map, XZ centered on 0)
uniform float ground_y = 250.0;         // cloud-bake ground reference (CloudVolume._groundHeight = terrain mid)
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // direction TOWARD the sun (normalized, world) — sun_dir.y > 0

uniform vec3 fog_tint : source_color = vec3(1.0, 0.97, 0.92);   // warm single-scatter ALBEDO of the shaft air
uniform float base_density = 0.02;       // LOW: fog everywhere but thin enough to see through; only shafts pop
uniform float height_base = 120.0;       // altitude where haze is densest (valley floor / terrain mid)
uniform float height_falloff = 1600.0;   // metres over which haze thins upward (tall → shafts have vertical reach)
uniform float shadow_softness = 0.15;    // shadowed air keeps this fraction lit (soft shaft shoulders, not pure black)
// NOTE: shaft BRIGHTNESS is the UI "god ray strength" → it drives the sun's light_volumetric_fog_energy
// (a LINEAR engine multiplier on the in-scatter), NOT a multiply on ALBEDO. ALBEDO stays a physical
// reflectance ≤1 so the gap-vs-shadow CONTRAST is never clipped (an earlier bug clamped strength·albedo
// to 1.5 and saturated the whole lit range → the strength knob did nothing). See GodRaysVolumetric.SetStrength.

void fog() {
	// Height profile: fog hugs the ground, thins with altitude. Breaks the "uniform medium in all
	// directions" trap — a shaft's base is in thicker air, its top in thin air (vertical gradient).
	float h = clamp(1.0 - max(WORLD_POSITION.y - height_base, 0.0) / height_falloff, 0.0, 1.0);

	// Per-froxel cloud gating, GROUND-PROJECTED along the sun ray (the geometry fix). Find the ground
	// point this froxel sits above along the sun ray, sample the cloud transmittance THERE.
	vec3 toSun = normalize(sun_dir);
	float denom = max(toSun.y, 0.15);                       // clamp: a low sun mustn't shoot the offset to infinity
	vec2 g = WORLD_POSITION.xz - toSun.xz * ((WORLD_POSITION.y - ground_y) / denom);
	vec2 uv = g / shadow_region + 0.5;

	// Off the finite map → no data → treat as open sky (lit). Inside → real cloud transmittance.
	float sunVis = 1.0;
	if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
		sunVis = texture(cloud_shadow_tex, uv).r;           // 1 = sun reaches the ground here (gap), 0 = under cloud
	}
	// Soft gate: even shadowed air keeps a little lit medium → soft shaft shoulders, not a hard wedge.
	float litFrac = mix(shadow_softness, 1.0, sunVis);

	// DENSITY + ALBEDO are the lever. The ENGINE multiplies (sun · sun-shadow · HG phase · vol_fog_energy)
	// by ALBEDO·DENSITY, so gating them by litFrac makes the sun's own in-scatter bright in gaps, dim
	// under cloud = shafts. litFrac in BOTH sharpens the gap/shadow contrast (the effect reads as litFrac²).
	DENSITY = base_density * h * litFrac;
	// ALBEDO is a physical reflectance ≤1 (litFrac∈[shadow_softness,1]) — the gap/shadow CONTRAST, never
	// clamped away. Shaft BRIGHTNESS comes from the sun's light_volumetric_fog_energy (UI strength), applied
	// by the engine downstream — so turning strength up brightens shafts WITHOUT crushing the contrast.
	ALBEDO  = fog_tint * litFrac;

	// Faint self-lit ambient floor so deep-shadow air isn't pure black (canonical "sky fills shadowed
	// froxels"). NOT the shaft source — that's the shadowed in-scatter on ALBEDO above.
	EMISSION = fog_tint * (0.02 * h);
}
