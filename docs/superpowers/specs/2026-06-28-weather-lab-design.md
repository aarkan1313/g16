# Weather Lab — Design Spec

**Date:** 2026-06-28
**Status:** brainstorming → spec (approved direction; awaiting spec review → writing-plans)
**Author:** weather-lab brainstorming session
**Pillars:** AAA quality = performance (8 ms budget) = long-term-best; separation of concerns; modularity; tunability.
**Lives at scaffold time:** `weather-lab/docs/superpowers/specs/` (copied from here when the lab repo is created). Written here for now because `weather-lab/` has no git yet.

---

## 1. Purpose & scope

Stand up a **standalone weather lab** (a sibling to `erosion-lab/`) to explore *dynamic* weather over WG16's
infinite world, as isolated parallel work. The weather code we write stays a clean, self-contained module so
it can later be exported back into WG16 — the same `erosion-lab → WG16` arc.

"Explore weather" = **all of it, eventually** (user): the weather **brain** (state over time, fronts,
transitions), **visible phenomena** (rain, snow, hail, dust, lightning, fog), and **world effects** (wetness,
puddles, snow accumulation/melt), with seasons as a longer-horizon modulator. We build it as **one vertical
slice first** (rain, end-to-end) on an architecture sized to hold the whole space, then expand.

### Decisions already made (this session)
- **Foundation (user):** *"copy the files we need / use in the infinite world"* — the lab renders WG16's real
  infinite streamed world + the look-complete sky, by copying in the needed clusters. Leave the
  `TerrainLabUI` god-class behind; the lab gets its own thin harness (the erosion-lab shape).
- **Architecture (user-approved):** **C# "brain" → GPU renderers.** The C#/GPU split is drawn on the
  *performance line* — the brain is microscopic compute (free in C#, and C# buys testability/tunability,
  which are pillars); all heavy rendering is GPU. The brain's only crossing into GPU is a world-anchored
  spatial weather **field** sampled by shaders.
- **Perf rule (user):** prefer GPU where it is as-good-or-better **and** faster; use C# only where needed for
  quality/testability. This *confirms* the split rather than changing it (the brain is too small for GPU to
  matter; everything expensive is already GPU).
- **First slice (user):** **rain**.

### Out of scope (this spec)
Gameplay hooks; multiplayer sync (the brain is *designed* deterministic so MP stays possible, but no netcode);
a full audio system beyond a thunder-delay stub; full seasonal vegetation/biome shifts (seasons enter only as
a slow brain modulator). Exporting the finished weather module back into WG16 is a **future arc**, noted not built.

---

## 2. Repo shape (mirrors `erosion-lab/`)

`weather-lab/` sibling folder, with its own `.git`:

- **`Weather.Core/`** — pure C# weather sim + state. **Zero Godot dependency.** Unit-testable. *The brain.*
- **`Weather.Tests/`** — xunit against `Weather.Core` (determinism, transition bounds, no-pop, integrators).
- **`WeatherLab/`** — Godot 4.6 viz harness: the copied infinite-world cluster + copied sky rendering cores +
  the new weather render modules + a thin `Main.cs` (clock, live knobs, presets, CLI shots/self-checks).
- **`docs/superpowers/specs/`** — design docs (this file moves here at scaffold time).
- `.gitignore` mirroring erosion-lab (`.godot/`, `bin/`, `obj/`).

### 2.1 Copy-set ("the files we need" — verified against `wg-16-project`)

**Infinite-world cluster — CONFIRMED standalone (no `TerrainLabUI` dependency):**
`CdlodTerrain`, `CdlodQuadtree`, `CdlodMesh`, `ChunkAabbProvider`, `ChunkFieldCache`, `ChunkSlot`,
`TerrainLab` (field gen), `FieldCompute`/`FieldParams`, ground shader(s).
*(`CdlodTerrain` only news up `CdlodQuadtree`/`ChunkAabbProvider`/`ChunkFieldCache`/`ChunkSlot` + calls
`TerrainLab.Build` — confirmed by dependency grep.)*

