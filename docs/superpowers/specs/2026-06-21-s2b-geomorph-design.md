# WG16 S2b — Per-Vertex Geomorph (the pop-free gate) + LOD-crossing test harness

Date: 2026-06-21. Status: SPEC (brainstormed; decisions to pillars + AAA + long-term-best per the
user's standing directive for this arc). Parent: `2026-06-21-s2-cdlod-quadtree-design.md` (§2 geomorph
mechanism). Follows: S2a (quadtree skeleton — chunks render, mechanical gate PASS, commits
e3595e7…fb981e6). Precedes: S2c (proxy shadows), then S3 (streaming).

## Why this arc (the graveyard)
S2a renders a CDLOD quadtree at 5.6 ms / 4.4× under budget, but it is **LOD-select only — it POPS**
(vertices snap when a chunk changes LOD; that's expected and gated mechanically in S2a, not by look).
**S2b adds the geomorph that makes those transitions invisible, and its gate is the USER's eye, in motion,
crossing LOD bands: ZERO pops.** This is the exact failure mode that killed WG1–15 fifteen times
(elevation pops + quality pops). It is the make-or-break arc; the spec's discipline reflects that.

## Decisions (to pillars + AAA + long-term-best)

### 1. Per-vertex geomorph in the vertex shader (NOT per-chunk)
- **The mechanism.** A chunk is a unit grid (S2a `CdlodMesh`) scaled to its world size. Its vertices split
  into **even** indices (present in the next-coarser LOD) and **odd** indices (absent there). The pop is
  the odd vertices vanishing when the chunk coarsens. Geomorph slides each odd vertex continuously onto the
  line between its two even neighbors as the camera nears the coarsen distance, so by the moment the LOD
  actually switches, the odd vertices already sit on the coarse grid → the switch is invisible. C0-continuous.
- **Per-VERTEX morphK (the load-bearing choice).** Each vertex computes its own `morphK ∈ [0,1]` from its
  distance to the camera within this chunk's LOD distance band (0 at the band's near edge = just subdivided;
  1 at the far edge = about to coarsen). **Per-vertex, NOT per-chunk** — because adjacent chunks' shared
  edge vertices then compute the same morphK from the same distance and agree exactly, making the morph
  continuous ACROSS chunk boundaries. Per-chunk morphK (one value per chunk) would leave a discontinuity at
  chunk seams — reintroducing the exact pop class we are killing. This is why Strugar's CDLOD is per-vertex;
  it is not a refinement, it is the pop-free property. Perf cost (a distance + smoothstep per vertex) is
  negligible next to the 3 `analytic_h` field evals already in the chunk vertex path, and S2a left 2.4 ms
  of budget headroom — so "performance favors per-chunk" is false here.
- **Why structurally pop-free (not tuned):** the morphed vertex samples the SAME analytic `field_height`
  the coarser chunk samples. There is no stored second heightmap to disagree — the fine surface morphs onto
  the EXACT height the coarse surface shows. The elevation pop cannot occur. (Payoff of S1's analytic path.)
- **Shader additions (small):**
  - A **camera world-position uniform** on `ground.gdshader` (the strip removed `cam_world`; add it back
    cleanly — used only by the chunk branch for per-vertex distance).
  - The chunk's **LOD band** (near/far distance for this level) — derive from chunk size
    (`length(MODEL_MATRIX[0].xyz)`) × the quadtree `splitFactor`, or pass as a uniform from `CdlodTerrain`
    (which owns `splitFactor`). Pick whichever keeps the shader/C# split clean; the band must match the
    quadtree's actual split rule (`camDist < size*splitFactor`) so the morph completes exactly as the chunk
    coarsens.
  - In `vertex()` chunk branch: compute `morphK` → lerp the vertex grid XZ toward its coarse-grid position
    by `morphK` (snap odd toward even: e.g. `floor(gridCoord/2)*2` target) → sample `field_height` at the
    **morphed** world XZ. The normal taps sample at the morphed position too (so shading matches geometry).
- **Skirt interaction (S2a):** the skirt drops border (even/shared) verts' final Y; geomorph moves interior
  XZ. They compose — verify they don't fight (border verts morph minimally since they're shared even verts).
- **The chunk-normal epsilon follow-up (from the S2a review):** the chunk normal currently uses
  `e = analytic_spacing` (4 m) but chunk verts are `chunkSize/64` apart (16 m+ at coarse LODs). S2b reworks
  the vertex path anyway, so fix it here: the normal finite-difference epsilon should scale with the chunk's
  actual vertex spacing (`chunkSize / (GridN-1)`), sampled at the morphed position.

### 2. Detail cross-fade — reserve the named curve only (YAGNI)
The parent spec requires all distance-varying SURFACE detail to blend on ONE shared distance curve (the
quality-pop killer). But the placeholder ground has NO distance-varying surface detail yet (only a
height/slope color ramp, identical near and far) — so there is **no quality-pop to fix in S2b.** S2b
**defines the single named fade** (a `detail_fade(dist)` → 0..1 on a tunable near/far band + its uniform)
as the reserved seam the future surfacing arc will ride, and leaves it **unused**. Zero visual change, zero
quality-pop work. S2b's pop-fighting is entirely the Section-1 geometry geomorph.

