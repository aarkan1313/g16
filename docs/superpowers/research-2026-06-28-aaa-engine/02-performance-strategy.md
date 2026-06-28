# 02 · Performance Strategy — all features ON, medium-high, under 8 ms

The user's lofty target: **every feature on at medium-high quality inside the 8 ms whole-frame
budget.** Today we're at ~10 ms flying with NO trees/caves/roads/POIs. This doc says how the math
can still close — and it leans hard on the lesson `performance.md` already proved: **the cheap
config dials are exhausted; the wins are architectural (bake / cache / presolve / budget).**

## The governing principle: pay once, not per-frame

The field-cache result is the whole strategy in miniature: live 5×/vertex field eval cost 10.7 ms;
baking it once on chunk birth cost 7.2 ms (−33%), pixel-identical. **Every expensive thing that is
stable across frames should be solved into a cached resource, then sampled.** This is also exactly
why `03` recommends the hybrid macro-cell presolve: drainage, road graphs, POI placement, biome
fields are all *stable* — solving them live every frame is the trap; solving them once per
macro-cell and sampling a cached texture/buffer is the budget fix AND the correctness fix at once.

## Step 1 — reclaim the current overspend (get to ~7 ms, features-off floor)

Measured, already-validated levers from `performance.md` (no new research needed):

| Lever | Saving | Notes |
|---|---|---|
| **Cut SSAO** (replace later w/ analytic horizon AO) | **−1.8 ms** | invisible on placeholder terrain; quality dial-down is a no-op (fixed pass cost) |
| **Cloud steps 128→32** | **−1.0 ms** | 32 is the knee; sky pillar must re-gate |
| **Analytic-gradient normal** (kill 4 of 5 field evals) | **est −0.5–1 ms** | also a quality gain (exact normals); gate `--fieldcheck` |
| **Adaptive far-chunk grid** (65→33/17 far) | **est −0.3–0.8 ms** | graveyard-adjacent; gate stitch/morph |
| **Shadows: drop finest LOD from far cascades** | **est −0.5–1 ms** | atlas/distance are dead no-ops; this is the real shadow lever |

Quick win already named: cut SSAO + cloudsteps=32 = **−2.8 ms → ~7.2 ms** with zero new tech. The
architectural levers (normals, far grid, shadow casters) are the **mid-range headroom** (×2.5–4) —
they matter most off the 5090. **Target after Step 1: ~6 ms features-off on the 5090.** That ~2 ms
of headroom is the budget pool for everything new.

## Step 2 — give every new module a budget and a "pay-once" tier

The 8 ms is a shared pool. Each module gets an explicit **medium-quality budget** and a tier
(pure-function / presolved-cached / streamed-instances). Proposed allocation (5090 ms; ×2.5–4 mid):

| Module | Budget (med-q) | How it stays cheap |
|---|---|---|
| Base terrain + CDLOD + shadows | 4.0 | Step-1 levers; far-cascade caster cull |
| Sky / atmosphere / clouds | 1.0 | already efficient; cloudsteps=32 |
| **Flora** (grass + trees) | **1.2** | GPU instancing + impostors beyond N m; density LOD; **placement baked per chunk on birth** (like field cache), not computed per frame |
| **Water render** (rivers/lakes) | **0.5** | mesh ribbons + screen-space, not volumetric; flow from cached substrate |
| **Caves** (when underground only) | **0.6** | sparse 3D chunks streamed only near player; surface cost ≈ 0 when above ground (the always-off win) |
| **Roads** | **0.3** | baked mesh + splat decal from presolved graph; no per-frame pathfinding |
| **POIs** | **0.4** | streamed instances + impostors; authored chunks are static scenes |
| Overhead / spikes margin | ~0 (reserve) | the worst-case spike is the real enemy, not avg |

Sum med-q all-on ≈ **8.0 ms on the 5090.** Tight but not impossible — *because most modules are
pay-once.* Flora placement, road meshes, POI layout, drainage all become **cached resources baked
at chunk/macro-cell birth**, so the per-frame cost is rendering instances + sampling textures, not
solving. This is the entire reason the lofty target is reachable.

## Step 3 — the spike, not the average, is the boss fight

`performance.md` is blunt: avg is already near budget; the **20 ms worst-case spikes** in motion
are shadow-map re-raster on chunk birth/morph. More modules = more birth-time work = bigger spikes
unless birth work is **throttled and amortized** (the `MaxRequestsPerFrame` / bake-throttle pattern
the field cache already uses). Rule for every new streamed module: **birth work is render-thread
async, throttled per frame, with a velocity-predictive lookahead** so it lands before it's seen.
Caves/roads/POIs/flora all inherit this contract from `ChunkFieldCache`.

## Step 4 — quality tiers as data (the mid-range story)

Medium-high on a 5090 is low-medium on a laptop iGPU. The engine already has `--loadring`,
`--cloudsteps`, `--shadowatlas` knobs. Formalize a **quality preset axis** in the registry
(Ultra/High/Medium/Low) that scales: load ring, flora density + impostor distance, cave stream
radius, shadow cascade count, cloud steps, presolve resolution. The 8 ms target is **per quality
tier**, not absolute — Medium holds 8 ms on mid-range, Ultra holds it on the 5090. Game-agnostic
means the consuming game picks the tier.

## Step 5 — where Rust / GPU-compute enter (the ladder, applied)

Per `TECH_STACK.md`: C# → GPU compute → Rust, only on measured bottlenecks. The likely candidates,
flagged now so we measure before porting:

- **GPU compute (high confidence):** flora placement bake, road splat bake, cave SDF eval, drainage
  macro-solve (the parallelizable flow-accum), biome field bake. All per-cell/per-texel → GPU.
- **Rust (only if measured):** the **serial** parts — drainage flow *routing* (ordered, the exact
  thing WG15's Rust port hit: 2.6× not 10× because routing is serial), road A* pathfinding, POI
  constraint solving. Build in C# first, port only if a presolve scheduler stalls.

## The honest risk

This budget is achievable on a 5090 and *tight* on mid-range. The two things that could blow it:
(1) **spikes** from too much birth work landing at once — mitigated by throttling + lookahead +
presolve-ahead; (2) **memory** from caves (3D) + caches + deltas — mitigated by sparse streaming
(only near the player) and the always-off module rule. If both mitigations underperform for a given
module, that module is the candidate for the `03` per-module bounded-world fallback. The strategy is
sound; the discipline is in honoring "pay once" and "throttle birth work" for every single module.
