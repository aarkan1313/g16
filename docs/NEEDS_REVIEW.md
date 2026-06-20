# WG16 — NEEDS REVIEW (the live eye-gate queue)

One place for everything **built but awaiting the user's live eye** — because the gate for look/feel
is always the user flying it, never a still or a mechanical check (project rule). Work the list when
you're back at a screen. Each item: **how to see it · what to judge · what it unblocks.**

How to run (windowed, one Godot at a time):
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn`
Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Judge **in motion**, and at
**close / mid / far** for ground items. `FLAT BASELINE` (top of panel) + per-item toggles isolate things.

Last updated: 2026-06-20.

---

## 🟥 P1 — blocks the active arc

### 0. Compositing core — eye-gated 2026-06-19 (plan `plans/2026-06-19-terrain-compositing-core.md`)
- **Phase A (Blend quality) ✅ APPROVED + SHIPPED (default).** User flying it: "weightmap is good! it
  actually looks good." Bake emits 7 smooth role weights → two linear Rgba8 weightmaps; fragment picks
  **top-2** roles, blends by a derived **material-height interlock** + organic breakup. `splat_blend_mode`
  **default now 1 (weightmap)**; legacy index path kept behind mode 0 for A/B. Knobs (Splat tab):
  `interlock sharpness/height drive/breakup`, `boundary warp (m)`. The de-block is the win; it does NOT add
  more material *variety* per area (that's placement/roles — G3/Unit 4, a separate lever).
- **AO ✅ kept.** Bound into the custom BRDF (`ao_on`, default on). User: "nice little detail, could do
  more" — fine as-is; revisit strength later.
- **Phase B POM ⏸ DEFERRED (built, default OFF).** User flying it: relief only "raises elevation a little"
  even tuned. Root cause: derived inverted-roughness height is nearly flat on this library (no height maps).
  POM is binary — it only reads with **real height maps** (spec ceiling seam #1). Decision (user): if we do
  height maps + POM later, **defer** now (seam kept, zero runtime cost). → see ROADMAP "height maps" arc.

### 0b. GI/SDFGI — INVESTIGATED 2026-06-19; decision handed to the LIGHT/SUN chat (don't change here)
- **⚠ Another chat owns light/sun (scene WorldEnvironment) — do NOT change `sdfgi_enabled`/ambient/GI
  proxy from a material chat; coordinate.** This is the investigation result for them to act on.
- **Finding (objective, `--sdfgi=`/`--giproxy=` A/B + pixel-diff, cam 0,120,0,-22,0, clouds off):**
  toggling SDFGI changes the rendered image by **0.002–0.003 / 255** (invisible) — confirmed the user's
  "does nothing." It is **NOT misconfigured/broken:** tested SDFGI on the full detail mesh (proxy off) →
  still ~0.003; and a custom `light()` does not block engine GI. It's **functioning but redundant on
  smooth open sky-lit terrain** (nothing to bounce/occlude; sky-ambient+SSAO already cover it).
- **Cost (in-motion `--profmove`, clouds off):** SDFGI on+proxy on **5.2 ms** · SDFGI **off**+proxy on
  **2.8 ms** (−2.4 ms) · SDFGI off+proxy **off** (sharp detail-mesh shadows) **4.7 ms**. So SDFGI costs
  ~2.4 ms for zero visible benefit *now*.
- **Recommendation (PARK, don't purge):** default SDFGI off + GI proxy off (sharp shadows, no blocky
  patches, ~4.7 ms, fully reversible toggles) → big step toward the 8 ms target with no visible loss.
  **Re-evaluate GI when flora / erosion canyons / day-night+moonlight land** — those add the occluding/
  enclosed geometry that makes GI earn its cost. Don't delete it.

### 1. Ground G1 — rule-based placement engine ✅ APPROVED 2026-06-19
- **Verdict (user, live):** "rule placement does work, it's basic, will want a lot more in the future but this proves the basics work." Gate PASSED.

### 1b. Ground Unit 4 — procedural breakup ❌ FAILED eye-gate 2026-06-20 → PARKED (not in the review queue)
- **Verdict (user, live):** hard square edges + "barely does anything." Built T1–T6, root-caused, **parked** (`breakup_on` default off, `3077abe`). NOT awaiting re-review — it's the wrong tool for "variety within an area" (masks are f(heightfield)). Infra kept for GM4 (revive after erosion). See DECISIONS 2026-06-20.
- **⮕ Active ground direction RE-SEQUENCED (2026-06-20):** material foundation first. Authoritative roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`.

