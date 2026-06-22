#[compute]
#version 450

// AT-1 GPU atmosphere — SKY-VIEW LUT (Hillaire 2020). Per-direction in-scattered radiance for
// the CURRENT world sun direction (P.sun_turb.xyz), using the transmittance (binding 1) +
// multi-scatter (binding 2) LUTs. Parameterised (azimuth, sqrt-warped elevation) — MUST match
// atmo_dir_to_uv() in cloud_sky.gdshader. Recomputed when the sun moves (not per-frame, not
// per-pixel). See specs/2026-06-20-gpu-atmosphere-design.md.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2) uniform sampler2D msLUT;
// Full layout so the C3 tail (extra suns) sits at the right offset. AT-1 used only sun_turb; sky/aerial
// now also read the tail. cam_pos_far / inv_view_proj are unused HERE but declared for offset correctness.
layout(set = 0, binding = 3, std430) restrict readonly buffer Params {
    vec4 sun_turb;          // primary sun: xyz to-sun, w turbidity
    vec4 cam_pos_far;       // (unused in skyview; present for layout)
    mat4 inv_view_proj;     // (unused in skyview; present for layout)
    vec4 sun_meta;          // C3: x = extra atmosphere-sun count
    vec4 extra_dir[3];      // C3: xyz = to-sun, w = intensity
    vec4 extra_col[3];      // C3: rgb = sun color tint
} P;

// ===== Hillaire shared block (KEEP IDENTICAL across atmosphere_*.glsl — edit all four together) =====
const float PI = 3.14159265358979;
const float groundRadiusMM = 6.360;
const float atmosphereRadiusMM = 6.460;
const vec3  rayleighScatteringBase = vec3(5.802, 13.558, 33.1);
const float mieScatteringBase = 3.996;
const float mieAbsorptionBase = 4.4;
const vec3  ozoneAbsorptionBase = vec3(0.650, 1.881, 0.085);
float safeacos(float x){ return acos(clamp(x, -1.0, 1.0)); }
float getMiePhase(float c){
    const float g = 0.8;
    float n = (1.0 - g*g) * (1.0 + c*c);
    float d = (2.0 + g*g) * pow(1.0 + g*g - 2.0*g*c, 1.5);
    return (3.0 / (8.0*PI)) * n / d;
}
float getRayleighPhase(float c){ return (3.0 / (16.0*PI)) * (1.0 + c*c); }
void getScatteringValues(vec3 pos, out vec3 rayleighS, out float mieS, out vec3 extinction){
    float altKM = (length(pos) - groundRadiusMM) * 1000.0;
    float rD = exp(-altKM / 8.0);
    float mD = exp(-altKM / 1.2);
    rayleighS = rayleighScatteringBase * rD;
    mieS = mieScatteringBase * mD;
    float mieA = mieAbsorptionBase * mD;
    vec3 ozoneA = ozoneAbsorptionBase * max(0.0, 1.0 - abs(altKM - 25.0) / 15.0);
    extinction = rayleighS + vec3(mieS + mieA) + ozoneA;
}
float rayIntersectSphere(vec3 ro, vec3 rd, float rad){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - rad*rad;
    if (c > 0.0 && b > 0.0) return -1.0;
    float d = b*b - c;
    if (d < 0.0) return -1.0;
    if (d > b*b) return -b + sqrt(d);
    return -b - sqrt(d);
}
vec3 getValFromLUT(sampler2D lut, vec3 pos, vec3 sunDir){
    float h = length(pos);
    vec3 up = pos / h;
    float c = dot(sunDir, up);
    vec2 uv = vec2(clamp(0.5 + 0.5*c, 0.0, 1.0),
                   clamp((h - groundRadiusMM) / (atmosphereRadiusMM - groundRadiusMM), 0.0, 1.0));
    return texture(lut, uv).rgb;
}
// ===== end shared block =====

const float SKY_STEPS = 32.0;

// C3: per-sun in-scatter at a march sample. The 32-step march (getScatteringValues / extinction / tr) is
// SHARED across suns; only this — phase + sun-transmittance + multiscatter — is per-sun, so N suns cost
// ~+per-step ops, not N full marches. sunCol tints the contribution (primary = white; extras = their hue).
vec3 sunInScatter(vec3 rayDir, vec3 np, vec3 sunDir, vec3 sunCol, vec3 rs, float ms){
    float cosTheta = dot(rayDir, sunDir);
    float miePhase = getMiePhase(cosTheta);
    float rayPhase = getRayleighPhase(-cosTheta);
    vec3 sunTr = getValFromLUT(transLUT, np, sunDir);
    vec3 psi   = getValFromLUT(msLUT, np, sunDir);
    return (rs * (rayPhase * sunTr + psi) + vec3(ms) * (miePhase * sunTr + psi)) * sunCol;
}

vec3 raymarchSky(vec3 pos, vec3 rayDir, vec3 sunDir){
    float atmoDist = rayIntersectSphere(pos, rayDir, atmosphereRadiusMM);
    float groundDist = rayIntersectSphere(pos, rayDir, groundRadiusMM);
    float tMax = (groundDist > 0.0) ? groundDist : atmoDist;
    if (tMax < 0.0) { return vec3(0.0); }
    int nExtra = int(P.sun_meta.x + 0.5);

    vec3 lum = vec3(0.0), tr = vec3(1.0);
    float t = 0.0;
    for (float s = 0.0; s < SKY_STEPS; s += 1.0){
        float nT = ((s + 0.3) / SKY_STEPS) * tMax;
        float dt = nT - t; t = nT;
        vec3 np = pos + t * rayDir;
        vec3 rs; float ms; vec3 ext;
        getScatteringValues(np, rs, ms, ext);
        vec3 sampleTr = exp(-dt * ext);
        vec3 inS = sunInScatter(rayDir, np, sunDir, vec3(1.0), rs, ms);   // primary sun (white)
        for (int j = 0; j < nExtra && j < 3; j++){
            inS += sunInScatter(rayDir, np, normalize(P.extra_dir[j].xyz), P.extra_col[j].rgb * P.extra_dir[j].w, rs, ms);
        }
        vec3 scatterInt = (inS - inS * sampleTr) / ext;
        lum += tr * scatterInt;
        tr *= sampleTr;
    }
    return lum;
}

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    vec3 sunDir = normalize(P.sun_turb.xyz);
    vec3 pos = vec3(0.0, groundRadiusMM + 0.0002, 0.0);   // camera ~200 m above ground, inside the atmosphere
    float az = uv.x * 2.0 * PI;
    float el = (uv.y * uv.y) * (0.5 * PI);                 // sqrt warp → dense near the horizon (matches atmo_dir_to_uv)
    float ce = cos(el);
    vec3 rayDir = vec3(ce * cos(az), sin(el), ce * sin(az));
    imageStore(outTex, id, vec4(raymarchSky(pos, rayDir, sunDir), 1.0));
}
