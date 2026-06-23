#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// matches HydrologyParams.Pack: slot0 res(int), 1 cell_size, 2 carve_strength, 3 depth_per_order,
// 4 width_per_order, 5 bank_sediment, rest pad.
layout(set=0, binding=0, std430) restrict buffer Params {
    int res; float cell_size; float carve_strength; float depth_per_order;
    float width_per_order; float bank_sediment; float _p6; float _p7;
    float _p8; float _p9; float _p10; float _p11; float _p12; float _p13; float _p14; float _p15;
} P;

layout(set=0, binding=1, std430) restrict buffer BaseH  { float baseh[]; };
layout(set=0, binding=2, std430) restrict buffer OutH   { float outh[]; };
layout(set=0, binding=3, std430) restrict buffer Accum  { float accum[]; };
layout(set=0, binding=4, std430) restrict buffer CMask  { float cmask[]; };
layout(set=0, binding=5, std430) restrict buffer WLevel { float wlevel[]; };
layout(set=0, binding=6, std430) restrict buffer Sed    { float sed[]; };
// segments: flat array of 6 floats each [Ax,Az,Bx,Bz,Order,Area]; count in push constant.
layout(set=0, binding=7, std430) restrict buffer Segs   { float seg[]; };

layout(push_constant, std430) uniform Push { int seg_count; float origin_x; float origin_z; float _pad; } pc;

// distance from point p to segment (a,b), plus the parametric t (0..1) for along-channel queries.
float seg_dist(vec2 p, vec2 a, vec2 b, out float t) {
    vec2 ab = b - a; float len2 = max(dot(ab, ab), 1e-6);
    t = clamp(dot(p - a, ab) / len2, 0.0, 1.0);
    vec2 proj = a + t * ab; return length(p - proj);
}

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = c.y * P.res + c.x;
    vec2 wp = vec2(pc.origin_x + float(c.x) * P.cell_size, pc.origin_z + float(c.y) * P.cell_size);

    // find the nearest channel segment; accumulate the SMOOTH valley influence of all nearby segments.
    float carve = 0.0;            // total depth to subtract (max-blended, so valleys merge smoothly)
    float nearOrder = 0.0, nearArea = 0.0, nearDist = 1e9;
    for (int s = 0; s < pc.seg_count; s++) {
        int o = s * 6;
        vec2 a = vec2(seg[o+0], seg[o+1]), b = vec2(seg[o+2], seg[o+3]);
        float order = seg[o+4], area = seg[o+5];
        float t; float d = seg_dist(wp, a, b, t);
        float halfw = P.width_per_order * order;                  // valley half-width grows with order
        if (d < halfw) {
            // smoothstep falloff: full depth at the channel line, 0 at halfw. C1-smooth => NO terracing.
            float fall = 1.0 - smoothstep(0.0, halfw, d);
            float depth = P.depth_per_order * order * fall;
            carve = max(carve, depth);                            // max => broad valley, no additive double-dip
        }
        if (d < nearDist) { nearDist = d; nearOrder = order; nearArea = area; }
    }
    outh[i] = baseh[i] - P.carve_strength * carve;                // carve_strength=0 => baseh untouched

    // substrate (own-cell writes): channel mask where close to a channel line; flow_accum from nearest area
    // falling off with distance; sediment within the valley; water_level = carved floor where masked (river
    // surface), else no-water sentinel (lake fill is a later phase).
    float chanW = max(P.cell_size * 1.5, P.width_per_order * 0.15);
    cmask[i]  = nearOrder >= 1.0 ? (1.0 - smoothstep(0.0, chanW, nearDist)) * clamp(nearOrder / 6.0, 0.1, 1.0) : 0.0;
    accum[i]  = nearArea * (1.0 - smoothstep(0.0, P.width_per_order * max(nearOrder, 1.0), nearDist));
    sed[i]    = (nearDist < P.width_per_order * max(nearOrder, 1.0)) ? P.bank_sediment : 0.0;
    wlevel[i] = (cmask[i] > 0.5) ? outh[i] : -1e9;                 // river surface at carved floor where masked
}
