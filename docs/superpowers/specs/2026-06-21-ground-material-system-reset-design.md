# GROUND — Material-Rendering Reset (per-pixel procedural placement + texture arrays)

Date: 2026-06-21. **This is the authoritative ground-rendering design and it changes the verdict.**
It supersedes the *iterate-don't-rebuild* stance of `2026-06-20-ground-rendering-system-master-design.md`
(whose **north star and keepers it keeps**) — because a live eye-gate proved the base problem is
**structural**, not surface-tuning. We reset the material RENDERING to a clean, holistically-designed
system, following the pillars, to bring the ground to **parity with the sky**.

## Why this exists (the live finding that changed the call)

On 2026-06-21 a "wrong-defaults" probe (G-0) put the suppressed material levers in front of the user's
live eye, isolating each. At the **real-height** step the ground broke into hard **rectangular grid
facets** at every material transition. Root cause confirmed in code: the active blend path
(`terrain_lab.gdshader` `interlock_blend`) **hard-thresholds a low-resolution baked weight field
(4.0 m/texel = 8192 m region ÷ 2048 res)**; the smooth inverted-roughness proxy hid it, but real height
is high-contrast so the threshold goes near-binary and **snaps the boundary onto the 4 m grid**. This is
exactly the structural ceiling the 2026-06-21 audit + ground review named (`docs/AUDIT-2026-06-21.md`):
*"no placement structure finer than one 4 m cell — fragment-side tricks HIDE the grid, they don't add
detail."* No amount of default-tuning fixes it; the **placement/blend architecture** is the problem.

User's call: stop iterating the accreted, never-holistically-designed material stack. **Start over,
replan, follow pillars** — give the ground the same focused, staged treatment the sky got. The sky reads
AAA because it's physically grounded and was built as a whole; the ground was bolted on one "Unit" at a
time and never was. We fix that now.

## North star (kept from the master design)

**A game-agnostic, data-driven ground rendering SYSTEM. The tech is the product, not one biome.**
- **Quality bar ≈ Skyrim / No Man's Sky** — believable, varied, readable game terrain; not
  photoreal-megatexture-locked.
- **Material-fluid:** materials and biomes are DATA. Many textures, swappable per biome, are a hard
  requirement — so binding 7 fixed samplers + baking a 4 m splat is out *by definition*.

## Pillars & the guardrail (how a "reset" stays disciplined)

- **PILLARS:** quality = performance = AAA-ish = best-long-term, regardless of time cost. Lead with the
  most-correct option. Perf budget = **8 ms in-motion** for the whole generator; material work is
  fragment-side + designed cheap.
- **The user's live eye is the only look-gate.** Build behind a toggle defaulting to the **current**
  look; A/B; judge in motion at close/mid/far. Never judge a motion artifact from a still.
