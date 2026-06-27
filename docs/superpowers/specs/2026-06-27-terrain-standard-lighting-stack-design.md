# Terrain Standard Forward+ Lighting Stack — Design

**Date:** 2026-06-27
**Status:** Approved (user: "Yep lets just do it")
**Supersedes:** the custom heightfield-march terrain self-shadow approach
(`docs/superpowers/specs/2026-06-27-terrain-self-shadow-heightfield-march-design.md`).

## Problem

The custom `horizon_shadow()` ray-march in `ground.gdshader` produced undersampling
acne (diagonal stripes along the sun azimuth) and a hard, artificial terminator. It
has been a recurring graveyard across the project. The user decided to kill it and
pivot the terrain's lighting+shadows to the **standard Godot Forward+ stack**: engine
CSM sun shadows in a near bubble + SDFGI + SSAO/SSIL + fog, hiding the far cutoff with
atmosphere rather than chasing infinite shadow distance.

## Scope

- **KEEP, untouched:** the sky/cloud/day-night/sun-position arc (LightingComposer,
  moods, N-suns/N-moons, atmosphere AT-1/2/3, clouds, godrays, time system). It supplies
  the sky and the Sun's position/color/energy.
- **REPLACE:** the terrain's custom shadow and any custom grounding, with engine-native
  lighting. The Sun `DirectionalLight3D` stays owned by LightingComposer; we add engine
  CSM on top of that same node.

## Feasibility (verified against the code)

- `ground.gdshader` has **no `light()` function** — only `vertex()`/`fragment()`. The
  terrain is already lit by Godot's default lighting, so engine CSM, SDFGI, SSAO, SSIL
  affect it natively with no re-plumbing.
- `EMISSION` is used **only** in debug paths (`dbg_unlit`, `dbg_hz_mask`); no custom
  emission-fill fights the engine in the normal path.
- The custom shadow is a ~6-line hack on the `AO`/`AO_LIGHT_AFFECT` channel (ground.gdshader
  ~516–522) plus the `hz_*` uniforms — trivial to excise.
- The WorldEnvironment (`scenes/terrain_lab.tscn`) already exposes `sdfgi_enabled`,
  `ssao_enabled`, `ssil_enabled`, `volumetric_fog_enabled` (all currently `false`),
  tonemap ACES/AgX, Sky background. Adopting the stack = flip these on + tune.

## Key risks & mitigations

1. **CSM-on-CDLOD popping** (the graveyard: caster-LOD ≠ visible-LOD). Mitigation: keep
   `DirectionalShadowMaxDistance` small (~250m for fast flight). Inside the bubble CDLOD
   is uniformly finest LOD, so caster == visible by construction. Far terrain casts
   nothing (culled by max distance) and is covered by ambient + fog. Front-loaded motion
   eye-gate in Phase 1.
2. **SDFGI × floating origin.** The world snaps `renderOrigin` every 8192m. SDFGI follows
   the camera but the snap may re-seed/flicker. Isolated to Phase 4 and verified there.
3. **Shared Sun.** The day-night system drives the Sun angle/color/energy. If a low/odd
   day-cycle angle or the multi-sun (N-sun) setup makes shadows look wrong, that is the
   user's explicit "review the sun" trigger — handle case-by-case, do not redesign the
   sun arc preemptively.

## Architecture / Phases

Each phase is independently eye-gated before the next; default-OFF features are enabled
one at a time so exactly one variable changes per gate.

### Phase 0 — Strip & bank a clean baseline (mechanical)
- Remove from `ground.gdshader`: `horizon_shadow()`, all `hz_*` uniforms, the
  `AO`/`AO_LIGHT_AFFECT` shadow hack, and the `dbg_hz_mask` path. Terrain returns to plain
  standard PBR (ALBEDO + normal + roughness), engine-lit.
- Retire march scaffolding: `HorizonMarchOwner`, `TerrainLabUI.Shadows.cs` registry wiring,
  `--horizon=`/`--hzmask=` CLI, the hz_* lab controls, `ShadowRegistry`/`IShadowOwner`/
  `ShadowSlot`/`ShadowCheck`/`ShadowDiagnostics` march-specific pieces. The "one shadow
  caster" rule moves into LightingComposer (Phase 5), so the registry abstraction is no
  longer needed.
