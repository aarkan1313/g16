# WG16 — Roadmap

The single source of truth for what's done, what's being judged, and what's next. Thin by
design. Pairs with `NEEDS_REVIEW.md` (the eye-gate queue), `DECISIONS.md` (the why, per
decision), and `HANDOFF.md` (orientation). Superseded designs are frozen in `docs/archive/`.

Last updated: 2026-06-20 (full-scope re-roadmap: finish both lanes → make it a world → climate/elements).

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost. Lead
with the better option, not the cheap shortcut.

---

## 🧭 The discipline rule (read first — this is why we reset)

In five days WG16 grew 20 specs + 23 plans — the "process outran the results" sprawl it was
founded to avoid — and **both lanes built three phases past their last eye-gate** because the user
couldn't always be at a screen. The cure, encoded:

> **A lane builds at most ONE phase ahead of the last PASSED eye-gate.** When the gate queue holds
> work the user can't yet see, **STOP and bank — don't open new depth.** Build behind toggles
> defaulting to the approved look. **Designing ahead (spec/plan) is fine and encouraged; *building*
> ahead is the trap.** Thin docs: a roadmap line + a `DECISIONS.md` entry, not a plan-per-lane.

The eye is the only gate for look. Mechanical checks gate correctness/cost. Never judge a motion
artifact from a still. Always profile `--profmove`.

---

## The shape (user direction 2026-06-20)

Three phases, in order. **Finish the two lanes fully — especially ground texture — before moving
to the world.** "It doesn't matter if one mountain looks good — it's not a world" — so once the
region look is *done*, the priority pivots hard to scale/biomes/procedural, then the elements.

- **Phase A — Finish the two lanes (the region look, FULLY).** Sun & Light → full sky system;
  Ground/Texture → full material stack **incl. erosion + water hydrology + surface height**.
- **Phase B — Make it a WORLD.** Scale infra (CDLOD → chunks → streaming → infinite) + biomes +
  macro procedural variety + erosion at scale + flora/world-editing integration. **Hybrid build:**
  the chunk/streaming spine is the keystone (it's also the perf fix and the thing that makes it a
  world), but each content system is designed region-first + stream-aware so scaling is wiring.
- **Phase C — Climate & elements.** Visible water rendering, precipitation (rain/snowfall) +
  wetness, snow-on-ground — consuming Phase-A's hydrology model + the Weather axis.

> **⚠ Water + erosion are coupled and span phases.** WG15's erosion failed because erosion wasn't
> informed by water (channels didn't follow real drainage; it "didn't make sense when water was
> added"). So the **water FLOW / hydrology MODEL co-designs with erosion in Phase A** (one field:
> carved terrain + flow + sediment all derived together). Only the **visible water RENDERING**
> (rivers/lakes/ocean shaders) defers to Phase C. Erosion E1 (single-region coupled sim) is Phase A;
> E2–E4 (bake/stream/scale) are Phase B.

---

## ▶ NOW — finish Phase A · the combined eye-gate session (lanes PAUSED here)

Everything built so far is **default-off / approved-look, awaiting the user's live eye.** Both
lanes are parked until this batch is judged; then we act on results and continue Phase A.
**Run `scenes/review.tscn` — number keys 1-9 jump to each gate item** (guide + per-item judge
criteria in `NEEDS_REVIEW.md`). **Run the gates upstream → downstream so judgments aren't
contaminated:**

1. **Light first** (it washes everything): GI/SDFGI default decision (0b) → Sun disc Stage 1 (3b)
   → Lighting decouple + time-of-day Stage 2 (3c).
2. **Base shading:** H1 BRDF clouds-off regression (6).
3. **Ground under settled light:** GM batch GM1 + GM2 + GM3-A (1c) → Unit 2 distance-detail (4) if base reads good.
4. **Sky:** Clouds feature review (5) → god rays final (3) → sun-cloud occlusion.
5. **Whole-scene last:** AA in motion (7) + GI-proxy fidelity (2).

## ⏸ Phase A — the two lanes (FULL scope; finish before Phase B)

