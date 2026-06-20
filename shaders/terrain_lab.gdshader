shader_type spatial;

// TERRAIN LOOK LAB — 7 material zones (valley, valley/slope transition, slope,
// slope/cliff transition, cliff, high, peak) composited by a SWAPPABLE mask mode
// and rendered with a SWAPPABLE blend/shader mode. Each zone has its own PBR set.
// All driven from the on-screen panel. Surface-only materials; flora is a future
// decoration pass.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

// 7 zone materials, each albedo + normal + roughness + ao.
uniform sampler2D z0_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z0_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z0_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
// Phase B: per-zone ambient-occlusion maps (bound from the material library's ao.png).
uniform sampler2D z0_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool ao_on = true;   // Phase B: apply baked material AO to the BRDF

uniform float tex_scale_m = 9.0;

// --- Ground anti-repetition (Unit 1) + shared distance seam -----------------
uniform bool ar_on = true;             // master toggle for stochastic bombing
uniform float ar_strength : hint_range(0.0, 1.0) = 1.0;  // blend toward bombed result
uniform float ar_far_m : hint_range(200.0, 6000.0) = 2500.0; // distance where AR fades to cheap
uniform vec3 cam_world = vec3(0.0);    // camera world pos (pushed from C#) for distanceWeight

// --- Distance detail layering (Unit 2) --------------------------------------
// Near detail = the SAME material textures resampled at a finer world->uv scale
// (tex_scale_m / detail_scale), still through ar_sample_wp (inherits Unit-1
// bombing), cross-faded into the macro sample by (1 - distanceWeight) shaped by
// detail_dist. A high-freq detail-normal is DERIVED from the detail albedo
// luminance gradient (no new asset/fetch). Off => pixel-identical to current.
// DEFAULT OFF: Unit 2 isn't user-approved yet, so startup keeps the approved
// (Unit-1-only) look; the Detail-tab toggle / --detail=1 turn it on to judge.
uniform bool detail_on = false;           // master toggle for distance detail
uniform float detail_strength : hint_range(0.0, 1.0) = 0.6;  // near detail blend amount
uniform float detail_scale : hint_range(2.0, 16.0) = 6.0;    // finer = tex_scale_m / this
uniform float detail_dist : hint_range(0.05, 1.0) = 0.1;     // fraction of ar_far_m over which detail fades out (0.1*2500 ≈ 250m near band; "near" detail should be genuinely near)
uniform float detail_nrm_amp : hint_range(0.0, 2.0) = 0.8;   // derived detail-normal strength

// --- Surface relief / parallax-occlusion (Phase B) ---------------------------
uniform bool pom_on = false;                                 // master toggle (default OFF until eye-gate)
uniform int pom_steps : hint_range(0, 64) = 24;              // max march steps (close range)
uniform float pom_scale_m : hint_range(0.0, 0.5) = 0.06;     // displacement depth, meters
uniform float pom_far_fade : hint_range(0.0, 1.0) = 0.6;     // distanceWeight at which POM hits 0

// mask_mode: 0 height bands | 1 height+slope | 2 noise-broken bands |
//            3 curvature-aware (dirt in hollows) | 4 steep=cliff sharp |
//            5 world-noise biome patches
uniform int mask_mode = 2;
// blend_mode: 0 flat albedo | 1 full PBR | 2 PBR + anti-tile |
//             3 PBR + anti-tile + macro-tint | 4 + distance-detail (full stack)
uniform int blend_mode = 3;

// Zone height/slope breakpoints (meters / 0..1 slope). 7 zones need 6 splits.
uniform float h_valley = 100.0;
uniform float h_slope = 350.0;
uniform float h_high = 700.0;
uniform float h_peak = 950.0;
uniform float slope_cliff_lo = 0.30;
uniform float slope_cliff_hi = 0.55;
uniform float band_softness_m = 120.0;
uniform float edge_noise_m = 80.0;
uniform float edge_noise_amp = 0.30;
uniform float macro_m = 480.0;
uniform float macro_amp = 0.14;
uniform float antitile_mult = 5.3;

// --- Anti-tiling (sharpness-preserving) -------------------------------------
// tile_mode chooses HOW each texture lookup breaks up its repetition without
// the contrast loss of the old 50/50 blend:
//   0 = none (single sample, sharpest; legacy blend_mode>=2 50/50 still applies)
//   1 = IQ 2-tap hash-tiling (cheap, contrast-boosted)         iquilezles.org
//   2 = hex-tiling (Mikkelsen: 3-tap, per-tile rotation,       JCGT 2022
//       variance-preserving + contrast restore, textureGrad mip-correct)
uniform int tile_mode = 1;   // IQ 2-tap default (hex caused tile-seam squares)
uniform float rough_floor : hint_range(0.04, 1.0) = 0.5;  // ground is matte; low floor = specular sparkle (fuzz)

// --- FUZZ-HUNT master switches: isolate each contributor. dbg_use_normalmap off =
//     flat geometric normal (kills normal-map specular shimmer). dbg_fullrough on =
//     force roughness 1.0 (kills ALL specular). All default to the normal look;
//     the Debug tab flips them so the user can bisect the speckle live. -----------
uniform bool dbg_use_normalmap = true;
uniform bool dbg_fullrough = false;   // true => ROUGHNESS forced 1.0 (matte, no spec)
uniform float tri_sharpness : hint_range(1.0, 32.0) = 8.0;  // triplanar blend exponent
uniform float hex_rot_strength : hint_range(0.0, 1.0) = 0.7; // per-tile rotation amount
uniform float hex_contrast : hint_range(0.3, 1.0) = 0.7;     // contrast-restore (lower=sharper)

// --- Lever 2: macro color variation -----------------------------------------
// Low-frequency, multi-octave color drift (the big "doesn't look tiled" trick,
// Far Cry 5 style). Drives brightness + gentle hue/saturation, neutral-centered
// so it both lightens and darkens. macro_amp above feeds the legacy value-only
// path; these drive the richer one.
uniform bool macro_on = true;
uniform float macro_val_amp : hint_range(0.0, 0.6) = 0.22;   // brightness drift
uniform float macro_hue_amp : hint_range(0.0, 0.2) = 0.05;   // hue drift (radians-ish)
uniform float macro_sat_amp : hint_range(0.0, 0.6) = 0.18;   // saturation drift
uniform float macro_scale2 : hint_range(60.0, 400.0) = 170.0; // 2nd (medium) octave wavelength

// --- Lever 4: contact & wear shading ----------------------------------------
// Uses already-computed curvature (v_curv) + slope to add depth: darken concave
// folds (cheap AO), roughen/expose steep faces, optional snow dusting up high.
uniform bool contact_on = true;
uniform float crevice_amp : hint_range(0.0, 1.0) = 0.45;     // darkening in concave folds
uniform float crevice_range : hint_range(0.5, 8.0) = 2.5;    // curvature span that counts as a fold
uniform float slope_wear_amp : hint_range(0.0, 1.0) = 0.35;  // extra roughness on steep faces
uniform float snow_dust_amp : hint_range(0.0, 1.0) = 0.0;    // dusting on upward faces near snow line
uniform float snow_dust_h : hint_range(0.0, 900.0) = 560.0;  // height where dusting begins

