# Terrain Self-Shadow — World-Anchored Heightfield March — Design

**Date:** 2026-06-27
**Status:** Approved (design); plan + build pending.
**Supersedes the active path of:** the crude single-sample `hz_on` macro march (`shaders/ground.gdshader`),
the just-disabled near-CSM owner (`CsmCastOwner`, parked at tag `shadowless-clean-2026-06-27`), and the
"CSM near + horizon far" hybrid framing of `docs/superpowers/specs/2026-06-27 shadow-rebuild` lineage.
**Baseline to build from:** tag `shadowless-clean-2026-06-27` (verified shadowless; registry kept, all owners off).

## Goal

Full-range, world-locked terrain self-shadows at the Enshrouded bar: crisp near, soft far, believable at
every time of day, **no popping, no yaw drift** — built on the existing infinite CDLOD terrain without
re-entering the engine-CSM-on-terrain graveyard. Quality is a runtime dial (max for stills, dialed for flying).

## Why this approach (the AAA-pillared decomposition)

The two bugs that made shadows a graveyard — "shadows yaw when I turn" and "caster LOD ≠ visible LOD popping"
— are **specific to engine shadow maps**: they render a view-dependent projection of *mesh* geometry, and on
infinite CDLOD terrain the caster mesh is the wobbling LOD mesh. Every prior failure used engine CSM to shadow
the **terrain**. That is the wrong tool for terrain.

The AAA-correct decomposition separates two different problems:

- **Terrain self-shadowing** → a world-anchored **heightfield ray-march toward the sun** (this design). It
  samples a world-space height field along the world-space sun ray, so it is world-locked *by construction*
  (the yaw bug is mathematically impossible) and uses **no mesh casters** (the LOD-pop bug cannot occur). This
  is a standard, published terrain-shadow technique, and it is precisely what `docs/SHADOWS_AUDIT_2026_06_27.md`
  (Pillars-Aligned Direction, point 4) recommended: "a world-anchored heightfield/clipmap shadow cache rather
  than relying on visible CDLOD meshes as shadow casters … same answer far away, more detail near, no lit/unlit pop."
- **Object shadowing** (characters, flora, props, buildings) → engine CSM, **casting objects only, never
  terrain**, added later as a separate `NearCast` owner when objects exist. The registry already reserves
  `NearCast` for "future objects." Not in scope here (YAGNI — there are no objects yet).

Done at the AAA bar, the march is **min-max mipmap accelerated** (Tevs et al., "Fast Heightfield Shadows"
family): a min-max height pyramid lets the ray take big steps through open sky and refine only near potential
occluders — fast *and* miss-free, eliminating the thin-ridge step-over artifact that sinks a naive march. Soft
penumbra comes from cone/closest-approach contact hardening. This is not a compromise relative to a CSM hybrid;
it is the hybrid done correctly, with the terrain half (all the current content needs) built first.

## Architecture

### Ownership (no new engine shadow maps)

- **One owner: `TerrainSelfShadowOwner`** in the existing `ShadowSlot.FarCast` slot, replacing
  `HorizonMarchOwner`. It owns the entire terrain self-shadow path and nothing else. It never enables an engine
  shadow map (`sun.ShadowEnabled` stays false; `WantsSunShadow` is a `NearCast` concern only). Its `Tick` syncs
  the sun vector + quality uniforms onto the ground material; `DiagIds = ["terrain:selfshadow"]`.
- The disabled `CsmCastOwner` stays parked/removed. `NearCast` is reserved for a future objects-only CSM owner.
- `ShadowDiagnostics` / `--shadowcheck` continue to assert: the owner is declared, and **engine shadow
  draws/objects/primitives remain 0** (the whole point — terrain shadows cost zero engine shadow passes).

### The shadow function (lives in `shaders/ground.gdshader`)

