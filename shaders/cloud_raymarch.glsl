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

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D out_tex;   // read+write: temporal history
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
    float perdeck;       // 0 = global phase/albedo (legacy A), 1 = per-deck lighting (B)
    float dbgdeck;       // >0.5 = deck-ID overlay (flat per-deck color instead of lighting)
    // TAIL — a single vec4 (16-aligned) holds the trailing scalars so the layers[] vec4
    // array below starts on a 16-byte boundary. Hand-packed std430 is fragile: scalars
    // before a vec2/vec4 drift the offset (a vec2 wind_offset here put layers[] off by 4
    // bytes → scrambled → NO CLOUDS). Keeping the tail as ONE vec4 + the C# writer padding
    // to 16 before it removes the hazard. x=wind_x, y=wind_y, z=cell_scale, w=layer_count.
    vec4 tail;
    // 8 layers × 20 floats = 5 vec4 PER LAYER (40 vec4). vec4[] (not float[]) so the stride
    // is tight 16B and matches CloudLayers.Pack's contiguous float run.
    // fields 0-11 = density (byte-identical to cloud_shadow.glsl); 12-19 = per-deck lighting.
    vec4 layers[40];
} P;
#define WIND vec2(P.tail.x, P.tail.y)
#define CELL_SCALE P.tail.z
#define LAYER_COUNT P.tail.w
// per-layer field accessor. f: 0 alt,1 thick,2 size,3 cell,4 covW,5 dens,6 opac,7 type,
// 8 edge,9 detail,10 detailSize,11 noiseId, 12 phaseG,13 phaseIso,14 albedo,15 sunAbsorb,
// 16 tintR,17 tintG,18 tintB,19 reserved.
#define LF(i, f) P.layers[(i)*5 + ((f)>>2)][(f)&3]

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
const float SHAPE_SCALE   = 1.0 / 6000.0;   // smaller individual clouds (was 1/9000 = ~giant)
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

    // PER-SCALE WIND (audit): shape/cell/detail scroll at DIFFERENT rates+directions so the
    // scales parallax against each other instead of translating in lockstep (lockstep reads
    // rigid/procedural in motion). windOff is the base; derive offset scrolls per scale.
    vec2 wShape  = windOff;
    vec2 wCell   = windOff * 0.55 + vec2(-windOff.y, windOff.x) * 0.18;   // slower + slight curl
    vec2 wDetail = windOff * 1.7  + vec2(windOff.y, -windOff.x) * 0.35;   // faster + opposite curl

    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;

    float sScale = SHAPE_SCALE / max(lsize, 0.01);
    vec3 suv = lpw * sScale + vec3(wShape.x, h, wShape.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.45, 1.0, 0.0, 1.0);   // stronger base erosion (was 0.3)

    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, ledge), 1.0);
    float shape = smoothstep(thresh, soft, base);

    // CELLULARITY — keep cell SEPARATION even at high coverage (don't merge into a sheet).
    // The gate's pass-band stays narrow at high cov (lo→0.42, hi→0.78) so cells keep their
    // seams; high coverage fills cells but the inter-cell gaps persist (audit fix #5).
    float cellScale = sScale * 0.7 * max(lcell, 0.05);   // higher cell freq → MANY clumps, not few giants
    float cell = texture(shape_tex, lpw * cellScale + vec3(wCell.x, h, wCell.y) * cellScale).g;
    float cellGate = smoothstep(mix(0.80, 0.42, coverage), mix(1.0, 0.78, coverage), cell);
    shape *= cellGate;

    shape *= type_gradient(h, type);
    if (shape <= 0.0) return 0.0;

    // DETAIL EROSION — stronger + biting harder at edges (low shape) and tops (audit:
    // round blobs = erosion too weak). erodeAmt up; also erode more where shape is small.
    if (ldetail > 0.0){
        float dScale = DETAIL_SCALE / max(ldetsize, 0.01);
        vec3 duv = lp * dScale + vec3(wDetail.x, h, wDetail.y) * dScale;
        // two-octave detail erosion → finer cauliflower than a single tap (the 32³ detail
        // volume alone reads soft/blobby). A 3.1× finer octave carves small-scale bumps.
        float det = texture(detail_tex, duv).r;
        float det2 = texture(detail_tex, duv * 3.1 + vec3(0.37)).r;
        det = det * 0.6 + det2 * 0.4;
        float edgeBoost = mix(2.3, 0.5, shape);              // erode edges MUCH harder → crisp silhouettes
        float erodeAmt = mix(0.45, 0.95, h) * ldetail * edgeBoost;
        shape = clamp(remap(shape, det * erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * ldens * densBias;   // opacity applied by the caller (sigma)
}

// Sum every active layer whose band contains p. Returns total density + a density-weighted
// opacity. activeOut = the index of the densest-contributing deck at p (decks are
// altitude-separated so usually exactly one), used by the caller to pick per-deck lighting.
float density_all(vec3 p, vec2 windOff, out float opacOut, out int activeOut){
    int n = clamp(int(LAYER_COUNT), 1, 8);
    float total = 0.0; float opAccum = 0.0; float r = length(p);
    float bestD = -1.0; activeOut = 0;
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r < baseR || r > topR) continue;
        float d = layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4));
        total += d; opAccum += d * LF(i,6);
        if (d > bestD){ bestD = d; activeOut = i; }
    }
    opacOut = (total > 1e-5) ? opAccum / total : 1.0;
    return total;
}
// density-only overload (sun light-march doesn't care which deck).
float density_all(vec3 p, vec2 windOff, out float opacOut){
    int unused; return density_all(p, windOff, opacOut, unused);
}

