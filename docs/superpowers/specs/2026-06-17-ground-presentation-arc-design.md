# Ground Presentation — Arc Design (the AAA ground-texturing rebuild)

Date: 2026-06-17 · Status: awaiting user review · Lane: surface/look (Presenter shader + a compute mask bake)

## Why

The ground texturing is "functional but bad" (user verdict, live): repetitive, flat/
lifeless, muddy blends, drab — **at every range**. Root cause (diagnosed live + in code):
the **application/presentation layer was never built out or validated**. Swapping
materials or randomizing doesn't help, which proves it's the *machinery*, not the
choices. The current shader is a basic terrain shader (triplanar + 2-tap anti-tile +
height-blend + macro tint + contact). The techniques that make AAA ground read as ground
were never implemented.

Per the project pillars (**quality = performance = AAA-ish = best-long-term, regardless
of effort**): this is the definitive ground-presentation subsystem for "the best world
generator we can build." Build out everything an AAA terrain has, properly, as coherent
units with shared interfaces — not six disconnected hacks.

## Scope of this doc

This is the **ARC spec** — the end-to-end architecture + interfaces for the whole ground-
presentation subsystem. Each numbered area below then gets its own focused plan and is
built + judged live one at a time, in order. This doc locks the shared contracts so the
units compose instead of colliding.

## The current pipeline (what we're rebuilding)

`shaders/terrain_lab.gdshader` fragment, per spot: GPU-baked splat picks dominant+
secondary zone → smooth plain triplanar samples each (~18 fetches) → height-blend mix →
macro-color tint → contact/crevice shading → custom `light()` (Burley+GGX). Anti-tiling
is a cheap IQ 2-tap. No distance detail, no parallax, no procedural breakup beyond a
crevice darken, no anti-repetition worth the name. `SplatCompute.cs` bakes the per-texel
zone/mix mask. 7 zones, materials chosen by index (a near-monochrome grey palette).

## Architecture — shared spine + the unit stack

The rebuild keeps the proven split (Field generates · Presenter draws) and the GPU-baked
mask pattern, but reorganizes the fragment into a **layered material-evaluation pipeline**
with clean seams so each technique is an independent, toggleable unit:

```
                 ┌── ground-data bake (GPU compute, extends SplatCompute) ──┐
heightfield ───► │  per-texel: zone weights, breakup masks (slope/curv/     │
                 │  cavity/aspect/flow), macro tint, distance hints         │
                 └──────────────────────────────┬──────────────────────────┘
                                                 ▼  (data textures, sampled in fragment)
fragment material pipeline (terrain_lab.gdshader), in evaluation order:
  1. ANTI-REPETITION sampler  — stochastic/texture-bombing wrapper around every
       material fetch (replaces tiled(); the base everything else calls)
  2. DISTANCE DETAIL          — near detail (albedo+normal) ⊕ far macro, blended by
       camera distance; drives sample scale + which layers contribute
  3. SURFACE DEPTH            — parallax-occlusion offset + real normal/roughness/AO
       feeding the BRDF (POM substitutes for Godot's missing tessellation)
  4. PROCEDURAL BREAKUP       — slope/curvature/cavity/aspect masks (from the bake)
       select/blend materials + add dirt-in-crevice, rock-on-steep, debris, wear
  5. COLOR / VALUE VARIATION  — large-scale tint + hue/value break tuned to SURVIVE
       GI/AgX (the current macro wash is replaced)
  6. "AND MORE" (later)       — detail-mesh scatter hooks (pebbles/debris; overlaps
       flora), wetness/puddles, snow-by-aspect, triplanar quality. Stubs/interfaces
       defined now; built after 1-5 land.
```

### Shared interfaces (locked here so units compose)
- **`sampleMaterial(zone, worldPos, normal, lod) → {albedo, normal, rough, ao, height}`** —
  the ONE material-fetch entry point. Anti-repetition (1) + distance detail (2) + surface
  depth (3) live INSIDE it; everything else calls it and never samples textures directly.
