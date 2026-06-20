# GROUND — AAA Anti-Tiling (histogram-preserving tiling & blending)

Date: 2026-06-20. Lane: GROUND / TEXTURE (Phase A). Fits the ground roadmap as the **Layer 4 —
Blending/compositing** anti-repetition upgrade (`2026-06-20-ground-roadmap-to-aaa-design.md`).
Status: **design (this doc) → plan → build behind a toggle → live eye-gate.**

## Why this exists (the eye-gate finding)

Driving the GM1 palette eye-gate (review key 3) live, the user judged: *"textures are OK,
implementation/everything around them sucks — artifacting, clear tile-to-tile chunk lines, weird
color differences."* Isolated live (`splat debug` + per-toggle A/B) and confirmed in shader code:

1. **Macro color** value-noise blotches — already fixed (`macro_on` default OFF, commit `52d1017`).
2. **Blocky tile-to-tile seams** — the `tile_mode=1` **IQ 2-tap** path (`iq_sample`): its blend
   factor `f` is **constant per `floor(uv)` cell** at `tex_scale_m ≈ 28 m`, so it jumps at every
   tile-cell boundary → hard stair-step blocks aligned to the 28 m grid, worst up close. Setting
   `tile_mode = none(sharp)` removes the blocks but reintroduces visible **repetition** — proving
   the two are the same dial. The existing alternatives all seam: `hex_sample` ("tile-seam squares"
   per its own code comment, from per-tile rotation) and the variance-preserving `ar_sample_wp`
   (used only for AO) shares that rotation seam on high-contrast channels.

The roadmap had Layer-4 anti-repetition marked "approved (Unit 1)"; the live eye proved it is **not
AAA at close range.** This is a foundation repair, not new scope — and foundational: GM2 height,
GM3-A variation, and GM5 detail all render *through* this sampler, so it must land before the
GM1/2/3-A batch can pass its gate.

## Pillars & the gate

- **PILLARS:** quality = performance = AAA-ish = best-long-term, regardless of time cost. Lead with
  the most-correct technique (user steer: *"whatever we gotta do to be AAA and follow pillars"*).
- **The user's live eye is the only look-gate.** Build behind a toggle defaulting to the current
  look; A/B against it. Judge in motion at close/mid/far. Never judge from a still.
- **Perf is co-equal:** 8 ms in-motion budget for the whole generator. The technique adds taps, so
  cost is a hard gate (`--profmove`), mitigated by the existing branched-triplanar skip + distance
  LOD — not an afterthought.

## North star — what "done" means

Fly close/mid on a uniform slope and on a cliff: **no blocky seams, no obvious texture repetition,
materials keep their full contrast/punch** (no muddy blend wash), gradual organic transitions, no
shimmer in motion, far view unchanged — and the `tile_mode` A/B vs the old IQ look is a clear win,
inside the perf budget.

## The technique — Deliot & Heitz, *Procedural Stochastic Textures by Tiling and Blending* (2019)

Two halves. The math fact it exploits: **a linear blend of two texture samples loses variance**
(the documented "muddiness"); blending instead in a **Gaussian-distributed space** and mapping back
through the texture's inverse histogram preserves the **exact original histogram** — no contrast
loss, and the stochastic offsets hide repetition with **no seam** (no per-tile rotation, no per-cell
blend jump).

### Bake (once per bound material, on the GPU)
Per texture channel: build a **256-bin histogram → CDF**, emit a forward transform `T`
(value → Gaussian quantile via the CDF) and its inverse `T⁻¹` (Gaussian → value) as **1D LUTs**.
Only the **7 currently-bound** zone materials are processed (not the 2.5 GB library), so VRAM/bake
cost is small. Re-baked on startup and whenever a zone material changes.

