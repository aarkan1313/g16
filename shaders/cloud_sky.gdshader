shader_type sky;

// Cloud sky composite (Stage 4, Path A). The expensive raymarch now runs in
// cloud_raymarch.glsl (compute, amortized over frames) into a lat-long hemisphere
// texture. This shader just SAMPLES that texture by view direction and composites
// over the background sky gradient. Cheap — a couple of texture reads per pixel.

// Cloud texture filled by CloudCompositorEffect on the render thread (rgb=radiance,
// a=alpha, lat-long). A per-material uniform (not a global) set from CloudVolume to
// the effect's Texture2Drd — avoids global-param compile-order races.
uniform sampler2D cloud_rd_tex : filter_linear, repeat_enable;
uniform bool cloud_enabled = true;
uniform int cloud_debug = 0;   // 0 normal | 1 show raw cloud tex

// Sun-disc brightness, decoupled from LIGHT0_ENERGY: the overcast proxy dims the directional
// light's energy globally (terrain gloom), but the VISIBLE sun disc must stay bright — a cloud
// in front occludes it via the dome composite, total coverage elsewhere must NOT dim it.
uniform float sun_disc_energy = 1.3;
uniform float sun_size = 0.6;    // angular disc radius in DEGREES (visible sun size)
uniform float sun_limb = 0.55;   // limb darkening: 0 = flat disc, 1 = strong center→rim falloff
uniform float sun_corona_size : hint_range(40.0, 6000.0) = 1200.0;  // tight inner bloom (higher = tighter)
uniform float sun_corona_energy = 2.0;
uniform float sun_halo_size : hint_range(8.0, 400.0) = 90.0;        // wide soft glow (lower = wider)
uniform float sun_halo_energy = 0.4;

uniform float sun_cloud_redden = 0.8;   // how much thin cloud reddens (vs just dims) the transmitted sun
uniform float sun_redden = 1.0;            // strength of warm→red shift at low sun
uniform float sun_redden_onset = 0.25;     // sun elevation (0..1, =LIGHT0_DIRECTION.y) where reddening begins
uniform float sun_horizon_grow = 0.6;      // extra disc/halo size factor at the horizon (0 = none)
uniform vec3  sun_redden_color : source_color = vec3(1.0, 0.42, 0.18);  // target low-sun color

// --- sun SURFACE (procedural granulation; gated, default off = flat disc) ---
uniform bool  sun_surface_on = false;
uniform float sun_surface_cells : hint_range(1.0, 40.0) = 9.0;     // granule frequency
uniform float sun_surface_contrast : hint_range(0.0, 1.0) = 0.0;   // 0 = flat (today), high = bold
uniform float sun_surface_spots : hint_range(0.0, 1.0) = 0.0;      // sunspot darkening
uniform float sun_surface_churn : hint_range(0.0, 1.0) = 0.15;     // animation speed (0 = static)
uniform float sun_surface_warm  : hint_range(0.0, 1.0) = 0.3;      // cell-vs-spot warm shift
uniform vec3  sun_surface_color : source_color = vec3(0.0);        // fantasy base override (black = off)

uniform vec3 sky_top : source_color = vec3(0.30, 0.48, 0.74);
uniform vec3 sky_horizon : source_color = vec3(0.68, 0.74, 0.80);
uniform vec3 ground_color : source_color = vec3(0.22, 0.26, 0.22);

// PI is a built-in in Godot sky shaders — don't redefine it.

vec3 background(vec3 dir){
    if (dir.y >= 0.0){
        float t = pow(clamp(dir.y, 0.0, 1.0), 0.5);
        return mix(sky_horizon, sky_top, t);
    }
    return mix(sky_horizon, ground_color, clamp(-dir.y * 4.0, 0.0, 1.0));
}

// view dir → lat-long UV (matches dir_from_texel in cloud_raymarch.glsl)
vec2 dir_to_uv(vec3 d){
    float az = atan(d.z, d.x);             // -π..π
    if (az < 0.0) az += 2.0 * PI;          // 0..2π
    float el = asin(clamp(d.y, 0.0, 1.0)); // 0..π/2
    return vec2(az / (2.0 * PI), el / (0.5 * PI));
}

float sun_hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float sun_vnoise(vec2 p){
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = sun_hash(i), b = sun_hash(i + vec2(1.0, 0.0));
    float c = sun_hash(i + vec2(0.0, 1.0)), d = sun_hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float sun_fbm(vec2 p){
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++){ s += a * sun_vnoise(p); p *= 2.02; a *= 0.5; }
    return s;   // ~0..1
}

