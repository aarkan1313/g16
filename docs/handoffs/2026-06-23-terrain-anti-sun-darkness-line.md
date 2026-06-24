# Handoff — terrain "anti-sun darkness / faint line" (RESOLVED 2026-06-24)

Date: 2026-06-23 (resolved 2026-06-24)
Branch: `experiment/presentation`
Scene: `scenes/terrain_lab.tscn`, Godot 4.6.2 mono, Vulkan.

## RESOLUTION (read this first)

**The core "anti-sun darkness / shadows that appear when I turn" was NOT a bug — it is correct
directional lighting.** A dune's anti-sun face has low sun·N → it is dark and (via the new fill) cool;
that shading is **welded to the world**. What changes as you rotate is *which faces are in frame* — face
the sun → you see lit faces (warm/bright); face away or look down → you see the shaded faces (dark/cool).
It only *reads* as view-dependent. Confirmed by single-toggle isolation: SSAO (`B`), SSIL (`N`), sun
shadow (`,`), and aerial (`K`) each showed **no** steady-state view-dependent darkening; and the EMISSION
fill is provably view-independent (uses `v_normal` + `sun_dir_to`, never the camera). Verdict: normal,
drop the bug hunt.

**Two REAL bugs were found and fixed along the way:**
1. **Aerial perspective fade-to-BLACK** (`aerial_screen.gdshader`): extinction `col*aer.a` (down to ~0.47)
   removed surface light that the sun-directional inscatter (≤0.14, ~0 anti-sun) couldn't replace → distant
   anti-sun terrain went black. Fixed with an energy-conserving path-radiance term `(1-aer.a)*aerial_haze`
   (fades to a sky-tint haze, not black). Toggle `Y` / `--aerialhaze`.
2. **SSIL crushing the terrain**: `ssil_intensity=1.0` was muddying the whole surface. Disabled
   (`LightingComposer` tune block + scene `ssil_enabled=false`).

