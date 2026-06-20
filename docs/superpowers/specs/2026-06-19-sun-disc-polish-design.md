# Spec: Sun Disc Polish (Stage 1 of the Sun & Light arc)

Date: 2026-06-19. Status: APPROVED (design). Owner: TBD (new session).
Companion: handoff `docs/superpowers/handoffs/2026-06-19-sun-light-arc-handoff.md`; plan (to be written).
Touches: `shaders/cloud_sky.gdshader` (the `sky()` shader), `scripts/lab/TerrainLabUI.Moods.cs`,
`scripts/lab/CloudVolume.cs` (sun uniforms to the sky material), `data/lab_controls.json`,
`data/lighting_moods.json`, `data/cloud_presets.json`.

## Why this exists

The lighting foundation is strong (per-mood sun + 4-cascade shadows, SDFGI/SSIL/SSAO, AgX, aerial+height
fog, color grade). But the **visible sun disc** is a flat white dot: in `cloud_sky.gdshader` it's a tight
`smoothstep` disc plus a single `pow(cosSun, 220)` glow, uniform-bright, no limb darkening, no
corona/halo structure, no horizon reddening. It reads as "a bright circle," not "the sun." This is the
first, most-visible, self-contained stage of the larger Sun & Light arc (Stage 2 = time-of-day driver;
Stage 3 = night + celestial bodies — see the handoff).

**Art-direction target (user, 2026-06-19): full-range tunable.** Physically-grounded default, every piece
on a knob so the look can be pushed realistic → stylized → fantasy (colored/multiple suns in a later
stage). Same data-driven ethos as the rest of WG16.

## What the sun renders today (the starting point)

`cloud_sky.gdshader` `sky()`:
- Background gradient `background(rd)` from `sky_top`/`sky_horizon`/`ground_color` (mood-driven).
- Sun added into the background: `cosSun = dot(rd, LIGHT0_DIRECTION)`; `disc = smoothstep(0.9994, 0.9998,
  cosSun)` (~0.6°); `glow = pow(max(cosSun,0), 220.0)*0.5`; `bg += LIGHT0_COLOR * sun_disc_energy *
  (disc*12 + glow)`.
- Clouds composite OVER the sun via premultiplied alpha: `outColor = bg*(1-a) + cloud.rgb`, where the
  cloud dome `cloud_rd_tex` (rgb=radiance, a=alpha, lat-long) is sampled by view dir. So thin cloud over
  the sun already partially occludes it (soft alpha) — but only dims, never reddens, and the disc itself
  has no internal structure.

`sun_disc_energy` is intentionally decoupled from `LIGHT0_ENERGY` (overcast dims the directional light's
ground energy but the visible disc must stay bright). Keep that decoupling.

## Design — four layered HDR pieces (replaces the flat disc + lone pow)

All computed from `cosSun` (angle between view dir and the sun) and `sunElev` (sun elevation; derive from
`LIGHT0_DIRECTION.y` — verify the sign against the existing disc placement, which already resolves cosSun≈1
at the sun). Everything stays in real HDR so the existing Environment glow blooms the bright cores
naturally (do NOT fake the outer bloom in-shader).

1. **Limb-darkened disc.** Within the disc angular radius, brightness falls from ~1.0 at center to
   ~`limb_floor` (default ~0.6) at the rim using a radial term (e.g. `pow(mu, limb)` where `mu` is the
   normalized cos of the angle from disc center), and the rim is shifted slightly warm. Replaces the flat
   `disc*12`.
2. **Corona.** A tight, intense bloom hugging the disc edge with a fast falloff (short angular reach, high
   intensity) — the searing inner ring that feeds the glow. Separate `corona_size` / `corona_energy`.
