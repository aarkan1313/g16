#[compute]
#version 450

// Thermal/talus slump, two phases per iteration (driven by `phase`):
//   phase 0 = accumulate: each cell reads neighbours (read-only) and atomicAdds
//             its material moves into a separate delta buffer (race-free).
//   phase 1 = apply: heights += delta; delta = 0 (each cell touches only itself).
// Matches ThermalErosionCpu (delta accumulated over the whole grid, then applied).

layout(local_size_x = 64) in;

layout(set = 0, binding = 0, std430) restrict buffer Heights { int heights[]; };
layout(set = 0, binding = 1, std430) restrict buffer Delta   { int delta[]; };
layout(set = 0, binding = 2, std430) restrict readonly buffer Params {
    int width; int height; int phase;
    float tanA; float strength; float cellSize; float scale;
};

const int dxs[8] = int[8](-1, 0, 1, -1, 1, -1, 0, 1);
const int dys[8] = int[8](-1, -1, -1, 0, 0, 1, 1, 1);

float getH(int x, int y) { return float(heights[y * width + x]) / scale; }

void main() {
    // 2D dispatch remap: the grid is dispatched as (groupsX, groupsY) so n/64 can exceed Vulkan's
    // 65535 single-dimension group limit (needed at native 4 m). Linearise back to the cell index.
    uint id = gl_GlobalInvocationID.y * (gl_NumWorkGroups.x * gl_WorkGroupSize.x) + gl_GlobalInvocationID.x;
    if (id >= uint(width * height)) return;

    if (phase == 1) { heights[id] += delta[id]; delta[id] = 0; return; }

    int x = int(id) % width;
    int y = int(id) / width;
    if (x < 1 || y < 1 || x >= width - 1 || y >= height - 1) return;

    float h = getH(x, y);
    float ex[8];
    float sum = 0.0, maxEx = 0.0;
    for (int k = 0; k < 8; k++) {
        float dist = (dxs[k] != 0 && dys[k] != 0) ? cellSize * 1.41421 : cellSize;
        float talus = tanA * dist;
        float diff = h - getH(x + dxs[k], y + dys[k]); // >0: neighbour lower
        float e = diff - talus;
        if (e > 0.0) { ex[k] = e; sum += e; maxEx = max(maxEx, e); }
        else ex[k] = 0.0;
    }
    if (sum <= 0.0) return;

    float move = strength * 0.5 * maxEx;
    for (int k = 0; k < 8; k++) {
        if (ex[k] <= 0.0) continue;
        float portion = move * (ex[k] / sum);
        int pf = int(round(portion * scale));
        atomicAdd(delta[y * width + x], -pf);
        atomicAdd(delta[(y + dys[k]) * width + (x + dxs[k])], pf);
    }
}
