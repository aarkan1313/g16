#[compute]
#version 450

// AT-2 aerial-perspective froxel LUT. gid.xy = screen tile, looped z = depth slice (near->far,
// quadratic bias). Each froxel = accumulated in-scatter (rgb) + mean transmittance (a) from the
// camera to that slice's distance, raymarching the atmosphere (single+multi scatter). Units: world
// is METRES; the atmosphere is MEGAMETRES (x1e-6). Horizontal position is ignored (atmosphere is
// ~uniform over the ~32 km froxel range); only altitude (camPos.y + rayDir.y*t) matters.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image3D aerialTex;
layout(set = 0, binding = 1) uniform sampler2D transLUT;
layout(set = 0, binding = 2) uniform sampler2D msLUT;
layout(set = 0, binding = 3, std430) restrict readonly buffer Params {
    vec4 sun_turb;        // xyz = world to-sun, w = turbidity (reserved)
    vec4 cam_pos_far;     // xyz = camera world pos (m), w = max aerial distance (m, ~32000)
    mat4 inv_view_proj;   // NDC -> world (same as godray_screen)
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

void main(){
    ivec3 sz = imageSize(aerialTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }

    vec2 uv = (vec2(id) + 0.5) / vec2(sz.xy);
    // world ray for this tile (NDC far point through inv_view_proj, same as godray depth reconstruct)
    vec4 wf = P.inv_view_proj * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec3 camPos = P.cam_pos_far.xyz;
    vec3 rayDir = normalize(wf.xyz / wf.w - camPos);
    float maxDist = P.cam_pos_far.w;                 // metres
    float camY_Mm = camPos.y * 1e-6;
    vec3 sunDir = normalize(P.sun_turb.xyz);
    float cosT = dot(rayDir, sunDir);
    float miePhase = getMiePhase(cosT);
    float rayPhase = getRayleighPhase(-cosT);

    vec3 lum = vec3(0.0), tr = vec3(1.0);
    float prevT = 0.0;
    const int SUB = 4;
    for (int z = 0; z < sz.z; z++){
        float frac = (float(z) + 1.0) / float(sz.z);
        float t_m = maxDist * frac * frac;            // quadratic near-bias, metres
        for (int s = 0; s < SUB; s++){
            float mid = mix(prevT, t_m, (float(s) + 0.5) / float(SUB));   // metres
            float dt_Mm = ((t_m - prevT) / float(SUB)) * 1e-6;
            // Clamp altitude to >= sea level. Aerial rays look DOWN/along terrain, so without this the
            // sample point dives BELOW the ground radius at large t → negative altKM → exp() density
            // EXPLODES → runaway extinction + in-scatter that washes the whole frame. (The sky LUTs never
            // hit this: sky rays go UP.) Real haze along the ground sits at ~sea-level density.
            float alt_Mm = max(camY_Mm + rayDir.y * mid * 1e-6, 0.0);
            vec3 pos = vec3(0.0, groundRadiusMM + alt_Mm, 0.0);
            vec3 rs; float ms; vec3 ext; getScatteringValues(pos, rs, ms, ext);
            vec3 sampleTr = exp(-dt_Mm * ext);
            vec3 sunTr = getValFromLUT(transLUT, pos, sunDir);
            vec3 psi = getValFromLUT(msLUT, pos, sunDir);
            vec3 inS = rs * (rayPhase * sunTr + psi) + vec3(ms) * (miePhase * sunTr + psi);
            vec3 scatterInt = (inS - inS * sampleTr) / max(ext, vec3(1e-6));
            lum += tr * scatterInt;
            tr *= sampleTr;
        }
        prevT = t_m;
        imageStore(aerialTex, ivec3(id, z), vec4(lum, dot(tr, vec3(0.33333))));   // rgb inscatter, a = mean T
    }
}