// --- Lever 1: GPU-baked splat mask (dominant+secondary material mixing) ------
// splat_tex (from SplatCompute): R=dominant zone, G=secondary zone, B=intra-zone
// mix amount, A=boundary blend. When splat_on, the fragment blends just those two
// materials (per-spot variety) instead of the soft 7-way accumulate.
// filter_nearest: the R/G channels are integer zone indices — linear filtering
// would interpolate them into wrong fractional indices at boundaries.
uniform sampler2D splat_tex : filter_nearest, repeat_disable, hint_default_black;
// Phase A weightmaps (filter_linear → smooth de-blocked weights). Roles packed:
// splat_wa = {0,1,2,3}, splat_wb = {4,5,6, spare}. Read by splat_blend_mode==1.
uniform sampler2D splat_wa : filter_linear, repeat_disable, hint_default_black;
uniform sampler2D splat_wb : filter_linear, repeat_disable, hint_default_black;
uniform bool splat_on = false;
uniform bool splat_rule_based = false;   // G1: read baked secondary (splat.g) vs fixed sec_zone[dom]
// Splat de-blocking: domain-warp the splat lookup by world-space noise so material
// boundaries flow organically instead of snapping to the axis-aligned splat grid
// (the stair-stepped blocky patches). 0 = off (raw grid). Live, no rebake.
uniform float splat_warp_m : hint_range(0.0, 120.0) = 24.0;    // warp distance (m)
uniform float splat_warp_wl : hint_range(10.0, 200.0) = 55.0;  // warp noise wavelength (m)
uniform float mix_strength : hint_range(0.0, 1.0) = 0.6;   // live: how strongly secondary shows
uniform int splat_debug = 0;   // 0 off | 1 show dominant zone as color | 2 show mix amount
// Phase A: smooth weight-blend path. 0 = legacy index path (current look) | 1 = weightmaps.
uniform int splat_blend_mode = 1;
// Derived material-height channel (the new "height" interface). true = 1-roughness
// (rough grit protrudes) | false = albedo luma. Real height maps: bind z*_hgt, sample there.
uniform bool height_from_rough = true;
// Height-interlock band: smaller = sharper relief-driven boundary; larger = softer crossfade.
uniform float interlock_sharp : hint_range(0.05, 0.9) = 0.35;
// How strongly the height channel drives the boundary (0 = pure weight crossfade).
uniform float interlock_height_amp : hint_range(0.0, 1.0) = 1.0;
// Phase A organic breakup: high-freq world-noise added to the interlock heights so the
// relief boundary is crinkled/irregular, not a smooth contour. 0 = off.
uniform float interlock_breakup : hint_range(0.0, 0.6) = 0.25;

// --- Height-blended transitions (AAA seamless multi-texture) -----------------
// Blend two materials by their RELIEF instead of a flat crossfade: the one whose
// surface is "higher" at this texel wins, so rock pokes through gravel, gravel
// settles into grass crevices. No dedicated displacement maps in this library, so
// roughness (inverted) is used as a height proxy (rough=protruding grit). The
// boundary becomes irregular + relief-driven, which removes the "line between
// textures" look. heightblend_sharp controls transition width.
uniform bool heightblend_on = true;
// NOTE: small sharp + high-freq roughness proxy => near-binary per-pixel switching
// = SPECKLE GRAIN. Default WIDE (soft) to avoid it; smoothstep below also softens.
uniform float heightblend_sharp : hint_range(0.1, 0.9) = 0.6;
// Per-zone companion material: sec_zone[d] = which zone's textures to blend INTO
// zone d (live, no rebake). Defaults set from C# to the adjacent zone.
uniform int sec_zone[7];

// --- Cloud shadows (Stage 5): a top-down sun-transmittance map baked by CloudVolume
// from the SAME density field as the sky clouds, so the ground shadow matches the
// cloud overhead. Sampled in light() to attenuate ONLY the direct sun (ambient/GI
// untouched → shadowed ground reads lit-but-sunless). Inert when cloud_shadow_on is
// false (light() reproduces the prior approved lighting exactly). ---
uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform bool cloud_shadow_on = false;     // turned on by CloudVolume once the map is live
uniform float cloud_shadow_region = 8192.0;
// DEBUG (proof tool): when on, the terrain renders the cloud-shadow factor directly as
// albedo (white = full sun, black = fully shadowed) so you can fly up, look at the clouds,
// look down, and verify the dark ground patches sit UNDER the clouds. Off in normal use.
uniform bool cloud_shadow_debug = false;

// Zone-band transition width (was the fixed band_softness_m=120). Wider = more
// gradual material boundaries. Live.
uniform float band_soft_mult : hint_range(0.3, 3.0) = 1.4;

varying vec2 v_uv;
varying vec3 v_world;
varying vec3 v_normal;
varying float v_h;
varying float v_slope;
varying float v_curv;
varying float v_ao;   // Phase B: material AO carried to light() for the DIRECT term

float height_at(vec2 uv) { return texture(heightmap, uv).r; }
float hash2(vec2 p){ return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453); }
float vnoise(vec2 w,float wl){ vec2 p=w/wl; vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(hash2(i),hash2(i+vec2(1,0)),f.x),mix(hash2(i+vec2(0,1)),hash2(i+vec2(1,1)),f.x),f.y); }

// ============================================================================
// TILING LAYER — break texture repetition while PRESERVING contrast.
// Each function wraps a single 2D lookup; the triplanar layer below calls the
// dispatcher `tiled()` once per projection plane. Tiling technique is isolated
// from triplanar blending and from zone compositing (one job each, swappable).
// ============================================================================

vec2 hash2v(vec2 p){
    mat2 m = mat2(vec2(127.1, 269.5), vec2(311.7, 183.3));
    return fract(sin(m * p) * 43758.5453);
}

// --- (1) IQ 2-tap hash-tiling. textureGrad with ORIGINAL-uv derivatives keeps
//         mips sharp; contrast-boosted blend hides the seam. iquilezles.org ---
vec4 iq_sample(sampler2D tex, vec2 uv, vec2 dx, vec2 dy){
    vec2 cell = floor(uv);
    vec2 off1 = sin(vec2(3.0,7.0)*hash2v(cell));        // per-cell virtual pattern A
    vec2 off2 = sin(vec2(5.0,11.0)*hash2v(cell+11.5));  // pattern B
    vec4 a = textureGrad(tex, uv + off1, dx, dy);
    vec4 b = textureGrad(tex, uv + off2, dx, dy);
    float f = 0.5 + 0.5*sin(6.2831*hash2v(cell).x);
    // boost contrast of the blend band by the inter-sample difference (IQ trick)
    vec4 d = a - b;
    return mix(a, b, smoothstep(0.2, 0.8, f - 0.1*(d.x+d.y+d.z)));
}

// --- (2) Mikkelsen hex-tiling: 3 nearest simplex vertices, per-tile rotation,
//         variance-preserving weighted blend + contrast restore. JCGT 2022. ---
void tri_grid(vec2 st, out float w1, out float w2, out float w3,
              out vec2 v1, out vec2 v2, out vec2 v3){
    const mat2 skew = mat2(vec2(1.0, 0.0), vec2(-0.57735027, 1.15470054));
    const mat2 unsk = mat2(vec2(1.0, 0.0), vec2(0.5, 0.86602540));
    vec2 p = skew * st;
    vec2 i = floor(p); vec2 f = fract(p);
    float tmp = f.x + f.y;
    if (tmp < 1.0){ v1 = i; w1 = 1.0 - tmp; v2 = i+vec2(1,0); w2 = f.x; v3 = i+vec2(0,1); w3 = f.y; }
    else          { v1 = i+vec2(1,1); w1 = tmp-1.0; v2 = i+vec2(1,0); w2 = 1.0-f.y; v3 = i+vec2(0,1); w3 = 1.0-f.x; }
    v1 = unsk*v1; v2 = unsk*v2; v3 = unsk*v3;
}
mat2 load_rot(vec2 idx){
    float a = hash2v(idx).x * 6.2831853 * hex_rot_strength;
    float c = cos(a), s = sin(a);
    return mat2(vec2(c,s), vec2(-s,c));
}
vec4 hex_sample(sampler2D tex, vec2 uv, vec2 dx, vec2 dy){
    float w1,w2,w3; vec2 v1,v2,v3;
    tri_grid(uv, w1,w2,w3, v1,v2,v3);
    mat2 r1 = load_rot(v1), r2 = load_rot(v2), r3 = load_rot(v3);
    // rotate each tile's lookup about its own center; jitter origin by a hash
    vec2 c1 = r1*(uv - v1) + v1 + hash2v(v1);
    vec2 c2 = r2*(uv - v2) + v2 + hash2v(v2);
    vec2 c3 = r3*(uv - v3) + v3 + hash2v(v3);
    vec4 t1 = textureGrad(tex, c1, r1*dx, r1*dy);
    vec4 t2 = textureGrad(tex, c2, r2*dx, r2*dy);
    vec4 t3 = textureGrad(tex, c3, r3*dx, r3*dy);
    // variance-preserving weights: normalize, blend, then divide by sqrt(Sum w^2)
    vec3 w = vec3(w1,w2,w3); w = w*w*w; w /= (w.x+w.y+w.z);     // sharpen weights
    vec4 m = t1*w.x + t2*w.y + t3*w.z;                          // weighted mean
    vec4 mean = (t1+t2+t3)/3.0;
    float vc = inversesqrt(dot(w,w));                           // 1/sqrt(Sum w^2)
    return mix(mean, mean + (m-mean)*vc, hex_contrast);         // restore contrast
}

