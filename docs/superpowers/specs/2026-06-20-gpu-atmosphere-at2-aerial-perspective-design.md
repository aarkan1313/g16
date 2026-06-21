# Spec: GPU Atmosphere AT-2 — Aerial Perspective (Sky lane #3, sub-phase 2)

Date: 2026-06-20. Status: DESIGN (brainstorm complete, user-approved: "send it"). Owner: Sun/Light lane.
Parent: `specs/2026-06-20-gpu-atmosphere-design.md` ▸ AT-2. Follows AT-1 (core sky color, BUILT + default-ON).
Technique: Hillaire 2020 aerial-perspective froxel LUT + a screen-space composite.

## Why

AT-1 made the **sky** physical. AT-2 makes the **distance haze on terrain** physical: in-scattered light + extinction
between the camera and each surface, varying with distance, altitude, sun direction, and time of day — so far
mountains haze warm at sunset / blue by day, consistent with the AT-1 sky. Today this is Godot's built-in depth fog
(`FogAerialPerspective` + a keyframed `FogLightColor`), which is a single-colour exponential blend, not physical.
The user chose the AAA option ("pillars") over the cheaper "drive the built-in fog" route.

## Constraints (hard)

- **No terrain-shader edit.** `terrain_lab.gdshader` is the Ground lane's on the shared `experiment/presentation`
  branch. AT-2 must apply aerial perspective WITHOUT editing it → a **screen-space composite pass** (reads the
  rendered frame + depth, writes the modified colour), mirroring `GodRaysScreen` (a clip-space fullscreen quad).
- **Don't edit `GodRays*` / `shaders/godray*`** (other chat's). AT-2 creates its OWN `AerialPerspective.cs` +
  `shaders/aerial_screen.gdshader`, modelled on the GodRaysScreen pattern.
- **Seam law** (memory `compute-to-material-callonrenderthread`): the aerial LUT is built on the render-thread RD,
  RID assigned to a `Texture2Drd` ONCE. Mirror AtmosphereCompute's existing 3 LUTs.
- **No-TDD (GPU/visual):** verify via build → `--import` → `--auto-shot` A/B + `--profmove` + numeric self-check →
  the user's live eye-gate. Never judge from a still.
- **Default behaviour:** AT-2 is ON with the atmosphere (atmosphere is now default-on), `--aerial=0` / a Light-tab
  toggle for off. OFF must restore today's built-in-fog look exactly (no regression).

## Scope

IN: a 3D aerial-perspective froxel LUT in `AtmosphereCompute`; a screen-space composite pass that applies
in-scatter + extinction to scene geometry by depth; the fog handoff (drop the built-in aerial fog when AT-2 owns
it); wiring + a toggle + CLI + a numeric self-check + a review-key A/B. OUT: AT-3 cloud-lighting (separate
sub-phase); volumetric/height fog rework (height fog may stay); editing the terrain or godray files.

## Architecture

