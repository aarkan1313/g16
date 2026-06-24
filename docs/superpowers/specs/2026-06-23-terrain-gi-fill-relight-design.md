# Terrain GI / Fill Relight — Design (sub-project #1)

Date: 2026-06-23
Branch: `experiment/presentation`
Scene: `scenes/terrain_lab.tscn` (Godot 4.6.2 mono, Vulkan, CDLOD terrain)
Backup checkpoint: commit `d5c558c` (pre-overhaul)

## Problem & diagnosis (why we are here)

The long-hunted "anti-sun darkness / shadow that grows when you look down" was mis-attributed for
hours (shadow map, SSAO, SSIL, aerial, ambient). Root cause, established by fixed-camera screenshot
isolation:

- The dark **anti-sun dune slopes** are a **fill-lighting** problem, not a shadow-map bug. A slope
  facing away from the sun is dark from low sun·N (diffuse), and the only fill it gets is dim,
  partly-blue **sky ambient** with **no warm ground bounce** — so it crashes to a dead blue-grey.
- The warm-flat-ambient WIP (`AmbientLightEnergy=0.9`, `AmbientLightSkyContribution=0.4`,
  warm-white `AmbientLightColor` in `LightingComposer.ApplyOvercastScaling`) cannot overcome it:
  40% sky-contribution keeps dragging blue back in, and a single flat color has no per-slope
  directionality.
- **SSIL was a red herring** — its dramatic-looking A/B (97% px / mean 52) was confounded by the
  auto-advancing day cycle drifting the sun between two *separate* launches. SSIL is now disabled
  (net-negative on dune relief) but it was never the cause.
- Sun shadows are separately **short-range** (shadow ON vs OFF diff = 2.6% of pixels) — distant hills
  cast no shadow. That is **sub-project #2**, not this spec.

This spec covers **sub-project #1: the terrain GI / fill relight** — make shaded/anti-sun terrain read
as warm sand (photoreal desert target) instead of dead blue.

## Goal & success criteria

- The shaded side of a dune reads as the **same warm sand material** as the lit side, with a subtle
  cool sky tint and believable warm bounce — never blue-grey/dead.
- Per-slope directionality: up-facing surfaces pick up more (cool) sky; surfaces surrounded by lit
  sand pick up more (warm) bounce.
- Tracks time-of-day and mood through the existing one-writer (`LightingComposer`).
- Robust to the CDLOD floating-origin snap (no world-space GI volume to break).
- Tunable at the lab gate; validated **drift-free** (frozen time), not by confounded A/Bs.

Non-goals (deferred to #2): long-range cast shadows, horizon-based terrain self-shadowing, fancier
contact AO.

## Approach (chosen: B — analytic physically-based irradiance)

Rejected **A — SDFGI**: real-time GI is the generic AAA answer, but the CDLOD terrain renders at
`worldXZ − renderOrigin` and **snaps every 8192 m**; SDFGI lives in world space and would re-voxelize
/ pop across each snap, plus cost + leak-tuning. Its generality is wasted on open, uniform-albedo
desert. Rejected **C — hybrid** as marginal gain over B for this scene.

**B** replaces the flat blue ambient with a two-part analytic irradiance computed **per-pixel in the
terrain shader**, because the open desert's light-transport environment is simple (uniform sunlit sand
+ sky), so an analytic model is near-exact, cheap, leak-free, and immune to the floating-origin snap.

## Architecture

- **Direct sun light** — unchanged. Godot `DirectionalLight` + its shadow keep owning direct lighting.
- **Indirect / fill** — owned by `ground.gdshader` via a new `EMISSION` term (currently unused →
  conflict-free injection point). `EMISSION = indirect(N) × ALBEDO × ao`.
- **Godot Environment ambient** — dialed down for the terrain so the built-in flat ambient cannot
  double-count or re-introduce blue. The warm-flat WIP override is removed (superseded).
- **`LightingComposer`** — pushes the irradiance uniforms to the terrain material each `Compose()`
  (single writer; tracks the day script / mood / overcast already flowing through it).

### Units

1. **Irradiance model (shader)** — `ground.gdshader`. Inputs: surface normal, the pushed uniforms.
   Output: `EMISSION`. Pure function of normal + uniforms; testable by eye + isolation viz.