3. **Atmospheric halo.** A wide, soft, warm forward-scatter glow around the sun (replaces the lone
   `pow(...,220)`), `halo_size` (angular reach) + `halo_energy`. This is what makes the region near the
   sun read bright/hazy and what warms the nearby sky. Stays LOCAL to the sun (does not recolor the broad
   sky gradient — that's Stage 2).
4. **Horizon reddening + growth.** A `sunset = smoothstep(redden_onset, 0.0, sunElev)` factor (0 high sun,
   →1 at horizon) drives: (a) a warm→orange→deep-red tint applied to disc+corona+halo color on a
   `redden_strength` curve, and (b) a disc/corona/halo size scale (`horizon_grow`, default subtle). This
   is the day/night seed.

## Design — soft optical-depth cloud interaction

Compute the sun's total contribution (`sunColor`) separately, then attenuate it by the cloud's optical
depth at the SUN's direction before compositing, so the transmitted sun dims AND reddens through thin
cloud and vanishes through thick:
- Sample the cloud dome at the sun direction: `aSun = texture(cloud_rd_tex, dir_to_uv(LIGHT0_DIRECTION)).a`
  (the coverage/transmittance there).
- Transmitted sun = `sunColor * (1 - aSun)` for the disc/corona (occluded by cloud in front), tinted by a
  warm extinction `mix(white, warm_extinction_tint, aSun * cloud_redden)` so partial cloud reddens it.
- The **halo** uses a softer/partial occlusion (a fraction of `aSun`) so the glow still bleeds around
  cloud edges (the "sun behind a cloud" rim), since the halo spans a wider cone than the disc.
- Composite as today (`bg*(1-a) + cloud.rgb`) — the sun now lives in `bg` already cloud-attenuated, so the
  existing premultiplied over-composite still holds. Verify no double-occlusion (the sun is attenuated by
  `aSun` once; the broad composite uses the per-pixel dome alpha for the rest of the sky).

## Controls (Light tab, registry-driven — same pattern as everything else)

New `lab_controls.json` rows (type `scenef`/`scene`, routed via the existing scene-uniform path → set on
the sky ShaderMaterial through `CloudVolume`/Moods), all with physical defaults:
`sun_disc` (size, exists) · `sun_energy` (exists) · `sun_limb` · `sun_corona_size` · `sun_corona_energy` ·
`sun_halo_size` · `sun_halo_energy` · `sun_redden` (strength) · `sun_redden_onset` (elevation) ·
`sun_horizon_grow` · `sun_cloud_redden`. Fold sensible values into the 6 moods (`lighting_moods.json`) and
add 1–2 sun-showcase entries to `cloud_presets.json` (e.g. "Golden Hour Sun", "High Noon"). No hardcoded
look — defaults physical, knobs cover stylized/fantasy.

Uniform plumbing: the sky shader gets the new uniforms; `CloudVolume` (which owns the sky material) gets
setters; Moods/ApplyCloud route to them. Follow the existing `sun_disc_energy` precedent.

## Scope

IN: the four-piece sun visual, horizon reddening+growth, soft optical-depth cloud reddening, the controls
+ mood/preset wiring, all in `sky()`. OUT (later stages): whole-sky atmospheric color as f(sun angle)
(Stage 2), the time-of-day driver / sun travel (Stage 2), moon/stars/night (Stage 3), the
distant-sky/horizon-gradient rework (separate roadmap item), and any cloud density/fuzziness work
(separate end-of-cloud pass — user flagged clouds read too nebulous/fuzzy on 2026-06-19).

## Acceptance

- The sun reads as a believable glowing sun (limb-darkened disc + tight corona + soft warm halo), not a
  flat dot — judged in the clear-sky `--clouds=0 --lookatsun` view and in motion.
- Lowering the sun (sun height slider / a low-sun mood) reddens + slightly grows the disc+halo on a smooth
  curve; high sun is neutral-white. No banding, no hard ring at the disc/halo edge.
- A cloud crossing the sun dims AND reddens it softly (thin → dim+warm, thick → gone), with the halo still
  bleeding around the cloud edge. Verify against the cloud dome (vary coverage).
- Every piece is a working Light-tab slider; the 6 moods set coherent sun looks; `--shadowcheck` /
  build-clean unaffected (this is sky-shader + uniforms only).
- Default behavior with clouds off and a high sun looks natural (not over-bloomed / not a white blob).

## Risks

1. `LIGHT0_DIRECTION` sign / elevation derivation — verify against the existing disc (cosSun≈1 at sun);
   get `sunElev` right or reddening triggers at the wrong altitude.
2. Double-occlusion of the sun (cloud attenuating it once in `bg`, then the over-composite again) — keep
   the sun's `aSun` attenuation and the broad dome composite consistent; A/B against clouds-off.
3. HDR + glow tuning — the corona is intentionally very bright; ensure the Environment glow blooms it
   without blowing the whole frame (the glow HDR threshold is 1.6; corona energy interacts with it).
4. Halo vs Stage-2 boundary — keep the halo LOCAL (don't recolor the broad sky), or it pre-empts the
   time-of-day atmosphere and the two will fight later.
