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
- **GI/SDFGI decision ✅ RESOLVED 2026-06-20 (eye-gate key 8):** default **SDFGI off + GI proxy off**
  (cascade box artifact removed, sharp shadows, ~4.7 ms). Parked, not purged — revive GI with flora /
  canyons / night. See DECISIONS + NEEDS_REVIEW 0b.
- **Shadow & Lighting pass (NEW stage — user's call 2026-06-20, "with whatever we have"):** with SDFGI
  parked, take the current **sharp directional-shadow** setup to AAA before/alongside Stage 3. Scope to
  spec: directional **CSM / cascade tuning** (splits, bias, range — kill any remaining shadow shimmer/
  shifting), **contact + soft (PCSS-style) shadows**, the **proxy-on cheap-coarse-shadow perf lever**
  (~2.8 ms vs ~4.7 ms — quality/cost A/B), and an **SSIL re-check** (still on; another view-dependent
  GI term — decide keep/tune/off). Eye-gated like the rest; design-ahead OK, build one phase past the
  last gate. Pairs naturally with the moonlight shadow work in Stage 3.
- **Daylight ✅ GATED 2026-06-20 (live):** Stage 1 sun disc (3b), Stage 2 decouple + time-of-day (3c),
  sun **surface** shader + presets (default `realistic_midday`), BRDF regression (6), clouds review (5),
  god rays (3). Specs `2026-06-20-lighting-decouple-and-time-axis-design.md`,
  `2026-06-20-sun-surface-shader-and-presets-design.md`.

#### ☀️🌙 FINISH THE FULL SKY SYSTEM (user's call 2026-06-20: "plan it all, make a roadmap, lets do it")
> **SKY LANE: Stage 3 (Night & Celestial) ✅ COMPLETE 2026-06-20** (all 4 sub-phases eye-gated live).
> Next item below is #2 (Clouds overhaul). Resume: pick #2/#3/#4 → brainstorm→spec→plan→gate.

Ordered, each its own spec → plan → eye-gate, built ONE phase past the last pass (discipline rule).
Master architecture: `specs/2026-06-20-sun-light-system-architecture.md`.
1. **Night & Celestial (Stage 3) ✅ DONE + GATED 2026-06-20** — 3a night sky (24h arc, sun-below-horizon,
   tunable darkness/floor) · 3b moon (textured/cratered, phases + terminator, cool halo, cloud-occluded) ·
   3c moonlight (2nd cool shadow-casting directional, gated night×moon-up×phase) · 3d stars + Milky Way
   (procedural 3D-noise, twinkle/rotation/fade). All on a **Night lab tab** (22 knobs) + **6 celestial
   presets** (`celestial_presets.json` + picker + `--celestial`). Also shipped: fog-wash night-grade,
   `L` inspection light, sun-disc limb polish. Plan `plans/2026-06-20-night-and-celestial.md`. Spec
   `specs/2026-06-20-night-and-celestial-design.md`. Owed (minor, await user eye): celestial-preset
   dropdown live-check; optional live moon/moonlight color pickers.
2. **Clouds overhaul — IN PROGRESS** (`specs/2026-06-20-clouds-overhaul-design.md`, plans
   `plans/2026-06-20-clouds-co1-vertical-realism.md` + `plans/2026-06-20-clouds-co2-types.md`). Phased CO-1
   vertical realism → CO-2 types (cirrus 2D layer + stratus shape-mode; cumulus preserved) → CO-3
   anti-repetition/horizon → CO-4 presets. **Weather-axis tie-in DEFERRED** (own later stage, user's call).
   - **CO-1 vertical realism — ✅ GATED 2026-06-20 (live).** Per-deck `height_profile` (flat base → faded/anvil
     top), layer fields 19-21, lockstep ×3 shaders; Clouds-tab toggle + knobs + `--cloudprofile`. Kept **opt-in
     (default-off)** — approved look unchanged; default-on is a later call.
   - **CO-2 types — ✅ GATED 2026-06-20 (live).** Stratus shape-mode (field 22, cellGate→sheet, **broken with
     gaps**, lockstep ×3) + **world-anchored** cirrus 2D layer (finite-altitude parallax + drift + per-region
     density/orientation/size variety, kept ~free via early-out + 3-octave noise). Clouds-tab `cumulus↔stratus`
     + `cirrus: coverage/density/wind/scale/sharpness/variety/speed/altitude`; `--stratus`/`--cirrus`; review
     key 6 cycles cumulus→stratus→cirrus. Stratus = selectable type, cirrus = opt-in layer (default off).
   - **CO-3 anti-repetition — 🔨 BUILT 2026-06-20, default-off/banked.** Macro coverage/size variety (field 23,
     lockstep ×3) clusters cumulus into varying-size groups; Clouds-tab `macro variety` + `--antirepeat`.
     On-chunk benefit marginal (user couldn't tell); the visible repetition is the far/down **"huge dome"**
     regime → **Phase-B (infinite world)**, banked. Horizon: no clear defect found from the live look — left.
     High-effort code review done (4 fixes incl. shadowcheck-active-config; 1 pre-existing detail-erosion
     divergence deferred). See DECISIONS.
   - **CO-4 presets — 🔨 BUILT 2026-06-20, awaiting the picker flip-through eye-gate.** 6 new Clouds-tab
     presets composing the new types (Fair Weather Cumulus, Cirrus Veil, Cumulus & Cirrus, Overcast Stratus,
     Stormy Anvil, Mackerel Sky); `--preset=N`. `ApplyCloudPreset` resets the type levers first → self-contained
     presets. Verified distinct via auto-shots. **#2 Clouds overhaul ~COMPLETE pending this gate** (weather-axis
     tie-in remains DEFERRED to its own stage).
3. **GPU-compute physical atmosphere — ✅ SPEC'd 2026-06-20** (`specs/2026-06-20-gpu-atmosphere-design.md`).
   Hillaire LUTs on the CloudVolume **`Texture2Drd`/`CallOnRenderThread` seam, NOT FieldCompute** (memory
   `compute-to-material-callonrenderthread`). Toggleable sky-color provider, **default OFF = approved
   keyframed look until it wins its A/B gate**. Phased AT-1 core sky color → AT-2 aerial perspective →
   AT-3 cloud-lighting integration (AT-3 after #2 lands). Supersedes the keyframed `day_script` for sky color.
4. **Stage 4 — IN PROGRESS** (`specs/2026-06-20-stage4-cycle-and-fantasy-design.md`).
   - **ST4-1 auto day/night cycle — ✅ GATED 2026-06-20 (live, "looks pretty good").** Clock in `_Process`
     (play toggle + `cycle speed` + `--autotime`); manual scrub preserved. Follow-ups from the gate: **moon
     arc decoupled from the sun** (own phase-lagged arc — FIXED) · **galaxy/Milky Way needs review** (banked,
     NEEDS_REVIEW 10).
   - **ST4-2 fantasy/exotic — 🔨 BUILT 2026-06-20, awaiting the picker gate.** Cross-system presets
     (`fantasy_presets.json` + `FantasyPresets.cs` + Night-tab picker + `--fantasy=N`) composing a sun preset
     + celestial preset + a new persistent `sky_tint` + moon/moonlight colors: blood_moon, alien_green,
     violet_night, harvest, ember_dusk. No time set → the look holds as the cycle runs. Verified distinct.
   **Multiple suns/moons DEFERRED** (own spec). **#4 Stage 4 ~COMPLETE pending this gate.**
5. **Shadow & Lighting pass** — CSM/cascade tuning, contact + soft (PCSS) shadows, the proxy-on cheap-
   shadow perf lever (~2.8 vs ~4.7 ms), SSIL re-check. Pairs with Stage-3 moonlight shadows.

## 🌍 Phase B — make it a WORLD (after Phase A is done)
Gated by the scale infra; hybrid build (spine first, content designed region-first + stream-aware).
- **Scale spine — CDLOD → chunks → streaming → infinite.** T1 pop-free on the fixed region (the
  gate; clipmap killed WG1–15 via pops) → T2 stable world tiles → T3 streaming. Also the dominant
  perf lever toward 8 ms. Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`.
  - **Banked (user-flagged 2026-06-20, Sun/Light lane):** the cloud field reads as a **camera-anchored
    "huge dome"** at far/down extreme-distance views (clouds line up / repeat far out). Fine on-chunk;
    revisit for the infinite world (the cloud shell + cirrus altitude-plane both anchor to the camera).
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
