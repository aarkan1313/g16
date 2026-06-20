# Erosion Unit 1 — Coupled Sim Core + Live Lab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove that WG16 can produce GREAT, logical erosion on the current region — valleys that drain downhill, dendritic channel networks, no elevation reversals or grow-then-shrink — via a single coherent GPU erosion sim, watched and tuned LIVE in motion, as a transform on the base heightfield (base-field math untouched). This unit owns the make-or-break creative question; bake/stream/infinite are later units.

**Architecture:** A GPU **hydraulic droplet (particle) erosion** sim — thousands of water particles descend the heightfield, each following true downhill flow while picking up / depositing sediment along its path, accumulated atomically into a height-delta buffer. This is ONE coherent model (every droplet obeys real drainage), NOT WG15's stack of fighting solvers — which is exactly why valleys come out logical (a droplet physically can't flow uphill). Runs on a local `RenderingDevice` (the `FieldCompute`/`SplatCompute` pattern), iterated over batches so progress is watchable; outputs a modified height `float[]` (+ flow/sediment debug fields). A thermal/talus relaxation pass follows each erosion batch to keep slopes physical. The Presenter re-uploads the eroded heightmap; a master toggle returns to the pristine base field instantly.

**Tech Stack:** Godot 4.6 mono, C# orchestration (new `scripts/erosion/ErosionCompute.cs`, wired from `scripts/lab/TerrainLab.cs` + `TerrainLabUI.cs`), GLSL compute (`shaders/erosion_droplet.glsl` + `shaders/erosion_thermal.glsl`), data-driven controls (`data/erosion_params.json` + `data/lab_controls.json` new **Erosion** tab).

---

## Why droplet erosion (model choice, justified per pillars)

The arc spec's mandate is "one coherent drainage-driven model, not a solver stack." Two candidates:
- **Grid shallow-water + stream-power + sediment-capacity (coupled):** physically richest, but it's an iterative PDE — stability/timestep is delicate, and getting it convergent is precisely where WG15-class "valleys grow then shrink" instability lurks. Higher ceiling, higher risk, slower to a good-looking result.
- **Hydraulic droplet (particle) erosion (CHOSEN for E1):** each of N water particles starts at a random cell, flows down the gradient, erodes when moving fast / under-capacity and deposits when slowing / over-capacity, dies after a lifetime. Coherent BY CONSTRUCTION (follows real downhill flow → dendritic, draining valleys), inherently stable (bounded per-droplet erosion, no global timestep to diverge), embarrassingly parallel on GPU (one thread per droplet, atomic-add into the height delta), well-proven (Beyer/Lague), and contained. It directly answers "do valleys do logical things" because droplets literally trace drainage.

Per pillars (best-long-term), droplet is the right FIRST model: fastest path to a *judged-good* result on the real region, and its flow/sediment output is exactly what E2's bake + E3's synthesis need. The coupled grid model is noted as the heavier upgrade if droplet's look has a ceiling we hit (e.g. wide river valleys / wetness that want true water depth). **Build droplet first; don't stack both.**

---

## Environment (Godot 4.6 gotchas — respect all)