// Dispatcher: pick the tiling technique. dx/dy are screen-space uv derivatives
// (computed once per plane) so every variant stays mip-correct.
vec4 tiled(sampler2D tex, vec2 uv){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    if (tile_mode == 1) return iq_sample(tex, uv, dx, dy);
    if (tile_mode == 2) return hex_sample(tex, uv, dx, dy);
    return textureGrad(tex, uv, dx, dy);   // tile_mode 0: plain sample
}

// 0 near .. 1 far — shared LOD factor for the ground-presentation units.
float distanceWeight(vec3 wp){
    float d = length(wp - cam_world);
    return clamp(d / ar_far_m, 0.0, 1.0);
}

// Near-detail blend factor: 1 at the camera, 0 by detail_dist*ar_far_m. Shares
// distanceWeight's normalization so the detail band scales with ar_far_m.
float detailWeight(vec3 wp){
    float far = distanceWeight(wp);                 // 0 near .. 1 far
    float k = 1.0 - smoothstep(0.0, max(detail_dist, 1e-3), far);
    return clamp(k, 0.0, 1.0);
}

vec3 tri_w(vec3 nr){ vec3 bw=pow(abs(nr),vec3(tri_sharpness)); return bw/(bw.x+bw.y+bw.z); }
// Branched-triplanar skip threshold: with tri_sharpness=8 a plane below this contributes
// <~0.4% (under 8-bit quantization), so skipping its texture taps is visually free. On flat
// ground the Y plane dominates → 1 sampled plane instead of 3 (perf pass 2026-06-18).
const float TRI_EPS = 0.004;

// Unit 1: histogram-preserving stochastic bombing. 3 hash-jittered + per-tile-rotated
// taps on a triangle grid, variance-preserving blend (keeps contrast — the old 50/50
// blend muddiness was variance loss). LODs to 1 plain tap at distance. No LUT needed.
// This is the SINGLE material-fetch entry the triplanar path goes through (the shared
// sampleMaterial seam for the ground-presentation arc).
vec4 ar_sample_wp(sampler2D tex, vec2 uv, vec3 wp){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    if (!ar_on) return textureGrad(tex, uv, dx, dy);
    float far = distanceWeight(wp);
    if (far > 0.95) return textureGrad(tex, uv, dx, dy);   // far: one cheap tap

    float w1,w2,w3; vec2 v1,v2,v3;
    tri_grid(uv, w1,w2,w3, v1,v2,v3);
    mat2 r1 = load_rot(v1), r2 = load_rot(v2), r3 = load_rot(v3);
    vec2 c1 = r1*(uv - v1) + v1 + hash2v(v1);
    vec2 c2 = r2*(uv - v2) + v2 + hash2v(v2);
    vec2 c3 = r3*(uv - v3) + v3 + hash2v(v3);
    vec4 t1 = textureGrad(tex, c1, r1*dx, r1*dy);
    vec4 t2 = textureGrad(tex, c2, r2*dx, r2*dy);
    vec4 t3 = textureGrad(tex, c3, r3*dx, r3*dy);
    vec3 w = vec3(w1,w2,w3); w *= w;                       // sharpen weights
    w /= max(w.x+w.y+w.z, 1e-5);
    vec4 mean = (t1+t2+t3)/3.0;
    vec4 blend = t1*w.x + t2*w.y + t3*w.z;
    float vc = inversesqrt(max(dot(w,w), 1e-5));           // variance restore (keep contrast)
    vec4 bombed = mean + (blend - mean)*vc;
    vec4 plain = textureGrad(tex, uv, dx, dy);
    float k = ar_strength * (1.0 - smoothstep(0.7, 0.95, far));
    return mix(plain, bombed, k);
}

vec3 s_alb(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec2 ux=wp.zy/tex_scale_m, uy=wp.xz/tex_scale_m, uz=wp.xy/tex_scale_m;
    vec3 base = tiled(t,ux).rgb*bw.x + tiled(t,uy).rgb*bw.y + tiled(t,uz).rgb*bw.z;
    // Legacy 50/50 anti-tile kept ONLY for A/B (tile_mode 0). It halves contrast
    // (the documented muddiness) — superseded by tile_mode 1/2.
    if (tile_mode == 0 && blend_mode >= 2) {
        vec3 big = texture(t,ux/antitile_mult).rgb*bw.x + texture(t,uy/antitile_mult).rgb*bw.y + texture(t,uz/antitile_mult).rgb*bw.z;
        base = mix(base, big, 0.5);
    }
    return base;
}
vec3 s_nrm(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    return tiled(t,wp.zy/tex_scale_m).rgb*bw.x + tiled(t,wp.xz/tex_scale_m).rgb*bw.y + tiled(t,wp.xy/tex_scale_m).rgb*bw.z;
}
float s_rgh(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    return tiled(t,wp.zy/tex_scale_m).r*bw.x + tiled(t,wp.xz/tex_scale_m).r*bw.y + tiled(t,wp.xy/tex_scale_m).r*bw.z;
}
// Phase B: AO fetch (single-channel, triplanar, through ar_sample_wp → shares anti-repetition + LOD).
float s_ao(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    return ar_sample_wp(t,wp.zy/tex_scale_m,wp).r*bw.x
         + ar_sample_wp(t,wp.xz/tex_scale_m,wp).r*bw.y
         + ar_sample_wp(t,wp.xy/tex_scale_m,wp).r*bw.z;
}
float ao_by(int z, vec3 wp, vec3 nr){
    if(z==0) return s_ao(z0_ao,wp,nr); if(z==1) return s_ao(z1_ao,wp,nr);
    if(z==2) return s_ao(z2_ao,wp,nr); if(z==3) return s_ao(z3_ao,wp,nr);
    if(z==4) return s_ao(z4_ao,wp,nr); if(z==5) return s_ao(z5_ao,wp,nr);
    return s_ao(z6_ao,wp,nr);
}

