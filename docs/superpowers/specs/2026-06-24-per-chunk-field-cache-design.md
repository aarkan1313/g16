# Per-chunk field cache (GPU-compute) — Design

**Date:** 2026-06-24
**Status:** ✅ BUILT + SHIPPED 2026-06-24 (default-on). Result: flying 10.7→7.2 ms (−33% avg / −38% worst),
quality-identical, all gates PASS. See `performance.md` ARC A-3 + `DECISIONS.md`. One deviation from this design:
the bake writes via **imageStore-direct (no readback)** — the planned buffer+readback made FLYING slower (the
GPU sync churned during streaming) — and the cache does NOT subsume the AABB (the cheap 7×7 probe stays). Owed:
user in-motion eye-gate. APPROVED 2026-06-24 — decisions resolved: BAKE the normal (AAA, quality-identical),
build ALL-AT-ONCE, no cache invalidation (field params are static at launch). User directive: "all-out
performance, GPU compute, not lowering quality."
**Goal:** Eliminate the dominant per-frame terrain cost — the CDLOD chunk vertex shader evaluating the full
multi-octave procedural field **5× per vertex every frame** (~12M field evals/frame) — by **baking each chunk's
height+normal once on birth via GPU compute** into a texture array and **sampling** it every frame. Quality-
identical (same field math; the same fixed-step normal, cached). Expected: removes most of the ~3.7 ms base floor.

## Background — the exact cost (verified in code)

`shaders/ground.gdshader` `vertex()`, `use_chunk` branch (lines ~245-269):
- `h0 = analytic_h(wxz) - carve_offset(wxz)` (line 248) — height at the geomorph-morphed world XZ.
- 4 MORE `analytic_h()` taps (`hxp/hxm/hzp/hzm`, lines 265-268) at ±`analytic_spacing` for the central-difference
  normal — a **fixed, LOD-independent** step (deliberate: shading continuity across LOD swaps, the hard-won fix).
- Each `analytic_h` → `field_height` (`field_math.gdshaderinc:122`) = continent (5-oct warped fbm) + uplift (3×
  fbm + warps) + slope_damped_fbm (6 oct) + oriented_ridges (6-oct ridged + warp).

GridN=65 (4225 verts/chunk) × ~570 live chunks × 5 evals = **~12M full multi-octave field evals/frame**, in the
vertex shader, recomputing a **static** field. Measured: the bulk of the 3.7 ms base floor (which is vertex-bound,
not fragment — confirmed by the `--shadow=0 --clouds=0 --ssao=0` decomposition).

**The field is static at runtime** (no field-param sliders in `data/lab_controls.json`; `FieldParams` is set once
in `TerrainLab.Build`, never re-baked live). So a per-chunk cache **never needs invalidation**.

## The existing infra this extends (the decisive de-risk)

`scripts/lab/ChunkAabbProvider.cs` is **already 90% of this system**:
- On chunk birth (`CdlodTerrain.cs:201` `_aabbProvider.Request(key, originXZ, size)`), it dispatches
  `field_height.glsl` over a `ProbeRes×ProbeRes` (7×7) grid covering the chunk footprint, **async on the render
  thread** (`CallOnRenderThread`), **throttled** (`MaxRequestsPerFrame=8`), **chunk-keyed**, with a **born-
  generous → refined-async** landing model (the AABB tighten).
- It uses `FieldCompute.PackParamsBytes` so the bake math is **byte-identical** to the live shader field path.

The cache **extends** this: change the dispatch from 7×7-keep-min/max to **(GridN)×(GridN)-keep-the-grid**, write
height+normal into a `Texture2DArray` slice, hand the slice index to the chunk. The async plumbing, throttle,
dedup, keyed landing, and param packing are all **reused unchanged**. It is finishing a system that already
exists and currently discards the grid it computes.

## Design — components

### 1. The bake (compute side)
- A compute shell (clone/extend `field_height.glsl`) dispatched over a **(GridN+2)×(GridN+2)** grid per chunk —
  the +1 border ring each side lets the **normal** be central-differenced at the fixed `analytic_spacing` step
  using in-grid neighbors (no out-of-tile sampling), and supports the geomorph coarse-texel lookup at edges.
- Per grid point it computes: **height** = `field_height(worldXZ)` and **normal** = the SAME fixed-step central
  difference the live shader uses (4 field taps at ±`analytic_spacing`), so the cached normal is **byte-identical**
  to today's and stays LOD-independent. (This means the bake does ~5 field evals/point, ONCE on birth — amortized
  — vs 5/point/frame live.)
