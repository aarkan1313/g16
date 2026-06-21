# Spec: GPU Atmosphere AT-3 — Cloud-Lighting Integration (Sky lane #3, sub-phase 3)

Date: 2026-06-21. Status: DESIGN (brainstorm complete, user-approved). Owner: Sun/Light lane.
Parent: `specs/2026-06-20-gpu-atmosphere-design.md` ▸ AT-3. Follows AT-1 (core sky color, default-ON) +
AT-2 (aerial perspective, default-ON, soft-pass 2026-06-21). Technique: light the volumetric clouds with the
physical atmosphere LUTs (Hillaire sky-view radiance + sun transmittance) sampled inside the cloud raymarch.

## Why

AT-1 made the **sky** physical and AT-2 the **distance haze** physical. The volumetric **clouds** are still lit by
two ad-hoc terms: a **flat mood sky-gradient** (`mix(sky_horizon, sky_top, rd.y)`) for ambient/fill, and a
**mood-keyframed sun color** for direct light. They don't physically track the atmosphere, so at sunset the clouds
don't redden the way the sky does, and the ambient doesn't carry the real sky color. AT-3 feeds the atmosphere's
per-direction sky radiance + sun transmittance into the cloud raymarch so the clouds are lit by the *real* sky at
every time of day. The user chose the AAA route ("pillars") — sampling the LUTs **inside** the raymarch (approach A)
over the cheaper C# color-handoff (approach B), because A's **directional ambient** lights cloud undersides with the
warm horizon and tops with the cooler zenith (the signature sunset-cloud cue B's flat 2-color gradient can't place).

## Constraints (hard)

- **Default OFF behind a toggle until the live eye-gate passes** (discipline rule + the 2026-06-21 audit's lesson on
  default-on-ungated features). On PASS, flip default-on. OFF path is byte-identical to today's cloud lighting.
- **Seam law** (memory `compute-to-material-callonrenderthread`): both `AtmosphereCompute` and `CloudVolume` build on
  the **shared render-thread RenderingDevice** (`RenderingServer.GetRenderingDevice()`, verified) — so the atmosphere
  LUT texture RIDs are valid to bind into the cloud raymarch compute's uniform set. No local-RD cross-device barrier.