// --- Phase A/B SHARED material-height channel -------------------------------
// Derived per-plane (no triplanar) so POM can march it as a true 2D field; inverted
// roughness matches the existing heightblend intent. Real height maps: bind z*_hgt here.
float material_height_uv(sampler2D rgh_t, sampler2D alb_t, vec2 uv, vec2 dx, vec2 dy){
    if (height_from_rough){ return clamp(1.0 - textureGrad(rgh_t, uv, dx, dy).r, 0.0, 1.0); }
    return clamp(dot(textureGrad(alb_t, uv, dx, dy).rgb, vec3(0.299,0.587,0.114)), 0.0, 1.0);
}
float height_by(int z, vec2 uv, vec2 dx, vec2 dy){
    if(z==0) return material_height_uv(z0_rgh,z0_alb,uv,dx,dy); if(z==1) return material_height_uv(z1_rgh,z1_alb,uv,dx,dy);
    if(z==2) return material_height_uv(z2_rgh,z2_alb,uv,dx,dy); if(z==3) return material_height_uv(z3_rgh,z3_alb,uv,dx,dy);
    if(z==4) return material_height_uv(z4_rgh,z4_alb,uv,dx,dy); if(z==5) return material_height_uv(z5_rgh,z5_alb,uv,dx,dy);
    return material_height_uv(z6_rgh,z6_alb,uv,dx,dy);
}
// Dominant-plane triplanar height for interlock (cheap: one plane, the relief boundary
// only needs a representative height per material at this fragment).
float material_height(int z, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec2 uv = (bw.x>=bw.y && bw.x>=bw.z) ? wp.zy/tex_scale_m : (bw.y>=bw.z ? wp.xz/tex_scale_m : wp.xy/tex_scale_m);
    return height_by(z, uv, dFdx(uv), dFdy(uv));
}
// 7 smooth role weights at uv (linear-filtered weightmaps).
void splat_weights7(out float w[7], vec2 uv){
    vec4 a = texture(splat_wa, uv);
    vec4 b = texture(splat_wb, uv);
    w[0]=a.r; w[1]=a.g; w[2]=a.b; w[3]=a.a; w[4]=b.r; w[5]=b.g; w[6]=b.b;
}
// Relief-aware weight of B in [0,1]. Standard UE/Unity heightlerp WITHOUT the legacy
// 0.5 wash; the height genuinely drives the boundary, controlled by interlock_sharp.
float interlock_blend(float hA, float hB, float t, float sharp){
    float a = hA + (1.0 - t);
    float b = hB + t;
    float ma = max(a, b) - max(sharp, 1e-3);
    float wa2 = max(a - ma, 0.0);
    float wb2 = max(b - ma, 0.0);
    return clamp(wb2 / max(wa2 + wb2, 1e-5), 0.0, 1.0);
}

void vertex(){
    v_uv=(VERTEX.xz+vec2(region_size*0.5))/region_size;
    VERTEX.y=height_at(v_uv);
    v_world=VERTEX;
    float t=texel_world/region_size;
    float hl=height_at(v_uv-vec2(t,0)), hr=height_at(v_uv+vec2(t,0));
    float hd=height_at(v_uv-vec2(0,t)), hu=height_at(v_uv+vec2(0,t));
    v_normal=normalize(vec3(hl-hr,2.0*texel_world,hd-hu));
    v_h=VERTEX.y; v_slope=1.0-v_normal.y;
    v_curv=(hl+hr+hd+hu)*0.25 - VERTEX.y;
}

// Returns the 7 zone weights given height, slope, curvature + the chosen mask.
void zone_weights(out float w[7], float h, float slope, float curv, vec3 wp){
    for(int i=0;i<7;i++) w[i]=0.0;
    float en = (mask_mode==2 || mask_mode==3) ? (vnoise(wp.xz,edge_noise_m)-0.5)*2.0*edge_noise_amp : 0.0;
    float hj = h + en*250.0;
    float sj = slope + en*0.12;
    float soft = band_softness_m * band_soft_mult;   // wider = more gradual material boundaries

    if (mask_mode==5){
        // world-noise biome patches: large blobs pick a dominant zone, still
        // modulated by height so peaks stay snowy.
        float b = vnoise(wp.xz, macro_m*1.4);
        int pick = int(clamp(floor(b*5.0),0.0,4.0));   // 0..4 of the ground zones
        w[pick]=1.0;
        // cliffs/peaks override by slope/height regardless of biome.
        float cliff=smoothstep(slope_cliff_lo,slope_cliff_hi,sj);
        float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
        for(int i=0;i<7;i++) w[i]*=(1.0-max(cliff,peak));
        w[4]+=cliff; w[6]+=peak;
        return;
    }

    // Height-band weights (valley, lowtrans, slope, hightrans, high) with soft edges.
    float v   = 1.0 - smoothstep(h_valley-soft, h_valley+soft, hj);
    float hi  = smoothstep(h_high-soft, h_high+soft, hj);
    float mid = clamp(1.0 - v - hi, 0.0, 1.0);
    float lowtrans = mid * (1.0 - smoothstep(h_slope-soft, h_slope+soft, hj));
    float slopez   = mid - lowtrans;

    w[0]=v; w[1]=lowtrans; w[2]=slopez; w[3]=0.0; w[5]=hi;

    // Slope -> cliff (zones 3 transition, 4 cliff). Stronger when mask uses slope.
    float useSlope = (mask_mode==1||mask_mode==2||mask_mode==4) ? 1.0 : 0.6;
    float cliff = smoothstep(slope_cliff_lo, slope_cliff_hi, sj) * useSlope;
    float cliffTrans = smoothstep(slope_cliff_lo*0.6, slope_cliff_lo, sj) * (1.0-cliff) * useSlope;
    // Curvature: hollows (curv>0) bias toward the transition/dirt zone 1.
    if (mask_mode==3){ float hollow=smoothstep(0.5,2.5,curv); w[1]+=hollow*0.5; }
    // Apply cliff over the ground zones.
    float groundScale=1.0-max(cliff,cliffTrans);
    for(int i=0;i<6;i++) if(i!=3&&i!=4) w[i]*=groundScale;
    w[3]+=cliffTrans; w[4]+=cliff;

    // Peak/snow on high flat ground.
    float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
    for(int i=0;i<6;i++) w[i]*=(1.0-peak);
    w[6]=peak;

    // normalize
    float sum=0.0; for(int i=0;i<7;i++) sum+=w[i];
    if(sum>1e-4) for(int i=0;i<7;i++) w[i]/=sum;
}

// --- HSV helpers (for Lever 2 hue/sat drift) ---
vec3 rgb2hsv(vec3 c){
    vec4 K=vec4(0.0,-1.0/3.0,2.0/3.0,-1.0);
    vec4 p=mix(vec4(c.bg,K.wz),vec4(c.gb,K.xy),step(c.b,c.g));
    vec4 q=mix(vec4(p.xyw,c.r),vec4(c.r,p.yzx),step(p.x,c.r));
    float d=q.x-min(q.w,q.y); float e=1.0e-10;
    return vec3(abs(q.z+(q.w-q.y)/(6.0*d+e)), d/(q.x+e), q.x);
}
vec3 hsv2rgb(vec3 c){
    vec4 K=vec4(1.0,2.0/3.0,1.0/3.0,3.0);
    vec3 p=abs(fract(c.xxx+K.xyz)*6.0-K.www);
    return c.z*mix(K.xxx,clamp(p-K.xxx,0.0,1.0),c.y);
}
// Multi-octave low-frequency color drift. Neutral-centered (mean 0) so it both
// lightens and darkens; perturbs value, saturation, and hue independently.
vec3 macro_color(vec3 alb, vec3 wp){
    if(!macro_on) return alb;
    float n1 = vnoise(wp.xz, macro_m) - 0.5;            // big octave
    float n2 = vnoise(wp.xz + vec2(57.0,131.0), macro_scale2) - 0.5; // medium octave
    float drift = n1 + 0.5*n2;                          // combined, ~[-0.75,0.75]
    vec3 hsv = rgb2hsv(alb);
    hsv.x = fract(hsv.x + drift * macro_hue_amp);       // hue
    hsv.y = clamp(hsv.y * (1.0 + drift * macro_sat_amp), 0.0, 1.0); // saturation
    vec3 rgb = hsv2rgb(hsv);
    rgb *= (1.0 + drift * macro_val_amp);               // brightness
    return max(rgb, vec3(0.0));
}

