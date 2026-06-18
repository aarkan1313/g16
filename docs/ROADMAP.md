# WG16 — Roadmap

Living view of what's done, in-flight, and queued. Newest status at the top of each
section. Pairs with HANDOFF.md (orientation) and DECISIONS.md (the why, per decision).
Last updated: 2026-06-17.

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost.

---

## ✅ Done & settled (don't redo)
- **Base field** — WG15 5-layer GPU heightfield on a displaced plane, no bake. PROVEN.
  Don't touch its math unless asked.
- **Lighting / atmosphere** — soft sun shadows, SDFGI+SSIL, SSAO, aerial+height fog, AgX
  tonemap, color grade, 6 curated MOOD presets. User: "really good."
- **Material judging** — 738 → 108 accepted materials (the library; choices are fine).

## ⚠ Review state (live in `scenes/terrain_lab.tscn`)
1. **Ground Unit 1 — anti-repetition** (Surface tab `anti-repeat`). REVIEWED → APPROVED by
   user. THE GATE for ground units 2-6 is passed.
2. **Clouds — heavily reworked + reviewed this session (2026-06-17→18).** Full refactor:
   world-space volumetric → multi-layer decks → audit-driven lighting/macro/stepping fixes.
   User verdict: "doesn't look bad overall" (acceptable; remaining polish in Cloud follow-ups).
   Shadow coupling PROVEN numerically (`--shadowcheck` r≈0.79 PASS). Sun disc renders+occludes.
3. **Cloud-presence suite** — mood cloud color, overcast dim + aerial, reflections. God rays
   REMOVED (FogVolume ripped out); to be rebuilt as in-march in-scatter (Cloud follow-ups).
4. **H1 BRDF check** — clouds-off terrain vs the approved look (custom `light()` = Burley+GGX);
   still wants an eye-confirm of no regression.

## 🔨 Active arc — GROUND PRESENTATION rebuild (6 units)
> Ground texturing is "functional but bad"; the presentation layer was never built out.
> Spec: `specs/2026-06-17-ground-presentation-arc-design.md`. Build order = impact; each
> eye-gated before the next. Shared seams: `ar_sample_wp` / `distanceWeight` /
> `groundData`+`breakup_tex`.
- [x] **Unit 1 — anti-repetition** (texture bombing). BUILT; awaiting live verify.
- [ ] **Unit 2 — distance detail** (near detail ⊕ far macro). PLANNED.
- [ ] **Unit 3 — surface depth** (parallax-occlusion + AO/normal/rough → BRDF). PLANNED.
- [ ] **Unit 4 — procedural breakup** (GPU-compute slope/curv/cavity/aspect masks). PLANNED.
- [ ] **Unit 5 — color/value** (replace washed macro tint; survive GI+AgX). PLANNED.
- [ ] **Unit 6 — "and more"** (scatter hooks/wetness/snow-by-aspect/hi-Q triplanar). PLANNED.
  Plans: `plans/2026-06-17-ground-unit{1..6}-*.md`. Don't build 2+ until Unit 1 verified.

