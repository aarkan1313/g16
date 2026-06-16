shader_type spatial;

// PRESENTER — height/slope color ramp (no textures, no splat, no screen-space
// tricks: structurally cannot have the eye-level artifact the splat hit).
// `polish` (0/1) toggles the richer color/shading treatment live so we can A/B
// it against the plain ramp in the running lab.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;
uniform float polish = 1.0;   // 0 = plain ramp, 1 = polished

// Plain ramp colors.
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

// Polished ramp colors (richer, warmer key, cleaner snow).
uniform vec3 p_grass = vec3(0.26, 0.40, 0.20);
uniform vec3 p_grass_dry = vec3(0.46, 0.46, 0.26);
uniform vec3 p_soil = vec3(0.42, 0.34, 0.23);
uniform vec3 p_rock = vec3(0.40, 0.37, 0.35);
uniform vec3 p_rock_hi = vec3(0.55, 0.54, 0.55);
uniform vec3 p_snow = vec3(0.95, 0.96, 1.00);

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

    // --- Plain ramp ---
    vec3 plain = mix(col_low, col_mid, smoothstep(h_low, h_mid, h));
    plain = mix(plain, col_high, smoothstep(h_mid, h_high, h));
    plain = mix(plain, col_snow, smoothstep(h_high, h_snow, h));
    plain = mix(plain, col_rock, smoothstep(slope_rock * 0.6, slope_rock, slope));

    // --- Polished ramp ---
    // Height drives the ground tone (lush low -> dry high), slope drives rock,
    // altitude drives snow (suppressed on steep faces so cliffs stay bare).
    vec3 ground = mix(p_grass, p_grass_dry, smoothstep(-100.0, 500.0, h));
    ground = mix(ground, p_soil, smoothstep(450.0, 750.0, h));
    float rock_w = smoothstep(0.18, 0.42, slope);
    vec3 rock = mix(p_rock, p_rock_hi, smoothstep(600.0, 1100.0, h));
    vec3 col = mix(ground, rock, rock_w);
    float snow_w = smoothstep(900.0, 1150.0, h) * (1.0 - smoothstep(0.30, 0.55, slope));
    col = mix(col, p_snow, snow_w);
    // Gentle valley darkening: low + flat reads cooler/shaded, ridges catch light.
    float ao = mix(0.82, 1.0, smoothstep(-150.0, 250.0, h));
    vec3 polished = col * ao;

    NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
    ALBEDO = mix(plain, polished, polish);
    ROUGHNESS = mix(0.95, 0.90 - 0.2 * snow_w, polish);
}
