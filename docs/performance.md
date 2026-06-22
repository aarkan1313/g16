# WG16 Performance — profiling + optimization pass (2026-06-18)

> **⚠ 2026-06-21 CORRECTION (read before trusting the numbers below — from `AUDIT-2026-06-21.md`).** Two staleness
> items: **(1) GI/shadow proxy + SDFGI are DEFAULT-OFF now** (the 0b eye-gate, 2026-06-20). Every "proxy ON / SDFGI ON"
> figure below (incl. the 9.9/9.6 ms clouds-on decomposition) describes a config the shipped default no longer uses;
> the shipped default is SDFGI-off + proxy-off sharp detail-mesh shadows (~4.7 ms clouds-off). **(2) The 9.6 ms budget
> predates AT-1 + AT-2** (both now default-on); **AT-2 aerial measured +0.7 ms** (`--profmove`, 114 vs 124 fps;
> NEEDS_REVIEW 11). **Night moonlight on clouds (2026-06-21)** adds one extra cloud light-march per in-cloud step,
> **gated night-only** (`P.moon_dir.w==0` in day/new-moon → skipped, zero daytime cost); same order as a 2nd sun
> light-march on the night cloud cost. TODO: re-decompose the in-motion frame for the actual shipped default. The relative costs + the "8 ms is unreachable
> on the single 4.19M-vert mesh" conclusion still hold; the absolute default-config attribution does not.

The reference for "where the frame goes" and what's been optimized. Numbers are RTX 5090
laptop, **uncapped** (`--profile=N` disables vsync + uncaps fps), so absolute ms are dev-machine
values — the **relative costs and before/after deltas** are the takeaway. Scale ~2.5–4× for a
mid-range GPU on this fragment/compute-bound work.

How to reproduce: `scenes/terrain_lab.tscn --rendering-driver vulkan -- --profile=4 --cam=0,350,0,-25,40 [flags]`.
Profiling probes: `--clouds=0/1`, `--godrays=0/1`, `--temporal=N`, `--cloudtex=H`, `--ssao/--shadow/--sdfgi/--ar/--hb=0/1`.
Caveat: the laptop GPU thermally throttles after many back-to-back runs — space runs out / trust
reproduced numbers, and treat single wild outliers as hitches.

## 2026-06-22 — SKY/LIGHT lane subsystem perf state-of-record (consolidated, not a fresh run)

> Consolidates the **sky/atmosphere/cloud/light** subsystem costs that ARE measured + scattered across
> `ROADMAP.md`/`DECISIONS.md`/`NEEDS_REVIEW.md`, because the older decompositions below price a config the
> shipped default no longer uses (SDFGI/proxy ON; pre-atmosphere). **Scoped to the sky lane on purpose:** the
> terrain mesh/CDLOD cost is in flux in the parallel chat, so a fresh *whole-frame* number would be contaminated
> and isn't taken here. A coordinated `--profmove` whole-frame re-decomposition is **owed** (run it when the
> terrain chat is idle — see "Owed" below). All ms are RTX 5090 laptop, uncapped, **in motion** unless noted.

**Sky-lane subsystem costs (all default-ON unless noted):**

