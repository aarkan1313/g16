# Terrain Shadow Hybrid Rebuild — Design

- **Date:** 2026-06-27
- **Branch:** experiment/presentation
- **Status:** Design approved (brainstorming); pending spec review → writing-plans
- **Supersedes the runtime approach of:** the single `hz_on` heightfield march in `shaders/ground.gdshader`
- **Builds on:** `docs/SHADOWS_AUDIT_2026_06_27.md` (catalog + Pillars-Aligned Direction), the per-chunk GPU field-cache, `ShadowDiagnostics`

## Context & Motivation

The reported "shadows shift/vanish when I yaw" was diagnosed (2026-06-27) as **not a view-dependent bug**: `horizon_shadow()` is provably yaw-independent (world surface XZ/height, world sun direction, camera *position* only). The visible symptom was the day/night clock still running on review key 4 (fixed in commit `238700d`). The math we have is world-anchored — but the *system* is far below the quality bar.

The user benchmarked against **Enshrouded** ("the infinite world I want to make, same or slightly better quality") and confirmed the target behavior: shadows must be world-locked (never follow the camera). The current shadow is deliberately cheap — one coarse heightfield sample, gated to low sun (`sun_up < hz_sun_gate ≈ 0.35`), fading out by ~4 km — so in a typical elevated view it contributes essentially nothing (verified: `--hzmask=1` renders an almost entirely black/no-shadow mask).

Decision (user): **rebuild as a full hybrid** — engine cascaded shadow maps (CSM) for the crisp near band, a world-anchored heightfield far band to the horizon, and a contact-AO owner — under a modular, tunable registry that is ready for future "decorations" without a rewrite. Lighting *look* is out of scope and must not change.

## Goals & Success Criteria

1. **World-anchored (hard invariant).** Fixed camera position + fixed luminaries ⇒ identical shadows under any yaw/pitch. Testable and enforced.
2. **All-day, multi-source.** Any luminary above the horizon with enough energy casts (primary sun, moon, C3 extra suns/moons). No low-sun gate.
3. **Full view distance.** Shadows reach the horizon; a mountain shadows its valley even when both are far from the camera. No near-only fade.
4. **Crisp near edges.** Shadow-map quality near the camera; distance may soften naturally (physical penumbra).
5. **Contact AO.** Subtle world-space crevice/valley-floor/slope-base darkening, distinct from cast shadows.
6. **Modular & tunable.** One registry, pluggable owners, lab controls + presets + drift-free A/B keys. Owners can be toggled and swapped independently.
7. **Object-ready.** Future decorations (trees/structures/player) join as casters with no architectural rewrite.
8. **AAA / pillars.** Quality-first; perf is a gate, not a cap on ambition.

## Non-Goals

- **Object shadows now.** WG16 has no objects yet; we *architect* for them (CSM is the natural path) but build/tune only terrain.
- **Changing the lighting look.** The sun/weather/mood/celestial system is look-complete; shadows are an occlusion term layered on top. No BRDF or color-grade changes.
- **A new terrain streaming/LOD system.** We reuse CDLOD + the field-cache as-is.

## Architecture

### ShadowRegistry (the spine)

A single state object that owns the list of active shadow **owners** and enforces:

- **Invariant 1 — world-anchored:** every owner's output depends only on world geometry, world luminary directions, and camera *position*; never camera orientation. (See Testing.)
- **Invariant 2 — one owner per (band × concern):** no two owners darken the same pixel for the same reason; the near→far handoff is a single cross-fade.

It exposes per-owner cost counters and tuning, and reuses `ShadowDiagnostics` (`PROFILE-SHADOWS` / `LIVEPROFILE-SHADOWS`) as its auditor: nothing casts an engine shadow or sets `CastShadow != Off` except through the registry. `ShadowDiagnostics` already scans for rogue casters — the registry becomes the authority it audits against.

### The four owners

1. **Near cast — CSM** (terrain + future objects): Godot cascaded shadow maps on the primary luminary's `DirectionalLight3D`, capped to a near band.
2. **Far cast — horizon-map** (terrain): world-anchored, baked into the field-cache, near-band edge → horizon.
3. **Contact AO** (terrain): derived from the same horizon-map bake.
4. **Blend/arbitrate**: registry cross-fades Near→Far by camera distance and composes in `ground.gdshader`.

### Data flow

`LightingComposer` Luminary list → registry selects top-N shadow casters by energy → primary luminary drives CSM (`DirectionalLight3D.ShadowEnabled`) **and** the horizon-map far term; secondary luminaries drive horizon-map only. The field-cache compute bakes height + normal (today) **+ horizon map (new)** on chunk birth. `ground.gdshader` composes: engine CSM in the near band (automatic, default lighting), horizon-map shadow + AO via the existing `AO` / `AO_LIGHT_AFFECT` channel in the far band, smoothstep crossover between them.

