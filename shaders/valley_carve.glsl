#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// matches HydrologyParams.Pack: slot0 res(int), 1 cell_size, 2 carve_strength, 3 depth_per_order,
// 4 width_per_order, 5 bank_sediment, rest pad.
layout(set=0, binding=0, std430) restrict buffer Params {
    int res; float cell_size; float carve_strength; float depth_per_order;
    float width_per_order; float bank_sediment; float carve_min_order; float _p7;
    float _p8; float _p9; float _p10; float _p11; float _p12; float _p13; float _p14; float _p15;
} P;

layout(set=0, binding=1, std430) restrict buffer BaseH  { float baseh[]; };
layout(set=0, binding=2, std430) restrict buffer OutH   { float outh[]; };
layout(set=0, binding=3, std430) restrict buffer Accum  { float accum[]; };
layout(set=0, binding=4, std430) restrict buffer CMask  { float cmask[]; };
layout(set=0, binding=5, std430) restrict buffer WLevel { float wlevel[]; };
layout(set=0, binding=6, std430) restrict buffer Sed    { float sed[]; };
// segments: flat array of 8 floats each [Ax,Az,Bx,Bz,Order,Area,BedA,BedB]; count in push constant.
layout(set=0, binding=7, std430) restrict buffer Segs   { float seg[]; };

layout(push_constant, std430) uniform Push { int seg_count; float origin_x; float origin_z; float _pad; } pc;

// distance from p to segment (a,b) + parametric t (0..1) for interpolating along-channel bed elevation.
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
    float base = baseh[i];

    // CARVE TOWARD A BED ELEVATION (Genevaux 2013: h = (1-w)*terrain + w*(u_z + cross_section)), NOT subtract a
    // per-segment depth from the bumpy base (that made disconnected gouges). For each cell we find the river
    // segment whose VALLEY most lowers us, taking the interpolated bed elevation u_z at the projection and a
    // smooth cross-section rising from the channel out to the valley edge. The floor is the (monotonic-
    // downstream) bed, so it's continuous along-channel; tributaries grade into trunks (shared bed field).
    float target = base;          // carved valley surface (only ever <= base: carving lowers, never raises)
    float wsum = 0.0;             // max smooth weight over contributing segments (compact support)
    float nearOrder = 0.0, nearArea = 0.0, nearDist = 1e9, nearBed = base;

    for (int s = 0; s < pc.seg_count; s++) {
        int o = s * 8;
        vec2 a = vec2(seg[o+0], seg[o+1]), b = vec2(seg[o+2], seg[o+3]);
        float order = seg[o+4], area = seg[o+5], bedA = seg[o+6], bedB = seg[o+7];
        float t; float d = seg_dist(wp, a, b, t);
        float uz = mix(bedA, bedB, t);                          // bed elevation at the projection (descends A->B)

        // substrate nearest-channel tracking uses ALL orders (full network feeds flow_accum/mask);
        // but only reaches of >= carve_min_order CARVE a visible valley (skip tiny rivulet herringbone).
        if (order < P.carve_min_order) {
            if (d < nearDist) { nearDist = d; nearOrder = order; nearArea = area; nearBed = uz; }
            continue;
        }

        // discharge-scaled valley half-width (continuous, NOT quantized order): phi = 0.42*A^0.69 (Genevaux/
        // Peytavie). width_per_order acts as an overall valley-width gain; order gives a gentle extra widening.
        float discharge = 0.42 * pow(max(area, 1.0), 0.69);
        float halfw = P.width_per_order * (0.6 + 0.4 * order) * (0.5 + 0.5 * sqrt(discharge / 8.0));
        halfw = max(halfw, P.cell_size * 2.0);

        if (d < halfw) {
            // cross-section: valley floor sits at uz at the channel line, rises smoothly to terrain at halfw.
            // depth_per_order * order = how deep the channel thalweg is below the local bed shoulder.
            float thalweg = P.depth_per_order * (0.5 + 0.5 * order);
            float k = smoothstep(0.0, halfw, d);                // 0 at channel, 1 at valley edge
            // floor sits at (bed - thalweg) at the channel line, rising smoothly to terrain at the valley edge.
            float carvedZ = mix(uz - thalweg, base, k);         // C1 cross-section, no terracing
            float w = (1.0 - k);                                // compact-support smooth weight
            // keep the deepest (most-lowering) contribution; valleys merge instead of double-dipping
            float cand = mix(base, carvedZ, P.carve_strength * w);
            if (cand < target) { target = cand; }
            wsum = max(wsum, w);
        }
        if (d < nearDist) { nearDist = d; nearOrder = order; nearArea = area; nearBed = uz; }
    }
    outh[i] = min(base, target);                                // never raise terrain

    // substrate (own-cell writes). channel mask near the channel line; flow_accum from nearest area falling off;
    // sediment within the valley; water_level = bed elevation where channel (river surface), else no-water.
    float chanW = max(P.cell_size * 1.5, P.width_per_order * 0.15);
    cmask[i]  = nearOrder >= 1.0 ? (1.0 - smoothstep(0.0, chanW, nearDist)) * clamp(nearOrder / 6.0, 0.1, 1.0) : 0.0;
    float wfall = P.width_per_order * (0.6 + 0.4 * max(nearOrder, 1.0));
    accum[i]  = nearArea * (1.0 - smoothstep(0.0, wfall, nearDist));
    sed[i]    = (nearDist < wfall) ? P.bank_sediment : 0.0;
    wlevel[i] = (cmask[i] > 0.5) ? nearBed : -1e9;
}
