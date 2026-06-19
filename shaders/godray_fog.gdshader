shader_type fog;

// God rays — Component A: in-scene, CLOUD-OCCLUDED volumetric shafts.
// A large camera-following FogVolume runs this fog() per froxel. The medium is a
// thin height-falling haze (DENSITY); the LIGHT in it (EMISSION) is gated by the
// cloud shadow map so the air glows where the sun breaks through a cloud gap and
// stays dim under clouds → real crepuscular rays near the terrain.
//
// Why this can't reproduce the old black-wedge bug: the cloud occlusion here is an
// ADDITIVE emission driven by a sampled texture — never a subtractive collision with
// the directional light's own volumetric shadow (that collision was the wedge source).
//
// The cloud shadow map is the SAME top-down transmittance map the terrain samples
// (CloudVolume bakes it from the cloud density field), so shafts line up with the
// shadow patches already on the ground. UV mapping matches terrain_lab.gdshader:
//   uv = world.xz / region + 0.5     (repeat_disable, white outside = full sun)

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;   // CloudVolume.RegionSize
uniform float shadow_plane_y = 0.0;     // terrain mid height — the plane the shadow map lives on

uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);   // direction TOWARD the sun (normalized, world)
uniform vec3 sun_color : source_color = vec3(1.0, 0.96, 0.9);
uniform float strength = 1.0;           // master intensity (UI: god ray strength)
uniform vec3 cam_pos = vec3(0.0);       // camera world pos (per-frame) → view dir for phase

uniform float haze = 0.001;             // base medium density (LOW — must see terrain through it; only shafts glow)
uniform float height_base = 200.0;      // altitude where haze is densest (~terrain mid)
uniform float height_falloff = 450.0;   // metres over which haze thins to nothing upward (hug the valley)
uniform float forward_g = 0.76;         // Henyey-Greenstein forward bias — shafts BLAZE toward the sun

// Henyey-Greenstein phase: strong forward peak at g→1. This is what makes a hazy medium
// read as god-ray BEAMS (bright looking toward the sun) instead of flat uniform fog.
float hg(float cosA, float g) {
	float g2 = g * g;
	return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * cosA, 1e-4), 1.5));
}

void fog() {
	// Thin haze that's densest low and fades with altitude — the medium light scatters in.
	// (Shafts read best raking through valley haze; almost nothing up high.) Kept LOW so the
	// terrain stays visible THROUGH it — only the lit shafts register, not a fog wall.
	float h = clamp(1.0 - max(WORLD_POSITION.y - height_base, 0.0) / height_falloff, 0.0, 1.0);
	DENSITY = haze * h;

	// ALBEDO 0: our haze must NOT scatter the directional sun uniformly — that un-occluded
	// uniform glow is what drowned the shafts (the dir light has no cloud shadow). All the
	// visible light comes from the cloud-gated EMISSION below → lit shafts vs dark gaps.
	ALBEDO = vec3(0.0);

	// Project this froxel along the sun ray onto the shadow plane, then sample the cloud
	// shadow there. This makes the lit columns ANGLE with the sun and coincide with the
	// ground shadows, instead of being purely vertical. Near-horizon sun (toSun.y small)
	// → long projection; repeat_disable makes far samples read white (lit), which is fine.
	vec3 toSun = normalize(sun_dir);
	float t = (toSun.y > 0.02) ? (WORLD_POSITION.y - shadow_plane_y) / toSun.y : 0.0;
	vec3 onPlane = WORLD_POSITION - toSun * t;
	vec2 uv = onPlane.xz / shadow_region + 0.5;
	float sunVis = texture(cloud_shadow_tex, uv).r;   // 1 = sun reaches, 0 = under cloud

	// Forward-scatter phase: the lit haze is FAR brighter when we look toward the sun
	// (view dir aligned with the sun) and fades away from it. This is what turns a uniform
	// glow into god-ray BEAMS. cosA = how aligned this froxel's view ray is with the sun.
	vec3 viewDir = normalize(WORLD_POSITION - cam_pos);
	float cosA = dot(viewDir, toSun);
	float phase = hg(cosA, forward_g);

	// The air glows where the sun reaches it (sunVis), weighted by the haze profile (h) and
	// the forward phase (beams toward the sun). EMISSION is added in the light pass.
	EMISSION = sun_color * (strength * sunVis * h * phase);
}
