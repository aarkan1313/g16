# Spec: Celestial C1 — Procedural Fantasy Night-Sky (galaxy + nebulae + starfield), baked

Date: 2026-06-21. Status: DESIGN (brainstorm complete, user-approved "send it"). Owner: Sun/Light lane.
Parent: ROADMAP #6 Celestial expansion ▸ C1. Supersedes the current `stars_layer()` Milky Way (NEEDS_REVIEW 10:
user verdict "just a fog band, a half-circle all the way across"). Follows the GPU-atmosphere arc (AT-1/2/3, done).

## Why

The night sky is a flagged eyesore: `stars_layer()` in `cloud_sky.gdshader` draws a **uniform great-circle band**
(no core → the uniform arc), **smooth low-freq fbm + flat blue-white + no embedded stars** (→ reads as fog). The user
does **not** want a realistic Milky Way — they want **procedural, "cool fantasy" night skies that are tunable** (per
WG16's physical→stylized→fantasy ethos; the day sky already has fantasy presets). And it must stay **performant** —
a multi-layer procedural sky is expensive per pixel, so the static structure is **baked** (extending the
`milkyway_bake.glsl` seam built 2026-06-21) and sampled in one tap at runtime, making the richer look *cheaper* than
today's 3-`fbm3` band.

## Constraints (hard)

- **Performance is a first-class requirement.** Runtime night-sky cost must be ≤ today's (target: lower). The static
  galaxy+nebula structure bakes to a lat-long texture (compute, on the render-thread RD seam), re-baked only on a
  tunable/preset change (not per frame). Runtime = 1 texture sample + the live point-stars.
- **Keep the procedural path as the reference + a toggle.** The baked output must be pixel-diff-verified against the
  procedural (the bake compute copies the same noise/math). The `night_sky_baked` toggle (today's `mw_baked`) flips
  between baked (default, fast) and procedural (reference). OFF restores the procedural exactly.
- **Tunable + presets.** Every layer is a Night-tab knob; presets (`data/night_sky_presets.json`) bundle them into
  named cool skies + a dialled-back subtle one. Picking a preset re-bakes.
- **Look gates on the user's eye** (it's a fantasy aesthetic). Perf + bake-correctness are mechanically verifiable
  now; the "is it cool" tuning is owed to a live session. Build the tunable system so the user gates/tunes live.
- **Stay in sky/light files.** `cloud_sky.gdshader`, the bake shader + `AtmosphereCompute`, `CloudVolume` (sky-material
  setters), `TerrainLabUI.*` sky bits, `data/*`. No terrain/ground/godray edits. Keep the bake's copied noise block in
  sync with `cloud_sky.gdshader`.

## Scope

**In (C1):**
- **Galaxy layer** (replaces the uniform band): a band with a concentrated bright **core/bulge** (kills the uniform
  arc), **dust lanes** (dark fbm carving the band), resolved **star-cloud knots**, and a **fantasy color gradient**
  (core hue → arm hue, tunable — not blue-white). Knobs: core direction + size, band tilt/width/curve, dust amount,
  core color, arm color, brightness.
- **Nebula layer**: a small set (≤4) of procedural colored gas clouds (fbm blobs) at tunable directions, each with a
  color + scale + density; separate from the band (nebulae-only or band-only both possible). Knobs: count, per-nebula
  direction/color/scale/density (or a palette + seed).
- **Starfield rework** (live, not baked — needs twinkle): realistic magnitude spread (few bright, many faint), star
  **size** + **color-temperature** variety (warm/cool tints), tunable density + twinkle. Keeps the cheap hash-grid.
- **Bake pipeline**: evolve `milkyway_bake.glsl` → `night_sky_bake.glsl` (composite galaxy + nebulae → `rgba16f`
  lat-long color); `AtmosphereCompute` grows the bake params + re-bake-on-change; `cloud_sky.gdshader` samples the
  baked color in `stars_layer`.
- **Presets**: `night_sky_presets.json` + loader (mirror the fantasy/celestial preset infra) + Night-tab picker;
  several "cool" presets + one subtle.

**Out (later / other phases):**
- **C2** celestial bodies (planets, meteors/shooting stars, named-star realism) — separate.
- **C3** N suns/moons — separate.
- Animated nebulae / volumetric parallax / moving aurora (the bake is static structure; runtime adds only rotation +
  star twinkle). Revisit if wanted.
- Day-sky changes (this is night only, gated by `night_factor`).

## Architecture

- **`night_sky_bake.glsl`** (compute, render-thread RD; evolves `milkyway_bake.glsl`): per lat-long texel → direction;
  compute the galaxy (core/dust/star-clouds/color-gradient) + composite the nebula blobs; write the additive night-sky
  **color** (`rgb`) to `rgba16f`. Copies the `cloud_sky.gdshader` noise (`hash13`/`vnoise3`/`fbm3`) verbatim so baked
  == procedural. Params: a std430 buffer holding all galaxy + nebula tunables (extends today's `MwParams`).
- **`AtmosphereCompute`**: the existing `_mwTex`/`_mwRd`/`BakeMilkyWay` generalize to the night-sky bake
  (`NightSkyTexture`, `BakeNightSky`, `SetNightSky(...)` with the full param set); re-bake when any param changes
  (`_mwDirty`). Still baked once at init + on change, on the render thread. (Reuses the seam wholesale.)
- **`CloudVolume`**: `SetMilkyWayTex` → `SetNightSkyTex`; `SetMilkyWayBaked` → `SetNightSkyBaked`; the SOLE `_skyMat`
  writer. New per-layer setters route from `ComposeLighting`.
- **`cloud_sky.gdshader` `stars_layer()`**: when baked, sample `night_sky_tex` at the rotated direction → galaxy+nebula
  color (1 tap, replacing the procedural galaxy+nebula block); ALWAYS add the live reworked point-stars; fade by
  `night_factor` × above-horizon. The procedural galaxy+nebula block stays as the `!night_sky_baked` reference.
- **Data flow**: tunable/preset change → `TerrainLabUI` pushes params → `AtmosphereCompute.SetNightSky` (`_mwDirty`) →
  `BakeNightSky` on the render thread → `night_sky_tex` updated → sampled every night frame. No per-frame bake.
- **Decomposition (build order, one plan):** (1) generalize the bake + toggle (galaxy color only, baked) → pixel-diff
  vs a procedural galaxy; (2) add nebula layer; (3) starfield rework; (4) tunables wired to Night tab; (5) presets.
  Each stage builds + perf-checks; the look gates at the end (live).

## Performance

- Runtime: **1 texture sample** (galaxy+nebula) + the live point-stars (cheap) — *lower* than today's 3 `fbm3`.
- Bake: a compute dispatch over the lat-long texture (e.g. 1024×512 or 2048×1024 for fantasy detail), on the render
  thread, **only on a tunable/preset change**. A few ms one-time; never per-frame. (Resolution is a perf/detail knob;
  pick at build from the pixel-diff fidelity vs cost.)
- Verify: `--profmove --profile=3` night before/after; pixel-diff baked vs procedural (must match within the
  star-twinkle noise floor, as the current MW bake did).

## Risks

1. **Bake re-cost on live tuning** — dragging a knob re-bakes each change → hitches while tuning. *Mitigation:* the
   bake is a few ms; optionally debounce/throttle re-bakes during a drag. Acceptable for a design-time tuner.
2. **Baked color can't tune without re-bake** — runtime color tweaks (e.g. overall tint) that don't need re-baking
   should stay runtime multipliers (brightness, a global tint); structural/color-gradient changes re-bake. Draw the
   line in the plan.
3. **Noise drift bake↔procedural** — the bake must copy the shader noise verbatim (the MW bake proved this works).
   *Mitigation:* shared-block discipline + the pixel-diff gate.
4. **Look is ungated now** — fantasy aesthetic needs the user's eye; perf/correctness verifiable, "cool" is not.
   *Mitigation:* ship tunable + presets + the toggle; the user gates/tunes live.
5. **Scope creep** — nebulae/presets can balloon. *Mitigation:* ≤4 nebulae, a handful of presets, C2/C3 explicitly out.

## Acceptance

- Night sky is a **procedural, tunable, fantasy-capable** system (galaxy core/dust/star-clouds/color + nebulae +
  reworked stars), driven by Night-tab knobs + presets — no longer a uniform fog band.
- **Baked default, pixel-diff-verified == the procedural reference**; `night_sky_baked` toggle restores procedural.
- Runtime night-sky cost **≤ today's** (`--profmove --profile=3`); re-bake only on change.
- Seam discipline (render-thread RD, RID assigned once, noise block in sync).
- Look (default + presets) PASSES the user's live eye-gate (owed); on PASS, default-on + record.
