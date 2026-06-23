#[compute]
#version 450
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Params (std430, matches ErosionParams.Pack). Slot 0 = res (int bits).
layout(set = 0, binding = 0, std430) restrict buffer Params {
    int res; float cell_size; float dt; float rain;
    float evaporate; float gravity; float capacity; float erode;
    float deposit; float max_erode; float talus_angle; float talus_rate;
    float min_tilt; float _p0; float _p1; float _p2;
} P;

layout(set = 0, binding = 1, std430) restrict buffer Height   { float h[]; };
layout(set = 0, binding = 2, std430) restrict buffer Water    { float w[]; };
layout(set = 0, binding = 3, std430) restrict buffer Sediment { float s[]; };
layout(set = 0, binding = 4, std430) restrict buffer Flux     { vec4 flux[]; }; // L,R,T,B
layout(set = 0, binding = 5, std430) restrict buffer Velocity { vec2 vel[]; };

// push_constant selects the phase (so one shader = all phases).
layout(push_constant, std430) uniform Push { int phase; } pc;

int idx(int x, int z) { return z * P.res + x; }

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = idx(c.x, c.y);
    // phase bodies implemented in T2 (water), T3 (erode/deposit), T4 (transport/thermal).
}
