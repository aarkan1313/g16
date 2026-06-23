shader_type spatial;
render_mode cull_disabled, depth_draw_always;

// Modular AAA water surface. Used by sea (one global plane) + later lakes/rivers (per-body planes). The mesh
// NODE is positioned at the water surface Y in C# (plane stays flat at local y=0); this shader does the look:
// depth/transparency, surface motion, shoreline foam. SSR reflections + flow-aligned normals land in Task 6.
uniform float water_level = 40.0;          // world Y of this water surface (matches the node's Y position)
uniform sampler2D terrain_height_tex;      // R32F carved terrain height per cell (for depth + shoreline)
uniform float region_size = 4096.0;        // world metres across the terrain grid (for UV mapping)
uniform float shallow_depth = 10.0;        // water depth that reaches full deep colour
uniform float wave_scale = 1.0;
uniform float flow_speed = 0.15;
uniform vec3 shallow_color : source_color = vec3(0.30, 0.62, 0.66);
uniform vec3 deep_color    : source_color = vec3(0.03, 0.14, 0.30);
uniform vec3 foam_color    : source_color = vec3(0.85, 0.92, 0.95);
uniform float foam_width = 2.5;            // metres of shoreline foam band

varying vec3 v_world;

void vertex() { v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

void fragment() {
    // sample terrain bed under this fragment (region-global UV, so offset lake planes still hit the right cell).
    vec2 uv = (v_world.xz / region_size) + vec2(0.5);
    float bed = texture(terrain_height_tex, uv).r;
    float depth = water_level - bed;
    if (depth <= 0.0) { discard; }                 // bed above the surface here → dry land (coastline edge)

    // cheap procedural ripple normal (normal-map upgrade in Task 6)
    float t = TIME * flow_speed;
    vec2 n2 = vec2(sin(v_world.x * 0.05 * wave_scale + t), cos(v_world.z * 0.05 * wave_scale - t)) * 0.06;
    NORMAL = normalize(vec3(n2.x, 1.0, n2.y));

    float td = clamp(depth / shallow_depth, 0.0, 1.0);
    vec3 col = mix(shallow_color, deep_color, td);
    float foam = 1.0 - smoothstep(0.0, foam_width, depth);   // foam band at the shoreline
    col = mix(col, foam_color, foam * 0.7);

    ALBEDO = col;
    METALLIC = 0.0; ROUGHNESS = mix(0.02, 0.12, td); SPECULAR = 0.7;
    ALPHA = mix(0.6, 0.95, td);
}
