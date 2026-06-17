#[compute]
#version 450

// Seamless tiling cloud-coverage bake (fresh 2026-06-17). Produces ONE grayscale
// coverage texture in [0,1] per texel: high-contrast FBM (NOT flat value noise —
// that read as squares last time), wrapped on a torus so it tiles seamlessly at
// period = res. Sampled in terrain_lab.gdshader's light() to attenuate the sun.
// Mirrors splat_weights.glsl: local 8x8, output buffer + params buffer.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict buffer OutBuf { float cov[]; };
layout(set = 0, binding = 1, std430) restrict buffer ParamsBuf {
    uint res;
    float seed;       // shifts the noise field
    float contrast;   // smoothstep tightening of the coverage edges
    float gain;       // FBM persistence
} P;

float hash(vec2 p){ p += P.seed; return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p){
    vec2 i = floor(p); vec2 f = fract(p); f = f*f*(3.0-2.0*f);
    return mix(mix(hash(i),            hash(i+vec2(1,0)), f.x),
               mix(hash(i+vec2(0,1)),  hash(i+vec2(1,1)), f.x), f.y);
}

// Seamless FBM on a torus: tile coords -> angles -> (cos,sin) pairs flattened to
// 2D. Edges wrap because cos/sin are periodic, so the texture tiles with no seam.
float seamless_fbm(vec2 uv, float freq){
    float amp = 0.5, sum = 0.0, norm = 0.0;
    for (int o = 0; o < 4; o++){
        float f = freq * exp2(float(o));
        vec2 a = uv * 6.2831853 * f;
        vec2 w = vec2(cos(a.x) + sin(a.y), sin(a.x) + cos(a.y));
        sum  += amp * vnoise(w * 1.7 + float(o) * 19.3);
        norm += amp;
        amp  *= P.gain;
    }
    return sum / max(norm, 1e-5);
}

void main(){
    uvec2 id = gl_GlobalInvocationID.xy;
    if (id.x >= P.res || id.y >= P.res) return;
    vec2 uv = (vec2(id) + 0.5) / float(P.res);     // [0,1), tiles seamlessly
    float n = seamless_fbm(uv, 3.0);
    // remap toward a patchy coverage look: center, stretch, contrast-curve.
    n = clamp((n - 0.5) * 1.8 + 0.5, 0.0, 1.0);
    n = smoothstep(0.5 - P.contrast, 0.5 + P.contrast, n);
    cov[id.y * P.res + id.x] = n;
}
