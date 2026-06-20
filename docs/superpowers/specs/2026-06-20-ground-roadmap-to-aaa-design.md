# GROUND — Roadmap to AAA (consolidated, single source of truth)

Date: 2026-06-20. **This is the authoritative ground roadmap.** It supersedes the scattered
ground items in `ROADMAP.md` (which stays as the cross-arc index but defers to this for the
ground sequence) and the per-unit framing in the 2026-06-17 / 2026-06-19 ground specs (those
remain valid as *layer* designs; this fixes their ORDER and completeness).

## ⏸ GROUND LANE — PAUSED (2026-06-20) for a project-wide re-roadmap

**Built this lane, all committed, none user-approved yet:** **GM1** (data-driven palette), **GM2** (real
per-material height from normals, Poisson bake), **GM3-A** (within-area variation). That's three phases past
the "approve before the next phase" rule — so the lane STOPS here. **Defaults reproduce the approved-era look:**
palette `active=alpine_green` (prior set), GM3-A `variation_on=false`, GM2 `height_from_maps=false` — every new
feature is opt-in. **GM1+GM2+GM3-A await ONE combined live eye-gate** (NEEDS_REVIEW §1c), after which we
re-roadmap. **Do NOT start GM4, GM3 Approach B/C, or any new unit** until then. Unit 4 stays parked.

## Why this doc exists

The ground arc had been built one shader "Unit" at a time, and each piece kept under-delivering
(latest: Unit 4 breakup — see the Lesson below). Root cause wasn't any single unit; it was
**build order**. The two highest-impact layers of AAA ground — the material *assets* (a curated,
distinct palette with real height data) — were deferred to the end as "polish," so every
variation/placement/breakup layer was decorating monochrome, flat, height-less materials and had
nothing to work with. This doc lays out the **complete** ground stack and sequences it by
impact × dependency, so the foundation comes first and every later layer is judgeable.

**It is a re-sequence, not a reset.** Nothing proven is thrown away. The base field, the blend
engine, anti-repetition, G1 placement, the BRDF+AO, and cloud shadows all stay. WG16's core
discipline holds: keep what works, build outward slowly, one eye-gated piece at a time (the WG15
graveyard was caused by repeated teardowns — do not repeat it).

## Pillars & the gate

- **PILLARS:** quality = performance = AAA-ish = best-long-term, regardless of time cost. Lead
  with the most-correct option, not the cheap shortcut.
- **The user's live eye is the only gate for look.** Mechanical checks gate correctness/cost;
  the user flying it at close/mid/far decides. Build behind toggles defaulting to the current
  look. Never judge a motion artifact from a still.
- **Perf budget = 8 ms in-motion for the whole world generator** (flora/water/erosion must still
  fit). Material work is fragment-side + baked-once and must stay cheap; the geometry/scale cost
  is the CDLOD arc's job. Always profile `--profmove`.

## Weighting — this is a SYSTEM, not a texture problem (user steer, 2026-06-20)

Good textures are a **prerequisite**, but they are NOT where most of the quality (or the work)
lives. AAA ground is a *system*, and the heavy engineering is in the **rendering tech**:
mask generation (at the *right resolution* — Unit 4 failed precisely because its masks were
4 m-coarse), shader compositing, material **blending** quality, **tile-to-tile** transitions
(both material-to-material across the surface and, later, chunk-seam continuity), within-area
procedural variation, and how all of it survives lighting. **These weigh as much as or more than
the texture choices.** Concretely in the sequence below: GM1 (palette) is the small prerequisite;
**GM2–GM4 are predominantly shader/mask/blending engineering** and are where the bulk of the AAA
work is. Don't read the "material foundation first" ordering as "textures are the main thing" —
it's "you can't judge the shader/blending work until the inputs are distinct."

Two corollaries: (1) **masks must be generated at adequate resolution / the right space** (the
Unit 4 lesson — a baked 4 m region-stretched mask can't do close-range work; prefer fragment-side
world-position fields or higher-res/finer-derived signals); (2) **lighting is co-equal** — the
current "drab" is partly warm/aerial light washing the palette flat, and the **Sun & Light arc is
progressing in a parallel chat** (sun-disc polish built; time-of-day next). Perceived material
quality depends on it, so coordinate — don't chase material contrast that's really a lighting issue.

## North star — what "AAA ground for this region" means when done

You fly from peak to valley and the ground reads **real**: distinct materials with genuine
surface depth (crevices, relief), no visible tiling, variety even across a single uniform slope,
the *right* material in the right place (scoured rock on ridges, sediment in drainage, snow on
shade faces), geomorphology that looks carved by water, and a lived-in detail layer — scattered
rock, pebbles, debris, wetness — all holding 8 ms in motion. Scope (user): make **THIS region**
great first → chunks → infinite → biomes last.

---

## The complete ground stack (7 layers)

Legend: ✅ done/approved · ◐ partial · 🅿️ built-but-parked · ⬜ not built · 📄 spec'd