### 1c. Ground GM1 (palette infra) + GM3-A (within-area variation) — BUILT, awaiting live eye-gate (2026-06-20)
- **Both built/committed, default-on, near-zero cost; awaiting the user's eye (built while user couldn't visualize).**
- **GM1 — data-driven palette** (`61dd605..90c39ef`): `data/ground_palette.json` (active `alpine_stone`; also `alpine_green`=prior look, `arid`) → loads on startup, per-role tuning via **Zones-tab dropdowns**. **GM1 T4 live audit owed:** fly to where roles vary (cliffs/peaks, not just the warm sand basin), under **midday/neutral mood** (the warm default washes it) at close range — isolate whether drab = lighting vs material saturation vs placement, then curate the 7 picks. Plan `plans/2026-06-20-ground-gm1-curated-palette.md` T4.
- **GM3-A — within-area variation** (`82f60fb`,`4108ad2`): fragment-side multi-scale world-pos noise modulating **roughness + relief + value/sat** of the composited surface (the real "variety within an area" lever; Unit-4 done right). Profmove 4.8 ms = baseline (free). **See it:** Color tab → **`within-area variation`** toggle + `var roughness`/`var value`/`var relief`/`var scale macro/meso`. **Judge (close/mid on a uniform slope):** does it stop reading uniform — drier-lighter-rougher vs damper-darker-smoother patches, organic, no tiling/squares, no shimmer? Far ~unchanged (`var far keep`). **Verdict gates Approach B/C** (true different-material patches via texture arrays — see `specs/2026-06-20-ground-gm3-within-area-variation-design.md`). Plan `plans/2026-06-20-ground-gm3a-within-area-variation.md` T3.

## 🟧 P2 — perf default needs a fidelity confirm

### 2. GI/shadow proxy — ✅ RESOLVED 2026-06-19 (default 512²)
- **What happened:** the original 256² proxy produced a big SDFGI dark-blob on steep ground (its ~32 m cells deviated from the real terrain → false GI occlusion). Bumped resolution → **512² (~260k verts) is blob-free (user-verified)** at ~8.4 ms in-motion; 1024² also clean but ~2 ms costlier; 256² blobbed. Default set to **512²**, tunable via `--proxyres=N`.
- **Net:** in-motion 1440p clouds-off 55→119 fps. Toggle `GI/shadow proxy (perf)` (Debug) / `--giproxy=0/1`.
- **Still inherent (NOT the proxy):** SDFGI's camera-centered cascades make a faint lighting shift "follow the camera" — that's SDFGI itself, on the perf-lever list (SDFGI config / cheaper GI), separate from this.

## 🟨 P3 — built earlier, eye-gate still owed

### 3. God rays — FINAL PASS (rebuilt in a parallel chat — coordinate there)
- **Status:** mostly finished (screen-space rework, in another chat). Needs a final visual pass.
- **See it:** Clouds tab → **`god rays`** toggle (or `--godrays=1`), clouds on, sun toward camera through cloud gaps.
- **Judge:** Do the shafts read as believable sun-through-cloud light (crisp where wanted, not uniform fog, not washing the scene)? Tune to taste with the god-ray chat's knobs.
- **Note:** god-ray files are owned by the other chat — coordinate; don't edit `GodRays*`/`shaders/godray*` from this thread.

### 3b. Sun disc polish (Stage 1, Sun & Light arc) — BUILT + published 2026-06-20, eye-gate owed
- **Status:** built + cherry-picked onto `experiment/presentation` (`b9cf52d..6a08f93`), builds clean. The flat
  white sun dot is now `sun_layers(rd, aSun)` in `cloud_sky.gdshader`: limb-darkened disc + corona + warm halo
  + horizon reddening/growth + soft optical-depth cloud occlusion. Spec/plan `docs/superpowers/{specs,plans}/2026-06-19-sun-disc-polish*`.
- **See it:** `--preset=8` ("Golden Hour Sun") `--godrays=0 --lookatsun` for the warm low sun; or any mood +
  raise/lower **Light tab → `sun height`** to swing high(neutral white)↔low(warm/red + grown). Drift a cumulus
  across the sun (or raise coverage) for the dim+redden cloud occlusion + halo bleed.