// All sun rendering lives here so clouds composite OVER it. Built from cosSun (view·sun) and
// sunElev (LIGHT0_DIRECTION.y). Pieces are layered in later tasks; this is the current look verbatim.
vec3 sun_layers(vec3 rd) {
    if (!LIGHT0_ENABLED) { return vec3(0.0); }
    float cosSun = dot(rd, LIGHT0_DIRECTION);
    float sunElev = clamp(LIGHT0_DIRECTION.y, 0.0, 1.0);
    float sunset = smoothstep(sun_redden_onset, 0.0, sunElev) * sun_redden;   // 0 high → 1 at horizon
    float grow = 1.0 + sunset * sun_horizon_grow;                             // disc/halo swell at horizon

    float discEdge = cos(radians(sun_size * grow));
    float mu = clamp((cosSun - discEdge) / max(1.0 - discEdge, 1e-6), 0.0, 1.0);
    float discMask = smoothstep(0.0, 0.04, mu);
    float limb = mix(1.0, mix(0.45, 1.0, sqrt(mu)), sun_limb);
    float disc = discMask * limb;

    float corona = pow(max(cosSun, 0.0), sun_corona_size) * sun_corona_energy;
    float halo   = pow(max(cosSun, 0.0), sun_halo_size / grow) * sun_halo_energy;  // halo widens at horizon

    vec3 sunCol = mix(LIGHT0_COLOR, sun_redden_color, sunset);                 // warm→red tint
    if (sun_surface_on && discMask > 0.0) {
        // disc-local UVs: world-anchored tangent frame around the sun direction (no swimming).
        vec3 S = LIGHT0_DIRECTION;
        vec3 upv = abs(S.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
        vec3 T = normalize(cross(upv, S));
        vec3 Bt = cross(S, T);
        float rad = max(sin(radians(sun_size * grow)), 1e-4);
        vec2 pSurf = vec2(dot(rd, T), dot(rd, Bt)) / rad;          // unit-disc coords (|p|<=1 inside)
        float r2 = clamp(dot(pSurf, pSurf), 0.0, 1.0);
        float ball = sqrt(1.0 - r2);                               // 1 center -> 0 rim (sphere foreshorten)
        vec2 sph = pSurf / (ball + 0.35);                          // de-foreshorten so cells round off at the limb

        // GRANULATION: soft low-contrast convection mottle (fades toward the rim so it reads spherical).
        vec2 q = sph * sun_surface_cells + vec2(TIME * sun_surface_churn);
        float gran = (sun_fbm(q) - 0.5) * 2.0;                     // -1..1
        float granAmt = sun_surface_contrast * 0.6 * ball;         // rim-faded mottle

        // SUNSPOTS: sparse rounded dark patches from a lower-freq field; carve the disc so they read
        // even on the bright core (multiplicative darkening up to ~0.85).
        vec2 qs = sph * (sun_surface_cells * 0.4) + vec2(3.1, 8.7) + vec2(TIME * sun_surface_churn * 0.4);
        float sf = sun_fbm(qs);
        float spot = smoothstep(0.60, 0.48, sf) * sun_surface_spots * ball;

        disc *= max((1.0 + granAmt) * (1.0 - 0.85 * spot), 0.0);
        // subtle warm/cool chroma (reads even when the disc clips bright): warm in bright cells, cool in dim.
        sunCol *= mix(vec3(1.0), sun_redden_color, sun_surface_warm * clamp(0.5 + 0.5 * gran, 0.0, 1.0));
        // fantasy/exotic base override: strong tint + dim so the hue dominates instead of bleaching to
        // white (a full replace on a nuclear-bright disc read as a flat blob; this keeps the granulation).
        if (dot(sun_surface_color, sun_surface_color) > 1e-4) {
            sunCol = mix(sunCol, sun_surface_color, 0.7);
            disc *= 0.65;
        }
    }
    // Per-PIXEL cloud occlusion: sample the cloud dome at THIS pixel's direction, not the sun centre.
    // Centre-sampling hid an entire large disc when a cloud touched its middle; per-pixel means a cloud
    // only occludes the part of the disc it actually covers (the dome composite below is per-pixel too).
    float aPix = (cloud_enabled && rd.y > 0.0) ? clamp(texture(cloud_rd_tex, dir_to_uv(rd)).a, 0.0, 1.0) : 0.0;
    // Soft optical depth: thin cloud over the sun dims AND reddens the disc/corona; thick cloud kills them.
    vec3 ext = mix(vec3(1.0), sun_redden_color, clamp(aPix * sun_cloud_redden, 0.0, 1.0));  // warm extinction
    float discT  = (1.0 - aPix);            // disc/corona transmittance (per-pixel)
    float haloT  = (1.0 - aPix * 0.5);      // halo bleeds (half occlusion)
    // Surface mode renders a TEXTURED ball, so the core can't be nuclear-bright (it would clip the
    // granulation/spots to flat white). Drop the disc multiplier when the surface is on.
    float discMul = sun_surface_on ? 2.5 : 12.0;
    vec3 core = sunCol * ext * (disc * discMul + corona) * discT;
    vec3 glow = sunCol * halo * haloT;
    return sun_disc_energy * (core + glow);
}

void sky(){
    vec3 rd = EYEDIR;
    vec3 bg = background(rd);

    bg += sun_layers(rd);

    vec3 outColor = bg;
    if (cloud_enabled && rd.y > 0.0){
        // SOFTENED dome sample: a 5-tap rotated-cross blur (~0.75 texel) damps the lat-long
        // texel-grid CRAWL on moving cloud edges (the "squares as it moves" artifact) without
        // any temporal history (so no ghosting during flight). Cheap — this is the light sky pass.
        vec2 uv = dir_to_uv(rd);
        vec2 tx = 0.75 / vec2(textureSize(cloud_rd_tex, 0));
        vec4 c = texture(cloud_rd_tex, uv) * 0.40
               + texture(cloud_rd_tex, uv + vec2( tx.x,  tx.y)) * 0.15
               + texture(cloud_rd_tex, uv + vec2(-tx.x,  tx.y)) * 0.15
               + texture(cloud_rd_tex, uv + vec2( tx.x, -tx.y)) * 0.15
               + texture(cloud_rd_tex, uv + vec2(-tx.x, -tx.y)) * 0.15;
        if (cloud_debug == 1){ outColor = c.rgb; }
        // PREMULTIPLIED over-composite. The raymarch accumulates `scattered` front-to-back
        // (scattered += T*lum*(1-beer)) so c.rgb is ALREADY weighted by coverage/transmittance.
        // The old `mix(bg, c.rgb, c.a)` multiplied it by alpha a SECOND time → clouds ~2x too
        // dim (thin clouds nearly vanished). Correct over-composite is bg*(1-a) + premult_rgb.
        else { outColor = bg * (1.0 - clamp(c.a, 0.0, 1.0)) + c.rgb; }
    }

    COLOR = outColor;
    ALPHA = 1.0;
}
