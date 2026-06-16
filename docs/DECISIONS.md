# WG16 — Decisions Log

One short entry per decision, newest first. The point is to not re-litigate
settled choices and to give "low plans" a memory. Link a spec only when a feature
was big enough to warrant one. Date · what · why.

---

**2026-06-16 — Terrain LOOK LAB built (terrain_lab.tscn) — the texturing tool.**
After the splat/ground-texture attempts kept missing, switched to a tool-first
approach: a lab on the same base field with an on-screen panel to swap, live: 7
zone materials (valley→peak), 6 mask modes (height / height+slope / noise-broken /
curvature / steep-cliff / noise-biome), 5 blend modes (flat → full-stack PBR), and
preset save/load. Presets = the embryo of the biome system. Built but NOT yet
flown — user explores combos next. Compiles clean. Room to add more masks/shaders
(each is one case in the swapper).

**2026-06-16 — Material library judged down from 738 → 108 accepted, then deduped.**
Discovered the real texture library is huge (~1,015 PBR material folders system-wide
under D:\assets, 738 distinct after collapsing seed/variant copies — a biome-organized
`world 4/candidates` tree + `world3/textures/wgv3` + AmbientCG packs). Built a
**material judging loop** (board scene): one material at a time, full-stack PBR,
**1=pass / 3=fail**, verdicts persisted to data/material_verdicts.json (resumes
across sittings). User judged all 738 → **113 pass**. Conservative dedupe dropped 5
(1 pixel-dupe rock035=rock_dark + 4 non-materials) → **108 clean accepted**, heavily
alpine/volcanic/tundra/rock (fits the mountainous terrain). Verdicts + the derived
material_library.json are committed; the 2.5 GB of texture files are gitignored
(re-derivable from D:\assets). **Lesson:** most of the library is AI-generated with
stamped flora (leaves/ferns) that tiles badly — surface-only materials are the keepers;
flora is a future procedural-decoration pass, never baked into ground textures.

**2026-06-15 — Texturing arc: ported WG15 splat → many problems → reverted.**
The WG15 4-texture splat was tuned for altitude; at eye level it showed blocky
border-jitter (raw floor-hash) and screen-space grain. Tried smooth-noise jitter,
lower detail-bump, disabling FXAA/SSAO; user still rejected → reverted to the M1
ramp. Key process lesson that led to the lab + judging approach: don't port a
tuned-for-one-distance shader; build fresh, judge at eye level, give every layer a
live toggle, and make iteration cheap (git + sandbox scenes). Added MSAA (kept).

**2026-06-15 — M3 splat REVERTED; back to the M1 minimal height/slope ramp.**
The ported splat showed an artifact up close that the user judged bad: a
grain/pattern that "appears when you stop, fine when moving." We chased the likely
causes (blocky border-jitter from a raw floor()-hash → fixed with smooth value
noise; over-strong detail bump → reduced; FXAA, then SSAO → disabled as suspects)
but the user still saw it and called the revert. **Lesson:** the WG15 splat was
tuned for one viewing distance/altitude; at eye level it has a screen-space
artifact we didn't root-cause. Reverted the shader, removed texture wiring + splat
knobs from LabTerrain/PresentationParams. Kept MSAA (a pure win, splat-independent)
and the lighter fog default in JSON; restored SSAO + scene fog to M1 values. Build
clean, M1 confirmed clean at the close angle where the splat broke. Texture PNGs
left in `assets/` (unreferenced) so a future, properly-debugged splat is easy to
re-add — but next time we build the material FRESH and judge it at eye level
early, not port WG15's tuned-for-altitude one. The base field itself is unaffected
(presentation-only churn — the modular boundary held).

**2026-06-15 — M3 texture splat landed (presentation lane).** Ported WG15's
proven 4-texture height/slope splat (grass/rock/snow/gravel, border jitter,
biplanar rock, distance-faded detail bump) into `lab_terrain.gdshader`, trimmed of
the erosion delta map and water/flow mask. Wired the 4 textures + 13 splat knobs
through `PresentationParams` → `LabTerrain.ApplyPresentation`. Presenter-only, zero
field risk. Build clean, renders clean (no shader-compile errors). AWAITING the
user's fly-verdict on the textured look. Chose splat over lighting-polish/reseed
because it's what makes the terrain actually judgeable (was a flat color ramp).

**2026-06-15 — Make a small fixed set of orienting docs (README, TECH_STACK,
DECISIONS), then build out slowly.** "No plans doesn't work, lots of plans
doesn't work, maybe low plans will work." WG15 generated 19 specs + 20 plans in 6
days and the process outran the results. So: a thin durable doc set + one-line
decision entries, not a plan per lane.

**2026-06-15 — Tech-stack policy locked: C# default, GPU compute for parallel
per-cell math, Rust only for measured serial hot paths.** Right tool for
performance + quality, nothing speculative. Everything modular (swap a unit
without rewriting neighbors). See [TECH_STACK.md](TECH_STACK.md). WG15 lesson
baked in: profile the parallel fraction before promising a Rust speedup (its port
hoped 10×, got 2.6× — serial routing was the real ceiling).

**2026-06-15 — Render the base field with a minimal height/slope color ramp
first (M1); texture splat deferred to M3.** "Build piece by piece." See the raw
field read clean before layering presentation on top. **VERDICT: user flew it,
"looks good."** Base field confirmed in the no-bake project.

**2026-06-15 — No bake stage. Base field generated live on the GPU.** The bake
existed in WG15 only to hold erosion deltas; we dropped erosion, so the bake has
no reason to exist. The field was always a fast pure GPU function (~390 ms for
2048²). User: "try base field without bake, lets see what happens" → it works.

**2026-06-15 — Erosion content dropped; architecture kept.** WG15's base field
passed across 8 seeds; the erosion pipeline (valley_carve → stream_power →
hydraulic → thermal → alluvial) + 5 water systems churned across ~19 versions and
were judged bad. We keep the proven field + clean unit boundaries; if erosion
ever returns it's a fresh, judged-early single pass, NOT a port of the old
pipeline.

**2026-06-15 — Fresh project at `C:\Wg16\wg-16-project`, not an in-place strip of
WG15.** Cleanest separation; WG15 stays untouched as reference. Ported the Field
unit ~verbatim, trimmed the precision-ladder ABI (a gated no-op with no consumer;
params block 144B → 128B).
