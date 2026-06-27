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
    // 8 layers × 24 floats = 6 vec4 PER LAYER (48 vec4). vec4[] (not float[]) so the stride
    // is tight 16B and matches CloudLayers.Pack's contiguous float run.
    // fields 0-11 = density (byte-identical to cloud_shadow.glsl); 12-18 = per-deck lighting;
    // 19-21 = vertical profile (CO-1, also byte-identical to the shadow shader).
    vec4 layers[48];
    // AT-3 physical cloud lighting: 3 sun-dependent colors read back from the atmosphere LUTs (CPU, on
    // sun change) and pushed as params — NOT sampled cross-node in this compute (that hazards the GPU; the
    // values are per-frame constants anyway). Used only when P.sun_color.a > 0; a == 0 → the mood path.
    vec4 atmo_zenith;     // sky-view radiance at the zenith (cloud-top ambient)
    vec4 atmo_horizon;    // sky-view radiance at the horizon toward the sun (cloud-underside ambient)
    vec4 atmo_suntrans;   // sun transmittance toward the sun (reddened direct-light color)
    // NIGHT MOONLIGHT (appended at the very end → std430-safe, no offset shift; raymarch-only). A weak 2nd
    // directional light so night/dusk clouds aren't black. w = phase·presence·user strength (0 = skip → free).
    vec4 moon_dir;        // xyz = unit dir TO the moon, w = cloud-light strength
    vec4 moon_color;      // rgb = moon tint (cool white), a unused
    vec4 visual_origin;   // xy = sky-cloud density origin, z = camera_parallax, w unused
} P;
#define WIND vec2(P.tail.x, P.tail.y)
#define CELL_SCALE P.tail.z
#define LAYER_COUNT P.tail.w
#define CLOUD_ORIGIN P.visual_origin.xy
// per-layer field accessor. f: 0 alt,1 thick,2 size,3 cell,4 covW,5 dens,6 opac,7 type,
// 8 edge,9 detail,10 detailSize,11 noiseId, 12 phaseG,13 phaseIso,14 albedo,15 sunAbsorb,
// 16 tintR,17 tintG,18 tintB, 19 profileBottom,20 profileTop,21 anvil, 22-23 reserved.
#define LF(i, f) P.layers[(i)*6 + ((f)>>2)][(f)&3]

// @@INCLUDE cloud_density
const float PI = 3.14159265;

// ===== PER-LAYER DENSITY. The SHARED shape (weather/coverage/cellularity/type_gradient/
// height_profile + scale consts) comes from cloud_density.gdshaderinc and is identical across the
// sky / shadow / check computes. The DETAIL-EROSION below is deliberately the CRISP variant (two
// octaves, harder edge erosion) — the sky needs fine cauliflower silhouettes. cloud_shadow.glsl
// uses a cheaper 1-octave variant; that divergence is intentional, NOT drift.
// One cloud deck's density at local shell point p. worldXZ is the visible sky-cloud
// footprint for weather/noise; camera_parallax=1 makes it the true camera-world path.
// baseR/topR = this deck's shell radii; all shape params come from the LAYER args
// (so each deck differs in size/clump/type/etc).
float layer_density(vec3 p, vec2 worldXZ, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW,
                    float pBottom, float pTop, float anvil, float shapeMode, float antiRep){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(worldXZ.x, r - baseR, worldXZ.y);

    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage * covW + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(ltype + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);
    // MACRO VARIETY (CO-3 anti-repetition): a mid-scale (~11 km) weather tap clusters cumulus into
    // varying-size groups with clearer gaps, breaking the uniform same-size-puff repetition. antiRep=0
    // → unchanged (gated, so zero cost for default cumulus). MUST stay byte-identical across shaders.
    if (antiRep > 0.0){
        vec2 mmuv = lp.xz * (1.0/11000.0) + windOff * (1.0/11000.0);
        vec4 wm = texture(weather_tex, mmuv);
        coverage = clamp(coverage * mix(1.0, smoothstep(0.2, 0.8, wm.r) * 1.5, antiRep), 0.0, 1.0);
        lsize *= mix(1.0, mix(0.6, 1.7, wm.g), antiRep);   // per-region clump size variety
    }

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
    // stratus: a mostly-connected sheet but KEEP occasional gaps (broken stratus, not 100% fill);
    // coverage drives how solid — high cov → near-overcast, lower cov → more breaks.
    float sheetGate = smoothstep(mix(0.45, 0.12, coverage), mix(0.75, 0.42, coverage), cell);
    cellGate = mix(cellGate, sheetGate, shapeMode);
    shape *= cellGate;

    shape *= type_gradient(h, type);
    shape *= height_profile(h, pBottom, pTop, anvil);
    if (shape <= 0.0) return 0.0;

    // DETAIL EROSION — stronger + biting harder at edges (low shape) and tops (audit:
    // round blobs = erosion too weak). erodeAmt up; also erode more where shape is small.
    if (ldetail > 0.0){
        float baseShape = shape;
        float dScale = DETAIL_SCALE / max(ldetsize, 0.01);
        vec3 duv = lp * dScale + vec3(wDetail.x, h, wDetail.y) * dScale;
        // two-octave detail erosion → finer cauliflower than a single tap (the 32³ detail
        // volume alone reads soft/blobby). A 3.1× finer octave carves small-scale bumps.
        float detLow = texture(detail_tex, duv).r;
        float det2 = texture(detail_tex, duv * 3.1 + vec3(0.37)).r;
        float det = detLow * 0.6 + det2 * 0.4;
        float edgeBand = clamp(baseShape * (1.0 - baseShape) * 4.0, 0.0, 1.0);
        float contour = (det2 - detLow) * edgeBand * (0.20 + 0.10 * ledge) * ldetail * (1.0 - 0.55 * shapeMode);
        float edgeBoost = mix(2.3, 0.5, baseShape);          // erode edges MUCH harder → crisp silhouettes
        float erodeAmt = mix(0.45, 0.95, h) * ldetail * edgeBoost * (1.0 - 0.7 * shapeMode);   // stratus: smoother (less cauliflower)
        shape = clamp(remap(baseShape, det * erodeAmt, 1.0, 0.0, 1.0) + contour, 0.0, 1.0);
    }
    return shape * ldens * densBias;   // opacity applied by the caller (sigma)
}

