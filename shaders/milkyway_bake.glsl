#[compute]
#version 450

// Milky-Way structure bake (sky perf). The Milky Way in cloud_sky.gdshader costs THREE 5-octave fbm3
// (~15 3D-noise taps) PER SKY PIXEL every night frame (~1.3 ms). But it's a STATIC function of the
// (un-rotated) view direction + mw_tilt/mw_width — so bake band*structure here ONCE (and on a mw-param
// change) into a lat-long texture; cloud_sky then does one sample + the cheap celestial rotation at
// runtime. The noise functions are copied VERBATIM from cloud_sky.gdshader so the bake matches the
// procedural pixel-for-pixel (the bake is pixel-diff-verified look-neutral, then default-on).
// R = band*structure (the expensive part); color + mw_brightness stay live in the sky shader.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;
layout(set = 0, binding = 1, std430) restrict readonly buffer Params {
    vec4 tilt_width;   // x = mw_tilt, y = mw_width
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
    vec3 sr = vec3(ce * cos(az), sin(el), ce * sin(az));

    float mw_tilt = P.tilt_width.x, mw_width = P.tilt_width.y;
    // Milky Way (band*structure) — math copied from cloud_sky.gdshader stars_layer().
    vec3 planeN = normalize(vec3(sin(mw_tilt), 0.35, cos(mw_tilt)));
    float wob = mw_width * (0.6 + 0.8 * fbm3(sr * 2.0 + vec3(11.0)));
    float band = smoothstep(wob, 0.0, abs(dot(sr, planeN)));
    float clouds = fbm3(sr * 6.0);
    float lanes = fbm3(sr * 16.0 + vec3(5.0));
    float structure = smoothstep(0.32, 0.72, clouds) * mix(0.5, 1.0, lanes);
    float mwStruct = band * structure;
    imageStore(outTex, id, vec4(mwStruct, band, structure, 1.0));
}
