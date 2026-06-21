#[compute]
#version 450

// Night-sky structure+color bake (Celestial C1). The procedural night sky in cloud_sky.gdshader's
// stars_layer() is a STATIC function of the (un-rotated) view direction + the galaxy/nebula tunables —
// so bake the full galaxy (core bulge + dust lanes + star-clouds + fantasy color gradient) here ONCE
// (and on a tunable/preset change) into an rgba16f lat-long COLOR texture; cloud_sky then does one
// sample + the cheap celestial rotation at runtime (cheaper than the old 3 per-pixel 5-octave fbm3).
// The noise functions are copied VERBATIM from cloud_sky.gdshader so the bake matches the procedural
// reference pixel-for-pixel (pixel-diff-verified, then default-on). Evolves milkyway_bake.glsl
// (band-structure R-channel) → full galaxy+nebula RGB. Brightness stays LIVE in the sky shader (not
// baked) so dragging it never re-bakes; the bake stores col*lum at unit brightness.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1, std430) restrict readonly buffer Params {
    vec4 coreDir_size;          // xyz = core direction (un-rotated), w = coreSize
    vec4 tilt_width_curve_dust; // x = tilt, y = width, z = curve, w = dust amount
    vec4 coreColor_bright;      // rgb = core color, w = (reserved; brightness is live in the shader)
    vec4 armColor;              // rgb = arm color, w unused
    vec4 neb_count;             // x = active nebula count (0..4)
    vec4 neb_dir_scale[4];      // xyz = nebula direction, w = scale (0 tight .. 1 broad)
    vec4 neb_color_dens[4];     // rgb = nebula color, w = density
} P;

const float PI = 3.14159265;

// ===== copied VERBATIM from cloud_sky.gdshader (hash13 / vnoise3 / fbm3) — keep identical =====
float hash13(vec3 p3){ p3 = fract(p3 * 0.1031); p3 += dot(p3, p3.zyx + 31.32); return fract((p3.x + p3.y) * p3.z); }
float vnoise3(vec3 p){
    vec3 i = floor(p), f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i), n100 = hash13(i + vec3(1,0,0));
    float n010 = hash13(i + vec3(0,1,0)), n110 = hash13(i + vec3(1,1,0));
    float n001 = hash13(i + vec3(0,0,1)), n101 = hash13(i + vec3(1,0,1));
    float n011 = hash13(i + vec3(0,1,1)), n111 = hash13(i + vec3(1,1,1));
    return mix(mix(mix(n000,n100,u.x), mix(n010,n110,u.x), u.y),
               mix(mix(n001,n101,u.x), mix(n011,n111,u.x), u.y), u.z);
}
float fbm3(vec3 p){
    float a = 0.5, s = 0.0;
    for (int i = 0; i < 5; i++){ s += a * vnoise3(p); p *= 2.03; a *= 0.5; }
    return s;
}
// ===== end copied block =====

// ridged noise: sharp bright filaments (folded |noise| over octaves) — cosmic gas tendrils, NOT puffy
// clouds. KEEP BYTE-IDENTICAL to cloud_sky.gdshader's copy.
float ridge(vec3 p){
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++){ float nn = 1.0 - abs(2.0 * vnoise3(p) - 1.0); s += nn * nn * a; p *= 2.1; a *= 0.5; }
    return s;
}