- **ONE Godot at a time:** `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` before any relaunch (ignore "not found").
- **ALWAYS `--rendering-driver vulkan`.**
- **Build/import order after any `.cs`/`.glsl`:** `dotnet build WG16.csproj` → headless `--import` (console exe: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`) to compile-check shaders → then launch windowed.
- **LOCAL-RD COMPUTE CANNOT RUN UNDER `--headless`** (`CreateLocalRenderingDevice()` returns null → NullRef). The erosion sim is local-RD (one-time/iterated bake-like compute, NOT per-frame) — it runs WINDOWED. `--headless --import` only compile-checks the GLSL.
- **std430 param buffers** in C# must match the GLSL struct field-for-field (pad to 16-byte multiples) — mirror `FieldCompute.BuildParamsBytes` (128B block) / `SplatCompute.BuildParams`.
- **GLSL atomics:** droplet erosion needs `atomicAdd` into the height-delta buffer (many droplets touch the same cell). Use an `int`/`uint` fixed-point delta buffer (atomicAdd on float isn't core GLSL); accumulate scaled ints, convert on readback. State this explicitly (Task 2).
- **Never judge a motion/erosion artifact from a still** — the whole WG15 lesson; erosion convergence + valley logic are judged by the user flying it and by watching it iterate.
- **Lab CLIs:** `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<abs.png>`. This unit adds `--erode=0/1` (apply eroded vs pristine) and `--erode-iters=N` (batch count for headless capture).
- **Controls** are data-driven in `data/lab_controls.json`. Erosion has a mix of LIVE display knobs and RE-SIM knobs (changing droplet count/strength requires re-running the sim, like splat's `field`+`rebake`) — see Task 6 for the wiring (and the registry param-vs-field gotcha: a `param` naming a missing uniform silently no-ops; sim knobs use `field`+`rebake`-style routing with a C# case, NOT `param`).

---

## Integration seam (verified against current code)

- `FieldCompute.ProducePage(params, originX, originZ) → float[]` returns the base heightfield, row-major `z*res + x`, `res = HeightmapRes`, `region = RegionSizeM` m, `spacing = Spacing` m/texel (`scripts/field/FieldCompute.cs:39`).
- `TerrainLab.Build` (`scripts/lab/TerrainLab.cs:25`) stores `_heights`/`_res`/`_regionSize`/`_spacing`, uploads `_heights` as an `Image.Format.Rf` `ImageTexture` → `heightmap` uniform; the terrain shader displaces from it. AABB recomputed from min/max height.
- **Erosion is a transform on `_heights`:** `ErosionCompute.Erode(baseHeights, res, spacing, params) → erodedHeights` (+ optional flow/sediment `float[]` for debug/coloring). `TerrainLab` holds both pristine + eroded; the toggle picks which to upload. Base-field generation is NOT touched (settled).

---

## Task 1: ErosionParams (data) + the Erosion tab scaffold

**Files:** Create `scripts/erosion/ErosionParams.cs`, `data/erosion_params.json`; modify `data/lab_controls.json`.

- [ ] **Step 1: Write `ErosionParams.cs`** — a record of the droplet-sim knobs, JSON-loadable like `CloudParams`. Fields (with sane defaults):
```csharp
using Godot;
using System.Text.Json;

namespace WG16.Erosion;

/// Hydraulic droplet erosion knobs. SIM knobs (require a re-run) + a few output knobs.
public record ErosionParams(
    int   Droplets,        // particles per batch (e.g. 200_000)
    int   Batches,         // batches to run for a "full" erode (watch convergence)
    int   MaxLifetime,     // steps a droplet lives before dying
    float Inertia,         // 0 = follow gradient exactly, 1 = keep direction (0.05-0.3)
    float CapacityK,       // sediment capacity = CapacityK * speed * |slope| * water
    float MinSlope,        // floor on slope used for capacity (avoids 0-capacity puddles)
    float ErodeRate,       // fraction of (capacity-sediment) cut per step (0..1)
    float DepositRate,     // fraction of excess sediment dropped per step (0..1)
    float Evaporation,     // water lost per step (0..1)
    float Gravity,         // speed gain from descending
    float ErodeRadius,     // brush radius (cells) erosion spreads over (anti-spike)
    float ThermalTalus,    // slope above which thermal relaxation slumps (tan angle)
    float ThermalRate,     // thermal slump amount per batch
    int   ThermalIters)    // thermal passes per batch
{
    public const string Path = "res://data/erosion_params.json";
    public static ErosionParams Defaults() => new(
        Droplets: 200_000, Batches: 12, MaxLifetime: 48, Inertia: 0.1f,
        CapacityK: 4.0f, MinSlope: 0.01f, ErodeRate: 0.3f, DepositRate: 0.3f,
        Evaporation: 0.02f, Gravity: 4.0f, ErodeRadius: 2.5f,
        ThermalTalus: 0.6f, ThermalRate: 0.5f, ThermalIters: 2);
    public static ErosionParams Load() {
        string abs = ProjectSettings.GlobalizePath(Path);
        if (!System.IO.File.Exists(abs)) return Defaults();
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        var r = doc.RootElement; var d = Defaults();
        float F(string k, float v) => r.TryGetProperty(k, out var e) ? e.GetSingle() : v;
        int   I(string k, int v)   => r.TryGetProperty(k, out var e) ? e.GetInt32()  : v;
        return new ErosionParams(I("droplets",d.Droplets), I("batches",d.Batches),
            I("max_lifetime",d.MaxLifetime), F("inertia",d.Inertia), F("capacity_k",d.CapacityK),
            F("min_slope",d.MinSlope), F("erode_rate",d.ErodeRate), F("deposit_rate",d.DepositRate),
            F("evaporation",d.Evaporation), F("gravity",d.Gravity), F("erode_radius",d.ErodeRadius),
            F("thermal_talus",d.ThermalTalus), F("thermal_rate",d.ThermalRate), I("thermal_iters",d.ThermalIters));
    }
}
```
- [ ] **Step 2: Write `data/erosion_params.json`** with those defaults (so it's the on-disk source of truth, hot-editable).
- [ ] **Step 3: Add `"Erosion"` to the `tabs` array in `data/lab_controls.json`.** (Controls themselves come in Task 6, once the sim exists to drive.)
- [ ] **Step 4: Build.** `dotnet build WG16.csproj` → 0 errors. Validate JSON (`python -c "import json;json.load(open(r'data/erosion_params.json'));json.load(open(r'data/lab_controls.json'));print('ok')"`).
- [ ] **Step 5: Commit.** `git add scripts/erosion/ErosionParams.cs data/erosion_params.json data/lab_controls.json && git commit -m "Erosion E1: ErosionParams + erosion_params.json + Erosion tab scaffold"`

---

## Task 2: The droplet erosion compute shader

**Files:** Create `shaders/erosion_droplet.glsl`.

One thread per droplet. Reads the current height buffer (read-only sampling via bilinear from the `float[]` packed as a storage buffer), simulates the droplet's descent, and `atomicAdd`s erosion/deposition into a **fixed-point int delta buffer** (atomics require int). Height is read as base + (delta/FIXED). Erosion spreads over `ErodeRadius` (a small brush) to avoid single-cell spikes (a known droplet-erosion artifact). Bilinear gradient gives smooth flow.

- [ ] **Step 1: Write `shaders/erosion_droplet.glsl`.** Key structure (full GLSL written at implementation; the contract):
  - `#[compute]`, `local_size_x = 64` (1D over droplets).
  - Bindings: `binding 0` = `height` (float storage buffer, read), `binding 1` = `deltaFixed` (int storage buffer, atomicAdd target), `binding 2` = params std430 (res, spacing, all ErosionParams sim fields, plus a per-batch `seed`/`batchIndex` so each batch's droplets start at different random cells).
  - Per droplet: random start (hash of gid+seed → cell), `pos`, `dir=0`, `speed=1`, `water=1`, `sediment=0`. Loop `MaxLifetime`:
    - bilinear height + gradient at `pos`; `dir = dir*Inertia - grad*(1-Inertia)`; normalize; step to `posNew`.
    - if off-map or no downhill (`heightNew >= heightOld`): deposit remaining sediment at old pos, break.
    - `dh = heightNew - heightOld` (negative descending); `capacity = max(-dh, MinSlope) * speed * water * CapacityK`.
    - if `sediment > capacity` or `dh > 0` (uphill): `dep = (dh>0) ? min(dh, sediment) : (sediment-capacity)*DepositRate`; atomicAdd `+dep` at old cell (deposition is single-cell — fills pits); `sediment -= dep`.
    - else: `ero = min((capacity - sediment)*ErodeRate, -dh)`; spread `-ero` over the `ErodeRadius` brush via atomicAdd (weighted); `sediment += ero`.
    - `speed = sqrt(max(speed*speed + dh*Gravity, 0))`; `water *= (1 - Evaporation)`; `pos = posNew`.
  - All height edits go through a `FIXED = 1024.0` int fixed-point: `atomicAdd(deltaFixed[idx], int(amount * FIXED))`.
- [ ] **Step 2: Build + import (compile-check).** `dotnet build` → `--import` → expect `erosion_droplet.glsl` reimports clean, no compile error. (It won't run until Task 3 dispatches it.)
- [ ] **Step 3: Commit.** `git add shaders/erosion_droplet.glsl && git commit -m "Erosion E1: droplet erosion compute (gradient descent + capacity sediment, fixed-point atomic delta)"`

---

## Task 3: The thermal/talus relaxation compute shader

**Files:** Create `shaders/erosion_thermal.glsl`.

After each erosion batch, relax slopes steeper than the talus angle (slump material downhill) so erosion walls don't stand at impossible angles — the physical complement that keeps the result natural. Grid pass, one thread per cell, reads neighbor heights, moves a fraction of the excess toward lower neighbors. Stable (bounded fraction, symmetric).

- [ ] **Step 1: Write `shaders/erosion_thermal.glsl`.** `local_size 8,8`. Binding 0 = height delta (the accumulated `deltaFixed` converted, or a separate float working buffer), params (res, spacing, ThermalTalus, ThermalRate). Per cell: for each of 4/8 neighbors, if `(h - hN)/spacing > ThermalTalus`, move `ThermalRate * 0.5 * (excess)` from this cell to that neighbor (write via atomicAdd to a delta, or ping-pong two float buffers to avoid races — ping-pong is cleaner; state the choice). Iterate `ThermalIters` (C# re-dispatches).
- [ ] **Step 2: Build + import.** Clean compile.
- [ ] **Step 3: Commit.** `git add shaders/erosion_thermal.glsl && git commit -m "Erosion E1: thermal/talus relaxation compute (slump steep slopes, ping-pong stable)"`

---

## Task 4: ErosionCompute.cs — the orchestrator (local RD, batched, watchable)

**Files:** Create `scripts/erosion/ErosionCompute.cs`.

Mirrors `FieldCompute`/`SplatCompute`: local RD, compile both shaders, manage buffers, dispatch in batches. Exposes a batched API so the lab can run N droplet-batches (+thermal) and re-upload between batches to WATCH the erosion progress (the whole point — see it cut live).

- [ ] **Step 1: Write `ErosionCompute.cs`.** Public surface:
```csharp
public sealed class ErosionCompute : IDisposable {
    public ErosionCompute();                       // create local RD, compile both shaders, pipelines
    // Upload the base heights once; allocate delta(int) + working buffers at res.
    public void Begin(float[] baseHeights, int res, float spacing, ErosionParams p);
    // Run ONE batch (droplets + thermal iters); returns the current eroded heights for re-upload.
    public float[] StepBatch();
    // Convenience: run all p.Batches and return final.
    public float[] RunAll();
    // Side fields for debug/coloring (flow accumulation / sediment), null until requested.
    public float[] FlowField { get; }
    public int CurrentBatch { get; }
    public void Dispose();
}
```
  - `Begin`: `StorageBufferCreate` for base height (float bytes), the fixed-point `deltaFixed` (int, zeroed), thermal ping-pong buffers; build the std430 param bytes (field-for-field with the GLSL struct — mirror `FieldCompute.BuildParamsBytes`).
  - `StepBatch`: update the per-batch seed in params; dispatch droplet shader with `groups=(Droplets+63)/64`; then dispatch thermal `ThermalIters` times; `Submit`/`Sync`; read back `deltaFixed`, compute `eroded[i] = base[i] + delta[i]/FIXED`, return it. (Local RD, windowed only.)
  - Atomics note in code comment: deltaFixed is int fixed-point because GLSL `atomicAdd` is integer-only.
- [ ] **Step 2: Build.** `dotnet build` → 0 errors.
- [ ] **Step 3: Commit.** `git add scripts/erosion/ErosionCompute.cs && git commit -m "Erosion E1: ErosionCompute orchestrator (local RD, batched droplet+thermal, watchable)"`

---

## Task 5: Wire into TerrainLab (toggle pristine↔eroded, re-upload, AABB)

**Files:** Modify `scripts/lab/TerrainLab.cs`.

- [ ] **Step 1: Hold both fields + the eroder.** Add `private float[]? _erodedHeights; private ErosionCompute? _eroder; private bool _eroded;` and keep `_heights` as the pristine base.
- [ ] **Step 2: Add `UploadHeights(float[] h)`** — factor the existing `Image.CreateFromData(Rf)` → `ImageTexture` → `SetShaderParameter("heightmap", tex)` + AABB recompute out of `Build()` into a method, so erosion can re-upload without rebuilding the mesh.
- [ ] **Step 3: Add the erosion API** the UI calls:
```csharp
public void ErodeAll(ErosionParams p) {
    if (_heights == null) return;
    _eroder ??= new ErosionCompute();
    _eroder.Begin(_heights, _res, _spacing, p);
    _erodedHeights = _eroder.RunAll();
    _eroded = true; UploadHeights(_erodedHeights);
}
public void SetEroded(bool on) {           // instant pristine↔eroded toggle
    _eroded = on;
    UploadHeights(on && _erodedHeights != null ? _erodedHeights : _heights!);
}
```
  (A batched/watchable variant — `ErodeStep()` calling `_eroder.StepBatch()` + `UploadHeights` each call, driven from `_Process` over N frames — is the LIVE-watch path; include it so the user can see it cut. Keep `RunAll` for headless `--erode`.)
- [ ] **Step 4: Build + windowed smoke test.** `dotnet build` → `--import` → run windowed, call `ErodeAll` once (temporarily from `_Ready` or via the Task 6 control); confirm the terrain visibly changes and toggling restores pristine. (Local RD runs windowed — NOT headless.)
- [ ] **Step 5: Commit.** `git add scripts/lab/TerrainLab.cs && git commit -m "Erosion E1: TerrainLab erode/toggle/re-upload wiring (pristine<->eroded)"`

---

## Task 6: Erosion tab controls + CLI (the live tuning surface)

**Files:** Modify `data/lab_controls.json`, `scripts/lab/TerrainLabUI.cs`.

Two kinds of knob (mind the registry gotcha — `param` is for live shader uniforms ONLY; these drive a C# re-sim, so they use a `field`-style route + a C# case, NOT `param`):
- **Re-sim knobs** (droplets/rates/etc.): set an `ErosionParams` field in C#, then re-run `ErodeAll` (like splat's `rebake`). 
- **Toggle**: `erosion_on` → `TerrainLab.SetEroded`.
- An **"Erode" button** + a **"Step" button** (run one batch, watch) — buttons, not sliders.

- [ ] **Step 1:** Add an `erosion_on` toggle + an "🌊 Erode (run)" and "▶ Erode step" button row to the Erosion tab (buttons added in `BuildPanel`'s per-tab area, like the existing Randomize/FLAT buttons). Wire: Erode → `_terrain.ErodeAll(_erosionParams)`; Step → `_terrain.ErodeStep()`; toggle → `_terrain.SetEroded(on)`.
- [ ] **Step 2:** Add the re-sim sliders as `field`-routed rows (id-routed to set `_erosionParams = _erosionParams with {…}` then call `ErodeAll`), NOT `param`. Add a C# handler keyed by control id (mirror how cloud knobs route to `CloudVolume`). Keep heavy re-sim off the drag — apply on release / via the Erode button to avoid re-simming every slider tick.
- [ ] **Step 3:** Add CLI: `--erode=0/1` (apply eroded at startup via `ErodeAll` then `SetEroded`), `--erode-iters=N` (override `Batches` for capture). Mirror the existing `--ar=` / `--coverage=` CLI plumbing.
- [ ] **Step 4: Build + validate JSON + windowed A/B + profile.**
  - `--erode=0` vs `--erode=1` auto-shots (same cam) → eroded shows valleys/channels the pristine doesn't.
  - Debug: also expose a flow-field view (color by `FlowField`) so the DRAINAGE can be verified directly (does water accumulate into coherent channels?) before judging the lit terrain.
  - Profile/time the full `ErodeAll` (it's offline, but record seconds for 200k×12 batches; tune `Droplets`/`Batches` if absurdly slow).
- [ ] **Step 5: Commit.** `git add data/lab_controls.json scripts/lab/TerrainLabUI.cs && git commit -m "Erosion E1: Erosion-tab controls (toggle + run/step buttons + re-sim knobs) + --erode CLI"`

---

## Task 7: Live judging handoff (THE gate — tests directly against WG15's failure)

- [ ] **Step 1:** Kill strays, build, launch windowed.
- [ ] **Step 2: User judges LIVE.** Run "Erode step" repeatedly and WATCH it cut (the thing WG15 never did — it baked once). Questions, aimed straight at the WG15 symptoms: (a) Do valleys/channels form a LOGICAL dendritic network that DRAINS downhill (no isolated pits, no uphill channels)? (b) Does it CONVERGE — do successive batches settle toward a stable good look, or oscillate/over-erode (grow-then-shrink, reversals)? Find the batch count that looks best. (c) Toggle `erosion_on` off/on — is the eroded result clearly better than pristine, not just different? (d) Tune rates (erode/deposit/capacity/thermal) live; does it stay logical across settings? Check the flow-field debug view shows coherent drainage. Watch perf only loosely (it's offline).
- [ ] **Step 3: On approval** → the erosion MODEL is proven good; proceed to E2 (drainage skeleton bake) + update ROADMAP/DECISIONS. If it over-erodes / oscillates / makes illogical pits: it's isolated to `erosion_droplet.glsl` (capacity/erode/deposit balance, or the deposit-in-pit logic) — tune, or escalate to the coupled grid model (arc spec's noted alternative). **If it can't reach "great" after a fair effort, STOP and reconsider — do not grind 19 versions (the WG15 trap); surface it.**

---

## Self-Review notes
- **Spec coverage:** implements arc E1 — the coupled (single coherent, droplet-based) sim core + live lab on the current region, as a transform on the base heightfield (`FieldCompute` float[] → `ErosionCompute` → re-upload), base-field math untouched, behind an instant pristine↔eroded toggle. Flow/sediment side-fields produced (feed E2/E3 + debug). Watchable batched stepping = judged in MOTION (the explicit WG15 fix). Bake/stream/infinite explicitly OUT (E2-E4).
- **Why droplet not solver-stack:** directly counters WG15's diagnosed root cause — one coherent model where every droplet obeys real downhill drainage (logical valleys by construction), inherently stable (bounded per-droplet, no divergent global timestep), vs the chained fighting solvers. Documented; grid-coupled noted as the heavier upgrade, not built.
- **Placeholder honesty:** Tasks 2/3 state the shader CONTRACT + full algorithm (per-droplet loop, capacity/erode/deposit math, fixed-point atomics, thermal ping-pong) rather than every GLSL line — this is a ~150-line shader written against a fully-specified algorithm, not a hand-wave; the math, bindings, atomics decision, and brush/ping-pong choices are all pinned. Not a placeholder; a bounded known write.
- **Type/interface consistency:** `ErosionParams` (record + JSON) mirrors `CloudParams`; `ErosionCompute` mirrors `FieldCompute`/`SplatCompute` (local RD, std430 params field-for-field, dispatch/sync/readback). `ErodeAll`/`ErodeStep`/`SetEroded` on `TerrainLab`; `UploadHeights` factored from `Build`. Registry: re-sim knobs use `field`-style + C# case (NOT `param` — the documented gotcha); toggle/buttons id-routed.
- **Atomics/format flagged:** GLSL atomicAdd is integer → fixed-point int delta buffer (FIXED=1024), converted on readback. Thermal uses ping-pong float buffers to avoid races. Both stated in-task.
- **Risk posture:** offline local-RD compute (windowed, not headless); base field is the always-available fallback; eye-gated by watching it cut; explicit STOP-don't-grind clause referencing the WG15 19-versions trap.