**A terrain RELIGHT shipped** (sub-project #1): analytic indirect fill = cool sky hemisphere + warm
sun→ground bounce, injected via `EMISSION` in `ground.gdshader`, pushed per-Compose by `LightingComposer`.
Spec `docs/superpowers/specs/2026-06-23-terrain-gi-fill-relight-design.md`, plan
`docs/superpowers/plans/2026-06-23-terrain-gi-fill-relight.md`. Env ambient dropped to a low neutral so the
shader fill owns terrain fill. Gate-tunable (`fill: sky` / `fill: ground bounce` sliders, `I` A/Bs it);
warmed defaults (sky 0.40 / bounce 2.40) so shaded faces read warm sand, not dead blue. **Optional polish,
not a bug fix** — the thing it "fixed" was largely correct lighting.

**METHOD LESSONS (the expensive ones):**
- **Drift-free A/B only.** The SSIL "97% px / mean 52" that sent me down a wrong path was a CONFOUND: the
  auto-advancing day cycle moved the sun between two *separate* launches. Freeze time (`--fillab` / the
  `--godrayab` pattern) or toggle in ONE launch. Never A/B across launches while the cycle runs.
- **Verify the probe takes effect.** Re-confirm a toggle actually changed state (the recorded SSAO `2.0` was
  overridden to `0.6` at runtime; check the order — `ApplyCliOverrides` runs after `ApplyAll`).
- **Don't chase correct directional shading as a bug.** Use the screen-locked-vs-world-locked test early.

Below is the original (pre-resolution) investigation log, kept for the record.

---


> Sister handoff (RESOLVED, same session): `2026-06-23-terrain-rings-artifact.md` — the CDLOD "rings"
> were a one-line UV bug (textured at the morphed `wxz` instead of the true vertex XZ; fixed
> `v_surf_xz = vtrue_fine`, commit 38aab96). That success is why the user expects this one to also be a
> small bug. **It may not be.** Read the "Ruled out" section before assuming anything.

## Symptom — the user's words (verbatim where possible)

- "if I look down shadows appear on the ground."
- "it seems like the camera is a source to cause shadows so it's like an object essentially. … the current
  shadow is way too big."
- "it gets worse if I aim down if I'm facing the shadows direction."
- "it's weird that it only happens if the sun is at your back, I feel like that's a huge clue."
- "I don't get why it would change based off how I angle / look down / up."
- After cranking ambient (see below): "there's an almost invisible line, and with that line, as you move
  the camera down that's where the darkness/shadow happens."
- Latest hypothesis from the user: "maybe something to do with not showing shadows when the player isn't
  looking at an area?" (i.e. a screen-space effect that only renders on-screen and reveals as you look).

In short: looking DOWN with the SUN BEHIND the camera, a large soft dark/blue region appears on the
terrain; it changes with view angle. After a big ambient boost the large blue region goes away but a
**faint line + darkness that appears as you pitch down** remains. THAT residual line is the open problem.

## Repro

```
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,300,0,-20,0
```
Fly over the sand/dune area. Put the SUN BEHIND you, pitch DOWN. Observe the dark region / faint line.
Process notes: kill ALL Godot before each relaunch (stale windows misled us); shaders hot-compile but the
player reads the USER shader cache `app_userdata/"WG16 base field"/shader_cache` — clear it if a shader edit
seems to no-op; C# changes need `dotnet build WG16.csproj`.

## What is CONFIRMED (with method, not theory)

This artifact is **two stacked things**, separated by the unlit test + the ambient-crank test:

1. **The big blue region = dim BLUE sky-ambient on the terrain's relief.**
   - Rendering the terrain UNLIT (EMISSION = geometric normal / slope) made the dark region VANISH → it is
     LIGHTING, not geometry/material/texture.
   - The slope-amplified viz showed the terrain is covered in fine relief (not flat) — so anti-sun-facing
     gentle slopes get low diffuse N·L and fall back to ambient.
   - Ambient source is the SKY (`ambient_light_source=3`), so the fill is BLUE and dim → shaded slopes
     crash to dark blue.
   - **Proof:** forcing a strong flat WARM ambient (energy 1.6, sky-contribution 0, warm color) made the
     big blue region fill in to uniform warm sand. → ambient was the dominant cause.
   - A **tasteful** version is currently LEFT IN as WIP (see repo state). This is the only thing that
     actually moved the bulk of the artifact.

2. **The residual faint LINE + darkness-when-pitching-down = STILL UNRESOLVED.** It SURVIVES the 1.6 flat
   ambient crank. Since ambient is ADDED to every pixel, anything that stays dark through that is NOT
   missing light — it must MULTIPLY light down / OCCLUDE / apply EXTINCTION (an AO term, a shadow
   visibility multiply, or a transmittance factor), OR be a screen-space effect that doesn't render
   off-screen. The user's "doesn't show shadows when you're not looking at an area" points squarely at a
   screen-space effect (SSAO/SSIL/screen-space shadow) or the view-fitted directional shadow frustum.

## RULED OUT for the residual line (so you don't repeat them) — each tested LIVE

- **Cast shadow (directional shadow map):** persists with sun shadows OFF (key `,`).
- **Environment fog (depth + height + aerial-perspective):** persists with `env.FogEnabled` forced FALSE
  at the source (LightingComposer). Blue `fog_light_color` was a red herring.
- **Specular / sky reflection:** persists with `SPECULAR = 0.0` in the terrain fragment.
- **Low sun / grazing:** persists at sun height ~80° (near noon).
- **Post stack toggled OFF together** (SSAO `B`, SSIL `N`, SDFGI `M`, god rays `/`, atmosphere AT-1 `O`,
  aerial AT-2 `K`, cloud shadow `.`): the BIG region persisted (→ it was ambient). The RESIDUAL line was
  NOT cleanly isolated against these individually post-ambient-fix — the user kept reporting "slight"
  changes and we never got a single decisive toggle on the residual. **This is the gap to close first.**
- **Aerial-perspective bug hypotheses:** `aerial_far` shader value (32000) MATCHES the LUT bake far
  (`Process.cs:196` passes 32000 to `AtmosphereCompute.SetCamera`). `--aerialcheck` PASSES (inscatter
  0..0.14, transmittance 0.468..0.999, finite). So the aerial LUT is not corrupt — BUT transmittance
  falling to 0.468 means it DOES multiply distant terrain to ~half (a real extinction multiply that fits
  "survives ambient"); whether it reads as the "line" was never confirmed.
- **Three subagents read ground.gdshader / field_math / LightingComposer / shadow+SSAO config / the
  screen-space shaders.** Their top concrete bug guesses were checked and DISMISSED:
  - "slope = 1 - clamp(v_normal.y)" forcing rock at grazing — WRONG: `v_normal` is a world-space geometry
    varying, not view-dependent.
  - "SSAO intensity 2.0" — it is overridden to **0.6** at runtime (`LightingComposer.cs:221`, gated by
    `_shadowTuned`). So SSAO is already tamed; the scene's 2.0 is not what runs.
  - "aerial_far mismatch" — values match (see above).

## Leading OPEN candidates for the residual line (NOT confirmed — explore all)

1. **SSIL** (`ssil_enabled=true`, `ssil_intensity=1.0`) — screen-space indirect light, untamed (SSAO got
   dialed to 0.6, SSIL did not). Screen-space → "doesn't show off-screen", multiplies/darkens, has a
   screen-edge fade that could read as a moving line. **Prime suspect given the user's latest hypothesis.**
2. **SSAO @0.6 / radius 8 / power 1.5** — still screen-space; could still band at grazing.
3. **Directional shadow CASCADE / view-fitted frustum** — `directional_shadow_mode=2`, splits
   0.08/0.2/0.5, max 8000 m, `DirectionalShadowBlendSplits=true`, `shadow_blur=1.5`. CDLOD chunks CAST
   shadows (no `CastShadow` override → Godot default ON) so the terrain SELF-shadows; a cascade boundary
   or the shadow-distance edge could be the line, and the shadow frustum is fit to the view ("doesn't show
   off-screen").
4. **Aerial perspective AT-2** (`aerial_screen.gdshader`, default ON) — `col * aer.a` transmittance
   multiply (0.468 at distance); gated to GEOMETRY only (sky discarded) → a horizon-line between
   hazed/extincted terrain and clean sky. `aerial_strength=10` may be too low vs the extinction, so it
   DARKENS distant terrain instead of hazing it bright.
5. **SDFGI** — default off in scene, but the user toggled `M` to ON during testing; not a likely root.

## THE decisive test that was never cleanly done (do this FIRST)

With the ambient WIP in place (big region already gone), go to the residual line and toggle ONE system at
a time, confirming each state in the log, and report which SINGLE toggle kills the line:
`N` (SSIL) → `B` (SSAO) → `,` (sun shadow) → `K` (aerial AT-2). Also determine whether the line is
**screen-locked** (moves with the camera / stuck to the monitor) or **world-locked** (stuck to the ground).
Screen-locked ⇒ SSAO/SSIL/SSR. World-locked-at-a-distance ⇒ shadow cascade or aerial froxel.

## Current repo state (uncommitted) — what is MINE vs NOT

`git diff --stat` shows 4 files dirty:
- `shaders/ground.gdshader` (+9) — MINE: `detail_fade_on` default flipped 1.0→**0.0** (+ rationale comment).
  Sound, eye-confirmed change (the dissolve-to-mean fade washed far detail; the ring fix removed its
  reason to exist). Toksvig kept. NOT related to this artifact. OK to keep/commit.
- `scripts/lab/TerrainLabUI.Process.cs` (+5) — MINE: `_detailFadeOn=false` to match the shader default.
  (This file was also intentionally touched elsewhere this session per a linter note — review before
  committing.)
- `scripts/lab/LightingComposer.cs` (+6) — MINE, **WIP**: the ambient override at the end of
  `ApplyOvercastScaling()` (energy 0.9, sky-contribution 0.4, warm `AmbientLightColor`). This is the only
  thing that fixed the big blue region, BUT it is a hard per-frame OVERRIDE that bypasses the mood/time
  ambient (ignores the dynamic `Time.AmbientSky` and stomps `AmbientLightColor` every frame). It will alter
  EVERY mood/time-of-day, not just this test. **Decide:** integrate properly (de-blue + warm the ambient
  inside the mood system) or revert. To revert, delete the 6 added lines (the originals just above them are
  intact: `env.AmbientLightEnergy = BaseAmbient*lerp(...)` and `env.AmbientLightSkyContribution = Time.AmbientSky`).
- `scripts/lab/TerrainLabUI.Cli.cs` (+28) — **NOT MINE.** Belongs to a concurrent chat. Do NOT touch.

All temporary debug scaffolding HAS been reverted: the EMISSION normal/slope viz and the `SPECULAR=0` test
in `ground.gdshader` are gone (dm==0 is back to the live render), and the `env.FogEnabled=false` test is
back to `true`. The hydrology files were committed by that chat (`cc9201c`).

## Files / code paths to read (every line if needed, per the user)

- `shaders/ground.gdshader` — terrain surface. Vertex normal (chunk branch, central diff at fixed step,
  sampled at the MORPHED `wxz` ~line 235); fragment lighting (ALBEDO/ROUGHNESS=`rgh_aa`/NORMAL=`nrm_pert`);
  Toksvig roughness floor; `diag_mode` stepper (key `J`: 0 live, 1 RED no-tex, 2 GREEN +albedo, 3 BLUE
  +roughness, 4 YELLOW +normalmap).
- `scripts/lab/LightingComposer.cs` — THE lighting writer. Ambient (`ApplyOvercastScaling`, ~605-615),
  shadow tune (`_shadowTuned` block ~215-231: 8192 atlas, SoftHigh PCF, SSAO→0.6, cascade splits,
  ShadowNormalBias, BlendSplits, max-distance), `OrientSun`, fog (~297-319).
- `scenes/terrain_lab.tscn` — Environment (ssao_*, ssil_*, sdfgi_*, fog_*, ambient_*, tonemap, glow) and
  the Sun DirectionalLight3D (transform/elevation, shadow_* params). Camera has NO children (nothing is
  literally parented to the camera — the "camera casts a shadow" feeling is the screen-space/relief
  illusion).
- `scripts/lab/TerrainLab.cs` — `_giProxy` GI/shadow proxy (PlaneMesh, `ShadowsOnly` when on; it is OFF in
  CDLOD mode — log "GI/shadow proxy off"); CDLOD wiring; `SetCameraWorld`.
- `scripts/lab/Cdlod*.cs` — chunk MeshInstances (check `CastShadow` — currently DEFAULT ON → terrain
  self-shadows), per-chunk AABB (shadow cascade depth), morph/`morphK`, renderOrigin.
- `scripts/lab/AerialPerspective.cs` + `shaders/aerial_screen.gdshader` + `shaders/atmosphere_aerial.glsl`
  — AT-2 aerial perspective (transmittance multiply, default ON, sky-gated).
- `scripts/lab/GodRaysScreen.cs` + `shaders/godray_screen.gdshader` — god rays (default ON).
- `scripts/lab/TerrainLabUI.Process.cs` — all live debug keys; per-frame camera/sun pushes; default-on
  flags (`_godraysOn=true`, AT-1/AT-2 default on).

## Debug aids (verify each does what its name says before trusting it)

Live keys (windowed): `J` diag_mode stepper · `G` detail-fade (now default OFF) · `V` LOD tint · `B` SSAO ·
`N` SSIL · `M` SDFGI · `,` sun shadow · `.` cloud shadow · `/` god rays · `O` atmosphere AT-1 ·
`K` aerial AT-2 · `L` inspect/studio light · `H` water overlay (hydrology chat's).
CLI: `--cdlod=1` `--clouds=0` `--ssao=0/1` `--aerial[=0/1]` `--aerialstr=N` `--nofog` `--aerialcheck`
(one-shot froxel readback) `--cam=x,y,z,pitch,yaw` `--auto-shot=C:/path.png`. User args need a bare `--`
separator. `--auto-shot` needs an OS path, not `user://`.

## Method lessons from this session (the user values these)

- The user's EYE on the LIVE scene is the gate; downscaled auto-shots mislead. But for OBJECTIVE
  measurement (is the ground sloped? is the LUT finite?) a probe/self-check beats eyeballing.
- Don't trust a recorded diagnosis — re-verify the probe itself (e.g. the SSAO 2.0 in the scene is NOT what
  runs; it's 0.6 at runtime). 
- The single most useful discriminators here were: UNLIT render (lighting vs geometry), the AMBIENT CRANK
  (missing-light vs multiply/occlude), and screen-locked-vs-world-locked. Use those framings.
- We never got a clean SINGLE-toggle isolation of the residual line. Get that before changing code.