- Rework review key-4 into the new shadow A/B: same frozen-sun vantage, but the toggle
  flips the **Sun's `ShadowEnabled`** (engine CSM) on↔off with lighting held constant.
  Revert the recent hz strength/ambient/camera experiments.
- **Commit + tag `standard-stack-baseline`** — the clean slate banked BEFORE any engine
  shadow/GI is re-enabled (defeats the recurring "rebuild re-enables shadows before a clean
  checkpoint" failure).
- Gate: terrain still renders correctly lit & normal; no shadow; build + `--review=4` clean.

### Phase 1 — Sun CSM (core deliverable, graveyard-safe)
- Sun `DirectionalLight3D`: `ShadowEnabled=true`,
  `DirectionalShadowMode=PSSM 4 Splits`, `DirectionalShadowMaxDistance≈250`,
  `DirectionalShadowBlendSplits=true`, splits ≈ 0.08 / 0.2 / 0.5, `ShadowFadeStart≈0.85`,
  `ShadowNormalBias` tuned **before** `ShadowBias`, soft shadow filter, shadow map 4096
  (8192 only for the High preset test).
- CDLOD: cast shadows **only from the finest-LOD chunks** near the camera; coarser/distant
  chunks `CastShadow.Off`. Reintroduce a clean caster-level gate (the prior
  `SetShadowCasterMinLevel` mechanism, rebuilt minimal).
- **Motion eye-gate (make-or-break):** fly + yaw through the world; confirm no shadow
  popping, swimming, or cascade-band crawl. If it pops, the bubble/caster-gate is wrong —
  fix before proceeding.

### Phase 2 — Grounding
- Env: `ssao_enabled=true` (contact darkness), `ssil_enabled=true` (short-range bounce),
  ambient source = Sky. Tune SSAO radius/intensity so near terrain reads grounded without
  crushing. Gate: contact darkness reads; no haloing/over-darkening.

### Phase 3 — Fog (hide the cutoff)
- Use **one** fog system to hide the ~250m shadow cutoff and add depth. The sky arc already
  has aerial perspective (AT-2) and a volumetric-fog option; extend/tune ONE of those rather
  than stacking the env `volumetric_fog` on top (avoid double-haze). Gate: shadow cutoff is
  not perceptible; fog reads as atmosphere, not a wall.

### Phase 4 — SDFGI bounce (high-quality desktop)
- Env: `sdfgi_enabled=true`, read sky light on, 4 cascades, Y-scale ~50–75%, bounce feedback
  ~0.5, max distance below camera far, use-occlusion only if leaks appear.
- **Verify across the 8192m floating-origin snap** (fly past a snap boundary; watch for GI
  re-seed flicker). Gate: bounce light reads outdoors; stable across a snap; perf acceptable
  on the High preset.

### Phase 5 — One-caster rule + presets
- LightingComposer owns shadow casting: only the **primary** luminary casts. Crossfade
  sun→moon at dusk/dawn (`ShadowEnabled` + a fade), moon shadows softer/weaker; extra fantasy
  suns never cast. Optionally `SKY_MODE_SKY_ONLY` for a visible-but-non-lighting body.
- Ship two presets:
  - **High (desktop):** full stack — CSM (8192 option), SDFGI, SSAO, SSIL, volumetric/quality
    fog, longer shadow distance.
  - **Performance:** SDFGI off, SSIL off, cheap height/depth fog, shorter shadow distance,
    soft-low/medium filter, moon shadows off.

## Out of scope
- The sky/cloud/atmosphere/time/multi-luminary look (kept as-is).
- Torch/local-light shadow budgets (no local lights exist yet; revisit when objects exist).
- Infinite-distance terrain shadows (explicitly rejected — bubble + fog instead).

## Success criteria
- No diagonal acne / hard terminator (the march is gone).
- Crisp near sun shadows that stay world-locked under fly + yaw (no pop/swim).
- Grounded, non-floating terrain (SSAO) with bounce fill (SDFGI), far cutoff hidden by fog.
- Clean `standard-stack-baseline` tag exists as a revert point.
- High + Performance presets both ship.