// deck-ID overlay palette (debug): each deck gets a saturated flat color so the user can
// SEE which sky regions belong to which deck (cumulus=warm, cirrus=cyan, etc.).
vec3 deck_dbg_color(int i){
    if (i == 0) return vec3(1.0, 0.30, 0.18);   // cumulus — warm red
    if (i == 1) return vec3(0.20, 0.65, 1.0);   // cirrus  — cyan
    if (i == 2) return vec3(0.35, 1.0, 0.35);    // green
    if (i == 3) return vec3(1.0, 0.85, 0.25);    // amber
    return vec3(1.0, 0.4, 1.0);                  // magenta
}

float hg(float cosA, float g){
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
}

// light march toward the sun summing ALL decks — returns raw OPTICAL DEPTH toward the sun
// (the caller does multi-octave multiple-scattering from it, per the audit). Cone-ish: a
// long final tap captures distant self-shadowing cheaply.
float light_optical_depth(vec3 p, vec3 L, vec2 windOff){
    const int LSTEPS = 6;
    float lss = 220.0;
    float d = 0.0; vec3 q = p; float op;
    for (int i = 0; i < LSTEPS; i++){ q += L * lss; d += density_all(q, windOff, op) * lss; }
    d += density_all(p + L * lss * 16.0, windOff, op) * lss;   // far tap (distant deck shadowing)
    // SUN extinction coefficient. Was 0.02 → for any real cloud the summed sun-path density
    // drove od to ~10-30, so exp(-od)≈0 EVERYWHERE: direct sun never reached the cloud and
    // it was lit by ambient ONLY (measured meanCloudLuma 0.187 = dim grey mush). Recalibrated
    // to 0.0045 so sunward faces land at od~0.5-2 (bright) while cores stay od~5-8 (dark) —
    // bright white clouds WITH 3D self-shadow form. (Raymarch self-lighting only; the ground
    // shadow map is a separate shader → cloud↔shadow coupling/shadowcheck unaffected.)
    return d * P.sun_absorb * 0.0035;
}

// dual-lobe phase: forward silver-lining (g1) + back-scatter (g2). Replaces single HG + 0.4.
float phase_dual(float cosA){
    return mix(hg(cosA, P.hg_aniso), hg(cosA, -0.25), 0.35);
}

