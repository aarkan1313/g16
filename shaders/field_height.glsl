#[compute]
#version 450

// FIELD unit: pure f(world_pos, seed, params) heightfield, band-limited by
// spacing. Ported verbatim from WG15 (the 5-layer composition the user judged
// good across 8 seeds), trimmed of the precision-ladder ABI (a gated no-op with
// no consumer). Params block is 128 B; the C# writer (BuildParamsBytes) stays
// field-for-field in lockstep with this block.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict writeonly buffer Heights {
    float h[];
};

layout(set = 0, binding = 1, std430) restrict readonly buffer ParamsBuf {
    float origin_x;
    float origin_z;
    float spacing;
    uint seed;
    uint res;
    uint octaves;
    float base_freq;
    float amplitude;
    float lacunarity;
    float gain;
    uint field_mode;     // 0 full | 1 continent | 2 uplift | 3 hills | 4 ridges | 5 macro base
    float cont_freq;
    float cont_weight;
    float uplift_freq;
    float uplift_weight;
    float uplift_lo;
    float uplift_hi;
    float macro_pivot;
    float macro_amp;
    float hill_damp;
    float ridge_freq;
    float ridge_amp;
    float mtn_lo;
    float mtn_hi;
    float grain_stretch;
    uint cont_octaves;
    float cont_warp;
    float uplift_warp;
    float massif_freq;
    float massif_floor;
    float foothill_w;
    float foothill_h;
} P;

uint hash_u(uint x) {
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float hash2(ivec2 p, uint seed) {
    uint h = hash_u(uint(p.x) * 0x9e3779b9u ^ hash_u(uint(p.y) * 0x85ebca6bu ^ hash_u(seed)));
    return float(h) * (1.0 / 4294967296.0);
}

float fade(float t) {
    return t * t * (3.0 - 2.0 * t);
}

float value_noise(vec2 p, uint seed) {
    ivec2 i = ivec2(floor(p));
    vec2 f = fract(p);
    float a = hash2(i + ivec2(0, 0), seed);
    float b = hash2(i + ivec2(1, 0), seed);
    float c = hash2(i + ivec2(0, 1), seed);
    float d = hash2(i + ivec2(1, 1), seed);
    vec2 u = vec2(fade(f.x), fade(f.y));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float octave_weight(float oct_freq, float spacing) {
    float wavelength = 1.0 / max(oct_freq, 1e-9);
    return smoothstep(1.0 * spacing, 2.0 * spacing, wavelength);
}

// Value noise with analytic derivatives (d/dx, d/dz in noise space).
vec3 value_noise_d(vec2 p, uint seed) {
    ivec2 i = ivec2(floor(p));
    vec2 f = fract(p);
    float a = hash2(i + ivec2(0, 0), seed);
    float b = hash2(i + ivec2(1, 0), seed);
    float c = hash2(i + ivec2(0, 1), seed);
    float d = hash2(i + ivec2(1, 1), seed);
    vec2 u = vec2(fade(f.x), fade(f.y));
    vec2 du = 6.0 * f * (1.0 - f);
    float k1 = b - a;
    float k2 = c - a;
    float k3 = a - b - c + d;
    float n = a + k1 * u.x + k2 * u.y + k3 * u.x * u.y;
    return vec3(n, du.x * (k1 + k3 * u.y), du.y * (k2 + k3 * u.x));
}

// Domain warp: bend coords by low-frequency noise so landforms are organic.
vec2 domain_warp(vec2 p, uint seed, float amount, float freq) {
    float wx = value_noise(p * freq, seed ^ 0x57415250u) - 0.5;
    float wz = value_noise(p * freq + vec2(31.4, 17.0), seed ^ 0x70726177u) - 0.5;
    return p + amount * 2.0 * vec2(wx, wz);
}

// Ridged fBM: rounded crests, detail riding the previous ridge, fixed norm.
float ridged_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain,
                 float world_base_freq, float spacing) {
    float sum = 0.0, amp = 0.5, norm = 0.0, freq = 1.0, prev = 1.0;
    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        float n = value_noise(p * freq, seed + o * 0x9e3779b9u);
        float r = 1.0 - abs(2.0 * n - 1.0);
        r = smoothstep(0.0, 1.0, r);
        r *= prev;
        prev = clamp(r, 0.0, 1.0);
        sum += amp * w * r;
        norm += amp;
        amp *= gain;
        freq *= lacunarity;
    }
    return sum / max(norm, 1e-6);
}

// Slope-damped fBM: each octave is reduced where accumulated gradient is steep.
float slope_damped_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain,
                       float damp, float world_base_freq, float spacing) {
    float sum = 0.0, amp = 1.0, norm = 0.0, freq = 1.0;
    vec2 dsum = vec2(0.0);
    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        vec3 nd = value_noise_d(p * freq, seed + o * 0x68bc21ebu);
        sum += amp * w * nd.x / (1.0 + damp * dot(dsum, dsum));
        dsum += vec2(nd.y, nd.z) * (amp * freq * w);
        norm += amp;
        amp *= gain;
        freq *= lacunarity;
    }
    return sum / max(norm, 1e-6);
}

float value_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain, float world_base_freq, float spacing) {
    float sum = 0.0;
    float amp = 1.0;
    float norm = 0.0;
    float freq = 1.0;

    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        sum += amp * w * value_noise(p * freq, seed + o * 0x68bc21ebu);
        norm += amp;
        amp *= gain;
        freq *= lacunarity;
    }

    return sum / max(norm, 1e-6);
}

