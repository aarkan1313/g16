# WG16 — NEEDS REVIEW (the live eye-gate queue)

One place for everything **built but awaiting the user's live eye** — because the gate for look/feel
is always the user flying it, never a still or a mechanical check (project rule). Work the list when
you're back at a screen. Each item: **how to see it · what to judge · what it unblocks.**

How to run (windowed, one Godot at a time):
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn`
Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Judge **in motion**, and at
**close / mid / far** for ground items. `FLAT BASELINE` (top of panel) + per-item toggles isolate things.

Last updated: 2026-06-19.

---

## 🟥 P1 — blocks the active arc

### 0. Compositing core — PHASES A+B — BUILT 2026-06-19, awaiting ONE combined eye-gate
- **What:** the whole compositing core (plan `plans/2026-06-19-terrain-compositing-core.md`), both phases
  built behind toggles defaulting to the current look:
  - **Phase A (Blend quality):** bake emits 7 smooth role weights → two linear Rgba8 weightmaps; fragment
    samples them, picks **top-2** roles, blends by a derived **material-height interlock**, with organic
    breakup (weight-domain warp + interlock noise). Behind **`splat_blend_mode`** (Splat tab): **0 = legacy
    index (current, default)**, **1 = weightmap (new)**.
  - **Phase B (Surface relief):** **parallax-occlusion** on the dominant plane, LOD-gated near
    (**`pom_on`**, Detail tab, default OFF) using the same derived height channel; material **AO** bound
    into the custom BRDF (**`ao_on`**, default on); normal-map application strengthened.
- **See it:** `... scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`, then fly **close / mid / far**, in motion:
  - Splat tab → toggle `blend: legacy/weightmap` 0↔1; tune `interlock sharpness/height drive/breakup`, `boundary warp (m)`.
  - Detail tab → toggle `surface depth (POM)` + `material AO`; tune `POM steps/depth m/fade @`. Debug tab `normal maps`/`force matte` bisect the BRDF.
- **Judge:** (A) mode 1 **less blocky**, organic relief-driven boundaries, no speckle/swimming, top-2 swap
  seams? (B) up close, real depth/self-occlusion vs flat; POM swimming or near→mid fade pop; AO believable;
  far unchanged (POM gated)?
- **Mechanically verified:** compiles/bakes/renders; weightmap in-motion clouds-off 164 fps/6.1 ms; POM-on
  ~5.4 ms; no speckle/NaN/black. NOT judged: whether it reads good — that's this gate.
- **On approval → unblocks:** flip `splat_blend_mode` default→1 and `pom_on` default→true; then **Unit 4
  (procedural breakup)**, then **Unit 5 (macro color)**. Escalations noted in the plan: top-3 blend (swap
  seams), per-plane POM (cliff seam), real height maps (if derived relief too weak).

### 1. Ground G1 — rule-based placement engine ✅ APPROVED 2026-06-19
- **Verdict (user, live):** "rule placement does work, it's basic, will want a lot more in the future but this proves the basics work." Gate PASSED — the engine + tunable knobs are in; richer rules grow via G3 + future expansion.
- **Unblocked → G2 (curated palette)** is the next build. Spec `specs/2026-06-19-ground-foundation-splatting-design.md`, plan `plans/2026-06-19-ground-foundation-g1-rule-engine.md`.

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