// radial height fraction of point p within whichever deck contains it (0 base .. 1 top),
// for base-occluded ambient. 0.5 if between decks (harmless — ambient unused where dens=0).
float height_frac(vec3 p){
    float r = length(p);
    int n = clamp(int(LAYER_COUNT), 1, 8);
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r >= baseR && r <= topR) return clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    }
    return 0.5;
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

    // TEMPORAL AMORTIZATION (roadmap #4, toggleable). Update only 1/stride of texels each frame,
    // cycling the offset over `stride` frames; non-updated texels PERSIST from prior frames
    // (history lives in out_tex, now read+write). Refreshed texels BLEND with history to smooth
    // the refresh seam. stride=1 (temporal_frames=1, the default) = every texel every frame = the
    // validated look, zero amortization. Higher stride = cheaper (fewer marches) but stale texels
    // can shimmer under fast drift — the dispersed (idx%stride) pattern keeps it noise, not rows.
    // Evaluate per scene; safe because default is OFF.
    int stride = clamp(int(P.update.y), 1, 16);
    int idx = px.y * int(P.tex_size.x) + px.x;
    vec4 history = imageLoad(out_tex, px);
    if (stride > 1 && (idx % stride) != int(P.update.x)) { return; }   // keep history (persistence)

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
        // STEPPING (audit fix #3): a FIXED cloud-relative fine step sized to the THINNEST
        // active deck (~1.5% of its thickness → ~64 samples through it), with empty-space
        // skip taking coarse strides through the void between decks. The old uniform
        // dt=span/steps put ~3 samples through a 1km deck (mush) + wasted budget on the
        // ~3km inter-deck gap. maxSteps caps total iterations for the budget.
        float minThick = 1e9;
        for (int i = 0; i < nL; i++){ minThick = min(minThick, LF(i,1)); }
        float fineStep = max(minThick * 0.015, 12.0);     // in-cloud step (m)
        float coarseStep = fineStep * 8.0;                // empty-air step (m)
        int maxSteps = clamp(int(P.steps), 32, 256);

        vec2 windOff = WIND;   // CPU-integrated; no teleport when speed/dir changes

        vec3 L = normalize(P.sun_dir.xyz);
        float cosA = dot(rd, L);
        float globalPhase = phase_dual(cosA);   // legacy uniform phase (P.perdeck blends away from it)
        vec3 sunCol = P.sun_color.rgb * P.sun_dir.w;
        // Mood sky fill; modulated per-voxel by height (base-occluded) below.
        vec3 skyAmbient = mix(P.sky_horizon.rgb, P.sky_top.rgb, clamp(rd.y, 0.0, 1.0));
        float forward = pow(max(cosA, 0.0), 6.0);   // for view-gated powder (back-lit only)

        float T = 1.0;
        vec3 scattered = vec3(0.0);
        float t = tStart;
        for (int i = 0; i < maxSteps && t < tEnd; i++){
            vec3 p = ro + rd * t;
            float opac; int act; float dens = density_all(p, windOff, opac, act);   // sum all decks; act = densest deck
            if (dens > 0.001){
                // PER-DECK LIGHTING (roadmap #1): the deck containing p picks its own phase,
                // albedo, sun-absorption + tint so cumulus reads forward-scattering/dark-cored
                // and cirrus near-isotropic/thin/bright. Blended toward the legacy global look
                // by (1 - P.perdeck) so the --perdeck toggle A/Bs it in motion.
                float pdeck   = clamp(P.perdeck, 0.0, 1.0);
                float perPhase = mix(hg(cosA, LF(act,12)), hg(cosA, -0.25), LF(act,13));
                float phase   = mix(globalPhase, perPhase, pdeck);
                float albedo  = mix(1.0, LF(act,14), pdeck);
                float absorb  = mix(1.0, LF(act,15), pdeck);
                vec3  tint    = mix(vec3(1.0), vec3(LF(act,16), LF(act,17), LF(act,18)), pdeck);

                float dt = fineStep;
                float od = light_optical_depth(p, L, windOff) * absorb;   // per-deck sun absorption
                // MULTIPLE-SCATTERING: a SHARP direct term (exp(-od)*phase) → dark self-shadowed
                // cores = 3D FORM, plus a softer isotropic fill (exp(-od*0.25)) for voluminous
                // interior light WITHOUT flattening. The old 3-octave loop summed ~1.75 weight
                // with weak extinction decay so cores stayed lit (flat). This separates the
                // crisp sun-modeling (direct) from the soft body fill (MS).
                float sun = exp(-od) * phase + 0.45 * exp(-od * 0.25);
                // BASE-OCCLUDED ambient: base dark (sky-occluded), tops bright → 3D form.
                float h = height_frac(p);
                vec3 amb = skyAmbient * P.ambient * mix(0.25, 1.0, h);
                // powder (dark edges) only when looking TOWARD the sun (back-lit); gated.
                float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * P.powder), forward);
                // VIEW-ray extinction. Was 0.02 → meanAlpha only ~0.52 (clouds half-transparent,
                // read as wispy haze not solid masses = "not a ton of clouds"). Raised so healthy
                // cloud bodies reach high opacity within a deck while wisps stay translucent.
                float sigma = dens * 0.05 * opac;
                float beer = exp(-sigma * dt);
                vec3 lum = (sunCol * sun * powder * albedo * tint + amb) * P.brightness;
                if (P.dbgdeck > 0.5) lum = deck_dbg_color(act) * 1.5;   // deck-ID overlay: flat per-deck color
                scattered += T * lum * (1.0 - beer);
                T *= beer;
                if (T < 0.01) break;
                t += fineStep;     // fine step while inside cloud
            } else {
                t += coarseStep;   // empty-space skip through the void between decks
            }
        }
        result = vec4(scattered, clamp(1.0 - T, 0.0, 1.0));
    }
    // temporal blend on REFRESHED texels (smooths the strided-refresh seam vs persisted history).
    if (stride > 1) { result = mix(history, result, 0.6); }
    imageStore(out_tex, px, result);
}