### Runtime (per fragment, the new `tile_mode = 3`)
1. **Triangle-grid tiling** (reuse the existing `tri_grid`): 3 nearest lattice vertices with
   barycentric weights `w1,w2,w3`, each vertex giving a **random translational UV offset** (hash) —
   **no per-tile rotation** (that's what made hex seam).
2. Sample the 3 offset tiles; transform each to Gaussian space via `T` (forward LUT).
3. **Variance-preserving blend:** `G = (Σ wᵢ·Gᵢ − μ)/√(Σ wᵢ²) + μ`.
4. Map back to texture space via `T⁻¹` (inverse LUT). Output keeps the original histogram.

## Architecture

### Component 1 — `HistogramCompute.cs` + `shaders/tile_histogram.glsl` (the bake)
Mirrors `HeightCompute.cs`/`SplatCompute.cs` (same windowed local-RD pattern — **will NOT run under
`--headless`**, per the project gotcha; bake windowed). Input: the 7 bound albedo textures. Output:
the forward+inverse LUTs. To avoid a sampler explosion, **all LUTs pack into ONE small atlas
texture** `tile_lut` (rows = material × channel × {T, T⁻¹}, width = 256) — one extra sampler, not
~28. Bound to the terrain material as `tile_lut`. Driven from `TerrainLab.cs` at startup and on
`SetZoneMaterial`.

### Component 2 — unified `sampleMaterial` seam in `terrain_lab.gdshader` (the structural fix)
Today sampling is fragmented: albedo/normal/rough → `tiled()`/IQ (blocky); AO → `ar_sample_wp`
(variance-preserving). Collapse all maps onto **one sampler entry** that runs the no-rotation
triangle grid. That alone removes the blocky IQ seams. Per-channel treatment (v1):
- **Albedo:** full **histogram-preserving** (forward `T` → blend → inverse `T⁻¹` via `tile_lut`).
- **Normal / roughness / AO:** **variance-preserving** blend (the approved `ar_sample_wp` math),
  no LUT — cheaper and correct enough for those channels.
The existing **branched-triplanar skip** (`TRI_EPS`, ~1 plane on flatish ground) and **distance LOD
to 1 plain tap** are preserved inside the unified seam.

### Component 3 — the toggle / A/B
New `tile_mode = 3` ("histogram"); `tile_mode = 1` (IQ) stays the default until the eye-gate passes,
so the current look is one keypress away and the change is fully reversible. Lab control
(`data/lab_controls.json`, Surface tab `tile mode` enum gains option 3) + `tex_scale_m` stays a
live tunable. Keep `ar_strength`/`tri_sharp` knobs meaningful.

## Data flow
`TerrainLab.SetZoneMaterial(z, name)` → bind albedo/normal/rough/ao/(hgt) → **HistogramCompute**
bakes `T`/`T⁻¹` for that material's albedo into `tile_lut` row → terrain material samples via the
unified `sampleMaterial` (tile_mode 3) → histogram blend (albedo) / variance blend (others) →
interlock/compositing downstream unchanged.

## Scope (v1) & explicit non-goals
**In:** the bake, the LUT atlas, the unified sampler seam, `tile_mode=3`, albedo full-LUT +
normal/rough/ao variance-preserving, the toggle/tunables, perf LOD.
**Out (separate roadmap phases, not bundled):** within-area **variation** (GM3-A), **macro
variation done-right** (revisit later), **tex_scale / scale** feel as a campaign (GM6), **lighting**
wash (Sun/Light lane), real-heightblend interlock sharpening (rides GM2). Roughness/AO full-LUT is a
**noted refinement** if a tonal seam shows on those channels — not v1.

## Perf posture
Triangle grid is 3-tap vs IQ's 2-tap (+50% on the near band), ×2 materials (dom+sec), ×1–3 planes.
Mitigation already in the shader: branched-triplanar (1 plane on flatish ground) + distance LOD to a
single plain tap. Gate on `--profmove` vs 8 ms; if near-cliff (3-plane) is too heavy, tighten the
LOD band first. The forward/inverse LUT taps are 1D and cheap.

## Risks / unknowns (verify early in the plan)
- **Sampler bind count:** ~+8 (one `tile_lut` atlas; originals already bound). Confirm against
  Godot's Vulkan limit before building wide.
- **Mip prefiltering** of a Gaussianized signal is approximate at extreme distance — we LOD to a
  plain tap there, so it's a non-issue now; flagged if it ever shows (Deliot-Heitz "blend toward the
  mean at high mip" is the later fix).
- **Structured (non-stationary) textures** (e.g. columnar basalt) suit tiling-and-blending less than
  stochastic ones (sand/gravel/cracked-clay); judge per-material at the gate, fall back to IQ on any
  material it hurts (the toggle makes this per-material-tunable later if needed).
- **Bake correctness:** verify the baked `T⁻¹(T(v)) ≈ v` round-trips (a `--auto-shot` of a known
  material A/B vs plain sample) before trusting the look.

## Files
- New: `scripts/lab/HistogramCompute.cs`, `shaders/tile_histogram.glsl`.
- Edit: `shaders/terrain_lab.gdshader` (unified `sampleMaterial`, `tile_mode==3`, `tile_lut`
  uniform), `scripts/lab/TerrainLab.cs` (drive the bake on bind/zone-change), `data/lab_controls.json`
  (Surface `tile mode` enum +option 3 — small, additive). No edits to cloud/lighting files.

## Gate
Live eye-gate via `scenes/review.tscn` (extend key 3/9 baseline, or a dedicated A/B): toggle
`tile mode` IQ ↔ histogram at close/mid/far on a uniform slope and a cliff. PASS = the north-star
criteria met inside the perf budget. On PASS: flip `tile_mode` default → 3, **retire the superseded
IQ / hex / legacy-50-50 anti-tile paths** (user steer — kill dead code, don't keep dead toggles;
keep only `none(sharp)` as the raw-repetition reference), update the ground roadmap Layer-4 entry,
then re-judge the GM1/2/3-A batch on the fixed surface. (Reversibility during the gate is via git, not
a permanent toggle zoo.)

## Self-review notes
- **Scope:** single implementation plan's worth — one bake unit + one shader seam + a toggle. Not
  decomposed further; the roughness-LUT and per-material-fallback are explicitly deferred.
- **Placeholders:** none — every component has inputs/outputs/owner.
- **Consistency:** v1 channel split (albedo-LUT / others-variance) is stated identically in
  Architecture, Scope, and Perf. Reversibility (default IQ until gated) stated once and honored.
- **Ambiguity:** "AAA" pinned to the north-star checklist (seams/repetition/contrast/shimmer), not
  left subjective; the user's live eye is the decider.
