shader_type spatial;

// Material board cell — renders ONE material under ONE technique, so the AAA
// toolbox can be surveyed across the library. `technique` selects the method;
// `secondary_*` is a second material used by the blend techniques.

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rough_tex : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D ao_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D secondary_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D secondary_height : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D height_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;

// technique:
// 0 flat albedo | 1 +normal | 2 +roughness | 3 +AO | 4 full PBR
// 5 anti-tiled | 6 macro-tint | 7 detail-map (macro+detail) | 8 distance-fade detail
// 9 height-blend two materials | 10 full PBR + anti-tile + macro (the "good" stack)
uniform int technique = 4;
uniform float tile = 3.0;
uniform float antitile_mult = 5.3;
uniform float detail_scale = 6.0;
uniform float macro_scale = 0.25;

float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

vec3 anti_tiled(sampler2D t, vec2 uv) {
    return mix(texture(t, uv).rgb, texture(t, uv / antitile_mult).rgb, 0.5);
}

void fragment() {
    vec2 uv = UV * tile;
    vec3 col = texture(albedo_tex, uv).rgb;
    float rough = 0.9;
    bool do_normal = false;
    bool do_ao = false;

    if (technique == 1) { do_normal = true; }
    else if (technique == 2) { rough = texture(rough_tex, uv).r; }
    else if (technique == 3) { do_ao = true; }
    else if (technique == 4) { do_normal = true; do_ao = true; rough = texture(rough_tex, uv).r; }
    else if (technique == 5) { col = anti_tiled(albedo_tex, uv); }
    else if (technique == 6) {
        float m = texture(albedo_tex, UV * macro_scale).r;   // broad value drift
        col *= 0.78 + 0.44 * m;
    }
    else if (technique == 7) {
        vec3 macro = texture(albedo_tex, uv).rgb;
        vec3 detail = texture(albedo_tex, UV * detail_scale).rgb;
        col = macro * (0.6 + 0.8 * luma(detail));            // detail modulates macro
    }
    else if (technique == 8) {
        // near = detailed, far = coarse; FRONT half of the quad detailed.
        float d = clamp(UV.y, 0.0, 1.0);
        col = mix(texture(albedo_tex, uv).rgb, texture(albedo_tex, uv * 0.4).rgb, d);
    }
    else if (technique == 9) {
        // height-blend: pick whichever material's height is higher at this texel
        // (the AAA "no muddy 50/50" blend), controlled across the quad by UV.x.
        vec3 a = texture(albedo_tex, uv).rgb;
        vec3 b = texture(secondary_albedo, uv).rgb;
        float ha = texture(height_tex, uv).r + (1.0 - UV.x);
        float hb = texture(secondary_height, uv).r + UV.x;
        float w = smoothstep(-0.1, 0.1, hb - ha);
        col = mix(a, b, w);
    }
    else if (technique == 10) {
        col = anti_tiled(albedo_tex, uv);
        float m = texture(albedo_tex, UV * macro_scale).r;
        col *= 0.82 + 0.36 * m;
        do_normal = true; do_ao = true; rough = texture(rough_tex, uv).r;
    }

    ALBEDO = col;
    ROUGHNESS = rough;
    if (do_ao) { AO = texture(ao_tex, uv).r; AO_LIGHT_AFFECT = 1.0; }
    if (do_normal) { NORMAL_MAP = texture(normal_tex, uv).rgb; NORMAL_MAP_DEPTH = 1.0; }
}
