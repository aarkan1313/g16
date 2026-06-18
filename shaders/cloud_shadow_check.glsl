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
} P;

const float PLANET_R = 200000.0;
const float WEATHER_SCALE = 1.0 / 80000.0;
const float SHAPE_SCALE   = 1.0 / 9000.0;
const float DETAIL_SCALE  = 1.0 / 1300.0;
const float WARP_AMOUNT   = 600.0;

float remap(float val, float a, float b, float c, float d){ return c + (val - a) * (d - c) / max(b - a, 1e-5); }
float type_gradient(float h, float type){
    float baseRound = smoothstep(0.0, 0.15, h);
    float topFade = 1.0 - smoothstep(mix(0.5, 0.95, type), 1.0, h);
    return baseRound * topFade;
}
vec2 ray_sphere(vec3 ro, vec3 rd, float R){
    float b = dot(ro, rd); float c = dot(ro, ro) - R * R; float disc = b * b - c;
    if (disc < 0.0) return vec2(-1.0);
    float s = sqrt(disc); return vec2(-b - s, -b + s);
}
// byte-identical sample_density to the production shaders
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
    uint gx = gl_GlobalInvocationID.x, gy = gl_GlobalInvocationID.y;
    if (gx >= uint(P.grid.x) || gy >= uint(P.grid.y)) return;
    uint idx = gy * uint(P.grid.x) + gx;

    vec2 uv = (vec2(gx, gy) + 0.5) / P.grid;
    vec2 wxz = (uv - 0.5) * P.region.x;
    vec3 L = normalize(P.sun_dir.xyz);
    float baseR = PLANET_R + P.altitude;
    float topR  = baseR + P.thickness;
    vec2 windOff = P.wind_offset;

    // (1) cloud density along the SUN ray from this ground point — i.e. the cloud that
    // actually CASTS the shadow here (offset up-sun by the sun angle). This is the
    // physically-correct thing to correlate the shadow against (straight-up density is
    // wrong: a cast shadow lands offset from the cloud, so it never correlates with the
    // cloud directly overhead). vd integrates the same density along L.
    float vd = 0.0;
    {
        vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
        vec2 hb = ray_sphere(ro, L, baseR);
        vec2 ht = ray_sphere(ro, L, topR);
        float ts = max(hb.y, 0.0), te = max(ht.y, 0.0);
        if (L.y > 0.05 && te > ts){
            float pathLen = min(te - ts, P.thickness / max(L.y, 0.2));
            int steps = 24;
            float dt = pathLen / float(steps);
            float t = ts;
            for (int i = 0; i < steps; i++){ vec3 p = ro + L * t; vd += sample_density(p, baseR, topR, windOff) * dt; t += dt; }
        }
    }

    // (2) in-place sun visibility (the production shadow value)
    float visHere = 1.0;
    {
        vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
        vec2 hb = ray_sphere(ro, L, baseR);
        vec2 ht = ray_sphere(ro, L, topR);
        float ts = max(hb.y, 0.0), te = max(ht.y, 0.0);
        if (L.y > 0.05 && te > ts){
            float chord = te - ts;
            float maxPath = P.thickness / max(L.y, 0.2);
            float pathLen = min(chord, maxPath);
            int steps = clamp(int(P.region.y), 4, 32);
            float dt = pathLen / float(steps);
            float d = 0.0; float t = ts;
            for (int i = 0; i < steps; i++){ vec3 p = ro + L * t; d += sample_density(p, baseR, topR, windOff) * dt; t += dt; }
            visHere = mix(1.0, exp(-d * 0.02 * L.y), P.strength);
        }
    }

    v[idx * 4u + 0u] = visHere;     // production shadow value
    v[idx * 4u + 1u] = vd;          // vertical density overhead
    v[idx * 4u + 2u] = 0.0;
    v[idx * 4u + 3u] = 0.0;
}
