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

const float PI = 3.14159265;

float remap(float v, float a, float b, float c, float d){ return c + (v - a) * (d - c) / max(b - a, 1e-5); }

float type_gradient(float h, float type){
    float baseRound = smoothstep(0.0, 0.15, h);
    float topFade = 1.0 - smoothstep(mix(0.5, 0.95, type), 1.0, h);
    return baseRound * topFade;
}

// World-space density: p is a real world-space point; base_y/top_y are the cloud
// slab's world Y extent. lp keeps WORLD XZ so the same noise lookup is shared with
// cloud_shadow.glsl → the ground shadow lands at the same world XZ as the cloud.
float sample_density(vec3 p, float base_y, float top_y, vec2 windOff){
    float h = clamp((p.y - base_y) / max(top_y - base_y, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, p.y - base_y, p.z);

    vec2 wuv = lp.xz * 0.00008 + windOff * 0.00008;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage + (w.r - 0.5) * 0.6, 0.0, 1.0);
    float type = clamp(P.cloud_type + (w.g - 0.5) * 0.4, 0.0, 1.0);

    float sScale = 0.0006 / max(P.size, 0.01);
    vec3 suv = lp * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.3, 1.0, 0.0, 1.0);

    float band = mix(0.5, 0.02, P.edge);
    float lo = clamp(1.0 - coverage, 0.0, 1.0);
    base = clamp(remap(base, lo, min(lo + band, 1.0), 0.0, 1.0), 0.0, 1.0);

    base *= type_gradient(h, type);

    if (base > 0.0 && P.detail > 0.0){
        float dScale = 0.004 / max(P.detail_size, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x, h, windOff.y) * dScale;
        float det = texture(detail_tex, duv).r;
        base = clamp(remap(base, det * 0.6 * P.detail, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return base * P.density;
}

float hg(float cosA, float g){
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
}

float light_march(vec3 p, vec3 L, float base_y, float top_y, vec2 windOff){
    const int LSTEPS = 6;
    float lss = P.thickness / float(LSTEPS) * 0.5;
    float d = 0.0;
    vec3 q = p;
    for (int i = 0; i < LSTEPS; i++){ q += L * lss; d += sample_density(q, base_y, top_y, windOff) * lss; }
    d += sample_density(p + L * lss * 18.0, base_y, top_y, windOff) * lss;
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

    // World-space cloud slab: rays start at the CAMERA (P.cam_world) and intersect two
    // horizontal planes at world Y = altitude and altitude+thickness. Because density is
    // then sampled at true world XZ, a cloud here casts its shadow at the same world XZ
    // (offset by sun angle) — clouds + shadow map share one frame. (Was: dome at infinity
    // from the origin → dome and shadows drifted apart + swam with camera motion.)
    vec3 rd = dir_from_texel(px);
    vec3 ro = P.cam_world.xyz;
    float cloudBase = P.altitude;
    float cloudTop  = P.altitude + P.thickness;
    float tBase = (cloudBase - ro.y) / max(rd.y, 1e-4);
    float tTop  = (cloudTop  - ro.y) / max(rd.y, 1e-4);
    float tStart = max(min(tBase, tTop), 0.0);
    float tEnd   = max(tBase, tTop);

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
            float dens = sample_density(p, cloudBase, cloudTop, windOff);
            if (dens > 0.001){
                float lightT = light_march(p, L, cloudBase, cloudTop, windOff);
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
