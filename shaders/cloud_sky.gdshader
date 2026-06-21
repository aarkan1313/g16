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

// --- AT-1 GPU atmosphere: sky-view LUT provider (default off = approved keyframed gradient) ---
uniform bool atmosphere_on = false;
uniform sampler2D atmo_skyview_tex : filter_linear, repeat_enable;   // Hillaire sky-view LUT (az, sqrt-warped el)
uniform float atmo_exposure : hint_range(0.0, 60.0) = 10.0;          // scales LUT radiance into the tonemapper (gate-tuned)
uniform vec3  sky_tint : source_color = vec3(1.0);                   // ST4-2 fantasy/manual tint applied to the physical sky (white = no-op)

// Sun-disc brightness, decoupled from LIGHT0_ENERGY: the overcast proxy dims the directional
// light's energy globally (terrain gloom), but the VISIBLE sun disc must stay bright — a cloud
// in front occludes it via the dome composite, total coverage elsewhere must NOT dim it.
uniform float sun_disc_energy = 1.3;
uniform float night_factor = 0.0;   // 0 day .. 1 deep night (drives sun horizon-gate; moon/star fade later)
uniform float sun_size = 0.6;    // angular disc radius in DEGREES (visible sun size)
uniform float sun_limb = 0.70;   // limb darkening: 0 = flat disc, 1 = strong center→rim falloff (polish: more spherical)
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

// --- MOON (Stage 3b): custom-uniform disc, gated to night. Reuses sun_fbm for maria/craters. ---
uniform vec3  moon_dir = vec3(0.0, 0.7, 0.7);                      // direction TO the moon (pushed from C#, normalized)
uniform vec3  moon_color : source_color = vec3(0.85, 0.88, 1.0);  // cool moonlight white
uniform float moon_disc_energy = 0.9;
uniform float moon_size = 1.2;        // angular radius in DEGREES
uniform float moon_limb = 0.6;        // limb darkening
uniform float moon_phase = 1.0;       // 0 new (dark) .. 0.5 half .. 1 full
uniform float moon_halo_size : hint_range(8.0, 400.0) = 140.0;   // higher = tighter (a subtle moon glow, not a blob)
uniform float moon_halo_energy = 0.2;
uniform float moon_surf_cells : hint_range(1.0, 40.0) = 8.0;
uniform float moon_surf_contrast : hint_range(0.0, 1.0) = 0.5;
uniform float moon_surf_spots : hint_range(0.0, 1.0) = 0.4;
uniform float moon_surf_churn : hint_range(0.0, 1.0) = 0.0;

// --- STARS + MILKY WAY (Stage 3d): procedural, faded in at night, on a slowly-rotating celestial sphere ---
uniform float star_brightness = 1.0;
uniform float star_density : hint_range(0.0, 1.0) = 0.5;
uniform float star_twinkle : hint_range(0.0, 1.0) = 0.5;
uniform float star_rotation = 0.003;     // celestial rotation speed (rad/s)
// METEORS (Celestial C2): occasional shooting stars. Defaults subtle; gate-tunable. meteor_debug forces a
// streak (headless capture). meteor_rate = how often a channel fires; speed = sweep speed (shorter window).
uniform bool meteors_on = true;
uniform float meteor_rate : hint_range(0.0, 1.0) = 0.2;
uniform float meteor_brightness : hint_range(0.0, 3.0) = 1.1;
uniform float meteor_length : hint_range(0.0, 1.0) = 0.5;
uniform float meteor_speed : hint_range(0.0, 1.0) = 0.5;
uniform vec3 meteor_color = vec3(0.85, 0.92, 1.0);          // base streak color (cool white); per-meteor hue varies by meteor_color_var
uniform float meteor_color_var : hint_range(0.0, 1.0) = 0.25;  // 0 = all base color · 1 = vivid fantasy hues per meteor
uniform bool meteor_debug = false;
// NIGHT SKY (Celestial Galaxy/Nebula v2): the global brightness multiplier for the billboard layer.
// LIVE master on/off for the whole galaxy/nebula lane (0 = truly free).
uniform float night_sky_brightness : hint_range(0.0, 3.0) = 0.25;
// CELESTIAL BILLBOARDS (Galaxy/Nebula v2): up to 8 discrete objects, each rendered in a local 2D
// tangent-plane disc (structure, not 3D fbm). Pushed from C# on change only. type in color_type.w:
// 0 = spiral galaxy, 1 = elliptical galaxy, 2 = nebula.
const int MAX_BILLBOARDS = 8;
uniform int  bb_count = 0;
uniform vec4 bb_dir_size[MAX_BILLBOARDS];    // xyz = sky dir (unit), w = angular radius (radians)
uniform vec4 bb_color_type[MAX_BILLBOARDS];  // rgb = primary/core color, w = type
uniform vec4 bb_color2[MAX_BILLBOARDS];      // rgb = secondary/arm color, w = brightness
uniform vec4 bb_params[MAX_BILLBOARDS];      // x = rotation(rad), y = tilt(0..1 squash), z = arms, w = seed