// Height-blend weight: given two relief proxies and a linear blend t (0=A,1=B),
// return the relief-aware weight of B. Standard UE/Unity heightlerp. Larger
// `sharp` = wider/softer transition. h1,h2 in [0,1].
float heightblend_w(float h1, float h2, float t, float sharp){
    float a = h1 + (1.0 - t);
    float b = h2 + t;
    float ma = max(a, b) - sharp;
    float w1 = max(a - ma, 0.0);
    float w2 = max(b - ma, 0.0);
    float w = w2 / max(w1 + w2, 1e-5);
    // soften: blend the relief-aware result back toward the smooth linear t, so
    // high-freq roughness can't drive a hard per-pixel switch (the speckle grain).
    return mix(t, w, 0.5);
}

// Pick a zone's PBR by index (samplers can't be dynamically indexed in GLSL).
vec3 alb_by(int z, vec3 wp, vec3 nr){
    if(z==0) return s_alb(z0_alb,wp,nr); if(z==1) return s_alb(z1_alb,wp,nr);
    if(z==2) return s_alb(z2_alb,wp,nr); if(z==3) return s_alb(z3_alb,wp,nr);
    if(z==4) return s_alb(z4_alb,wp,nr); if(z==5) return s_alb(z5_alb,wp,nr);
    return s_alb(z6_alb,wp,nr);
}
vec3 nrm_by(int z, vec3 wp, vec3 nr){
    if(z==0) return s_nrm(z0_nrm,wp,nr); if(z==1) return s_nrm(z1_nrm,wp,nr);
    if(z==2) return s_nrm(z2_nrm,wp,nr); if(z==3) return s_nrm(z3_nrm,wp,nr);
    if(z==4) return s_nrm(z4_nrm,wp,nr); if(z==5) return s_nrm(z5_nrm,wp,nr);
    return s_nrm(z6_nrm,wp,nr);
}
float rgh_by(int z, vec3 wp, vec3 nr){
    if(z==0) return s_rgh(z0_rgh,wp,nr); if(z==1) return s_rgh(z1_rgh,wp,nr);
    if(z==2) return s_rgh(z2_rgh,wp,nr); if(z==3) return s_rgh(z3_rgh,wp,nr);
    if(z==4) return s_rgh(z4_rgh,wp,nr); if(z==5) return s_rgh(z5_rgh,wp,nr);
    return s_rgh(z6_rgh,wp,nr);
}

// Smooth PLAIN triplanar (3 planes, direct textureGrad — no tiling hash). Used by
// the splat path: smooth (no per-pixel plane-pick grain) and cheap (no hex/IQ).
vec3 tp_alb(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec3 c=vec3(0.0);
    // branched triplanar: only sample planes with meaningful weight (see TRI_EPS).
    if(bw.x>TRI_EPS) c+=ar_sample_wp(t,wp.zy/tex_scale_m,wp).rgb*bw.x;
    if(bw.y>TRI_EPS) c+=ar_sample_wp(t,wp.xz/tex_scale_m,wp).rgb*bw.y;
    if(bw.z>TRI_EPS) c+=ar_sample_wp(t,wp.xy/tex_scale_m,wp).rgb*bw.z;
    return c;
}
float tp_rgh(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    float r=0.0;
    if(bw.x>TRI_EPS) r+=ar_sample_wp(t,wp.zy/tex_scale_m,wp).r*bw.x;
    if(bw.y>TRI_EPS) r+=ar_sample_wp(t,wp.xz/tex_scale_m,wp).r*bw.y;
    if(bw.z>TRI_EPS) r+=ar_sample_wp(t,wp.xy/tex_scale_m,wp).r*bw.z;
    return r;
}
// PLAIN branched triplanar (no anti-repetition bombing) for NORMAL + ROUGHNESS. The
// histogram-preserving multi-tap exists to hide ALBEDO (color) tiling, which the eye
// catches; normal/roughness tiling under a matte ground BRDF at terrain distances is
// imperceptible, AND averaging tangent-space normals actually flattens detail. So these
// take ONE tap per (branched) plane instead of ar_sample_wp's 4 (perf pass 2026-06-18).
vec4 tap_plain(sampler2D t, vec2 uv){ return textureGrad(t, uv, dFdx(uv), dFdy(uv)); }
vec3 tp_nrm(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec3 c=vec3(0.0);
    if(bw.x>TRI_EPS) c+=tap_plain(t,wp.zy/tex_scale_m).rgb*bw.x;
    if(bw.y>TRI_EPS) c+=tap_plain(t,wp.xz/tex_scale_m).rgb*bw.y;
    if(bw.z>TRI_EPS) c+=tap_plain(t,wp.xy/tex_scale_m).rgb*bw.z;
    return c;
}
float tp_rgh_plain(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    float r=0.0;
    if(bw.x>TRI_EPS) r+=tap_plain(t,wp.zy/tex_scale_m).r*bw.x;
    if(bw.y>TRI_EPS) r+=tap_plain(t,wp.xz/tex_scale_m).r*bw.y;
    if(bw.z>TRI_EPS) r+=tap_plain(t,wp.xy/tex_scale_m).r*bw.z;
    return r;
}
// --- Unit 2: distance detail ------------------------------------------------
// Macro (existing branched ar_sample_wp scale) (+) near detail (finer scale),
// both through ar_sample_wp so each inherits Unit-1 bombing. Branched on TRI_EPS
// exactly like tp_alb so detail-off == current (perf pass preserved). The detail
// is added as the detail tap's deviation from ITS OWN luma (not a constant 0.5),
// so it layers high-freq contrast WITHOUT shifting macro brightness for dark/light
// materials (audit M1). Crossfaded by detailWeight. detail_on=false ⇒ exactly tp_alb.
vec3 detail_alb(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec3 macro=vec3(0.0);
    if(bw.x>TRI_EPS) macro+=ar_sample_wp(t,wp.zy/tex_scale_m,wp).rgb*bw.x;
    if(bw.y>TRI_EPS) macro+=ar_sample_wp(t,wp.xz/tex_scale_m,wp).rgb*bw.y;
    if(bw.z>TRI_EPS) macro+=ar_sample_wp(t,wp.xy/tex_scale_m,wp).rgb*bw.z;
    if(!detail_on) return macro;
    float dw=detailWeight(wp);
    if(dw<0.001) return macro;                      // far: skip the extra taps
    float ds=tex_scale_m/max(detail_scale,1e-3);    // finer world->uv scale
    vec3 det=vec3(0.0);
    if(bw.x>TRI_EPS) det+=ar_sample_wp(t,wp.zy/ds,wp).rgb*bw.x;
    if(bw.y>TRI_EPS) det+=ar_sample_wp(t,wp.xz/ds,wp).rgb*bw.y;
    if(bw.z>TRI_EPS) det+=ar_sample_wp(t,wp.xy/ds,wp).rgb*bw.z;
    float detLuma=dot(det,vec3(0.299,0.587,0.114));
    vec3 over=macro+(det-vec3(detLuma))*(detail_strength*dw);
    return clamp(over,0.0,1.0);
}

