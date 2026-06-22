# S2b — Per-Vertex Geomorph + LOD-crossing Test Harness Implementation Plan

> **⚠ POST-EXECUTION CORRECTION (2026-06-21).** Task 1 Step 1's shader code below contains a SIGN BUG:
> the line `morphK = 1.0 - morphK;` (with its "morph toward coarse as the chunk APPROACHES coarsening"
> comment) is WRONG and was removed during the eye-gate (commit cc9638a). It inverted the morph: it put
> the full-coarse morph at the band's NEAR edge (where a chunk actually splits into FINER children) and
> ZERO morph at the FAR edge (where the chunk is replaced by its COARSER parent), so the far-edge LOD
> hand-off snapped raw — a simultaneous height + detail pop. The CORRECT direction is `morphK = 0` at the
> near edge, `1` at the far edge (exactly what the clamp on the line above already produces, and what the
> SPEC says — the plan code contradicted its own spec). Delete the inversion line. Locked by `--morphcheck`
> (MorphCheck.cs): far-edge morphed verts coincide with the coarse grid to 0.0 m; re-adding the inversion
> makes --morphcheck FAIL. Leaving the buggy block below as the historical record of what shipped + was fixed.


> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make CDLOD LOD transitions pop-free via per-vertex geomorph (each vertex morphs its grid XZ toward the coarser grid by its own camera-distance factor, sampling the same `field_height` at the morphed position), and build a reusable LOD-crossing test harness (3 scripted camera paths, keys + `--testpath=N`, per-path perf + invariant report) to make the in-motion pop eye-gate repeatable.

**Architecture:** Geomorph lives entirely in `ground.gdshader`'s chunk vertex branch — it recovers each vertex's integer grid coordinate from its unit position, computes a per-vertex `morphK` from camera distance within the chunk's LOD band (camera position + band passed as uniforms; band derived from chunk size × `splitFactor`), lerps the fine grid position toward the coarse (even) grid position by `morphK`, and samples the field at the morphed world XZ. A new `TerrainTestPaths` Node drives the camera along JSON-defined paths and reports avg/worst/spike ms + chunk count + the ≤1-level invariant sampled along the path. A `detail_fade` curve is reserved (unused).

