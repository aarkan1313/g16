#[compute]
#version 450

// Shadow self-CHECK (diagnostic, not a render path). For a grid of world-XZ points it
// computes, using the SAME density model as cloud_shadow.glsl/cloud_raymarch.glsl:
//   out.x = sun-visibility (the shadow value the production shader writes)
//   out.y = vertical cloud density overhead (straight-up optical depth at that XZ)
//   out.z = sun-visibility sampled at the XZ OFFSET by the sun's horizontal direction
//           (where a real cast shadow from the overhead cloud should land)
// C# reads these back and asserts the correctness relationships numerically (no eyeball):
//   (1) more cloud overhead (y high) ⇒ darker shadow (x low): negative correlation.
//   (2) the OFFSET-sampled shadow (z) tracks overhead density better than the in-place (x),
//       proving the shadow lands in the sun direction like a real cast shadow.
// Reuses the exact sample_density so the check tests the real math.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict buffer OutBuf { float v[]; };   // 4 floats/cell
layout(set = 0, binding = 1) uniform sampler3D shape_tex;
layout(set = 0, binding = 2) uniform sampler3D detail_tex;
layout(set = 0, binding = 3) uniform sampler2D weather_tex;
layout(set = 0, binding = 4, std430) restrict buffer ParamsBuf {
    vec4 sun_dir;
    vec2 grid;            // grid resolution (x,y)
    vec2 region;          // x = region size (m), y = march steps
    float altitude, thickness;
    float coverage, density, cloud_type;
    float size, detail, detail_size, edge;
    float strength, ground_height;
    vec2 wind_offset;
    vec4 tail;            // w = layer_count (x/y/z unused here)
    vec4 layers[48];      // 8 layers × 6 vec4 (CloudLayers.Pack order) — 0-11 density, 19-21 profile
} P;
#define LAYER_COUNT P.tail.w
#define LF(i, f) P.layers[(i)*6 + ((f)>>2)][(f)&3]

// @@INCLUDE cloud_density

// per-layer density — mirrors cloud_shadow.glsl's layer_density (the shadow-side CHEAP 1-octave
// erosion variant), so this --shadowcheck validator reproduces the SHIPPED shadow density exactly.
// SHARED shape comes from cloud_density.gdshaderinc; only the detail-erosion below is local.
float layer_density(vec3 p, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW,
                    float pBottom, float pTop, float anvil, float shapeMode, float antiRep){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);
    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage * covW + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(ltype + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);
    // MACRO VARIETY (CO-3 anti-repetition): a mid-scale (~11 km) weather tap clusters cumulus into
    // varying-size groups with clearer gaps, breaking the uniform same-size-puff repetition. antiRep=0
    // → unchanged (gated, so zero cost for default cumulus). MUST stay byte-identical across shaders.
    if (antiRep > 0.0){
        vec2 mmuv = lp.xz * (1.0/11000.0) + windOff * (1.0/11000.0);
        vec4 wm = texture(weather_tex, mmuv);
        coverage = clamp(coverage * mix(1.0, smoothstep(0.2, 0.8, wm.r) * 1.5, antiRep), 0.0, 1.0);
        lsize *= mix(1.0, mix(0.6, 1.7, wm.g), antiRep);   // per-region clump size variety
    }
    vec2 wShape  = windOff;
    vec2 wCell   = windOff * 0.55 + vec2(-windOff.y, windOff.x) * 0.18;
    vec2 wDetail = windOff * 1.7  + vec2(windOff.y, -windOff.x) * 0.35;
    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;
    float sScale = SHAPE_SCALE / max(lsize, 0.01);
    vec3 suv = lpw * sScale + vec3(wShape.x, h, wShape.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.45, 1.0, 0.0, 1.0);
    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, ledge), 1.0);
    float shape = smoothstep(thresh, soft, base);
    float cellScale = sScale * 0.7 * max(lcell, 0.05);   // higher cell freq → MANY clumps, not few giants
    float cell = texture(shape_tex, lpw * cellScale + vec3(wCell.x, h, wCell.y) * cellScale).g;
    float cellGate = smoothstep(mix(0.80, 0.42, coverage), mix(1.0, 0.78, coverage), cell);
    // stratus: a mostly-connected sheet but KEEP occasional gaps (broken stratus, not 100% fill);
    // coverage drives how solid — high cov → near-overcast, lower cov → more breaks.
    float sheetGate = smoothstep(mix(0.45, 0.12, coverage), mix(0.75, 0.42, coverage), cell);
    cellGate = mix(cellGate, sheetGate, shapeMode);
    shape *= cellGate;
    shape *= type_gradient(h, type);
    shape *= height_profile(h, pBottom, pTop, anvil);
    if (shape <= 0.0) return 0.0;
    if (ldetail > 0.0){
        float dScale = DETAIL_SCALE / max(ldetsize, 0.01);
        vec3 duv = lp * dScale + vec3(wDetail.x, h, wDetail.y) * dScale;
        float det = texture(detail_tex, duv).r;
        float edgeBoost = mix(1.6, 0.7, shape);
        float erodeAmt = mix(0.35, 0.85, h) * ldetail * edgeBoost * (1.0 - 0.7 * shapeMode);   // stratus: smoother (less cauliflower)
        shape = clamp(remap(shape, det * erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * ldens * densBias;
}
float density_all(vec3 p, vec2 windOff){
    int n = clamp(int(LAYER_COUNT), 1, 8);
    float total = 0.0; float r = length(p);
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r < baseR || r > topR) continue;
        total += layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4),
            LF(i,19), LF(i,20), LF(i,21), LF(i,22), LF(i,23));
    }
    return total;
}

