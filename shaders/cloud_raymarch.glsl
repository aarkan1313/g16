#[compute]
#version 450

// Volumetric cloud raymarch (Stage 4, Path A). Raymarches the cloud shell into an
// rgba16f texture (rgb = in-scattered radiance, a = cloud alpha), parameterized as
// a lat-long map of the SKY hemisphere (x = azimuth 0..2π, y = elevation 0..π/2).
// The sky shader just SAMPLES this texture by view direction — so the expensive
// march runs here, on the MAIN RenderingDevice, and is AMORTIZED: each dispatch
// updates only a strided subset of texels (params.update_offset / update_stride),
// cycling over N frames. Mirrors the density/lighting math of cloud_sky.gdshader.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict writeonly image2D out_tex;
layout(set = 0, binding = 1) uniform sampler3D shape_tex;
layout(set = 0, binding = 2) uniform sampler3D detail_tex;
layout(set = 0, binding = 3) uniform sampler2D weather_tex;

layout(set = 0, binding = 4, std430) restrict buffer ParamsBuf {
    vec4 sun_dir;        // xyz dir, w energy
    vec4 sun_color;      // rgb, a unused
    vec4 sky_top;
    vec4 sky_horizon;
    vec4 cam_world;      // xyz world-space ray origin (the camera), w unused
    vec2 tex_size;       // output texture dims
    vec2 update;         // x = offset index, y = stride (frames to spread over)
    float time;
    float coverage, density, cloud_type;
    float altitude, thickness;
    float drift_speed, drift_dir;
    float hg_aniso, powder, sun_absorb;
    float size, detail, detail_size, edge, opacity, brightness, ambient;
    float steps;
    float _pad0, _pad1;
} P;

const float PLANET_R = 200000.0;
const float PI = 3.14159265;

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

// Anti-repetition scale consts (world meters → texture UV). DELIBERATELY MISMATCHED,
// non-integer-related periods so the combined tiling period is enormous (defense #1):
//   weather ~80 km · shape ~9 km · detail ~1.3 km · warp ~ low-freq detail tap.
const float WEATHER_SCALE = 1.0 / 80000.0;
const float SHAPE_SCALE   = 1.0 / 9000.0;
const float DETAIL_SCALE  = 1.0 / 1300.0;
const float WARP_AMOUNT   = 600.0;          // domain-warp displacement in meters (defense #2)