**Layer 1 — Surface form (geometry: the shapes)**
- ✅ Base heightfield (WG15 5-layer GPU field, proven — don't touch)
- ⬜📄 **Erosion** (hydraulic + thermal → valleys/gullies/talus/drainage; the biggest "real vs
  noise" lever) — spec `specs/2026-06-17-erosion-arc-design.md` (E1–E4)
- ⬜ Micro-relief displacement (close-up surface bumps; needs Layer-2 height maps + tessellation/POM)

**Layer 2 — Material assets (the textures themselves)**
- ⬜ **Curated distinct palette** (GM1)
- ⬜📄 **Real per-material height maps** (GM2; library is albedo/normal/rough/AO only — no height)
- ◐ Detail/high-res textures for close-up (Unit 2 built, shelved)
- ◐ Asset-quality audit (are the 108 accepted enough / AAA-grade for the chosen biome?)

**Layer 3 — Placement (what goes where)**
- ✅ Rule-based by altitude/slope/signed-curvature (G1, approved)
- ⬜ Aspect/moisture/flow rules (G3 — snow on shade, green in drainage, sediment in channels)
- ⬜ Erosion-driven placement (exposed rock on scoured ridges, deposition fans; consumes Layer-1 erosion)
- ⬜ Climate/biome field (moisture/temperature drives material+color+wetness; backlog)

**Layer 4 — Blending / compositing (how they combine)**
- ✅ Weight-blend top-2 + derived height interlock (compositing core, approved; `splat_blend_mode` 1)
- ✅ Anti-repetition / stochastic tiling (Unit 1, approved)
- ⬜ Real heightblend interlock (sharpens once GM2 height maps exist)
- ⬜ RVT caching for many-layer compositing at world scale (needs chunks)

**Layer 5 — Variation (kill uniformity / "variety within an area")**
- ⬜ **Within-area procedural material patches** (GM3 — the actual fix for the user's complaint)
- ◐ Macro color/value variation tuned (Unit 5; `macro_color` exists, needs tuning to survive GI+AgX)
- ◐ Distance detail near⊕far (Unit 2, shelved)
- 🅿️ Context breakup masks (Unit 4 — built+parked; revive as *secondary* bias after erosion)

**Layer 6 — Detail layers (the "stuff")**
- ⬜ Rock/pebble/debris scatter (GPU-instanced; Godot has no tessellation, so instanced meshes)
- ⬜ Decals (puddles, mud, snow patches, trails)
- ⬜ Wetness / dynamic weather response (Unit 6 hooks)
- ⬜ Flora integration (parallel chat returns grass/trees/shrubs — wire providers; scatter consumes biome/breakup fields)

**Layer 7 — Scale & performance architecture (world scale)**
- ✅ GI/shadow proxy (perf; default on)
- ⬜📄 **CDLOD terrain LOD** (perf floor + close tessellation/displacement) — spec
  `specs/2026-06-18-terrain-lod-roadmap-design.md` (T1–T3); gate = pop-free in motion (clipmap killed WG1–15)
- ⬜ Chunks (prerequisite for streaming, RVT, erosion E3/E4, world-editing)
- ⬜ RVT caching · ⬜ Streaming → infinite

---

## Recommended build sequence (impact × dependency; each eye-gated)

> Each phase is its own spec → plan → build → eye-gate. Build behind a toggle defaulting to the
> current look. Don't start the next phase until the current one is user-approved.

### GM1 — Curated palette  *(NEXT; small but foundational)*
- **Goal:** the 7 roles load *distinct, contrast-rich* materials on startup instead of the
  drab default mapping → instant "drab → rich" (most viewports span several roles).
- **Mechanism (the picker already exists):** the `zone_mat` material control already expands to
  7 per-role dropdowns backed by `material_library.json`; `SetZoneMaterial` already loads
  albedo/normal/rough/ao. GM1 adds `data/ground_palette.json` (named palette → 7 role material
  names), loads it where `ZoneDefaultMaterialIndex` currently hardcodes the drab defaults, and
  the existing dropdowns refine it live. Data-driven, reversible, near-zero perf.
- **Starting palette (alpine/arctic; tune live):** valley=`13_sun_baked_clay` · valley→slope=
  `01_fine_sand` · slope=`13_dry_loose_scree` · slope→cliff=`02_coarse_talus` · cliff=
  `01_columnar_basalt_face` · high=`04_arctic_rock_with_orange_lichen` · peak=`01_fresh_powder`.
- **Gate:** reads photoreal/varied across the gradient, not drab. (Old G2.)

### GM2 — Real per-material height maps  *(flat → deep surface)*  — ✅ BUILT (Approach B), eye-gate owed
- **📄 Detailed design: `specs/2026-06-20-ground-gm2-real-height-maps-design.md`** (library has NO height →
  derive from normal via a GPU Poisson solve, baked once; one seam `material_height_uv`; ⚠ eye-gated — alters the approved interlock).
- **BUILT 2026-06-20** (`39058ab`,`b907b77`): `shaders/height_from_normal.glsl` + `HeightCompute.cs` derive
  real height from normals (Jacobi-Poisson, inspected = genuine relief); threaded through the height seam +
  `height_from_maps` toggle (default OFF). Revives POM. NEEDS_REVIEW §1d. (GM2-C displacement/array = later.)
- **Goal:** genuine surface depth — sharpens the heightblend interlock and revives POM relief
  (both currently derive height from inverted roughness, which is too flat).
- **Approach:** generate per-material height from normal+albedo (or source better maps); bind
  `z*_hgt`; the POM seam (`material_height_uv`, `pom_offset`) is already in place to consume it.
- **Gate:** crevices/relief read as real 3D under motion+light, no swimming. (Was the deferred
  "height maps" arc — elevated to scheduled.)

### GM3 — Within-area variation  *(uniform → varied; the user's core complaint)*
- **📄 Detailed design: `specs/2026-06-20-ground-gm3-within-area-variation-design.md`** (Approach A =
  cheap multi-scale material-property modulation core; B/C = true material patches via texture arrays, deferred).
- **Goal:** variety even across a single uniform slope.
- **Pieces:** (a) **procedural mesoscale material patches** — blend 2–3 ground materials by a
  domain-warped, multi-octave **world-position** noise field (infinite detail, no baked-grid
  squares, varies *within* the area) — this is what Unit 4 *should* have been; (b) **macro
  color/value** tuned (Unit 5) so value/hue drift survives GI+AgX; (c) revive **distance detail**
  (Unit 2) near⊕far.
- **Gate:** a single slope reads varied + non-tiling at close/mid/far, gradual, no seams.

### GM4 — Placement realism + Erosion  *(right material right place, geomorphologically real)*
- **Goal:** the *right* material follows terrain process, on terrain that looks carved.
- **Pieces:** (a) **G3** aspect/moisture/flow rules; (b) **Erosion E1** (coupled hydraulic+thermal
  sim core, its own gated arc — spec `specs/2026-06-17-erosion-arc-design.md`); (c) **revive Unit
  4 breakup masks** as a *secondary* context bias — once erosion provides real steep faces/gullies
  the slope/cavity/flow masks finally have features to read.
- **Gate:** terrain reads like it follows water + exposure; erosion judged GREAT on this region
  before any bake/stream infra (E2–E4 are later, need chunks).

### GM5 — Detail layers  *(lived-in)*
- Rock/pebble/debris GPU-instanced scatter (consumes placement/breakup/biome fields); decals
  (puddles/mud/snow patches); wetness/dynamic response (Unit 6); flora integration (parallel chat).
- **Gate:** ground reads detailed/inhabited up close without tanking the budget.

### GM6 — Scale & perf architecture  *(world scale + the 8 ms target)*
- **CDLOD** (T1 pop-free on this region first) → chunks → RVT caching → streaming. Spec
  `specs/2026-06-18-terrain-lod-roadmap-design.md`. This is also the dominant remaining perf lever
  and is shared with the world-editing/flora/erosion-E4 arcs.
- **Gate:** pop-free in motion; 8 ms in-motion budget met with the material stack on.

---

## Cross-arc dependencies
- **Erosion** (Layer 1 / GM4) reshapes geometry → its output feeds placement (Layer 3) and gives
  the parked breakup masks (Layer 5) real features. Big independent arc; intersects ground at GM4.
- **CDLOD / chunks** (Layer 7 / GM6) is needed by streaming, RVT, erosion E3/E4, and world-editing.
- **Parallel chats:** flora (grass/trees) and world-editing return libraries to integrate; both
  consume ground fields and invalidate splat/breakup/scatter on edit → re-bake after edits.

## Lesson banked — why Unit 4 (procedural breakup) failed (don't relearn)
Context masks (slope/curv/cavity/aspect/flow) are all `f(heightfield)`. **Within a small uniform
area the heightfield is smooth → every mask is ~constant → they add between-area variety, not
within-area variety.** They were also baked at 4 m/texel (8192 m region / 2048 res) and stretched
across the region, so thresholding them revealed the texel grid as faint squares; and the
near-monochrome palette gave the material swaps no contrast. Three compounding causes, one wrong
premise: **context-driven breakup is the wrong tool for "variety within an area."** The right tool
is **procedural world-position noise** (GM3) on top of a **distinct palette** (GM1) with **real
depth** (GM2). Breakup's infra is kept (parked) for GM4, where it becomes a secondary bias on
erosion-carved features. → memory + DECISIONS 2026-06-20.

## Perf posture
Material layers (GM1–GM5) are fragment-side + baked-once and designed to stay within budget;
profile `--profmove` after each, with `bk_far_fade`-style distance fades and branch-guarded fetches
as the levers. The structural cost is geometry (GM6/CDLOD), tracked in the perf arc.

## Self-review notes
- **Scope:** this is a *roadmap* spec (decomposition + sequence), not a single implementation spec.
  Each GM phase decomposes into its own spec → plan. GM1 is small enough to go straight to a plan.
- **Placeholders:** none — every phase has goal/mechanism/gate; unbuilt arcs point to their specs.
- **Consistency:** sequence (GM1→GM6) matches the 7-layer stack; nothing proven is rebuilt.