### GROUND / TEXTURE — roadmap to AAA
Lane roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`.
- **Built, awaiting the gate (1c):** GM1 palette, GM2 surface-height maps, GM3-A within-area
  variation (all default-off). Specs `specs/2026-06-20-ground-gm2-*`, `specs/2026-06-20-ground-gm3-*`.
- **Then (one phase past the gate at a time):** GM3 B/C true material patches (if A isn't enough)
  · GM5 detail layers — rock/pebble/debris scatter, decals, wetness · G3 placement realism
  (snow-on-shade, green-in-drainage) · macro color/value.
- **The big one — Terrain depth & hydrology (erosion + water-flow + surface height):** needs a
  **fresh brainstorm → spec → plan → review** (user's call). The water-coupled redesign of the
  erosion arc (`specs/2026-06-17-erosion-arc-design.md` is the base — already diagnoses the
  water-coupling root cause; the new spec elevates visible water to a co-designed output of one
  hydrology field). Pairs with the **material surface-height / POM** write-up (the "we didn't have
  height setup" fix — GM2, built fast, owes a proper spec + its eye-gate). **Build gated behind the
  GM eye-gate** (don't carve terrain before the material batch is judged).

### SUN & LIGHT — full sky system
Lane roadmap: `specs/2026-06-20-sun-light-system-architecture.md`.
- **Built, awaiting the gate (3b/3c):** Stage 1 sun disc; Stage 2 decouple (`ComposeLighting` +
  Time/Weather/Grade states) + analytic sun arc + keyframed day color (`time_of_day`). Spec
  `specs/2026-06-20-lighting-decouple-and-time-axis-design.md`.
- **Then (full scope, user's call — each gated, one phase ahead max):** Stage 3 night + moon
  (disc/phases/cool moonlight) + star field · the **GPU-compute physical atmosphere** (Hillaire
  LUTs — re-spec around the CloudVolume render-thread `Texture2Drd` seam, NOT FieldCompute; a
  local-RD texture can't be sampled by a material) · Stage 4 auto day/night cycle + fantasy/exotic.
  Designed now is fine; **build the atmosphere/night only after Stage 1+2 daylight is eye-gated.**

### CLOUDS (finish the review)
Shipped & accepted; owes the feature-by-feature sign-off (gate 5) + god-rays final pass (gate 3).
Reference: `cloud-system-overview.md`, `cloud-next-steps.md`, `godray-system-overview.md`.

## 🌍 Phase B — make it a WORLD (after Phase A is done)
Gated by the scale infra; hybrid build (spine first, content designed region-first + stream-aware).
- **Scale spine — CDLOD → chunks → streaming → infinite.** T1 pop-free on the fixed region (the
  gate; clipmap killed WG1–15 via pops) → T2 stable world tiles → T3 streaming. Also the dominant
  perf lever toward 8 ms. Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`.
- **Biomes** — climate field (moisture/temperature) selects per-region material palettes + rules +
  flora; the ground palette/rule system is already data-driven for this.
- **Macro procedural variety** — regions genuinely differ (not one mountain tiled).
- **Erosion at scale** — E2 drainage-skeleton bake → E3 per-chunk procedural detail → E4 coarse
  global pre-solve; consumes the Phase-A hydrology model. `specs/2026-06-17-erosion-arc-design.md`.
- **Flora integration** (parallel chat returns grass/trees/shrubs) + **world-editing integration**
  (brush + height-delta + undo) — wire providers; edits invalidate splat/breakup/scatter → re-bake.

## 🌧 Phase C — climate & elements
- **Visible water** — rivers/lakes/ocean rendering, consuming the Phase-A hydrology flow field.
- **Precipitation** — rain/snowfall on the Weather axis (currently a "future hook" only) + the
  ground **wetness** response (Unit 6 hooks) + dynamic weather.
- **Snow-on-ground** — aspect/temperature placement (partly in ground G3).

## ✅ Done & settled (approved — don't redo)
- **Base field** — WG15 5-layer GPU heightfield, no bake. PROVEN; don't touch its math. (Design:
  `archive/specs/2026-06-15-wg16-base-field-design.md`.)
- **Lighting base + 6 moods** — "really good" (now decoupled, re-gating as Stage 2).
- **Materials** — 738 → 108 accepted.
- **Ground** — Unit 1 anti-repetition; compositing-core Phase A weight-blend + AO; G1 placement. All APPROVED.
- **Clouds** — volumetric + multi-layer, accepted (broad review owed, gate 5).
- **Perf — GI/shadow proxy** (512²), default on, user-verified. Details: `performance.md`.
- **God rays** — screen-space radial scatter, shipped (final pass owed, gate 3).
- **Infra** — `Std430Writer`, `CallOnRenderThread` compute→material bridge, the lab + `FLAT
  BASELINE` harness, `--auto-shot`/`--profmove`/`--shadowcheck`.

## 📌 Conventions
- `main` = clean baseline · `experiment/presentation` = HEAD (work here). Git is the undo. Restore
  tag: `backup-doc-reset-2026-06-20`.
- Verify: build → headless `--import` → `--auto-shot` A/B + `--profmove`, then the USER's live eye
  (no TDD; GPU/visual). Never debug a motion artifact from a still.
- Launch (one Godot at a time): `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan
  scenes/terrain_lab.tscn`. Kill strays first.
- Gotchas (full list in `HANDOFF.md` + memory): ONE Godot at a time; always `--rendering-driver
  vulkan`; local-RD compute can't run `--headless`; per-frame compute→material via
  `CallOnRenderThread` (not CompositorEffect), assign `Texture2Drd` RID once; hand-packed std430
  drifts → use `Std430Writer`.
