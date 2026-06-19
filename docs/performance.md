# WG16 Performance — profiling + optimization pass (2026-06-18)

The reference for "where the frame goes" and what's been optimized. Numbers are RTX 5090
laptop, **uncapped** (`--profile=N` disables vsync + uncaps fps), so absolute ms are dev-machine
values — the **relative costs and before/after deltas** are the takeaway. Scale ~2.5–4× for a
mid-range GPU on this fragment/compute-bound work.

How to reproduce: `scenes/terrain_lab.tscn --rendering-driver vulkan -- --profile=4 --cam=0,350,0,-25,40 [flags]`.
Profiling probes: `--clouds=0/1`, `--godrays=0/1`, `--temporal=N`, `--cloudtex=H`, `--ssao/--shadow/--sdfgi/--ar/--hb=0/1`.
Caveat: the laptop GPU thermally throttles after many back-to-back runs — space runs out / trust
reproduced numbers, and treat single wild outliers as hitches.

## 2026-06-19 — IN-MOTION profiling (major correction) + GI/shadow proxy

**The static `--profile` was measuring the wrong thing.** A still camera lets SDFGI converge,
shadows settle, and cloud temporal idle — so it badly *understated* the real cost of FLYING. Added
**`--profmove`** (orbits the camera during `--profile`) to capture motion cost. Reproduce:
`--profmove --profile=4 [--clouds= --giproxy= --groundrules=]`.

**In-motion decomposition (1440p, moving, clouds off):**

| State | ms | fps | Δ vs baseline |
|---|---|---|---|
| baseline (moving) | 18.3 | 55 | — |
| `--sdfgi=0` | 6.1 | 163 | **SDFGI ≈ 12 ms IN MOTION** (≈0.2 ms static!) |
| `--shadow=0` | 15.3 | 65 | shadows ≈ 3 ms moving |
| sdfgi+shadow off | 3.9 | 257 | — |

**Root cause:** SDFGI re-voxelizes — and shadows re-raster — the **4M-vertex un-LOD'd mesh** every
frame as the camera translates. Resolution barely matters (4K ≈ 1440p) → the frame is
**geometry/CPU-bound, not pixel-bound**. This (not the fragment shader) is the flying-fps problem,
and explains the 30–120 swing (slow look ≈ 120, fast fly ≈ 30 — the SDFGI revoxelization signature).