## 🌊 Active arc — EROSION (re-introduced fresh; WG15's graveyard, done differently)
> WG15 had ~19 erosion versions + 5 water systems, all bad — root cause was a STACK of
> fighting solvers with no unifying drainage model. WG16 redoes it as ONE coherent coupled
> model, eye-gated in motion, downstream of the settled base field (behind a toggle).
> Spec: `specs/2026-06-17-erosion-arc-design.md`. Reconciles "really cool coupled sim" +
> infinite + small bakes via **bake the low-freq drainage SKELETON, synthesize detail
> procedurally**. Build order (user's call): prove the sim GREAT on the current single
> region FIRST, before any bake/stream/chunk infra.
- [ ] **E1 — coupled sim core + live lab** (droplet hydraulic + thermal on a local RD;
  pristine↔eroded toggle; watched cutting live). PLANNED — THE "is erosion good" gate.
  Plan: `plans/2026-06-17-erosion-unit1-sim-core.md`.
- [ ] **E2 — drainage skeleton bake** (compact coarse height-delta + flow + sediment; small
  on disk). LATER. ⚠ scoped reversal of WG16's no-bake stance, for erosion only.
- [ ] **E3 — semi-procedural detail synthesis** (per-chunk fine detail from the skeleton).
  LATER — needs a chunk/streaming system WG16 doesn't have yet.
- [ ] **E4 — coarse global pre-solve + streaming** (true-infinite). LATER — biggest infra,
  needs chunks. Build E1 → judge GREAT → E2 → E3 → E4, each its own plan, each eye-gated.

## 🌍 Parallel (isolated build chats — no project access → return LIBRARY code to integrate)
- **Procedural flora** — trees/grass/forests/shrubs; GPU-instanced scatter, LOD/impostors,
  wind. Returns drop-in units + interfaces (no demo).
- **World editing / terrain deformation** — brush system (raise/lower/flatten/smooth/noise/
  paint) over a GPU-compute editable height-DELTA layer + undo/redo; additive (doesn't touch
  base field). Returns library, no demo.
- **Integration owed on return (our side):** implement the height/biome/scatter providers,
  wire into the scene + terrain shader. World-editing edits invalidate splat + ground
  breakup masks (Unit 4) + flora scatter → re-bake after edits. Flora scatter density should
  later consume the biome/breakup/coverage fields.

## 🧭 Backlog — researched, not started (the remaining "great" levers)
- **Climate / moisture field** — drives material + color + wetness together (ties into
  ground units 4-5 + flora).
- **Water** — surface rivers/lakes/ocean (additive; placement improves once flow/erosion data
  exists). Considered for an isolated chat; deferred. Natural consumer of E2's flow field.
- **Composition tooling** — more hero-shot / framing aids.
- **Cloud follow-ups — ALL DONE 2026-06-18** (cloud-polish session; see
  `docs/cloud-system-overview.md` for the full feature/toggle inventory, and the memory note
  `cloud-look-real-rootcauses`). The session first found the audit's "already fixed" lighting was
  BUGGY (premultiplied-composite double-alpha, sun-extinction crushing direct light, weather mean
  0.32 + a dead macro octave, giant cloud scale) — fixing those is what actually made the clouds
  read good. Then the roadmap items, each behind a toggle defaulting to the validated look:
  1. ✅ **Per-deck phase/albedo** — per-deck phase_g/iso/albedo/sun_absorb/tint; `--perdeck` / `cloud_perdeck`.
  2. ✅ **Presets → layers** — presets author a deck stack (Overcast = 3 decks); `CloudVolume.SetLayers`
     + `SetLayerWeight` seam for the future weather/biome system; `--preset=N`.
  3. ✅ **Ranged presets + 'surprise me'** — seeded jitter around a known-good preset (coherent randomize).
  4. ✅ **Temporal amortization** — strided update + history blend; `temporal_frames` / `--temporal=N` (default 1 = off).
  5. ✅ **Texture res** — configurable dome res `--cloudtex=H` (default 512×128). [View-space half-res
     march + TAA remains a separate, EYE-GATED rewrite — not done.]
  6. ✅ **God rays** — in-march in-scatter (cloud-od sun visibility, NOT the directional-light shadow →
     no black wedges); `--godrays` / `cloud_godrays` + `godray_strength` (default off).
  - REMAINING (eye-gated, after the user reviews visuals feature-by-feature): the view-space march
    rewrite; horizon/distant-sky handling (below); sun-disc polish (below); snapshot cloud settings
    into mood presets.
- **Distant sky / horizon handling** (cloud refactor, 2026-06-17) — the far/horizon sky reads
  weird (flat cloud band + no convincing clear-sky falloff toward the horizon). Need a proper
  distance/horizon treatment: cloud density fade-to-haze near the horizon + a believable
  non-cloud sky gradient in the distance so a sparse sky doesn't look cut off. Surfaced in the
  T-checkpoint review; deferred to a focused pass after the cloud core is judged good.
- **Sun disc shader polish** (2026-06-17) — the sun now renders (cloud sky shader draws a
  LIGHT0-based disc + glow, clouds occlude it) but it's just a flat bright circle. Wants a real
  sun shader: limb darkening, corona/bloom, atmospheric scatter halo, sun-near-horizon
  reddening, so it reads "sunny" not "white dot." Smallish focused pass; do near-term (it's
  visible whenever clouds are on).

## 🌌 Future arc — DAY/NIGHT + CELESTIAL BODIES (near the end; big, its own spec)
> User vision (2026-06-17): a full sky-time system. Sequenced LATE — after clouds/ground/
> erosion cores are solid — because it's a large coordinated arc touching lighting, sky, and
> mood. Captured now so it's not lost.
- **Day/night cycle** — sun travels, time-of-day drives sky color + light energy + shadows;
  dawn/dusk/golden-hour transitions; sun peeking over / setting behind mountains.
- **Variable sun coverage / position** — sun height/azimuth as part of the cycle, not a fixed
  transform.
- **Moon(s)** — moon disc(s), phases, distinct cool moonlight + night ambient/GI; star field.
- **Fantasy options** — red/blood moon, differently-colored suns, multiple suns/moons, exotic
  skies — all as tunable/togglable looks (fits WG16's data-driven, full-range ethos).
- Coordinates with the existing MOOD presets (which would become time-of-day points) and the
  cloud mood-tint wiring. Almost certainly its own brainstorm → spec → plan when reached.

## 📌 Conventions
- main = clean baseline · `experiment/presentation` = HEAD (work here). Git is the undo.
- Verification: build → headless `--import` → `--auto-shot` A/B + `--profile`, then the
  USER's live eye (no TDD; GPU/visual). Never debug a motion artifact from a still.
- Key gotchas (full list in HANDOFF §4 + memory): ONE Godot at a time; always
  `--rendering-driver vulkan`; local-RD compute can't run `--headless`; per-frame
  compute→material via `CallOnRenderThread` (not CompositorEffect), assign Texture2Drd once.
