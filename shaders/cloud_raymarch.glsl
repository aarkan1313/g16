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
    // TAIL — a single vec4 (16-aligned) holds the trailing scalars so the layers[] vec4
    // array below starts on a 16-byte boundary. Hand-packed std430 is fragile: scalars
    // before a vec2/vec4 drift the offset (a vec2 wind_offset here put layers[] off by 4
    // bytes → scrambled → NO CLOUDS). Keeping the tail as ONE vec4 + the C# writer padding
    // to 16 before it removes the hazard. x=wind_x, y=wind_y, z=cell_scale, w=layer_count.
    vec4 tail;
    // 8 layers × 12 floats = 3 vec4 PER LAYER (24 vec4). vec4[] (not float[]) so the stride
    // is tight 16B and matches CloudLayers.Pack's contiguous float run.
    vec4 layers[24];
} P;
#define WIND vec2(P.tail.x, P.tail.y)
#define CELL_SCALE P.tail.z
#define LAYER_COUNT P.tail.w
// per-layer field accessor (f: 0 alt,1 thick,2 size,3 cell,4 covW,5 dens,6 opac,7 type,8 edge,9 detail,10 detailSize,11 noiseId)
#define LF(i, f) P.layers[(i)*3 + ((f)>>2)][(f)&3]

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

// ===== PER-LAYER DENSITY — MUST stay byte-identical to cloud_shadow.glsl's layer_density.
// One cloud deck's density at world point p. baseR/topR = this deck's shell radii; all shape
// params come from the LAYER args (so each deck differs in size/clump/type/etc). Same HZD
// recipe + anti-repeat (mismatched scales, domain warp, weather field, height erosion) +
// cellularity as before — just parameterized per layer instead of from P.* globals.
float layer_density(vec3 p, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);

    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage * covW + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(ltype + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);

    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;

    float sScale = SHAPE_SCALE / max(lsize, 0.01);
    vec3 suv = lpw * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.3, 1.0, 0.0, 1.0);

    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, ledge), 1.0);
    float shape = smoothstep(thresh, soft, base);

    float cellScale = sScale * 0.35 * max(lcell, 0.05);
    float cell = texture(shape_tex, lpw * cellScale + vec3(windOff.x, h, windOff.y) * cellScale).g;
    float cellGate = smoothstep(mix(0.78, 0.35, coverage), mix(1.0, 0.6, coverage), cell);
    shape *= cellGate;

    shape *= type_gradient(h, type);
    if (shape <= 0.0) return 0.0;

    if (ldetail > 0.0){
        float dScale = DETAIL_SCALE / max(ldetsize, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x * 1.7, h, windOff.y * 1.7) * dScale;
        float det = texture(detail_tex, duv).r;
        float erodeAmt = mix(0.25, 0.6, h) * ldetail;
        shape = clamp(remap(shape, det * erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * ldens * densBias;   // opacity applied by the caller (sigma)
}

// Sum every active layer whose band contains p. Returns total density + a density-weighted
// opacity (so the caller's extinction reflects the mix of decks at that point).
float density_all(vec3 p, vec2 windOff, out float opacOut){
    int n = clamp(int(LAYER_COUNT), 1, 8);
    float total = 0.0; float opAccum = 0.0; float r = length(p);
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r < baseR || r > topR) continue;
        float d = layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4));
        total += d; opAccum += d * LF(i,6);
    }
    opacOut = (total > 1e-5) ? opAccum / total : 1.0;
    return total;
}

float hg(float cosA, float g){
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
}

// light march toward the sun summing ALL decks (fixed metres/step, layer-agnostic).
float light_march_all(vec3 p, vec3 L, vec2 windOff){
    const int LSTEPS = 6;
    float lss = 250.0;
    float d = 0.0; vec3 q = p; float op;
    for (int i = 0; i < LSTEPS; i++){ q += L * lss; d += density_all(q, windOff, op) * lss; }
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
    // full span across all ACTIVE decks: march one shell from min-base to max-top.
    int nL = clamp(int(LAYER_COUNT), 1, 8);
    float minBase = 1e9, maxTop = -1e9;
    for (int i = 0; i < nL; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
    float belowBase = min(P.cam_world.y, minBase - 1.0);
    vec3 ro = vec3(P.cam_world.x, PLANET_R + belowBase, P.cam_world.z);
    float baseR = PLANET_R + minBase;
    float topR  = PLANET_R + maxTop;
    vec2 hitB = ray_sphere(ro, rd, baseR);
    vec2 hitT = ray_sphere(ro, rd, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);

    vec4 result = vec4(0.0);
    if (rd.y > 0.02 && tEnd > tStart){
        int steps = clamp(int(P.steps), 16, 160);
        float dt = (tEnd - tStart) / float(steps);

        vec2 windOff = WIND;   // CPU-integrated; no teleport when speed/dir changes

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
        float t = tStart; float emptyRun = 0.0;
        for (int i = 0; i < steps; i++){
            vec3 p = ro + rd * t;
            float opac; float dens = density_all(p, windOff, opac);   // sum all decks
            if (dens > 0.001){
                emptyRun = 0.0;
                float lightT = light_march_all(p, L, windOff);
                float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * P.powder), 0.5);
                float sigma = dens * 0.02 * opac;   // density-weighted per-deck opacity
                float beer = exp(-sigma * dt);
                float sun = lightT * (phase + 0.4);
                vec3 lum = (sunCol * sun + skyAmbient * P.ambient) * P.brightness;
                lum *= powder;
                scattered += T * lum * (1.0 - beer);
                T *= beer;
                if (T < 0.01) break;
                t += dt;
            } else {
                // step acceleration: bigger stride through empty air between decks
                emptyRun += 1.0;
                t += dt * (1.0 + min(emptyRun, 4.0));
            }
        }
        result = vec4(scattered, clamp(1.0 - T, 0.0, 1.0));
    }
    imageStore(out_tex, px, result);
}
