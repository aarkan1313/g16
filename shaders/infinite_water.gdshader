shader_type spatial;
render_mode cull_disabled;

// Infinite-world sea. Kept deliberately SIMPLE + guaranteed-visible (the depth-buffer version rendered "just
// clear"). Solid-ish blue water with animated ripples + fresnel; depth/foam refinement comes back later once the
// base is confirmed visible. No depth_texture dependency (that was the suspect for the invisible surface).
uniform vec3 water_color : source_color = vec3(0.08, 0.30, 0.45);
uniform vec3 deep_color  : source_color = vec3(0.02, 0.12, 0.26);
uniform float wave_scale = 1.0;
uniform float flow_speed = 0.15;
uniform float water_alpha = 0.9;

varying vec3 v_world;

void vertex() { v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }

void fragment() {
    // ripples via NORMAL_MAP (tangent space) — do NOT overwrite NORMAL with a world-space guess (that was the
    // bug: NORMAL is VIEW-space, so a world-up guess lit the plane correctly only from one side → "clear from
    // above, blue from below"). NORMAL_MAP perturbs the real geometry normal correctly for any view angle.
    float t = TIME * flow_speed;
    vec2 r = vec2(sin(v_world.x * 0.04 * wave_scale + t), cos(v_world.z * 0.04 * wave_scale - t)) * 0.4
           + vec2(sin(v_world.x * 0.13 * wave_scale - t * 1.7), cos(v_world.z * 0.11 * wave_scale + t * 1.3)) * 0.2;
    NORMAL_MAP = normalize(vec3(r.x, r.y, 1.0)) * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = 0.6;

    // fresnel from the true geometry normal (view space) — bright rim at grazing angles.
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
    vec3 col = mix(water_color, deep_color, 0.4) + fres * 0.30;

    ALBEDO = col;
    METALLIC = 0.0;
    ROUGHNESS = 0.06;
    SPECULAR = 0.8;
    ALPHA = water_alpha;
}
