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
    vec3 outColor = bg;

    if (cloud_enabled && rd.y > 0.0){
        vec4 c = texture(cloud_rd_tex, dir_to_uv(rd));
        if (cloud_debug == 1){ outColor = c.rgb; }
        else { outColor = mix(bg, c.rgb, clamp(c.a, 0.0, 1.0)); }
    }

    COLOR = outColor;
    ALPHA = 1.0;
}