void main(){
    uint gx = gl_GlobalInvocationID.x, gy = gl_GlobalInvocationID.y;
    if (gx >= uint(P.grid.x) || gy >= uint(P.grid.y)) return;
    uint idx = gy * uint(P.grid.x) + gx;

    vec2 uv = (vec2(gx, gy) + 0.5) / P.grid;
    vec2 wxz = (uv - 0.5) * P.region.x;
    vec3 L = normalize(P.sun_dir.xyz);
    vec2 windOff = P.wind_offset;

    // full span across active decks (matches production)
    int nL = clamp(int(LAYER_COUNT), 1, 8);
    float minBase = 1e9, maxTop = -1e9;
    for (int i = 0; i < nL; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
    float baseR = PLANET_R + minBase, topR = PLANET_R + maxTop;
    vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
    vec2 hb = ray_sphere(ro, L, baseR);
    vec2 ht = ray_sphere(ro, L, topR);
    float ts = max(hb.y, 0.0), te = max(ht.y, 0.0);

    // density along the SUN ray (the cloud that casts the shadow here) + the production
    // shadow value, both summing all decks — for the correlation test.
    float vd = 0.0; float visHere = 1.0;
    if (L.y > 0.05 && te > ts){
        float pathLen = min(te - ts, (maxTop - minBase) / max(L.y, 0.2));
        int steps = clamp(int(P.region.y), 4, 32);
        float dt = pathLen / float(steps);
        float t = ts;
        for (int i = 0; i < steps; i++){ vec3 p = ro + L * t; vd += density_all(p, windOff) * dt; t += dt; }
        // MIRROR cloud_shadow.glsl's production visibility EXACTLY (audit #3): the production shader gates
        // out the faint tail (smoothstep) before the mix, so a check without it validated math that doesn't
        // ship. trans = Beer (slant-normalized); shadowed = ignore the faint tail; vis = strength·shadowed mix.
        float trans = exp(-vd * 0.02 * L.y);
        float shadowed = smoothstep(0.02, 0.5, 1.0 - trans);
        visHere = mix(1.0, trans, P.strength * shadowed);
    }

    v[idx * 4u + 0u] = visHere;     // production shadow value
    v[idx * 4u + 1u] = vd;          // density along sun ray (all decks)
    // DEBUG: cell 0 reports the decoded layer-0 params so C# can verify the buffer layout.
    if (idx == 0u){
        v[2] = LF(0,0);             // layer0 altitude (expect ~1800)
        v[3] = LAYER_COUNT;         // expect 2
    } else { v[idx*4u+2u] = 0.0; v[idx*4u+3u] = 0.0; }
}
