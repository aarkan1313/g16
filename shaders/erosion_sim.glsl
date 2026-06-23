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
float H(int x, int z) { x = clamp(x, 0, P.res-1); z = clamp(z, 0, P.res-1); return h[idx(x,z)] + w[idx(x,z)]; }

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = idx(c.x, c.y);

    if (pc.phase == 1) {                      // FLUX: virtual pipes to 4 neighbors
        w[i] += P.rain * P.dt;                // rain
        float hc = h[i] + w[i];
        vec4 f = flux[i];
        float k = P.dt * P.gravity / P.cell_size;
        f.x = max(0.0, f.x + k * (hc - H(c.x-1, c.y)));   // L
        f.y = max(0.0, f.y + k * (hc - H(c.x+1, c.y)));   // R
        f.z = max(0.0, f.z + k * (hc - H(c.x, c.y-1)));   // T
        f.w = max(0.0, f.w + k * (hc - H(c.x, c.y+1)));   // B
        float tot = f.x + f.y + f.z + f.w;
        // scale so we never drain more water than this cell holds (stability clamp)
        float avail = w[i] * P.cell_size * P.cell_size / max(P.dt, 1e-6);
        float scale = (tot > 1e-6) ? min(1.0, avail / tot) : 0.0;
        flux[i] = f * scale;
    }
    else if (pc.phase == 2) {                 // WATER: apply net flux + velocity + evaporation
        vec4 fo = flux[i];
        float inL = (c.x > 0)        ? flux[idx(c.x-1, c.y)].y : 0.0;
        float inR = (c.x < P.res-1)  ? flux[idx(c.x+1, c.y)].x : 0.0;
        float inT = (c.y > 0)        ? flux[idx(c.x, c.y-1)].w : 0.0;
        float inB = (c.y < P.res-1)  ? flux[idx(c.x, c.y+1)].z : 0.0;
        float dV = (inL + inR + inT + inB - (fo.x + fo.y + fo.z + fo.w)) * P.dt;
        float wn = max(0.0, w[i] + dV / (P.cell_size * P.cell_size));
        // velocity from horizontal throughput (x: +R-L, z: +B-T net flow)
        vel[i] = vec2((inL - fo.x + fo.y - inR), (inT - fo.z + fo.w - inB)) * 0.5;
        wn *= (1.0 - P.evaporate * P.dt);     // converge
        w[i] = wn;
    }
    else if (pc.phase == 3) {                 // ERODE/DEPOSIT: from the shared flow state
        float hl = h[idx(max(c.x-1,0), c.y)], hr = h[idx(min(c.x+1,P.res-1), c.y)];
        float ht = h[idx(c.x, max(c.y-1,0))], hb = h[idx(c.x, min(c.y+1,P.res-1))];
        float slope = max(P.min_tilt, length(vec2(hr-hl, hb-ht)) / (2.0 * P.cell_size));
        float speed = length(vel[i]);
        float cap = P.capacity * slope * speed;            // transport capacity
        float sed = s[i];
        if (cap > sed) {                                   // erode bedrock into suspension
            float amt = min(P.erode * (cap - sed) * P.dt, P.max_erode);   // hard cap = anti-overshoot
            h[i] -= amt; s[i] = sed + amt;
        } else {                                            // deposit
            float amt = P.deposit * (sed - cap) * P.dt;
            h[i] += amt; s[i] = sed - amt;
        }
    }
}
