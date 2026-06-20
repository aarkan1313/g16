#[compute]
#version 450

// GM2: derive a tileable per-material HEIGHT field from its NORMAL map by Jacobi-relaxed
// Poisson integration. The normal is the derivative of height: slope = (-nx/nz, -ny/nz),
// so h solves ∇²h = ∂x(slope.x) + ∂y(slope.y). Jacobi converges mid/high-freq relief fast
// (the part POM + the heightblend interlock need); low-freq drift is removed by the CPU
// min/max normalize on readback. Run K times, ping-ponging HIn/HOut. Tileable: wrap edges.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly  buffer NormalIn { vec4 nrm[]; };  // raw normal rgb in [0,1]
layout(set = 0, binding = 1, std430) restrict readonly  buffer HIn      { float hin[]; };
layout(set = 0, binding = 2, std430) restrict writeonly buffer HOut     { float hout[]; };
layout(set = 0, binding = 3, std430) restrict readonly  buffer ParamsBuf {
    uint  res;
    float amp;        // relief gain on the divergence (normalize makes this mostly cosmetic)
    uint  flip_y;     // 1 = flip green channel (DirectX↔OpenGL normal convention)
    uint  invert;     // 1 = negate the field (peaks↔valleys) if relief reads inverted
};

int wrap(int v, int n){ return (v % n + n) % n; }
int idx(int x, int y){ return wrap(y,int(res))*int(res) + wrap(x,int(res)); }

// tangent-space slope (∂h/∂x, ∂h/∂y) at a texel from its normal.
vec2 slope_at(int x, int y){
    vec3 n = nrm[idx(x,y)].rgb * 2.0 - 1.0;        // decode [0,1]→[-1,1]
    if (flip_y == 1u) n.y = -n.y;
    float nz = max(abs(n.z), 0.05);                // avoid div-by-0 on steep texels
    return vec2(-n.x / nz, -n.y / nz);
}

void main(){
    ivec2 id = ivec2(gl_GlobalInvocationID.xy);
    if (id.x >= int(res) || id.y >= int(res)) return;
    int x = id.x, y = id.y;

    // divergence of the slope field (central differences, wrapped) → the Poisson RHS.
    vec2 sR = slope_at(x+1, y), sL = slope_at(x-1, y);
    vec2 sU = slope_at(x, y+1), sD = slope_at(x, y-1);
    float f = ((sR.x - sL.x) + (sU.y - sD.y)) * 0.5 * amp;
    if (invert == 1u) f = -f;

    // one Jacobi sweep:  h = (h_R + h_L + h_U + h_D - f) / 4
    float h = (hin[idx(x+1,y)] + hin[idx(x-1,y)] + hin[idx(x,y+1)] + hin[idx(x,y-1)] - f) * 0.25;
    hout[y*int(res) + x] = h;
}