A per-fragment function `terrain_self_shadow(world_pos, sun_dir) -> float occlusion[0,1]`, injected through
`AO` + `AO_LIGHT_AFFECT = 1.0` (the same hook today's `hz_on` uses at `ground.gdshader:496-509`) so it
attenuates **direct sun** without touching any engine shadow map. Replaces the `hz_on` block.

**Early-outs (keep it ~free when it can't matter):**
- Sun below horizon (night) → no shadow, return 1.0.
- `dot(N, sun_dir) <= 0` → surface already faces away; Lambert already shadows it → return 1.0 (no march).
- Sun-elevation gate (cheap near noon; cost grows as the sun lowers and marches lengthen — expected).

**March `world_pos → sun_dir`:**
1. **Min-max mip traversal (acceleration):** at each step compare the ray height to the current mip cell's
   *max* stored height. Ray above max → no occluder possible → big step / coarser mip. Ray below max → descend
   to a finer mip and step smaller. Guarantees no missed occluder while skipping empty sky.
2. **Two-scale height source:** first ~`near_dist` meters sample the **fine per-chunk field-cache height**
   (crisp near detail); beyond that, the **macro min-max pyramid** (long ridge shadows). Blend the handoff over
   a short band so there is no seam.
3. **Soft penumbra:** accumulate occlusion from the ray's closest *angular* approach to occluders; penumbra
   width grows with distance-to-occluder (PCSS-style contact hardening) → soft far, harder near. Output is a
   smooth `[0,1]`, not a binary hit.
4. **Bias:** start the ray offset slightly along `N` and `sun_dir` to kill grazing-angle self-shadow acne.

### New data: the min-max height pyramid

A small GPU-compute bake producing a **min-max mip pyramid of the macro height field** (and, if Slice 2 needs
it, of the near field-cache height). Each texel at mip *m* stores `(minH, maxH)` over its footprint. Rebaked
only when the field changes (seed / region params) — effectively static at runtime. Follows the existing
field-cache bake pattern (`imageStore`-direct compute, no readback; bake windowed — headless has no local
RenderingDevice). Sampled by the march; never read back to the CPU.

### Control surface

A **`Terrain Shadow` lab tab** with the quality dial: `selfshadow_on` (bool), `steps` (max march steps),
`max_dist` (m), `near_dist` (fine→macro handoff, m), `softness` (cone scale), `strength`. CLI mirrors:
`--selfshadow=0|1` plus per-knob overrides (extending the existing `--horizon` plumbing in
`TerrainLabUI.Cli.cs`). **Default OFF** until each slice is flown and passed; the flying default quality is
chosen at the eye-gate. World-lock is provable via the existing `--yawab`.

## Build slices (one at a time, default-OFF, flown before default-on)

This is the discipline that was missing every prior attempt — encoded here as the build contract.

1. **Slice 1 — Far ridge shadows.** Min-max macro pyramid + accelerated march; hard shadows; far band only
   (extends today's macro march). Biggest payoff (low-sun ridge drama), lowest new risk.
   *Gate:* long ridge shadows appear at low sun; `--yawab` world-locked; no pop/flicker flying + teleporting;
   `--shadowcheck` green with 0 engine shadow draws; record `--profmove` cost.
2. **Slice 2 — Near crisp detail.** Add the fine field-cache height for the `near_dist` band + seamless blend
   to the macro march. *Gate:* crisp near self-shadow; clean near→far handoff (no seam); world-lock holds; cost.
3. **Slice 3 — Soft penumbra.** Cone / closest-approach contact hardening. *Gate:* soft far, harder near, reads
   natural in motion at multiple times of day; no shimmer; cost.
4. **Slice 4 — Dial + integration.** Quality dial, lab tab, CLI, default tuning, retire `HorizonMarchOwner` →
   `TerrainSelfShadowOwner`, doc + memory update. *Gate:* pick the flying default; final A/B max-vs-default.

Build at most ONE slice past the last passed eye-gate (project discipline rule).

## Testing / verification (per slice)

- **`--yawab`** — world-anchored invariant (a shadow stays glued to its caster geometry across yaw). Must pass.
- **`--shadowcheck`** — registry/auditor agreement; owner declared; **engine shadow draws/objects/primitives = 0**.
- **Graveyard motion test** — fly + teleport across LOD transitions; expect *no* pop/acne (no mesh casters).
- **`--profmove`** — cost curve of the dial at default and max; against the 8 ms budget.
- **The real gate** — user's live eye, in motion, at close / mid / far and several times of day.

No TDD (GPU/visual project convention); verification is the live eye-gate + the mechanical CLI self-checks.

## Risks & mitigations

- *Thin-occluder step-over* → min-max mip traversal makes the march miss-free (the core reason for the pyramid).
- *Per-pixel cost at low sun / high quality* → the quality dial + early-outs + sun-elevation gating; cheap floor
  is today's macro march (~+1.7 ms low sun).
- *Near→far seam* → blend the field-cache↔macro handoff over a band; eye-gated in Slice 2.
- *Grazing-angle acne* → normal/along-ray start bias; eye-gated in Slice 1.
- *Bake can't run headless* → bake windowed (known project gotcha); `--import` only compile-checks.

## Explicitly out of scope (noted, not silent)

- **Object/prop/flora shadows** — a future `NearCast` objects-only CSM owner, when objects exist.
- **Cloud→terrain shadows** — deleted in the audit; its own future spec (world-anchored transmittance cache).
- **Contact AO** (`ShadowSlot.ContactAo`) — small-radius cavity darkening; separate later owner.
- **Shadows for water surfaces** — belongs to the water arc.