uniform vec3 sky_top : source_color = vec3(0.30, 0.48, 0.74);
uniform vec3 sky_horizon : source_color = vec3(0.68, 0.74, 0.80);
uniform vec3 ground_color : source_color = vec3(0.22, 0.26, 0.22);

// --- CIRRUS (CO-2): cheap 2D wind-streaked high layer, composited under the cumulus dome. default off. ---
uniform bool  cirrus_on = false;
uniform float cirrus_coverage : hint_range(0.0, 1.0) = 0.6;   // how much sky has streaks
uniform float cirrus_density : hint_range(0.0, 1.0) = 0.7;    // opacity of the streaks
uniform float cirrus_wind_deg = 30.0;                          // streak direction (degrees)
uniform float cirrus_speed = 0.04;                             // drift along the wind (visible motion)
uniform float cirrus_scale = 1.4;                              // streak frequency
uniform float cirrus_sharpness : hint_range(0.0, 1.0) = 0.5;  // soft wisps ↔ crisp filaments
uniform float cirrus_variety : hint_range(0.0, 1.0) = 0.7;    // per-region density/orientation/size variation (realism)
uniform float cirrus_altitude = 8000.0;                        // world altitude of the sheet (m) → sets parallax
uniform vec3  cirrus_color : source_color = vec3(0.92, 0.94, 1.0);
uniform vec3  cam_world = vec3(0.0);                           // camera world pos (pushed from CloudVolume) — for world-anchored cirrus

// PI is a built-in in Godot sky shaders — don't redefine it.

// AT-1 sky-view sampling mapping — MUST match atmosphere_skyview.glsl's per-texel ray build
// (az = u*2π, el = v²·π/2). repeat_enable on the sampler wraps the az seam at u=0/1.
vec2 atmo_dir_to_uv(vec3 d){
    float az = atan(d.z, d.x); if (az < 0.0) az += 2.0 * PI;
    float el = asin(clamp(d.y, 0.0, 1.0));
    return vec2(az / (2.0 * PI), sqrt(el / (0.5 * PI)));
}

vec3 background(vec3 dir){
    // keyframed gradient (the approved look — also the night fallback)
    vec3 grad = (dir.y >= 0.0)
        ? mix(sky_horizon, sky_top, pow(clamp(dir.y, 0.0, 1.0), 0.5))
        : mix(sky_horizon, ground_color, clamp(-dir.y * 4.0, 0.0, 1.0));
    if (atmosphere_on){
        // atmo_dir_to_uv clamps el>=0, so a below-horizon dir samples the HORIZON row → the sky color is
        // CONTINUOUS across dir.y=0 (no hard "line where the sky ends" when the finite terrain doesn't fill
        // the lower hemisphere). Below the horizon, fade that physical horizon color toward the ground tint.
        vec3 atmo = texture(atmo_skyview_tex, atmo_dir_to_uv(dir)).rgb * atmo_exposure;
        // sky_tint: white = physical sky unchanged; a saturated (fantasy) tint RECOLORS the sky to that hue while
        // keeping the physical brightness / gradient / sun-halo structure (luminance-preserving, no-op at white).
        float tintSat = max(max(sky_tint.r, sky_tint.g), sky_tint.b) - min(min(sky_tint.r, sky_tint.g), sky_tint.b);
        vec3 tintN = sky_tint / max(dot(sky_tint, vec3(0.2126, 0.7152, 0.0722)), 1e-3);   // luminance-normalized hue
        vec3 recolored = dot(atmo, vec3(0.2126, 0.7152, 0.0722)) * tintN;
        atmo = mix(atmo, recolored, clamp(tintSat * 2.0, 0.0, 1.0));
        vec3 sky = (dir.y >= 0.0) ? atmo : mix(atmo, ground_color, clamp(-dir.y * 4.0, 0.0, 1.0));
        // DAY = physical atmosphere; blend to the approved keyframed gradient as night falls so the
        // gated night look (moon/stars/night-grade) is regression-free + the dusk→night tail is clean.
        return mix(sky, grad, night_factor);
    }
    return grad;
}