**Tech Stack:** Godot 4.6.2 mono (C#), spatial `.gdshader` (`MODEL_MATRIX`, uniforms, `instance uniform`), JSON (`System.Text.Json` / Godot `FileAccess`), `dotnet build`.

## Global Constraints

- **⚠ BUILD C# AFTER EVERY `.cs` EDIT** — `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` BEFORE launching. The Godot player binary does NOT rebuild C# (runs the stale `.godot/mono/temp/bin/Debug/WG16.dll`); only `.gdshader` hot-compiles. A stale DLL silently masks `.cs` changes (cost a whole debugging session — memory `wg16-csharp-stale-dll-gotcha`). If a code change "has no effect," suspect the stale DLL FIRST.
- **Build clean:** `dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.
- **Skin not bones:** do NOT touch `field_math.gdshaderinc` / `field_height.glsl` / `FieldParams`. `--fieldcheck` MUST stay `PASS maxAbsDiff=0m`.
- **`--cdlodcheck` MUST stay PASS** (≤1-level invariant intact). **`--profmove` stays under 8 ms** (geomorph adds a distance+smoothstep+lerp per vertex — negligible vs the 3 `analytic_h` evals; S2a had 2.4 ms headroom).
- **NO TDD** (GPU/visual). "Test" = build → `--fieldcheck`/`--cdlodcheck` PASS → `--testpath=N` report → `--profmove` → THE eye-gate (user flies the 3 paths in motion, ZERO pops). Auto-shots are SANITY only — NEVER claim pop-free from a downscaled still (`ground-texture-feedback`).
- **⚠ RUN-INVOCATION:** user `--flags` AFTER a bare `--` separator (else `OS.GetCmdlineUserArgs()` empty → flags no-op, scene never quits). `--auto-shot=` needs an OS path (`C:/tmp/wg16shots/x.png`), NOT `user://`. Canonical: `Godot…_console.exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- <FLAGS>`. ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` first); scene CANNOT run `--headless`; analytic-slow profiles need ~80 s wall-clock poll.
- **Commit per task** (send-it/revert). Stage ONLY this plan's files (NEVER `git add -A` — the sky lane edits in parallel: `CloudVolume.cs`, `TerrainLabUI.Lighting.cs`, `cloud_sky.gdshader`, `project.godot`, `NightBillboards.cs`). Footer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **Gate viewport:** `scenes/terrain_lab.tscn`. Toggles: `--cdlod=1` (quadtree), `--lodviz=1` (LOD tint), `--cdlodcheck`, key `1` (analytic↔baked).

### Key facts (verified this session)
- Chunk vertex branch in `shaders/ground.gdshader` (`vertex()`, `if (use_chunk > 0.5)`, ~lines 90-107): derives `wxz` from `MODEL_MATRIX`, `chunk_size = length(MODEL_MATRIX[0].xyz)`, displaces `VERTEX.y = analytic_h(wxz) - skirt`, normal from 2 forward taps at `e = analytic_spacing`. The skirt drops border verts (`edge < 0.001`).
- `CdlodTerrain` (`scripts/lab/CdlodTerrain.cs`): `Tick(camPos)` maps quadtree leaves → pooled `MeshInstance3D` chunks (Position+Scale, per-chunk `CustomAabb` via `ChunkHeightRange`, `SetInstanceShaderParameter("lod_viz", level)`). Fields `GridN=65`, `MaxDepth=6`, `SplitFactor=2.5f`. `CdlodQuadtree.SplitFactor` rule: subdivide when `camDist < size*splitFactor`.
- `TerrainLab` (`scripts/lab/TerrainLab.cs`): owns `_mat` (the `ground.gdshader` ShaderMaterial) + `_cdlod`; `SetCdlod`, `CdlodTick`, `SetCdlodViz`. Pushes `cam_world` via `SetCameraWorld` (but the stripped `ground.gdshader` does NOT declare it — S2b adds it).
- Key-handler pattern: `scripts/lab/TerrainLabUI.Process.cs` `_Process` `if (_ready)` block — the key-`1` toggle (`Input.IsKeyPressed(Key.Key1)` + `_lastKey1Down` debounce) is the template. Camera node: `/root/TerrainLabRoot/Camera`. FlyCamera script drives it normally.
- CLI pattern: `scripts/lab/TerrainLabUI.Cli.cs` `ParseCli()` (flag table) + `ApplyCliOverrides()`. Exact-match flags MUST precede `StartsWith` prefixes of the same stem.
- Godot `PlaneMesh` unit grid: `VERTEX.xz ∈ [-0.5, 0.5]`; integer grid coord = `round((VERTEX.xz + 0.5) * (GridN-1))`.

---

## Task ordering
1. **Task 1 — geomorph shader + wiring.** The core pop-killer. Per-vertex morphK + odd→even XZ morph + sample-at-morphed-XZ + spacing-scaled normal epsilon in `ground.gdshader`; `cam_world` + `grid_n` + `split_factor` uniforms fed from `CdlodTerrain`/`TerrainLab`. Gate: builds, `--fieldcheck`/`--cdlodcheck` PASS, renders, perf under budget.
2. **Task 2 — test harness.** `TerrainTestPaths` + `data/terrain_test_paths.json` (3 paths) + keys (`5`/`6`/`7`) + `--testpath=N` + the per-path report. Gate: each path drives the camera + prints a report.
3. **Task 3 — reserved detail-fade curve.** `detail_fade()` + uniform, unused. Trivial; commit.
4. **THE EYE-GATE** (after Tasks 1-2): user flies the 3 paths, confirms ZERO pops in motion.

---

### Task 1: Per-vertex geomorph (the pop-killer)

Add geomorph to `ground.gdshader`'s chunk branch and feed it the camera position, grid resolution, and split factor. The morph: recover the vertex's grid coord, snap it toward the even (coarse) grid, lerp by a per-vertex `morphK` derived from camera distance within this chunk's LOD band, sample the field at the morphed XZ.

**Files:**
- Modify: `shaders/ground.gdshader` — chunk vertex branch: add `cam_world`, `grid_n`, `split_factor` uniforms; geomorph math; spacing-scaled normal epsilon.
- Modify: `scripts/lab/TerrainLab.cs` — declare/push `cam_world` (re-add — it's pushed via `SetCameraWorld` but the stripped shader dropped the uniform), `grid_n`, `split_factor` on `_mat` when CDLOD is set up.
- Modify: `scripts/lab/CdlodTerrain.cs` — expose `GridN`/`SplitFactor` to `TerrainLab` so it can push them (add a getter or pass at `Setup`).

**Interfaces:**
- Consumes: `MODEL_MATRIX`, `analytic_h(vec2)`, `analytic_spacing` (existing); `CdlodTerrain.GridN`, `CdlodTerrain.SplitFactor`.
- Produces (shader uniforms): `uniform vec3 cam_world;`, `uniform float grid_n;`, `uniform float split_factor;` on `ground.gdshader`. No new C# public types.

- [ ] **Step 1: Add the geomorph uniforms + math to `ground.gdshader`**

Add three uniforms near the chunk uniform (`use_chunk`):

```glsl
uniform vec3  cam_world = vec3(0.0);    // camera world pos (re-added post-strip; chunk geomorph distance)
uniform float grid_n = 65.0;            // chunk grid resolution (verts/side) — for odd->even snap
uniform float split_factor = 2.5;       // quadtree split rule: subdivide when camDist < size*split_factor
```

Replace the chunk branch (`if (use_chunk > 0.5) { ... }`, the block from `vec2 wxz = ...` through `v_h = h0;`) with the geomorph version:

```glsl
    if (use_chunk > 0.5) {
        // --- S2b GEOMORPH: morph each vertex toward the COARSE grid by a per-vertex factor, so a
        // chunk's odd verts slide onto the coarse grid before it coarsens -> no elevation pop, and
        // per-vertex distance makes the morph continuous across chunk seams. ---
        float chunk_size = length(MODEL_MATRIX[0].xyz);    // chunk world size (X-basis length)

        // fine vertex grid coord (integer) from unit pos; coarse target = snap odd index down to even.
        vec2 u_fine = VERTEX.xz + vec2(0.5);               // unit [0,1]
        vec2 g_fine = u_fine * (grid_n - 1.0);             // [0, grid_n-1]
        vec2 g_coarse = floor(g_fine * 0.5 + 0.5) * 2.0;   // nearest even grid index
        vec2 u_coarse = g_coarse / (grid_n - 1.0);

        // per-vertex morphK: distance from camera to THIS vertex's world pos, mapped across the chunk's
        // LOD band. The chunk coarsens at camDist == chunk_size*split_factor; it was created (subdivided
        // from its parent) at camDist == 2*chunk_size*split_factor. Morph 0->1 over the FAR HALF of that
        // band (start morphing at the midpoint, fully morphed by the coarsen distance) — the standard
        // CDLOD morph window.
        vec3 vworld_fine = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;   // pre-displacement world pos (y≈0)
        float d = distance(cam_world.xz, vworld_fine.xz);
        float far_d  = 2.0 * chunk_size * split_factor;    // band outer (this LOD just appeared)
        float near_d = 1.0 * chunk_size * split_factor;    // band inner (about to coarsen)
        float mid_d  = mix(near_d, far_d, 0.5);
        float morphK = clamp((d - mid_d) / max(far_d - mid_d, 1e-3), 0.0, 1.0);  // 0 near .. 1 far edge
        // NOTE: morph toward coarse as the chunk APPROACHES coarsening, i.e. as d -> near_d. So invert:
        morphK = 1.0 - morphK;                              // 1 at near edge (coarsen imminent), 0 at far

        vec2 u_morph = mix(u_fine, u_coarse, morphK);
        vec2 wxz = (MODEL_MATRIX * vec4(u_morph.x - 0.5, 0.0, u_morph.y - 0.5, 1.0)).xz;  // morphed world XZ

        float h0 = analytic_h(wxz);

        // Skirt (S2a): drop the border ring so it tucks under coarser neighbors (crack backstop). Use
        // the FINE unit pos for the border test (a vertex is "border" by its mesh position, not morph).
        float edge = min(min(u_fine.x, 1.0 - u_fine.x), min(u_fine.y, 1.0 - u_fine.y));
        float skirt = (edge < 0.001) ? (chunk_size / 32.0 + 4.0) : 0.0;

        VERTEX.y = h0 - skirt;

        // Normal from forward differences scaled to the chunk's ACTUAL vertex spacing (S2a review fix:
        // was analytic_spacing=4 m but coarse-chunk verts are 16 m+ apart). Sample at the morphed XZ.
        float vs = chunk_size / max(grid_n - 1.0, 1.0);    // chunk vertex spacing (m)
        float hx = analytic_h(wxz + vec2(vs, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, vs));
        v_normal = normalize(vec3(h0 - hx, vs, h0 - hz));
        NORMAL = v_normal;
        v_h = h0;
    } else if (use_analytic) {
```

NOTE: the morphed XZ is computed by applying `MODEL_MATRIX` to the morphed unit position (re-centered to ±0.5). This keeps the world-space mapping identical to the un-morphed path; only the sampled XZ shifts.

- [ ] **Step 2: Expose GridN/SplitFactor from CdlodTerrain + push uniforms from TerrainLab**

In `scripts/lab/CdlodTerrain.cs`, add public getters (the fields exist):

```csharp
    public float GridResolution => GridN;
    public float SplitFactorValue => SplitFactor;
```

In `scripts/lab/TerrainLab.cs`, in `SetCdlod(bool on)`, after `_mat?.SetShaderParameter("use_chunk", ...)`, push the geomorph params (the `_cdlod` is non-null here):

```csharp
        if (on && _cdlod != null)
        {
            _mat?.SetShaderParameter("grid_n", _cdlod.GridResolution);
            _mat?.SetShaderParameter("split_factor", _cdlod.SplitFactorValue);
        }
```

Confirm `cam_world` is already pushed every frame: `TerrainLab.SetCameraWorld(Vector3 p)` does `_mat.SetShaderParameter("cam_world", p)` — it exists (used by the sky lane); the only change is the shader now DECLARES `cam_world` so it's consumed. (If `SetCameraWorld` is not wired to push every frame, ensure `TerrainLabUI.Process.cs` calls `_terrain.SetCameraWorld(camPos)` in the `if (_ready)` block — it already does, line ~128.)

- [ ] **Step 3: Build (C# — per the stale-DLL rule)**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 4: Field + invariant guards still PASS + shader compiles**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
mkdir -p /c/tmp/wg16shots
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --fieldcheck --cdlodcheck --cam=0,250,600,-20,0 --auto-shot=C:/tmp/wg16shots/s2b_t1.png > /tmp/s2b_t1.log 2>&1 &
P=$!; for i in $(seq 1 9); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "FIELDCHECK: (PASS|FAIL)|CDLODCHECK: (PASS|FAIL)|SHADER ERROR.*ground|exception" /tmp/s2b_t1.log | head
```
Expected: `FIELDCHECK: PASS`, `CDLODCHECK: PASS`, NO `ground.gdshader` shader error. The auto-shot renders terrain (the morph is subtle at a static frame; this only confirms it still renders + compiles, NOT pop-freeness — that's the eye-gate).

- [ ] **Step 5: Perf still under budget (--profmove)**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --profile=5 --profmove > /tmp/s2b_prof.log 2>&1 &
P=$!; for i in $(seq 1 30); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s2b_prof.log
```
Expected: avg ms still under 8 (was 5.6 ms in S2a; geomorph adds negligible cost). If it regressed materially, the morph math is heavier than expected — investigate before proceeding.

- [ ] **Step 6: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader scripts/lab/TerrainLab.cs scripts/lab/CdlodTerrain.cs
git commit -m "$(cat <<'EOF'
S2b.1: per-vertex geomorph in the chunk vertex shader

Each chunk vertex morphs its grid XZ toward the COARSE (even) grid by a
per-vertex morphK (camera distance across the chunk's LOD band), then samples
the SAME field_height at the morphed XZ -> elevation pop structurally
impossible, and per-vertex distance makes the morph continuous across chunk
seams. Adds cam_world (re-declared post-strip) + grid_n + split_factor
uniforms (fed from CdlodTerrain via TerrainLab). Also scales the chunk normal
finite-difference epsilon to the chunk's real vertex spacing (S2a review fix).
--fieldcheck/--cdlodcheck still PASS; perf under budget. Pop-freeness is the
user eye-gate (S2b.2 harness + flight).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: LOD-crossing test harness (`TerrainTestPaths` + keys + `--testpath=N` + report)

A `TerrainTestPaths` Node that flies the camera along JSON-defined paths and reports per-path perf + the ≤1-level invariant sampled along the path. Three starter paths. Keys 5/6/7 start them live; `--testpath=N` runs path N headlessly and quits.

**Files:**
- Create: `scripts/lab/TerrainTestPaths.cs` — the path player + report.
- Create: `data/terrain_test_paths.json` — 3 path definitions (tunable).
- Modify: `scripts/lab/TerrainLab.cs` — own a `TerrainTestPaths` (sibling like `CdlodTerrain`), expose `RunTestPath(int)` + `TickTestPath(double)` + chunk-count/invariant accessors for the report.
- Modify: `scripts/lab/CdlodTerrain.cs` — add `LeafCountLastTick` + `InvariantHoldsNow(out string)` accessors (for the along-path report).
- Modify: `scripts/lab/TerrainLabUI.Process.cs` — keys 5/6/7 start paths; drive `TickTestPath(delta)` each frame.
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — `--testpath=N` flag.
- Modify: `scripts/lab/TerrainLabUI.cs` — apply `--testpath=N` at startup.

**Interfaces:**
- Produces:
  - `class TerrainTestPaths : Node { void Setup(Camera3D cam, CdlodTerrain cdlod); void Start(int pathIndex); void Tick(double delta); bool Running { get; } }` — flies the camera along path `pathIndex`, accumulates per-frame ms + chunk count + invariant, prints `TESTPATH: <name> avg=… worst=… spikes=… chunks=lo-hi invariant=PASS/FAIL` when the path completes (and quits if started via CLI).
  - `CdlodTerrain.LeafCountLastTick` (int), `CdlodTerrain.InvariantHoldsNow(out string msg)` (bool).
- Consumes: the `Camera3D` at `/root/TerrainLabRoot/Camera`, `CdlodTerrain`.

- [ ] **Step 1: Write `data/terrain_test_paths.json`**

Create `data/terrain_test_paths.json` (3 paths; positions are world XYZ, look is a target point; `duration` seconds, `cli_quit` whether a CLI run quits after):

```json
{
  "schema": "terrain_test_paths.v1",
  "paths": [
    {
      "name": "low_fast_flythrough",
      "duration": 8.0,
      "from": [-3500, 80, 0], "to": [3500, 80, 0],
      "look_offset": [0, -8, 200],
      "comment": "low + fast horizontal across many LOD rings — worst-case pop stress"
    },
    {
      "name": "vertical_descend_ascend",
      "duration": 10.0,
      "from": [0, 1800, 1200], "to": [0, 120, 1200],
      "look_offset": [0, -40, -400],
      "comment": "drop through LOD bands over a fixed point — altitude-driven LOD"
    },
    {
      "name": "slow_boundary_hover",
      "duration": 12.0,
      "from": [300, 120, 900], "to": [700, 120, 900],
      "look_offset": [0, -10, -300],
      "comment": "creep slowly across a single LOD boundary — isolate one transition"
    }
  ]
}
```

- [ ] **Step 2: Write `TerrainTestPaths.cs`**

Create `scripts/lab/TerrainTestPaths.cs`:

```csharp
using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// Flies the camera along scripted LOD-crossing paths (data/terrain_test_paths.json) so pops can be
/// eye-judged in motion and perf is repeatable. Reports avg/worst/spike ms + chunk-count range + the
/// <=1-level invariant sampled ALONG the path. Drive+measure only — auto pop-detection is deferred
/// (the _onFrame hook is reserved for it). S2b verification machinery; reusable beyond S2b.
public sealed partial class TerrainTestPaths : Node
{
    private sealed class PathDef
    {
        public string Name = "";
        public float Duration = 8f;
        public Vector3 From, To, LookOffset;
    }

    private Camera3D _cam = null!;
    private CdlodTerrain _cdlod = null!;
    private readonly List<PathDef> _paths = new();
    private int _active = -1;
    private double _t;
    private bool _cliQuit;
    // accumulators
    private double _accum; private int _frames; private double _worst; private int _spikes;
    private int _chunkLo, _chunkHi; private bool _invOk; private string _invMsg = "ok";

    public bool Running => _active >= 0;

    public void Setup(Camera3D cam, CdlodTerrain cdlod)
    {
        _cam = cam; _cdlod = cdlod;
        Load();
    }

    private void Load()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/terrain_test_paths.json");
        if (!System.IO.File.Exists(abs)) { GD.PrintErr("TerrainTestPaths: missing data/terrain_test_paths.json"); return; }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        foreach (JsonElement p in doc.RootElement.GetProperty("paths").EnumerateArray())
        {
            var d = new PathDef
            {
                Name = p.GetProperty("name").GetString() ?? "?",
                Duration = p.GetProperty("duration").GetSingle(),
                From = Vec3(p, "from"), To = Vec3(p, "to"), LookOffset = Vec3(p, "look_offset"),
            };
            _paths.Add(d);
        }
        GD.Print($"TerrainTestPaths: loaded {_paths.Count} paths");
    }

    private static Vector3 Vec3(JsonElement p, string key)
    {
        JsonElement a = p.GetProperty(key);
        return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    }

    /// Start path by index (0-based). cliQuit: quit the tree when the path finishes (for --testpath).
    public void Start(int pathIndex, bool cliQuit = false)
    {
        if (pathIndex < 0 || pathIndex >= _paths.Count) { GD.PrintErr($"TerrainTestPaths: no path {pathIndex}"); return; }
        _active = pathIndex; _t = 0; _cliQuit = cliQuit;
        _accum = 0; _frames = 0; _worst = 0; _spikes = 0;
        _chunkLo = int.MaxValue; _chunkHi = 0; _invOk = true; _invMsg = "ok";
        GD.Print($"TerrainTestPaths: START '{_paths[pathIndex].Name}' ({_paths[pathIndex].Duration:F0}s)");
    }

    public void Tick(double delta)
    {
        if (_active < 0) { return; }
        PathDef p = _paths[_active];
        _t += delta;
        float u = Mathf.Clamp((float)(_t / p.Duration), 0f, 1f);
        Vector3 pos = p.From.Lerp(p.To, u);
        _cam.GlobalPosition = pos;
        _cam.LookAt(pos + p.LookOffset, Vector3.Up);

        // measure (skip the first 0.3 s warm-up so a one-off load frame doesn't dominate 'worst')
        if (_t > 0.3)
        {
            _accum += delta; _frames++;
            if (delta > _worst) { _worst = delta; }
            if (delta > 0.008) { _spikes++; }   // frames over the 8 ms budget
            int lc = _cdlod.LeafCountLastTick;
            if (lc < _chunkLo) { _chunkLo = lc; }
            if (lc > _chunkHi) { _chunkHi = lc; }
            if (!_cdlod.InvariantHoldsNow(out string m)) { _invOk = false; _invMsg = m; }
        }
        // _onFrame hook reserved here for future auto pop-detection (deferred).

        if (u >= 1f) { Finish(); }
    }

    private void Finish()
    {
        double avg = _accum / Mathf.Max(_frames, 1);
        GD.Print($"TESTPATH: {_paths[_active].Name}  avg={avg * 1000.0:F1}ms  worst={_worst * 1000.0:F1}ms  " +
                 $"spikes={_spikes}  chunks={_chunkLo}-{_chunkHi}  invariant={(_invOk ? "PASS" : "FAIL:" + _invMsg)}");
        bool quit = _cliQuit;
        _active = -1;
        if (quit) { GetTree().Quit(); }
    }
}
```

- [ ] **Step 3: Add CdlodTerrain accessors**

In `scripts/lab/CdlodTerrain.cs`, add a stored leaf count + an on-demand invariant check. Store the last leaves in `Tick` (add `private List<CdlodChunk> _lastLeaves = new();` and set `_lastLeaves = leaves;` after `Select`), then:

```csharp
    public int LeafCountLastTick => _lastLeaves.Count;
    public bool InvariantHoldsNow(out string msg) => _qt.NeighborInvariantHolds(_lastLeaves, out msg);
```

- [ ] **Step 4: Own TerrainTestPaths in TerrainLab + drive it**

In `scripts/lab/TerrainLab.cs`, add a field + creation (sibling, deferred — same as `_cdlod`) at the end of `Build()`:

```csharp
        _testPaths ??= new TerrainTestPaths { Name = "TerrainTestPaths" };
        if (_testPaths.GetParent() == null)
        {
            (GetParent() ?? (Node)this).CallDeferred(Node.MethodName.AddChild, _testPaths);
            // Setup needs the camera + cdlod; defer it one frame too (camera is a sibling under our parent).
            CallDeferred(nameof(SetupTestPaths));
        }
```

Add the field + setup + passthroughs:

```csharp
    private TerrainTestPaths? _testPaths;
    private void SetupTestPaths()
    {
        var cam = GetParent()?.GetNodeOrNull<Camera3D>("Camera");
        if (cam != null && _cdlod != null) { _testPaths?.Setup(cam, _cdlod); }
    }
    public void RunTestPath(int i, bool cliQuit = false) => _testPaths?.Start(i, cliQuit);
    public void TickTestPath(double delta) => _testPaths?.Tick(delta);
    public bool TestPathRunning => _testPaths?.Running ?? false;
```

- [ ] **Step 5: Keys 5/6/7 + per-frame tick in Process.cs**

In `scripts/lab/TerrainLabUI.Process.cs`, inside the `if (_ready)` block (after the key-1 toggle), add path keys + drive the tick. (Guard against ReviewMode owning number keys, like key 1.)

```csharp
            _terrain.TickTestPath(delta);   // S2b: advance an active test-path flight
            if (!ReviewMode)
            {
                if (Input.IsKeyPressed(Key.Key5) && !_lastKey5Down) { _terrain.RunTestPath(0); }
                if (Input.IsKeyPressed(Key.Key6) && !_lastKey6Down) { _terrain.RunTestPath(1); }
                if (Input.IsKeyPressed(Key.Key7) && !_lastKey7Down) { _terrain.RunTestPath(2); }
                _lastKey5Down = Input.IsKeyPressed(Key.Key5);
                _lastKey6Down = Input.IsKeyPressed(Key.Key6);
                _lastKey7Down = Input.IsKeyPressed(Key.Key7);
            }
```

Add the debounce fields near `_lastKey1Down`:

```csharp
    private bool _lastKey5Down, _lastKey6Down, _lastKey7Down;
```

- [ ] **Step 6: `--testpath=N` CLI**

In `scripts/lab/TerrainLabUI.Cli.cs`: field near `_cdlodCli`:

```csharp
    private int _testPathCli = -1;   // --testpath=N → run test path N (0-based) headlessly, then quit
```

parse (exact-stem; place near `--cdlod`):

```csharp
            else if (a.StartsWith("--testpath=")) { int.TryParse(a.Substring("--testpath=".Length), out _testPathCli); }
```

apply in `scripts/lab/TerrainLabUI.cs` — but it must run AFTER the deferred `SetupTestPaths`. Fire it deferred from `ApplyCliOverrides` context: add to `ApplyCliOverrides()` in `TerrainLabUI.Cli.cs`:

```csharp
        if (_testPathCli >= 0) { CallDeferred(nameof(StartTestPathDeferred)); }
```

and add the helper in `scripts/lab/TerrainLabUI.cs`:

```csharp
    private void StartTestPathDeferred() { _terrain.SetCdlod(true); _terrain.RunTestPath(_testPathCli, cliQuit: true); }
```

(`CallDeferred` runs next idle frame, after the `_testPaths` sibling-add + `SetupTestPaths` have completed.)

- [ ] **Step 7: Build (C#)**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly`
Expected: `0 Error(s)`.

- [ ] **Step 8: Run each path via CLI, confirm a report prints**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
for N in 0 1 2; do
  echo "=== testpath $N ==="
  "$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --testpath=$N > /tmp/tp$N.log 2>&1 &
  P=$!; for i in $(seq 1 20); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
  grep -E "TESTPATH:|TerrainTestPaths: START" /tmp/tp$N.log
done
```
Expected: each prints `TerrainTestPaths: START '<name>'` then `TESTPATH: <name> avg=… worst=… spikes=… chunks=lo-hi invariant=PASS`. The camera flew the path (the run quits itself via `cli_quit`). If a run hangs (>40s), a flag no-op'd — check the `--` separator + that `_testPaths` got set up (deferred timing).

- [ ] **Step 9: Commit**

```bash
cd /c/Wg16/wg-16-project
git add scripts/lab/TerrainTestPaths.cs data/terrain_test_paths.json scripts/lab/CdlodTerrain.cs scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.cs
git commit -m "$(cat <<'EOF'
S2b.2: LOD-crossing test harness (TerrainTestPaths) — keys 5/6/7 + --testpath=N

Flies the camera along 3 JSON-defined LOD-crossing paths (low-fast
flythrough / vertical descend-ascend / slow boundary-hover) so geomorph pops
can be eye-judged in motion and perf is repeatable. Reports per path:
avg/worst/spike ms + chunk-count range + the <=1-level invariant sampled
ALONG the path (upgrade over S2a's single-frame check). Keys 5/6/7 start them
live; --testpath=N runs path N then quits. Auto pop-detection deferred (the
_onFrame hook is reserved). Paths tunable in data/terrain_test_paths.json.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Reserved detail-fade curve (unused)

Define the single named distance-fade the future surfacing arc will ride, so it can't create a quality-pop. Unused now (the placeholder has no distance-varying detail).

**Files:**
- Modify: `shaders/ground.gdshader` — add `detail_fade()` + its band uniforms.

**Interfaces:**
- Produces (shader): `float detail_fade(float dist)` → 0..1; `uniform float detail_fade_near;`, `uniform float detail_fade_far;`.

- [ ] **Step 1: Add the reserved curve to `ground.gdshader`**

Near the other uniforms:

```glsl
// S2b: the ONE shared distance-fade curve future surface detail (textures/parallax) must ride, so all
// detail transitions share a single soft band (no quality-pop / no stacked rings). UNUSED today — the
// placeholder ground has no distance-varying detail yet. Reserved seam for the surfacing arc.
uniform float detail_fade_near = 400.0;
uniform float detail_fade_far  = 1600.0;
float detail_fade(float dist) { return 1.0 - smoothstep(detail_fade_near, detail_fade_far, dist); }
```

- [ ] **Step 2: Build + render check**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v q -clp:ErrorsOnly` → `0 Error(s)`.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 --auto-shot=C:/tmp/wg16shots/s2b_t3.png > /tmp/s2b_t3.log 2>&1 &
P=$!; for i in $(seq 1 9); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "SHADER ERROR.*ground|auto-shot ->" /tmp/s2b_t3.log | head
```
Expected: no `ground.gdshader` shader error (an UNUSED function must still compile), auto-shot written. (A Godot shader may warn about an unused function — that's fine; an ERROR is not.)

- [ ] **Step 3: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader
git commit -m "$(cat <<'EOF'
S2b.3: reserve the shared detail-fade curve (unused)

detail_fade(dist) + near/far band uniforms — the ONE distance-fade future
surface detail (textures/parallax) must ride so all detail transitions share
a single soft band (no quality-pop). Unused today (placeholder has no
distance-varying detail); reserved seam for the surfacing arc.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### THE EYE-GATE (after Tasks 1 & 2 — the arc's definition of done)

Hand the user the running scene to fly the 3 paths and judge pops in motion. This is NOT optional and NOT
claimable from auto-shots.

- [ ] **Step 1: Launch windowed for the user; leave it open**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --cdlod=1 &
```
Tell the user: **press 5 (low-fast flythrough), 6 (vertical), 7 (boundary-hover)** to fly each path; also free-fly low across LOD bands. `--lodviz=1` (relaunch) tints LOD levels so band edges are visible. Watch for: vertices snapping to new heights (elevation pop), surface detail jumping (quality pop — N/A yet, no detail), gaps at chunk borders (cracks).

- [ ] **Step 2: The gate (USER verdict)**

**PASS = the user confirms ZERO elevation pops, ZERO cracks, in motion across LOD bands, on all 3 paths.**
That is S2b's definition of done. If the user sees a pop:
- Elevation pop at LOD change → the morph window/band is mistuned (the morph isn't completing before the
  coarsen): re-derive `near_d`/`far_d` against the quadtree's actual `camDist < size*split_factor` rule, or
  widen the morph window. Iterate the shader (hot-compiles) until the user confirms pop-free.
- Crack at a seam → skirt depth or the ≤1-level constraint; deepen the skirt or check `--cdlodcheck`.
Do NOT proceed to S2c until the user confirms pop-free.

---

## Self-Review

**1. Spec coverage:**
- Spec §1 per-vertex geomorph (morphK, odd→even snap, sample-at-morphed-XZ, cam_world + band uniforms,
  spacing-scaled normal) → Task 1. ✅
- Spec §2 reserve the named detail-fade curve, unused → Task 3. ✅
- Spec §3 test harness (3 paths, keys + `--testpath=N`, per-path avg/worst/spikes/chunks + invariant-along-
  path, detection deferred/hook reserved) → Task 2. ✅
- Spec "the gate = user flies the paths, ZERO pops in motion" → THE EYE-GATE section. ✅
- Spec verification (build-C#-after-cs-edit, --fieldcheck/--cdlodcheck PASS, --profmove under budget) →
  every task's steps + Global Constraints. ✅
- Out of scope (correctly absent): auto pop-detection (hook only), real surfacing detail, streaming, proxy
  shadows (S2c). ✅

**2. Placeholder scan:** No TBD/TODO; every code step shows full code; every run step shows the command +
expected output. The eye-gate "iterate the shader until pop-free" is a real instruction (the morph-window
tuning), not a placeholder — the failure modes + fixes are named.

**3. Type consistency:** `TerrainTestPaths.Setup(Camera3D, CdlodTerrain)/Start(int,bool)/Tick(double)/Running`
consistent across Task 2 definition + `TerrainLab` passthroughs + Process/CLI callers. `CdlodTerrain`
new members `GridResolution`/`SplitFactorValue` (Task 1) + `LeafCountLastTick`/`InvariantHoldsNow(out string)`
(Task 2) consistent with their uses. Shader uniforms `cam_world`/`grid_n`/`split_factor` (Task 1) +
`detail_fade_near`/`detail_fade_far` (Task 3) consistent. `--testpath=` parse/apply + `RunTestPath(int,bool)`
consistent. ✅

## Notes for the executor
- **STALE DLL is the #1 trap:** `dotnet build WG16.csproj -c Debug` after EVERY `.cs` edit, before launching.
  If a C# change "does nothing," it's the stale DLL — not your logic.
- **The morph window is the thing most likely to need tuning** at the eye-gate. The math here morphs over the
  far half of the band `[size*split_factor, 2*size*split_factor]`; if pops persist, that window is the knob
  (it must complete the morph by the coarsen distance `size*split_factor`). Shaders hot-compile, so iterate
  fast without a C# rebuild.
- **Pop-freeness is ONLY the user's eye in motion** — never claim it from an auto-shot (the morph is near-
  invisible in a static frame anyway).
- **Concurrency:** stage only this plan's files; the sky lane edits `TerrainLabUI.*`/`CloudVolume.cs`/
  `cloud_sky.gdshader`/`project.godot` in parallel.
