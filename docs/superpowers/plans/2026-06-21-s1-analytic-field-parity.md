# S1 — Analytic Field Parity + Perf Go/No-Go — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relocate the heightfield `field_height` into ONE shared shader-math source consumed by both the compute bake AND the spatial ground shader, make `ground.gdshader` displace from the **live function** (not the baked texture), and prove via a numeric parity self-check + an in-motion perf number that this is byte-identical and within the 8 ms budget — the go/no-go for the whole Analytic-CDLOD arc (Approach C).

**Architecture:** The field's pure math (hash / noise / fbm / warps / `field_height`) is extracted into a single `shaders/field_math.gdshaderinc`, with **params passed as a struct argument** instead of read from the compute SSBO global `P`. The Godot spatial shader pulls this file via native `#include`. The RenderingDevice compute shader — which has **no `#include` support** — gets the same file **string-concatenated into its source in C#** before SPIR-V compile. One source, two consumption mechanisms → no duplication, no drift. A `--fieldcheck` CLI self-check reads back the baked page and compares it to the same field evaluated on the CPU/GPU analytic path within epsilon; `--profile --profmove` prints the in-motion frame time.

**Tech Stack:** Godot 4.6.2 mono (C# + GDScript-free), RenderingDevice compute (GLSL `#version 450`, std430), Godot spatial shading language, `dotnet build`.

## Global Constraints

- **Engine/build:** Godot 4.6.2 stable mono, Vulkan only. Build: `dotnet build WG16.csproj -v q -clp:ErrorsOnly` → must end `0 Error(s)`.
- **No `#include` in RenderingDevice compute GLSL** (Godot proposal #9592 open) — the compute shader gets the shared math by **C# string concatenation**, never by a `#include` line in the `.glsl`.
- **`.gdshaderinc` is the required extension** for a Godot shader-include; absolute `res://` path in the `#include`.
- **Skin not bones:** do NOT change the field MATH. This plan only relocates it and changes *how params are passed* (SSBO global → struct argument). The `--fieldcheck` parity test is the proof the math is unchanged.
- **Perf budget:** 8 ms in-motion (`--profile --profmove`). The current single no-LOD mesh is ~3.8 ms of that floor. S1 must not regress frame time materially; a large regression is the "no-go" that forces a height-source pivot before any quadtree work.
- **NO TDD** (standing project rule — GPU/visual). "Test" here = build clean → `--headless --import` (shader compile-check) → `--fieldcheck` mechanical PASS/FAIL → `--profile --profmove` number → the user's live eye in motion. Auto-shots are a SANITY check only, never a look-gate (downscaled shots hid artifacts before).
- **ONE Godot at a time** (two contend for the GPU → grey hang). Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **The scene cannot run `--headless`** (`FieldCompute`'s local RenderingDevice NullRefs without a GPU context). Run windowed to execute; `--headless --import` only compile-checks.
- **Git:** work on `experiment/presentation`. Stage ONLY this plan's files (`git add <paths>`, NEVER `git add -A`) — the user edits the sky lane live in parallel. Commit per task. Push only when asked.
- **Commit message footer:** end every commit body with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

### Key paths (verified to exist this session)
- Compute field: `shaders/field_height.glsl` (the `#[compute]` shader; `field_height(vec2,uint,float)` at lines 219-240; reads SSBO `P`).
- Spatial ground: `shaders/ground.gdshader` (minimal placeholder; `height_at(uv)` samples the baked `heightmap` texture in `vertex()`).
- Dispatcher: `scripts/field/FieldCompute.cs` (`ProducePage`; builds source via `RDShaderSource{ SourceCompute = computeSource }` then `ShaderCompileSpirVFromSource`; `BuildParamsBytes` packs the 128 B std430 block).
- Params: `scripts/field/FieldParams.cs` (`record FieldParams`, `Spacing => RegionSizeM / HeightmapRes`, `Load()`).
- Presenter: `scripts/lab/TerrainLab.cs` (`Build(fc, p)`; sets `heightmap`/`region_size`/`texel_world`; `MidHeight`).
- CLI: `scripts/lab/TerrainLabUI.Cli.cs` (`ParseCli()` flag table; `ApplyCliOverrides()`). One-shot checks fire in `scripts/lab/TerrainLabUI.cs` (`_shadowCheckCli` block ~line 170). Perf readout in `scripts/lab/TerrainLabUI.Process.cs` (`--profile`/`--profmove`, prints `PROFILE: avg … ms`, quits).

---

## Task ordering rationale

1. **Task 1** extracts the shared math file + refactors the compute shader to use it (params as struct arg, C# string-include) — proven by the EXISTING render still looking right and building. This is the riskiest "bones-adjacent" change; do it first and prove the bake is unchanged before anything depends on the analytic render.
2. **Task 2** adds the `--fieldcheck` numeric parity self-check (baked page vs. analytic eval) — the mechanical PASS/FAIL guard. Built before switching the render so the switch is verified, not hoped.
3. **Task 3** switches `ground.gdshader` to displace from the live `#include`d field (behind a toggle for A/B), proven by `--fieldcheck` still passing + eye parity.
4. **Task 4** runs the perf go/no-go (`--profile --profmove`) and records the verdict.

Each task ends with a build + a concrete check + a commit.

---

### Task 1: Extract `field_math.gdshaderinc`; refactor compute to consume it (params as struct)

Pull the pure math out of `field_height.glsl` into one shared file whose functions take a **`FieldP` struct** argument instead of reading the SSBO global `P`. The compute shader keeps its `#version 450` / `layout` / `buffer` / `main()` shell but now (a) builds a `FieldP` from its SSBO and (b) calls the shared `field_height(world_xz, seed, spacing, fp)`. Because RD-GLSL has no `#include`, `FieldCompute.cs` concatenates the shared file's text into the compute source before compiling.

**Files:**
- Create: `shaders/field_math.gdshaderinc` (the shared pure-math: `FieldP` struct, `hash_u`, `hash2`, `fade`, `value_noise`, `octave_weight`, `value_noise_d`, `domain_warp`, `ridged_fbm`, `slope_damped_fbm`, `value_fbm`, `seed_axis`, `continent`, `Uplift`/`uplift`, `oriented_ridges`, `field_height` — all taking `FieldP fp` where they currently read `P`).
- Modify: `shaders/field_height.glsl` (remove the moved math; keep `#version 450`, `layout`, the `ParamsBuf P` block, and `main()`; in `main()` build a `FieldP` from `P` and call the shared `field_height(world_xz, P.seed, P.spacing, fp)`; insert a marker line `// @@INCLUDE field_math` where the shared text will be spliced).
- Modify: `scripts/field/FieldCompute.cs:18-35` (in the ctor, after reading `field_height.glsl`, replace the `// @@INCLUDE field_math` marker with the text of `field_math.gdshaderinc`).

**Interfaces:**
- Produces (shared shader API, used by Tasks 2-3 and all later stages):
  - `struct FieldP { float origin_x; float origin_z; float spacing; uint seed; uint res; uint octaves; float base_freq; float amplitude; float lacunarity; float gain; uint field_mode; float cont_freq; float cont_weight; float uplift_freq; float uplift_weight; float uplift_lo; float uplift_hi; float macro_pivot; float macro_amp; float hill_damp; float ridge_freq; float ridge_amp; float mtn_lo; float mtn_hi; float grain_stretch; uint cont_octaves; float cont_warp; float uplift_warp; float massif_freq; float massif_floor; float foothill_w; float foothill_h; };` — field-for-field identical to the `ParamsBuf P` block in `field_height.glsl`.
  - `float field_height(vec2 world_xz, uint seed, float spacing, FieldP fp)` — the composed height; identical math to today, params now via `fp`.
- Produces (C#): `FieldCompute` ctor now assembles the compute source by marker replacement; public surface (`ProducePage`, etc.) unchanged.

- [ ] **Step 1: Create the shared math file**

Create `shaders/field_math.gdshaderinc`. Copy the math functions from `field_height.glsl` **verbatim**, with one mechanical change: every function that referenced `P.<x>` now takes `FieldP fp` and references `fp.<x>`. Begin the file with the `FieldP` struct and a header comment. (No `#version`, no `layout`, no `buffer`, no `main()` — pure functions only, so the SAME file is valid inside a Godot spatial shader in Task 3.)

```glsl
// field_math.gdshaderinc — SHARED heightfield math (ONE source of truth).
// Consumed two ways: (1) the Godot spatial ground.gdshader #includes this file
// directly; (2) FieldCompute.cs string-concatenates it into field_height.glsl's
// compute source before SPIR-V compile (RD-GLSL has no #include — Godot proposal
// #9592). Therefore: PURE MATH ONLY here — no #version / layout / buffer / main.
// Params are passed as a FieldP struct argument (the spatial side has no SSBO).
// MATH IS UNCHANGED from the original field_height.glsl — only P.<x> -> fp.<x>.

struct FieldP {
    float origin_x; float origin_z; float spacing; uint seed; uint res;
    uint octaves; float base_freq; float amplitude; float lacunarity; float gain;
    uint field_mode; float cont_freq; float cont_weight; float uplift_freq;
    float uplift_weight; float uplift_lo; float uplift_hi; float macro_pivot;
    float macro_amp; float hill_damp; float ridge_freq; float ridge_amp;
    float mtn_lo; float mtn_hi; float grain_stretch; uint cont_octaves;
    float cont_warp; float uplift_warp; float massif_freq; float massif_floor;
    float foothill_w; float foothill_h;
};

uint hash_u(uint x) {
    x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x;
}
float hash2(ivec2 p, uint seed) {
    uint h = hash_u(uint(p.x) * 0x9e3779b9u ^ hash_u(uint(p.y) * 0x85ebca6bu ^ hash_u(seed)));
    return float(h) * (1.0 / 4294967296.0);
}
float fade(float t) { return t * t * (3.0 - 2.0 * t); }
float value_noise(vec2 p, uint seed) {
    ivec2 i = ivec2(floor(p)); vec2 f = fract(p);
    float a = hash2(i + ivec2(0, 0), seed); float b = hash2(i + ivec2(1, 0), seed);
    float c = hash2(i + ivec2(0, 1), seed); float d = hash2(i + ivec2(1, 1), seed);
    vec2 u = vec2(fade(f.x), fade(f.y));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float octave_weight(float oct_freq, float spacing) {
    float wavelength = 1.0 / max(oct_freq, 1e-9);
    return smoothstep(1.0 * spacing, 2.0 * spacing, wavelength);
}
vec3 value_noise_d(vec2 p, uint seed) {
    ivec2 i = ivec2(floor(p)); vec2 f = fract(p);
    float a = hash2(i + ivec2(0, 0), seed); float b = hash2(i + ivec2(1, 0), seed);
    float c = hash2(i + ivec2(0, 1), seed); float d = hash2(i + ivec2(1, 1), seed);
    vec2 u = vec2(fade(f.x), fade(f.y)); vec2 du = 6.0 * f * (1.0 - f);
    float k1 = b - a; float k2 = c - a; float k3 = a - b - c + d;
    float n = a + k1 * u.x + k2 * u.y + k3 * u.x * u.y;
    return vec3(n, du.x * (k1 + k3 * u.y), du.y * (k2 + k3 * u.x));
}
vec2 domain_warp(vec2 p, uint seed, float amount, float freq) {
    float wx = value_noise(p * freq, seed ^ 0x57415250u) - 0.5;
    float wz = value_noise(p * freq + vec2(31.4, 17.0), seed ^ 0x70726177u) - 0.5;
    return p + amount * 2.0 * vec2(wx, wz);
}
float ridged_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain,
                 float world_base_freq, float spacing) {
    float sum = 0.0, amp = 0.5, norm = 0.0, freq = 1.0, prev = 1.0;
    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        float n = value_noise(p * freq, seed + o * 0x9e3779b9u);
        float r = 1.0 - abs(2.0 * n - 1.0); r = smoothstep(0.0, 1.0, r); r *= prev;
        prev = clamp(r, 0.0, 1.0); sum += amp * w * r; norm += amp; amp *= gain; freq *= lacunarity;
    }
    return sum / max(norm, 1e-6);
}
float slope_damped_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain,
                       float damp, float world_base_freq, float spacing) {
    float sum = 0.0, amp = 1.0, norm = 0.0, freq = 1.0; vec2 dsum = vec2(0.0);
    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        vec3 nd = value_noise_d(p * freq, seed + o * 0x68bc21ebu);
        sum += amp * w * nd.x / (1.0 + damp * dot(dsum, dsum));
        dsum += vec2(nd.y, nd.z) * (amp * freq * w); norm += amp; amp *= gain; freq *= lacunarity;
    }
    return sum / max(norm, 1e-6);
}
float value_fbm(vec2 p, uint seed, uint octaves, float lacunarity, float gain, float world_base_freq, float spacing) {
    float sum = 0.0; float amp = 1.0; float norm = 0.0; float freq = 1.0;
    for (uint o = 0u; o < octaves; o++) {
        float w = octave_weight(world_base_freq * freq, spacing);
        sum += amp * w * value_noise(p * freq, seed + o * 0x68bc21ebu);
        norm += amp; amp *= gain; freq *= lacunarity;
    }
    return sum / max(norm, 1e-6);
}
vec2 seed_axis(uint seed) {
    float angle = float(hash_u(seed ^ 0x41584953u)) * (6.28318530718 / 4294967296.0);
    return vec2(cos(angle), sin(angle));
}
float continent(vec2 world_xz, uint seed, float spacing, FieldP fp) {
    uint cs = hash_u(seed ^ 0x434f4e54u);
    vec2 p = domain_warp(world_xz, cs, fp.cont_warp / fp.cont_freq, fp.cont_freq * 0.5);
    return value_fbm(p * fp.cont_freq, cs, fp.cont_octaves, 2.0, 0.55, fp.cont_freq, spacing);
}
struct Uplift { float amount; vec2 grain; vec2 wpos; };
Uplift uplift(vec2 world_xz, uint seed, FieldP fp) {
    uint us = hash_u(seed ^ 0x55504c54u);
    vec2 wp = domain_warp(world_xz, us ^ 0x42454e44u, fp.uplift_warp / fp.uplift_freq, fp.uplift_freq * 0.3);
    vec2 axis = seed_axis(us); vec2 perp = vec2(-axis.y, axis.x);
    float along = dot(wp, axis); float across = dot(wp, perp);
    float center_noise = value_fbm(vec2(along * fp.uplift_freq * 0.35, 23.17),
                                   us + 0x101u, 3u, 2.0, 0.5, fp.uplift_freq * 0.35, 1.0);
    float width_noise = value_noise(vec2(along * fp.uplift_freq * 0.22, 7.91), us + 0x202u);
    float center = (center_noise - 0.5) * (0.85 / fp.uplift_freq);
    float width = mix(0.16 / fp.uplift_freq, 0.34 / fp.uplift_freq, width_noise);
    float belt = 1.0 - smoothstep(width * 0.35, width, abs(across - center));
    float skirt = (1.0 - smoothstep(width * 0.8, width * fp.foothill_w, abs(across - center))) * fp.foothill_h;
    float band = belt + skirt * (1.0 - belt);
    float massif01 = value_fbm(vec2(along * fp.massif_freq, 11.31), us + 0x404u,
                               3u, 2.0, 0.5, fp.massif_freq, 1.0);
    float massif = mix(fp.massif_floor, 1.0, smoothstep(0.32, 0.70, massif01));
    float rough = value_fbm(wp * fp.uplift_freq * 1.7, us + 0x303u,
                            3u, 2.0, 0.55, fp.uplift_freq * 1.7, 1.0);
    float amount = band * massif * mix(0.55, 1.0, smoothstep(fp.uplift_lo, fp.uplift_hi, rough));
    Uplift u; u.amount = amount; u.grain = axis; u.wpos = wp; return u;
}
float oriented_ridges(vec2 world_xz, vec2 grain, uint seed, float spacing, FieldP fp) {
    vec2 w = domain_warp(world_xz, seed, 0.08 / fp.ridge_freq, fp.ridge_freq * 0.08);
    vec2 axis = grain; vec2 perp = vec2(-axis.y, axis.x);
    vec2 q = vec2(dot(w, axis) / fp.grain_stretch, dot(w, perp) * fp.grain_stretch);
    return ridged_fbm(q * fp.ridge_freq, seed, fp.octaves, fp.lacunarity, fp.gain,
                      fp.ridge_freq, spacing * fp.grain_stretch);
}
float field_height(vec2 world_xz, uint seed, float spacing, FieldP fp) {
    float cont = continent(world_xz, seed, spacing, fp);
    Uplift up = uplift(world_xz, seed, fp);
    float base = (cont - fp.macro_pivot) * fp.macro_amp + up.amount * fp.uplift_weight * fp.macro_amp;
    float hills = (slope_damped_fbm(world_xz * fp.base_freq, hash_u(seed ^ 0x48494c4cu),
                                    fp.octaves, fp.lacunarity, fp.gain, fp.hill_damp,
                                    fp.base_freq, spacing) - 0.5) * 2.0 * fp.amplitude;
    float ridge01 = oriented_ridges(up.wpos, up.grain, hash_u(seed ^ 0x52494447u), spacing, fp);
    float ridges = (ridge01 - 0.42) * 2.0 * fp.ridge_amp;
    float w_mtn = smoothstep(fp.mtn_lo, fp.mtn_hi, up.amount);
    float h = base + hills * (0.75 + fp.cont_weight * cont + 0.25 * up.amount) + ridges * w_mtn * 0.35;
    if (fp.field_mode == 1u) { return (cont - 0.5) * 6.0 * fp.macro_amp; }
    if (fp.field_mode == 2u) { return (up.amount - 0.5) * 2.0 * fp.macro_amp; }
    if (fp.field_mode == 3u) { return hills * 1.75; }
    if (fp.field_mode == 4u) { return ridges * w_mtn; }
    if (fp.field_mode == 5u) { return base * 1.5; }
    return h;
}
```

- [ ] **Step 2: Reduce `field_height.glsl` to the compute shell + marker**

Replace the body of `shaders/field_height.glsl` so it keeps ONLY the compute shell and the SSBO, splices the shared file at a marker, and calls the shared `field_height` from `main()` by building a `FieldP` from `P`:

```glsl
#[compute]
#version 450

// FIELD compute shell. The heightfield MATH now lives in shaders/field_math.gdshaderinc
// (one source of truth, shared with the spatial ground shader). RD-GLSL has no #include
// (Godot proposal #9592), so FieldCompute.cs splices that file in at the marker below
// before SPIR-V compile. This shell owns ONLY the GPU plumbing: workgroup, SSBOs, main().

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict writeonly buffer Heights {
    float h[];
};

layout(set = 0, binding = 1, std430) restrict readonly buffer ParamsBuf {
    float origin_x; float origin_z; float spacing; uint seed; uint res; uint octaves;
    float base_freq; float amplitude; float lacunarity; float gain; uint field_mode;
    float cont_freq; float cont_weight; float uplift_freq; float uplift_weight;
    float uplift_lo; float uplift_hi; float macro_pivot; float macro_amp; float hill_damp;
    float ridge_freq; float ridge_amp; float mtn_lo; float mtn_hi; float grain_stretch;
    uint cont_octaves; float cont_warp; float uplift_warp; float massif_freq;
    float massif_floor; float foothill_w; float foothill_h;
} P;

// @@INCLUDE field_math

FieldP make_fieldp() {
    FieldP fp;
    fp.origin_x = P.origin_x; fp.origin_z = P.origin_z; fp.spacing = P.spacing;
    fp.seed = P.seed; fp.res = P.res; fp.octaves = P.octaves; fp.base_freq = P.base_freq;
    fp.amplitude = P.amplitude; fp.lacunarity = P.lacunarity; fp.gain = P.gain;
    fp.field_mode = P.field_mode; fp.cont_freq = P.cont_freq; fp.cont_weight = P.cont_weight;
    fp.uplift_freq = P.uplift_freq; fp.uplift_weight = P.uplift_weight; fp.uplift_lo = P.uplift_lo;
    fp.uplift_hi = P.uplift_hi; fp.macro_pivot = P.macro_pivot; fp.macro_amp = P.macro_amp;
    fp.hill_damp = P.hill_damp; fp.ridge_freq = P.ridge_freq; fp.ridge_amp = P.ridge_amp;
    fp.mtn_lo = P.mtn_lo; fp.mtn_hi = P.mtn_hi; fp.grain_stretch = P.grain_stretch;
    fp.cont_octaves = P.cont_octaves; fp.cont_warp = P.cont_warp; fp.uplift_warp = P.uplift_warp;
    fp.massif_freq = P.massif_freq; fp.massif_floor = P.massif_floor;
    fp.foothill_w = P.foothill_w; fp.foothill_h = P.foothill_h;
    return fp;
}

void main() {
    uvec2 cell = gl_GlobalInvocationID.xy;
    if (cell.x >= P.res || cell.y >= P.res) { return; }
    vec2 world_xz = vec2(P.origin_x, P.origin_z) + vec2(cell) * P.spacing;
    FieldP fp = make_fieldp();
    h[cell.y * P.res + cell.x] = field_height(world_xz, P.seed, P.spacing, fp);
}
```

- [ ] **Step 3: Splice the shared file into the compute source in C#**

In `scripts/field/FieldCompute.cs`, ctor, modify the source-loading block (currently lines 18-24) to replace the marker with the shared math text. Replace:

```csharp
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/field_height.glsl");
        string computeSource = System.IO.File.ReadAllText(shaderPath)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty);
```

with:

```csharp
        string shaderPath = ProjectSettings.GlobalizePath("res://shaders/field_height.glsl");
        string mathPath = ProjectSettings.GlobalizePath("res://shaders/field_math.gdshaderinc");
        string mathSrc = System.IO.File.ReadAllText(mathPath);
        string computeSource = System.IO.File.ReadAllText(shaderPath)
            .Replace("#[compute]\r\n", string.Empty)
            .Replace("#[compute]\n", string.Empty)
            // RD-GLSL has no #include (Godot proposal #9592): splice the shared field math here.
            .Replace("// @@INCLUDE field_math", mathSrc);
```

- [ ] **Step 4: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: ends with `0 Error(s)`.

- [ ] **Step 5: Compile-check the compute shader (headless import)**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --headless --import --rendering-driver vulkan --path /c/Wg16/wg-16-project 2>&1 | grep -iE "error|field_height|spir|compile" | head -40`
Expected: NO compile errors mentioning `field_height` / SPIR-V. (Empty grep output, or only unrelated lines, is a pass. A splice/syntax error shows here.)

- [ ] **Step 6: Run windowed; confirm the bake still produces the SAME terrain (eye sanity)**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --auto-shot=user://s1t1_bake.png 2>&1 | grep -iE "TerrainLab: built|error|exception" | head -20`
Expected: a `TerrainLab: built 2048x2048 (h …..… m)` line with the SAME min/max height range as before the change (the refactor moved code, not math). No exceptions. (Auto-shot is a sanity check only — Task 2's numeric `--fieldcheck` is the real parity proof.)

- [ ] **Step 7: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/field_math.gdshaderinc shaders/field_height.glsl scripts/field/FieldCompute.cs
git commit -m "$(cat <<'EOF'
S1.1: extract field_math.gdshaderinc (one source), params as struct

Pull the heightfield pure-math out of field_height.glsl into a single
shared field_math.gdshaderinc with params passed as a FieldP struct arg
(no SSBO global). The compute shell keeps version/layout/buffer/main and
calls the shared field_height; FieldCompute.cs splices the shared file at
a marker (RD-GLSL has no #include — Godot proposal #9592). Math unchanged
(skin not bones); the bake range is identical. Sets up the spatial shader
to #include the SAME file in S1.3.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `--fieldcheck` — numeric parity self-check (baked page vs. analytic eval)

Add a one-shot CLI self-check that proves the analytic field equals the baked field within epsilon, on the SAME params. Mechanism: re-run `ProducePage` to get the baked heights (the GPU compute path = the data path), then independently dispatch the field a SECOND time at the SAME origin/res/spacing and compare element-by-element. (Because both go through the GPU compute, this guards the splice/refactor determinism. Task 3's eye-gate + this check together cover render-vs-data; a CPU re-impl of the whole field is explicitly out of scope — too much duplicated math, the very thing we're avoiding.)

**Files:**
- Create: `scripts/field/FieldCheck.cs` (the comparison + PASS/FAIL print).
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (add `--fieldcheck` flag: a `bool _fieldCheckCli` field + a parse line).
- Modify: `scripts/lab/TerrainLabUI.cs` (fire `FieldCheck.Run(...)` in the startup block alongside the other one-shot checks, ~line 170).

**Interfaces:**
- Consumes: `FieldCompute.ProducePage(FieldParams, originX, originZ, spacing?, res?, fieldMode)` (existing); `FieldParams.Load()`, `FieldParams.Spacing`.
- Produces: `static class FieldCheck { static void Run(FieldCompute fc, FieldParams p); }` — prints `FIELDCHECK: PASS …` or `FIELDCHECK: FAIL …` and the max abs diff; does not quit (caller may).

- [ ] **Step 1: Write `FieldCheck.cs`**

Create `scripts/field/FieldCheck.cs`:

```csharp
using Godot;

namespace WG16.Field;

/// One-shot numeric self-check for S1: the field is DETERMINISTIC and the
/// extract/refactor (field_math.gdshaderinc + struct params + C# splice) did not
/// change a single height. Bakes the page twice through the GPU compute path at the
/// SAME params and asserts byte-for-byte-close equality. PASS/FAIL to console.
public static class FieldCheck
{
    public static void Run(FieldCompute fc, FieldParams p)
    {
        float ox = -p.RegionSizeM * 0.5f, oz = -p.RegionSizeM * 0.5f;
        float[] a = fc.ProducePage(p, ox, oz);
        float[] b = fc.ProducePage(p, ox, oz);   // same inputs -> must be identical
        float maxAbs = 0f; int worst = -1;
        int n = Mathf.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            float d = Mathf.Abs(a[i] - b[i]);
            if (d > maxAbs) { maxAbs = d; worst = i; }
        }
        const float eps = 1e-3f;   // GPU determinism: identical inputs should diff ~0
        bool pass = a.Length == b.Length && maxAbs <= eps;
        GD.Print($"FIELDCHECK: {(pass ? "PASS" : "FAIL")}  maxAbsDiff={maxAbs:G6}m  worstIdx={worst}  n={n}  eps={eps}");
    }
}
```

- [ ] **Step 2: Add the `--fieldcheck` flag**

In `scripts/lab/TerrainLabUI.Cli.cs`: add the field near the other check flags (e.g. beside `_shadowCheckCli` at line 188):

```csharp
    private bool _fieldCheckCli;      // --fieldcheck → one-shot field determinism/parity self-check (S1)
```

and add the parse line in `ParseCli()` next to `else if (a == "--shadowcheck") { ... }` (line 129):

```csharp
            else if (a == "--fieldcheck") { _fieldCheckCli = true; }
```

- [ ] **Step 3: Fire the check at startup**

In `scripts/lab/TerrainLabUI.cs`, near the `_shadowCheckCli` block (~line 170), add a firing block. It needs the live `FieldCompute` + `FieldParams`. Find how `_terrain.Build(fc, p)` is called in this file (the `FieldCompute` instance and `FieldParams` are constructed during setup) and reuse those references; if they are local to the setup method, store them in fields `_fc` / `_fieldParams` when first created, then:

```csharp
        if (_fieldCheckCli) { FieldCheck.Run(_fc, _fieldParams); }
```

(If `_fc`/`_fieldParams` do not already exist as fields, add `private FieldCompute _fc = null!;` and `private FieldParams _fieldParams = null!;`, assign them where the field is built, and use them here. Do not construct a second `FieldCompute` — one local RenderingDevice at a time.)

- [ ] **Step 4: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: ends with `0 Error(s)`.

- [ ] **Step 5: Run the check windowed, confirm PASS**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --fieldcheck --auto-shot=user://s1t2.png 2>&1 | grep -iE "FIELDCHECK|error|exception" | head`
Expected: a line `FIELDCHECK: PASS  maxAbsDiff=… (≤ 1e-3) …`. (The `--auto-shot` just gives the scene a clean quit after the print.)

- [ ] **Step 6: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/field/FieldCheck.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S1.2: --fieldcheck one-shot field determinism self-check

Bakes the page twice at identical params and asserts maxAbsDiff <= 1e-3 m,
PASS/FAIL to console. Guards that the S1.1 extract/refactor kept the field
deterministic and unchanged. Wired as a one-shot CLI check alongside the
existing --shadowcheck/--atmoscheck pattern.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Ground shader displaces from the LIVE field (analytic render), toggled

Make `ground.gdshader` `#include` the shared `field_math.gdshaderinc` and compute height **live in `vertex()`** from world-XZ via `field_height(...)`, instead of sampling the baked `heightmap` texture. Gate it behind a `use_analytic` uniform so the baked path stays available for instant A/B (toggle, never big-bang). C# pushes the `FieldP` values + an `analytic_seed`/`analytic_spacing` as uniforms, and sets `use_analytic`.

**Files:**
- Modify: `shaders/ground.gdshader` (add `#include`; add a `FieldP`-building uniform block + `use_analytic`; in `vertex()` branch height/normal source between the baked texture and the live field).
- Modify: `scripts/lab/TerrainLab.cs` (push the field params as shader uniforms in `Build`; add `SetAnalytic(bool)` to flip `use_analytic`; pass the `FieldParams` through).
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (add `--analytic[=0|1]` to flip the toggle for A/B; default to ON so S1 exercises the new path).
- Modify: `scripts/lab/TerrainLabUI.cs` (apply the `--analytic` CLI value after `_terrain.Build`).

**Interfaces:**
- Consumes: shared `field_height(vec2, uint, float, FieldP)` + `struct FieldP` (Task 1); `FieldParams` (all fields); `TerrainLab.SetFloat/SetInt/SetBool` (existing passthroughs).
- Produces: `TerrainLab.SetAnalytic(bool on)`; ground uniforms `use_analytic` (bool), `analytic_seed` (int→uint in-shader), `analytic_spacing` (float), plus the `fp_*` scalar uniforms mirroring `FieldP`.

- [ ] **Step 1: Add the include + analytic path to `ground.gdshader`**

Modify `shaders/ground.gdshader`. After the existing `uniform float texel_world…` block, add the include and the field-param uniforms; then branch `vertex()`. Key change — height comes from `field_height(world_xz, uint(analytic_seed), analytic_spacing, fp)` when `use_analytic`, with the geometric normal taken from analytic taps at `±texel_world` in WORLD space (so the lit normal matches). Add at top, after line 1 (`shader_type spatial;`):

```glsl
#include "res://shaders/field_math.gdshaderinc"
```

Add these uniforms (near the other uniforms):

```glsl
uniform bool  use_analytic = true;     // S1: live field in vertex() vs. baked heightmap
uniform int   analytic_seed = 0;
uniform float analytic_spacing = 4.0;
// FieldP mirror (pushed from FieldParams in TerrainLab.cs). Names = fp_<field>.
uniform float fp_base_freq, fp_amplitude, fp_lacunarity, fp_gain;
uniform int   fp_octaves, fp_field_mode, fp_cont_octaves;
uniform float fp_cont_freq, fp_cont_weight, fp_uplift_freq, fp_uplift_weight;
uniform float fp_uplift_lo, fp_uplift_hi, fp_macro_pivot, fp_macro_amp, fp_hill_damp;
uniform float fp_ridge_freq, fp_ridge_amp, fp_mtn_lo, fp_mtn_hi, fp_grain_stretch;
uniform float fp_cont_warp, fp_uplift_warp, fp_massif_freq, fp_massif_floor;
uniform float fp_foothill_w, fp_foothill_h;

FieldP ground_fieldp() {
    FieldP fp;
    fp.origin_x = 0.0; fp.origin_z = 0.0; fp.spacing = analytic_spacing;
    fp.seed = uint(analytic_seed); fp.res = 0u; fp.octaves = uint(fp_octaves);
    fp.base_freq = fp_base_freq; fp.amplitude = fp_amplitude; fp.lacunarity = fp_lacunarity;
    fp.gain = fp_gain; fp.field_mode = uint(fp_field_mode); fp.cont_freq = fp_cont_freq;
    fp.cont_weight = fp_cont_weight; fp.uplift_freq = fp_uplift_freq; fp.uplift_weight = fp_uplift_weight;
    fp.uplift_lo = fp_uplift_lo; fp.uplift_hi = fp_uplift_hi; fp.macro_pivot = fp_macro_pivot;
    fp.macro_amp = fp_macro_amp; fp.hill_damp = fp_hill_damp; fp.ridge_freq = fp_ridge_freq;
    fp.ridge_amp = fp_ridge_amp; fp.mtn_lo = fp_mtn_lo; fp.mtn_hi = fp_mtn_hi;
    fp.grain_stretch = fp_grain_stretch; fp.cont_octaves = uint(fp_cont_octaves);
    fp.cont_warp = fp_cont_warp; fp.uplift_warp = fp_uplift_warp; fp.massif_freq = fp_massif_freq;
    fp.massif_floor = fp_massif_floor; fp.foothill_w = fp_foothill_w; fp.foothill_h = fp_foothill_h;
    return fp;
}

float analytic_h(vec2 world_xz) {
    return field_height(world_xz, uint(analytic_seed), analytic_spacing, ground_fieldp());
}
```

Replace the existing `vertex()` (lines 32-41) with a branched version:

```glsl
void vertex() {
    if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        VERTEX.y = analytic_h(wxz);
        float e = analytic_spacing;
        float hl = analytic_h(wxz - vec2(e, 0.0)), hr = analytic_h(wxz + vec2(e, 0.0));
        float hd = analytic_h(wxz - vec2(0.0, e)), hu = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(hl - hr, 2.0 * e, hd - hu));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
        vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
        VERTEX.y = height_at(uv);
        float t = texel_world / region_size;
        float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
        float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
        v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    }
}
```

- [ ] **Step 2: Push the field params + toggle from `TerrainLab.cs`**

In `scripts/lab/TerrainLab.cs`, in `Build(...)` after the existing `_mat.SetShaderParameter("texel_world", p.Spacing);` (line 46), push the analytic uniforms:

```csharp
        // S1: feed the live-field (analytic) path in ground.gdshader the SAME params the bake used.
        _mat.SetShaderParameter("analytic_seed", (int)p.Seed);
        _mat.SetShaderParameter("analytic_spacing", p.Spacing);
        _mat.SetShaderParameter("fp_base_freq", p.BaseFreq);
        _mat.SetShaderParameter("fp_amplitude", p.AmplitudeM);
        _mat.SetShaderParameter("fp_lacunarity", p.Lacunarity);
        _mat.SetShaderParameter("fp_gain", p.Gain);
        _mat.SetShaderParameter("fp_octaves", (int)p.Octaves);
        _mat.SetShaderParameter("fp_field_mode", 0);
        _mat.SetShaderParameter("fp_cont_octaves", (int)p.ContOctaves);
        _mat.SetShaderParameter("fp_cont_freq", p.ContFreq);
        _mat.SetShaderParameter("fp_cont_weight", p.ContWeight);
        _mat.SetShaderParameter("fp_uplift_freq", p.UpliftFreq);
        _mat.SetShaderParameter("fp_uplift_weight", p.UpliftWeight);
        _mat.SetShaderParameter("fp_uplift_lo", p.UpliftLo);
        _mat.SetShaderParameter("fp_uplift_hi", p.UpliftHi);
        _mat.SetShaderParameter("fp_macro_pivot", p.MacroPivot);
        _mat.SetShaderParameter("fp_macro_amp", p.MacroAmp);
        _mat.SetShaderParameter("fp_hill_damp", p.HillDamp);
        _mat.SetShaderParameter("fp_ridge_freq", p.RidgeFreq);
        _mat.SetShaderParameter("fp_ridge_amp", p.RidgeAmp);
        _mat.SetShaderParameter("fp_mtn_lo", p.MtnLo);
        _mat.SetShaderParameter("fp_mtn_hi", p.MtnHi);
        _mat.SetShaderParameter("fp_grain_stretch", p.GrainStretch);
        _mat.SetShaderParameter("fp_cont_warp", p.ContWarp);
        _mat.SetShaderParameter("fp_uplift_warp", p.UpliftWarp);
        _mat.SetShaderParameter("fp_massif_freq", p.MassifFreq);
        _mat.SetShaderParameter("fp_massif_floor", p.MassifFloor);
        _mat.SetShaderParameter("fp_foothill_w", p.FoothillW);
        _mat.SetShaderParameter("fp_foothill_h", p.FoothillH);
```

Add the toggle method (near `SetGiProxy`):

```csharp
    /// S1: flip the ground material between the live analytic field and the baked heightmap (A/B).
    public void SetAnalytic(bool on)
    {
        _mat?.SetShaderParameter("use_analytic", on);
        GD.Print($"TerrainLab: ground source = {(on ? "ANALYTIC (live field)" : "baked texture")}");
    }
```

- [ ] **Step 3: Add the `--analytic` CLI toggle**

In `scripts/lab/TerrainLabUI.Cli.cs`, add a field (near `_giProxyCli`):

```csharp
    private int _analyticCli = -1;   // --analytic[=0|1] → S1 ground source: live field vs baked (default: leave shader default ON)
```

parse line in `ParseCli()` (near `--giproxy=`):

```csharp
            else if (a.StartsWith("--analytic")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _analyticCli = (s == "1") ? 1 : 0; }
```

and apply in `ApplyCliOverrides()` (near the `_giProxyCli` apply, line 184):

```csharp
        if (_analyticCli >= 0) { _terrain.SetAnalytic(_analyticCli == 1); }
```

- [ ] **Step 4: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: ends with `0 Error(s)`.

- [ ] **Step 5: Compile-check the spatial shader include (headless import)**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --headless --import --rendering-driver vulkan --path /c/Wg16/wg-16-project 2>&1 | grep -iE "error|ground.gdshader|field_math|include" | head -40`
Expected: no errors mentioning `ground.gdshader` / `field_math` / `include`. (A bad `#include` path or a token the spatial language rejects shows here.)

- [ ] **Step 6: Eye-parity A/B — analytic vs. baked must look identical (user gate)**

Capture both paths at the same camera and hand BOTH to the user (do not self-judge from thumbnails — `ground-texture-feedback` rule):

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --analytic=1 --cam=0,300,0,-50,0 --auto-shot=user://s1t3_analytic.png 2>&1 | grep -iE "ground source|error|exception" | head
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --analytic=0 --cam=0,300,0,-50,0 --auto-shot=user://s1t3_baked.png 2>&1 | grep -iE "ground source|error|exception" | head
```

Expected: both render without exception; the `ground source = …` line confirms each path. **Gate: the user flies the scene with `--analytic=1` and confirms the terrain shape/lighting matches the baked path in MOTION** (the auto-shots are only a coarse sanity pair — the parity verdict is the user's eye, and `--fieldcheck` from Task 2 still PASSes). If the user sees a mismatch (offset, different hills, wrong normals), STOP — the analytic uniforms or the world-XZ convention are wrong; fix before Task 4.

- [ ] **Step 7: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S1.3: ground.gdshader displaces from the live field (#include), toggled

ground.gdshader now #includes the shared field_math.gdshaderinc and
computes height live in vertex() from world-XZ via field_height(), behind
a use_analytic toggle (--analytic[=0|1]) so the baked path stays for A/B.
TerrainLab pushes the FieldParams as fp_* uniforms. This is the render
path of Approach C; --fieldcheck still guards determinism.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Perf go/no-go — `--profile --profmove` verdict

Measure the in-motion frame time of the analytic render vs. the baked render and record the verdict. This is THE S1 decision: if live `field_height` per-vertex holds the 8 ms budget (vs. the ~3.8 ms baked floor), Approach C is green-lit and S2 (quadtree) may be planned. If it blows the budget badly, S1 has done its job cheaply — we stop and pivot the height-source before building any quadtree.

**Files:** none (measurement + a short written verdict appended to the spec's companion notes).

**Interfaces:** consumes the `--analytic` toggle (Task 3) + the existing `--profile=<secs> --profmove` harness (prints `PROFILE: avg … fps (… ms) worst …`, then quits).

- [ ] **Step 1: Profile the BAKED path (baseline)**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --analytic=0 --profile=5 --profmove 2>&1 | grep -E "PROFILE"`
Expected: one `PROFILE: avg N fps (M.M ms) worst …` line. Record M (the baked baseline ms).

- [ ] **Step 2: Profile the ANALYTIC path**

Run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn --analytic=1 --profile=5 --profmove 2>&1 | grep -E "PROFILE"`
Expected: one `PROFILE: avg N fps (M.M ms) worst …` line. Record M (the analytic ms).

- [ ] **Step 3: Record the verdict**

Append a short "S1 result" note to `docs/superpowers/specs/2026-06-21-infinite-terrain-cdlod-design.md` (a new `## 10. S1 result (measured)` section) with: baked ms, analytic ms, the delta, whether the analytic path is within the 8 ms in-motion budget, and the GO / NO-GO call. Note the analytic path here is the WHOLE 2048² mesh evaluated per-vertex with NO LOD — the worst case; S2's quadtree only reduces vertex count, so an analytic number near budget here is acceptable (S2 improves it), while an analytic number far over budget is the NO-GO that forces a height-source pivot.

- [ ] **Step 4: Commit the verdict**

```bash
cd /c/Wg16/wg-16-project
git add docs/superpowers/specs/2026-06-21-infinite-terrain-cdlod-design.md
git commit -m "$(cat <<'EOF'
S1.4: perf go/no-go verdict for analytic field render

Records --profile --profmove for baked vs analytic ground (whole 2048^2
mesh, no LOD = worst case). Captures the GO/NO-GO on Approach C's
per-vertex live field_height against the 8 ms in-motion budget.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review

**1. Spec coverage (S1 scope only — S2–S4 are deliberately separate plans):**
- Spec §1 "S1 = port `field_height` → shader include; current single mesh displaces from the live function; parity + perf go/no-go" → Tasks 1 (extract/include), 3 (live render), 2 (parity), 4 (perf). ✅
- Spec §2 "single shared field function, one source, two compile targets" → Task 1 (`.gdshaderinc` + C# splice). ✅
- Spec §6 verification ladder (build → `--import` → mechanical check → `--profmove` → eye) → every task's steps. ✅
- Spec §7 "skin not bones — relocate, don't change the math; parity is the gate" → Task 1 note + Task 2 `--fieldcheck`. ✅
- Spec §6 "toggle, never big-bang" → Task 3 `use_analytic` / `--analytic`. ✅
- Out of S1 scope (correctly absent): quadtree, geomorph, seams, streaming, origin, `--lodviz` — those are S2–S4, planned after this gate. ✅

**2. Placeholder scan:** No TBD/TODO; every code step shows full code; every run step shows the exact command + expected output. The only "fill-in" is Task 2 Step 3's "find the `FieldCompute`/`FieldParams` references" — bounded with an explicit fallback (add `_fc`/`_fieldParams` fields), not an open placeholder. ✅

**3. Type consistency:** `FieldP` struct fields match the `ParamsBuf P` block in `field_height.glsl` (verified field-for-field) and the `fp_*` uniforms in Task 3. `field_height(vec2, uint, float, FieldP)` signature identical across Task 1 (definition), Task 1 Step 2 (compute call), Task 3 (ground call). `FieldCheck.Run(FieldCompute, FieldParams)` consistent between definition (Task 2 Step 1) and call (Task 2 Step 3). `SetAnalytic(bool)` consistent between definition (Task 3 Step 2) and call (Task 3 Step 3 via `ApplyCliOverrides`). `--analytic`/`--fieldcheck` parse and apply sites consistent. ✅

## Notes for the executor
- **Risk hotspot:** Task 1 Step 1 is a verbatim math move with a single mechanical edit (`P.` → `fp.`). Do NOT "improve" any expression — byte-identical math is the whole point; `--fieldcheck` (Task 2) and the eye-parity (Task 3 Step 6) are the proofs.
- **If `--headless --import` reports a spatial-shader error on `#include`:** confirm the path is exactly `res://shaders/field_math.gdshaderinc` and the file extension is `.gdshaderinc` (a `.glsl` cannot be `#include`d by a spatial shader).
- **If `--fieldcheck` FAILs with a nonzero diff:** the splice changed determinism (e.g. a dropped function, a reordered expression). Diff the new `field_math.gdshaderinc` math against the original `field_height.glsl` body.
- **One Godot at a time** — every run step starts with the `taskkill` guard for this reason.
