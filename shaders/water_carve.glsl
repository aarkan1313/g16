#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Unified WATER CARVE (pillars: water sits IN carved basins/channels, not as a flat plane flooding raw terrain).
// Carves a smooth BOWL under each significant lake (floor = surface - basin_depth, blended rim → banks) AND a
// thin GROOVE under each river centerline. Additive delta on a COPY of the base height (base field untouched).
layout(set=0, binding=0, std430) restrict buffer Params {
    int res; float cell; float origin_x; float origin_z;
    float lake_depth; float lake_blend; float river_width; float river_depth;
    float river_blend; int lake_count; int seg_count; float _p;
} P;
layout(set=0, binding=1, std430) restrict buffer BaseH { float baseh[]; };
layout(set=0, binding=2, std430) restrict buffer OutH  { float outh[]; };
// lakes: 5 floats each [cx, cz, halfx, halfz, surfaceLevel]
layout(set=0, binding=3, std430) restrict buffer Lakes { float lake[]; };
// rivers: 4 floats each [Ax, Az, Bx, Bz]
layout(set=0, binding=4, std430) restrict buffer Segs  { float seg[]; };

float seg_dist(vec2 p, vec2 a, vec2 b) {
    vec2 ab = b - a; float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
    return length(p - (a + t * ab));
}

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy); if (c.x >= P.res || c.y >= P.res) { return; }
    int i = c.y * P.res + c.x;
    vec2 wp = vec2(P.origin_x + float(c.x) * P.cell, P.origin_z + float(c.y) * P.cell);
    float h = baseh[i];
    float target = h;   // carved height; we take the LOWEST carve (water wins), never raise

    // --- LAKE BOWLS: for each lake, signed distance to its footprint rectangle; inside+rim → carve toward
    //     (surface - lake_depth), blended out over lake_blend so the rim forms natural banks. ---
    for (int l = 0; l < P.lake_count; l++) {
        int o = l * 5;
        vec2 ctr = vec2(lake[o], lake[o+1]); vec2 ext = vec2(lake[o+2], lake[o+3]); float surf = lake[o+4];
        vec2 d2 = abs(wp - ctr) - ext;                          // SDF of an axis-aligned box (ext = half-extents)
        float sd = length(max(d2, vec2(0.0))) + min(max(d2.x, d2.y), 0.0);
        if (sd < P.lake_blend) {
            float floorZ = surf - P.lake_depth;                  // bowl floor below the water surface
            float k = clamp(sd / P.lake_blend, 0.0, 1.0);        // 0 inside → 1 at blend edge
            float bowl = mix(floorZ, h, smoothstep(0.0, 1.0, k));// floor inside, easing to terrain at the rim
            target = min(target, bowl);
        }
    }

    // --- RIVER GROOVES: thin channel under each centerline ---
    float nearest = 1e9;
    for (int s = 0; s < P.seg_count; s++) {
        int o = s * 4;
        nearest = min(nearest, seg_dist(wp, vec2(seg[o], seg[o+1]), vec2(seg[o+2], seg[o+3])));
    }
    if (nearest < P.river_width + P.river_blend) {
        float k = smoothstep(P.river_width, P.river_width + P.river_blend, nearest); // 0 in channel → 1 outside
        float groove = h - P.river_depth * (1.0 - k);
        target = min(target, groove);
    }

    outh[i] = target;
}