// Sum every active layer whose band contains p. Returns total density + a density-weighted
// opacity. activeOut = the index of the densest-contributing deck at p (decks are
// altitude-separated so usually exactly one), used by the caller to pick per-deck lighting.
float density_all(vec3 p, vec2 worldXZ, vec2 windOff, out float opacOut, out int activeOut){
    int n = clamp(int(LAYER_COUNT), 1, 8);
    float total = 0.0; float opAccum = 0.0; float r = length(p);
    float bestD = -1.0; activeOut = 0;
    for (int i = 0; i < n; i++){
        float baseR = PLANET_R + LF(i,0), topR = baseR + LF(i,1);
        if (r < baseR || r > topR) continue;
        float d = layer_density(p, worldXZ, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4),
            LF(i,19), LF(i,20), LF(i,21), LF(i,22), LF(i,23));
        total += d; opAccum += d * LF(i,6);
        if (d > bestD){ bestD = d; activeOut = i; }
    }
    opacOut = (total > 1e-5) ? opAccum / total : 1.0;
    return total;
}
// density-only overload (sun light-march doesn't care which deck).
float density_all(vec3 p, vec2 worldXZ, vec2 windOff, out float opacOut){
    int unused; return density_all(p, worldXZ, windOff, opacOut, unused);
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
float light_optical_depth(vec3 p, vec2 worldXZ, vec3 L, vec2 windOff){
    const int LSTEPS = 6;
    float lss = 220.0;
    float d = 0.0; vec3 q = p; vec2 qxz = worldXZ; float op;
    for (int i = 0; i < LSTEPS; i++){
        q += L * lss;
        qxz += L.xz * lss;
        d += density_all(q, qxz, windOff, op) * lss;
    }
    d += density_all(p + L * lss * 16.0, worldXZ + L.xz * lss * 16.0, windOff, op) * lss;   // far tap (distant deck shadowing)
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
    // the refresh seam. stride=1 (temporal_frames=1) = every texel every frame = zero
    // amortization. The review default is 2: it preserves the converged look while halving
    // refreshed dome texels per frame; higher stride is a tuning knob but can show stale texels
    // under fast drift. The dispersed (idx%stride) pattern keeps it noise, not rows.
    int stride = clamp(int(P.update.y), 1, 16);
    int idx = px.y * int(P.tex_size.x) + px.x;
    vec4 history = imageLoad(out_tex, px);
    if (stride > 1 && (idx % stride) != int(P.update.x)) { return; }   // keep history (persistence)

    // Curved shell in the camera's local tangent frame. XZ stays local to avoid far-world
    // precision loss, but Y is the true camera altitude: flying into/above clouds must not
    // keep sampling a fake under-cloud dome. Density/weather samples use CLOUD_ORIGIN, which
    // equals true camera XZ only when camera_parallax is 1.
    vec3 rd = dir_from_texel(px);
    // full span across all ACTIVE decks: march one shell from min-base to max-top.
    int nL = clamp(int(LAYER_COUNT), 1, 8);
    float minBase = 1e9, maxTop = -1e9;
    for (int i = 0; i < nL; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
    vec3 ro = vec3(0.0, PLANET_R + P.cam_world.y, 0.0);
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

        // AT-3: physical cloud lighting when the atmo cloud-light strength (sun_color.a) > 0. Sun color =
        // neutral star × atmospheric transmittance toward the sun (Rayleigh reddening); ambient endpoints =
        // the physical sky radiance at the zenith (top fill) + the horizon toward the sun (underside fill).
        // All three colors are pushed as params (read back from the LUTs on the CPU, sun-change cadence) —
        // they're per-frame constants, so no per-pixel cross-node LUT sampling. a == 0 → the mood path.
        float atmoStr = P.sun_color.a;
        vec3 sunCol = P.sun_color.rgb * P.sun_dir.w;        // mood sun (a == 0 path)
        vec3 skyTopC = P.sky_top.rgb, skyHorC = P.sky_horizon.rgb;   // mood ambient endpoints (a == 0 path)
        if (atmoStr > 0.0){
            sunCol = P.atmo_suntrans.rgb * P.sun_dir.w;     // neutral white × transmittance × energy
            skyTopC = P.atmo_zenith.rgb * atmoStr;          // physical zenith → cloud tops
            skyHorC = P.atmo_horizon.rgb * atmoStr;         // physical horizon-toward-sun → cloud undersides
        }
        // ambient endpoints (mood or physical) blended by view elevation, same structure as before.
        vec3 skyAmbient = mix(skyHorC, skyTopC, clamp(rd.y, 0.0, 1.0));
        float forward = pow(max(cosA, 0.0), 6.0);   // for view-gated powder (back-lit only)

        float T = 1.0;
        vec3 scattered = vec3(0.0);
        float t = tStart;
        for (int i = 0; i < maxSteps && t < tEnd; i++){
            vec3 p = ro + rd * t;
            vec2 worldXZ = CLOUD_ORIGIN + rd.xz * t;
            float opac; int act; float dens = density_all(p, worldXZ, windOff, opac, act);   // sum all decks; act = densest deck
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
                float od = light_optical_depth(p, worldXZ, L, windOff) * absorb;   // per-deck sun absorption
                // MULTIPLE-SCATTERING: a SHARP direct term (exp(-od)*phase) → dark self-shadowed
                // cores = 3D FORM, plus a softer isotropic fill (exp(-od*0.25)) for voluminous
                // interior light WITHOUT flattening. The old 3-octave loop summed ~1.75 weight
                // with weak extinction decay so cores stayed lit (flat). This separates the
                // crisp sun-modeling (direct) from the soft body fill (MS).
                float sun = exp(-od) * phase + 0.45 * exp(-od * 0.25);
                // BASE-OCCLUDED ambient: base dark (sky-occluded), tops bright → 3D form. AT-3: when physical,
                // also tint bases toward the horizon color and tops toward the zenith color (warm undersides
                // at sunset). a == 0 → skyHorC/skyTopC are the mood endpoints, so this equals the old skyAmbient.
                float h = height_frac(p);
                vec3 ambEnd = (atmoStr > 0.0) ? mix(skyHorC, skyTopC, h) : skyAmbient;
                vec3 amb = ambEnd * P.ambient * mix(0.25, 1.0, h);
                // powder (dark edges) only when looking TOWARD the sun (back-lit); gated.
                float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * P.powder), forward);
                // VIEW-ray extinction. Keep healthy cumulus/stratus masses solid enough to read as bodies
                // while edge wisps remain translucent.
                float sigma = dens * 0.065 * opac;
                float beer = exp(-sigma * dt);
                // NIGHT MOONLIGHT: a weak 2nd directional light (same scatter model as the sun) so night/dusk
                // clouds get silver-lit edges/undersides instead of going black. Gated: w==0 (day/no moon) skips
                // the extra light-march entirely. Phase-aware strength + cool moon tint pushed from the composer.
                vec3 moonLit = vec3(0.0);
                if (P.moon_dir.w > 0.001){
                    vec3 mL = normalize(P.moon_dir.xyz);
                    float odM = light_optical_depth(p, worldXZ, mL, windOff) * absorb;
                    float mPhase = mix(globalPhase, hg(dot(rd, mL), LF(act, 12)), pdeck);
                    float moonScatter = exp(-odM) * mPhase + 0.45 * exp(-odM * 0.25);
                    moonLit = P.moon_color.rgb * P.moon_dir.w * moonScatter;
                }
                vec3 lum = ((sunCol * sun + moonLit) * powder * albedo * tint + amb) * P.brightness;
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
