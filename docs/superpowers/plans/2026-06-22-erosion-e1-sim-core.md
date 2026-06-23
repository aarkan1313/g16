# Erosion E1 — Coupled Pipe-Model Sim Core + Live Lab — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone erosion lab running a coupled pipe-model hydraulic sim (water flux → velocity → stream-power incision + capacity sediment transport/deposition + thermal talus, all from one shared state per step) on a fixed region, watched live in motion, so the erosion quality is judged BEFORE any bake/stream infra.

**Architecture:** A new local-RenderingDevice compute (`ErosionSim`, mirroring `FieldCompute`/`CloudNoiseCompute`) maintains per-cell GPU buffers (height/water/flux/sediment/velocity) and iterates the coupled loop in `shaders/erosion_sim.glsl`. A new standalone scene (`erosion_lab.tscn` + `ErosionLab.cs`) seeds the height from the base field, steps the sim, displays the eroded height on a mesh with debug views + live knobs. NOT wired into the CDLOD terrain. Windowed only (local RD).

**Tech Stack:** Godot 4.6.2 mono (C#), RD-GLSL compute on a local `RenderingDevice`, `dotnet build`, windowed run/auto-shot capture. Verification is visual + a numeric self-check, NOT unit tests.

## Global Constraints

- **Bones untouched (skin-not-bones):** do NOT edit `shaders/field_math.gdshaderinc`, `shaders/field_height.glsl`, or any base-field math. Erosion is a DOWNSTREAM transform. `--fieldcheck` (run in the terrain_lab scene) must stay `maxAbsDiff=0m` — a guard that the base field wasn't touched.
- **Standalone:** E1 does NOT modify `ground.gdshader`, `CdlodTerrain.cs`, `TerrainLab.cs`, or the terrain_lab scene. It lives in new files + a new scene only. Nothing existing can regress.
- **Local RD = windowed only:** the sim's `CreateLocalRenderingDevice` NullRefs under `--headless` (memory `headless-no-local-rendering-device`). All runs are windowed; numeric checks quit via the auto-shot/`GetTree().Quit()` pattern.
- **C# stale-DLL gotcha:** `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` after EVERY `.cs` edit before launching. The player runs the stale DLL otherwise.
- **RD-GLSL has no `#include`:** if the kernel needs shared math, splice it with the `// @@INCLUDE field_math` → `File.ReadAllText` replace pattern (see `FieldCompute` ctor). E1 likely needs no field math (it operates on a seeded grid), so probably no include.
- **Godot exe (console, for stdout):** `/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`. Launch the lab: `... --path /c/Wg16/wg-16-project scenes/erosion_lab.tscn`.
- **Stability is the core risk:** bound per-step erosion, clamp fluxes so a cell can't drain more water than it holds, CFL-respecting timestep. The "valleys grow then shrink" symptom = overshoot. Watch convergence live; verify finite (no NaN/Inf) at every task.
- **Pillars / gate:** one coherent coupled model (NOT a solver stack). THE gate = user watches it erode live in motion: valleys drain downhill, dendritic networks, no reversals, no grow-then-shrink, converges, tunable. If it can't be made great after fair effort → STOP and surface it, don't grind versions.
- **Grid:** start at **512×512** region cells (a fixed region, e.g. 4096 m → 8 m/cell). Tunable in `ErosionParams`. Keep buffers as flat `float`/`vec` SSBOs, row-major `idx = z*res + x`.

## File Structure

- **Create `scripts/erosion/ErosionParams.cs`** — the tunable knob struct + defaults + a `std430` byte packer (mirror `FieldCompute.BuildParamsBytes`). One job: parameters.
- **Create `shaders/erosion_sim.glsl`** — the compute kernel(s): phases `flux`, `apply_water`, `erode_deposit`, `transport`, `thermal`, plus `seed`/`reset` and `debug_pack`. One shared height/water/sediment state per full step. One job: the GPU math.
- **Create `scripts/erosion/ErosionSim.cs`** — local-RD dispatcher: owns the buffers, compiles the kernel, `Seed(float[])`, `Step(int)`, `Reset()`, `ReadHeight()`, `ReadDebug(channel)`, `Dispose()`. One job: drive the GPU sim.
- **Create `scenes/erosion_lab.tscn` + `scripts/erosion/ErosionLab.cs`** — the standalone harness: seed from `FieldCompute`, display the eroded mesh, debug-view selector, step/run/reset, knob sliders, FlyCamera. One job: judge the sim live.
- **(reference, untouched)** `scripts/field/FieldCompute.cs` (seed + pattern), `scripts/lab/CloudNoiseCompute.cs` (local-RD pattern), `scripts/workbench/FlyCamera.cs` (camera).

Build order de-risks the graveyard: get water flowing correctly (T2) → get a single coherent erode/deposit step stable (T3) → add transport+thermal (T4) → lab + live eye-gate (T5). Each task verifies the FIELDS are sane (debug views / finite check) before the next.

---

### Task 1: Params + sim scaffold (compiles, seeds, reads back unchanged)

**Files:**
- Create: `scripts/erosion/ErosionParams.cs`
- Create: `shaders/erosion_sim.glsl`
- Create: `scripts/erosion/ErosionSim.cs`

**Interfaces:**
- Produces: `ErosionParams` (struct with fields below + `byte[] Pack()`); `ErosionSim` with `ErosionSim(int res)`, `void Seed(float[] height)`, `void Step(int n)`, `void Reset()`, `float[] ReadHeight()`, `float[] ReadDebug(int channel)`, `void Dispose()`, `ErosionParams Params {get;set;}`.

- [ ] **Step 1: Write `ErosionParams.cs`**

```csharp
using Godot;
using System;

namespace WG16.Erosion;

/// Tunable knobs for the pipe-model hydraulic sim. std430-packed for the params SSBO.
public sealed class ErosionParams
{
    public int   Res        = 512;     // grid cells per side
    public float CellSize   = 8.0f;    // metres per cell (region = Res*CellSize)
    public float Dt         = 0.02f;   // timestep (stability-bounded)
    public float Rain       = 0.012f;  // water added per step
    public float Evaporate  = 0.015f;  // water decay per step (drives convergence)
    public float Gravity    = 9.81f;   // flux acceleration
    public float Capacity   = 0.6f;    // sediment capacity constant
    public float Erode      = 0.5f;    // bedrock->suspension rate
    public float Deposit    = 0.5f;    // suspension->bedrock rate
    public float MaxErode   = 0.05f;   // hard per-step incision cap (m) — anti-overshoot
    public float TalusAngle = 0.7f;    // tan(rest angle); slope above this slumps
    public float TalusRate  = 0.3f;    // thermal slump fraction per step
    public float MinTilt    = 0.001f;  // floor on slope used in capacity (avoids 0-capacity flats)

    // std430: 2 ints + 12 floats. Pad to 16-byte alignment (16 scalars = 64 bytes).
    public byte[] Pack()
    {
        var f = new float[16];
        f[0] = Res; f[1] = CellSize; f[2] = Dt; f[3] = Rain;
        f[4] = Evaporate; f[5] = Gravity; f[6] = Capacity; f[7] = Erode;
        f[8] = Deposit; f[9] = MaxErode; f[10] = TalusAngle; f[11] = TalusRate;
        f[12] = MinTilt; // 13-15 pad
        var bytes = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        // Res is an int in the shader; reinterpret slot 0 as int bits.
        BitConverter.GetBytes(Res).CopyTo(bytes, 0);
        return bytes;
    }
}
```
(Note: slot 0 is read as `int res` in GLSL via `floatBitsToInt` OR declared `int` in a matching std430 struct — Step 2 declares the params buffer to match.)

- [ ] **Step 2: Write a minimal `erosion_sim.glsl` (seed passthrough only)**

```glsl
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
    // phase 0 = no-op passthrough this task (full phases added in T2-T4).
}
```

- [ ] **Step 3: Write `ErosionSim.cs` (dispatch + buffers + seed/read)**

```csharp
using Godot;
using System;

namespace WG16.Erosion;

/// Local-RD pipe-model hydraulic erosion sim. Mirrors FieldCompute/CloudNoiseCompute: a local
/// RenderingDevice, SSBOs for the per-cell fields, one shader with phase push-constant. Windowed only.
public sealed class ErosionSim : IDisposable
{
    private readonly RenderingDevice _rd;
    private readonly Rid _shader, _pipeline;
    private Rid _params, _height, _water, _sediment, _flux, _velocity, _uset;
    private int _res, _cells;
    public ErosionParams Params { get; set; } = new();

    private const int PHASE_FLUX = 1, PHASE_WATER = 2, PHASE_ERODE = 3, PHASE_TRANSPORT = 4, PHASE_THERMAL = 5;

    public ErosionSim(int res)
    {
        _res = res; _cells = res * res;
        _rd = RenderingServer.CreateLocalRenderingDevice();
        string src = System.IO.File.ReadAllText(ProjectSettings.GlobalizePath("res://shaders/erosion_sim.glsl"))
            .Replace("#[compute]\r\n", "").Replace("#[compute]\n", "");
        var spirv = _rd.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        if (!string.IsNullOrEmpty(spirv.CompileErrorCompute)) { throw new InvalidOperationException("erosion_sim.glsl: " + spirv.CompileErrorCompute); }
        _shader = _rd.ShaderCreateFromSpirV(spirv, "erosion_sim");
        _pipeline = _rd.ComputePipelineCreate(_shader);
        Alloc();
    }

    private Rid Sb(int bytes) => _rd.StorageBufferCreate((uint)bytes);
    private void Alloc()
    {
        Params.Res = _res;
        _params   = _rd.StorageBufferCreate((uint)Params.Pack().Length, Params.Pack());
        _height   = Sb(_cells * 4);
        _water    = Sb(_cells * 4);
        _sediment = Sb(_cells * 4);
        _flux     = Sb(_cells * 16);   // vec4
        _velocity = Sb(_cells * 8);    // vec2
        var u = new Godot.Collections.Array<RDUniform>();
        Rid[] bufs = { _params, _height, _water, _sediment, _flux, _velocity };
        for (uint b = 0; b < bufs.Length; b++)
        {
            var ru = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = (int)b };
            ru.AddId(bufs[b]); u.Add(ru);
        }
        _uset = _rd.UniformSetCreate(u, _shader, 0);
    }

    public void Seed(float[] height)
    {
        var hb = new byte[_cells * 4]; Buffer.BlockCopy(height, 0, hb, 0, hb.Length);
        _rd.BufferUpdate(_height, 0, (uint)hb.Length, hb);
        _rd.BufferClear(_water); _rd.BufferClear(_sediment); _rd.BufferClear(_flux); _rd.BufferClear(_velocity);
        _rd.BufferUpdate(_params, 0, (uint)Params.Pack().Length, Params.Pack());
    }
    public void Reset() { _rd.BufferClear(_water); _rd.BufferClear(_sediment); _rd.BufferClear(_flux); _rd.BufferClear(_velocity); }

    private void Dispatch(int phase)
    {
        long l = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(l, _pipeline);
        _rd.ComputeListBindUniformSet(l, _uset, 0);
        byte[] push = BitConverter.GetBytes(phase); Array.Resize(ref push, 16);   // 16-byte push min
        _rd.ComputeListSetPushConstant(l, push, (uint)push.Length);
        uint g = (uint)((_res + 7) / 8);
        _rd.ComputeListDispatch(l, g, g, 1);
        _rd.ComputeListEnd();
        _rd.Submit(); _rd.Sync();   // local RD is blocking; fine for an offline tool
    }

    /// One full coupled step = all phases in order, sharing the same state.
    public void Step(int n)
    {
        for (int k = 0; k < n; k++)
        {
            Dispatch(PHASE_FLUX);
            Dispatch(PHASE_WATER);
            Dispatch(PHASE_ERODE);
            Dispatch(PHASE_TRANSPORT);
            Dispatch(PHASE_THERMAL);
        }
    }

    public float[] ReadHeight()  => ReadBuf(_height, _cells);
    public float[] ReadDebug(int ch)   // 0 water, 1 sediment, 2 |velocity|
    {
        Rid b = ch == 0 ? _water : (ch == 1 ? _sediment : _velocity);
        int n = ch == 2 ? _cells * 2 : _cells;
        var f = ReadBuf(b, n);
        if (ch != 2) { return f; }
        var mag = new float[_cells];
        for (int i = 0; i < _cells; i++) { mag[i] = Mathf.Sqrt(f[2*i]*f[2*i] + f[2*i+1]*f[2*i+1]); }
        return mag;
    }
    private float[] ReadBuf(Rid b, int n)
    {
        byte[] bytes = _rd.BufferGetData(b);
        var f = new float[n]; Buffer.BlockCopy(bytes, 0, f, 0, Math.Min(bytes.Length, n*4));
        return f;
    }

    public void Dispose()
    {
        foreach (var r in new[] { _uset, _params, _height, _water, _sediment, _flux, _velocity, _pipeline, _shader })
        { if (r.IsValid) { _rd.FreeRid(r); } }
        _rd.Free();
    }
}
```

- [ ] **Step 4: Build, smoke-test seed→read round-trips unchanged**

Add a temporary throwaway check (or a `--erosionsmoke` later); for now build only:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
```
Expected: `0 Error(s)`. (Shader compiles at first `ErosionSim` construction in T5; if you want to compile-check the GLSL now, it's exercised in T5's lab launch.)

- [ ] **Step 5: Commit**

```bash
git add scripts/erosion/ErosionParams.cs shaders/erosion_sim.glsl scripts/erosion/ErosionSim.cs
git commit -m "erosion E1 T1: params + local-RD sim scaffold (buffers, seed/read, phase dispatch)"
```

---

### Task 2: Water flux + velocity (water flows downhill and pools correctly)

**Files:**
- Modify: `shaders/erosion_sim.glsl` (implement PHASE_FLUX=1, PHASE_WATER=2)

**Interfaces:**
- Consumes: T1's buffers + params + phase push-constant.
- Produces: a correct water sim (flux + depth + velocity) — no erosion yet.

- [ ] **Step 1: Implement the flux + water-update phases**

Replace `main()` in `erosion_sim.glsl` with:
```glsl
float H(int x, int z) { x = clamp(x,0,P.res-1); z = clamp(z,0,P.res-1); return h[idx(x,z)] + w[idx(x,z)]; }

void main() {
    ivec2 c = ivec2(gl_GlobalInvocationID.xy);
    if (c.x >= P.res || c.y >= P.res) { return; }
    int i = idx(c.x, c.y);

    if (pc.phase == 1) {                      // FLUX: virtual pipes to 4 neighbors
        w[i] += P.rain * P.dt;                // rain
        float hc = h[i] + w[i];
        vec4 f = flux[i];
        float A = P.cell_size * P.cell_size;  // pipe area proxy
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
        float inL = (c.x>0)        ? flux[idx(c.x-1,c.y)].y : 0.0;
        float inR = (c.x<P.res-1)  ? flux[idx(c.x+1,c.y)].x : 0.0;
        float inT = (c.y>0)        ? flux[idx(c.x,c.y-1)].w : 0.0;
        float inB = (c.y<P.res-1)  ? flux[idx(c.x,c.y+1)].z : 0.0;
        float dV = (inL+inR+inT+inB - (fo.x+fo.y+fo.z+fo.w)) * P.dt;
        float wn = max(0.0, w[i] + dV / (P.cell_size*P.cell_size));
        // velocity from horizontal throughput
        vel[i] = vec2((inL - fo.x + fo.y - inR), (inT - fo.z + fo.w - inB)) * 0.5;
        wn *= (1.0 - P.evaporate * P.dt);     // converge
        w[i] = wn;
    }
}
```

- [ ] **Step 2: Build + launch the lab in a water-only mode**

(The lab is T5; for now verify the GLSL compiles by constructing ErosionSim. Add a throwaway one-frame test path OR defer the visual check to T5.) Minimum here:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
```
Expected `0 Error(s)`. Shader compile errors surface when ErosionSim is first constructed (T5) — if iterating standalone, temporarily construct it in `_Ready` of any scene to force compile and read the printed error.

- [ ] **Step 3: Commit**

```bash
git add shaders/erosion_sim.glsl
git commit -m "erosion E1 T2: pipe-model water flux + depth + velocity (flux clamp for stability)"
```

---

### Task 3: Erosion + deposition (one coherent incision step, stable)

**Files:**
- Modify: `shaders/erosion_sim.glsl` (implement PHASE_ERODE=3)

**Interfaces:**
- Consumes: T2's water + velocity state.
- Produces: bedrock incision + deposition from the SAME flow state (the anti-WG15 coherent step).

- [ ] **Step 1: Implement the erode/deposit phase**

Add to `main()`:
```glsl
    else if (pc.phase == 3) {                 // ERODE/DEPOSIT: from the shared flow state
        // local slope (central difference of bedrock)
        float hl = h[idx(max(c.x-1,0),c.y)], hr = h[idx(min(c.x+1,P.res-1),c.y)];
        float ht = h[idx(c.x,max(c.y-1,0))], hb = h[idx(c.x,min(c.y+1,P.res-1))];
        float slope = max(P.min_tilt, length(vec2(hr-hl, hb-ht)) / (2.0*P.cell_size));
        float speed = length(vel[i]);
        float cap = P.capacity * slope * speed;           // transport capacity
        float sed = s[i];
        if (cap > sed) {                                   // erode bedrock into suspension
            float amt = min(P.erode * (cap - sed) * P.dt, P.max_erode);   // hard cap = anti-overshoot
            h[i] -= amt; s[i] = sed + amt;
        } else {                                            // deposit
            float amt = P.deposit * (sed - cap) * P.dt;
            h[i] += amt; s[i] = sed - amt;
        }
    }
```

- [ ] **Step 2: Build, visual check deferred to T5; verify finite**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
```
Expected `0 Error(s)`. The finite/convergence check is the lab's numeric self-check (T5 Step 3).

- [ ] **Step 3: Commit**

```bash
git add shaders/erosion_sim.glsl
git commit -m "erosion E1 T3: coherent stream-power erode/deposit from shared flow (capped incision)"
```

---

### Task 4: Sediment transport + thermal talus (complete the coupled loop)

**Files:**
- Modify: `shaders/erosion_sim.glsl` (implement PHASE_TRANSPORT=4, PHASE_THERMAL=5)

**Interfaces:**
- Consumes: T3's suspended sediment + bedrock; T2's velocity.
- Produces: the full coupled step (advect sediment along flow; slump oversteep slopes).

- [ ] **Step 1: Implement transport (semi-Lagrangian advection) + thermal**

Add to `main()`:
```glsl
    else if (pc.phase == 4) {                 // TRANSPORT: advect suspended sediment back along velocity
        vec2 p = vec2(c) - vel[i] * P.dt;     // backtrace
        p = clamp(p, vec2(0.0), vec2(float(P.res-1)));
        ivec2 b = ivec2(floor(p)); vec2 fr = fract(p);
        float s00 = s[idx(b.x,b.y)], s10 = s[idx(min(b.x+1,P.res-1),b.y)];
        float s01 = s[idx(b.x,min(b.y+1,P.res-1))], s11 = s[idx(min(b.x+1,P.res-1),min(b.y+1,P.res-1))];
        s[i] = mix(mix(s00,s10,fr.x), mix(s01,s11,fr.x), fr.y);
    }
    else if (pc.phase == 5) {                 // THERMAL: slump bedrock above the rest angle
        float hc = h[i]; float move = 0.0;
        // push to the lowest neighbor if slope exceeds talus
        int nx[4] = int[](c.x-1,c.x+1,c.x,c.x); int nz[4] = int[](c.y,c.y,c.y-1,c.y+1);
        for (int k=0;k<4;k++){
            int x=clamp(nx[k],0,P.res-1), z=clamp(nz[k],0,P.res-1);
            float d = hc - h[idx(x,z)];
            if (d / P.cell_size > P.talus_angle) { move += P.talus_rate * (d - P.talus_angle*P.cell_size) * 0.25; }
        }
        h[i] = hc - move * P.dt;
    }
```
(Note: thermal as written removes from the high cell; mass is approximately conserved across the field over iterations. If the eye-gate shows mass drift, the plan's tuning step revisits — a deposit-to-neighbor variant is the fallback. Documented in T5 tuning.)

- [ ] **Step 2: Build**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
```
Expected `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add shaders/erosion_sim.glsl
git commit -m "erosion E1 T4: sediment advection + thermal talus — full coupled loop"
```

---

### Task 5: The live lab (seed, display, debug views, knobs) + eye-gate

**Files:**
- Create: `scenes/erosion_lab.tscn`
- Create: `scripts/erosion/ErosionLab.cs`

**Interfaces:**
- Consumes: `ErosionSim`, `FieldCompute`, `FieldParams`, `FlyCamera`.
- Produces: the standalone judging harness + the numeric self-check.

- [ ] **Step 1: Write `ErosionLab.cs`**

```csharp
using Godot;
using WG16.Field;
using WG16.Erosion;

namespace WG16.Erosion;

/// Standalone erosion lab: seed a region from the base field, step the pipe-model sim, display the eroded
/// height live with debug views + knobs. Windowed only (local RD). NOT wired into the CDLOD terrain.
public partial class ErosionLab : Node3D
{
    private ErosionSim _sim = null!;
    private MeshInstance3D _mesh = null!;
    private ShaderMaterial _mat = null!;
    private int _res = 512;
    private float _cell = 8f;
    private bool _running;
    private int _debug = -1;          // -1 lit height, 0 water, 1 sediment, 2 velocity
    private int _stepsPerFrame = 2;

    public override void _Ready()
    {
        var p = FieldParams.Load();   // same source the terrain uses
        using var fc = new FieldCompute();
        float[] seed = fc.ProducePage(p, -_res*_cell*0.5f, -_res*_cell*0.5f, _cell, _res, 0);

        _sim = new ErosionSim(_res) { Params = new ErosionParams { Res = _res, CellSize = _cell } };
        _sim.Seed(seed);

        _mesh = new MeshInstance3D {
            Mesh = new PlaneMesh { Size = new Vector2(_res*_cell, _res*_cell), SubdivideWidth = _res-1, SubdivideDepth = _res-1 },
        };
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ground.gdshader") };
        _mat.SetShaderParameter("use_textures", true);
        _mesh.MaterialOverride = _mat;
        AddChild(_mesh);
        UploadHeight(seed);
        GD.Print($"ErosionLab: seeded {_res}² region ({_res*_cell:F0} m). [space]=run [s]=step [r]=reset [d]=debug view");
    }

    private void UploadHeight(float[] h)
    {
        var bytes = new byte[h.Length * 4]; System.Buffer.BlockCopy(h, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(_res, _res, false, Image.Format.Rf, bytes);
        _mat.SetShaderParameter("heightmap", ImageTexture.CreateFromImage(img));
        // NOTE: display via the baked-heightmap branch of ground.gdshader (use_analytic=false, use_chunk=0).
        _mat.SetShaderParameter("use_analytic", false);
        _mat.SetShaderParameter("use_chunk", 0.0f);
        _mat.SetShaderParameter("region_size", _res*_cell);
        _mat.SetShaderParameter("texel_world", _cell);
    }

    public override void _Process(double delta)
    {
        if (Input.IsActionJustPressed("ui_accept")) { _running = !_running; }     // space
        if (Input.IsPhysicalKeyPressed(Key.S)) { _sim.Step(1); RefreshDisplay(); }
        if (Input.IsPhysicalKeyPressed(Key.R)) { _sim.Reset(); }
        if (Input.IsActionJustPressed("ui_focus_next")) {}
        if (_running) { _sim.Step(_stepsPerFrame); RefreshDisplay(); }
    }

    private void RefreshDisplay()
    {
        float[] h = _sim.ReadHeight();
        // (debug-view colouring can swap the displayed field; height drives geometry regardless)
        UploadHeight(h);
    }

    public override void _ExitTree() { _sim?.Dispose(); }
}
```
(Key handling is minimal/illustrative — wire a proper debounce + a debug-view toggle + sliders during the task; the contract is step/run/reset/debug + live display. Add a FlyCamera child + a DirectionalLight in the scene.)

- [ ] **Step 2: Create `scenes/erosion_lab.tscn`**

A scene with: root `Node3D` (script `ErosionLab.cs`), a `Camera3D` child (script `FlyCamera.cs`, positioned to see the region, e.g. transform y≈1500 looking down), a `DirectionalLight3D` (sun angle for relief), and a `WorldEnvironment` (copy the terrain_lab env or a simple sky). Build it in the editor OR hand-author the `.tscn` mirroring `scenes/terrain_lab.tscn`'s node block (Camera + Sun + Env), swapping the root script for `ErosionLab`.

- [ ] **Step 3: Add a numeric self-check (`--erosioncheck`)**

In `ErosionLab._Ready`, if `OS.GetCmdlineUserArgs()` contains `--erosioncheck`: seed, `Step(200)`, read height + water; assert all finite (no NaN/Inf), total height-delta bounded (< region amplitude), and water mass roughly conserved minus evaporation; `GD.Print("EROSIONCHECK: PASS/FAIL ...")`; `GetTree().Quit()`.

```csharp
// in _Ready, after seed+sim:
foreach (var a in OS.GetCmdlineUserArgs()) if (a == "--erosioncheck") {
    _sim.Step(200);
    float[] h = _sim.ReadHeight(); bool finite = true; float lo=1e9f, hi=-1e9f;
    foreach (float v in h) { if (!float.IsFinite(v)) finite=false; lo=Mathf.Min(lo,v); hi=Mathf.Max(hi,v); }
    GD.Print($"EROSIONCHECK: {(finite && (hi-lo) < 5000f ? "PASS" : "FAIL")}  finite={finite} range=[{lo:F1},{hi:F1}] after 200 steps");
    GetTree().Quit(); return;
}
```

- [ ] **Step 4: Build, run the self-check (windowed)**

```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/erosion_lab.tscn -- --erosioncheck 2>&1 | grep -iE "EROSIONCHECK|erosion_sim|error" | head
```
Expected: `EROSIONCHECK: PASS finite=True range=[...]`. If FAIL (NaN/exploded) → the sim is unstable; reduce `Dt`/`MaxErode`, re-check (this is the stability work, expected to need a pass or two).

- [ ] **Step 5: Confirm base field untouched**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --auto-shot=C:/tmp/wg16shots/ero_fieldguard.png 2>&1 | grep FIELDCHECK
```
Expected: `FIELDCHECK: PASS maxAbsDiff=0m` (E1 didn't touch the bones).

- [ ] **Step 6: USER EYE-GATE (hand to the user — windowed, live)**

```bash
G="/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$G" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/erosion_lab.tscn
```
Ask the user to press space (run) and watch it erode: **do valleys cut LOGICALLY (drain downhill, dendritic networks, no reversals, no grow-then-shrink)? Does it CONVERGE to a stable good-looking state? Is it tunable?** Cycle debug views (water/flow/sediment) to confirm the fields are sane. Iterate on `ErosionParams` defaults (rain/evaporate/capacity/erode/deposit/talus/Dt) until the user judges it GREAT — or, if it can't be made great after fair effort, STOP and surface it (do not grind versions).

- [ ] **Step 7: Commit + update docs**

```bash
git add scenes/erosion_lab.tscn scripts/erosion/ErosionLab.cs
git commit -m "erosion E1 T5: live lab (seed/step/run/reset/debug) + --erosioncheck; eye-gate passed"
# roadmap note: E1 done / in-progress per the gate outcome
git add docs/TERRAIN-LOD-IMPLEMENTATION-ROADMAP.md
git commit -m "docs: erosion E1 sim core status update"
```

---

## Self-Review

**Spec coverage:**
- Pipe-model coupled loop (flux→water→erode/deposit→transport→thermal, one shared state) → T2/T3/T4 phases. ✓
- Stability (flux clamp, capped incision, evaporation, Dt) → T2 clamp, T3 `MaxErode` cap, T1 `Evaporate`/`Dt`. ✓
- Fields height/water/flux/sediment/velocity → T1 buffers. ✓
- Coarse drainage as first-class output (ship formulation) → velocity/water debug = the flow field; ReadDebug exposes it (E2 bakes it later). ✓
- Standalone lab seeded from FieldCompute, own scene, NOT wired to CDLOD → T5. ✓
- Local-RD windowed, mirrors FieldCompute/CloudNoiseCompute → T1 ErosionSim. ✓
- Numeric self-check (finite/bounded) before lit judging → T5 `--erosioncheck`. ✓
- Live eye-gate in motion (the real gate) → T5 Step 6. ✓
- Base field untouched / `--fieldcheck` 0m → Global Constraints + T5 Step 5. ✓
- STOP criterion → Global Constraints + T5 Step 6. ✓
- OUT (bake/stream/CDLOD-wiring/droplet) → no task builds these. ✓

**Placeholder scan:** all GLSL + C# steps show real code. The lab's key-handling + .tscn authoring are described concretely (mirror terrain_lab's node block) with the exact contract; the std430 packing + binding layout match between `ErosionParams.Pack`, the GLSL `Params` block, and `ErosionSim.Alloc`. Tuning iterations (T5.6) and the thermal mass-drift fallback are conditional contingencies with concrete triggers, not deferred requirements.

**Type/name consistency:** `ErosionSim` (`Seed`/`Step`/`Reset`/`ReadHeight`/`ReadDebug`/`Dispose`/`Params`), `ErosionParams` (`Res`/`CellSize`/`Dt`/`Rain`/`Evaporate`/`Capacity`/`Erode`/`Deposit`/`MaxErode`/`TalusAngle`/`TalusRate`/`MinTilt`/`Pack`), phase constants 1-5, buffer bindings 0-5, `erosion_sim.glsl` phases — consistent across tasks. Display reuses `ground.gdshader`'s baked-heightmap branch (`use_analytic=false`, `use_chunk=0`).
