shader_type spatial;
render_mode cull_disabled;

// Infinite-world sea: a flat translucent plane that derives depth/shoreline from the SCENE DEPTH BUFFER (the
// terrain already rendered), not a height texture — the standard infinite-ocean approach. Depth = how far the
// terrain bed is below the water surface at each pixel → shallow shore (light + foam) → deep (dark, opaque).
uniform vec3 shallow_color : source_color = vec3(0.22, 0.55, 0.60);
uniform vec3 deep_color    : source_color = vec3(0.02, 0.12, 0.26);
uniform vec3 foam_color    : source_color = vec3(0.85, 0.92, 0.95);
uniform float depth_for_deep = 30.0;   // metres of water depth reaching full deep colour
uniform float foam_depth = 1.5;        // shoreline foam band (metres of depth)
uniform float wave_scale = 1.0;
uniform float flow_speed = 0.15;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

void fragment() {
    // water depth from the scene depth buffer: linearize the terrain depth + this water pixel's depth (both to
    // view space) and subtract → how much water column is in front of the terrain bed at this pixel.
    vec2 uv = SCREEN_UV;
    float sceneRaw = texture(depth_tex, uv).r;
    vec4 sv = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, sceneRaw, 1.0); sv.xyz /= sv.w;   // terrain view pos
    vec4 wv = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, FRAGCOORD.z, 1.0); wv.xyz /= wv.w; // water view pos
    float water_depth = max((-sv.z) - (-wv.z), 0.0);    // view-space distance between bed and water surface

    float td = clamp(water_depth / depth_for_deep, 0.0, 1.0);
    vec3 col = mix(shallow_color, deep_color, td);
    float foam = 1.0 - smoothstep(0.0, foam_depth, water_depth);
    col = mix(col, foam_color, foam * 0.6);

    // animated ripple normal
    float t = TIME * flow_speed;
    vec2 n2 = vec2(sin(VERTEX.x * 0.04 * wave_scale + t), cos(VERTEX.z * 0.04 * wave_scale - t)) * 0.05;
    NORMAL = normalize(vec3(n2.x, 1.0, n2.y));

    ALBEDO = col;
    METALLIC = 0.0; ROUGHNESS = mix(0.03, 0.12, td); SPECULAR = 0.7;
    ALPHA = mix(0.55, 0.95, td);
}
