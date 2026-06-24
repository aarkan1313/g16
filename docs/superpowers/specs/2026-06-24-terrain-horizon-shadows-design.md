# Terrain Heightfield Horizon Shadows — Design

Date: 2026-06-24
Branch: `experiment/presentation`
Scene: `scenes/terrain_lab.tscn` (Godot 4.6.2 mono, Vulkan, CDLOD terrain)

## Problem & context

Distant terrain casts **no long-range shadow**. Verified drift-free this session: the directional shadow
*map* contributes only ~2.6% of pixels at midday / ~5% at low sun, **all near/mid contact shadow**, and it's
**range-independent** (500 m = 6 km = 24 km, visually identical) — distant terrain casts nothing castable at
coarse CDLOD LOD. The rest of the dune/crater darkening is **diffuse N·L** (correct directional lighting),
not the shadow map. So a cascade-range knob is a confirmed no-op; the AAA way to get a ridge shadowing a
valley km away at golden hour is a **heightfield horizon-shadow march**: per terrain pixel, march the
heightfield toward the sun and occlude the direct light where the terrain rises above the sun ray.

## Goal & success criteria

- **Long-range terrain self-shadow**: at a low sun, ridges/dunes cast soft shadows into distant valleys —
  visibly more than the shadow map alone gives.
- **World-locked + LOD-independent**: shading welded to the world, independent of mesh LOD or shadow range
  (it reads the analytic field, not the mesh).
