# WG16 — Roadmap

The single source of truth for what's done, what's being judged, and what's next. Thin by
design. Pairs with `NEEDS_REVIEW.md` (the eye-gate queue), `DECISIONS.md` (the why, per
decision), and `HANDOFF.md` (orientation). Older designs are frozen in `docs/archive/`.

Last updated: 2026-06-20 (doc-set reset).

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost. Lead
with the better option, not the cheap shortcut.

---

## 🧭 The discipline rule (read first — this is why we reset)

In five days WG16 grew 20 specs + 23 plans — the exact "process outran the results" sprawl it
was founded to avoid, and **both active lanes built three phases deep past their last eye-gate**
because the user couldn't always be at a screen to judge. The cure, encoded:

> **A lane builds at most ONE phase ahead of the last PASSED eye-gate.** When the gate queue holds
> work the user can't yet see, **STOP and bank — do not open new depth.** Build behind toggles
> defaulting to the approved look so everything is opt-in and reversible.
>
> **Thin docs:** a roadmap line + a `DECISIONS.md` entry per choice — not a plan-per-lane. A spec
> only when a feature is big enough to need one; a plan only for the phase being built now.

The eye is the only gate for look. Mechanical checks gate correctness/cost. Never judge a motion
artifact from a still. Always profile `--profmove`.

---

## ▶ NOW — the combined eye-gate session (both lanes PAUSED here)

Everything below is **built, committed, default-off / approved-look, awaiting the user's live
eye**. Both active lanes are parked until this batch is judged, then we re-roadmap from the
results. Full per-item "how to see it / judge / unblocks" lives in `NEEDS_REVIEW.md`.

**Run the gates in this order — upstream → downstream, so judgments aren't contaminated:**

1. **Light first** (it washes everything else):
   - GI/SDFGI default decision (`NEEDS_REVIEW` 0b) — park SDFGI off? (~2.4 ms for ~no visible gain now)
   - Sun disc — Stage 1 (`NEEDS_REVIEW` 3b)
   - Lighting decouple + time-of-day — Stage 2 (`NEEDS_REVIEW` 3c)
2. **Base shading:** H1 BRDF clouds-off regression (`NEEDS_REVIEW` 6)
3. **Ground under settled neutral light:** GM batch GM1 + GM2 + GM3-A (`NEEDS_REVIEW` 1c) →
   then Unit 2 distance-detail (`NEEDS_REVIEW` 4) **only if** the base now reads good.
4. **Sky:** Clouds feature-by-feature review (`NEEDS_REVIEW` 5) → god rays final pass
   (`NEEDS_REVIEW` 3) → sun-cloud occlusion.
5. **Whole-scene last:** AA in motion (`NEEDS_REVIEW` 7) + GI-proxy fidelity confirm (`NEEDS_REVIEW` 2).

---

## ⏸ The two paused lanes

