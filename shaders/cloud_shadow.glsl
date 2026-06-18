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
    vec2 wind_offset;      // CPU-integrated wind (m) — match cloud_raymarch.glsl
} P;

const float PLANET_R = 200000.0;

float remap(float v, float a, float b, float c, float d){ return c + (v - a) * (d - c) / max(b - a, 1e-5); }

float type_gradient(float h, float type){
    float baseRound = smoothstep(0.0, 0.15, h);
    float topFade = 1.0 - smoothstep(mix(0.5, 0.95, type), 1.0, h);
    return baseRound * topFade;
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
const float SHAPE_SCALE   = 1.0 / 9000.0;
const float DETAIL_SCALE  = 1.0 / 1300.0;
const float WARP_AMOUNT   = 600.0;

// ===== SHARED DENSITY — byte-identical to cloud_raymarch.glsl's sample_density. If this
// ever diverges from the raymarch copy, the shadow desyncs from the visible cloud.
float sample_density(vec3 p, float baseR, float topR, vec2 windOff){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);

    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(P.cloud_type + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);

    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;

    float sScale = SHAPE_SCALE / max(P.size, 0.01);
    vec3 suv = lpw * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.3, 1.0, 0.0, 1.0);

    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, P.edge), 1.0);
    float shape = smoothstep(thresh, soft, base);

    // CELLULARITY — identical to cloud_raymarch.glsl (keep clumps+gaps even when dense).
    float cellScale = sScale * 0.35;
    float cell = texture(shape_tex, lpw * cellScale + vec3(windOff.x, h, windOff.y) * cellScale).g;
    float cellGate = smoothstep(mix(0.78, 0.35, coverage), mix(1.0, 0.6, coverage), cell);
    shape *= cellGate;

    shape *= type_gradient(h, type);
    if (shape <= 0.0) return 0.0;

    if (P.detail > 0.0){
        float dScale = DETAIL_SCALE / max(P.detail_size, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x * 1.7, h, windOff.y * 1.7) * dScale;
        float det = texture(detail_tex, duv).r;
        float erodeAmt = mix(0.25, 0.6, h) * P.detail;
        shape = clamp(remap(shape, det * erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * P.density * densBias;
}

void main(){
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= int(P.tex_size.x) || px.y >= int(P.tex_size.y)) return;

    // texel → world XZ over the terrain footprint (centered at origin)
    vec2 uv = (vec2(px) + 0.5) / P.tex_size;
    vec2 wxz = (uv - 0.5) * P.region.x;

    // March from a representative terrain elevation toward the sun, through the SAME curved
    // shell as cloud_raymarch.glsl (lifted into planet space) so the shadow's density taps
    // land at the same world XZ as the visible cloud → matched cast.
    vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
    vec3 L = normalize(P.sun_dir.xyz);

    float baseR = PLANET_R + P.altitude;
    float topR  = baseR + P.thickness;
    vec2 hitB = ray_sphere(ro, L, baseR);
    vec2 hitT = ray_sphere(ro, L, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);

    float vis = 1.0;
    if (L.y > 0.05 && tEnd > tStart){
        // CAP the marched path to ~the vertical layer thickness. A low sun makes the raw
        // slanted chord (tEnd-tStart) huge → it accumulates density from clouds far away,
        // darkening ground under thin/clear sky (the "way more shadow than cloud in view"
        // bug). Normalizing by 1/L.y so the optical depth reflects the cloud DIRECTLY above
        // the point (cosine-corrected), not the long slant.
        float chord = tEnd - tStart;
        float maxPath = P.thickness / max(L.y, 0.2);   // bound the slant to ~thickness/sinSun
        float pathLen = min(chord, maxPath);
        int steps = clamp(int(P.region.y), 4, 32);
        float dt = pathLen / float(steps);
        vec2 windOff = P.wind_offset;   // CPU-integrated; no teleport
        float d = 0.0;
        float t = tStart;
        for (int i = 0; i < steps; i++){
            vec3 p = ro + L * t;
            d += sample_density(p, baseR, topR, windOff) * dt;
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
