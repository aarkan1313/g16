# GM3 — Within-area variation (procedural patches) — Design

Date: 2026-06-20. Part of the GROUND roadmap (`specs/2026-06-20-ground-roadmap-to-aaa-design.md`),
phase **GM3**. This is the **heavy shader lever** the user flagged ("masks, shaders, blending,
tile-to-tile matter as much or more than textures"). It is the *real* fix for the original
complaint — "the ground doesn't have enough variety within an area" — that Unit 4 (context
breakup) failed to deliver.

> **Status: DESIGN — awaiting user review.** Build order: do GM1 (palette) live-audit + GM2
> (height maps) first per the roadmap, unless the GM1 audit shows variation should jump ahead.
> This spec is drafted now so the design is ready and reviewable.

## The problem, precisely

Within a single role's region — e.g., a long uniform slope that is all the "slope" role
(gravel) — the surface renders **one material** (the weightmap path blends only the top-2
*roles*, d0/d1; inside a uniform area d1 is weak, so you see ~one material). Real ground is
never uniform: the same gravel varies in tone, wetness, compaction, coarseness, and is broken
by patches of sand, dirt, scree. **GM3 adds that within-area variation.**

Why Unit 4 couldn't: its masks were `f(heightfield)` (≈constant within a uniform area) and baked
4 m-coarse. **GM3's signal must be a fragment-side, multi-scale, world-position noise field** —
infinite detail, no baked grid, and it varies *within* the area by construction. (This is the
banked Unit-4 lesson: right space, right resolution.)

## What already exists (don't duplicate)

- `macro_color` (Color tab, default on): broad multi-octave world-pos noise drifting albedo
  **value/hue/sat** (`macro_val_amp` 0.22, `macro_sat_amp` 0.18, `macro_hue_amp` 0.05,
  `macro_scale2` 170 m). So a *single-scale, albedo-only* within-area color drift already exists.
- Anti-repetition (`ar_sample_wp`): kills sub-meter texture tiling. ✅
- The weightmap blend (d0/d1 roles + height interlock). ✅
- 7 role material slots `z{0..6}_{alb,nrm,rgh,ao}` via `trip_alb_by(role,…)`; `SetZoneMaterial`.

**The gap:** variation is albedo-tint-only, single-scale, and never changes the *surface
character* (roughness, relief) or introduces a *second material*. That's why areas still read
uniform — same gravel, faintly re-tinted.

## Goal

Make a single uniform area read varied at close/mid range: multi-scale tonal + **surface-character**
variation (rougher/smoother, drier/damper, coarser/finer patches) everywhere, plus optional true
second-material patches where the sampler/perf budget allows — all fragment-side, organic
(domain-warped), distance-faded, fully tunable, behind a toggle. No tiling, no hard/square edges.

## Approaches (with recommendation)

**Approach A — Material-property modulation (RECOMMENDED core).** A multi-scale, domain-warped
world-position noise field modulates the *already-fetched* material's **albedo value+sat, a small
hue push, roughness, and normal-detail strength** — so patches read as genuinely different ground
*character*, not just tint. Essentially `macro_color` promoted to a real within-area layer that
touches surface properties at 2–3 world scales. **No extra samplers, no extra texture fetches —
near-free.** Sidesteps the sampler-budget wall (already ~28 samplers). This alone should make a
uniform slope read varied because varying roughness+value is what real ground does.

**Approach B — Selective true material patches.** For the 1–2 most-seen ground roles, add ONE
variant material, blended with the primary by a patch-noise mask (height-interlocked edge like the
role blend, so transitions read natural). Dramatic (actually-different material), but each variant
costs a material's worth of samplers + a fetch → with ~28 samplers already, doing this for all 7
roles risks the limit. **Scope to dominant-role-only, branch-guarded, distance-faded; or defer
until C.**

**Approach C — Texture-array material set (the scalable endgame).** Move the role/variant
materials into a `sampler2DArray` (one sampler, N layers). Removes the sampler wall, enables many
materials + patches cheaply, and is the same structure RVT/AAA terrains use. **Bigger refactor;
ties to GM6 (RVT/chunks).** Not now — but A is designed to compose with C later.

**Recommendation:** ship **A** as GM3 (cheap, big subjective win, no architectural risk), measure
its ceiling live; add **B** dominant-role-only if A isn't enough; pursue **C** when GM6/RVT lands.

## Design (Approach A)

**Architecture:** a new fragment helper `groundVariation(wp, role)` produces a small struct of
modulation factors from a **2–3 octave, domain-warped, world-XZ noise field** (reusing the
existing `vnoise`/warp idiom). Applied to the role's sampled `alb/rgh/nrm` *after* the weightmap
blend (so it modulates the final composited surface, decoupled from the top-2 math — the Unit-4
overlay lesson: continuous, post-blend, no weight-boost). Distance-faded via `distanceWeight`.

**Noise field (the signal):**
- **Macro patches** (~30–80 m, domain-warped): the dominant "this part of the slope is drier/
  lighter/coarser" character. Drives the largest value/roughness swing.
- **Meso patches** (~6–15 m): medium breakup so mid-range isn't a smooth gradient.
- (Sub-meter is already handled by anti-repetition — don't touch it.)
- Domain-warp both octaves by a low-freq field so patches are organic blobs, not iso-noise.

**What it modulates (the "character"):**
- `alb` **value** ±`var_val` and **saturation** ±`var_sat` (drier patches lighter/desaturated).
- `alb` **hue** a tiny ±`var_hue` (warm/cool drift; keep small to avoid rainbowing).
- `rgh` ±`var_rough` (compacted-smooth ↔ loose-coarse) — **this is the key add over macro_color**;
  varying roughness is what makes patches read as different surface under light.
- `nrm` detail **strength** ×(1±`var_nrm`) (coarser patches read bumpier).
- All amounts per-channel sliders; a master `variation_amt`; `variation_on` toggle (default on);
  `var_far_fade` distance fade.

**Interface:**
```glsl
struct GroundVar { float val; float sat; float hue; float rough; float nrmAmp; };
GroundVar groundVariation(vec3 wp);   // multi-scale warped noise → modulation factors, distance-faded
```
Applied in the weightmap branch after `alb/rgh/nrm` are composited, before macro_color (or fold
macro_color into it). Pseudocode:
```glsl
GroundVar gv = groundVariation(wp);
alb = applyValSatHue(alb, gv.val, gv.sat, gv.hue);
rgh = clamp(rgh + gv.rough, 0.0, 1.0);
nrm = normalize(mix(vec3(0,0,1), nrm, 1.0 + gv.nrmAmp));   // scale tangent-space detail
```

**Relationship to macro_color:** GM3-A supersedes/absorbs it — `macro_color` becomes the
albedo-value/sat/hue sub-part of `groundVariation`, now joined by roughness+normal at multiple
scales. Keep the existing `macro_*` controls wired to the corresponding `var_*` for continuity, or
migrate them. (Decide during implementation; default to keeping current look at amt=current.)

**Per-role tuning (optional, cheap):** scale the variation amount per role (snow varies little,
gravel/dirt vary a lot) via a small per-role multiplier in the palette JSON — no extra fetches.

## Perf
Approach A is **near-free**: one extra multi-octave noise eval + a few `mix`/clamp on
already-fetched values; no new samplers or texture fetches. Distance-fade caps far cost. Profile
`--profmove`; expected < 0.3 ms. (B/C carry the real cost and are out of this phase.)

## Integration seams (verified 2026-06-20)
- `shaders/terrain_lab.gdshader`: weightmap branch (the `alb=mix(trip_alb_by(d0..),..)` block);
  `vnoise`, domain-warp idiom, `distanceWeight`, `macro_color` all present to reuse.
- Controls: `data/lab_controls.json` Color/Detail tab (`var_*` sliders, `variation_on`,
  `var_far_fade`); live `param`s (no rebake — fragment-side).
- No C# bake change (fully fragment-side). No new samplers (A).

## The gate (user, live)
A single uniform slope reads **varied** at close/mid — tonal AND surface-character (rougher/
smoother, drier/damper) patches, organic blobs, no tiling, no hard/square edges, gradual, no
shimmer in motion; far unchanged. A/B `variation_on`. If A's ceiling is "nice but I want actual
different materials in patches," that greenlights B (dominant-role) / C (arrays).

## Decision points for review
1. **A-core now, B/C later?** (recommended) — vs. push straight to true material patches (B/C,
   bigger, sampler-budget refactor).
2. **Absorb `macro_color` into `groundVariation`** vs. keep it separate and add variation alongside.
3. **Per-role variation multipliers** in the palette JSON now, or flat global first?

## Self-review
- **Scope:** single focused phase (within-area variation, Approach A); B/C explicitly deferred with
  rationale — not a multi-subsystem spec.
- **Placeholders:** none; the GLSL interface + integration seam are concrete and verified.
- **Consistency with the roadmap:** implements GM3's "within-area procedural patches"; honors the
  banked Unit-4 lesson (fragment-side, multi-scale, world-pos, continuous post-blend, no weight-boost);
  respects the sampler-budget reality flagged in the weighting note.
- **Honesty:** A is the cheap-first move; it may not fully satisfy "different materials in patches"
  — that's B/C, gated on this phase's eye-result and the texture-array refactor.