// view dir → lat-long UV (matches dir_from_texel in cloud_raymarch.glsl)
vec2 dir_to_uv(vec3 d){
    float az = atan(d.z, d.x);             // -π..π
    if (az < 0.0) az += 2.0 * PI;          // 0..2π
    float el = asin(clamp(d.y, 0.0, 1.0)); // 0..π/2
    return vec2(az / (2.0 * PI), el / (0.5 * PI));
}

float sun_hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float hash13(vec3 p3){ p3 = fract(p3 * 0.1031); p3 += dot(p3, p3.zyx + 31.32); return fract((p3.x + p3.y) * p3.z); }
// 3D value noise + fbm — sampled directly on the view direction so it's smooth on the sphere (no
// blocky 2D-projection artifacts the Milky Way had).
float vnoise3(vec3 p){
    vec3 i = floor(p), f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i), n100 = hash13(i + vec3(1,0,0));
    float n010 = hash13(i + vec3(0,1,0)), n110 = hash13(i + vec3(1,1,0));
    float n001 = hash13(i + vec3(0,0,1)), n101 = hash13(i + vec3(1,0,1));
    float n011 = hash13(i + vec3(0,1,1)), n111 = hash13(i + vec3(1,1,1));
    return mix(mix(mix(n000,n100,u.x), mix(n010,n110,u.x), u.y),
               mix(mix(n001,n101,u.x), mix(n011,n111,u.x), u.y), u.z);
}
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
    float horizonGate = smoothstep(-0.04, 0.0, LIGHT0_DIRECTION.y);   // 1 above horizon, 0 once below
    return sun_disc_energy * (core + glow) * horizonGate;
}

