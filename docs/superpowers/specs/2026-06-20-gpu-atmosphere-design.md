# Spec: GPU-Compute Physical Atmosphere (Sky lane #3)

Date: 2026-06-20. Status: DESIGN (brainstorm complete, user-approved). Owner: Sun/Light lane.
Parent: ROADMAP ▸ ☀️🌙 FINISH THE FULL SKY SYSTEM ▸ #3. Follows #2 (Clouds overhaul). The "sky pass that
relates to everything." Technique: Hillaire, "A Scalable and Production Ready Sky and Atmosphere Rendering
Technique" (sky-view / transmittance / multi-scatter / aerial-perspective LUTs).

## Why

Sky color today is a **hand-authored keyframed script** (`time_presets.json` `day_script` + night anchors)
driving `sky_top/horizon/ground` — cheap, approved, but hand-tuned per hour and not physical. The GPU
atmosphere computes sky color **physically from the sun direction** (Rayleigh + Mie scattering): correct
dawn/dusk/twilight gradients, aerial perspective, and (eventually) physically-lit clouds — one unifying
model instead of keyframes. Long-term-best (pillars); built incrementally and gated (discipline).

## Scope

IN: Hillaire LUTs on the **CloudVolume render-thread `Texture2Drd` seam** (`RenderingServer.
CallOnRenderThread` — a node drives compute, RID assigned to a `Texture2Drd` ONCE, sampled by the sky
material; **NOT FieldCompute** — a local-RD texture can't be sampled by a material, memory
`compute-to-material-callonrenderthread`). A **toggleable sky-color provider, default OFF = the approved
keyframed look**; becomes default only if it wins its eye-gate. Phases: core sky color → aerial perspective
→ cloud-lighting integration. OUT: ground-truth single-scattering reference path (LUTs only); precipitation;
replacing the WHOLE composer (atmosphere supersedes SKY COLOR only — sun energy/ambient/night-grade/moon/
stars/fog stay in the one-writer composer).

## Architecture — LUTs on the Texture2Drd seam, a toggleable sky-color provider

```
scripts/lab/AtmosphereCompute.cs (NEW, mirrors CloudVolume's seam) — a Node:
  InitCompute on the render thread (RenderingServer.GetRenderingDevice()): build the LUT shaders +
  storage textures (StorageBit|SamplingBit), assign each RID to a Texture2Drd ONCE. Recompute the LUTs
  only when the sun/atmosphere params change (not per-frame) via CallOnRenderThread. NEVER reassign RIDs.
  Holds atmosphere params (Rayleigh/Mie coeffs, planet/atmos radius, sun intensity, turbidity).
  LUTs: transmittance (2D) · multi-scatter (2D) · sky-view (2D, lat-long sky color) · aerial-persp (3D, AT-2).

shaders/atmosphere_*.glsl (NEW compute) — the Hillaire LUT passes.

cloud_sky.gdshader — when atmosphere_on: background() samples the SKY-VIEW LUT (Texture2Drd) by direction
  instead of the sky_top/horizon/ground gradient. Sun/moon/stars/clouds composite over it unchanged.
  atmosphere_on=false → the current approved keyframed gradient (no regression).

TerrainLabUI.Lighting.cs (ComposeLighting) — push sun dir + atmosphere params to AtmosphereCompute;
  the keyframed day_script becomes an artistic GRADE/tint lever (not retired). One-writer unchanged.

scenes/*.tscn — add the AtmosphereCompute node (deferred-add pattern, like the moon light / cloud node).
data/atmosphere_presets.json (AT-1+) — Earth-clear / hazy / alien-air, mirroring the preset pattern.
```

### Build order (sub-phases — each its own eye-gate; build one past the last pass)

1. **AT-1 — Core sky color.** Transmittance + multi-scatter + sky-view LUTs on the seam; `atmosphere_on`
   toggle; `background()` samples the sky-view LUT. Gate (A/B vs the keyframed look): physical sky reads
   as good-or-better across a full `time_of_day` scrub (dawn→noon→dusk→night), no pops, night dark with
   moon/stars on top; cost confirmed (`--profmove` — LUTs recompute on sun-change, not per pixel).
   **Default flips to atmosphere only if it wins; else stays off, banked.**
2. **AT-2 — Aerial perspective.** Aerial-perspective LUT wired into the depth fog (distance haze/blue-shift
   becomes physical, replacing the night-graded fog tint where atmosphere is on). Gate: distant terrain
   hazes believably by time of day; near terrain unaffected; ties cleanly with the existing fog.
3. **AT-3 — Cloud-lighting integration.** Feed the atmosphere (ambient sky radiance + sun transmittance)
   into the volumetric cloud raymarch's ambient/scatter so clouds are lit by the real sky. Built AFTER the
   clouds overhaul (#2) lands (it touches the raymarch). Gate: clouds lit consistently with the sky at all
   times of day; sunset clouds redden physically; no double-counting with the existing cloud lighting.

## Acceptance

- Atmosphere is a toggleable sky-color provider; **default stays the approved keyframed look until the
  physical sky wins its A/B eye-gate**, then flips. No regression when off.
- Built entirely on the `Texture2Drd` / `CallOnRenderThread` seam (no FieldCompute); LUTs recompute on
  sun/param change, not per-frame; in-motion cost within budget (`--profmove`).
- Physical sky reads good-or-better than the keyframes across a 24 h scrub; night dark with moon/stars on
  top; aerial perspective believable; clouds lit by the real sky (AT-3).
- Atmosphere presets (Earth-clear/hazy/alien) compose with time/weather/grade.

## Risks

1. **Texture2Drd seam (the banked wall)** — assign each LUT RID to its Texture2Drd ONCE; never reassign;
   one benign first-frame "binding not valid" is expected (self-heals). Mirror CloudVolume exactly.
2. **Local-RD vs render-thread** — any noise/input baked on a local RD must be re-uploaded to the
   render-thread RD (memory). LUTs are computed on the render-thread RD directly — keep them there.
3. **Look regression** — the physical sky may differ from the approved hand-tuned look; that's why it's
   toggle-default-off and A/B eye-gated before any default flip.
4. **Headless** — local-RD compute can't run `--headless` (memory `headless-no-local-rendering-device`);
   bake/run windowed. Compile-check via `--import`.
5. **Night/twilight tail** — physical scattering at very low/negative sun must hand off cleanly to the
   night grade + moon/stars (no bright band lingering); verify the dusk→night transition in motion.
6. **AT-3 ordering** — depends on the clouds-overhaul raymarch (#2); do not start AT-3 before #2 lands.
