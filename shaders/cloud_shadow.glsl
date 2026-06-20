#[compute]
#version 450

// Cloud-shadow map (Stage 5). For each texel over the terrain footprint, march from
// the ground UP toward the sun through the SAME cloud density field the sky raymarch
// uses, accumulating Beer transmittance → store sun visibility (1 = full sun, 0 =
// fully shadowed) in R. Because it samples the same field, the ground shadow MATCHES
// the cloud overhead (offset by the sun angle, like a real shadow). The terrain
// light() samples this to attenuate the sun. Mirrors cloud_raymarch.glsl's density.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(r16f, set = 0, binding = 0) uniform restrict writeonly image2D shadow_tex;
layout(set = 0, binding = 1) uniform sampler3D shape_tex;
layout(set = 0, binding = 2) uniform sampler3D detail_tex;
layout(set = 0, binding = 3) uniform sampler2D weather_tex;

layout(set = 0, binding = 4, std430) restrict buffer ParamsBuf {
    vec4 sun_dir;        // xyz dir toward sun, w unused
    vec2 tex_size;       // shadow map dims
    vec2 region;         // x = region size (m), y = shadow march steps
    float time;
    float coverage, density, cloud_type;
    float altitude, thickness;
    float drift_speed, drift_dir;
    float size, detail, detail_size, edge;
    float strength;        // shadow darkness (0 none .. 1 full)
    float ground_height;   // representative terrain elevation to start the sun-march from
    // TAIL vec4 (16-aligned) so layers[] starts on a 16-byte boundary. x=wind_x, y=wind_y,
    // z=cell_scale, w=layer_count. (See cloud_raymarch.glsl — hand-packed std430 drift.)
    vec4 tail;
    vec4 layers[48];       // 8 layers × 6 vec4 (CloudLayers.Pack order); fields 12-18 are
                           // raymarch-only lighting (ignored here); 19-21 = vertical profile
                           // (applied — density-affecting); shadow uses density 0-11 + 19-21.
} P;
#define WIND vec2(P.tail.x, P.tail.y)
#define LAYER_COUNT P.tail.w
#define LF(i, f) P.layers[(i)*6 + ((f)>>2)][(f)&3]

const float PLANET_R = 200000.0;

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
// MUST be byte-identical to cloud_raymarch.glsl's height_profile (density-affecting → shadows).
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

// Anti-repetition scale consts — MUST match cloud_raymarch.glsl exactly.
const float WEATHER_SCALE = 1.0 / 80000.0;
const float SHAPE_SCALE   = 1.0 / 6000.0;   // smaller individual clouds (was 1/9000 = ~giant)
const float DETAIL_SCALE  = 1.0 / 1300.0;
const float WARP_AMOUNT   = 600.0;

// ===== PER-LAYER DENSITY — byte-identical to cloud_raymarch.glsl's layer_density.
float layer_density(vec3 p, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW,
                    float pBottom, float pTop, float anvil, float shapeMode){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);

    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage * covW + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(ltype + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);

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
    cellGate = mix(cellGate, 1.0, shapeMode);   // stratus: don't fragment into clumps → a connected sheet
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

float density_all(vec3 p, vec2 windOff, out float opacOut){
    int n = clamp(int(LAYER_COUNT), 1, 8);
    float total = 0.0; float opAccum = 0.0; float r = length(p);
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r < baseR || r > topR) continue;
        float d = layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4),
            LF(i,19), LF(i,20), LF(i,21), LF(i,22));
        total += d; opAccum += d * LF(i,6);
    }
    opacOut = (total > 1e-5) ? opAccum / total : 1.0;
    return total;
}

void main(){
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= int(P.tex_size.x) || px.y >= int(P.tex_size.y)) return;

    // texel → world XZ over the terrain footprint (centered at origin)
    vec2 uv = (vec2(px) + 0.5) / P.tex_size;
    vec2 wxz = (uv - 0.5) * P.region.x;

    vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
    vec3 L = normalize(P.sun_dir.xyz);

    // full span across all ACTIVE decks (matches cloud_raymarch.glsl)
    int nL = clamp(int(LAYER_COUNT), 1, 8);
    float minBase = 1e9, maxTop = -1e9;
    for (int i = 0; i < nL; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
    float baseR = PLANET_R + minBase;
    float topR  = PLANET_R + maxTop;
    vec2 hitB = ray_sphere(ro, L, baseR);
    vec2 hitT = ray_sphere(ro, L, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);

    float vis = 1.0;
    if (L.y > 0.05 && tEnd > tStart){
        float chord = tEnd - tStart;
        float maxPath = (maxTop - minBase) / max(L.y, 0.2);   // bound the slant
        float pathLen = min(chord, maxPath);
        int steps = clamp(int(P.region.y), 4, 32);
        float dt = pathLen / float(steps);
        vec2 windOff = WIND;
        float d = 0.0;
        float t = tStart; float op;
        for (int i = 0; i < steps; i++){
            vec3 p = ro + L * t;
            d += density_all(p, windOff, op) * dt;   // sum all decks
            t += dt;
        }
        // cosine-correct so the shadow ~ cloud thickness overhead, independent of sun angle.
        // The numeric self-check (--shadowcheck) showed ~91% of ground was at least faintly
        // shadowed at coverage 0.45 — correct PLACEMENT (r=0.60) but too BROAD. Gate out the
        // faint tail so only meaningful cloud casts a visible shadow (thin wisps → ~full sun).
        float trans = exp(-d * 0.02 * L.y);    // Beer transmittance, slant-normalized
        float shadowed = smoothstep(0.02, 0.5, 1.0 - trans);   // ignore the faint tail
        vis = mix(1.0, trans, P.strength * shadowed);          // strength scales darkness
    }
    imageStore(shadow_tex, px, vec4(vis, 0.0, 0.0, 1.0));
}
