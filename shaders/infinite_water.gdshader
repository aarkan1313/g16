shader_type spatial;
render_mode cull_disabled;

// Infinite-world sea: a flat translucent plane that derives depth/shoreline from the SCENE DEPTH BUFFER (the
// terrain already rendered), not a height texture — the standard infinite-ocean approach. Depth = how far the
// terrain bed is below the water surface at each pixel → shallow shore (light + foam) → deep (dark, opaque).
uniform vec3 shallow_color : source_color = vec3(0.16, 0.42, 0.50);
uniform vec3 deep_color    : source_color = vec3(0.02, 0.10, 0.22);
uniform vec3 foam_color    : source_color = vec3(0.88, 0.94, 0.97);
uniform float depth_for_deep = 40.0;   // metres of water depth reaching full deep colour
uniform float foam_depth = 3.0;        // shoreline foam band (metres of depth)
uniform float wave_scale = 1.0;
uniform float flow_speed = 0.15;
uniform float base_alpha = 0.86;       // legible by default (was too transparent → "hard to see")

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

void fragment() {
    // water depth from the scene depth buffer (enhancement, not the whole look — water stays legible even if
    // depth is ~0): linearize terrain depth + this pixel's depth to view space and subtract.
    vec2 uv = SCREEN_UV;
    float sceneRaw = texture(depth_tex, uv).r;
    vec4 sv = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, sceneRaw, 1.0); sv.xyz /= sv.w;
    vec4 wv = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, FRAGCOORD.z, 1.0); wv.xyz /= wv.w;
    float water_depth = max((-sv.z) - (-wv.z), 0.0);

    float td = clamp(water_depth / depth_for_deep, 0.0, 1.0);
    vec3 col = mix(shallow_color, deep_color, td);
    float foam = 1.0 - smoothstep(0.0, foam_depth, water_depth);
    col = mix(col, foam_color, foam * 0.7);

    // animated ripple normal (two octaves for a livelier, more visible surface)
    float t = TIME * flow_speed;
    vec2 n2 = vec2(sin(VERTEX.x * 0.04 * wave_scale + t), cos(VERTEX.z * 0.04 * wave_scale - t)) * 0.10
            + vec2(sin(VERTEX.x * 0.13 * wave_scale - t * 1.7), cos(VERTEX.z * 0.11 * wave_scale + t * 1.3)) * 0.05;
    NORMAL = normalize(vec3(n2.x, 1.0, n2.y));

    // fresnel rim brightens at grazing angles so the water surface reads clearly against terrain/sky.
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 4.0);
    col += fres * 0.25;

    ALBEDO = col;
    METALLIC = 0.0; ROUGHNESS = mix(0.04, 0.14, td); SPECULAR = 0.8;
    ALPHA = clamp(base_alpha + fres * 0.1, 0.0, 1.0);   // mostly opaque so it's clearly visible
}