### GROUND — roadmap to AAA
Lane roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md` (the 7-layer stack + GM1–GM6).
- **Built, awaiting the combined gate (1c):** GM1 curated palette, GM2 real per-material height
  maps (Poisson normal→height bake), GM3-A within-area variation. All default-off / approved-look.
  Active specs: `specs/2026-06-20-ground-gm2-real-height-maps-design.md`,
  `specs/2026-06-20-ground-gm3-within-area-variation-design.md`.
- **Next after the gate (do NOT start yet):** GM3 Approach B/C (true material patches via texture
  arrays), GM4 (placement realism + erosion), GM5 (detail layers), GM6 (scale/perf · CDLOD).

### SUN & LIGHT
Lane roadmap: `specs/2026-06-20-sun-light-system-architecture.md` (3-axis Time × Weather × Grade
+ celestial).
- **Built, awaiting the gate (3b/3c):** Stage 1 sun disc (`sun_layers()` in `cloud_sky.gdshader`);
  Stage 2 decouple — one writer `ComposeLighting` + Time/Weather/Grade(+SunDisc) states + analytic
  sun arc + keyframed day color script (`time_of_day` knob). Active spec:
  `specs/2026-06-20-lighting-decouple-and-time-axis-design.md`.
- **Deferred (NOT next):** the **GPU-compute Hillaire atmosphere** (transmittance/sky-view LUTs)
  was pulled OUT of Stage 2 — it's a net-new subsystem and must be re-spec'd around CloudVolume's
  render-thread `Texture2Drd` seam, NOT the FieldCompute pattern (a local-RD texture can't be
  sampled by a material — see memory `compute-to-material-callonrenderthread`). Stage 3 (night +
  moon/stars) and Stage 4 (auto cycle + fantasy) remain future stages.

---

## ✅ Done & settled (approved — don't redo)
- **Base field** — WG15 5-layer GPU heightfield on a displaced plane, no bake. PROVEN. Don't
  touch its math. (Design archived: `archive/specs/2026-06-15-wg16-base-field-design.md`.)
- **Lighting / atmosphere base** — soft sun shadows, SDFGI+SSIL, SSAO, aerial+height fog, AgX
  tonemap, color grade, 6 curated MOOD presets. User: "really good." (Now decoupled — being
  re-gated as Stage 2.)
- **Materials** — 738 → 108 accepted (library choices are fine).
- **Ground Unit 1 — anti-repetition** (stochastic bombing). APPROVED.
- **Ground compositing core — Phase A weight-blend** (top-2 roles + height interlock + breakup) +
  **AO**. APPROVED ("it actually looks good"). POM deferred (needs GM2 height — now built).
- **Ground G1 — rule-based placement.** APPROVED ("proves the basics work").
- **Clouds** — world-space volumetric, multi-layer decks, lighting root-caused & fixed; accepted
  ("doesn't look bad overall"). Broad feature review still owed (gate 5). Reference:
  `cloud-system-overview.md`, `cloud-next-steps.md`.
- **Perf — GI/shadow proxy** (512² heightfield copy feeds SDFGI+shadows). User-verified blob-free,
  default on. Big in-motion win. Details: `performance.md`.
- **God rays** — screen-space radial scatter, shipped (final visual pass owed, gate 3). Reference:
  `godray-system-overview.md`.
- **Infra/process** — `Std430Writer`, the `CallOnRenderThread` compute→material bridge, the lab
  + `FLAT BASELINE` bisection harness, `--auto-shot`/`--profmove`/`--shadowcheck`. Keep.

## 📋 Backlog / parked arcs (not started — designs frozen in `docs/archive/`)
Re-roadmap these after the gate session; re-spec fresh when scheduled rather than porting.
- **Erosion** (E1 coupled sim → E2 skeleton bake → E3 detail → E4 stream). The biggest "real vs
  noise" lever; feeds ground placement (GM4). `archive/specs/2026-06-17-erosion-arc-design.md`.
- **Terrain LOD / CDLOD** (T1 pop-free on this region → T2 tiles → T3 stream). The dominant perf
  lever toward 8 ms + the keystone streaming/erosion-E4/world-editing/flora all need.
  `archive/specs/2026-06-18-terrain-lod-roadmap-design.md`.
- **Perf to 8 ms** — current in-motion ~9.6 ms (clouds on). Levers, all eye-gated: CDLOD (#1),
  SDFGI config / cheaper GI (#2, ~2.9 ms), cloud cost (#3, ~2 ms). `performance.md`.
- **Flora integration** — parallel chat returns grass/trees/shrubs libraries; wire providers;
  scatter consumes biome/breakup/coverage fields; re-bake on edits.
- **World-editing integration** — parallel chat returns a height-delta brush + undo/redo; wire
  providers; edits invalidate splat/breakup/scatter → re-bake.
- **GPU-compute atmosphere** (deferred sun stage) · **Stage 3 night + moon/stars** · **Stage 4
  auto day/night + fantasy** — re-spec around the CloudVolume render-thread seam.
- **Climate / biome field**, **Water**, **Distant-sky / horizon handling**, **Composition tooling**.

## 📌 Conventions
- `main` = clean baseline · `experiment/presentation` = HEAD (work here). Git is the undo.
- Verify: build → headless `--import` → `--auto-shot` A/B + `--profmove`, then the USER's live eye
  (no TDD; GPU/visual). Never debug a motion artifact from a still.
- Launch windowed (one Godot at a time): `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver
  vulkan scenes/terrain_lab.tscn`. Kill strays first.
- Gotchas (full list in `HANDOFF.md` + memory): ONE Godot at a time; always
  `--rendering-driver vulkan`; local-RD compute can't run `--headless`; per-frame compute→material
  via `CallOnRenderThread` (not CompositorEffect), assign `Texture2Drd` RID once; hand-packed
  std430 buffers drift → use `Std430Writer`.
