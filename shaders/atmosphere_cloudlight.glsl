#[compute]
#version 450

// AT-3 cloud-light 3-texel extractor (#7 perf). The cloud raymarch needs exactly 3 colors from the
// physical sky LUTs each time the sun moves: sky-view ZENITH, sky-view HORIZON-toward-sun, and sun
// TRANSMITTANCE. Reading them on the CPU meant TextureGetData on the WHOLE sky LUT (192x108) + the
// WHOLE transmittance LUT (256x64) = ~297 KB GPU->CPU just to pull 3 texels. This compute fetches the
// 3 texels (coords computed CPU-side, passed in) and writes them to a tiny 3x1 image, so the readback
// transfers 3 texels (~24 bytes) instead. Pure data extraction — no atmosphere math, look-neutral.
// out texel 0 = zenith, 1 = horizon-toward-sun, 2 = sun transmittance.

layout(local_size_x = 4, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0, rgba16f) uniform restrict writeonly image2D outTex;  // 3x1
layout(set = 0, binding = 1) uniform sampler2D skyLUT;     // sky-view (sampled via texelFetch)
layout(set = 0, binding = 2) uniform sampler2D transLUT;   // transmittance
// Three texel coordinates (x,y) packed as ivec-in-vec4: .xy = sky zenith, .zw = sky horizon; trans .xy.
layout(set = 0, binding = 3, std430) restrict readonly buffer Coords {
    ivec4 sky_coords;     // x,y = zenith texel ; z,w = horizon texel
    ivec4 trans_coords;   // x,y = sun-transmittance texel (z,w unused)
} C;

void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i == 0u) {
        imageStore(outTex, ivec2(0, 0), texelFetch(skyLUT, ivec2(C.sky_coords.x, C.sky_coords.y), 0));
    } else if (i == 1u) {
        imageStore(outTex, ivec2(1, 0), texelFetch(skyLUT, ivec2(C.sky_coords.z, C.sky_coords.w), 0));
    } else if (i == 2u) {
        imageStore(outTex, ivec2(2, 0), texelFetch(transLUT, ivec2(C.trans_coords.x, C.trans_coords.y), 0));
    }
}