// galaxy color at a (un-rotated) direction d. Core = a bright bulge concentrated around coreDir (not a
// uniform band → kills the old uniform arc); band = a great circle through the core plane, carved by
// dust-lane fbm; star clouds = bright fbm knots; color = mix(armColor, coreColor) by core proximity.
// Returns col*lum WITHOUT brightness (brightness is a live shader multiplier). Look constants are
// gate-tunable. KEEP THIS FUNCTION BYTE-IDENTICAL to cloud_sky.gdshader's reference copy.
vec3 galaxy_color(vec3 d){
    vec3 coreDir = normalize(P.coreDir_size.xyz);
    float coreSize = P.coreDir_size.w;
    float tilt = P.tilt_width_curve_dust.x, width = P.tilt_width_curve_dust.y;
    float curve = P.tilt_width_curve_dust.z, dust = P.tilt_width_curve_dust.w;
    // LOCALIZE: the galaxy is a PATCH around coreDir, NOT a world-spanning great-circle band. Hard-cut the
    // far hemisphere + fade by angular distance from the centre so it reads as a distant galaxy you look AT.
    float cd = dot(d, coreDir);
    if (cd <= 0.0) return vec3(0.0);
    float ang = acos(clamp(cd, 0.0, 1.0));                                 // angular distance from the centre
    float reach = mix(0.30, 0.80, coreSize);                              // smaller patch (~17°..46°) → reads distant
    float env = smoothstep(reach, reach * 0.22, ang);                     // 1 at centre → 0 by reach
    if (env <= 0.0) return vec3(0.0);
    // band/streak THROUGH the centre → an elongated lens shape (localized by env, not a full ring).
    vec3 planeN = normalize(vec3(sin(tilt), 0.35, cos(tilt)));
    float along = dot(normalize(cross(planeN, vec3(0.0, 1.0, 0.0))), d);
    float w = width * (0.6 + 0.8 * fbm3(d * 4.0 + vec3(11.0)));
    float bandDist = abs(dot(d, planeN) + curve * along * along * sign(dot(d, planeN)));
    float band = smoothstep(w, 0.0, bandDist);
    float clouds = fbm3(d * 16.0);
    float lanes = fbm3(d * 30.0 + vec3(5.0));
    float dustCarve = mix(1.0, smoothstep(0.30, 0.62, lanes), dust);     // dark dust lanes carve the body
    float texv = mix(0.5, 1.2, smoothstep(0.35, 0.70, clouds));          // bright/dim texture (never fully 0)
    float body = band * env * texv * dustCarve;                          // CONTINUOUS galactic band (reads as a galaxy)
    float knots = smoothstep(0.62, 0.90, fbm3(d * 22.0)) * env * 0.8;    // bright star-cloud knots in the body
    float core = pow(cd, mix(300.0, 900.0, 1.0 - coreSize)) * 0.5;       // tight defined nucleus (not a soft bloom blob)
    float lum = body + knots + core;
    vec3 col = mix(P.armColor.rgb, P.coreColor_bright.rgb, clamp(core * 0.8 + env * 0.5, 0.0, 1.0));
    return col * lum;
}

// nebula color at direction d: up to 4 procedural colored gas clouds (fbm blobs) at tunable directions.
// KEEP BYTE-IDENTICAL to cloud_sky.gdshader's reference copy.
vec3 nebula_color(vec3 d){
    vec3 acc = vec3(0.0);
    int n = int(P.neb_count.x);
    for (int i = 0; i < n; i++){
        vec3 nd = normalize(P.neb_dir_scale[i].xyz); float sc = P.neb_dir_scale[i].w;
        float prox = max(dot(d, nd), 0.0);
        float falloff = pow(prox, mix(170.0, 70.0, sc));
        // RIDGED filaments → sharp glowing tendrils with black voids between them (an emission nebula) —
        // a totally different generator from the soft fbm clouds.
        float r = ridge(d * mix(34.0, 18.0, sc) + vec3(float(i) * 9.1));
        float tendril = smoothstep(0.50, 0.95, r);
        float a = falloff * tendril * P.neb_color_dens[i].w;
        vec3 c = mix(P.neb_color_dens[i].rgb, vec3(1.0), falloff * falloff * 0.4);   // hotter/whiter core
        acc += c * a * (0.5 + 2.2 * falloff);
    }
    return acc;
}

void main(){
    ivec2 sz = imageSize(outTex);
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= sz.x || id.y >= sz.y) { return; }
    vec2 uv = (vec2(id) + 0.5) / vec2(sz);
    // texel → direction (full-sphere lat-long): az 0..2π, el -π/2..π/2. cloud_sky samples with the
    // matching inverse: u = az/2π, v = (asin(d.y)/(π/2)+1)/2.
    float az = uv.x * 2.0 * PI;
    float el = (uv.y * 2.0 - 1.0) * (0.5 * PI);
    float ce = cos(el);
    vec3 dir = vec3(ce * cos(az), sin(el), ce * sin(az));

    vec3 c = galaxy_color(dir) + nebula_color(dir);
    imageStore(outTex, id, vec4(c, 1.0));
}