| Subsystem | Cost | Source / confidence | Lever |
|---|---|---|---|
| Atmosphere **AT-1** sky-view LUTs | **+0.2 ms** | measured (DECISIONS 2026-06-20) | recompute-on-change; LUT res |
| Atmosphere **AT-2** aerial froxel (32³) | **+0.7 ms** | measured `--profmove` 114↔124 fps (NEEDS_REVIEW 11) | froxel res; recompute cadence |
| Atmosphere **AT-3** cloud-lighting | **CPU readback / change** (not ms-profiled) | structural (DECISIONS 2026-06-21) | **readback throttle owed (#7)** |
| **Clouds** raymarch (512 + temporal default) | **~1.0 ms** (1024 = +3.2; in-motion floor ~2.0) | measured | temporal stride; dome res; coverage |
| **God rays** (screen-space radial, shipped w/ tangential high-pass) | **~+1.1 ms** | memory `godray-emission-vs-albedo-rootcause` ⚠ **supersedes the "+0.2 ms" old-path figure below** | sample count; downsample |
| Moonlight-on-clouds (2nd night light-march) | **~0 day / small night** | structural; `P.moon_dir.w==0` skips in day/new-moon | night-gated already |
| Directional shadow map (8192 atlas, SoftHigh PCF) | **not re-measured since the 4096→8192 bump** | — | **8192 = the dial-down perf lever** (4096/6144) once edges hold |

**Reading it:** the whole default-on GPU-atmosphere arc (AT-1+AT-2+AT-3) is **~0.9 ms + a CPU readback** — cheap,
and the C3 N-sun design keeps it cheap (suns summed inside the one skyview raymarch, transmittance/multiscatter LUTs
sun-independent). The sky lane's real cost is **clouds + god rays + the shadow map**, not the atmosphere. SDFGI
(~2.9 ms intrinsic / ~12 ms in-motion on the full mesh) and the GI/shadow proxy are **OFF** since decision 0b — ignore
every "proxy/SDFGI ON" figure below for the shipped default.

**Owed (needs a coordinated launch — don't unilaterally kill the terrain chat's Godot):**
1. Fresh whole-frame `--profmove` decomposition for the *current* shipped default (after terrain/CDLOD settles).
2. Re-measure the directional shadow map at 8192 vs 4096/6144 (the audit's "dial it down for non-5090 HW" lever).
3. AT-3 readback throttle (folds into the #7 end-of-arc efficiency pass).

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
the Debug tab + `--giproxy=0/1`, **DEFAULT ON since 2026-06-19** — user approved enabling it; its
GI/shadow *fidelity* eye-check in motion is still owed but it's the active default):
a coarse 256² (~65k-vert) copy of the *same* heightfield feeds SDFGI (`GIMode Static`) + casts
shadows (`ShadowsOnly`, invisible in colour); the 4M-vert detail mesh renders the view only
(`GIMode Disabled`, `CastShadow Off`). GI/shadows are low-frequency → they need terrain *shape*, not
fine verts. SDFGI/shadow passes process ~60× less geometry.

| In-motion 1440p (rule on) | proxy OFF | proxy ON |
|---|---|---|
| clouds off | 55 fps (18.3 ms) | **131 fps (7.6 ms)** |
| clouds on  | 49 fps (20.5 ms) | **101 fps (9.9 ms)** |

GI is **retained, not dropped**. Reuses `_mat` so GI bounce colour stays accurate. Static A/B shots
identical (no z-fight, proxy not visible). ⚠ **Look eye-gate still owed** (GI/shadow fidelity in
motion) — default-on was the user's call regardless; revert via the toggle if the eye dislikes it.

### Post-proxy in-motion decomposition (1440p, proxy ON = default) — the path to the 8 ms target

**Long-term TOTAL budget for the world generator = 8 ms (~125 fps)**, and flora + water + erosion +
more biomes STILL have to fit inside it. Current flying frame (clouds on) = **9.6 ms** — already over,
before those features. Where the 9.6 ms goes:

| Component (in motion) | ms | Lever |
|---|---|---|
| **Mesh raster + fragment floor** | ~4.0 | **CDLOD terrain LOD** (fewer verts far/culled) — also shrinks SDFGI+shadow further. BIGGEST lever. Eye-gated (pop-free). |
| **SDFGI (on proxy)** | ~2.9 | Largely *intrinsic cascade cost* now (proxy already removed the geometry part). Lever: fewer cascades / larger cell / update throttle, or cheaper GI. Quality/eye call. |
| **Clouds** | ~2.0 | Temporal amortization / dome res / march steps (cloud arc). |
| Shadows (on proxy) | ~0.6 | Done (proxy fixed it). |
| AR / SSAO | ~free | Done. |

**Ranked path to 8 ms (all eye-gated → parked until the user can do visual checks):**
1. **CDLOD terrain LOD** — the mesh floor is the dominant resolution-independent cost AND it inflates
   SDFGI/shadow; LOD attacks all three. The single highest-leverage perf work. (`specs/2026-06-18-terrain-lod-roadmap-design.md`.)
2. **SDFGI config / cheaper GI** — ~2.9 ms intrinsic; cascade/cell/update tuning behind toggles.
3. **Cloud cost** — temporal + dome res + steps.

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
- **✅ Cache per-frame `GetNode("/root/...")` — PARTIALLY DONE 2026-06-22.** `ComposeLighting` +
  `ApplyOvercastScaling` (the per-frame lighting hot path while the day/night cycle runs) now use cached
  `EnvNode`/`SunNode` lazy props instead of re-walking `/root/TerrainLabRoot/Env|Sun` each call (~4 walks/frame
  removed). Remaining one-shot/`_Process` walks elsewhere are not per-frame-hot; fold into the #7 pass if wanted.
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
