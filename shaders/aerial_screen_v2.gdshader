shader_type spatial;
// AT-2 v2 screen-space aerial perspective — log-Z froxel sampling.
//
// Instead of the old quadratic froxel-z (sqrt(dist/far)), samples with log-Z encoding that matches
// the compute shader's log-Z depth mapping. This gives a smooth froxel coordinate across all view
// distances with no hard band or slice-stepping discontinuity.
//
// Log-Z encode: zf = log(dist / z_near + 1.0) / log(aerial_far / z_near + 1.0)
//
// blend_mix (NOT blend_add): aerial must MULTIPLY scene by transmittance AND add inscatter — a replace.
// fog_disabled is critical: this quad is at the far plane, env depth fog would grey it solid otherwise.
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never, blend_mix, fog_disabled;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform sampler2D depth_tex  : hint_depth_texture;
uniform sampler3D aerial_tex : filter_linear;
uniform mat4  inv_view_proj;
uniform vec3  cam_world = vec3(0.0);
uniform float aerial_far = 90000.0;         // metres — MUST match compute shader cam_pos_far.w
uniform float aerial_z_near = 1.0;          // log-Z near anchor (MUST match compute shader Z_NEAR = 1.0)
uniform float aerial_strength = 10.0;
// Path-radiance haze: as transmittance drops, distant terrain fades to this tint (not to black).
// Energy-conserving: (1 - T) weight grows exactly as extinction removes surface light.
uniform vec3  aerial_haze : source_color = vec3(0.20, 0.26, 0.34);
uniform float aerial_haze_str : hint_range(0.0, 2.0) = 1.0;
uniform bool  aerial_on = false;
// Reverse-Z (Godot 4.3+): sky clears to depth 0.0; geometry is > 0. Gate out sky (AT-1 handles it).
uniform float sky_depth_eps = 0.000001;

void vertex() {
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float d = texture(depth_tex, uv).r;
    if (!aerial_on || d < sky_depth_eps) { ALPHA = 0.0; discard; }

    vec3 col = texture(screen_tex, uv).rgb;
    vec4 ndc = vec4(uv * 2.0 - 1.0, d, 1.0);
    vec4 wp = inv_view_proj * ndc; wp /= wp.w;
    float dist = length(wp.xyz - cam_world);

    // Log-Z encode: zf in [0,1] matching the compute shader's log-Z decode.
    float logBase = log(aerial_far / aerial_z_near + 1.0);
    float zf = clamp(log(dist / aerial_z_near + 1.0) / logBase, 0.0, 1.0);
    vec4 aer = texture(aerial_tex, vec3(uv, zf));   // rgb inscatter, a = transmittance

    vec3 outc = col * aer.a + aer.rgb * aerial_strength + (1.0 - aer.a) * aerial_haze * aerial_haze_str;

    ALBEDO = outc;
    ALPHA = 1.0;
}
