// cloud_density.gdshaderinc — SHARED cloud density PRIMITIVES (ONE source of truth).
// RD-GLSL has no #include (Godot proposal #9592), so the three cloud computes string-splice this
// fragment in via `// @@INCLUDE cloud_density` before SPIR-V compile (same mechanism as
// field_math + FieldCompute): cloud_raymarch.glsl (sky), cloud_shadow.glsl (shadow map),
// cloud_shadow_check.glsl (the --shadowcheck validator).
//
// PURE MATH ONLY — no buffer / sampler / P.* references — so it compiles identically in all three.
// These are the parts that were previously hand-duplicated with "byte-identical" comments. The
// most important is height_profile: it is DENSITY-AFFECTING, so any drift between the sky march and
// the shadow march silently desyncs the ground shadow from the cloud overhead. Single-sourcing it
// here makes that whole bug class impossible.
//
// NOT here: per-deck layer_density and density_all. Their detail-erosion and signatures legitimately
// DIFFER per consumer (sky = 2-octave crisp silhouettes; shadow/check = cheaper 1-octave), so they
// stay local and are now documented honestly rather than falsely claimed identical.

const float PLANET_R = 200000.0;

// Anti-repetition scale consts (world meters → texture UV). DELIBERATELY MISMATCHED, non-integer
// periods so the combined tiling period is enormous: weather ~80 km · shape ~6 km · detail ~1.3 km.
const float WEATHER_SCALE = 1.0 / 80000.0;
const float SHAPE_SCALE   = 1.0 / 6000.0;
const float DETAIL_SCALE  = 1.0 / 1300.0;
const float WARP_AMOUNT   = 600.0;          // domain-warp displacement in meters

float remap(float v, float a, float b, float c, float d){ return c + (v - a) * (d - c) / max(b - a, 1e-5); }

float type_gradient(float h, float type){
    float baseRound = smoothstep(0.0, 0.15, h);
    float topFade = 1.0 - smoothstep(mix(0.5, 0.95, type), 1.0, h);
    return baseRound * topFade;
}

// Vertical density profile WITHIN a deck (CO-1). Turns a flat slab into a 3D body:
//   pBottom = height fraction over which density rounds up from the base (flat-ish bottom),
//   pTop    = height fraction at which density begins fading to the top,
//   anvil   = 0 cumulus (taper) .. 1 cumulonimbus (a spreading top lobe near the crown).
// NEUTRAL (0,1,0) returns ~1.0 across the body so the approved look reproduces exactly.
float height_profile(float h, float pBottom, float pTop, float anvil){
    float bottom = smoothstep(0.0, max(pBottom, 1e-4), h);   // rounded base
    float top    = 1.0 - smoothstep(pTop, 1.0, h);           // faded top
    // anvil: a secondary density lobe just below the crown so tops spread instead of tapering.
    float bump = anvil * smoothstep(pTop, mix(pTop, 1.0, 0.5), h) * (1.0 - smoothstep(0.85, 1.0, h));
    return bottom * max(top, bump);
}

vec2 ray_sphere(vec3 ro, vec3 rd, float R){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - R * R;
    float disc = b * b - c;
    if (disc < 0.0) return vec2(-1.0);
    float s = sqrt(disc);
    return vec2(-b - s, -b + s);
}