**Sky rendering cores — MOSTLY clean (the few `TerrainLabUI` hits are comments):**
`CloudVolume` (sole sky-material owner; owns `cloud_sky.gdshader`), `CloudLayers`, `CloudWeather`,
`CloudParams`, `CloudNoiseCompute`, `AtmosphereCompute`, `cloud_sky.gdshader`. Plus `Std430` (the
`std430` packing helper — already a known-required util). `LightingState` (0 god-class refs) copies clean.

**Re-authored thin in the lab (do NOT copy wholesale — god-class-entangled):**
the lighting/preset driver. `LightingComposer` (7 god-class refs), `SkyPresets` (4), `CloudPresets` (2) are
the orchestration layer. The lab harness drives the sky uniforms itself with a minimal driver. We copy only
the clean data holders and re-author the orchestration as a few dozen lines in `Main.cs`.

**Left behind:** `TerrainLabUI` + its 12 partials.

**Shared infra:** `FlyCamera`.

> **Planning task — compile-driven trace.** The terrain cluster is verified standalone; the sky cluster is
> *mostly* verified. At scaffold time: copy the cluster, build, and resolve the first wave of missing-symbol
> errors. Expect a small de-glue pass on the sky lighting layer; budget for it explicitly.

---

## 3. `Weather.Core` — the spine (C#, testable)