// Macro normal-map (-1..1) + a derived high-freq bump near the camera. The MACRO
// normal uses the SAME plain branched taps as tp_nrm (perf pass: normal is NOT
// bombed — averaging tangent normals flattens detail; matte ground at distance
// hides normal tiling), so detail-off == current. The bump's slope = luminance
// gradient of the finer ALBEDO tap, taken from EXPLICIT offset taps (textureGrad),
// NOT dFdx of an ar_sample_wp result — the latter crawls at the bombing's tile
// seams (audit H1). Un-normalized: fragment() only consumes .xy.
vec3 detail_nrm(sampler2D albT, sampler2D nrmT, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec3 mraw=vec3(0.0);                            // macro normal raw 0..1 (== tp_nrm)
    if(bw.x>TRI_EPS) mraw+=tap_plain(nrmT,wp.zy/tex_scale_m).rgb*bw.x;
    if(bw.y>TRI_EPS) mraw+=tap_plain(nrmT,wp.xz/tex_scale_m).rgb*bw.y;
    if(bw.z>TRI_EPS) mraw+=tap_plain(nrmT,wp.xy/tex_scale_m).rgb*bw.z;
    vec3 macroN=mraw*2.0-1.0;
    if(!detail_on) return macroN;
    float dw=detailWeight(wp);
    if(dw<0.001) return macroN;
    float ds=tex_scale_m/max(detail_scale,1e-3);
    // dominant triplanar plane drives the bump (cheap, avoids 3x derivative work)
    vec2 duv = (bw.x>=bw.y && bw.x>=bw.z) ? wp.zy/ds
             : (bw.y>=bw.z)               ? wp.xz/ds
                                          : wp.xy/ds;
    vec2 dx=dFdx(duv), dy=dFdy(duv);
    float e=1.0/512.0;                              // small uv offset for the difference
    float lC=dot(textureGrad(albT,duv,               dx,dy).rgb, vec3(0.299,0.587,0.114));
    float lX=dot(textureGrad(albT,duv+vec2(e,0.0),   dx,dy).rgb, vec3(0.299,0.587,0.114));
    float lY=dot(textureGrad(albT,duv+vec2(0.0,e),   dx,dy).rgb, vec3(0.299,0.587,0.114));
    vec2 g=vec2(lX-lC,lY-lC)/e;                     // surface luminance slope
    return vec3(macroN.xy - g*detail_nrm_amp*dw, macroN.z);
}

vec3 trip_alb_by(int z, vec3 wp, vec3 nr){
    if(z==0) return detail_alb(z0_alb,wp,nr); if(z==1) return detail_alb(z1_alb,wp,nr);
    if(z==2) return detail_alb(z2_alb,wp,nr); if(z==3) return detail_alb(z3_alb,wp,nr);
    if(z==4) return detail_alb(z4_alb,wp,nr); if(z==5) return detail_alb(z5_alb,wp,nr);
    return detail_alb(z6_alb,wp,nr);
}
vec3 trip_nrm_by(int z, vec3 wp, vec3 nr){
    // detail_nrm returns -1..1; re-encode to 0..1 so fragment()'s *2-1 is correct.
    // detail_on=false ⇒ detail_nrm == tp_nrm*2-1, so *0.5+0.5 round-trips to tp_nrm.
    if(z==0) return detail_nrm(z0_alb,z0_nrm,wp,nr)*0.5+0.5; if(z==1) return detail_nrm(z1_alb,z1_nrm,wp,nr)*0.5+0.5;
    if(z==2) return detail_nrm(z2_alb,z2_nrm,wp,nr)*0.5+0.5; if(z==3) return detail_nrm(z3_alb,z3_nrm,wp,nr)*0.5+0.5;
    if(z==4) return detail_nrm(z4_alb,z4_nrm,wp,nr)*0.5+0.5; if(z==5) return detail_nrm(z5_alb,z5_nrm,wp,nr)*0.5+0.5;
    return detail_nrm(z6_alb,z6_nrm,wp,nr)*0.5+0.5;
}
float trip_rgh_by(int z, vec3 wp, vec3 nr){
    if(z==0) return tp_rgh_plain(z0_rgh,wp,nr); if(z==1) return tp_rgh_plain(z1_rgh,wp,nr);
    if(z==2) return tp_rgh_plain(z2_rgh,wp,nr); if(z==3) return tp_rgh_plain(z3_rgh,wp,nr);
    if(z==4) return tp_rgh_plain(z4_rgh,wp,nr); if(z==5) return tp_rgh_plain(z5_rgh,wp,nr);
    return tp_rgh_plain(z6_rgh,wp,nr);
}

// Debug palette for visualizing the splat mask's zone assignment.
vec3 zone_color(int z){
    vec3 c[7]=vec3[7](vec3(0.2,0.7,0.2),vec3(0.5,0.6,0.2),vec3(0.6,0.5,0.3),
                      vec3(0.6,0.4,0.3),vec3(0.5,0.5,0.55),vec3(0.7,0.7,0.7),vec3(0.95,0.97,1.0));
    return c[clamp(z,0,6)];
}

// Phase B: parallax-occlusion. Marches the DOMINANT triplanar plane only, using the
// Phase-A material height channel. Returns the in-plane uv offset (caller maps to world).
// dom out: 0=zy(normal x), 1=xz(normal y), 2=xy(normal z).
vec2 pom_offset(sampler2D rgh_t, sampler2D alb_t, vec3 wp, vec3 nr, vec3 vdir, out int dom){
    vec3 bw = tri_w(nr);
    dom = (bw.x >= bw.y && bw.x >= bw.z) ? 0 : (bw.y >= bw.z ? 1 : 2);
    vec2 uv; vec2 vp; float vdepth;
    if (dom == 0){ uv = wp.zy/tex_scale_m; vp = vdir.zy; vdepth = vdir.x; }
    else if (dom == 1){ uv = wp.xz/tex_scale_m; vp = vdir.xz; vdepth = vdir.y; }
    else { uv = wp.xy/tex_scale_m; vp = vdir.xy; vdepth = vdir.z; }

    float far = distanceWeight(wp);
    float lod = 1.0 - smoothstep(0.0, max(pom_far_fade, 1e-3), far);
    int steps = int(float(pom_steps) * lod);
    if (!pom_on || steps < 1 || pom_scale_m <= 0.0) return vec2(0.0);

    vec2 dx = dFdx(uv), dy = dFdy(uv);
    float vz = max(abs(vdepth), 0.05);                       // grazing → longer offset
    vec2 maxShift = (vp / vz) * (pom_scale_m / tex_scale_m);
    float dStep = 1.0 / float(steps);
    vec2 dUV = maxShift * dStep;
    vec2 curUV = uv; float rayH = 1.0;
    float h = material_height_uv(rgh_t, alb_t, curUV, dx, dy);
    for (int i = 0; i < 64; i++){
        if (i >= steps) break;
        if (rayH <= h) break;
        rayH -= dStep; curUV -= dUV;
        h = material_height_uv(rgh_t, alb_t, curUV, dx, dy);
    }
    vec2 prevUV = curUV + dUV;
    float hPrev = material_height_uv(rgh_t, alb_t, prevUV, dx, dy);
    float after  = h - rayH;
    float before = hPrev - (rayH + dStep);
    float wgt = after / max(after - before, 1e-4);
    vec2 off = mix(curUV, prevUV, wgt) - uv;
    float seam = smoothstep(0.0, 0.15, lod);                // thin fade only in last 15% before cutoff
    return off * seam;
}

