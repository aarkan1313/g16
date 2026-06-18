#[compute]
#version 450

// 3D cloud-noise bake (Stage 1). Produces TILEABLE volume noise for the volumetric
// cloud raymarch. One job: generate noise volumes — knows nothing about clouds,
// raymarching, the sun, or the scene. Mirrors SplatCompute/FieldCompute (local RD,
// dispatch, readback). Two modes selected by params.mode:
//   mode 0 = SHAPE volume (RGBA): R = Perlin-Worley (low-freq base shape),
//            G,B,A = Worley at 3 increasing frequencies (FBM detail octaves).
//            The raymarch builds its base FBM as g*0.625 + b*0.25 + a*0.125.
//   mode 1 = DETAIL volume (R only, packed in .r): high-freq Worley to erode edges.
// All noise is tileable on [0,res) so the volume repeats seamlessly across the sky.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(set = 0, binding = 0, std430) restrict buffer OutBuf { float v[]; };
layout(set = 0, binding = 1, std430) restrict buffer ParamsBuf {
    uint res;      // volume edge length (e.g. 128 shape / 32 detail)
    uint mode;     // 0 = shape (RGBA), 1 = detail (R)
    float seed;
    float _pad;
} P;

// --- hashing -----------------------------------------------------------------
vec3 hash33(vec3 p){
    p = vec3(dot(p, vec3(127.1, 311.7, 74.7)),
             dot(p, vec3(269.5, 183.3, 246.1)),
             dot(p, vec3(113.5, 271.9, 124.6)));
    return fract(sin(p + P.seed) * 43758.5453123);
}
float hash13(vec3 p){
    p = fract(p * 0.3183099 + 0.1 + P.seed);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// --- tileable gradient (Perlin-ish value) noise on a grid of `freq` cells ------
float vnoise_tiled(vec3 x, float freq){
    vec3 p = floor(x);
    vec3 f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    float n = 0.0;
    for (int dz = 0; dz <= 1; dz++)
    for (int dy = 0; dy <= 1; dy++)
    for (int dx = 0; dx <= 1; dx++){
        vec3 c = p + vec3(dx, dy, dz);
        vec3 cw = mod(c, freq);                 // wrap → tileable
        float h = hash13(cw);
        float w = mix(1.0 - f.x, f.x, float(dx))
                * mix(1.0 - f.y, f.y, float(dy))
                * mix(1.0 - f.z, f.z, float(dz));
        n += h * w;
    }
    return n;
}

// --- tileable Worley (cellular): 1 - dist to nearest jittered feature point ----
float worley_tiled(vec3 x, float freq){
    vec3 p = floor(x);
    vec3 f = fract(x);
    float md = 1.0;
    for (int dz = -1; dz <= 1; dz++)
    for (int dy = -1; dy <= 1; dy++)
    for (int dx = -1; dx <= 1; dx++){
        vec3 g = vec3(dx, dy, dz);
        vec3 cell = p + g;
        vec3 cw = mod(cell, freq);              // wrap the cell → tileable
        vec3 fp = g + hash33(cw) - f;           // vector to this cell's feature point
        md = min(md, dot(fp, fp));              // squared distance (cheap)
    }
    return 1.0 - clamp(sqrt(md), 0.0, 1.0);     // invert: 1 at cores, 0 at edges
}

float remap(float v, float a, float b, float c, float d){
    return c + (v - a) * (d - c) / max(b - a, 1e-5);
}

// The raymarch builds its detail FBM from the channels directly
// (g*0.625 + b*0.25 + a*0.125), so each channel here is ONE single-frequency
// Worley octave — NOT a pre-summed fbm. (Pre-fbm'ing every channel was both a
// correctness mismatch and a ~3x cost blowup; kept single-octave per channel.)
void main(){
    uvec3 id = gl_GlobalInvocationID.xyz;
    if (id.x >= P.res || id.y >= P.res || id.z >= P.res) return;
    vec3 uvw = (vec3(id) + 0.5) / float(P.res);   // [0,1) tileable domain
    uint base = (id.z * P.res + id.y) * P.res + id.x;

    if (P.mode == 0u){
        // SHAPE: R = Perlin-Worley base; G/B/A = Worley FBM octaves. Each detail channel is
        // now a 2-octave Worley FBM (audit: single-octave channels read blobby/round). The
        // Perlin base also gets a 3rd octave for finer structure.
        float perlin = vnoise_tiled(uvw * 4.0, 4.0) * 0.55
                     + vnoise_tiled(uvw * 8.0, 8.0) * 0.30
                     + vnoise_tiled(uvw * 16.0, 16.0) * 0.15;
        float w0 = worley_tiled(uvw * 3.0, 3.0);
        float pw = remap(perlin, w0 - 1.0, 1.0, 0.0, 1.0);  // Perlin-Worley (Schneider)
        // 2-octave Worley FBM per channel (low/mid/high bands)
        float w1 = worley_tiled(uvw * 3.0, 3.0) * 0.65 + worley_tiled(uvw * 6.0, 6.0) * 0.35;
        float w2 = worley_tiled(uvw * 6.0, 6.0) * 0.65 + worley_tiled(uvw * 12.0, 12.0) * 0.35;
        float w3 = worley_tiled(uvw * 12.0, 12.0) * 0.65 + worley_tiled(uvw * 24.0, 24.0) * 0.35;
        v[base * 4u + 0u] = clamp(pw, 0.0, 1.0);
        v[base * 4u + 1u] = clamp(w1, 0.0, 1.0);
        v[base * 4u + 2u] = clamp(w2, 0.0, 1.0);
        v[base * 4u + 3u] = clamp(w3, 0.0, 1.0);
    } else {
        // DETAIL: 2-octave Worley FBM (was single freq-8) for stronger, wispier edge erosion.
        float d = worley_tiled(uvw * 8.0, 8.0) * 0.6 + worley_tiled(uvw * 16.0, 16.0) * 0.4;
        v[base] = clamp(d, 0.0, 1.0);   // single-channel detail buffer (R)
    }
}