> Research basis: **"target-easing blackboard"** model (verified, high confidence). NOT a grid ODE/fluid sim
> (overkill, hard to make deterministic + seamless on an infinite world, realism invisible once the GPU look
> is AAA), and NOT a hard FSM (pops; too coarse at WG16's fidelity). This is the Ultra Dynamic Sky pattern
> (presets + season-weighted probability + transition-blend) hardened for determinism. AAA-shipped evidence
> (RDR2, SIGGRAPH 2019) is that *believable weather is carried by the rendering + authored presets, not a
> heavy state sim.*

### 3.1 `WeatherState` — immutable per-frame snapshot (the renderer contract)

The single typed object every render module consumes. The brain *produces* it; renderers only *read* it.

| Group        | Field                | Type / range                 | Notes |
|--------------|----------------------|------------------------------|-------|
| Clouds       | `cloudCoverage`      | float 0..1                   | drives raymarch coverage threshold |
|              | `cloudType`          | float 0..1 (stratus→cumulus→cb) | selects density-height gradient |
| Precip       | `precipType`         | enum {None,Rain,Sleet,Snow,Hail,Dust} | **derived**, not authored |
|              | `precipIntensity`    | float 0..1                   | the ONE shared scalar (see §3.4) |
| Wind         | `windDir`            | float radians (world XZ)     | reused by cloud advection |
|              | `windSpeed`          | float 0..1 (normalized)      | |
|              | `gustiness`          | float 0..1                   | |
| Drivers      | `temperatureC`       | float °C                     | sets rain↔snow line (lapse-corrected per-fragment) |
|              | `humidity`           | float 0..1                   | |
| Visibility   | `fogDensity`         | float 0..1                   | base height-fog density |
|              | `visibilityM`        | float meters                 | |
| Events       | `lightningRate`      | float strikes/min            | **derived** from precip + cloudType |
| World-effect | `wetness`            | float 0..1                   | **slow integrator** (accumulate from precip, evaporate) |
|              | `snowDepth`          | float 0..1                   | **slow integrator** (accumulate from snow, degree-day melt) |

`precipType` and `lightningRate` are computed by the sim from the drivers, but exposed on the snapshot so
renderers never re-derive (a desync source — see pitfalls).

### 3.2 `WeatherSim` — advances state over time

Per brain tick (`dt` seconds, ticked at **1–4 Hz decoupled from frame rate**, interpolated per-frame):

1. **Season** — `seasonPhase = frac(timeDays / daysPerYear)`; a smooth cosine maps it to `seasonalTempBias`
   and `seasonalWetBias`. Season only *biases targets*; never a hard switch.
2. **Target selection (deterministic, world-anchored)** — sample 2–3 low-frequency value-noise fields at
   `(worldX·kSpace, worldZ·kSpace, timeHours·kTime)` seeded by `worldSeed` → continuous `synopticPressure`
   ∈[-1,1] and `frontalActivity` ∈[0,1]. Map these (+ seasonal biases + future per-biome weights) to a target
   by **blending the N nearest presets by weight** so the *target itself* is continuous (hard probability
   buckets reintroduce popping). The noise *is* the "front passing over"; advecting the sample point along the
   global wind vector makes weather visibly travel.
3. **Easing** — for each scalar: `s = lerp(target, s, exp2(-rate·dt))` (exponential decay — provably
   frame-rate-independent; per-variable `rate` gives natural asymmetry: clouds build slowly, a squall arrives
   fast, fog burns off gradually). **Not** a constant-fraction lerp.
4. **Derive `precipType`** — from drivers: if `humidity < threshold` or `cloudType` too thin → `None`; else by
   `temperatureC`: snow below ~0 °C, sleet in a thin band, else rain; hail/dust gated by convection/aridity
   flags. The rain/snow threshold is a **per-biome-biased ~1.0 °C**, not a global constant (real precip type
   depends on the vertical profile; surface temp is a simplified proxy — acknowledged approximation).
5. **Integrators** — `wetness += precipIntensity·rainK·dt` then evaporate (faster when warm/sunny);
   `snowDepth += (precipType==Snow)·snowK·dt` then melt via **degree-day**: `melt = DDF·max(0, T−T_melt)·dt`.

### 3.3 Guaranteed properties (unit-tested in `Weather.Tests`)
- **Deterministic** — every output is a pure function of `(worldSeed, timeHours, worldXZ)`. Live eased/
  integrated state is reconstructible from a **periodic deterministic keyframe + fixed-dt re-integration**
  (easing alone does NOT converge across clients — explicit correction from the research).
- **Bounded** — all `WeatherState` fields stay in range.
- **No-pop** — bounded rate of change per second.
- **Reachable extremes** — presets reach true-clear and true-storm.
- **Frame-rate independence** — `exp2(-rate·dt)` easing identical at 30 vs 144 fps.

### 3.4 `WeatherField` — world-anchored spatial modulation (the only GPU crossing)

Different regions = different weather (front here, clear there), advected by wind, **seamless across streamed
chunk borders**. Two-tier (research-recommended hybrid):

- **Coarse tier (C#, the brain):** slow per-cell target state on a coarse world-anchored grid (4–16 km cells),
  deterministic from `(worldSeed, worldCellCoord, time)`. Emitted as uniforms. *v1 may be near-global (one
  blackboard); `synopticPressure` is promoted to a true spatial field later WITHOUT changing the public
  interface.*
- **Fine tier (GPU, the shader):** per-pixel detail = procedural multi-octave noise evaluated in **world-XZ**
  inside the cloud shader, biased/scaled by the coarse state. **Movement = domain-scroll:**
  `weatherUV = (worldXZ − windOffset) / fieldScale`, with `windOffset` accumulated as a **C# `double`** and
  reconstructed *identically to the terrain world-XZ path*.

**Channel packing is ours** — we define `R=coverage, G=cloudType, B=precip` (or whatever); we do *not* inherit
Horizon's order (which is actually R=coverage / G=precipitation / B=type — a citation we deliberately don't
depend on).

**`precipIntensity` is ONE shared scalar** feeding clouds + ground wetness + god-ray occlusion + rain
particles + fog. If each system re-derives "is it raining," they desync (rain with no dark clouds, wet ground
under clear sky).

**Defer** true semi-Lagrangian GPU texture advection (curling/deforming fronts) to a later eye-gated,
default-OFF arc: first-order semi-Lagrangian is **diffusive** (fronts smear; needs MacCormack/BFECC + a
limiter), needs ping-pong + toroidal recenter, and reintroduces the exact floating-origin/seam bug class WG16
already fought. Domain-scroll + a moving coarse cell grid reads as evolving weather for far less risk.

`Weather.Core` never touches Godot/RenderingDevice/shaders (also dodges the headless-RD gotcha). Data in, data out.

---

## 4. Render modules (GPU, pure consumers of `WeatherState`)

Each module is independent, owns one visual, and answers *what / how-used / depends-on*. Add a phenomenon =
add a module. **All default-OFF behind a toggle until flown + eye-gated** (project discipline).

### 4.1 `SkyCoupling`
Maps `WeatherState` → existing cloud/atmosphere uniforms (coverage→density threshold, cloudType→shape, storm→
darkening, wind→cloud parallax). Reuses the copied sky cores. Sample the weather field **once at ray entry/
exit and lerp**, *not* per march step — the difference between "cheap" and "free."

### 4.2 `Precipitation`
> Research: **hybrid, world-anchored stack.**
- **Near-field drops:** a camera-following `GPUParticles3D` spawn box, particles emitting in **world coords**
  (`local_coords=false`) so drops fall world-locked while the box tracks the camera over the infinite world.
  **⚠ Verify first:** a reported Godot 4.6 bug where node transform still affects particles despite
  `local_coords=false` — empirically confirm world-anchoring before committing.
- **Splashes:** a **SEPARATE** system — Godot's `sub_emitter` *disables the parent's own emission*, so one
  emitter can't do both streaks and splashes. Collision source = `GPUParticlesCollisionHeightField3D` with
  `follow_camera_enabled=true` (lower resolution to offset the per-move re-bake; no overhangs/caves).
- **Heavy/distant:** a constant-cost textured-streak / screen-space layer (SIGGRAPH 2004 Wang; 2006 Tatarchuk)
  so cost does **not** scale with intensity.
- **Optional lens droplets:** a distinct additive post pass (Lagarde "Water drop 2a"), gated to first-person/
  wet-camera only — *not* part of the precipitation volume.
- Type/intensity/wind from the brain via uniforms. **Real cost = GPU fillrate (overlapping translucent quads)
  + heightfield re-bake, NOT particle count.** No transferable perf figure exists — **MUST profile in-engine
  against the 8 ms budget before defaulting on.**

### 4.3 `Wetness/Accumulation` (analytic, stateless — couples into `ground.gdshader`)
> Research: **adopt analytic, stateless, world-anchored layers** — NOT persisted per-chunk state (which adds
> chunk-birth bake cost — the brown-rectangle birth-budget race class — per-slot VRAM, and a cross-chunk seam
> problem). High confidence; maps to actual `ground.gdshader` lines.

Three stacked layers at the `dm==0` material write (`ground.gdshader` ~L571-573), mutating `(alb, rgh_aa,
nrm_pert)` just before the writes, driven by **global shader uniforms** the brain pushes via
`RenderingServer.GlobalShaderParameterSet` (one push → every chunk material, no per-chunk plumbing):

1. **Wetness (Lagarde porosity):** `wet = wetness · accessibility · (1−is_steep) · (1−snow_cover)`, then
   `porosity = saturate(((1−gloss)−0.5)/0.4)` *(use this form — the blog text has a saturate-paren typo)*;
   `factor = mix(1,0.2,porosity)`; `alb *= mix(1,factor,wet)`; `rgh *= mix(1,factor,0.5·wet)`; flatten normal
   toward geometric under thin water. ~6–12 ALU, 0 texture fetches. Non-metal only.
2. **Puddles:** `puddle = smoothstep(0.6,0.9,wet) · basin_mask · flat_mask`, `basin_mask` = analytic concavity
   (sample macro-height ±few m, center<neighbors) ∩ world-anchored noise; in a puddle force `roughness≈0.02–
   0.05`, flat normal, spec up. Anchored to `v_surf_xz` → identical at any LOD/chunk.
3. **Snow (analytic coverage, level not sim):** `T_local = base − lapse·(v_h − ref)`; `upness = clamp(N.y..)`;
   `snow_cover = smoothstep(...) · sky_exposure · step(T_local,0)`, world-noise break-up; blend the **existing
   `mat4` snow band** (L158-160) by `snow_cover`. Melt is **free** — `snowDepth` drops from the brain's
   degree-day model. Optional few-cm vertex displacement in `vertex()` from `snow_cover` for bulk.

**Hard rules (load-bearing — promoted from pitfalls):**
- Read `gloss = 1 − rgh_aa` **AFTER** the Toksvig specular-AA floor (`ground.gdshader` ~L553), and apply
  wetness so the Toksvig `max()` still clamps grazing/far fragments — else a wet near-mirror **re-introduces
  the specular shimmer Toksvig exists to kill** (the "moving line" artifact class). This is the most likely
  regression.
- Snow/puddle masks use **fwidth-soft** edges (like existing band weights ~L484-490) and anchor to
  `v_surf_xz`/`v_h` — never screen-space — or coverage aliases into a swimming line across LOD/chunk borders.
- Gate each layer with a `if (wetness > 0.0)` **global-uniform** branch (uniform-coherent → ~free).
- The **field cache is read-only** for this — accumulation is shading-only; cache untouched.
- `<0.3 ms` for all three layers is a **profiler target, not a measured fact** (puddle concavity taps + snow
  band fetches scale with on-screen wet/snow coverage).

**Planned next slice (AAA close-range feel):** a **camera-follow toroidal RT** for footprint/track/melt
deformation in snow — bounded, world-anchored, world-size-independent. Shipped AAA snow (Horizon, Tomb
Raider, GoW) pairs analytic coverage with this; frame it as the snow follow-up, not a gameplay-only extra.

### 4.4 `Fog/Visibility`
> Research: two-layer, single world-anchored visibility scalar.
- **Base:** Godot built-in depth+height fog (near-free); brain animates density from `fogDensity`/`visibilityM`
  (precip raises, wind thins, time-of-day: radiation/ground fog peaks pre-dawn, burns off as sun climbs).
  **Tint from the physical sky** (or it reads wrong at dawn/dusk/night).
- **Mood layer:** Godot 4.6 volumetric fog + a ground-hugging `FogVolume` (Height Falloff + a **world-scrolled**
  `NoiseTexture3D`) for drifting mist — reserved for hero weather (~1–3 ms, brain-tunable, falls back to height
  fog at low intensity; it stacks with clouds + god-rays in the budget). World-anchored noise = same field as
  clouds → seamless across chunk borders.

### 4.5 `Lightning`
> Research: **"flash-first, bolt-second."**
- **Flash (90% of the look):** a **burst** of 2–4 sub-flashes with randomized gaps and decaying amplitude (a
  single linear ramp reads fake), injected as a **NEW additive flash/ambient term** into the lighting/atmosphere
  + a transient cloud-underside boost. **Do NOT touch the look-complete sun/lights** (project constraint: the
  user refactors shadows aggressively but NOT lights). The flash term must **bypass auto-exposure** or the
  ~200 ms spike gets clamped and loses punch.
- **Bolt geometry:** Drilian midpoint-displacement + occasional forking (offset halves per generation;
  fork = `Rotate(dir, smallAngle)·0.7`, dimmer), as an additive glow ribbon with brief jitter + ~⅓ s fade —
  generated in `Weather.Core` (testable), rendered **only for rare near strikes**; distant/intra-cloud = flash
  only.
- **Thunder:** C# scheduled, `delay = distance/343 m/s`, attenuate + low-pass with distance. Cheap, testable.

### 4.6 `WindCoupling`
Drives cloud parallax now (via `SkyCoupling`); foliage sway later. `windDir`/`windSpeed`/`gustiness`.

---

## 5. Lab harness (tunability)

Thin `Main.cs` spawns the infinite world + sky + weather modules and runs the clock (real-time + time-scale):
- **Live controls** (hotkeys + a small panel): scrub weather state; force presets (Clear / Overcast / Rain /
  Storm / Snow / Fog); time-scale; per-module toggles (each = its own A/B); wind drive; before/after.
- **Data-driven presets** — `.tres` Resources / JSON (presets *are* the brain's target blackboards) + a
  randomizer, mirroring the existing lab's pattern.
- **CLI** — `--shot=PATH` (OS path, not `user://`; needs the bare `--` separator per the run-invocation
  gotcha); `--weathercheck` / `--precipcheck` exit-code mechanical self-checks (assert state bounds,
  transition rates, particle/strike counts).
- **Verification** — the **live eye-gate** is the real verifier (project convention: no TDD for GPU/visuals);
  xunit guards only `Weather.Core` logic.

---

## 6. First vertical slice → then expand

Prove the whole spine end-to-end with **rain** (user-chosen). Each slice behind a toggle, default-OFF until
flown + eye-gated.

- **Slice 0 — scaffold.** Lab repo (own git) + copy-set; infinite world + look-complete sky render; fly around.
  *Eye-gate:* "the real infinite world with the AAA sky, standalone." *(Includes the compile-driven sky de-glue.)*
- **Slice 1 — brain.** `Weather.Core` + `WeatherState` + Clear↔Overcast↔Rain over time (target-easing);
  `SkyCoupling` darkens/thickens clouds; global-uniform plumbing. *Eye-gate:* the sky visibly "weathers."
- **Slice 2 — precipitation.** Hybrid rain stack (near-field world-anchored particles first; verify the
  `local_coords` bug), wind-sheared, following the brain. **Profile against 8 ms.** *Eye-gate:* convincing rain.
- **Slice 3 — wetness.** Analytic Lagarde wetness + puddles in `ground.gdshader`, driven by the brain's
  `wetness` integrator; dries after. *Eye-gate:* the world reacts; **no Toksvig shimmer regression.**
- **Slice 4 — tunability.** Presets, knobs, self-checks, randomizer; consolidate.
- **Then expand:** snow coverage → snow deformation (toroidal RT) → storm + lightning → fog (height then
  volumetric) → dust → wind→foliage → seasons. Each a new module/preset on the proven spine.

---

## 7. Performance budget framing

8 ms frame budget (pillar). Each render module is measured in isolation (its own toggle = its own A/B), in
motion, against the real infinite world (not stills, not the CDLOD-OFF single mesh).
- **Brain:** negligible (~8–12 scalars + 2–4 noise samples at 1–4 Hz).
- **Wetness/accumulation:** target `<0.3 ms`, **to be verified** (scales with on-screen wet/snow coverage).
- **Precipitation:** **unknown until profiled** — fillrate + heightfield re-bake driven; the key budget risk.
- **Fog volumetric:** ~1–3 ms, brain-tunable, falls back to height fog; stacks with clouds + god-rays.
- **Lightning:** ~free common case (flash term + occasional ribbon).

---

## 8. Risks & open questions

- **Sky copy-set de-glue** — the lighting/preset layer is god-class-entangled; mitigated by the thin
  re-authored driver + a compile-driven trace at Slice 0.
- **Godot 4.6 `local_coords` world-anchoring bug** — verify empirically in Slice 2 before committing the
  particle anchoring approach; have the textured-layer fallback ready.
- **Field/weather seamlessness across the 8192 m renderOrigin snap** — sample `(worldXZ − windOffset)` with the
  *identical* world-XZ reconstruction as terrain; verify with a `--snapdiff` straddle-shot (the proven method).
- **Toksvig shimmer regression from wetness** — the single most likely artifact; hard ordering rule in §4.3.
- **MP determinism** — live eased/integrated state must reconstruct from `(seed, time)` via keyframe +
  fixed-dt re-integration; easing alone does not converge. (Designed-in now even though MP is out of scope.)
- **Scope** — "all of weather" is several subsystems; the slice plan keeps each shippable + eye-gated, on one
  spine, so it never becomes a big-bang.

---

## 9. Research provenance

Each heavy-GPU decision was research-hardened (5 dimensions, each adversarially verified — `weather-techniques
-research` workflow, 2026-06-28). Key sources, by area:
- **Wetness:** Lagarde "Water drop 3a/3b — physically based wet surfaces"; Naughty Dog *Uncharted 4* puddles
  (SIGGRAPH 2016).
- **Snow:** Foldes & Beneš "Occlusion-Based Snow Accumulation" (2007); degree-day melt (AntarcticGlaciers);
  lapse-rate (Nature Sci. Reports 2022); Alisavakis slope-snow shader.
- **Spatial field:** Schneider/Vos *Horizon Zero Dawn* cloudscapes (SIGGRAPH 2015) + Nubis; *Horizon Forbidden
  West* superstorms (GDC 2022); clayjohn Godot cloud demo; Stam advection; GPU Gems 3 large-world precision;
  Ronja tiling noise.
- **Brain:** Ultra Dynamic Sky (presets + season probability + transition-blend); *RDR2* sky (SIGGRAPH 2019);
  Minecraft FSM (rejected); NWS/AMS precip-type-by-temperature; deterministic-sim networking.
- **Lightning/Fog:** Drilian midpoint-displacement bolts; Level Design Book storm staging; *RDR2* unified
  volumetrics (SIGGRAPH 2019); Godot volumetric-fog / FogVolume docs.
