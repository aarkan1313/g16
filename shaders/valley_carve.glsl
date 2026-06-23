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
// coarse grid fields (row-major cz*cres+cx): Filled = spill surface (=lake water level); LakeDepth = Filled-origH.
layout(set=0, binding=8, std430) restrict buffer Filled { float filled[]; };
layout(set=0, binding=9, std430) restrict buffer Lake   { float lakedepth[]; };

layout(push_constant, std430) uniform Push {
    int seg_count; float origin_x; float origin_z; int cres;
    float coarse_ox; float coarse_oz; float coarse_sp; float lake_min_depth;
} pc;

// bilinear sample of a coarse row-major field at world (wx,wz). which: 0=Filled, 1=LakeDepth.
float sample_coarse(float wx, float wz, int which) {
    float gx = (wx - pc.coarse_ox) / pc.coarse_sp;
    float gz = (wz - pc.coarse_oz) / pc.coarse_sp;
    gx = clamp(gx, 0.0, float(pc.cres - 1));
    gz = clamp(gz, 0.0, float(pc.cres - 1));
    int x0 = int(floor(gx)), z0 = int(floor(gz));
    int x1 = min(x0 + 1, pc.cres - 1), z1 = min(z0 + 1, pc.cres - 1);
    float fx = gx - float(x0), fz = gz - float(z0);
    #define LF(X,Z) (which == 0 ? filled[(Z)*pc.cres+(X)] : lakedepth[(Z)*pc.cres+(X)])
    float a = mix(LF(x0,z0), LF(x1,z0), fx);
    float b = mix(LF(x0,z1), LF(x1,z1), fx);
    #undef LF
    return mix(a, b, fz);
}

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
    // TWO trackers (substrate coherence, audit finding 2): the CARVED-channel tracker (only reaches that
    // actually carve a valley, order >= carve_min_order) drives the visible-water substrate (channel_mask,
    // water_level, the carved valley floor) so those NEVER latch onto a tiny uncarved rivulet. A separate
    // any-order drainage tracker feeds flow_accum MAGNITUDE (the full network's upstream area is physically real
    // even where no valley is carved). carvedHalfw at the projection lets cmask follow the actual valley width.
    float cvDist = 1e9, cvOrder = 0.0, cvBed = base, cvHalfw = 0.0;   // nearest CARVED channel
    float anyArea = 0.0, anyDist = 1e9;                              // nearest channel of ANY order (for accum)

    for (int s = 0; s < pc.seg_count; s++) {
        int o = s * 8;
        vec2 a = vec2(seg[o+0], seg[o+1]), b = vec2(seg[o+2], seg[o+3]);
        float order = seg[o+4], area = seg[o+5], bedA = seg[o+6], bedB = seg[o+7];
        float t; float d = seg_dist(wp, a, b, t);
        float uz = mix(bedA, bedB, t);                          // bed elevation at the projection (descends A->B)

        if (d < anyDist) { anyDist = d; anyArea = area; }       // any-order: feeds flow_accum magnitude

        if (order < P.carve_min_order) { continue; }            // sub-threshold rivulet: no valley, no water signal

        // discharge-scaled valley half-width (continuous, NOT quantized order): phi = 0.42*A^0.69 (Genevaux/
        // Peytavie). width_per_order acts as an overall valley-width gain; order gives a gentle extra widening.
        float discharge = 0.42 * pow(max(area, 1.0), 0.69);
        float halfw = P.width_per_order * (0.6 + 0.4 * order) * (0.5 + 0.5 * sqrt(discharge / 8.0));
        halfw = max(halfw, P.cell_size * 2.0);

        if (d < halfw) {
            // cross-section: valley floor sits at uz at the channel line, rises smoothly to terrain at halfw.
            float thalweg = P.depth_per_order * (0.5 + 0.5 * order);
            float k = smoothstep(0.0, halfw, d);                // 0 at channel, 1 at valley edge
            float carvedZ = mix(uz - thalweg, base, k);         // C1 cross-section, no terracing
            float w = (1.0 - k);                                // compact-support smooth weight
            float cand = mix(base, carvedZ, P.carve_strength * w);
            if (cand < target) { target = cand; }               // keep the deepest (valleys merge, no double-dip)
        }
        if (d < cvDist) { cvDist = d; cvOrder = order; cvBed = uz - P.depth_per_order * (0.5 + 0.5 * order); cvHalfw = halfw; }
    }
    outh[i] = min(base, target);                                // never raise terrain

    // substrate, derived from the CARVED-channel tracker (coherent with the actual valley geometry).
    float chanW = max(P.cell_size * 1.5, cvHalfw * 0.25);       // channel-water band scales with the real valley
    cmask[i]  = cvOrder >= 1.0 ? (1.0 - smoothstep(0.0, chanW, cvDist)) * clamp(cvOrder / 6.0, 0.2, 1.0) : 0.0;
    accum[i]  = anyArea * (1.0 - smoothstep(0.0, max(P.width_per_order, 1.0), anyDist));  // full-network magnitude
    sed[i]    = (cvDist < cvHalfw) ? P.bank_sediment : 0.0;     // banks along the carved valley
    // river water surface = carved channel thalweg where masked; lakes handled below.
    wlevel[i] = (cmask[i] > 0.5) ? cvBed : -1e9;

    // LAKE pass (audit finding 1): where the coarse depression-fill submerged the ground beyond lake_min_depth,
    // this cell is under a lake whose surface = the spill level (coarse Filled). Set water_level to that surface
    // and clamp the terrain to sit just below it (a flat lake floor), so Arc 2 can render real lakes/basins.
    float lakeD = sample_coarse(wp.x, wp.y, 1);
    if (lakeD > pc.lake_min_depth) {
        float lakeSurface = sample_coarse(wp.x, wp.y, 0);   // coarse Filled = water surface elevation
        wlevel[i] = max(wlevel[i], lakeSurface);            // lake wins over (or joins) a channel surface
        // terrain modification honors carve_strength (0 => base untouched, the modularity guarantee).
        if (P.carve_strength > 0.0) { outh[i] = min(outh[i], lakeSurface - 0.5); }
        cmask[i] = max(cmask[i], 0.6);                       // mark as water for the substrate
    }
}
