# Night/Dusk Cloud Lighting — moonlight on clouds (design, 2026-06-21)

Lead item of #5 (shadow & lighting). Fixes night/dusk clouds rendering as flat black blobs, and lays the
moon→cloud light path the night-variation system needs. Follows the pillars: a real second directional
light (not a cheap ambient lift), gated to night-only for perf. See DECISIONS 2026-06-21 (night variation).

## Problem
`cloud_raymarch.glsl` lights clouds as `lum = (sunCol·sun·… + amb)·brightness` (~L384). `sunCol = sun_color ×
sun_energy`, so once the sun is low/down (dusk→night) clouds get **only `amb`** (dark night-sky ambient) →
black blobs. There is **no moon contribution** to clouds at all.

## Goal / non-goals
- **Goal:** moonlit clouds — silver-lit edges/undersides where the moon is behind/beside a cloud at night;
  dim grey (not black) bodies. Subtle/realistic by default, crankable. Phase-aware (new moon → ~no light).
- **Non-goal:** no forced "never dark" floor — **no-moon nights SHOULD be dark** (that's a wanted variation).
  No change to the daytime/sun look (moon term gated off when its strength is ~0). No new shadow map for the moon.

## Approach — moon as a weak second directional light in the raymarch
Mirror the existing sun term with the moon:
1. **Params (std430, APPEND at the end of the raymarch `ParamsBuf` only — safe, no offset shift; the shadow
   shader's struct diverges before this and is untouched):**
   - `vec4 moon_dir;`   // xyz = unit dir TO the moon, w = cloud-light strength (phase·presence·user-knob)
   - `vec4 moon_color;` // rgb = moon tint (cool white), a unused
2. **Raymarch term** (in the per-step lighting, after the sun `sun` term), gated `if (P.moon_dir.w > 0.001)`:
   ```
   vec3 mL = normalize(P.moon_dir.xyz);
   float odM = light_optical_depth(p, mL, windOff) * absorb;     // one short light-march toward the moon
   float mPhase = mix(globalPhase, hg(dot(rd, mL), LF(act,12)), pdeck);  // reuse the deck phase vs moon
   float moonScatter = exp(-odM) * mPhase + 0.45 * exp(-odM * 0.25);
   moonLit = P.moon_color.rgb * P.moon_dir.w * moonScatter;
   ```
   Combine into the direct term: `lum = ((sunCol·sun + moonLit)·powder·albedo·tint + amb)·brightness`.
   **Cost:** one extra `light_optical_depth` march per in-cloud step, **only when moon strength > 0** (day = 0 →
   skipped). Measure with `--profmove` (night, clouds on).
3. **No ambient floor change.** Rely on the moon term for fill; deep no-moon night stays dark (intended).

## C# wiring
- `CloudVolume`: fields `_moonDir`, `_moonColor`, `_moonCloudStrength`; `SetCloudMoon(Vector3 dir, Color col,
  float strength)`; append the two `.Vec4(...)` to `BuildParams` after `_atmoSunTrans`. CloudVolume stays the
  sole writer of its param buffer.
- `TerrainLabUI.Lighting.ComposeLighting` (night block): compute `strength = moonIllum · moonUp · night ·
  MoonCloudLight` (reuse the moon-illumination/`moonUp` already computed for the moonlight directional) and
  call `_cloud.SetCloudMoon(_lastMoonDir, _moon.Color, strength)`. Phase-aware because `moonIllum` ∝ phase.
- Knob: `MoonState.MoonCloudLight` (default ~1.0) → `lab_controls.json` Night-tab `scenef` `moon_cloud_light`
  (0–3) → `TerrainLabUI.Apply.cs` case → recompose. The user dials the silver; sets up bright/no-moon nights.

## Files
`shaders/cloud_raymarch.glsl` (struct + moon term) · `scripts/lab/CloudVolume.cs` (fields/setter/BuildParams) ·
`scripts/lab/TerrainLabUI.Lighting.cs` (push) · `scripts/lab/LightingState.cs` (MoonCloudLight) ·
`scripts/lab/TerrainLabUI.Apply.cs` (case) · `data/lab_controls.json` (knob).

## Verify
`dotnet build` + clean key-`2` night eye-gate (clouds on): moonlit silver edges/undersides, dim-not-black
bodies, no-moon (drop `moon_cloud_light` or moon energy) → dark. Dusk: warmer fill reads. `--profmove` cost
delta acceptable (night). Eye-gate target: subtle realistic moonlit silver, crankable.

## Risks
- **R1 std430 drift** (memory `std430-packing-helper`) — append at the very end via `Std430Writer`; only the
  raymarch struct, not the shadow struct.
- **R2 perf** — the extra light-march; gated night-only; measured. If heavy, reduce the moon light-march steps.
- **R3 over-bright/fake** — keep default subtle; eye-gate; it's a knob.
