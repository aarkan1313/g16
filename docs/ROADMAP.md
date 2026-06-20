# WG16 — Roadmap

Living view of what's done, in-flight, and queued. Newest status at the top of each
section. Pairs with HANDOFF.md (orientation) and DECISIONS.md (the why, per decision).
Last updated: 2026-06-19.

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost.

---

## ✅ Done & settled (don't redo)
- **Base field** — WG15 5-layer GPU heightfield on a displaced plane, no bake. PROVEN.
  Don't touch its math unless asked.
- **Lighting / atmosphere** — soft sun shadows, SDFGI+SSIL, SSAO, aerial+height fog, AgX
  tonemap, color grade, 6 curated MOOD presets. User: "really good."
- **Material judging** — 738 → 108 accepted materials (the library; choices are fine).

## ⚡ Perf arc — TARGET 8 ms total (in-motion is the real metric; profile with `--profmove`)
> **Long-term budget = 8 ms (~125 fps) for the WHOLE world generator**, with flora + water + erosion +
> biomes still to fit. Current flying frame (1440p, clouds on) = **9.6 ms** → over budget before those.
> **Lesson banked:** static `--profile` hid the flying cost (SDFGI converges when still) — ALWAYS use
> `--profmove`. Full decomposition + numbers in `docs/performance.md`.
- [x] **GI/shadow PROXY — BUILT + DEFAULT ON (2026-06-19).** Coarse 256² heightfield copy feeds SDFGI
  + casts shadows so the 4M-vert detail mesh isn't re-voxelized/re-shadowed each frame. In-motion 1440p:
  clouds-off 55→131, clouds-on 49→101 fps. `--giproxy=0/1` / Debug toggle. ⚠ GI/shadow *fidelity*
  eye-check still owed (default-on was the user's call). Restore tag `backup-pre-gi-proxy-2026-06-19`.
- **Remaining levers to 8 ms — ALL eye-gated → PARKED until the user can do visual checks:**
  1. **CDLOD terrain LOD** (mesh floor ~4 ms, resolution-independent; also cuts SDFGI+shadow further) —
     the dominant lever. Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`. Gate: pop-free in motion.
  2. **SDFGI config / cheaper GI** (~2.9 ms intrinsic cascade cost even on the proxy) — cascade/cell/
     update tuning behind toggles; quality/eye call.
  3. **Cloud cost** (~2.0 ms) — temporal + dome res + march steps.

## ⚠ Review state (live in `scenes/terrain_lab.tscn`)
1. **Ground Unit 1 — anti-repetition** (Surface tab `anti-repeat`). REVIEWED → APPROVED by
   user. THE GATE for ground units 2-6 is passed.
2. **Clouds — refactored, then POLISHED to "good" (2026-06-17→18).** World-space volumetric →
   multi-layer decks → the cloud-polish session found the audit's "already fixed" lighting was
   BUGGY (premultiplied-composite double-alpha, sun-extinction crushing direct light, dead
   weather macro-octave + mean 0.32, giant scale) and fixed the real root causes + finished the
   cloud roadmap #1-#6 (per-deck lighting, presets→layer stack, coherent randomize, temporal
   amortization, configurable dome res, in-march god rays), each behind a toggle. Coupling PROVEN
   (`--shadowcheck` PASS throughout). Lab menu complete + correctly linked. ⮕ Awaiting the user's
   feature-by-feature visual review — see `docs/cloud-next-steps.md` + `docs/cloud-system-overview.md`.
3. **Cloud-presence suite** — mood cloud color, overcast dim + aerial, reflections. (God rays
   are now REBUILT as in-march in-scatter — toggle, default off; no FogVolume.)
4. **H1 BRDF check** — clouds-off terrain vs the approved look (custom `light()` = Burley+GGX);
   still wants an eye-confirm of no regression.

## 🔨 Active arc — GROUND: roadmap to AAA (RE-SEQUENCED 2026-06-20)
> **⮕ AUTHORITATIVE GROUND ROADMAP: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`** — read it
> for the full 7-layer stack + the GM1–GM6 build sequence. The items below are the prior framing,
> kept as *layer* designs; the new doc fixes their ORDER and completeness.
>
> **Why re-sequenced:** building one shader "Unit" at a time kept under-delivering because the two
> highest-impact layers — the material *assets* (curated palette + real height maps) — were deferred
> as polish, so every variation/breakup layer decorated monochrome, flat materials. **Unit 4
> (breakup) eye-gate FAILED + PARKED** (default off; `3077abe`): context masks are f(heightfield) →
> ~uniform within a uniform area → add between-area, NOT within-area, variety; baked 4m-coarse →
> square edges; monochrome palette → no contrast. Re-sequence, NOT a reset — nothing proven is dropped.
>
> **NEW SEQUENCE (each eye-gated; own spec→plan):**
> - **GM1 — Curated palette** (NEXT; picker already exists → curation + `ground_palette.json` load path).
> - **GM2 — Real per-material height maps** (flat→deep; revives heightblend interlock + POM).
> - **GM3 — Within-area variation** (procedural world-pos material patches = the real "variety in an
>   area" fix + macro color (Unit 5) + distance detail (Unit 2)).
> - **GM4 — Placement realism + Erosion** (G3 aspect/moisture/flow + Erosion E1; revives parked Unit 4
>   masks as a SECONDARY bias on erosion-carved features).
> - **GM5 — Detail layers** (scatter/decals/wetness/flora).
> - **GM6 — Scale & perf** (CDLOD → chunks → RVT → streaming; also the dominant perf lever).
>
> ── prior framing (2026-06-19; layer designs the GM sequence reuses) ──

## 🔨 (prior) GROUND: build the MATERIAL SYSTEM properly (reframed 2026-06-19, eve)
> **⮕ STATUS (2026-06-19, eye-gated live):** compositing core built (plan
> `plans/2026-06-19-terrain-compositing-core.md`). **Phase A weight-blend (smooth top-2 weightmaps +
> height interlock + breakup) APPROVED + SHIPPED** (`splat_blend_mode` default 1) — user: "it actually
> looks good." **AO** bound + kept. **POM (relief) DEFERRED** — built behind a toggle (default off) but
> derived-roughness height is too flat; it only reads with **real height maps** → see the new "height maps"
> deferred arc below. **NEXT material levers:** Unit 4 (procedural breakup — also adds the per-area variety
> the user noticed missing), Unit 5 (macro color). ⚠ User also flagged **GI/SDFGI** ("doesn't do much" +
> weird blocky shadows) → under review (see NEEDS_REVIEW 0b; possible purge = perf win toward 8 ms).
>
> **⮕ CURRENT DIRECTION (supersedes the framing below):** live review showed the ground is "only
> frameworked" — **blocky** (splat blends nearest dom/sec on a grid) + **flat** (no relief). The roadmap
> below planned most layers but **never had a plan for the blend/compositing QUALITY layer — the gap.**
> Approved: build the standard AAA **weight-blended material pipeline** as a real system, **compositing-
> core first** (Blend quality: smooth weights + height-map interlock + organic breakup; + Surface relief:
> parallax + real normals), then breakup (Unit 4), then color (Unit 5). G1 placement APPROVED, feeds it.
> Ceiling seams deferred: real height maps (derive first) + RVT caching (infinite-world perf; needs
> chunks). Spec `specs/2026-06-19-terrain-material-system-design.md`; handoff
> `handoffs/2026-06-19-terrain-material-system.md`. Perf: top-2 blend · POM LOD-gated near · masks baked ·
> profile in-motion.
>
> **⛏ DEFERRED arc — real height maps (revives POM + sharpens the blend interlock).** The 108-material
> library has albedo/normal/roughness/AO only; POM + height-interlock currently derive height from inverted
> roughness, which is too flat for real relief. To get genuine surface depth: generate per-material height
> maps (from albedo/normal, or sourced) and bind `z*_hgt`; the POM seam + `material_height_uv` are already
> in place to consume them. User's call to do this when it's worth it; POM stays off until then.
>
> ── prior framing (units/foundation — still the layer plan-set the system reuses) ──
> Live judging proved the ground "doesn't look good enough to judge detail" — it's "random
> ground" because material PLACEMENT (companion = `dominant−1` by index) + PALETTE (grey subset)
> were never designed; the original arc wrongly deferred them to the end as polish. **Reordered:
> build the placement+palette FOUNDATION first** (rule-based splatting), THEN the detail units on
> top. Foundation spec: `specs/2026-06-19-ground-foundation-splatting-design.md` (supersedes the
> 2026-06-17 arc's ORDER). Scope (user): make THIS region really good FIRST → chunks → infinite →
> biomes last. Baked-once → fragment flat → protects the 160+ fps target. Shared seams:
> `ar_sample_wp` / `distanceWeight` / the baked splat (now rule-driven) + `ground_palette.json`.
>
> **FOUNDATION (rule-based splatting — the gate; each eye-gated):**
- [ ] **G1 — signals + rule engine in the bake.** Replace altitude/slope bands with a signal set
  (altitude, slope, signed curvature, aspect, moisture/flow proxy, cavity) + role placement rules;
  reuse the 7 slots as ROLES, current materials. Gate: placement COHERENT (right material right place).
- [ ] **G2 — curated palette.** `data/ground_palette.json` + per-role dropdowns; contrast-rich picks
  chosen live. Gate: reads photoreal, not drab.
- [ ] **G3 — aspect + moisture/flow rules** (snow-by-aspect, green-follows-drainage, sediment in
  channels) + cavity→contact. Gate: reads like real terrain follows water + exposure.
>
> **DETAIL (resume on top of the good foundation):**
- [x] **Unit 1 — anti-repetition** (texture bombing). BUILT + APPROVED.
- [~] **Unit 2 — distance detail** (near detail ⊕ far macro). **BUILT 2026-06-19, default OFF, SHELVED**
  until the foundation reads good (only added a near-band albedo tweak on an unready base). Plan:
  `plans/2026-06-17-ground-unit2-distance-detail.md` (built w/ 2 deviations: perf-pass preserved,
  toggle default off).
- [x] **Compositing core (was Unit 3 + new blend layer) — Phase A blend SHIPPED, AO kept, POM deferred.**
  Plan `plans/2026-06-19-terrain-compositing-core.md`. Weightmap top-2 + height interlock APPROVED
  (`splat_blend_mode` default 1). AO bound + kept. POM built but deferred (needs real height maps).
- [🅿️] **Unit 4 — procedural breakup** — BUILT (T1–T6) + eye-gate FAILED + **PARKED** (`breakup_on`
  default off, `3077abe`). Bakes slope/curv/cavity/aspect/flow masks → fragment overlay. Failure
  root cause (see the GM roadmap's "Lesson banked"): masks are f(heightfield) → no within-area
  variety; 4m-coarse → square edges; monochrome palette → no contrast. **Infra kept** for revival
  as a SECONDARY context bias in **GM4** (after erosion gives it real features). Plan
  `plans/2026-06-19-ground-unit4-breakup.md`.
- [ ] **Unit 5 — color/value tint** (hue/value break that survives GI+AgX — the macro-tint layer,
  distinct from G2's base palette). PLANNED.
- [ ] **Unit 6 — "and more"** (scatter hooks / wetness / hi-Q triplanar). PLANNED.
  Note: old Unit 4 (breakup masks) + the palette half of old Unit 5 are now folded into the FOUNDATION
  (G1/G3 + G2). Old plans `plans/2026-06-17-ground-unit{3..6}-*.md` stay valid for the detail units.

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

## ⛰ Planned arc — TERRAIN LOD (CDLOD) — spec'd 2026-06-18, not yet scheduled
> The terrain is ONE 2048² PlaneMesh (4M verts, NO LOD) = the ~3.8 ms perf "floor" (perf pass
> 2026-06-18) + a ×4 shadow-cascade redraw, and it's the keystone streaming/erosion E4/world-
> editing/flora all need. **Clipmap killed WG1-15** via ELEVATION + QUALITY POPS — so the whole
> arc is built around POP-FREE CONTINUOUS LOD (geomorphing + detail cross-fade), proven in motion
> before any infra. Chosen: **CDLOD (quadtree + per-vertex geomorph)** — the AAA heightfield
> standard, whose purpose IS killing LOD pops; stable world-XZ tiles fit WG16's per-region systems
> (clipmap's moving rings don't). Spec: `specs/2026-06-18-terrain-lod-roadmap-design.md`. Memory:
> `terrain-clipmap-killed-wg1-15`.
- [ ] **T1 — pop-free continuous LOD on the current fixed 8 km region.** THE GATE: geomorph +
  cross-fade, ZERO elevation/quality pop in motion. Fixes the floor + shadow redraw. No streaming.
- [ ] **T2 — stable world tiles** (per-tile LOD + edge stitch + cull; per-region bakes → per-tile). LATER.
- [ ] **T3 — streaming** (load/unload tiles → true-infinite; erosion E4 / world-editing / flora consume). LATER.
  Anti-WG1-15 discipline: T1 must be eye-approved pop-free BEFORE T2/T3 (no infra before the core).

## 🌍 Parallel (other chats — coordinate, don't double-work)
- **God rays — DONE (2026-06-19): screen-space radial scatter.** Shipped `GodRaysScreen.cs` +
  `shaders/godray_screen.gdshader` (GPU Gems 3 Ch.13). The 3 old approaches (FogVolume, in-march,
  uniform-fog) were all stripped — they read as washy fog, never beams. New = fullscreen-quad radial
  blur of a HYBRID occlusion buffer (terrain depth + clouds via local-to-sun luminance) from the sun's
  screen position → real crepuscular shafts where clouds occlude the sun. Clouds-tab "god rays
  (screen-space)" toggle + 5 tunable sliders; presets Showcase/Subtle/Dramatic; test scene
  `scenes/godray_test.tscn`. Default off. User approved; thorough visual review pending.
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
## ☀️ ACTIVE arc — SUN & LIGHT (started 2026-06-19; user pulled it forward after the god-ray work)
> **⮕ MASTER ARCHITECTURE + ROADMAP: `specs/2026-06-20-sun-light-system-architecture.md`** — the full
> vision. Per user direction (2026-06-20): a **fully decoupled, data-driven sky & light system** = three
> orthogonal axes (**Time × Weather × Mood/grade**) + a **Celestial** content layer, every parameter a
> knob, any combination valid ("any weather at any time, any mood at any time — it's a complex system").
> Today a "mood" bundles all of it; the arc splits it behind one `LightingComposer`. Target: full-range
> tunable (physical default → stylized → fantasy). Whole arc handoff: `handoffs/2026-06-19-sun-light-arc-handoff.md`.
- **Stage 1 — Sun disc polish — BUILT + on `experiment/presentation` (2026-06-19→20), builds clean.** The
  flat white dot is replaced by `sun_layers(rd, aSun)` in `cloud_sky.gdshader`: limb-darkened disc + corona
  + warm atmospheric halo + horizon reddening/growth (elev-driven, `+LIGHT0_DIRECTION.y`) + soft
  optical-depth cloud occlusion (dim+redden). 8 new Light-tab controls (sun_size/limb/corona_size+energy/
  halo_size+energy/redden/redden_onset/horizon_grow/cloud_redden) + per-mood pushes + slider sync + "Golden
  Hour Sun" preset. NOTE: the old `sun_disc` control is shadow-penumbra (LightAngularDistance/PCSS), NOT the
  visible disc — that's the new `sun_size`. Built via subagent-driven-development on a worktree branch, then
  cherry-picked onto `experiment/presentation` (`b9cf52d..6a08f93`) to avoid the parked-Unit-4 terrain
  shader. ⚠ EYE-GATE still owed (visual review of the disc/halo/reddening in motion). Spec/plan under
  `docs/superpowers/{specs,plans}/2026-06-19-sun-disc-polish*`.
- **Stage 2 — Decouple + Time-of-day driver (daylight) — BUILT through the arc (2026-06-20), eye-gate owed.**
  `LightingComposer`/`ComposeLighting` is the ONE writer (absorbed `UpdateOvercast`); lighting split into
  Time/Weather/Grade(+SunDisc) state structs; `time_of_day` knob (+ `--time`, sunrise/sunset/noon-height)
  drives the sun along an analytic arc (feeds `OrientSun`, `--shadowcheck` PASS) + a **keyframed daytime
  color script** (`time_presets.json` day_script) for sky/sun-color/ambient — cool dawn → bright noon →
  warm dusk. The 6 moods still reproduce (via `MoodToStates`). Commits `fcb0b43..12cbf3a`. **The GPU-compute
  atmosphere is DEFERRED to its own future stage** (over-scoped + a render-thread defect: local-RD textures
  can't be sampled by a material — needs CallOnRenderThread+Texture2Drd like CloudVolume; correction banked
  in the plan's Task 5). Remaining decouple polish (independent Weather/Grade pickers, retire last bundled
  paths) is optional. ⮕ user eye-gates Stage 1 + this, then a project-wide re-roadmap. Spec/plan
  `2026-06-20-lighting-decouple-and-time-axis*`.
- **Stage 3 — Night & celestial** — extend Time through 24 h: sky darkens to night + night ambient/GI;
  **moon** (disc + phases + cool moonlight 2nd light) + **star field**; clouds occlude sun AND moon.
- **Stage 4 — Auto day/night cycle + fantasy/exotic** — a running clock (play/pause/speed) with smooth
  dawn/dusk transitions; blood/colored moon, colored/multiple suns/moons, exotic sky palettes (all knobs).

> (Full per-stage scope + acceptance in the master architecture doc above. The old "DAY/NIGHT + CELESTIAL
> BODIES future arc" is now Stages 3–4 here.)
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
