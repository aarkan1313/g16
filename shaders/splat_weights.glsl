#[compute]
#version 450

// SPLAT unit (Lever 1): precompute a per-texel material-weight mask so the
// terrain fragment shader can blend a DOMINANT + SECONDARY material per spot
// (intra-zone variety) instead of filling each height/slope zone with a single
// material. Pure f(heightfield, params) per cell — embarrassingly parallel, the
// GPU-compute tier of the stack ladder.
//
// Input:  the already-computed heightfield (from FieldCompute) as a buffer, so
//         the 5-layer field math lives in ONE place (field_height.glsl) and this
//         pass is a pure consumer of heights.
// Output: an RGBA32F image, per texel:
//   R = dominant zone index   (0..6, as float)
//   G = secondary zone index  (0..6) — the companion material to blend toward
//   B = mix amount [0,1]      (how much secondary shows at this texel)
//   A = soft-edge blend [0,1] — relative weight of dominant vs secondary at zone
//                               boundaries, so transitions stay smooth
//
// The fragment shader samples this (filter_linear) and does: sample(dom),
// sample(sec), mix by a combination of B (intra-zone) and A (boundary). Mix
// STRENGTH is a live fragment uniform; this mask (structure) is re-baked on demand.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer Heights {
    float h[];
};
// Output: 4 floats (RGBA) per cell, row-major. Read back to CPU and uploaded as
// an ImageTexture (the FieldCompute pattern) — avoids cross-device texture races.
layout(set = 0, binding = 1, std430) restrict writeonly buffer SplatOut {
    vec4 splat[];
};

layout(set = 0, binding = 2, std430) restrict readonly buffer ParamsBuf {
    uint  res;
    float texel_world;     // metres per texel (spacing)
    float region_size;     // metres
    // zone breakpoints — kept in lockstep with terrain_lab.gdshader defaults
    float h_valley;
    float h_slope;
    float h_high;
    float h_peak;
    float slope_cliff_lo;
    float slope_cliff_hi;
    float band_softness_m;
    // intra-zone material mixing
    float mix_scale_m;     // wavelength of the companion-material patches
    float mix_bias;        // shifts how much secondary appears (0..1)
    uint  mask_mode;       // mirrors fragment mask_mode (0..5) for edge noise/biomes
    float edge_noise_m;
    float edge_noise_amp;
    float macro_m;
    uint  rule_based;      // 0 = legacy bands (zone_weights) | 1 = rule engine (role_weights)
    float curv_k;          // curvature scale (m) separating convex ridges from concave hollows
};

float fetch(int x, int z){
    x = clamp(x, 0, int(res)-1); z = clamp(z, 0, int(res)-1);
    return h[z*int(res)+x];
}
float hash2(vec2 p){ return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453); }
float vnoise(vec2 w,float wl){ vec2 p=w/wl; vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(hash2(i),hash2(i+vec2(1,0)),f.x),mix(hash2(i+vec2(0,1)),hash2(i+vec2(1,1)),f.x),f.y); }

// Same 7-zone weighting as the fragment shader's zone_weights(), so the baked
// mask matches what the live masks would produce. (Kept in sync deliberately.)
void zone_weights(out float w[7], float hh, float slope, float curv, vec2 wxz){
    for(int i=0;i<7;i++) w[i]=0.0;
    float en = (mask_mode==2u || mask_mode==3u) ? (vnoise(wxz,edge_noise_m)-0.5)*2.0*edge_noise_amp : 0.0;
    float hj = hh + en*250.0;
    float sj = slope + en*0.12;
    float soft = band_softness_m;

    if (mask_mode==5u){
        float b = vnoise(wxz, macro_m*1.4);
        int pick = int(clamp(floor(b*5.0),0.0,4.0));
        w[pick]=1.0;
        float cliff=smoothstep(slope_cliff_lo,slope_cliff_hi,sj);
        float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
        for(int i=0;i<7;i++) w[i]*=(1.0-max(cliff,peak));
        w[4]+=cliff; w[6]+=peak;
    } else {
        float v   = 1.0 - smoothstep(h_valley-soft, h_valley+soft, hj);
        float hi  = smoothstep(h_high-soft, h_high+soft, hj);
        float mid = clamp(1.0 - v - hi, 0.0, 1.0);
        float lowtrans = mid * (1.0 - smoothstep(h_slope-soft, h_slope+soft, hj));
        float slopez   = mid - lowtrans;
        w[0]=v; w[1]=lowtrans; w[2]=slopez; w[3]=0.0; w[5]=hi;

        float useSlope = (mask_mode==1u||mask_mode==2u||mask_mode==4u) ? 1.0 : 0.6;
        float cliff = smoothstep(slope_cliff_lo, slope_cliff_hi, sj) * useSlope;
        float cliffTrans = smoothstep(slope_cliff_lo*0.6, slope_cliff_lo, sj) * (1.0-cliff) * useSlope;
        if (mask_mode==3u){ float hollow=smoothstep(0.5,2.5,curv); w[1]+=hollow*0.5; }
        float groundScale=1.0-max(cliff,cliffTrans);
        for(int i=0;i<6;i++) if(i!=3 && i!=4) w[i]*=groundScale;
        w[3]+=cliffTrans; w[4]+=cliff;

        float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
        for(int i=0;i<6;i++) w[i]*=(1.0-peak);
        w[6]=peak;
    }
    float sum=0.0; for(int i=0;i<7;i++) sum+=w[i];
    if(sum>1e-4) for(int i=0;i<7;i++) w[i]/=sum;
}

