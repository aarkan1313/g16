# GROUND — Game-Agnostic Rendering System (master design)

Date: 2026-06-20. **This is the authoritative ground design.** It supersedes the GM-sequence framing
in `2026-06-20-ground-roadmap-to-aaa-design.md` (that doc's 7-layer *inventory* stays valid as
reference; this fixes the *north star, scope, keep/rebuild calls, and foundation-first order*).

> **⮕ CURRENCY UPDATE (2026-06-21 review — `../../AUDIT-2026-06-21.md`).** A code-grounded iterate-vs-rebuild
> review confirmed this design's verdict (**ITERATE / surgical execution, do NOT rebuild**) and updated its status:
> - **G-1 (compositing core) is ~done; the G-1 spec is STALE:** the float/half weight field AND histogram anti-tiling
>   (this design's two "rebuild" items) already SHIPPED + PASSED on 2026-06-20. Real remaining G-1: the `fwidth`
>   transition AA + a reversible `hq_blend` A/B toggle (the half-float flip shipped un-gated), single-sourcing the
>   **drifted duplicate `zone_weights`** (a band_soft_mult bake-vs-live correctness bug), and the tile_mode dropdown cleanup.
> - **Run a "G-0 wrong-defaults" gate FIRST** (NEEDS_REVIEW 1e): `rough_floor` is pinned 0.5 (no UI control) forcing the
>   ground dead-matte; real height + within-area variation default OFF; the active palette is the drab one. Flipping these
>   is the biggest immediate look lift and is pure iterate — judge it before building.
> - **Two genuine structural items** (so "execution not structure" is half-right): (a) the active placement/weight field
>   is **4.0 m/texel** — the Unit-4-fatal resolution, now on the LIVE blend path → component 4 needs a resolution/space
>   rebuild; (b) component 1 (real height as a first-class material channel) is still unaddressed at the DATA layer — the
>   library has **zero height maps** and `MaterialBoard` mis-binds AO→height, so the 108 accepted materials were never
>   judged for relief. **Sourcing/authoring height-bearing assets is the real G-2 long pole, not shader tuning.**

## Why this exists (the foundation reset)

The user stepped back and asked the right question: *"are we building more and more on a bad
foundation?"* Honest answer: the **base-field geometry** got a focused from-scratch pass and is
solid; everything above it — the material/surface **rendering** — was built **one bolted-on "Unit" at
a time** (Unit 1/2/4, compositing core, G1, GM1/2/3) and **never designed as a whole.** So we keep
discovering its quality/precision debt **one eye-gate at a time** (IQ tiling, 8-bit weights — each
"approved at distance, broken up close") — reactive whack-a-mole. Meanwhile clouds/light/sun/night
each got the focused treatment and *feel* finished. Ground never did. User's call: give it the
**zero-to-100 treatment now.**

Key user steer: *"the concept behind how textures are placed is nice … but it's held back by what it
actually looks like."* So the problem is **visual execution, not structure** — this redesign centers
on **look/realism**, not re-architecting placement logic.

## North star (locked)

**A game-agnostic, data-driven ground rendering SYSTEM.** The tech is the product, not one biome.
- **Quality bar ≈ Skyrim / No Man's Sky:** believable, varied, readable game terrain — **NOT**
  photoreal-megatexture-locked.
- **Game-agnostic / data-driven:** any material set / biome / art-leaning, plugged in via data,
  should read at the bar. This is a *systems* goal that plays to the codebase's data-driven bones.

## Pillars & the discipline guardrail

- **PILLARS:** quality = performance = AAA-ish = best-long-term, regardless of time cost. Lead with
  the most-correct option (user: "if it follows pillars lets do it").
- **The user's live eye is the only look-gate.** Build behind toggles defaulting to the current look;
  A/B; judge in motion at close/mid/far; never judge a motion artifact from a still.
- **Perf budget = 8 ms in-motion** for the whole generator. Profile `--profmove` each phase.
- **⚠ GUARDRAIL (this is why WG15 died — repeated teardowns):** design the whole target **holistically
  now** (free, encouraged), but **execute incrementally, each piece gated** — never a big-bang rip-out.
  *"From the ground up" in DESIGN; surgical in EXECUTION.* Keep the three proven keepers below.

## Scope

- **In:** the ground **material / surface rendering** system — sampling, blending, placement
  *execution*, variation, surface depth, detail layers, lighting response.
- **Keepers (proven — do not rebuild):**
  1. **Base-field geometry** (WG15 5-layer field; "good for what it is — a single chunk"). Don't touch its math.
  2. **Histogram anti-tiling** (`tile_mode=3`, shipped + gated 2026-06-20).
  3. **The zone-placement *concept*** (material by height/slope/zone — stone on cliffs, grass in valleys). Concept kept; *execution* rebuilt.
- **Out (separate arcs, NOT this redesign):** geometry **erosion / hydrology** (geometry is "fine for
  now"); **scale / CDLOD / chunks / streaming** (Phase B). Don't boil the ocean.

## The system — 8 components (keep / rebuild / build · what each must deliver)

1. **Material data model** — *reshape.* Per-material channel set incl. **real height as first-class**
   (not roughness-derived); clean schema for material libraries + biome palettes + placement rules, so
   any biome is data. Game-agnostic foundation.
2. **Texture sampling** — *keep.* Histogram anti-tiling + verify correct **mip / anisotropic
   filtering** (the documented "fuzz = no mipmaps" gotcha) + triplanar.
3. **Compositing / blending** — *rebuild.* **Float (half) weight field** + **fragment-resolution
   organic transitions** (the boundary is generated from full-res material height + noise; the smooth
   low-res weight only *biases* it). Kills the blocky/stair-stepped/smeary blend. **The core everything
   renders through → built first.**
4. **Placement execution** — *rebuild.* The zone concept re-executed: signal-driven rules
   (height/slope/curvature/aspect/flow) producing **natural-looking** distribution + transitions,
   data-driven per biome. (G1 is "basic, proves the concept".)
5. **Within-area variation** — *rebuild.* Multi-scale (macro value/hue + meso patches + procedural
   material variation) so **nothing reads uniform or tiled**. (Old macro was a net-negative, now off.)
6. **Surface depth / micro-detail** — *rebuild.* Real-height-driven **POM / displacement** + micro-relief
   + close-up detail so surfaces read **3D, not flat**. (GM2 was built fast; owes a proper design.)
7. **Detail scatter / decals / wetness** — *build.* GPU-instanced rock/pebble/debris scatter + decals
   (puddles/mud/snow patches) + wetness, placement-driven, game-agnostic — the "lived-in" layer.
8. **Lighting response** — *tune.* Custom BRDF + AO + contact, and how ground reads under the
   (separate) Sun/Light lane. Coordinate; don't chase material contrast that's really a lighting issue.

## Build sequence — foundation-first, each its own spec → plan → build → eye-gate

The user's worry — *harden the core before layering on it* — sets the order:

- **Phase G-1 · Compositing core** (component 2 verify + **3 rebuild**). Everything renders through it;
  the float-weight + fragment-resolution blend fix lives here. **← FIRST, next.**
- **Phase G-2 · Material data + surface depth** (1 + 6). Real height first-class → surfaces read 3D.
- **Phase G-3 · Placement execution + variation** (4 + 5). Natural distribution, no uniformity.
- **Phase G-4 · Detail scatter / decals** (7). Close-up richness.
- **Phase G-5 · Lighting response** (8). Tuned throughout, locked last.

Each phase: behind a toggle defaulting to the current look, A/B'd, gated by the user's live eye before
the next. The old GM2/GM3-A built-but-ungated work is **reabsorbed** into G-2/G-3 (re-judged or
rebuilt there), not separately gated.

## Relationship to existing docs
- Supersedes the **GM1→GM6 sequence** in `2026-06-20-ground-roadmap-to-aaa-design.md` (kept for its
  layer inventory + the Unit-4 lesson). `ROADMAP.md`'s Phase-A ground line should point here.
- Erosion (`2026-06-17-erosion-arc-design.md`) and CDLOD (`2026-06-18-terrain-lod-roadmap-design.md`)
  remain the separate geometry/scale arcs, unchanged.

## Self-review notes
- **Scope:** a *master/decomposition* spec — each phase gets its own spec → plan. G-1 is brainstormed next.
- **Placeholders:** none — every component has a status + a concrete deliverable; every phase a gate.
- **Consistency:** keepers (base field, anti-tiling, placement concept) stated once and honored in the
  component table + sequence. North star (game-agnostic / Skyrim-NMS / not-photoreal) used throughout.
- **Ambiguity:** "AAA ground" pinned to the Skyrim/NMS bar + game-agnostic data-driven, not left subjective.
  Scope boundary (rendering, NOT erosion/scale) explicit so the redesign stays bounded.
