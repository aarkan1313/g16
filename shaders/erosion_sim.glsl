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
layout(set = 0, binding = 4, std430) restrict buffer Flux     { vec4 flux[]; };   // water pipes L,R,T,B
layout(set = 0, binding = 5, std430) restrict buffer Velocity { vec2 vel[]; };
layout(set = 0, binding = 6, std430) restrict buffer DHeight  { float dh[]; };    // erode height delta (race-free)
layout(set = 0, binding = 7, std430) restrict buffer TFlux    { vec4 tflux[]; };  // thermal slump outflow L,R,T,B
layout(set = 0, binding = 8, std430) restrict buffer Sed2     { float s2[]; };    // transport double-buffer

// push_constant selects the phase (so one shader = all phases).
layout(push_constant, std430) uniform Push { int phase; } pc;

// RACE-FREENESS RULE: a phase that READS neighbor h/s must NOT write h/s in the same pass. Neighbor-reading
// mutations are split into a "compute flux/delta" pass (read-only on h/s) + a GATHER "apply" pass (writes
// h/s[i] only from its OWN index + neighbors' already-computed flux). This mirrors the water phases 1→2.

int idx(int x, int z) { return z * P.res + x; }
float H(int x, int z) { x = clamp(x, 0, P.res-1); z = clamp(z, 0, P.res-1); return h[idx(x,z)] + w[idx(x,z)]; }

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = idx(c.x, c.y);

    if (pc.phase == 1) {                      // FLUX: water virtual pipes to 4 neighbors (READ-ONLY on h/w)
        // Rain is added in phase 2 (the gather pass that owns the w[i] write). Phase 1 must NOT write w[i] —
        // neighbors read w[] via H() in this same dispatch, so writing here is a read-while-write race.
        float hc = h[i] + w[i] + P.rain * P.dt;   // local rain-adjusted surface (matches phase 2's increment)
        vec4 f = flux[i];
        float k = P.dt * P.gravity / P.cell_size;
        f.x = max(0.0, f.x + k * (hc - H(c.x-1, c.y)));
        f.y = max(0.0, f.y + k * (hc - H(c.x+1, c.y)));
        f.z = max(0.0, f.z + k * (hc - H(c.x, c.y-1)));
        f.w = max(0.0, f.w + k * (hc - H(c.x, c.y+1)));
        float tot = f.x + f.y + f.z + f.w;
        float avail = (w[i] + P.rain * P.dt) * P.cell_size * P.cell_size / max(P.dt, 1e-6);
        float scale = (tot > 1e-6) ? min(1.0, avail / tot) : 0.0;
        flux[i] = f * scale;                  // writes OWN flux only — race-free
    }
    else if (pc.phase == 2) {                 // WATER: gather net flux → own water + velocity (race-free gather)
        vec4 fo = flux[i];
        float inL = (c.x > 0)        ? flux[idx(c.x-1, c.y)].y : 0.0;
        float inR = (c.x < P.res-1)  ? flux[idx(c.x+1, c.y)].x : 0.0;
        float inT = (c.y > 0)        ? flux[idx(c.x, c.y-1)].w : 0.0;
        float inB = (c.y < P.res-1)  ? flux[idx(c.x, c.y+1)].z : 0.0;
        float dV = (inL + inR + inT + inB - (fo.x + fo.y + fo.z + fo.w)) * P.dt;
        float wn = max(0.0, w[i] + P.rain * P.dt + dV / (P.cell_size * P.cell_size));   // rain added here (race-free)
        vel[i] = vec2((inL - fo.x + fo.y - inR), (inT - fo.z + fo.w - inB)) * 0.5;
        w[i] = wn * (1.0 - P.evaporate * P.dt);   // writes OWN water only
    }
    else if (pc.phase == 3) {                 // ERODE/DEPOSIT: read h(neighbors)+vel+s, write OWN dh + OWN s
        float hl = h[idx(max(c.x-1,0), c.y)], hr = h[idx(min(c.x+1,P.res-1), c.y)];
        float ht = h[idx(c.x, max(c.y-1,0))], hb = h[idx(c.x, min(c.y+1,P.res-1))];
        float slope = max(P.min_tilt, length(vec2(hr-hl, hb-ht)) / (2.0 * P.cell_size));
        float speed = length(vel[i]);
        // capacity weighted by WATER (flow accumulation): dry cells barely erode → channels gather, no rill-everywhere
        float cap = P.capacity * slope * speed * clamp(w[i] * 40.0, 0.0, 1.0);
        float sed = s[i];
        if (cap > sed) {
            float amt = min(P.erode * (cap - sed) * P.dt, P.max_erode);
            dh[i] = -amt; s[i] = sed + amt;        // dh applied in phase 5 (no neighbor reads h[i] mid-erode)
        } else {
            float amt = P.deposit * (sed - cap) * P.dt;
            dh[i] = amt;  s[i] = sed - amt;
        }
    }
    else if (pc.phase == 4) {                 // THERMAL FLUX: read h(neighbors), write OWN slump outflow (read-only h)
        float hc = h[i];
        vec4 t = vec4(0.0);                    // L,R,T,B amounts to send DOWN to each lower neighbor
        float dL = hc - h[idx(max(c.x-1,0), c.y)];
        float dR = hc - h[idx(min(c.x+1,P.res-1), c.y)];
        float dT = hc - h[idx(c.x, max(c.y-1,0))];
        float dB = hc - h[idx(c.x, min(c.y+1,P.res-1))];
        float thr = P.talus_angle * P.cell_size;
        t.x = max(0.0, dL - thr); t.y = max(0.0, dR - thr);
        t.z = max(0.0, dT - thr); t.w = max(0.0, dB - thr);
        float tot = t.x + t.y + t.z + t.w;
        // send at most talus_rate of the excess this step (talus_rate < 1 keeps it stable / non-inverting)
        float sc = (tot > 1e-6) ? min(1.0, P.talus_rate) : 0.0;
        tflux[i] = t * sc;                    // writes OWN tflux only — race-free
    }
    else if (pc.phase == 5) {                 // APPLY: own erode delta + GATHER thermal (conservative slump)
        vec4 fo = tflux[i];                                    // my outflow
        float inL = (c.x > 0)        ? tflux[idx(c.x-1, c.y)].y : 0.0;   // left neighbor's R-send
        float inR = (c.x < P.res-1)  ? tflux[idx(c.x+1, c.y)].x : 0.0;
        float inT = (c.y > 0)        ? tflux[idx(c.x, c.y-1)].w : 0.0;
        float inB = (c.y < P.res-1)  ? tflux[idx(c.x, c.y+1)].z : 0.0;
        float thermal = (inL + inR + inT + inB) - (fo.x + fo.y + fo.z + fo.w);   // conserved: in - out
        h[i] = h[i] + dh[i] + thermal;        // writes OWN h only — race-free (dh + thermal both index i)
        dh[i] = 0.0;
    }
    else if (pc.phase == 6) {                 // TRANSPORT: backtrace read s, write s2 (double-buffer, race-free)
        vec2 p = vec2(c) - vel[i] * P.dt;
        p = clamp(p, vec2(0.0), vec2(float(P.res-1)));
        ivec2 b = ivec2(floor(p)); vec2 fr = fract(p);
        int bx1 = min(b.x+1, P.res-1), bz1 = min(b.y+1, P.res-1);
        float a00 = s[idx(b.x, b.y)],  a10 = s[idx(bx1, b.y)];
        float a01 = s[idx(b.x, bz1)],  a11 = s[idx(bx1, bz1)];
        s2[i] = mix(mix(a00, a10, fr.x), mix(a01, a11, fr.x), fr.y);
    }
    else if (pc.phase == 7) {                 // SWAP: s = s2 (commit the advected sediment)
        s[i] = s2[i];
    }
}