**Fix landed — GI/shadow PROXY mesh** (`TerrainLab.SetGiProxy`; toggle "GI/shadow proxy (perf)" on
the Debug tab + `--giproxy=0/1`, **default OFF** pending the user's eye-gate on GI/shadow fidelity):
a coarse 256² (~65k-vert) copy of the *same* heightfield feeds SDFGI (`GIMode Static`) + casts
shadows (`ShadowsOnly`, invisible in colour); the 4M-vert detail mesh renders the view only
(`GIMode Disabled`, `CastShadow Off`). GI/shadows are low-frequency → they need terrain *shape*, not
fine verts. SDFGI/shadow passes process ~60× less geometry.

| In-motion 1440p (rule on) | proxy OFF | proxy ON |
|---|---|---|
| clouds off | 55 fps (18.3 ms) | **131 fps (7.6 ms)** |
| clouds on  | 49 fps (20.5 ms) | **101 fps (9.9 ms)** |

GI is **retained, not dropped**: proxy-on SDFGI still costs ~1.7 ms (vs ~12 ms full mesh, ~0 fully
off). Reuses `_mat` so GI bounce colour stays accurate. Static A/B shots identical (no z-fight, proxy
not visible). ⚠ **Look eye-gate owed:** confirm GI/shadow fidelity in motion before defaulting ON.
Next perf lever after this is the same mesh's raster/vertex floor → the CDLOD terrain-LOD arc.

## Results so far (terrain-filling view, STATIC — see in-motion section above for the real flying cost)

| State | Before | After | Δ |
|---|---|---|---|
| Clouds OFF (baseline) | 9.7 ms | **5.6 ms** | −42% |
| Clouds ON (1024 dome default) | ~12.4 ms | **8.6 ms** | −31% |

All from **code efficiency, zero quality reduction**.

## What landed (committed)

1. **Branched triplanar** (`terrain_lab.gdshader`) — `tri_w` (sharpness 8) makes one plane dominate
   on flat/moderate terrain; the others are <0.4% (under 8-bit quantization). `tp_alb`/`tp_rgh` now
   skip sub-`TRI_EPS` planes instead of always sampling 3 → ~3× fewer texture fetches on flat ground.
   **−2.3 ms**. Bit-near-identical.
2. **Anti-repetition on albedo only** — the histogram-preserving multi-tap bombing hides *color*
   tiling; normal/roughness tiling under the matte ground BRDF is imperceptible (and averaging
   normals flattens detail). Normal/rough now use plain branched triplanar (1 tap/plane, not 4).
   **−1.4 ms**. Took the measured anti-repetition cost ~3.4 → ~1.4 ms.
3. **`light()` `(1-x)^5` via 5 muls** instead of `pow()` (×3 per lit pixel) — bit-identical ALU win.
4. **Cloud compute GC** (`CloudVolume.cs`, `Std430.cs`) — cache the two compute uniform sets once
   (was 10 `RDUniform` + 2 `UniformSetCreate`/`FreeRid` **per frame**); `Std430Writer` writes into a
   reusable `byte[]` via `TryWriteBytes` (was ~400 short-lived 4-byte arrays/frame). Stutter/smoothness
   win (shows in worst-frame / GC, not avg). Verified clouds render + `--cloudstats` packing intact.
5. **`--sdfgi=0/1` profiling probe** added.

## Cost decomposition (measured, for targeting future work)

**Baseline ~9.7 ms (pre-opt, terrain-filling), decomposed:**
| Item | Cost | Status |
|---|---|---|
| Anti-repetition (Ground Unit 1) | ~3.4 ms | ✅ cut to ~1.4 ms (branched triplanar + albedo-only) |
| **Terrain mesh** (4.19M-vert single PlaneMesh, NO LOD) | part of the ~3.8 ms floor | ⛔ PARKED — needs terrain roadmap |
| Sun shadow map (re-draws the 4M-vert mesh ×4 PSSM cascades) | ~2.0 ms | ⛔ tied to mesh — parked |
| SDFGI (real-time GI) | ~0.4 ms | already cheap (surprise) |
| SSAO | ~0.1 ms | ~free |
| Heightblend, splat-toggle | ~0 ms | free |

**Clouds (add-on):** 1024 dome +3.2 ms, 512 dome +1.5 ms, 512+temporal +1.0 ms. **God rays +0.2 ms (≈free).**
Raymarch step count (64–160) is NOT a cost lever (early-exit march). Coverage drives cost (denser = more samples hit cloud).

## PARKED — the terrain floor (do NOT touch without a plan)

The ~3.8 ms floor is dominated by the terrain being a **single 2048² PlaneMesh = ~4.19M verts with
no LOD**, drawn full-cost at every distance AND again ×4 in the sun shadow cascades. It is reducible
(LOD), **but**: every prior project iteration (WG1–15) **fell apart at the terrain clipmap** — it is
the recurring failure point (memory `terrain-clipmap-killed-wg1-15`). So the mesh/floor + shadow-pass
optimization is **deferred to a dedicated terrain roadmap** that must OPEN with a post-mortem of why
clipmap failed before, then weigh alternatives (chunked quadtree, GPU tessellation, Godot
visibility-range mesh LOD, simpler uniform-but-smaller) against those failure modes — not against
"most AAA." Do not unilaterally add a clipmap.

## Remaining safe backlog (no terrain geometry; not yet done)

From the code audit (4 read-only subagents, 2026-06-18) — ranked, quality-preserving:
- **✅ 16-bit noise volumes — DONE.** Was `R32G32B32A32`; now `R16G16B16A16` half-float (cloudstats
  byte-identical). Halved bandwidth on the density taps.
- **✗ Cheap sun light-march — TRIED & REVERTED (do not re-attempt as-is).** Skipping the 2-octave detail
  erosion in `light_optical_depth` gave only ~0.1 ms (noise): the detail volume is 32³ (~256 KB at
  16-bit) = **cache-resident**, so its taps cost ~no bandwidth — the audit counted taps, not cache
  behavior. It also darkened clouds ~6% (meanCloudLuma 0.618→0.579). Net loss. If ever revisited, target
  the 96³ SHAPE volume (not cache-resident) — but that changes the density math (coupling).
- **Shadow-map amortization** — re-marched every frame; could stride like the dome, but risks the ground
  shadow lagging the cloud overhead during motion (coupling) → needs care + eye-check.
- **Cache per-frame `GetNode("/root/...")`** in `TerrainLabUI._Process`/`UpdateOvercast` (string-path
  tree walks every frame; resolve to fields once in `AttachClouds`).
- **macro `rgb2hsv→hsv2rgb` roundtrip** per pixel — value/saturation drift can be done in RGB.
- **⚠ MSAA — REVIEW NEEDED (user eye, in motion).** Was 4× (`msaa_3d=2`), set to **2× (`msaa_3d=1`)** as
  the safe no-regret call (still real edge AA, banks ~0.7 ms, no new artifact class). Fully OFF saved
  ~1.4 ms but risks motion crawl/specular shimmer that needs the user's eye to clear. To evaluate when
  the user can judge motion: **off** (cheapest), **FXAA** (`screen_space_aa=1`, cheap, slightly blurry),
  or **TAA** (`use_taa=true`, kills shimmer+crawl, temporally stable, but may GHOST the drifting clouds).
- **Cloud temporal default 1 → 3** (validated look) — near-linear dome-march reduction; a knob/preset call.

## Cloud quality preset (perf lever, not a code fix)
Ship **512 + temporal** as the mid-range default, **1024** for High/Ultra — 512+temporal is ~+1 ms
cloud cost vs +3.2 ms at 1024. God rays ride along free.