// MOON (Stage 3b): mirrors sun_layers — a disc at moon_dir with limb darkening, a reused-sun_fbm
// surface (maria + crater mottle), a phase terminator, and a soft cool halo. Gated to night
// (night_factor) and to above-horizon, occluded per-pixel by clouds (same as the sun).
vec3 moon_layers(vec3 rd) {
    float vis = night_factor * smoothstep(-0.04, 0.02, moon_dir.y);   // only at night + above the horizon
    if (vis <= 0.0) { return vec3(0.0); }
    float cosM = dot(rd, moon_dir);
    float discEdge = cos(radians(moon_size));
    float mu = clamp((cosM - discEdge) / max(1.0 - discEdge, 1e-6), 0.0, 1.0);
    float discMask = smoothstep(0.0, 0.04, mu);
    float limb = mix(1.0, mix(0.45, 1.0, sqrt(mu)), moon_limb);
    float disc = discMask * limb;

    // disc-local frame (world-anchored → no swimming), de-foreshortened like the sun surface
    vec3 S = moon_dir;
    vec3 upv = abs(S.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 T = normalize(cross(upv, S));
    vec3 Bt = cross(S, T);
    float rad = max(sin(radians(moon_size)), 1e-4);
    vec2 p = vec2(dot(rd, T), dot(rd, Bt)) / rad;     // unit-disc coords (|p|<=1 inside)
    float r2 = clamp(dot(p, p), 0.0, 1.0);
    float ball = sqrt(1.0 - r2);                      // sphere foreshorten (1 centre → 0 rim)
    vec2 sph = p / (ball + 0.35);

    if (discMask > 0.0) {
        // MARIA: large dark "seas" from a low-frequency field (the big recognizable dark patches).
        vec2 qm = sph * (moon_surf_cells * 0.32) + vec2(5.3, 1.7);
        float maria = smoothstep(0.58, 0.40, sun_fbm(qm)) * moon_surf_spots;
        // CRATERS: finer scattered dark pits from a higher-frequency field.
        vec2 qc = sph * (moon_surf_cells * 1.7) + vec2(2.1, 9.4);
        float craters = smoothstep(0.60, 0.72, sun_fbm(qc)) * moon_surf_spots * 0.6;
        // fine granular mottle (optional churn) for surface texture between the big features.
        vec2 q = sph * moon_surf_cells + vec2(moon_surf_churn * TIME);
        float gran = (sun_fbm(q) - 0.5) * 2.0;
        float shade = (1.0 + moon_surf_contrast * 0.35 * gran) * (1.0 - 0.72 * maria) * (1.0 - 0.45 * craters);
        disc *= max(mix(1.0, shade, ball), 0.0);   // surface strongest at centre, foreshortens to the rim
    }

    // PHASE terminator: lit fraction from a phase-derived light direction over the disc-sphere normal.
    // phase 1 → light toward the viewer (full), 0.5 → from the side (half), 0 → behind (new/dark).
    float angle = (1.0 - moon_phase) * PI;
    vec3 L = vec3(sin(angle), 0.0, cos(angle));
    vec3 n = normalize(vec3(p.x, p.y, ball));
    float lit = smoothstep(-0.08, 0.08, dot(n, L));
    disc *= lit;

    // Halo = scattered moonlight, so it must scale with the ILLUMINATED FRACTION (a thin crescent
    // emits little light → faint halo; a full moon → full halo). Without this the glow was a fixed
    // bright ring regardless of phase ("doesn't change relative to shape").
    float illum = 0.5 + 0.5 * cos(angle);   // 1 full · 0.5 half · 0 new
    float halo = pow(max(cosM, 0.0), moon_halo_size) * moon_halo_energy * illum;

    // per-pixel cloud occlusion (same mechanism as the sun)
    float aPix = (cloud_enabled && rd.y > 0.0) ? clamp(texture(cloud_rd_tex, dir_to_uv(rd)).a, 0.0, 1.0) : 0.0;
    float discT = (1.0 - aPix);
    float haloT = (1.0 - aPix * 0.5);

    vec3 core = moon_color * disc * 2.2 * discT;   // lower mult → less runaway bloom (the "crazy luminosity")
    vec3 glow = moon_color * halo * haloT;
    return moon_disc_energy * vis * (core + glow);
}

// STARFIELD (Celestial C1) — magnitude-stratified live point-stars: a dense FAINT grid + a sparse BRIGHT
// grid layered over it (few-bright/many-faint), each star with size variety + a warm/cool color-temp tint
// + smooth twinkle. Live (not baked) so the twinkle animates. sr = the (rotated) celestial direction.
vec3 star_field(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int L = 0; L < 2; L++){
        float scale = (L == 0) ? 320.0 : 95.0;                            // dense faint / sparse bright
        float bright = (L == 0) ? 0.55 : 1.5;
        vec3 cell = floor(sr * scale); vec3 f = fract(sr * scale) - 0.5;
        float h = hash13(cell + float(L) * 7.0);
        float present = step(1.0 - star_density * (L == 0 ? 0.06 : 0.018), h);   // ~60% fewer stars (user)
        vec3 jit = (vec3(hash13(cell + 1.7), hash13(cell + 4.3), hash13(cell + 8.1)) - 0.5) * 0.7;
        float d = length(f - jit);
        float sz = mix(0.10, 0.20, hash13(cell + 20.0));                   // size variety
        float pt = present * smoothstep(sz, 0.0, d);
        float tw = mix(1.0, 0.5 + 0.5 * sin(TIME * 0.9 + h * 62.8), star_twinkle);   // ~70% slower blink (user)
        float temp = hash13(cell + 30.0);                                  // 0 warm .. 1 cool
        vec3 tint = mix(vec3(1.0, 0.82, 0.7), vec3(0.75, 0.85, 1.0), temp);
        acc += tint * (pt * tw * mix(0.35, 1.0, hash13(cell + 12.0)) * bright);
    }
    return acc * star_brightness;
}