void fragment(){
    vec3 wp=v_world; vec3 nr=v_normal;

    // Phase B: parallax-occlusion. Pick the dominant zone's height field, march it, and
    // shift wp along the dominant plane so every later material fetch parallaxes.
    if (pom_on){
        vec3 vdir = normalize(wp - cam_world);
        int hz = 0;
        if (splat_on){
            if (splat_blend_mode == 1){
                float pw[7]; splat_weights7(pw, v_uv);
                float mx=-1.0; for(int i=0;i<7;i++){ if(pw[i]>mx){ mx=pw[i]; hz=i; } }
            } else { hz = clamp(int(texture(splat_tex, v_uv).r + 0.5), 0, 6); }
        } else {
            if      (v_slope > slope_cliff_hi) hz = 4;
            else if (v_h > h_peak)             hz = 6;
            else if (v_h > h_high)             hz = 5;
            else if (v_slope > slope_cliff_lo) hz = 3;
            else if (v_h > h_slope)            hz = 2;
            else if (v_h > h_valley)           hz = 1;
            else                               hz = 0;
        }
        int domPlane; vec2 off = vec2(0.0);
        if      (hz==0) off = pom_offset(z0_rgh,z0_alb,wp,nr,vdir,domPlane);
        else if (hz==1) off = pom_offset(z1_rgh,z1_alb,wp,nr,vdir,domPlane);
        else if (hz==2) off = pom_offset(z2_rgh,z2_alb,wp,nr,vdir,domPlane);
        else if (hz==3) off = pom_offset(z3_rgh,z3_alb,wp,nr,vdir,domPlane);
        else if (hz==4) off = pom_offset(z4_rgh,z4_alb,wp,nr,vdir,domPlane);
        else if (hz==5) off = pom_offset(z5_rgh,z5_alb,wp,nr,vdir,domPlane);
        else            off = pom_offset(z6_rgh,z6_alb,wp,nr,vdir,domPlane);
        vec2 woff = off * tex_scale_m;
        if      (domPlane==0){ wp.z += woff.x; wp.y += woff.y; }
        else if (domPlane==1){ wp.x += woff.x; wp.z += woff.y; }
        else                 { wp.x += woff.x; wp.y += woff.y; }
    }

    float w[7]; zone_weights(w, v_h, v_slope, v_curv, wp);

    vec3 alb=vec3(0.0); vec3 nrm=vec3(0.0,0.0,1.0); float rgh=0.0; float ao=1.0;

    int dbg = 0;            // set when a splat debug view should override final output
    vec3 dbg_col = vec3(0.0);
    if (splat_on){
        // Lever 1 + baked-ground: the splat texture (baked by SplatCompute) supplies
        // the LOW-FREQ decisions — dominant zone, per-zone companion, blend amount —
        // and the fragment blends just those TWO materials via smooth plain triplanar
        // (trip_*). ~18 texture fetches/px (3 planes × 2 materials × 3 maps), 3× under
        // the original ~54 (which had hex × triplanar). NO hex: hex's rotated tiles
        // made tile-seam squares. Detail stays crisp (real mipmapped texture taps).
        if (splat_blend_mode == 0){
            // ---- LEGACY index path (current approved look). UNCHANGED. ----
            // De-block the splat: domain-warp the lookup with 2 octaves of world-space noise
            // so boundaries are organic/high-freq, not axis-aligned stair-stepped blocks.
            vec2 swarp = (vec2(vnoise(wp.xz + vec2(19.0,57.0), splat_warp_wl),
                               vnoise(wp.xz + vec2(83.0,11.0), splat_warp_wl)) - 0.5)
                       + 0.5 * (vec2(vnoise(wp.xz + vec2(5.0,99.0), splat_warp_wl*0.4),
                                     vnoise(wp.xz + vec2(61.0,7.0), splat_warp_wl*0.4)) - 0.5);
            vec4 sp = texture(splat_tex, v_uv + swarp * (splat_warp_m / region_size));
            int dom = int(sp.r + 0.5);
            // G1: the rule engine bakes a spatially-meaningful runner-up role into splat.g;
            // use it. Legacy path keeps the fixed per-dom companion lookup.
            int sec = splat_rule_based ? clamp(int(sp.g + 0.5), 0, 6) : clamp(sec_zone[dom], 0, 6);
            float t = clamp(max(sp.b, sp.a) * mix_strength, 0.0, 1.0);

            float rD = trip_rgh_by(dom, wp, nr);
            float rS = trip_rgh_by(sec, wp, nr);
            float m = heightblend_on ? heightblend_w(rD, rS, t, heightblend_sharp) : t;

            if (splat_debug == 1){ dbg = 1; dbg_col = mix(zone_color(dom),zone_color(sec),m); }
            else if (splat_debug == 2){ dbg = 1; dbg_col = vec3(m); }

            alb = mix(trip_alb_by(dom,wp,nr), trip_alb_by(sec,wp,nr), m);
            if (blend_mode>=1){
                nrm = mix(trip_nrm_by(dom,wp,nr), trip_nrm_by(sec,wp,nr), m)*2.0-1.0;
                rgh = mix(rD, rS, m);
                ao = mix(ao_by(dom,wp,nr), ao_by(sec,wp,nr), m);
            } else { rgh = 0.9; }
        } else {
            // ---- Phase A: smooth weightmap top-2 blend (de-blocked) ----
            // Organic breakup: domain-warp the weightmap lookup (folds in splat_warp) so
            // boundaries flow with the world, not the weightmap grid.
            vec2 swarp = (vec2(vnoise(wp.xz + vec2(19.0,57.0), splat_warp_wl),
                               vnoise(wp.xz + vec2(83.0,11.0), splat_warp_wl)) - 0.5)
                       + 0.5 * (vec2(vnoise(wp.xz + vec2(5.0,99.0), splat_warp_wl*0.4),
                                     vnoise(wp.xz + vec2(61.0,7.0), splat_warp_wl*0.4)) - 0.5);
            float sw[7]; splat_weights7(sw, v_uv + swarp * (splat_warp_m / region_size));
            int d0=0; float m0=-1.0; for(int i=0;i<7;i++){ if(sw[i]>m0){ m0=sw[i]; d0=i; } }
            int d1=d0; float m1=-1.0; for(int i=0;i<7;i++){ if(i!=d0 && sw[i]>m1){ m1=sw[i]; d1=i; } }
            float t = clamp(m1 / max(m0+m1, 1e-5), 0.0, 1.0);   // 0 deep in d0 .. ~0.5 at boundary

            // crinkle the interlock heights with opposite-signed high-freq noise so the
            // boundary weaves between the two materials (organic, not a smooth contour).
            float bk = (vnoise(wp.xz + vec2(127.0,311.0), tex_scale_m*1.7) - 0.5) * interlock_breakup;
            float hD = clamp(material_height(d0, wp, nr) * interlock_height_amp + bk, 0.0, 1.0);
            float hS = clamp(material_height(d1, wp, nr) * interlock_height_amp - bk, 0.0, 1.0);
            float m = heightblend_on ? interlock_blend(hD, hS, t, interlock_sharp) : t;

            if (splat_debug == 1){ dbg = 1; dbg_col = mix(zone_color(d0),zone_color(d1),m); }
            else if (splat_debug == 2){ dbg = 1; dbg_col = vec3(m); }

            alb = mix(trip_alb_by(d0,wp,nr), trip_alb_by(d1,wp,nr), m);
            if (blend_mode>=1){
                nrm = mix(trip_nrm_by(d0,wp,nr), trip_nrm_by(d1,wp,nr), m)*2.0-1.0;
                rgh = mix(trip_rgh_by(d0,wp,nr), trip_rgh_by(d1,wp,nr), m);
                ao = mix(ao_by(d0,wp,nr), ao_by(d1,wp,nr), m);
            } else { rgh = 0.9; }
        }
    } else {
        // accumulate weighted PBR across all 7 zones (soft masks)
        ao = 0.0;   // accumulate AO from 0 (weights sum to 1 → weighted average)
        #define ZONE(i,A,N,R,O) alb+=w[i]*s_alb(A,wp,nr); if(blend_mode>=1){ nrm+=w[i]*(s_nrm(N,wp,nr)*2.0-1.0); rgh+=w[i]*s_rgh(R,wp,nr); ao+=w[i]*s_ao(O,wp,nr);} else { rgh+=w[i]*0.9; ao+=w[i]; }
        ZONE(0,z0_alb,z0_nrm,z0_rgh,z0_ao)
        ZONE(1,z1_alb,z1_nrm,z1_rgh,z1_ao)
        ZONE(2,z2_alb,z2_nrm,z2_rgh,z2_ao)
        ZONE(3,z3_alb,z3_nrm,z3_rgh,z3_ao)
        ZONE(4,z4_alb,z4_nrm,z4_rgh,z4_ao)
        ZONE(5,z5_alb,z5_nrm,z5_rgh,z5_ao)
        ZONE(6,z6_alb,z6_nrm,z6_rgh,z6_ao)
    }

    // Lever 2: macro color variation (multi-octave LF value/hue/sat drift).
    // Own toggle (macro_on), independent of blend_mode so it isolates cleanly.
    alb = macro_color(alb, wp);

    // Lever 4: contact & wear shading. v_curv>0 = concave (fold/crease).
    if (contact_on){
        float fold = smoothstep(0.3, crevice_range, v_curv);   // how deep a fold
        alb *= (1.0 - fold * crevice_amp);                     // darken crevices
        rgh = mix(rgh, 1.0, smoothstep(slope_cliff_lo, slope_cliff_hi, v_slope) * slope_wear_amp); // steep=rougher
        // snow dusting on near-flat upward faces above a height.
        float dust = snow_dust_amp * smoothstep(snow_dust_h, snow_dust_h+120.0, v_h)
                                   * (1.0 - smoothstep(0.18, 0.42, v_slope));
        alb = mix(alb, vec3(0.92,0.94,0.97), dust);
        rgh = mix(rgh, 0.6, dust);
    }

    // Cloud shadows are applied in light() below (they must attenuate the SUN/direct
    // light, not albedo — albedo-darkening gets washed out by ambient+SDFGI, which is
    // why max strength was barely visible). Ambient still fills, so shadows read
    // natural rather than black.

    // splat debug view overrides the lit output (no early return — gdshader bans it).
    ALBEDO = (dbg==1) ? dbg_col : alb;
    // PROOF tool: paint the cloud-shadow factor as albedo (white=sun, black=shadow) so the
    // ground darkening can be compared 1:1 against the clouds overhead. Emissive too so it's
    // not re-shaded by light(). Overrides everything when on.
    if (cloud_shadow_debug){
        // inline the shadow-map sample (cloud_shadow_factor is declared after fragment()).
        float sv = cloud_shadow_on ? texture(cloud_shadow_tex, v_world.xz / cloud_shadow_region + 0.5).r : 1.0;
        ALBEDO = vec3(sv);
    }
    ROUGHNESS = dbg_fullrough ? 1.0 : clamp(rgh, rough_floor, 1.0);
    // Phase B: material AO. AO/AO_LIGHT_AFFECT only reach engine ambient/GI under a custom
    // light(); the DIRECT term is darkened in light() via v_ao below (M4).
    ao = ao_on ? clamp(ao, 0.0, 1.0) : 1.0;
    AO = ao;
    AO_LIGHT_AFFECT = 1.0;
    v_ao = ao;
    // Normal: detailed normal-map (specular-shimmer suspect) only when enabled AND
    // blend_mode supports it. Otherwise the flat geometric normal.
    if (blend_mode>=1 && dbg_use_normalmap){
        // apply the tangent-space normal across BOTH horizontal axes (was y-only) so
        // POM-revealed relief gets matching shading; stronger near, calm far (shares POM LOD intent).
        float nrNear = 1.0 - distanceWeight(wp);
        vec3 wn = normalize(nr + vec3(nrm.x, 0.0, nrm.y) * mix(0.4, 1.0, nrNear));
        NORMAL = normalize((VIEW_MATRIX*vec4(wn,0.0)).xyz);
    } else {
        NORMAL = normalize((VIEW_MATRIX*vec4(nr,0.0)).xyz);
    }
}
// Cloud-shadow sun factor in [0,1] (1 = full sun). Samples the top-down transmittance
// map (baked by CloudVolume from the same density field as the visible clouds) at this
// fragment's world XZ. Returns 1.0 when off → light() reduces to default lighting.
float cloud_shadow_factor(vec3 wp){
    if (!cloud_shadow_on) { return 1.0; }
    vec2 uv = wp.xz / cloud_shadow_region + 0.5;   // [-R/2,R/2] → [0,1] (matches the bake)
    return texture(cloud_shadow_tex, uv).r;
}