- **Cost-bounded**: must not meaningfully regress the (mesh-bound ~27 ms) frame. A `--profmove` perf check is
  a HARD gate. Gated off at high sun (where long shadows don't exist) → ~free most of the day.
- **Modular + tunable** (pillars): a self-contained shader function + a shared cheap macro-height function +
  uniform knobs; `hz_on=0` → byte-identical to today. Live toggle + sliders.

Non-goals: replacing the shadow map (kept for crisp near/mid contact); precomputed per-region horizon maps
(approach C — deferred to an infinite-world pass); fine-relief self-shadowing (that's near contact, already
the shadow map's job).

## Approach (chosen: B — cheap macro-proxy march + AO injection)

Rejected **A (full-field march)**: marching the complete `analytic_h` (continent+uplift+hills+ridges FBM)
~24–48 steps/pixel on a mesh-bound frame is too costly. Rejected **C (precomputed horizon map)**: new
streaming infra, overkill for a first cut.

**B** marches only the **macro relief** (continent + uplift — the large-scale shape that actually casts long
shadows) with an increasing stride, a sun-elevation gate, and a distance cap. Long horizon shadows come from
*big* relief, so the macro field suffices at a fraction of the full-field cost.

## Architecture & units

### Unit 1 — `field_macro_height()` (shared cheap macro eval)
- **File:** `shaders/field_math.gdshaderinc` (the one source of truth, already `#include`d by `ground.gdshader`
  and spliced into the compute shaders).
- **What:** `float field_macro_height(vec2 world_xz, uint seed, FieldP fp)` — returns the LARGE-SCALE base
  only: `continent()` + `uplift().amount * uplift_weight`, scaled by `macro_amp` (the `base` term already
  computed inside `field_height`, minus hills/ridges). No per-octave hill/ridge FBM → ~3–5× cheaper than a
  full `field_height`.
- **Depends on:** the existing `continent()`/`uplift()` (already in field_math). Pure function.
- **Why shared:** so the horizon march and any future consumer use ONE macro definition (no drift).

### Unit 2 — `horizon_shadow()` (the march)
- **File:** `shaders/ground.gdshader` (fragment-side helper).
- **Signature:** `float horizon_shadow(vec2 surf_xz, float surf_h, vec3 sun_dir_to)` → visibility `[0,1]`
  (1 = full sun, 0 = fully occluded).
- **What:** from the surface point, step toward `sun_dir_to` in world XZ by an **increasing stride**
  (`stride *= growth` each step) up to `hz_maxdist`, for `hz_steps` steps. At each step the sun ray's height
  is `surf_h + horizontalDist * (sun_dir_to.y / length(sun_dir_to.xz))`. Sample `field_macro_height` at the
  stepped XZ; the occluder *angle* is `(sampleH − rayBaseH) / horizontalDist`. Track the **running max**
  occluder angle; **soft penumbra** = `smoothstep` of (max occluder angle − sun elevation angle) scaled by
  `hz_softness`. Returns `1 − that` clamped.
- **Gating:** returns 1.0 immediately when `sun_dir_to.y` is above the `hz_sun_gate` elevation (high sun → no
  long shadows → skip the whole march → free at midday) or when `hz_on == false`.
- **Depends on:** Unit 1; the `sun_dir_to` uniform (already pushed for the relight fill); the `hz_*` uniforms.

### Unit 3 — Integration (no BRDF rewrite)
- **File:** `ground.gdshader` fragment tail.
- **What:** `if (hz_on) { float vis = horizon_shadow(v_surf_xz, v_h, sun_dir_to); AO = vis; AO_LIGHT_AFFECT = 1.0; }`.
  Godot's `AO_LIGHT_AFFECT=1` makes `AO` multiply **direct light** → the sun is occluded with no custom
  `light()` (which would mean re-implementing the BRDF). The `EMISSION` sky/bounce fill is unaffected by AO →
  a shadowed point still gets its sky fill (correct: a point in terrain shadow still sees the sky). The shadow
  map continues to handle near contact (the two compose: a point can be both map-shadowed and horizon-shadowed).
- **Side effect (accepted):** AO also dims Godot's env ambient — but that's the low-neutral 0.12 base now, so
  negligible; and it dims other lights (moon) the same way, which is *correct* (the moon is horizon-occluded too).

### Unit 4 — Knobs & push (C#)
- **Uniforms (pushed/tunable):** `hz_on` (bool), `hz_steps` (int ~16–24), `hz_maxdist` (m, ~6000), `hz_stride0`
  (first step m), `hz_growth` (stride multiplier ~1.3), `hz_softness`, `hz_strength` (0..1 final occlusion gain),
  `hz_sun_gate` (sin-elevation above which to skip). `sun_dir_to` reuses the relight push.
- **Files:** lab controls in `data/lab_controls.json` (Light tab: toggle + sliders, routed via the generic
  `slider`/`toggle` types → `_terrain.SetFloat/SetBool`, no new C# case needed — same as the relight sliders);
  a live A/B key in `TerrainLabUI.Process.cs` for `hz_on`.

## Data flow

```
LightingComposer.PushTerrainFill()  →  sun_dir_to  (already pushed)
lab_controls.json (Light tab)       →  hz_on / hz_steps / hz_maxdist / hz_stride0 / hz_growth /
                                        hz_softness / hz_strength / hz_sun_gate   (→ _terrain.SetFloat/Bool)
ground.gdshader.fragment():
  if (hz_on && sun_dir_to.y < hz_sun_gate)
      AO = horizon_shadow(v_surf_xz, v_h, sun_dir_to);  AO_LIGHT_AFFECT = 1.0;
```

## Tuning, perf, validation

- **Tunable:** the 8 `hz_*` knobs on the Light tab + a live toggle key. Default ON but conservative
  (`hz_steps≈16`, `hz_maxdist≈6000`, `hz_sun_gate≈sin(28°)`), or default OFF if perf forces it.
- **Perf (HARD gate):** `--profmove` before/after, low sun (worst case = march active). The march must not
  meaningfully move the mesh-bound frame; if it does, `hz_steps`/`hz_sun_gate`/`hz_maxdist` are the dials, and
  default-OFF is the fallback. Measured, not eyeballed.
- **Look (eye-gate):** drift-free at a low sun (the regime where it matters) — the user confirms ridges now
  cast into distant valleys, soft not hard, no banding/grid from the stride, no swimming in motion.
- **Numeric sanity:** shadowed-pixel count at low sun should rise materially with `hz_on` vs off, and stay ~0
  at high sun (the gate works).

## Risks & mitigations

- **Cost** — the headline risk. Mitigated by the macro proxy (Unit 1, ~3–5× cheaper), increasing stride,
  step cap, distance cap, and the sun-elevation gate (free at midday). Perf is a hard gate, default-OFF fallback.
- **Banding/aliasing from a coarse march** — the increasing stride + soft penumbra (running-max angle) smooth
  it; a small per-pixel dither on the first step (like the god-ray march) is the fallback.
- **Macro proxy misses fine relief** — by design (fine relief = near contact = the shadow map's job). The two
  systems compose.
- **AO-injection mis-reads** — if `AO_LIGHT_AFFECT` interacts badly with SDFGI/SSAO, the fallback is a custom
  `light()` (more control, more risk). Verify SSAO (tamed 0.6) + this AO coexist.

## File touch-list (finalized in the plan)
- `shaders/field_math.gdshaderinc` — `field_macro_height()`.
- `shaders/ground.gdshader` — `horizon_shadow()` + the `hz_*` uniforms + AO injection at the fragment tail.
- `data/lab_controls.json` — Light-tab toggle + 7 sliders.
- `scripts/lab/TerrainLabUI.Process.cs` — live A/B key for `hz_on`.
- (`sun_dir_to` already pushed by `LightingComposer.PushTerrainFill`.)