## The Graveyard Fix (why the full hybrid is safe this time)

CSM-for-terrain was killed across WG1–15 (the project's terrain-LOD graveyard). Root cause from the audit: a **camera-centered caster ring selected per chunk** pulled coarse far chunks into the shadow pass whose LOD ≠ the visible LOD, so the caster surface and the rendered surface disagreed → shadow popping/acne in motion.

The fix is structural, not a tuning band-aid:

- **CSM covers only the near band, where CDLOD is uniformly finest**, so caster-LOD == visible-LOD *by construction*. We do not maintain a separate caster selection; the visible near chunks cast, gated by `directional_shadow_max_distance`.
- **Everything past the near band is the horizon-map owner.** CSM never touches a coarse far chunk.
- **Fallback** if a mid-distance band ever needs cast detail beyond CSM but sharper than the horizon map: cast from a **stable coarse shadow-proxy mesh** (the existing GI proxy pattern, a non-CDLOD mesh) rather than CDLOD chunks — a stable caster can't pop.

A dedicated motion test (fly + teleport-far) is part of the CSM phase's eye-gate specifically to prove the graveyard failure does not recur.

## Owner Details

### ① Near cast — CSM

- Re-enable `ShadowEnabled` on the primary luminary's `DirectionalLight3D` (currently forced false at `LightingComposer.cs:215`).
- Near CDLOD chunks flip `CastShadow.On` (currently forced `Off` at `CdlodTerrain.cs:414-415`); far chunks stay `Off` and are beyond `directional_shadow_max_distance` regardless.
- 2–4 PSSM cascades within the near band; PCF/softness tunable.
- **No custom-`light()` integration needed** — `ground.gdshader` uses Godot's default lighting, so the engine applies the CSM shadow term to the BRDF automatically.
- **Altitude-aware crossover:** WG16 uses a fast, often-elevated camera (not a ground game). The near-band distance scales with camera height (e.g. `max(near_floor, k · camHeight)`) so the CSM band always covers the on-screen near field. Exact curve is a tuning parameter.

### ② + ③ One bake — the horizon map

On chunk birth, the field-cache compute additionally bakes a **horizon map**: per texel, the maximum horizon-elevation angle in N azimuth bins (default N≈16), stored alongside the existing height/normal cache. Two reductions from one cache:

- **Far cast shadow:** for each casting luminary, sample its azimuth bin's horizon angle and compare to the luminary's elevation → soft occlusion, all-day, world-anchored, full-distance, multi-source (one lookup per luminary). No per-pixel ray-marching. Azimuth bins are interpolated to avoid banding.
- **Contact AO:** average the horizon angles over all azimuths → sky-aperture occlusion → crevice/valley darkening. Applied to ambient/indirect.

The current per-pixel `horizon_shadow()` march is retired as the author but kept available as a near-crossover refinement / validation oracle.

### ④ Compose & blend (`ground.gdshader`)

- Per pixel, camera distance picks the band weight `w = smoothstep(near0, near1, camDist)`.
- **Near (w→0):** engine CSM does the cast shadow automatically; the shader's horizon-map term is faded out (so no double-darkening). CSM is auto-limited to `directional_shadow_max_distance`.
- **Far (w→1):** no CSM (beyond max distance); the shader applies the horizon-map shadow as a direct-light attenuator via `AO` + `AO_LIGHT_AFFECT` (the mechanism used today).
- **AO** multiplies ambient/indirect everywhere, independent of the cast-shadow band.
- The two cast terms are complementary by construction (CSM full near / zero far; horizon-map zero near / full far), so a matched crossover is seamless.

## Multi-Source Luminaries

The registry reads the Luminary list and ranks by effective energy:

- **Primary** (highest-energy up luminary): CSM (near) + horizon-map (far).
- **Secondaries** (additional suns/moons): horizon-map only (cheap — extra lookups), no extra CSM passes by default. A tunable cap (default 1 CSM caster, up to ~3 horizon-map casters) bounds cost.
- At night, the moon becomes primary if it is the dominant up-source.

## Lab Integration & Tunability

- New `data/lab_controls.json` entries on a **Shadows** tab (or Light tab group): per-owner enables, CSM cascade count / near-band floor / altitude factor / PCF softness, horizon-map azimuth count (bake-time) / softness / strength / max distance, AO radius/strength, and the near→far crossover band.
- **Presets** for quick eye-gates (e.g. "near only", "far only", "AO only", "full").
- **Drift-free A/B keys** in `scenes/review.tscn` (extend the existing review keys): toggle each owner with time frozen and the scene held, so the diff is purely the owner under test.
- All controls route through the registry (`ILabControls`), so none silently no-op; bake-time params (azimuth count) trigger a field-cache re-bake.

## Testing & Verification

The defining lesson from prior shadow work: **drift-free A/B only.** Never compare two launches while the day/night cycle runs (the sun moves between them and fakes a difference).

- **World-anchored invariant test:** stationary camera, time frozen; capture each owner's raw contribution (the `dbg_hz_mask`-style emission overlay, extended per owner) at two yaw angles; assert the contribution on a given world point is identical. Automatable as a screenshot A/B with `Engine.TimeScale = 0`.
- **Graveyard motion test (CSM):** fly a path + teleport far; assert no shadow popping/acne at LOD transitions and no caster/visible mismatch.
- **`PROFILE-SHADOWS` owner audit:** exactly the intended owners report active; zero rogue casters; shadow draw/object/primitive counts match the enabled CSM band only.
- **Numeric terrain gates unaffected:** `--fieldcheck` stays `0 m` (height untouched), `--popcheck` Δh/Δn unchanged (shadows don't alter geometry).
- **Perf gates:** per-owner cost counters; frame-time percentiles via `--profile` stationary, at 5000 m/s, and at altitude; both low-sun and noon.
- **Eye-gates:** per owner and on the composed result, in motion, against the Enshrouded bar.

## Performance Budget & Considerations

- Frame budget target ~8 ms (current stationary ~7.2 ms; horizon march added ~1 ms). The hybrid is expected to cost more than the retired march; per the pillars, quality leads, but each owner ships behind a measured gate.
- **CSM** is the main new cost (a shadow pass). Bounded by the near-band cap, cascade count, and atlas size — all tunable.
- **Horizon map** is a per-texel lookup at shade time (cheap) plus a one-time bake cost on chunk birth (amortized, like the existing height/normal bake; throttled by the field-cache bake budget).
- **AO** is free given the horizon-map bake (a second reduction of the same data).

## Risks & Mitigations

| Risk | Mitigation |
| --- | --- |
| CSM-for-terrain popping (the graveyard) | Near-band-only CSM where LOD is uniformly finest; visible chunks cast (no separate ring); stable-proxy fallback; dedicated motion eye-gate. |
| Horizon-map softness/banding at the far field | Azimuth-bin interpolation; tunable bin count; keep the per-pixel march available as a sharper near-crossover refinement. |
| Crossover seam (near↔far) | Complementary terms by construction + smoothstep; A/B the crossover band; the AO term is continuous across it. |
| Bake cost / memory for the horizon map | Reuse the field-cache infra + bake budget; modest azimuth count; store compactly. |
| Perf at altitude / fast flight | Altitude-aware near band; per-owner gates at 5000 m/s and high camera; CSM cap. |
| Multi-source CSM cost blow-up | Default to 1 CSM caster; secondaries are horizon-map only. |

## Build Sequence (phases — each independently eye-gateable)

0. **Registry + invariant harness.** Build `ShadowRegistry` (owner list, invariants, cost counters); wire `ShadowDiagnostics` as auditor; add the world-anchored A/B test scaffold. No new shadow yet. (Audit: registry before effects.)
1. **Far owner — horizon-map bake + far cast shadow.** Extend the field-cache compute to bake the horizon map; sample it in `ground.gdshader` for the all-day, full-distance, multi-source far shadow; retire the `hz_on` march as author. Eye-gate + invariant test.
2. **Contact AO owner.** Second reduction of the horizon map → ambient darkening. Eye-gate.
3. **Near owner — CSM.** Primary-luminary CSM capped to the altitude-aware near band; near chunks cast. Graveyard motion eye-gate.
4. **Blend/arbitrate.** Distance crossover near↔far; seam-free. Eye-gate.
5. **Multi-source + tuning + presets + perf pass.** Top-N luminaries; lab controls/presets/A-B keys; measured perf gates.

## Acceptance Criteria

- Stationary + time-frozen: yaw/pitch produces byte-identical shadows (world-anchored invariant passes for every owner).
- Terrain casts shadows at noon and at low sun, from the primary luminary and at least one secondary, reaching the horizon.
- Crisp near shadows with no popping/acne while flying and teleporting far (graveyard test passes).
- Contact AO reads in crevices/valley floors without crushing the terrain into mud.
- `PROFILE-SHADOWS` shows exactly the intended owners, zero rogue casters; `--fieldcheck`/`--popcheck` unchanged.
- Perf gates met (or an explicit, accepted quality/perf trade recorded) at stationary, 5000 m/s, and altitude.
- No regression to the lighting look.

## Open Decisions (deferred to implementation/tuning)

- Horizon-map storage layout and exact azimuth count (quality vs bake cost/memory).
- Near-band crossover curve vs camera altitude.
- CSM cascade count + atlas size defaults.
- Whether the mid-distance stable-proxy caster is ever needed, or near-CSM + far-horizon-map fully covers the gap.
