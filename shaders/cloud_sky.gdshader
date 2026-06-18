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

void sky(){
    vec3 rd = EYEDIR;
    vec3 bg = background(rd);

    // SUN DISC + glow — drawn into the BACKGROUND so clouds composite OVER it (a cloud in
    // front of the sun correctly occludes it; in clear sky the disc shows). The cloud sky
    // shader previously drew no sun at all, so toggling clouds on made the sun vanish and
    // left only a bright gradient ("a light behind fog"). LIGHT0_* is the scene's sun.
    if (LIGHT0_ENABLED){
        float cosSun = dot(rd, LIGHT0_DIRECTION);
        // tight disc + wider soft glow toward the sun
        float disc = smoothstep(0.9994, 0.9998, cosSun);          // ~0.6° disc
        float glow = pow(max(cosSun, 0.0), 220.0) * 0.5;          // halo
        vec3 sunRGB = LIGHT0_COLOR * LIGHT0_ENERGY;
        bg += sunRGB * (disc * 12.0 + glow);
    }

    vec3 outColor = bg;
    if (cloud_enabled && rd.y > 0.0){
        vec4 c = texture(cloud_rd_tex, dir_to_uv(rd));
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