```
scripts/lab/AtmosphereCompute.cs (EXTEND) — add the 4th LUT:
  _aerialTex (3D, 32×32×32 rgba16f, Storage|Sampling), RID → a NEW Texture2Drd (AerialTexture), assigned ONCE.
  New compute shader atmosphere_aerial.glsl. Recompute cadence: the aerial froxel is CAMERA-frustum aligned, so
  it recomputes when the CAMERA (pos/orientation) OR sun changes → effectively every frame in flight (cheap, 32³).
  New params pushed each frame: camera world pos + inverse(view·proj) (to map froxel→world ray), + max aerial
  distance (~32 km). SetCamera(Vector3 camPos, Projection invViewProj) sets a dirty flag.

shaders/atmosphere_aerial.glsl (NEW compute) — for each froxel (gid.xy = screen tile, gid.z = depth slice):
  reconstruct the world ray from inv_view_proj (NDC at the tile centre) + camPos; the slice's distance t is an
  EXPONENTIAL distribution near→far (more slices close where haze detail matters). Raymarch the atmosphere from
  the camera to t (Hillaire single+multi scatter, reusing getValFromLUT on transLUT/msLUT + the sun dir),
  accumulating in-scatter (rgb) and transmittance (a). Store vec4(inscatter, transmittance) per froxel. Keep the
  Hillaire shared block byte-identical with the other atmosphere_*.glsl (edit all together).

scripts/lab/AerialPerspective.cs (NEW, mirrors GodRaysScreen) — Node3D + a CLIP-SPACE fullscreen QuadMesh with
  aerial_screen.gdshader, RenderPriority just BELOW the god-ray quad (aerial composites first, beams on top).
  Attach(cam): per-frame push inv_view_proj + cam_world (depth→distance reconstruction) + the AerialTexture +
  the far-distance + an `aerial_strength`. SetEnabled(on) toggles Visible. Bind the Texture2Drd once it's ready
  (CloudVolume-style gate: sample only after AtmosphereCompute.AerialReady, else frame-1 binding error).

shaders/aerial_screen.gdshader (NEW) — fullscreen quad: sample hint_screen_texture (scene colour) +
  hint_depth_texture; reconstruct per-pixel world distance d (inv_view_proj, same math as godray_screen). SKY gate:
  if depth is at/near the far plane (no geometry) → output the scene colour unchanged (AT-1 already coloured the
  sky; no double-count). Else sample the aerial LUT 3D by (screen UV, d→exponential z) → (inscatter, T); output
  COLOR = scene·T + inscatter·aerial_strength, ALPHA = 1 (opaque replace via blend_mix). occ/debug modes optional.

scripts/lab/TerrainLabUI.* (EXTEND):
  - Process.cs: push the camera to AtmosphereCompute each frame (SetCamera) + the readiness gate that flips the
    aerial pass on once AerialReady (like the AT-1 _atmoMatActivated gate).
  - Lighting.cs (ComposeLighting): when _aerialOn, drop the built-in aerial fog (env.FogAerialPerspective → 0, or
    FogEnabled handling) so the froxel pass owns distance haze; OFF restores it. Height fog may stay.
  - .Clouds.cs: ApplyCloudBool "aerial_on" → Cloud/Aerial setters + ComposeLighting; field _aerialOn.
  - .cs (AttachClouds): create + AddChild + Attach AerialPerspective; bind AtmosphereCompute.AerialTexture.
  - .Cli.cs: --aerial[=1], --aerialcheck, --aerialstr=N.
  - .Review.cs: a review-key A/B (extend key 8's cycle, or wire the aerial toggle into it).
  data/lab_controls.json: "aerial_on" (Light, default true) + "aerial strength" (Light).
```

### Build order (sub-tasks — each its own mechanical check; one phase past the last pass)

1. **AT2-1 — Aerial LUT in AtmosphereCompute.** New `atmosphere_aerial.glsl` + the 3D texture + camera params +
   per-frame recompute + `--aerialcheck` (readback: froxel transmittance ∈[0,1], inscatter finite/non-negative,
   monotonic transmittance with depth). Nothing composited yet (no visual change). Verify: build, `--aerialcheck`
   PASS, `--atmoscheck` still PASS, no perf regression.
2. **AT2-2 — Screen-space composite pass.** New `AerialPerspective.cs` + `aerial_screen.gdshader`; depth
   reconstruction + LUT sample + composite + sky gate; readiness gate; toggle/CLI/lab-control; fog handoff in
   ComposeLighting. **This is the gate.** Verify: `--auto-shot` A/B (off == built-in-fog baseline; on = physical
   haze, distant terrain warm at sunset / blue by day, near terrain unaffected, sky unchanged); `--profmove` cost;
   then the user's live A/B eye-gate via the review key.

## Acceptance

- AT-2 ON: distant terrain hazes physically by time of day (warm low-sun, blue-shift high), near terrain
  unaffected, the sky (AT-1) unchanged (no double-haze on the sky), consistent with the AT-1 sky colour.
- OFF restores the current built-in-fog look exactly (no regression). Built entirely on the render-thread seam +
  a screen-space pass; no terrain/godray shader edits.
- In-motion cost within budget (`--profmove`); LUT recompute per-frame is cheap (32³). Numeric `--aerialcheck` PASS.

## Risks

1. **Double-haze / sky double-count** — the sky already has AT-1 colour; the aerial pass MUST skip sky pixels
   (depth ≈ far) and the built-in aerial fog MUST drop when AT-2 is on. Gate both; A/B the sky stays identical.
2. **Depth reconstruction** — reuse `godray_screen.gdshader`'s exact inv_view_proj + cam_world math (proven). A
   wrong reconstruction puts haze at the wrong distance. Verify with a known camera.
3. **Screen-pass ordering** — aerial must composite BEFORE god rays add beams (aerial = extinction/inscatter on
   the scene; beams = additive sun shafts). RenderPriority: aerial < godray. Both after the scene.
4. **Per-frame recompute** — the froxel is camera-aligned → recomputes every frame in flight; confirm it's cheap
   (`--profmove`) and that fast camera motion shows no temporal popping (froxel is low-res; linear filter the LUT).
5. **Texture2Drd first-frame** — one benign binding line expected; the readiness gate avoids sampling an unbound
   aerial LUT (mirror AT-1).
6. **Halton/temporal** — AT-2 v1 has no temporal jitter; if the 32³ froxel bands visibly, add a per-frame depth
   jitter later (banked, not v1).
