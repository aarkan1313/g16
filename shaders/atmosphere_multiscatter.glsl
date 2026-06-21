#[compute]
#version 450

// AT-1 GPU atmosphere — MULTI-SCATTER LUT (Hillaire 2020 isotropic approximation).
// psi_ms = L_2nd_order / (1 - f_ms); parameterised by (sunCosZenith, altitude). Samples the
// transmittance LUT (binding 1). What keeps the physical sky from looking flat/too-dark in
// twilight & shadow. See specs/2026-06-20-gpu-atmosphere-design.md.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2, std430) restrict readonly buffer Params { vec4 sun_turb; } P;

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

const float MS_STEPS = 20.0;
const float SQRT_SAMPLES = 8.0;   // 8x8 = 64 sphere directions
const float GROUND_ALBEDO = 0.3;

void getMS(vec3 pos, vec3 sunDir, out vec3 lumTotal, out vec3 fmsTotal){
    lumTotal = vec3(0.0); fmsTotal = vec3(0.0);
    float invSamples = 1.0 / (SQRT_SAMPLES * SQRT_SAMPLES);
    for (float i = 0.0; i < SQRT_SAMPLES; i += 1.0){
        for (float j = 0.0; j < SQRT_SAMPLES; j += 1.0){
            float theta = PI * (i + 0.5) / SQRT_SAMPLES;
            float phi = safeacos(1.0 - 2.0 * (j + 0.5) / SQRT_SAMPLES);
            float cp = cos(phi), sp = sin(phi), ct = cos(theta), st = sin(theta);
            vec3 rayDir = vec3(cp * st, ct, sp * st);

            float atmoDist = rayIntersectSphere(pos, rayDir, atmosphereRadiusMM);
            float groundDist = rayIntersectSphere(pos, rayDir, groundRadiusMM);
            float tMax = (groundDist > 0.0) ? groundDist : atmoDist;

            float cosTheta = dot(rayDir, sunDir);
            float miePhase = getMiePhase(cosTheta);
            float rayPhase = getRayleighPhase(-cosTheta);

            vec3 lum = vec3(0.0), lumFactor = vec3(0.0), tr = vec3(1.0);
            float t = 0.0;
            for (float s = 0.0; s < MS_STEPS; s += 1.0){
                float nT = ((s + 0.3) / MS_STEPS) * tMax;
                float dt = nT - t; t = nT;
                vec3 np = pos + t * rayDir;
                vec3 rs; float ms; vec3 ext;
                getScatteringValues(np, rs, ms, ext);
                vec3 sampleTr = exp(-dt * ext);

                vec3 scatterNoPhase = rs + vec3(ms);
                vec3 scatterF = (scatterNoPhase - scatterNoPhase * sampleTr) / ext;
                lumFactor += tr * scatterF;

                vec3 sunTr = getValFromLUT(transLUT, np, sunDir);
                vec3 inS = rs * rayPhase * sunTr + vec3(ms) * miePhase * sunTr;
                vec3 scatterInt = (inS - inS * sampleTr) / ext;
                lum += tr * scatterInt;
                tr *= sampleTr;
            }
            if (groundDist > 0.0 && dot(pos, sunDir) > 0.0){
                vec3 hitPos = normalize(pos + groundDist * rayDir) * groundRadiusMM;
                lum += tr * vec3(GROUND_ALBEDO) * getValFromLUT(transLUT, hitPos, sunDir);
            }
            fmsTotal += lumFactor * invSamples;
            lumTotal += lum * invSamples;
        }
    }
}

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    float sunCosZenith = 2.0 * uv.x - 1.0;
    float height = mix(groundRadiusMM, atmosphereRadiusMM, uv.y);
    vec3 pos = vec3(0.0, height, 0.0);
    vec3 sunDir = normalize(vec3(0.0, sunCosZenith, -sqrt(max(0.0, 1.0 - sunCosZenith * sunCosZenith))));
    vec3 lum, fms;
    getMS(pos, sunDir, lum, fms);
    vec3 psi = lum / max(vec3(1e-4), vec3(1.0) - fms);   // geometric series sum of multiple scattering
    imageStore(outTex, id, vec4(psi, 1.0));
}
