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

// All sun rendering lives here so clouds composite OVER it. Built from cosSun (view·sun) and
// sunElev (LIGHT0_DIRECTION.y). Pieces are layered in later tasks; this is the current look verbatim.
vec3 sun_layers(vec3 rd, float aSun) {
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
    // Soft optical depth: thin cloud over the sun dims AND reddens the disc/corona; thick cloud kills them.
    // The halo spans a wider cone, so only partially occlude it (it bleeds around the cloud edge).
    vec3 ext = mix(vec3(1.0), sun_redden_color, clamp(aSun * sun_cloud_redden, 0.0, 1.0));  // warm extinction
    float discT  = (1.0 - clamp(aSun, 0.0, 1.0));            // disc/corona transmittance
    float haloT  = (1.0 - clamp(aSun, 0.0, 1.0) * 0.5);     // halo bleeds (half occlusion)
    vec3 core = sunCol * ext * (disc * 12.0 + corona) * discT;
    vec3 glow = sunCol * halo * haloT;
    return sun_disc_energy * (core + glow);
}

void sky(){
    vec3 rd = EYEDIR;
    vec3 bg = background(rd);

    float aSun = 0.0;
    if (cloud_enabled && LIGHT0_DIRECTION.y > 0.0) {
        aSun = clamp(texture(cloud_rd_tex, dir_to_uv(LIGHT0_DIRECTION)).a, 0.0, 1.0);
    }
    bg += sun_layers(rd, aSun);

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
