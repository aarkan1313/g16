shader_type spatial;

// PRESENTER M1 — minimal material: draw the base field as a displaced plane,
// shaded by height + slope as a simple color ramp so the raw field is legible.
// No textures, no splat, no bake maps, no overlays. Displacement happens in the
// vertex shader (the mesh AABB is told the true range C#-side).

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

// Height color ramp (low -> high). Tuned for the default ~ -300..1100 m field.
uniform vec3 col_low = vec3(0.20, 0.34, 0.18);     // valley green
uniform vec3 col_mid = vec3(0.45, 0.40, 0.28);     // foothill tan
uniform vec3 col_high = vec3(0.52, 0.52, 0.55);    // rock grey
uniform vec3 col_snow = vec3(0.92, 0.94, 0.97);    // snow
uniform vec3 col_rock = vec3(0.38, 0.36, 0.34);    // steep slope
uniform float h_low = -200.0;
uniform float h_mid = 350.0;
uniform float h_high = 800.0;
uniform float h_snow = 1000.0;
uniform float slope_rock = 0.45;   // 1 - n.y above this reads as rock

varying vec2 v_uv;
varying vec3 v_world;

float height_at(vec2 uv) {
    return texture(heightmap, uv).r;
}

void vertex() {
    v_uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
    VERTEX.y = height_at(v_uv);
    v_world = VERTEX;   // mesh sits at the origin: model space == world space
}

void fragment() {
    float t = texel_world / region_size;
    float hl = height_at(v_uv - vec2(t, 0.0));
    float hr = height_at(v_uv + vec2(t, 0.0));
    float hd = height_at(v_uv - vec2(0.0, t));
    float hu = height_at(v_uv + vec2(0.0, t));
    vec3 n = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));

    float h = height_at(v_uv);
    float slope = 1.0 - n.y;

    // Height ramp.
    vec3 col = mix(col_low, col_mid, smoothstep(h_low, h_mid, h));
    col = mix(col, col_high, smoothstep(h_mid, h_high, h));
    col = mix(col, col_snow, smoothstep(h_high, h_snow, h));
    // Steep slopes read as rock regardless of height.
    col = mix(col, col_rock, smoothstep(slope_rock * 0.6, slope_rock, slope));

    NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
    ALBEDO = col;
    ROUGHNESS = 0.95;
}