- **Don't edit `terrain_lab.gdshader` / `TerrainLab.cs` / `GodRays*` / `shaders/godray*`** (Ground/other lanes on the
  shared branch). AT-3 touches only the cloud + atmosphere files (this lane's).
- **Keep the cloud shaders' shared param block byte-identical** across `cloud_raymarch.glsl` /
  `cloud_shadow.glsl` / `cloud_shadow_check.glsl` (memory: the per-layer buffer drift bug). New atmosphere bindings +
  the `atmo_light_on` gate live in `cloud_raymarch.glsl` only (the shadow shaders don't light clouds); if the gate
  must sit in the shared param struct, add it to all three identically.
- **No double-counting** with the existing cloud lighting: when `atmo_light_on`, the cloud uses the LUTs **instead of**
  the mood `sky_top`/`sky_horizon`/`sun_color`, not in addition (the same handoff law AT-1/AT-2 use).
- **No-TDD (GPU/visual):** verify via build → windowed `--import` → `--auto-shot` A/B + `--profmove` + the user's live
  eye-gate. Never judge from a still.

## Scope

**In:**
- Expose `AtmosphereCompute`'s sky-view + transmittance LUT RIDs (+ a `ReadyForCloudLight` flag) to `CloudVolume`.
- Bind those two LUTs into the cloud raymarch compute uniform set, with a **one-time uniform-set rebuild** once the
  atmosphere RIDs are live (the RID handshake — mirrors how the terrain shadow map goes live via `cloud_shadow_on`).
- `cloud_raymarch.glsl`, behind an `atmo_light_on` uniform:
  - **Ambient term:** replace the flat mood gradient with sky-view LUT samples — zenith (up) for the top contribution,
    horizon-toward-sun for the underside contribution, blended by the existing height factor. Reuse the exact
    `atmo_dir_to_uv` parameterization from `cloud_sky.gdshader` (kept in sync).
  - **Direct sun term:** multiply the sun light by the sun-transmittance LUT toward the sun; the sun base color becomes
    the star's neutral intrinsic color × transmittance (not the mood-reddened color) → physical sunset reddening.
- A `physical cloud light (AT-3)` toggle + `--cloudlight[=0/1]` CLI + a Light/Clouds-tab control, default OFF.
- An `atmo cloud light strength` knob for live gate tuning (a single gain to start; split into ambient/sun only if
  the gate needs it).
- Review-key 8 gate banner extended (atmosphere + clouds both on; toggle AT-3 to A/B).

**Out (YAGNI / later):**
- Multi-scatter LUT for cloud ambient (the 2-direction sky-view sample is the step; revisit if the gate wants more).
- Per-sample (per-altitude) transmittance variation across the deck (negligible over the cloud altitude range — sample
  once at the deck altitude toward the sun).
- Driving the **terrain** sun color from physical transmittance (AT-3 is clouds only; terrain sun stays keyframed —
  note any cloud/terrain sun-color divergence at the gate).
- Cloud self-shadowing changes, precipitation, atmosphere presets (Earth/alien) for clouds.

## Architecture

- **`AtmosphereCompute`** (this lane): add `public Rid SkyViewTexRid`, `public Rid TransTexRid`, and a
  `bool ReadyForCloudLight => _ready` (the LUT textures exist after `InitCompute`). No new compute — reuse the existing
  sky-view + transmittance LUTs (recomputed on sun change only).
- **`CloudVolume`** (this lane): receive the two RIDs (e.g. `SetAtmosphereLuts(Rid skyView, Rid trans)`), store them,
  and **rebuild the raymarch uniform set once** when they arrive + atmosphere on (a `_atmoSetDirty` re-`EnsureSets`).
  Add `atmo_light_on` (+ a strength) to the cloud param buffer; set it from the toggle/readiness. Bind the two LUTs as
  `sampler2D` at new binding slots in the raymarch uniform set only.
- **`cloud_raymarch.glsl`**: add `uniform sampler2D atmo_skyview`, `uniform sampler2D atmo_transmittance`, read
  `atmo_light_on` + strength from params; the ambient + sun terms branch on `atmo_light_on` (LUT path vs the existing
  mood path). The mood path is unchanged → OFF == today.
- **`TerrainLabUI`** wiring: in `AttachClouds`, after both nodes attach, push the atmosphere RIDs to CloudVolume; a
  `_Process` readiness gate enables `atmo_light_on` once `AtmosphereCompute.ReadyForCloudLight` && the toggle is on
  (mirrors the AT-1/AT-2 activation gates). `ApplyCloudBool`/`ApplyCloudFloat` cases for the toggle + strength.
- **Data flow:** sun moves → `AtmosphereCompute` recomputes LUTs (render thread) → CloudVolume's raymarch (render
  thread, same RD) samples the now-current LUTs each frame it renders → cloud dome → `cloud_sky.gdshader` composite
  (unchanged). The LUTs persist between sun changes, so the cloud always reads the last-good LUT (no per-frame
  atmosphere recompute needed).

## Default & gate

- Default **OFF**. Toggle `physical cloud light (AT-3)` / `--cloudlight=1`. OFF restores the mood cloud lighting exactly.
- **Gate (user, live, review key 8):** atmosphere + clouds both on; toggle AT-3 on/off while cycling times. Judge:
  (1) cloud ambient/color **tracks the sky** across the day (cool midday, warm dusk); (2) **sunset cloud undersides
  redden** physically (the directional-ambient win); (3) **no double-count** (clouds not over-bright/over-red vs OFF in
  a believable way); (4) clouds read consistent with the AT-1 sky + AT-2 haze; (5) **perf** within budget (`--profmove`
  — the raymarch gains 2–3 LUT taps/step; measure). Tune the strength live. On PASS → default-on + record.

## Risks

1. **RID handshake ordering** — the atmosphere LUT textures may not exist when CloudVolume first builds its uniform
   set (independent render-thread inits). *Mitigation:* one-time uniform-set rebuild when the RIDs arrive + the
   `atmo_light_on` readiness gate (don't sample an empty RID; fall back to the mood path). Proven pattern
   (`cloud_shadow_on`).
2. **Editing the look-sensitive cloud raymarch** — the gate is the real proof; expect tuning iterations (the strength
   knob). Default-off means no regression to the approved look until it passes.
3. **`atmo_dir_to_uv` drift** — the raymarch's sky-view sampling must match the LUT's parameterization exactly (the
   `sqrt` elevation warp). *Mitigation:* copy the mapping verbatim from `cloud_sky.gdshader`; keep them in sync.
4. **Shared param-block drift** — if `atmo_light_on` goes in the shared cloud param struct, all three cloud shaders
   must stay byte-identical. *Mitigation:* prefer a raymarch-only uniform; if shared, edit all three together + diff.
5. **Perf** — extra LUT taps in the raymarch inner loop. *Mitigation:* sample the sky-view at 2 fixed directions
   (zenith + horizon-toward-sun) per march step, transmittance once per ray; measure with `--profmove`, gate on budget.

## Acceptance

- Clouds lit by the physical sky: ambient + sun color track the atmosphere across a 24 h scrub; **sunset undersides
  redden**; OFF == today's mood lighting (byte-identical path).
- No double-count vs the existing cloud lighting; consistent with the AT-1 sky + AT-2 haze.
- Seam law honored (shared render-thread RD; RIDs assigned once; readiness-gated).
- In-motion cost measured (`--profmove`) and within budget.
- Default-off until the user's live eye-gate PASSES; on PASS, default-on + DECISIONS/NEEDS_REVIEW/ROADMAP recorded.