// Custom light(): a FAITHFUL replication of Godot 4's default spatial BRDF — Burley
// (Disney) diffuse + GGX specular (correlated Smith visibility + Schlick Fresnel) —
// so that with cloud_shadow_on=false this matches the engine's built-in lighting (the
// approved "really good" look) as closely as a custom light() can. The ONLY addition
// is the cloud shadow factor multiplying the DIRECT sun term (ambient/GI untouched).
// NOTE: a custom light() fully REPLACES the built-in BRDF (Godot has no fall-through),
// so this must mirror it rather than be a cheaper approximation. Audit H1, per pillars.
// Exact (1-x)^5 as 5 multiplies — the Schlick/Burley exponent. Removes 3 transcendental
// pow() per lit pixel with bit-identical result (perf pass 2026-06-18).
float pow5(float x){ float x2 = x * x; return x2 * x2 * x; }

void light() {
    float cloud = cloud_shadow_factor(v_world);

    vec3 N = NORMAL;
    vec3 V = VIEW;
    vec3 L = LIGHT;
    vec3 H = normalize(V + L);
    float NoL = clamp(dot(N, L), 0.0, 1.0);
    float NoV = clamp(dot(N, V), 1e-4, 1.0);
    float NoH = clamp(dot(N, H), 0.0, 1.0);
    float LoH = clamp(dot(L, H), 0.0, 1.0);

    float rough = clamp(ROUGHNESS, 0.02, 1.0);
    float a = rough * rough;
    float a2 = a * a;

    // light energy reaching the surface (Godot folds energy/atten into LIGHT_COLOR/
    // ATTENUATION); cloud scales the direct sun only.
    vec3 radiance = LIGHT_COLOR * ATTENUATION * cloud * NoL;

    // --- Burley (Disney) diffuse, matching Godot's DIFFUSE_BURLEY default ---
    float fd90 = 0.5 + 2.0 * rough * LoH * LoH;
    float lightScatter = 1.0 + (fd90 - 1.0) * pow5(1.0 - NoL);
    float viewScatter  = 1.0 + (fd90 - 1.0) * pow5(1.0 - NoV);
    float burley = lightScatter * viewScatter * (1.0 / 3.14159265);
    DIFFUSE_LIGHT += ALBEDO * radiance * burley * v_ao;

    // --- GGX specular: D (Trowbridge-Reitz) * V (correlated Smith) * F (Schlick) ---
    float d = NoH * NoH * (a2 - 1.0) + 1.0;
    float D = a2 / (3.14159265 * d * d);
    float k = a * 0.5;   // Schlick-GGX visibility (Godot's SchlickGGX direct-light form)
    float visL = NoL * (NoV * (1.0 - k) + k);
    float visV = NoV * (NoL * (1.0 - k) + k);
    float Vis = 0.5 / max(visL + visV, 1e-4);
    // dielectric F0 ~0.04 (terrain is non-metal); Schlick Fresnel
    vec3 f0 = vec3(0.04);
    vec3 F = f0 + (vec3(1.0) - f0) * pow5(1.0 - LoH);
    SPECULAR_LIGHT += radiance * (D * Vis) * F * v_ao;
}
