shader_type spatial;

// PRESENTER — layered AAA terrain. Four surface-only base materials (grass, dirt,
// scree, rock; NO baked flora — flora is procedural decoration later) composited
// by a stack of procedural masks: slope, height, curvature (dirt collects in
// concavities), each boundary BROKEN BY NOISE so materials interlock organically
// instead of forming clean contour lines. Large-scale macro color variation on
// top so no two areas look identical. Per-texture two-scale anti-tiling +
// mipmaps/aniso. NO border jitter, NO detail bump. Toggles: P polish, T rock,
// Y grass, U scree, I dirt.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;
uniform float polish = 1.0;

uniform float grass_on = 1.0;
uniform float dirt_on = 1.0;
uniform float scree_on = 1.0;
uniform float rock_on = 1.0;

uniform sampler2D grass_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D dirt_tex  : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D scree_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rock_tex  : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float grass_scale_m = 9.0;
uniform float dirt_scale_m = 9.0;
uniform float scree_scale_m = 8.0;
uniform float rock_scale_m = 11.0;

uniform float antitile_mult = 5.3;
uniform float antitile_blend = 0.5;

// Boundary break-up: noise added to every mask threshold so transitions ragged.
uniform float edge_noise_m = 90.0;      // wavelength of the boundary-breaking noise
uniform float edge_noise_amp = 0.28;    // how much it pushes thresholds
// Macro color variation: broad value/tint drift so no area looks identical.
uniform float macro_color_m = 600.0;
uniform float macro_color_amp = 0.14;

// Plain ramp (polish OFF).
uniform vec3 col_low = vec3(0.20, 0.34, 0.18);
uniform vec3 col_mid = vec3(0.45, 0.40, 0.28);
uniform vec3 col_high = vec3(0.52, 0.52, 0.55);
uniform vec3 col_snow = vec3(0.92, 0.94, 0.97);
uniform float h_low = -200.0;
uniform float h_mid = 350.0;
uniform float h_high = 800.0;
uniform float h_snow = 1000.0;

// Subtle per-texture tints.
uniform vec3 grass_tint = vec3(0.88, 1.00, 0.74);
uniform vec3 dirt_tint  = vec3(0.92, 0.82, 0.66);
uniform vec3 scree_tint = vec3(1.00, 0.98, 0.93);
uniform vec3 rock_tint  = vec3(0.96, 0.96, 1.00);
uniform vec3 snow_col   = vec3(0.95, 0.96, 1.00);

// Zone controls (m / slope).
uniform float grass_top_h = 420.0;       // grass fades out above here
uniform float scree_top_h = 900.0;       // scree gives way to snow above
uniform float rock_slope_lo = 0.22;
uniform float rock_slope_hi = 0.40;
uniform float snow_h = 1000.0;

varying vec2 v_uv;
varying vec3 v_world;

float height_at(vec2 uv) {
    return texture(heightmap, uv).r;
}

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 wxz, float wl) {
    vec2 p = wxz / wl; vec2 i = floor(p); vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash2(i), hash2(i + vec2(1,0)), f.x),
               mix(hash2(i + vec2(0,1)), hash2(i + vec2(1,1)), f.x), f.y);
}

vec3 s2(sampler2D tex, vec2 uv, float sc) {
    return mix(texture(tex, uv / sc).rgb, texture(tex, uv / (sc * antitile_mult)).rgb, antitile_blend);
}
vec3 tri(sampler2D tex, vec3 wp, vec3 nrm, float sc) {
    vec3 bw = pow(abs(nrm), vec3(4.0)); bw /= (bw.x + bw.y + bw.z);
    return s2(tex, wp.zy, sc) * bw.x + s2(tex, wp.xz, sc) * bw.y + s2(tex, wp.xy, sc) * bw.z;
}

void vertex() {
    v_uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
    VERTEX.y = height_at(v_uv);
    v_world = VERTEX;
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
    // Curvature: positive in concavities (valleys/cracks) where dirt collects.
    float curv = (hl + hr + hd + hu) * 0.25 - h;

    // --- Plain ramp (polish OFF) ---
    vec3 plain = mix(col_low, col_mid, smoothstep(h_low, h_mid, h));
    plain = mix(plain, col_high, smoothstep(h_mid, h_high, h));
    plain = mix(plain, col_snow, smoothstep(h_high, h_snow, h));

    // --- Boundary-breaking noise: pushes every threshold so edges interlock ---
    float en = (vnoise(v_world.xz, edge_noise_m) - 0.5) * 2.0 * edge_noise_amp;
    float hN = h + en * 250.0;          // height masks jittered in meters
    float sN = slope + en * 0.12;       // slope mask jittered

    // --- Sample the 4 bases (anti-tiled triplanar), toggle to flat fallback ---
    vec3 grass = mix(vec3(0.26,0.40,0.20), tri(grass_tex, v_world, n, grass_scale_m) * grass_tint, grass_on);
    vec3 dirt  = mix(vec3(0.40,0.32,0.22), tri(dirt_tex,  v_world, n, dirt_scale_m)  * dirt_tint,  dirt_on);
    vec3 scree = mix(vec3(0.50,0.50,0.52), tri(scree_tex, v_world, n, scree_scale_m) * scree_tint, scree_on);
    vec3 rock  = mix(vec3(0.40,0.38,0.36), tri(rock_tex,  v_world, n, rock_scale_m)  * rock_tint,  rock_on);

    // --- Composite by masks (each boundary uses the jittered values) ---
    // Base ground: grass low -> scree high.
    vec3 col = mix(grass, scree, smoothstep(grass_top_h - 200.0, grass_top_h + 200.0, hN));
    // Dirt collects in concavities (curvature) and along the grass->scree band.
    float dirt_w = smoothstep(0.6, 2.5, curv) * (1.0 - smoothstep(scree_top_h, scree_top_h + 200.0, hN));
    dirt_w = max(dirt_w, smoothstep(0.15, 0.35, en + 0.3) * (1.0 - smoothstep(grass_top_h, grass_top_h + 250.0, hN)) * 0.4);
    col = mix(col, dirt, clamp(dirt_w, 0.0, 1.0));
    // Rock on steep slopes (jittered).
    col = mix(col, rock, smoothstep(rock_slope_lo, rock_slope_hi, sN));
    // Snow on high, flat-ish ground (jittered), suppressed on steep faces.
    float snow_w = smoothstep(snow_h - 120.0, snow_h + 120.0, hN) * (1.0 - smoothstep(0.30, 0.52, slope));
    col = mix(col, snow_col, snow_w);

    // --- Macro color variation: broad tint/value drift so areas differ ---
    float mc = vnoise(v_world.xz + vec2(53.0, 191.0), macro_color_m);
    col *= (1.0 + (mc - 0.5) * 2.0 * macro_color_amp);

    NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
    ALBEDO = mix(plain, col, polish);
    ROUGHNESS = 0.92;
}