- `carve_offset` (water carve, default-OFF) is **NOT** baked — it stays a live per-vertex add after the texture
  sample (one cheap texture fetch), preserving `--fieldcheck 0m` and avoiding a cache-invalidation trigger.

### 2. The texture-array slot manager (`CdlodTerrain`)
- A `Texture2DArray` with one slice per pool slot. **Format:** R32F height + a separate RGBA8 (or RG16) normal
  array, OR a single RGBA32F (R=height, GBA=normal) — chosen in the plan by precision/VRAM (≈12–40 MB at 570
  chunks; negligible). Normal stored as world-space unit vector (or xz with y reconstructed, y>0 on terrain).
- The existing `_free`/`_active` slot pool maps to array layers: a retired chunk's layer is reused by the next
  birth (the `cache_ready` flag gates use until the new bake lands — covers the recycle race).
- The slice index is pushed per-instance via `SetInstanceShaderParameter("chunk_slot", i)` — the SAME mechanism
  as the existing `lod_viz` (`ground.gdshader` / `ApplyChunk`).

### 3. The vertex shader (`ground.gdshader`)
- New uniforms: `sampler2DArray chunk_cache_h`, `chunk_cache_n` (or one array); per-instance `chunk_slot`,
  `cache_ready`.
- When `cache_ready`: `h0 = mix(texelH(u_fine, slot), texelH(u_coarse, slot), morphK) - carve_offset(wxz)` (the
  standard two-sample geomorph blend — both heights are static and present in the baked fine grid); normal =
  `mix(texelN(u_fine,slot), texelN(u_coarse,slot), morphK)` (normalize). **Zero field evals.**
- When NOT ready (the few frames after birth): the **existing live path** (5 evals) runs unchanged — the dual
  path. Same values → no pop on the flip.

### 4. The async landing + flag
- `Request` queues the bake; `Pump`/`DrainTightened`-style drain marks the slot `cache_ready` + uploads the slice
  (render-thread, like the AABB tighten landing). Mirror the existing landing exactly.
- `public bool FieldCache = true;` on `CdlodTerrain` (reversible, like `TightenAabb`) + `--fieldcache=0` CLI to
  A/B. Off → the live path everywhere (today's behavior).

## Verification

No unit tests (Godot lab). Per the build: green; the mechanical gates stay PASS — **`--fieldcheck`** (the cached
height must match the live field at the sampled XZ to ~0 m), **`--popcheck`** (no height/normal pop across LOD
swaps — the cached normal is the same fixed-step FD), **`--morphcheck`** (geomorph two-sample blend correct),
**`--stitchcheck`** (edge-stitch variants sample the cache correctly), **`--snapdiff`**/**`--streamcheck`**
(streaming + floating-origin unaffected). Plus a **drift-free in-motion eye-gate** (`--profmove`): the terrain
must look IDENTICAL with `--fieldcache=1` vs `=0` (it's the same math). And a **re-profile** (`--profmove
--profile=8 --profspeed=800`) quantifying the base-floor saving + the per-birth bake cost (watch the render-thread
birth-burst spike; may need to lower the bake's `MaxRequestsPerFrame` vs the 7×7 probe's).

## Non-goals / out of scope

- Analytic-gradient normal (a smoothness *gain*) — we bake the EXACT current fixed-step FD normal for quality
  identity; analytic gradient is a future enhancement on the bake side.
- The other perf hedges (horizon-march cache, MSAA→TAA, AABB batching, fragment weight-gate) — separate items in
  `REVIEW-2026-06-24` Area 5.
- Field-param live editing / cache invalidation (the field is static; out of scope by construction).
- Any change to the geomorph/stitch/snap algorithms (they're correct; the cache only changes the data SOURCE).

## Risks

1. **Per-birth bake cost / render-thread spike.** A (GridN+2)² dispatch is ~86× the 7×7 probe; on a birth burst
   (fast flight) the render-thread bake could spike. Mitigation: its own throttle (lower `MaxRequestsPerFrame`
   for the heavier bake), measured in the re-profile; the live-eval dual-path covers un-baked chunks so a slower
   fill is invisible (no holes).
2. **Geomorph correctness** — the two-sample blend must reproduce CDLOD geomorph exactly. Gated by `--morphcheck`/
   `--popcheck`. The coarse texel = the even-snapped index in the same baked fine grid (in-range via the +1 border).
3. **Slot/layer recycle race** — a reused layer must finish its new bake before sampling; the `cache_ready` flag +
   live fallback handle it.
4. **"No bake" philosophy** — resolved (user approved): a per-chunk async transient cache, not a global offline
   bake; the AABB provider already established the pattern. Reversible via `--fieldcache=0`.