vec2 seed_axis(uint seed) {
    float angle = float(hash_u(seed ^ 0x41584953u)) * (6.28318530718 / 4294967296.0);
    return vec2(cos(angle), sin(angle));
}

// Structural spine: low-frequency placement and direction for P1 landforms.
float continent(vec2 world_xz, uint seed, float spacing) {
    uint cs = hash_u(seed ^ 0x434f4e54u);
    vec2 p = domain_warp(world_xz, cs, P.cont_warp / P.cont_freq, P.cont_freq * 0.5);
    return value_fbm(p * P.cont_freq, cs, P.cont_octaves, 2.0, 0.55, P.cont_freq, spacing);
}

struct Uplift { float amount; vec2 grain; vec2 wpos; };

Uplift uplift(vec2 world_xz, uint seed) {
    uint us = hash_u(seed ^ 0x55504c54u);
    vec2 wp = domain_warp(world_xz, us ^ 0x42454e44u, P.uplift_warp / P.uplift_freq, P.uplift_freq * 0.3);
    vec2 axis = seed_axis(us);
    vec2 perp = vec2(-axis.y, axis.x);
    float along = dot(wp, axis);
    float across = dot(wp, perp);

    float center_noise = value_fbm(vec2(along * P.uplift_freq * 0.35, 23.17),
                                   us + 0x101u, 3u, 2.0, 0.5, P.uplift_freq * 0.35, 1.0);
    float width_noise = value_noise(vec2(along * P.uplift_freq * 0.22, 7.91), us + 0x202u);
    float center = (center_noise - 0.5) * (0.85 / P.uplift_freq);
    float width = mix(0.16 / P.uplift_freq, 0.34 / P.uplift_freq, width_noise);
    float belt = 1.0 - smoothstep(width * 0.35, width, abs(across - center));
    float skirt = (1.0 - smoothstep(width * 0.8, width * P.foothill_w, abs(across - center))) * P.foothill_h;
    float band = belt + skirt * (1.0 - belt);

    // Along-axis massifs: the range breaks into peaks and saddles instead of
    // holding one uniform height for its whole length.
    float massif01 = value_fbm(vec2(along * P.massif_freq, 11.31), us + 0x404u,
                               3u, 2.0, 0.5, P.massif_freq, 1.0);
    float massif = mix(P.massif_floor, 1.0, smoothstep(0.32, 0.70, massif01));

    float rough = value_fbm(wp * P.uplift_freq * 1.7, us + 0x303u,
                            3u, 2.0, 0.55, P.uplift_freq * 1.7, 1.0);
    float amount = band * massif * mix(0.55, 1.0, smoothstep(P.uplift_lo, P.uplift_hi, rough));

    Uplift u;
    u.amount = amount;
    u.grain = axis;
    u.wpos = wp;
    return u;
}

float oriented_ridges(vec2 world_xz, vec2 grain, uint seed, float spacing) {
    vec2 w = domain_warp(world_xz, seed, 0.08 / P.ridge_freq, P.ridge_freq * 0.08);
    vec2 axis = grain;
    vec2 perp = vec2(-axis.y, axis.x);
    vec2 q = vec2(dot(w, axis) / P.grain_stretch, dot(w, perp) * P.grain_stretch);
    return ridged_fbm(q * P.ridge_freq, seed, P.octaves, P.lacunarity, P.gain,
                      P.ridge_freq, spacing * P.grain_stretch);
}

float field_height(vec2 world_xz, uint seed, float spacing) {
    float cont = continent(world_xz, seed, spacing);
    Uplift up = uplift(world_xz, seed);
    float base = (cont - P.macro_pivot) * P.macro_amp + up.amount * P.uplift_weight * P.macro_amp;

    float hills = (slope_damped_fbm(world_xz * P.base_freq, hash_u(seed ^ 0x48494c4cu),
                                    P.octaves, P.lacunarity, P.gain, P.hill_damp,
                                    P.base_freq, spacing) - 0.5) * 2.0 * P.amplitude;
    // Ridges live in the same warped frame as the belt, so crests follow the
    // bent range instead of the straight global axis.
    float ridge01 = oriented_ridges(up.wpos, up.grain, hash_u(seed ^ 0x52494447u), spacing);
    float ridges = (ridge01 - 0.42) * 2.0 * P.ridge_amp;
    float w_mtn = smoothstep(P.mtn_lo, P.mtn_hi, up.amount);
    float h = base + hills * (0.75 + P.cont_weight * cont + 0.25 * up.amount) + ridges * w_mtn * 0.35;

    if (P.field_mode == 1u) { return (cont - 0.5) * 6.0 * P.macro_amp; }
    if (P.field_mode == 2u) { return (up.amount - 0.5) * 2.0 * P.macro_amp; }
    if (P.field_mode == 3u) { return hills * 1.75; }
    if (P.field_mode == 4u) { return ridges * w_mtn; }
    if (P.field_mode == 5u) { return base * 1.5; }
    return h;
}

void main() {
    uvec2 cell = gl_GlobalInvocationID.xy;
    if (cell.x >= P.res || cell.y >= P.res) {
        return;
    }

    vec2 world_xz = vec2(P.origin_x, P.origin_z) + vec2(cell) * P.spacing;
    h[cell.y * P.res + cell.x] = field_height(world_xz, P.seed, P.spacing);
}