// ===== SHARED DENSITY — MUST stay byte-identical to cloud_shadow.glsl's sample_density.
// HZD recipe on a curved shell (baseR/topR planet radii; h = radial height fraction; lp
// keeps WORLD XZ so the ground shadow lands at the same world XZ as the visible cloud).
// Anti-repeat: mismatched sample scales (#1) + domain warp (#2) + large weather field (#3)
// + height-varied erosion (#4). Coverage raises the THRESHOLD so coverage=0 → TRUE CLEAR
// (nothing passes) and coverage=1 → overcast; the knob spans the full range.
float sample_density(vec3 p, float baseR, float topR, vec2 windOff){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);

    // #3 large low-freq weather field — macro placement never repeats in view.
    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(P.cloud_type + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);             // per-region density variation

    // #2 domain warp — bend tiling seams into organic shapes (the biggest "kills the
    // procedural look" lever). Warp by a low-freq detail tap before sampling the shape.
    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;

    // #1 shape at a scale MISMATCHED from the detail scale below.
    float sScale = SHAPE_SCALE / max(P.size, 0.01);
    vec3 suv = lpw * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.3, 1.0, 0.0, 1.0);

    // coverage → threshold: cov0 → thresh 0.92 (almost nothing passes = clear sky);
    // cov1 → thresh 0.02 (overcast). Smooth, full range.
    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, P.edge), 1.0);
    float shape = smoothstep(thresh, soft, base);
    shape *= type_gradient(h, type);                 // rounded bottoms, wispy tops
    if (shape <= 0.0) return 0.0;                     // hard gap → sky shows through

    // #4 detail erosion at a MISMATCHED scale, height-varied (erode tops more than bases).
    if (P.detail > 0.0){
        float dScale = DETAIL_SCALE / max(P.detail_size, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x * 1.7, h, windOff.y * 1.7) * dScale;
        float det = texture(detail_tex, duv).r;
        float erodeAmt = mix(0.25, 0.6, h) * P.detail;
        shape = clamp(remap(shape, det * erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * P.density * densBias;
}

float hg(float cosA, float g){
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
}

float light_march(vec3 p, vec3 L, float baseR, float topR, vec2 windOff){
    const int LSTEPS = 6;
    float lss = P.thickness / float(LSTEPS) * 0.5;
    float d = 0.0;
    vec3 q = p;
    for (int i = 0; i < LSTEPS; i++){ q += L * lss; d += sample_density(q, baseR, topR, windOff) * lss; }
    d += sample_density(p + L * lss * 18.0, baseR, topR, windOff) * lss;
    return exp(-d * P.sun_absorb * 0.02);
}

// texel → view direction (lat-long over the upper hemisphere)
vec3 dir_from_texel(ivec2 px){
    vec2 uv = (vec2(px) + 0.5) / P.tex_size;
    float az = uv.x * 2.0 * PI;
    float el = uv.y * (0.5 * PI);          // 0 horizon .. π/2 zenith
    float ce = cos(el);
    return normalize(vec3(cos(az) * ce, sin(el), sin(az) * ce));
}

vec3 background(vec3 dir){
    float t = pow(clamp(dir.y, 0.0, 1.0), 0.5);
    return mix(P.sky_horizon.rgb, P.sky_top.rgb, t);
}

void main(){
    ivec2 px = ivec2(gl_GlobalInvocationID.xy);
    if (px.x >= int(P.tex_size.x) || px.y >= int(P.tex_size.y)) return;

    // Temporal amortization (strided subset per frame). NOTE (audit L3): this updates
    // texels in place with NO double-buffer/reprojection, so stride>1 leaves stale
    // texels = visible stripes under drift. Until proper reconstruction is added, the
    // C# side is clamped to stride 1; this guard is belt-and-suspenders so a stray
    // value can't smear. (Full reconstruction is the future upgrade — see DECISIONS.)
    int stride = clamp(int(P.update.y), 1, 1);   // forced 1 until reconstruction exists
    int idx = px.y * int(P.tex_size.x) + px.x;
    if ((idx % stride) != int(P.update.x)) return;

    // Curved shell anchored at the CAMERA (world space → density samples land at true
    // world XZ, so the ground shadow matches the visible cloud). The curved shell (vs a
    // flat slab) makes horizon rays traverse a longer chord → a real horizon cloud band.
    // Clamp the origin to just BELOW the cloud base when the camera is above the layer —
    // clouds aren't geometry; always march as if viewed from under the shell (fixes
    // "clouds vanish from above"). Sampling stays world XZ → shadow stays coupled.
    vec3 rd = dir_from_texel(px);
    float belowBase = min(P.cam_world.y, P.altitude - 1.0);
    vec3 ro = vec3(P.cam_world.x, PLANET_R + belowBase, P.cam_world.z);
    float baseR = PLANET_R + P.altitude;
    float topR  = baseR + P.thickness;
    vec2 hitB = ray_sphere(ro, rd, baseR);
    vec2 hitT = ray_sphere(ro, rd, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);

    vec4 result = vec4(0.0);
    if (rd.y > 0.02 && tEnd > tStart){
        int steps = clamp(int(P.steps), 16, 160);
        float dt = (tEnd - tStart) / float(steps);

        float ang = radians(P.drift_dir);
        vec2 windOff = vec2(cos(ang), sin(ang)) * P.time * P.drift_speed;

        vec3 L = normalize(P.sun_dir.xyz);
        float cosA = dot(rd, L);
        float phase = mix(hg(cosA, P.hg_aniso), hg(cosA, -0.2), 0.3);
        vec3 sunCol = P.sun_color.rgb * P.sun_dir.w;
        // Ambient/sky fill from the MOOD sky colors (passed in): top-of-sky tint blends
        // toward the warmer horizon near the bottom of the cloud. So golden-hour gives
        // warm fill, overcast grey, blue-dawn cool — clouds track the mood automatically.
        vec3 skyAmbient = mix(P.sky_horizon.rgb, P.sky_top.rgb, clamp(rd.y, 0.0, 1.0));

        float T = 1.0;
        vec3 scattered = vec3(0.0);
        float t = tStart;
        for (int i = 0; i < steps; i++){
            vec3 p = ro + rd * t;
            float dens = sample_density(p, baseR, topR, windOff);
            if (dens > 0.001){
                float lightT = light_march(p, L, baseR, topR, windOff);
                float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * P.powder), 0.5);
                float sigma = dens * 0.02 * P.opacity;
                float beer = exp(-sigma * dt);
                float sun = lightT * (phase + 0.4);
                vec3 lum = (sunCol * sun + skyAmbient * P.ambient) * P.brightness;
                lum *= powder;
                scattered += T * lum * (1.0 - beer);
                T *= beer;
                if (T < 0.01) break;
            }
            t += dt;
        }
        result = vec4(scattered, clamp(1.0 - T, 0.0, 1.0));
    }
    imageStore(out_tex, px, result);
}