// One meteor channel. Cycles on `period` seconds; only OCCASIONALLY fires (hash vs a rate-derived gate).
// When it fires, a head sweeps an arc over a ~window-second window, drawing a thin head-bright/tail-fading
// streak. Pure TIME (stateless); two early-outs make it ~free when idle. meteor_debug pins it visible.
const int METEOR_CHANNELS = 2;
vec3 meteor_channel(vec3 rd, float seed){
    float window = mix(1.6, 0.7, clamp(meteor_speed, 0.0, 1.0));   // active seconds (faster → shorter)
    float period = mix(42.0, 8.0, clamp(meteor_rate, 0.0, 1.0));   // seconds between potential fires
    float t = TIME + seed * 53.0;
    float cyc = floor(t / period);
    float local = fract(t / period) * period;                     // seconds into this cycle
    if (!meteor_debug && local > window) { return vec3(0.0); }     // idle most of the cycle (early-out 1)
    float fireGate = mix(0.05, 0.9, clamp(meteor_rate, 0.0, 1.0)); // fraction of cycles that actually fire
    if (!meteor_debug && hash13(vec3(cyc, seed, 1.0)) > fireGate) { return vec3(0.0); }   // (early-out 2)
    float p = meteor_debug ? 0.45 : clamp(local / window, 0.0, 1.0);   // 0..1 sweep progress
    // hashed per-cycle params
    vec3 A = normalize(vec3(hash13(vec3(cyc, seed, 2.0)) * 2.0 - 1.0,
                            0.35 + 0.6 * hash13(vec3(cyc, seed, 3.0)),
                            hash13(vec3(cyc, seed, 4.0)) * 2.0 - 1.0));   // start dir, upper sky
    vec3 up = abs(A.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 tang = normalize(cross(A, up));
    vec3 bnor = cross(A, tang);
    float ad = hash13(vec3(cyc, seed, 5.0)) * 6.2831853;
    vec3 v = normalize(cos(ad) * tang + sin(ad) * bnor);          // random travel tangent at A
    float arc = radians(mix(18.0, 42.0, hash13(vec3(cyc, seed, 6.0))));
    vec3 H = normalize(A + v * tan(arc * p));                     // head at progress p
    vec3 hv = normalize(v - H * dot(v, H));                       // travel tangent at the head
    vec3 hw = cross(H, hv);
    vec3 rel = rd - H;
    float xa = dot(rel, hv);                                      // along-track (ahead of head = +)
    float ya = dot(rel, hw);                                      // cross-track
    float trail = radians(mix(2.0, 7.0, clamp(meteor_length, 0.0, 1.0)));
    float thick = radians(0.10);
    float crossT = smoothstep(thick, 0.0, abs(ya));              // thin
    float tail = smoothstep(-trail, 0.0, xa);                    // tail-end (0) → head (1): extent + gradient
    float headCut = smoothstep(0.012, 0.0, xa);                 // keep only BEHIND the head
    float streak = crossT * tail * headCut;
    // bright HEAD flare — a glowing tip so it reads as more than a thin line
    float hd = acos(clamp(dot(rd, H), -1.0, 1.0));
    float head = smoothstep(radians(0.55), 0.0, hd);
    float life = sin(p * 3.14159265);                          // fade in/out over the window
    float bri = mix(0.45, 1.3, hash13(vec3(cyc, seed, 7.0))) * meteor_brightness;
    // per-meteor COLOR: base meteor_color → a vivid hashed hue, blended by meteor_color_var (the fantasy knob)
    float hue = hash13(vec3(cyc, seed, 9.0));
    vec3 fancy = 0.55 + 0.45 * cos(6.2831853 * (hue + vec3(0.0, 0.33, 0.67)));
    vec3 mcol = mix(meteor_color, fancy, meteor_color_var);
    return mcol * ((streak + head * 0.8) * life * bri);
}
vec3 meteors(vec3 rd){
    if (!meteors_on || rd.y < -0.02) { return vec3(0.0); }       // off / below horizon → free
    vec3 acc = vec3(0.0);
    for (int i = 0; i < METEOR_CHANNELS; i++) { acc += meteor_channel(rd, float(i) + 1.0); }
    return acc;
}

// CELESTIAL BILLBOARDS (Galaxy/Nebula v2) — each object is a direction-anchored 2D disc. Structure,
// not 3D fbm: spiral arms / elliptical cores / (Phase 3) nebula lobes + dust lanes. Cheap: a pixel only
// pays for objects whose disc it's inside (per-object dot-product early-out).

// Project a sky dir `d` into a billboard's local disc centered on `dir`. Returns uv where |uv|~0 at
// center and ~1 at the rim (reach). `rot` spins the disc; `tilt` squashes one axis (disc inclination).
vec2 bb_disc_uv(vec3 d, vec3 dir, float reach, float rot, float tilt){
    vec3 up = abs(dir.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 ax = normalize(cross(up, dir));
    vec3 ay = cross(dir, ax);
    vec3 rel = d - dir * dot(d, dir);            // tangent-plane component
    vec2 uv = vec2(dot(rel, ax), dot(rel, ay)) / max(sin(reach), 1e-4);
    float cs = cos(rot), sn = sin(rot);
    uv = vec2(cs * uv.x - sn * uv.y, sn * uv.x + cs * uv.y);
    uv.y /= max(1.0 - 0.85 * tilt, 0.15);        // squash → inclined disc
    return uv;
}

// Realistic spiral/elliptical galaxy in disc-uv space (Hubble-reference look). Organic FLOCCULENT arms via
// domain-warped log-spiral (not clean cosine arms), golden HDR bulge + blinding nucleus, reddish-brown dust
// lanes threading the arms, faint inter-arm disk glow, pink/blue star-cluster knots, fine field stars, halo.
// coreCol = warm bulge, armCol = blue arms. Calibrated so arms keep color at brightness ~0.8 (nucleus clips).
vec3 galaxy_billboard(vec2 uv, vec3 coreCol, vec3 armCol, float arms, float isSpiral, float seed){
    float r = length(uv);
    if (r > 1.0) return vec3(0.0);
    float rim = smoothstep(1.0, 0.80, r);
    float theta = atan(uv.y, uv.x);

    // DOMAIN WARP — perturb angle + radius with noise so the structure is organic, not mathematical.
    float nA = vnoise3(vec3(uv * 2.0, seed));
    float nB = vnoise3(vec3(uv * 2.1 + 5.0, seed * 1.3));
    float wTheta = theta + (nA - 0.5) * 0.9;
    float wR     = r * (1.0 + (nB - 0.5) * 0.28);

    // radial light profiles: blinding nucleus, golden bulge, warped exponential disk, faint halo
    float nucleus = exp(-r * r * 90.0);
    float bulge   = exp(-r * r * 7.0);
    float disc    = exp(-wR * 3.0);
    float halo    = exp(-r * 1.2) * 0.12;

    // spiral arms (warped coords) — broad arms broken into flocculent patches along their length
    float wind = 4.5;
    float phase = arms * wTheta - wind * log(r + 0.06) + seed * 6.2831;
    float arm = pow(0.5 + 0.5 * cos(phase), 3.0);
    float flock = 0.35 + 0.95 * vnoise3(vec3(cos(phase), sin(phase), r * 9.0 + seed * 4.0) * 3.0);
    float armEnv = smoothstep(0.03, 0.18, r) * smoothstep(1.0, 0.50, r);
    float armLight = disc * arm * flock * armEnv;

    // multi-octave filigree modulating the faint inter-arm disk
    float fil = 0.6 + 0.4 * (vnoise3(vec3(uv * 8.0, seed)) * 0.6 + vnoise3(vec3(uv * 18.0, seed * 2.0)) * 0.4);
    float diskGlow = disc * 0.16 * fil;

    // dust lanes — dark reddish-brown, follow the arms (offset phase) + fine dust grain, inner-to-mid disk
    float dphase = arms * wTheta - wind * log(r + 0.06) + seed * 6.2831 + 0.5;
    float dustArm = pow(max(cos(dphase), 0.0), 3.0);
    float dustGrain = vnoise3(vec3(uv * 14.0 + 9.0, seed));
    float dust = clamp(dustArm * 0.8 + (dustGrain - 0.45) * 0.7, 0.0, 1.0) * smoothstep(0.85, 0.07, r) * armEnv;

    // assemble luminance: spiral (arms dominate) vs elliptical (smooth)
    float spiralLum = bulge * 0.5 + armLight * 1.35 + diskGlow;
    spiralLum *= 1.0 - 0.85 * dust;
    float ellipLum = bulge * 0.7 + disc * 0.3;
    float lum = (nucleus * 2.5 + mix(ellipLum, spiralLum, isSpiral)) * rim;

    // color: golden bulge → blue arms; white-hot nucleus; dust reddens/darkens
    vec3 col = mix(armCol, coreCol, clamp(bulge * 1.3, 0.0, 1.0));
    col = mix(col, armCol * vec3(0.9, 0.95, 1.1), clamp(armLight * 1.5, 0.0, 0.55) * isSpiral);
    col = mix(col, vec3(1.0, 0.97, 0.9), nucleus * 0.8);
    col = mix(col, col * vec3(0.72, 0.52, 0.40), dust * 0.6 * isSpiral);

    // star clusters along arms (pink HII + blue OB associations)
    vec2 cell = floor(uv * 26.0); vec2 f = fract(uv * 26.0) - 0.5;
    float h = hash13(vec3(cell + seed * 9.0, seed));
    float knot = step(0.82, h) * smoothstep(0.22, 0.0, length(f)) * arm * armEnv;
    vec3 knotCol = mix(vec3(1.0, 0.5, 0.6), vec3(0.6, 0.8, 1.0), hash13(vec3(cell, seed)));

    // fine field stars across the disk
    vec2 c2 = floor(uv * 12.0); vec2 f2 = fract(uv * 12.0) - 0.5;
    float h2 = hash13(vec3(c2 + seed * 3.0, seed * 1.7));
    float spark = step(0.92, h2) * smoothstep(0.12, 0.0, length(f2)) * (disc * 0.6 + 0.15) * armEnv;

    return (col * lum + knotCol * knot * 1.3 + vec3(0.9, 0.95, 1.0) * spark) * rim;
}

// Emission nebula in disc-uv space (Hubble-reference look: Omega/Horsehead/NGC604). Domain-warped turbulent
// gas, dark dust PILLARS/voids carving it, saturated two-color emission with hot bright cores, embedded
// star cluster. The black-space context + stars + dust + saturated color make turbulence read as a NEBULA,
// not a terrestrial cloud (the C1 failure was using cloud-style fbm with weak color/structure on a galaxy).
vec3 nebula_billboard(vec2 uv, vec3 col1, vec3 col2, float seed){
    float r = length(uv);
    if (r > 1.0) return vec3(0.0);
    float rim = smoothstep(1.0, 0.62, r);

    // domain-warped billowing gas
    vec2 p = uv * 2.2;
    float w1 = vnoise3(vec3(p, seed));
    float w2 = vnoise3(vec3(p.yx * 1.4 + 7.0, seed * 1.7));
    p += (vec2(w1, w2) - 0.5) * 1.2;
    float f = 0.0, a = 0.55; vec2 q = p;
    for (int i = 0; i < 5; i++) { f += a * vnoise3(vec3(q, seed + float(i) * 1.3)); q *= 2.07; a *= 0.5; }
    float fall = exp(-r * r * 1.8);                              // localize to a patch
    float density = clamp(f * 1.55 * fall - 0.12, 0.0, 1.0);
    float glow = pow(density, 1.6);                              // hot bright cores

    // dark dust pillars/voids carve the gas (low-freq mask) — the Horsehead silhouette element
    float dustMask = smoothstep(0.55, 0.28, vnoise3(vec3(uv * 3.0 + 21.0, seed * 0.7)));
    density *= mix(1.0, 0.20, dustMask);

    // saturated two-color emission; hot cores brighten + warm-shift
    float cmap = clamp(vnoise3(vec3(uv * 1.6 + 3.0, seed)) * 1.3, 0.0, 1.0);
    vec3 gas = mix(col1, col2, cmap);
    gas = mix(gas, gas + vec3(0.28, 0.13, -0.04), glow * 0.6);

    float lum = density * 1.5 * rim;

    // embedded star cluster — brighter/denser where the gas glows (OB associations light the nebula)
    vec2 cell = floor(uv * 20.0); vec2 cf = fract(uv * 20.0) - 0.5;
    float h = hash13(vec3(cell + seed * 4.0, seed));
    float star = step(0.86, h) * smoothstep(0.16, 0.0, length(cf)) * (0.35 + density);

    return gas * lum + vec3(0.9, 0.95, 1.0) * star * rim;
}

vec3 billboards(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int i = 0; i < bb_count; i++){
        vec3 dir = bb_dir_size[i].xyz; float reach = bb_dir_size[i].w;
        if (dot(sr, dir) < cos(reach)) continue;               // EARLY-OUT: one dot product
        float type = bb_color_type[i].w;
        float rot = bb_params[i].x, tilt = bb_params[i].y, arms = bb_params[i].z, seed = bb_params[i].w;
        vec2 uv = bb_disc_uv(sr, dir, reach, rot, tilt);
        vec3 c;
        if (type < 1.5) c = galaxy_billboard(uv, bb_color_type[i].rgb, bb_color2[i].rgb, arms, type < 0.5 ? 1.0 : 0.0, seed);
        else            c = nebula_billboard(uv, bb_color_type[i].rgb, bb_color2[i].rgb, seed);
        acc += c * bb_color2[i].w;                              // per-slot brightness
    }
    return acc;
}

// STARS + NIGHT SKY (Stage 3d / Celestial C1): a reworked magnitude-stratified starfield + a baked
// procedural galaxy/nebula color, on a slowly-rotating celestial sphere, faded in by night_factor and
// above the horizon. Direction-only (EYEDIR), so they stay locked to the sky as the camera moves.
vec3 stars_layer(vec3 rd){
    if (night_factor <= 0.001) { return vec3(0.0); }
    vec3 sr = normalize(rd);
    float a = TIME * star_rotation;                       // celestial rotation about the vertical axis
    float ca = cos(a), sa = sin(a);
    sr = vec3(ca * sr.x - sa * sr.z, sr.y, sa * sr.x + ca * sr.z);

    // STARS: magnitude-stratified live point-stars (few-bright/many-faint, size + color-temp variety).
    vec3 star = star_field(sr);

    // BILLBOARDS: galaxy/nebula objects (early-out at brightness 0 → lane is free). Global multiplier
    // + fade applied once here (per-slot brightness is inside billboards()).
    vec3 bb = vec3(0.0);
    if (night_sky_brightness > 0.0001) { bb = billboards(sr) * night_sky_brightness; }

    float fade = night_factor * smoothstep(-0.02, 0.12, rd.y);   // night + above horizon
    return (star + meteors(rd) + bb) * fade;
}

// CIRRUS (CO-2): anisotropic wind-stretched fbm3 → high thin filaments. Streaks run ALONG the wind
// (cross-wind varies faster). Gated by elevation (fades at the horizon), tinted warm near the sun,
// dimmed at night. Cheap: one fbm3 tap. Returns (rgb, alpha) for an over-composite.
// Cheap 3-octave value-noise fbm for cirrus (the 5-octave fbm3 is too costly full-screen).
float cfbm(vec3 p){ float s = 0.0, a = 0.5; for (int i = 0; i < 3; i++){ s += a * vnoise3(p); p *= 2.07; a *= 0.5; } return s * 1.14; }

// One cirrus band anchored at world altitude `alt`. Cheap per-region MACRO fields (single vnoise3 taps)
// VARY density (gathered into regions with gaps), orientation (fanning streaks) and feature size (mixed
// fine/broad filaments) across the sky → varied real cirrus, not one uniform field. cirrus_variety scales
// it all (0 = uniform). EARLY-OUT in gap regions skips the expensive streak noise (keeps it cheap).
float cirrus_band(vec3 rd, float alt, float scaleMul, float angOff, float densMul){
    float t = (alt - cam_world.y) / max(rd.y, 0.05);
    if (t <= 0.0) { return 0.0; }                              // camera at/above the sheet → ray never reaches it
    vec2 wpos = cam_world.xz + rd.xz * t;                       // world XZ where the ray pierces this band
    vec2 muv = wpos * 0.00003;                                  // macro coords (~33 km regions)
    float vary = cirrus_variety;
    float region = vnoise3(vec3(muv + TIME * cirrus_speed * 0.07, 0.3));      // cheap macro region field
    float macro = smoothstep(mix(-1.0, 0.42, vary), mix(0.55, 0.72, vary), region);   // density variety + gaps
    if (macro < 0.02) { return 0.0; }                          // EARLY OUT (gap) before the streak noise
    float orient = (vnoise3(vec3(muv * 2.1 + 5.0, 1.0)) - 0.5) * 2.2 * vary;  // local orientation jitter (rad)
    float scl    = mix(1.0, mix(0.55, 1.8, vnoise3(vec3(muv * 1.6 + 9.0, 2.0))), vary);  // local size variety
    float ang = radians(cirrus_wind_deg) + angOff + orient;
    vec2 w = vec2(cos(ang), sin(ang));
    vec2 huv = wpos * (cirrus_scale * scaleMul * scl * 0.00025);
    float along = dot(huv, w);
    float cross = dot(huv, vec2(-w.y, w.x));
    float n = cfbm(vec3(along + TIME * cirrus_speed, cross * 6.0, 0.0));      // streaks (cross stretched ×6)
    float streak = smoothstep(1.0 - cirrus_coverage, 1.0, n);
    streak = pow(streak, mix(1.0, 4.0, cirrus_sharpness));
    return streak * macro * densMul;
}

vec4 cirrus_layer(vec3 rd){
    if (!cirrus_on || rd.y < 0.02) { return vec4(0.0); }
    float band = smoothstep(0.02, 0.18, rd.y) * (1.0 - 0.3 * smoothstep(0.85, 1.0, rd.y));
    if (band <= 0.001) { return vec4(0.0); }
    float a = clamp(cirrus_band(rd, cirrus_altitude, 1.0, 0.0, 1.0) * band * cirrus_density, 0.0, 1.0);
    // sun tint: warm the streaks near the sun direction (forward scatter)
    float toSun = max(dot(normalize(rd), LIGHT0_DIRECTION), 0.0);
    vec3 col = mix(cirrus_color, sun_redden_color, 0.4 * pow(toSun, 8.0)) * mix(0.6, 1.2, LIGHT0_COLOR.r);
    a *= mix(0.12, 1.0, 1.0 - night_factor);           // mostly fade out at night
    return vec4(col, a);
}

void sky(){
    vec3 rd = EYEDIR;
    vec3 bg = background(rd);

    bg += stars_layer(rd);     // behind the sun/moon/clouds (they composite over)
    bg += sun_layers(rd);
    bg += moon_layers(rd);

    vec4 ci = cirrus_layer(rd);              // CO-2: high thin layer, OVER sun/moon, UNDER cumulus
    bg = bg * (1.0 - ci.a) + ci.rgb * ci.a;

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
