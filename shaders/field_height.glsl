#[compute]
#version 450

// FIELD compute shell. The heightfield MATH now lives in shaders/field_math.gdshaderinc
// (one source of truth, shared with the spatial ground shader). RD-GLSL has no #include
// (Godot proposal #9592), so FieldCompute.cs splices that file in at the marker below
// before SPIR-V compile. This shell owns ONLY the GPU plumbing: workgroup, SSBOs, main().

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict writeonly buffer Heights {
    float h[];
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
    vec2 world_xz = vec2(P.origin_x, P.origin_z) + vec2(cell) * P.spacing;
    FieldP fp = make_fieldp();
    h[cell.y * P.res + cell.x] = field_height(world_xz, P.seed, P.spacing, fp);
}
