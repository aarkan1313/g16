shader_type spatial;
#include "res://shaders/field_math.gdshaderinc"
// GROUND — minimal height-color PLACEHOLDER (2026-06-21 strip).
// The full per-pixel material system (placement / arrays / blend / splat / old terrain_lab) was
// stripped back to a clean base. This just displaces the mesh from the heightfield and paints a
// height + slope color ramp, lit by Godot's default lighting (the sky/sun). Not blank; the base
// we plan CDLOD / infinite terrain on. NO render_mode — object space == world at identity, matching
// the field. Everything tunable is a uniform.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

// S1: live-field (analytic) displacement vs. the baked heightmap. ground_fieldp()
// mirrors FieldParams (pushed as fp_* from TerrainLab.cs); analytic_h() evaluates the
// SAME field_height() the compute bake uses, so the two paths agree by construction.
uniform bool  use_analytic = true;
uniform int   analytic_seed = 0;
uniform float analytic_spacing = 4.0;
// Godot's shading language requires ONE variable per uniform statement (no comma lists).
uniform float fp_base_freq;
uniform float fp_amplitude;
uniform float fp_lacunarity;
uniform float fp_gain;
uniform int   fp_octaves;
uniform int   fp_field_mode;
uniform int   fp_cont_octaves;
uniform float fp_cont_freq;
uniform float fp_cont_weight;
uniform float fp_uplift_freq;
uniform float fp_uplift_weight;
uniform float fp_uplift_lo;
uniform float fp_uplift_hi;
uniform float fp_macro_pivot;
uniform float fp_macro_amp;
uniform float fp_hill_damp;
uniform float fp_ridge_freq;
uniform float fp_ridge_amp;
uniform float fp_mtn_lo;
uniform float fp_mtn_hi;
uniform float fp_grain_stretch;
uniform float fp_cont_warp;
uniform float fp_uplift_warp;
uniform float fp_massif_freq;
uniform float fp_massif_floor;
uniform float fp_foothill_w;
uniform float fp_foothill_h;

FieldP ground_fieldp() {
    FieldP fp;
    fp.origin_x = 0.0; fp.origin_z = 0.0; fp.spacing = analytic_spacing;
    fp.seed = uint(analytic_seed); fp.res = 0u; fp.octaves = uint(fp_octaves);
    fp.base_freq = fp_base_freq; fp.amplitude = fp_amplitude; fp.lacunarity = fp_lacunarity;
    fp.gain = fp_gain; fp.field_mode = uint(fp_field_mode); fp.cont_freq = fp_cont_freq;
    fp.cont_weight = fp_cont_weight; fp.uplift_freq = fp_uplift_freq; fp.uplift_weight = fp_uplift_weight;
    fp.uplift_lo = fp_uplift_lo; fp.uplift_hi = fp_uplift_hi; fp.macro_pivot = fp_macro_pivot;
    fp.macro_amp = fp_macro_amp; fp.hill_damp = fp_hill_damp; fp.ridge_freq = fp_ridge_freq;
    fp.ridge_amp = fp_ridge_amp; fp.mtn_lo = fp_mtn_lo; fp.mtn_hi = fp_mtn_hi;
    fp.grain_stretch = fp_grain_stretch; fp.cont_octaves = uint(fp_cont_octaves);
    fp.cont_warp = fp_cont_warp; fp.uplift_warp = fp_uplift_warp; fp.massif_freq = fp_massif_freq;
    fp.massif_floor = fp_massif_floor; fp.foothill_w = fp_foothill_w; fp.foothill_h = fp_foothill_h;
    return fp;
}
float analytic_h(vec2 world_xz) {
    return field_height(world_xz, uint(analytic_seed), analytic_spacing, ground_fieldp());
}

// height-color ramp (meters -> color) + slope->rock. Placeholder palette; tune freely.
uniform vec3  col_low  : source_color = vec3(0.34, 0.40, 0.24);   // valley grass/dirt
uniform vec3  col_mid  : source_color = vec3(0.46, 0.39, 0.28);   // slope earth
uniform vec3  col_high : source_color = vec3(0.43, 0.43, 0.45);   // rock
uniform vec3  col_peak : source_color = vec3(0.93, 0.94, 0.97);   // snow
uniform vec3  col_cliff: source_color = vec3(0.34, 0.33, 0.34);   // steep rock
uniform float h_low  = 40.0;
uniform float h_mid  = 280.0;
uniform float h_high = 500.0;
uniform float h_peak = 600.0;
uniform float cliff_lo = 0.45;
uniform float cliff_hi = 0.72;
uniform float ground_rough = 0.92;

varying vec3 v_normal;   // world-space geometric normal from neighbour height taps
varying float v_h;       // height (m)

float height_at(vec2 uv) { return texture(heightmap, uv).r; }

void vertex() {
    if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        VERTEX.y = analytic_h(wxz);
        float e = analytic_spacing;
        float hl = analytic_h(wxz - vec2(e, 0.0)), hr = analytic_h(wxz + vec2(e, 0.0));
        float hd = analytic_h(wxz - vec2(0.0, e)), hu = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(hl - hr, 2.0 * e, hd - hu));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
        vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
        VERTEX.y = height_at(uv);
        float t = texel_world / region_size;
        float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
        float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
        v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
        NORMAL = v_normal;   // object-space (== world at identity) -> Godot transforms to view for lighting
        v_h = VERTEX.y;
    }
}

void fragment() {
    vec3 c = mix(col_low, col_mid, smoothstep(h_low, h_mid, v_h));
    c = mix(c, col_high, smoothstep(h_mid, h_high, v_h));
    c = mix(c, col_peak, smoothstep(h_high, h_peak, v_h));
    float slope = 1.0 - v_normal.y;                       // 0 flat .. 1 vertical
    c = mix(c, col_cliff, smoothstep(cliff_lo, cliff_hi, slope));   // steep faces -> rock
    ALBEDO = c;
    ROUGHNESS = ground_rough;
}
