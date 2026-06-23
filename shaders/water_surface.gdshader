shader_type spatial;
render_mode cull_disabled, depth_draw_always;

// Minimal static-water surface for the hydrology lab / Arc 2: a grid mesh lifted to the substrate's
// water_level where wet, collapsed far below where dry (so dry cells render under terrain = invisible).
// Translucent blue with depth-based deepening + a soft fresnel. Reads the substrate, agrees with terrain.

uniform sampler2D water_level_tex;   // R32F: water surface elevation per cell, or a large-negative sentinel = dry
uniform sampler2D terrain_height_tex;// R32F: carved terrain height per cell (for water depth → colour)
uniform float region_size = 4096.0;  // world metres across the grid
uniform float dry_sentinel = -1.0e8;  // water_level below this = dry (hide)

uniform vec3 shallow_color : source_color = vec3(0.25, 0.55, 0.62);
uniform vec3 deep_color    : source_color = vec3(0.04, 0.16, 0.35);
uniform float depth_for_deep = 12.0;  // metres of water depth that reaches full deep_color
uniform float alpha_min = 0.55;
uniform float alpha_max = 0.92;

varying float v_depth;     // water depth (surface - bed) at this vertex
varying float v_wet;       // 1 wet, 0 dry

void vertex() {
    vec2 uv = (VERTEX.xz / region_size) + vec2(0.5);
    float wl = texture(water_level_tex, uv).r;
    float th = texture(terrain_height_tex, uv).r;
    if (wl < dry_sentinel) {
        v_wet = 0.0; v_depth = 0.0;
        VERTEX.y = th - 1000.0;          // collapse dry cells far under terrain → not visible
    } else {
        v_wet = 1.0;
        v_depth = max(wl - th, 0.0);
        VERTEX.y = wl;                   // lift to the water surface elevation
    }
}

void fragment() {
    if (v_wet < 0.5) { discard; }
    float t = clamp(v_depth / depth_for_deep, 0.0, 1.0);
    vec3 col = mix(shallow_color, deep_color, t);
    // soft fresnel brighten at grazing angles
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
    ALBEDO = col + fres * 0.15;
    METALLIC = 0.0;
    ROUGHNESS = 0.08;
    SPECULAR = 0.6;
    ALPHA = mix(alpha_min, alpha_max, t);
}