// G1 rule engine: assign a weight per surface ROLE from terrain signals (altitude,
// slope, SIGNED curvature). Reuses the 7 slots as roles with the CURRENT materials:
//   0 valley/meadow · 1 valley->slope · 2 slope · 3 slope->cliff · 4 cliff/rock ·
//   5 high alpine · 6 snow. Curvature splits convex breaks (scree/exposed rock) from
//   concave hollows (where soil/grass collects) — meaning the bands alone can't give.
// Aspect + moisture rules come in G3; palette in G2. Same out-array shape as zone_weights.
// Pure function of LOCAL signals (no world position) -> tiles across infinite chunks.
void role_weights(out float w[7], float hh, float slope, float curv){
    for(int i=0;i<7;i++) w[i]=0.0;
    float soft = band_softness_m;
    float aLow  = 1.0 - smoothstep(h_valley-soft, h_valley+soft, hh);  // low ground
    float aHigh = smoothstep(h_high-soft,  h_high+soft,  hh);          // high ground
    float aMid  = clamp(1.0 - aLow - aHigh, 0.0, 1.0);                 // slopes between
    float aSnow = smoothstep(h_peak-soft,  h_peak+soft,  hh);          // snow band
    float rock  = smoothstep(slope_cliff_lo, slope_cliff_hi, slope);   // steep -> rock/scree
    float ground= 1.0 - rock;
    float k = max(curv_k, 1e-3);
    float convex  = smoothstep(0.0, k, -curv);                         // ridges / breaks
    float concave = smoothstep(0.0, k,  curv);                         // hollows

    w[0] = aLow  * ground * (0.5 + 0.5*concave);                       // meadow/valley (collects in hollows)
    w[1] = aMid  * ground * (1.0 - aHigh) * 0.6;                       // valley->slope transition
    w[2] = aMid  * (0.5*ground + 0.5*rock) * (0.4 + 0.6*convex);       // slope scree on breaks
    w[3] = mix(aMid, aHigh, 0.5) * rock * (0.3 + 0.7*convex) * (1.0 - aSnow); // loose scree below cliffs
    w[4] = rock  * (0.6 + 0.4*convex);                                 // cliff/rock (steep, any altitude)
    w[5] = aHigh * ground * (1.0 - aSnow);                             // high alpine
    w[6] = aSnow * ground;                                             // snow (sheds off steep faces)

    float s=0.0; for(int i=0;i<7;i++) s+=w[i];
    if(s>1e-4) for(int i=0;i<7;i++) w[i]/=s;
}

void main(){
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= int(res) || id.y >= int(res)) return;

    // world position + slope/curvature from height neighbours
    float hh = fetch(id.x, id.y);
    float hl = fetch(id.x-1, id.y), hr = fetch(id.x+1, id.y);
    float hd = fetch(id.x, id.y-1), hu = fetch(id.x, id.y+1);
    vec3 n = normalize(vec3(hl-hr, 2.0*texel_world, hd-hu));
    float slope = 1.0 - n.y;
    float curv  = (hl+hr+hd+hu)*0.25 - hh;
    vec2 wxz = (vec2(id) * texel_world) - vec2(region_size*0.5);

    float w[7];
    if (rule_based == 1u) { role_weights(w, hh, slope, curv); }
    else                  { zone_weights(w, hh, slope, curv, wxz); }

    // find the two strongest zones (dominant + runner-up) → smooth boundaries
    int d0=0; float m0=-1.0;
    for(int i=0;i<7;i++){ if(w[i]>m0){ m0=w[i]; d0=i; } }
    int d1=d0; float m1=-1.0;
    for(int i=0;i<7;i++){ if(i!=d0 && w[i]>m1){ m1=w[i]; d1=i; } }
    // boundary blend: how much the runner-up shows (0 deep in a zone, ~0.5 at edge)
    float boundary = (m0+m1>1e-4) ? m1/(m0+m1) : 0.0;

    // intra-zone companion mixing: a mid-scale field picks a companion material
    // (the adjacent-by-index zone) and a per-texel mix amount. This is what makes
    // a single zone read as a believable MIX rather than one flat fill.
    float mn = vnoise(wxz + vec2(311.0, 91.0), mix_scale_m);
    float mix_amt = clamp((mn - (1.0 - mix_bias)) / max(mix_bias, 1e-3), 0.0, 1.0);
    // companion = neighbouring zone (toward the lower/valley side for ground,
    // capped to valid range). Keeps it to materials already loaded.
    int companion = clamp(d0 - 1, 0, 6);
    if (d0 == 0) companion = 1; // valley mixes up toward its transition

    // Secondary material. Rule engine: ALWAYS the runner-up ROLE (d1) — spatially
    // meaningful, the whole point of G1. Legacy: the old index-adjacency companion.
    // R dominant, G secondary, B intra-zone mix amount, A boundary blend.
    float secondary = (rule_based == 1u)
        ? float(d1)
        : ((boundary > mix_amt) ? float(d1) : float(companion));
    splat[id.y*int(res)+id.x] = vec4(float(d0), secondary, mix_amt, boundary);
}