- **Judge:** Does it read as a believable glowing sun (limb-darkened disc, tight corona, soft warm halo) vs a
  flat circle? Does it redden + grow at the horizon and stay neutral-white high? Does cloud dim AND warm it
  softly (halo bleeding around the edge)? No banding / hard ring at the disc/halo edge.
- **Knobs (Light tab):** `sun size (deg)` (the VISIBLE disc — the old `sun size` is now "shadow penumbra"),
  `sun limb darken`, `sun corona size/energy`, `sun halo size/energy`, `sun horizon redden`, `redden onset`,
  `sun horizon grow`, `sun cloud redden`.
- **Built via:** subagent-driven-development (6 tasks, each spec+quality reviewed; opus whole-branch review = "ready
  to merge, no Critical/Important"). All rendering lives in `sun_layers()` inside `sky()`; `sun_disc_energy` stays
  decoupled from `LIGHT0_ENERGY` (overcast dims the directional light, not the visible disc); the halo is kept LOCAL
  to the sun cone (no broad-sky recolor — that's deliberately Stage 2). Elevation sign verified `+LIGHT0_DIRECTION.y`.
- **Pre-checked (controller, stills only — NOT a substitute for your live eye):** high sun (mood 2) = neutral-white
  soft disc + gentle halo, not over-bloomed; low sun (mood 0) = warm orange halo + reddening; Golden Hour preset =
  warm low sun with a lit cloud band. Build clean.
- **Watch for (known soft spots to judge):** (1) the default `sun size (deg)` = 0.6 renders a SMALL disc (limb
  darkening is sub-pixel at default) — decide if the default disc should be bigger. (2) The Golden Hour preset sun
  sits very low (8°) so its disc can read subtle / partly behind the cloud band — also swing a clear-sky low mood to
  see the bare warm disc. (3) `sun horizon redden` > 1 intentionally pushes the tint past the target color (a
  strength boost, not a bug). (4) cloud occlusion needs a cumulus actually crossing the sun — raise coverage (~0.8)
  if the sun sits in a gap.
- **Unblocks:** Stage 2 (time-of-day driver) — being designed now in parallel (`specs/2026-06-20-sun-light-system-architecture.md`).

### 4. Ground Unit 2 — distance detail (BUILT, default OFF, SHELVED)
- **See it:** Detail tab → **`distance detail`** + `detail amt/scale/normal/fade dist` (or `--detail=1`). **Fly down CLOSE** — it's a ~250 m near band; does nothing from altitude.
- **Judge:** Up close, real micro-detail vs flat? Any **swimming/crawling** of the derived normal in motion, or a fade **pop** at mid range? Far should be unchanged.
- **Unblocks:** only worth judging **after G1+G2 make the base good** — it's polish on the foundation. Plan `plans/2026-06-17-ground-unit2-distance-detail.md`.

### 5. Clouds — feature-by-feature visual review
- **See it:** Clouds tab (coverage/density/type/decks/presets/temporal/etc.), `--clouds=1`, `--preset=N`.
- **Judge:** the per-feature checklist in `docs/cloud-next-steps.md` (+ `cloud-system-overview.md`). Distant-sky/horizon handling + sun-disc polish are the known soft spots.
- **Unblocks:** signs off the cloud system as "reviewed good."

### 6. H1 — BRDF regression check
- **See it:** clouds OFF terrain. Compare against the approved look.
- **Judge:** the custom `light()` (Burley+GGX replica + cloud-shadow hook) shows no regression vs the engine default it replaced (the perf pass touched this shader — all bit-near-identical, but an eye-confirm is owed).

### 7. AA in motion
- **See it:** currently MSAA 2×. Compare MSAA off / FXAA / TAA.
- **Judge:** crawl/shimmer on terrain edges + cloud ghosting (TAA) vs the perf cost. Pick the AA. See `docs/performance.md`.

---

## ⏸ Not "review" — build-when-you-can-see (eye-gated, parked)
These need your eye to *build*, not just approve — listed so they're not forgotten:
- **CDLOD terrain LOD (T1)** — the dominant remaining perf lever toward the **8 ms** target (mesh floor ~4 ms; also cuts SDFGI+shadow). Gate is pop-free-in-motion. Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`.
- **SDFGI config / cheaper GI** (~2.9 ms intrinsic in motion) and **cloud cost** (~2 ms) — the #2/#3 perf levers; quality/eye calls.
- **Erosion E1** — coupled sim core, watched cutting live (the "is erosion good" gate). Spec `specs/2026-06-17-erosion-arc-design.md`.