2. **Irradiance push (C#)** — `LightingComposer`. Computes/forwards sun dir, sun radiance, sky
   zenith/horizon/ground colors, a ground-albedo estimate, and the strength knobs; drops env ambient.
3. **Tuning + validation harness** — lab sliders (`sky_strength`, `bounce_strength`), a live A/B key
   (fill on/off), and a frozen-time isolation auto-shot.

## The irradiance model (per-pixel)

`indirect(N) = sky_hemisphere(N) + ground_bounce(N)`
`EMISSION = indirect(N) * ALBEDO * ao`

- **`sky_hemisphere(N)` (cool):** blend `sky_zenith → sky_horizon → sky_ground` by the normal's
  up-ness (`N.y`), scaled by `sky_strength`. Up-facing slopes get blue zenith; vertical/down faces
  get progressively less sky. The directional sky fill the flat ambient could not produce.
- **`ground_bounce(N)` (warm) — the key term:**
  `sun_color * sun_energy * ground_albedo * bounceFactor(N, sun_elev) * bounce_strength`.
  `bounceFactor` rises for surfaces facing downward / toward the surrounding ground and scales with
  sun elevation (more sunlit ground ⇒ more bounce). This is what fills the anti-sun slope warm: it
  faces away from the sun but still "sees" the brightly lit sand around it.
- **`ao`:** the already-tamed SSAO (intensity 0.6) modulates the fill for contact darkening in
  crevices/valleys. (Godot SSAO affects ambient; for the EMISSION-injected fill we apply an AO factor
  explicitly so crevices still darken — exact wiring decided in the plan.)

### Why `× ALBEDO`
The fill must be tinted by the surface so warm bounce on sand reads as warm sand, not a grey wash. The
emission is an *indirect-light* contribution, so it is conceptually `irradiance × albedo` (Lambertian
response), consistent with how direct light already multiplies albedo.

## Data flow

```
LightingComposer.Compose()
  └─ push uniforms → terrain ShaderMaterial:
       sun_dir, sun_radiance (color*energy),
       sky_zenith / sky_horizon / sky_ground,
       ground_albedo (estimate), sky_strength, bounce_strength,
       fill_on (A/B)
  └─ drop Env.AmbientLightEnergy / sky-contribution for terrain (no double-count)

ground.gdshader.fragment()
  └─ EMISSION = (sky_hemisphere(NORMAL) + ground_bounce(NORMAL)) * ALBEDO * ao
```

## Tuning & controls

- Lab sliders: `sky_strength`, `bounce_strength` (gate-tunable, per the lab control pattern —
  field/setter/scene/id + a C# case, per the lab-registry note).
- Live A/B key: toggle the new fill on/off (`fill_on` uniform) to compare against the old look.
- Defaults chosen to read photoreal at midday Clear-Alpine, then eye-gated.

## Validation (drift-free — the lesson learned)

The SSIL mistake came from A/B-ing across two launches while the day cycle drifted the sun. This spec
mandates **drift-free isolation**:

1. **Frozen-time isolation auto-shot:** in one launch, freeze the day cycle (`Engine.TimeScale = 0`,
   the same mechanism `--godrayab` uses), capture **fill-ON**, toggle `fill_on`, capture **fill-OFF**,
   quit. The two frames differ ONLY by the fill term.
2. **Numeric check:** on the shaded-slope region, the fill must shift hue/chroma from blue toward warm
   sand and lift luminance — report the measured shift, not an eyeball.
3. **User eye-gate** on the live scene (the final gate; downscaled auto-shots only support the
   numeric/structural checks, never the final look call).

## Risks & mitigations

- **Double-counting** (env ambient + EMISSION both filling): mitigated by dropping the env ambient for
  terrain and tuning strengths against the frozen-time A/B.
- **EMISSION ignores the sun shadow** (it is unshaded): acceptable — indirect fill should not be
  occluded by the *direct* sun shadow; contact occlusion is handled by the AO factor.
- **Over-warm / washed look at high sun:** `bounce_strength` + the `sun_elev` scaling are the levers;
  eye-gate the midday case specifically.
- **Other lit objects** still use env ambient; dropping it is scoped to terrain’s needs — confirm no
  other object in the lab regresses (the giProxy is off in CDLOD mode).

## File touch-list (anticipated; finalized in the plan)

- `shaders/ground.gdshader` — irradiance uniforms + `EMISSION` term at fragment tail.
- `scripts/lab/LightingComposer.cs` — push irradiance uniforms each Compose; remove warm-flat WIP;
  drop terrain env ambient.
- `scripts/lab/TerrainLabUI.*` — sliders + A/B key + frozen-time isolation flag (validation harness).
- `scenes/terrain_lab.tscn` — env ambient defaults if needed.

## Sub-project #2 (separate spec, after #1): long-range cast shadows

Extend cascade range/quality so distant hills cast, and/or add heightfield horizon self-shadowing for
long-range terrain shadows the cascade can't reach. Out of scope here; good fill makes #2 easier to
judge.