### 3. The LOD-crossing test harness (drive + measure; detection deferred)
A reusable, tunable module that flies the camera along scripted LOD-crossing paths so the user can eye-judge
pops in motion AND get repeatable perf — built for S2b's gate but designed to outlive it.
- **`TerrainTestPaths` component** reading `data/terrain_test_paths.json` (tunable: waypoints, speed,
  duration, per-path thresholds) — mirrors the existing `lab_controls.json` / 1-9 review-preset pattern.
- **Three starter paths**, each targeting a geomorph failure mode:
  1. **Low-fast flythrough** — low + fast, horizontal across many LOD rings (worst-case pop stress).
  2. **Vertical descend/ascend** — drop/rise through LOD bands over a fixed point (altitude-driven LOD).
  3. **Slow boundary-hover** — creep at a single LOD boundary (isolate one transition for close inspection).
- **Driven both ways (user's call):** **keys** (start a path live in-scene while watching, like 1-9) AND a
  **`--testpath=N`** CLI flag (run path N, print report, quit — repeatable logged regression).
- **Measures (the upgrade over S2a's single-frame check):** per-path avg / worst / spike-count ms, chunk
  count range, and the ≤1-level invariant checked **along the path** (not one static frame). Prints
  `TESTPATH: <name> avg=… worst=… spikes=… chunks=…-… invariant=PASS/FAIL`.
- **Detection DEFERRED (pillars):** automated pop/crack detection is NOT built. A detector must be validated
  against the user's eye before it can be trusted, and no real pop has been seen in the data yet. The harness
  **reserves the hook** (a per-frame callback slot) but ships drive+measure only; detection gets calibrated
  to a real pop after the S2b eye-gate reveals what one looks like.
- **Boundaries (clean unit):** reads the camera + `CdlodTerrain` (chunk count / invariant), drives the camera
  transform, prints the report. Does NOT touch geomorph, the field, or the sky lane.

## Staged build (commit per step — send-it/revert)
- **S2b.1 — geomorph.** Per-vertex morphK + odd→even XZ morph + sample-at-morphed-XZ + the spacing-scaled
  normal epsilon, in `ground.gdshader`'s chunk branch; `cam_world` uniform + band wiring from `CdlodTerrain`.
  Commit when it builds + `--fieldcheck` PASS + renders.
- **S2b.2 — test harness.** `TerrainTestPaths` + `data/terrain_test_paths.json` (3 paths) + keys + `--testpath=N`
  + the per-path report (avg/worst/spikes/chunks/invariant-along-path). Commit when it drives + reports.
- **S2b.3 — reserved detail-fade curve.** The `detail_fade` function + uniform, unused. Commit.
- **THE GATE (after S2b.1+S2b.2):** the **user flies the 3 test paths in `terrain_lab.tscn` and confirms ZERO
  elevation pops, ZERO cracks, in motion across LOD bands.** Numeric backstop: morphK is C0-continuous across
  band boundaries (assert no discontinuity). One visible pop → do NOT proceed; fix the morph. This is the
  arc's definition of done.

## Verification (NO TDD — GPU/visual; gate viewport = `scenes/terrain_lab.tscn`)
- **⚠ Build C# after EVERY `.cs` edit** (`dotnet build WG16.csproj -c Debug`) before launching — the player
  binary does NOT rebuild C# (memory `wg16-csharp-stale-dll-gotcha`; cost a session). Shaders hot-compile.
- **Build clean** → `0 Error(s)`. **`--fieldcheck` stays PASS** (field math untouched — skin not bones).
- **`--cdlodcheck` PASS** (invariant intact). **`--testpath=N`** runs each path, report printed.
- **`--profmove`** stays under 8 ms (geomorph adds negligible vertex cost). Also address the S2a transient
  worst-frame spike (~42 ms on quadtree-boundary chunk-set rebuilds) if it's cheap to amortize here.
- **THE eye-gate:** user flies the 3 paths in motion → ZERO pops/cracks. Never claimed from a downscaled
  still (`ground-texture-feedback`). Run rules: user `--flags` after a bare `--`; `--auto-shot` needs an OS
  path; ONE Godot at a time; scene can't run `--headless`; analytic-slow profiles need a generous wall-clock
  poll (memory `wg16-cli-run-invocation`).

## Definition of done
S2b.1 (geomorph) + S2b.2 (test harness) + S2b.3 (reserved curve) committed; the **user confirms ZERO pops in
motion across LOD bands flying the 3 test paths** (+ morphK C0-continuity backstop). Then S2c (proxy shadows,
fenced to terrain geometry flags only).

## NOT in scope (YAGNI / deferred)
- Automated pop/crack detection (hook reserved; calibrate after the eye-gate shows a real pop).
- Actual surface detail / textures riding the cross-fade curve → the surfacing arc (S2b only reserves the curve).
- Streaming / camera-following root / floating origin → S3/S4.
- Any change to the field math or the sky/light system. Proxy shadows → S2c (separate, fenced).
