shader_type spatial;
// GROUND — minimal height-color PLACEHOLDER (2026-06-21 strip).
// The full per-pixel material system (placement / arrays / blend / splat / old terrain_lab) was
// stripped back to a clean base. This just displaces the mesh from the heightfield and paints a
// height + slope color ramp, lit by Godot's default lighting (the sky/sun). Not blank; the base
// we plan CDLOD / infinite terrain on. NO render_mode — object space == world at identity, matching
// the field. Everything tunable is a uniform.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

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
    vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
    VERTEX.y = height_at(uv);
    float t = texel_world / region_size;
    float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
    float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
    v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
    NORMAL = v_normal;   // object-space (== world at identity) -> Godot transforms to view for lighting
    v_h = VERTEX.y;
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
