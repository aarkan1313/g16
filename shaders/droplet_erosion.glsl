#[compute]
#version 450

// One invocation per droplet. Many droplets run concurrently on a SHARED
// height buffer. The old "millions of cuts" sawtooth was plain read-modify-write
// racing; here every terrain write is an atomicAdd on an INTEGER fixed-point
// height buffer, so write-write is always consistent (the race fix E1 lacked).

layout(local_size_x = 64) in;

layout(set = 0, binding = 0, std430) restrict buffer Heights { int heights[]; };

layout(set = 0, binding = 1, std430) restrict readonly buffer Params {
    int width; int height; int numDroplets; int maxLifetime;
    float inertia; float capacityFactor; float minSlope; float erosionRate;
    float depositionRate; float evaporation; float gravity; float initialWater;
    float initialSpeed; int brushCount; int seed; float scale;
};

layout(set = 0, binding = 2, std430) restrict readonly buffer Brush { vec4 brush[]; }; // x=dx y=dy z=weight

float getH(int x, int y) { return float(heights[y * width + x]) / scale; }
void addH(int x, int y, float v) {
    if (x < 0 || y < 0 || x >= width || y >= height) return;
    atomicAdd(heights[y * width + x], int(round(v * scale)));
}

uint hash(uint s) { s ^= s >> 16; s *= 0x7feb352du; s ^= s >> 15; s *= 0x846ca68bu; s ^= s >> 16; return s; }
float rnd(uint s) { return float(hash(s) & 0xffffffu) / float(0xffffff); }

void hg(float px, float py, out float h, out float gx, out float gy) {
    int x0 = min(int(px), width - 2);
    int y0 = min(int(py), height - 2);
    float u = px - float(x0), v = py - float(y0);
    float nw = getH(x0, y0), ne = getH(x0 + 1, y0), sw = getH(x0, y0 + 1), se = getH(x0 + 1, y0 + 1);
    gx = (ne - nw) * (1.0 - v) + (se - sw) * v;
    gy = (sw - nw) * (1.0 - u) + (se - ne) * u;
    h = nw * (1.0 - u) * (1.0 - v) + ne * u * (1.0 - v) + sw * (1.0 - u) * v + se * u * v;
}

void depositBilinear(int x, int y, float cx, float cy, float amt) {
    addH(x, y, amt * (1.0 - cx) * (1.0 - cy));
    addH(x + 1, y, amt * cx * (1.0 - cy));
    addH(x, y + 1, amt * (1.0 - cx) * cy);
    addH(x + 1, y + 1, amt * cx * cy);
}

void main() {
    uint id = gl_GlobalInvocationID.x;
    if (id >= uint(numDroplets)) return;

    uint s = hash(id ^ uint(seed) * 747796405u);
    float px = rnd(s) * float(width - 1);  s = hash(s);
    float py = rnd(s) * float(height - 1); s = hash(s);

    float dirX = 0.0, dirY = 0.0, speed = initialSpeed, water = initialWater, sediment = 0.0;
    int cx = int(px), cy = int(py); float ox = 0.0, oy = 0.0;

    for (int life = 0; life < maxLifetime; life++) {
        cx = int(px); cy = int(py); ox = px - float(cx); oy = py - float(cy);
        float oldH, gx, gy; hg(px, py, oldH, gx, gy);

        dirX = dirX * inertia - gx * (1.0 - inertia);
        dirY = dirY * inertia - gy * (1.0 - inertia);
        float len = sqrt(dirX * dirX + dirY * dirY);
        if (len < 1e-6) break;
        dirX /= len; dirY /= len;
        px += dirX; py += dirY;
        if (px < 0.0 || px >= float(width - 1) || py < 0.0 || py >= float(height - 1)) break;

        float newH, ngx, ngy; hg(px, py, newH, ngx, ngy);
        float dH = newH - oldH;
        float capacity = max(-dH, minSlope) * speed * water * capacityFactor;

        if (sediment > capacity || dH > 0.0) {
            float deposit = (dH > 0.0) ? min(dH, sediment) : (sediment - capacity) * depositionRate;
            sediment -= deposit;
            depositBilinear(cx, cy, ox, oy, deposit);
        } else {
            float erode = min((capacity - sediment) * erosionRate, -dH);
            sediment += erode;
            for (int k = 0; k < brushCount; k++)
                addH(cx + int(brush[k].x), cy + int(brush[k].y), -erode * brush[k].z);
        }

        speed = sqrt(max(0.0, speed * speed + dH * (-gravity)));
        water *= (1.0 - evaporation);
        if (water < 1e-4) break;
    }

    if (sediment > 0.0) depositBilinear(cx, cy, ox, oy, sediment);
}
