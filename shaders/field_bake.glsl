#[compute]
#version 450

// FIELD CACHE bake shell (per-chunk height+normal). Same field MATH as field_height.glsl /
// the ground shader's analytic_h (one source of truth in field_math.gdshaderinc, spliced in
// at the marker by ChunkFieldCache.cs — RD-GLSL has no #include). This shell bakes a chunk's
// (GridN+2)² grid: per texel, the field HEIGHT + the fixed-step central-difference NORMAL the
// live vertex shader produces, so sampling the cache is byte-identical to evaluating live.
//
// TWO spacings (the key correctness point): the field math uses P.spacing = analytic_spacing
// (=FieldParams.Spacing, the octave-gate step the live shader always uses, LOD-independent),
// while texel POSITIONING uses grid_spacing (the chunk's vertex spacing = size/(GridN-1)).
// Conflating them (like the coarse AABB probe) would change the octave gate → fail --fieldcheck.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// out: vec4 per texel = (height, normal.x, normal.y, normal.z), row-major res×res.
layout(set = 0, binding = 0, std430) restrict writeonly buffer OutBuf {
    vec4 o[];
};

layout(set = 0, binding = 1, std430) restrict readonly buffer ParamsBuf {
    float origin_x; float origin_z; float spacing; uint seed; uint res; uint octaves;
    float base_freq; float amplitude; float lacunarity; float gain; uint field_mode;
    float cont_freq; float cont_weight; float uplift_freq; float uplift_weight;
    float uplift_lo; float uplift_hi; float macro_pivot; float macro_amp; float hill_damp;
    float ridge_freq; float ridge_amp; float mtn_lo; float mtn_hi; float grain_stretch;
    uint cont_octaves; float cont_warp; float uplift_warp; float massif_freq;
    float massif_floor; float foothill_w; float foothill_h;
} P;

// Positioning-only: the chunk's vertex spacing (NOT the field octave-gate spacing P.spacing).
layout(set = 0, binding = 2, std430) restrict readonly buffer GridBuf {
    float grid_spacing;
} G;

// @@INCLUDE field_math

FieldP make_fieldp() {
    FieldP fp;
    fp.origin_x = P.origin_x; fp.origin_z = P.origin_z; fp.spacing = P.spacing;
    fp.seed = P.seed; fp.res = P.res; fp.octaves = P.octaves; fp.base_freq = P.base_freq;
    fp.amplitude = P.amplitude; fp.lacunarity = P.lacunarity; fp.gain = P.gain;
    fp.field_mode = P.field_mode; fp.cont_freq = P.cont_freq; fp.cont_weight = P.cont_weight;
    fp.uplift_freq = P.uplift_freq; fp.uplift_weight = P.uplift_weight; fp.uplift_lo = P.uplift_lo;
    fp.uplift_hi = P.uplift_hi; fp.macro_pivot = P.macro_pivot; fp.macro_amp = P.macro_amp;
    fp.hill_damp = P.hill_damp; fp.ridge_freq = P.ridge_freq; fp.ridge_amp = P.ridge_amp;
    fp.mtn_lo = P.mtn_lo; fp.mtn_hi = P.mtn_hi; fp.grain_stretch = P.grain_stretch;
    fp.cont_octaves = P.cont_octaves; fp.cont_warp = P.cont_warp; fp.uplift_warp = P.uplift_warp;
    fp.massif_freq = P.massif_freq; fp.massif_floor = P.massif_floor;
    fp.foothill_w = P.foothill_w; fp.foothill_h = P.foothill_h;
    return fp;
}

void main() {
    uvec2 cell = gl_GlobalInvocationID.xy;
    if (cell.x >= P.res || cell.y >= P.res) { return; }
    FieldP fp = make_fieldp();
    // Texel world XZ from the chunk's VERTEX spacing; field math uses P.spacing (analytic_spacing).
    vec2 w = vec2(P.origin_x, P.origin_z) + vec2(cell) * G.grid_spacing;
    float h0 = field_height(w, P.seed, P.spacing, fp);
    // Fixed-step central-difference normal — IDENTICAL to ground.gdshader lines 264-269.
    float ns = max(P.spacing, 1.0);
    float hxp = field_height(w + vec2(ns, 0.0), P.seed, P.spacing, fp);
    float hxm = field_height(w - vec2(ns, 0.0), P.seed, P.spacing, fp);
    float hzp = field_height(w + vec2(0.0, ns), P.seed, P.spacing, fp);
    float hzm = field_height(w - vec2(0.0, ns), P.seed, P.spacing, fp);
    vec3 n = normalize(vec3(hxm - hxp, 2.0 * ns, hzm - hzp));
    o[cell.y * P.res + cell.x] = vec4(h0, n.x, n.y, n.z);
}
