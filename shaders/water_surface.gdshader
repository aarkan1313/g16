shader_type spatial;
// AAA river/lake water. Transparent depth-faded water: reads the DEPTH buffer to know how deep the water
// is at each pixel (riverbed visible through shallow water, opaque deep blue in the channel), screen-space
// REFRACTION (the bed warps under the surface), FRESNEL reflectivity (grazing angles mirror the sky),
// flow-mapped animated NORMALS (current scrolls downstream from the ribbon's V), and shoreline + shallow
// FOAM. Modular: every look knob is a uniform.
render_mode cull_disabled, depth_draw_never, blend_mix;

uniform vec3 shallow_color : source_color = vec3(0.20, 0.45, 0.52);
uniform vec3 deep_color : source_color = vec3(0.02, 0.12, 0.22);
uniform float depth_fade_m = 4.0;        // metres of water depth over which it goes shallow→deep
uniform float flow_speed = 0.15;
uniform float wave_scale = 1.0;
uniform float refraction = 0.04;         // screen-space refraction strength
uniform float foam_edge = 0.82;          // UV.x bank-foam threshold
uniform float fresnel_strength = 0.5;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex : hint_depth_texture;

varying vec3 v_world;
varying float v_flowdist;

void vertex() {
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_flowdist = UV.y;
}

void fragment() {
    float t = TIME * flow_speed;
    // --- flow-mapped normal: two scrolling wave sets, the dominant one moving DOWNSTREAM (along V) ---
    vec2 n1 = vec2(sin(v_world.x * 0.06 * wave_scale + v_flowdist * 5.0 - t * 4.0),
                   cos(v_world.z * 0.06 * wave_scale + v_flowdist * 5.0 - t * 4.0)) * 0.5;
    vec2 n2 = vec2(sin(v_world.x * 0.17 * wave_scale - t * 2.0),
                   cos(v_world.z * 0.15 * wave_scale + t * 2.3)) * 0.25;
    vec2 ndxy = n1 + n2;
    NORMAL_MAP = normalize(vec3(ndxy, 1.0)) * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = 0.8;

    // --- water DEPTH from the depth buffer: how much terrain is behind the water surface at this pixel ---
    float scene_d = texture(depth_tex, SCREEN_UV).r;
    vec4 scene_v = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_d, 1.0);
    scene_v.xyz /= scene_v.w;
    float surface_vz = VERTEX.z;                       // this fragment's view-space depth (negative fwd)
    float water_depth = max(0.0, surface_vz - scene_v.z);   // metres of water column
    float depthN = clamp(water_depth / depth_fade_m, 0.0, 1.0);

    // --- screen-space refraction: warp the scene behind the water by the surface normal ---
    vec2 ref_uv = SCREEN_UV + ndxy * refraction;
    vec3 bed = texture(screen_tex, ref_uv).rgb;        // refracted terrain/bed behind the water

    // --- compose: shallow shows the (refracted) bed; deep is opaque water colour ---
    vec3 watercol = mix(shallow_color, deep_color, depthN);
    vec3 col = mix(bed, watercol, clamp(depthN * 1.3 + 0.25, 0.0, 1.0));

    // --- fresnel: grazing angles reflect sky (bright rim), head-on is transparent ---
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 4.0);
    col += fres * fresnel_strength;

    // --- foam: at the banks (UV.x near 0/1) AND where the water is very shallow (meets terrain) ---
    float edge = abs(fract(UV.x) - 0.5) * 2.0;
    float bankfoam = smoothstep(foam_edge, 1.0, edge);
    float shorefoam = (1.0 - smoothstep(0.0, 0.6, depthN)) * 0.6;   // foam where water is thin over terrain
    float foam = clamp(bankfoam + shorefoam, 0.0, 1.0);
    col = mix(col, vec3(0.95), foam * 0.7);

    ALBEDO = col;
    METALLIC = 0.0;
    ROUGHNESS = mix(0.04, 0.5, foam);
    SPECULAR = 0.6;
    ALPHA = clamp(depthN * 0.85 + fres * 0.4 + foam, 0.25, 1.0);   // transparent shallows, opaque deep
}