- **⚠ GUARDRAIL — reset the SKIN, not the BONES (this is how WG16 was born + why WG1–15 died).** WG1–15
  died from *repeated big-bang teardowns*. WG16 survived because its teardown was **disciplined**: strip
  WG15 to the one proven thing (the base field) and rebuild slowly. We do the same at the material layer:
  1. The base-field **geometry is untouched** (it's the bones; this resets the skin).
  2. **Proven pieces are re-hosted, not re-derived** (histogram anti-tiling; the BRDF).
  3. **No big-bang delete.** The new material shader is built **alongside** the old path; a toggle A/Bs
     them; the old path stays until the new core is judged **at parity-or-better**. Only then flip the
     default and retire the dead paths. Designing the whole target now is free; *building* stays gated.

## Scope

- **In:** the material / surface **rendering** system — material data model, placement, sampling,
  blending, surface depth, lighting response.
- **Keepers (proven — re-host or leave untouched, do NOT rebuild):**
  1. **Base-field geometry** — the 5-layer GPU heightfield (continent → uplift → hills → ridges → macro
     base) in `field_height.glsl`. Pure shape; consumed by the new skin unchanged. Don't touch its math.
  2. **Histogram anti-tiling** — the Deliot–Heitz tiling-and-blending + inverse-histogram LUT (the one
     user-PASSED ground feature). Re-hosted as the per-material sampler.
  3. **Custom `light()` BRDF** — the Burley+GGX + cloud-shadow hook; correct and isolated → ports as-is.
  4. **The 108 curated materials** — a *starting* set (fluid; will grow/swap per biome), not sacred.
- **Out (separate arcs — NOT this reset):**
  - **Erosion / hydrology** — reshapes the *bones* (carves valleys/gullies). Its own arc; this reset
    assumes the shape as-is and must consume a future carved heightfield without a rewrite.
  - **Biomes / climate field / many-material streaming** — the downstream sub-project. This core is built
    **biome-ready** (data-driven material sets + rules) so biomes are *wiring*, not a re-architecture.
  - **Detail scatter / decals / wetness** — the downstream "lived-in" layer.
  - **Scale (CDLOD / chunks / streaming)** — the geometry/perf arc; unchanged.

## The core technical decision — per-pixel procedural placement + texture arrays

Materials live in **texture arrays** (many bound at once → scales to a ton + swappable per biome).
Placement is computed **PER FRAGMENT** from world-space terrain fields — **there is no baked splat at
all.** This is the decision everything follows from, and it's the pillars answer:

- **Kills the 4 m blockiness by construction** — placement is evaluated at pixel resolution, so material
  boundaries are as fine as the screen. There is no low-res field to snap to.
- **Scales to many materials** — a `sampler2DArray` holds N materials; placement indexes it. No 7-sampler
  ceiling. A biome swaps the array contents + rules.
- **Data-driven / game-agnostic** — placement rules + material set are data; any biome plugs in.
- **Within-area variation is native** — the domain-warped noise that drives placement *is* the variation,
  so "drier patches / no uniformity" falls out of the same mechanism (no separate GM3 unit).

**Rejected alternatives:** a **higher-res baked splat** just throws memory at the blockiness, keeps a
resolution ceiling, and splat channels don't scale to fluid-many-materials (the patch, not the pillars).
**Virtual texturing / RVT** is a Phase-B *caching* layer that needs chunk/streaming infra and still sits
on top of a placement system — overkill as the foundation; we can cache *into* it later if perf demands.

## The system — 6 units (one job each · clean interface · independently judgable)

> Built in stage order with the sky's discipline. The **CORE = units 1–4**; it replaces the broken thing
> and is gated first, at parity-or-better with the current look (no blockiness). Then 5, then 6.

**Unit 1 — Material data model + library build.**
- Each material = `{ albedo, normal, ORM (occlusion/roughness/metallic), HEIGHT }`, packed into
  **`sampler2DArray`s** (one array per channel) at a common tiling resolution. A C# build step resizes the
  library materials into the arrays at load (mirrors the existing local-RD bake pattern).
- **Real height is first-class** (closes the "library has no height" gap). Source: re-host the proven
  Poisson normal→height bake (`HeightCompute` — produces genuine mesoscale relief) to fill the height
  array. Authored/sourced height maps are a later per-material upgrade (same slot).
- Data manifest (`data/ground_materials.json`): material list → array index + per-material params (tiling
  scale, height amplitude, roughness range, triplanar bias). **Biome = { material list + placement rules }.**
- **Interface:** the arrays + an index/param UBO. **Depends on:** the material library on disk.
- *Capacity:* start at **32 slots** (7 current roles + headroom), array-resizable; not a 7-zone fixed model.

**Unit 2 — Placement (the blockiness killer).**
- Per fragment, from interpolated world-space fields (height, slope from normal, curvature, aspect) +
  **domain-warped multi-octave world-position noise**, evaluate each candidate material's **rule** → a
  weight. Normalize → take the **top-4** by weight for blending.
- Rules are DATA (per biome): height band, slope band, curvature, aspect, noise-patch — composed into a
  weight. (The G1 zone concept, re-expressed as per-pixel rules instead of a baked field.)
- **Interface:** `(world fields) → top-4 {material index, weight}`. **Depends on:** Unit 1 (indices), the
  vertex-stage terrain fields (already computed: `v_h`, `v_slope`, `v_curv`).
- *No baked splat, no rebake step* — placement is live per-pixel.

**Unit 3 — Sampling + anti-tiling.**
- For each of the top-4: sample its array slot through the **re-hosted histogram-preserving anti-tiling**
  (translation-only bombing + inverse-histogram LUT) + **triplanar** on steep faces + verified
  **mip/anisotropic** filtering (the documented "fuzz = no mipmaps" gotcha — assert it).
- **Interface:** `(material index, world uv, derivatives) → {albedo, normal, orm, height}`. **Depends on:**
  Units 1–2.

**Unit 4 — Blend.**
- Height-aware blend of the top-4: each material's contribution = its placement weight biased by its
  **real height** (higher material wins at the boundary — rock pokes through sand), with **`fwidth` AA**
  on the transition so the boundary is per-pixel-soft, never aliased. Output one `{albedo, normal, orm}`.
- **Interface:** `(top-4 samples + weights) → final surface`. **Depends on:** Unit 3. *This is where the
  old `interlock_blend`'s job moves — onto a per-pixel boundary, so there is no grid to snap to.*

**Unit 5 — Surface depth (its own spec at its turn).**
- Real-height-driven relief so surfaces read **3D, not flat**: POM-with-depth (writes depth → silhouette,
  unlike the current UV-only POM) **or** tessellation/displacement — decided at Unit 5's spec — plus
  micro-relief and close-up detail. Consumes Unit 1's real height.

**Unit 6 — Lighting response (tuned last, under the finished sky).**
- Re-host the custom `light()` BRDF + AO + contact; tune how ground reads **under the atmosphere/sky
  lane** so the two agree (the review's note: don't chase material contrast that's really a lighting
  issue). Coordinate; lock last.

## Build sequence (each its own spec → plan → build → eye-gate)

1. **CORE (Units 1–4) — FIRST.** A new shader (`shaders/ground.gdshader`) + the C# array-build, built
   **alongside** `terrain_lab.gdshader` behind a toggle (extend the lab's existing blend-path switch).
   Default stays the **current** look. **Gate:** fly close/mid/far, A/B new-vs-old — the new core must read
   **at least as good with the blockiness GONE** (no grid facets, materials placed sensibly, variation
   native, anti-tiling intact). On PASS: flip default, then **retire the dead paths** (the 4 m splat bake,
   `interlock_blend`, the bolted-on GM1/2/3 units, dead uniforms — the cleanup the audit wanted).
2. **Surface depth (Unit 5)** — once the core reads good, add real relief; gate "reads 3D, no swimming."
3. **Lighting response (Unit 6)** — tune under the sky; lock.
4. *Downstream sub-projects (own specs):* detail scatter · biomes/climate · (separate arcs) erosion, scale.

## Perf posture

Fragment-side + array fetches. Levers: **top-4** cap (fewer fetches than the worst case), distance LOD
fading the blend to top-1 far out, branch-guarded fetches, the anti-tiling already proven ~1.4 ms. Profile
`--profmove` each stage against the 8 ms budget. The structural geometry cost (mesh floor / CDLOD) is the
separate scale arc, not this.

## Relationship to existing docs

- **Supersedes** the *iterate* verdict in `2026-06-20-ground-rendering-system-master-design.md` and the
  GM-sequence in `2026-06-20-ground-roadmap-to-aaa-design.md`; **keeps** their north star + keepers + the
  Unit-4 lesson (procedural world-position placement is the right tool — now it IS the placement engine).
- `ROADMAP.md`'s Ground / Texture lane should point here; `DECISIONS.md` gets the reset entry.
- The G-0 probe (`review.tscn` key 3) + the `rough_floor` slider + the `fwidth` AA already added to
  `interlock_blend` stay as-is — the G-0 probe was the diagnostic that proved this reset is needed; the
  `fwidth` AA is correct for the old path too until it's retired.

## Self-review notes

- **Scope:** a master/decomposition spec. Units 1–4 are detailed enough to plan as the first build; Units
  5–6 are sketched (own specs at their turn). Erosion/biomes/scale explicitly OUT.
- **Placeholders:** none — every unit has a job, interface, dependency, and a gate; every keeper is named.
- **Consistency:** the per-pixel-procedural decision is honored in every unit (no baked splat anywhere);
  the guardrail (skin-not-bones, build-alongside, gate-at-parity) is applied in the build sequence.
- **Ambiguity resolved:** "start over" = reset the material RENDERING (skin), keep the base geometry
  (bones) + the two proven algorithms; real height = re-hosted Poisson bake to start; placement = per-pixel
  rules (not a baked field); blend = top-4 height-aware + fwidth. POM-vs-tessellation deferred to Unit 5.