- **`groundData(uv) → {domZone, secZone, mix, breakup masks, macroTint}`** — the baked
  data-texture read (extends the current splat read). Breakup (4) + color (5) consume it.
- **`distanceWeight(worldPos)`** — camera-distance → near/far blend factor, shared by (2)
  and used to LOD-gate (3) parallax (off at distance) and (1) sample count.
- **Toggle + knob per unit** in the Clouds-tab style registry, all in `lab_controls.json`,
  so each contributor is isolated live (FLAT BASELINE already exists for full bisection).

## The units (each its own plan; build order = impact)

1. **Anti-repetition (texture bombing).** Replace the IQ 2-tap with proper stochastic
   tiling (Wang/hex-stochastic or histogram-preserving bombing) inside `sampleMaterial`.
   Foundational: kills the wallpaper look at all ranges; everything else renders on it.
2. **Distance detail layering.** Near detail albedo+normal ⊕ far macro, cross-faded by
   `distanceWeight`. The biggest single AAA lever — fixes flat-up-close AND samey-far.
3. **Surface depth.** Parallax-occlusion mapping (height from the material set) + ensure
   normal/roughness/AO genuinely drive the BRDF; LOD-gated off at distance via (3)/`distanceWeight`.
4. **Procedural breakup.** Extend the compute bake to output slope/curvature/cavity/aspect/
   flow masks; fragment uses them to vary material + add crevice dirt, exposed rock,
   debris, wear — so the surface is never uniform.
5. **Color/value variation.** Replace the washed macro tint with large-scale tint + hue/
   value break authored to survive GI + AgX (validate against the lit result, not raw).
6. **"And more" (deferred, interfaces stubbed):** detail-mesh scatter hooks (pebbles/
   debris — coordinate with the flora subsystem), wetness/puddles, snow accumulation by
   aspect, higher-quality triplanar. Built only after 1-5 are judged good.

Palette/zone-assignment retune (the near-monochrome grey set) rides along with units 4-5
(breakup + color decide what material goes where and how it reads), not as a separate unit.

## Performance posture

Per pillars, quality first — but measured. Each unit adds cost; the existing `--profile`
harness + FPS HUD gate it. Levers: parallax + max anti-repetition taps are LOD-gated off
at distance (`distanceWeight`); the breakup masks are baked once (compute), not per-frame;
detail layering reuses the existing fetches where possible. Budget judged live per unit;
if a unit is too heavy, its quality knobs scale it before we cut it.

## Testing / validation

Mechanical per unit: `dotnet build` + headless `--import` + `--auto-shot` A/B (unit on/off)
+ `--profile` (ms cost). **The gate is the user flying it in motion** at THREE ranges
(close / mid / far) — the badness is range-spanning, so all three must improve. Each unit
is judged live before the next; FLAT BASELINE + per-unit toggles isolate regressions.

## Risk / undo

Large fragment-shader rebuild, but Presenter-only (base field untouched), layered so each
unit is additive + toggleable, `git checkout .` reverts. Biggest risk: cumulative perf —
mitigated by LOD-gating + per-unit profiling. Second risk: subjective "is it actually
better" — mitigated by judging live at three ranges per unit, never from stills.

## NOT doing (YAGNI / boundaries)

- Not touching the base field math, the cloud/lighting systems, or other scenes.
- Not replacing the 108-material library (the user approved those; usage is the problem).
- Not building flora here (separate subsystem, in progress elsewhere) — but unit 6 leaves
  a clean detail-scatter interface for it to plug into.
- Virtual texturing / megatexture: out of scope unless a measured need appears.

## Build order (each = its own writing-plans plan + live gate)

1 Anti-repetition → 2 Distance detail → 3 Surface depth → 4 Procedural breakup →
5 Color/value → 6 "and more". Spine + shared interfaces (`sampleMaterial`, `groundData`,
`distanceWeight`) are established as part of building unit 1, since it owns `sampleMaterial`.
