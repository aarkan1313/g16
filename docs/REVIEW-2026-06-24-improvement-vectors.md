# WG16 — Deep-Dive Improvement-Vector Review (2026-06-24)

Commissioned by the user ("deep dive and review, see what needs revamped, all vectors for improvement that
maintain quality"). Built from a parallel 4-agent review (terrain geometry · ground surfacing · terrain
lighting · code architecture) + the ARC-A perf decomposition. All findings grounded in real code/values.
Ranked within each area; the cross-cutting headlines are at the top.

---

## HEADLINES (the findings that change the plan)

1. **Ground surfacing is NOT blocked on hydrology — the biggest quality gap is needlessly deferred.** The
   "wait for hydrology, sequence LAST" framing is ~30% true: only **wetness masks + moisture-biome palettes**
   need the water substrate. Anti-tiling (user-APPROVED then STRIPPED), full normals/triplanar/per-material
   tiling, real height+POM, and the texture-array placement core are all `f(terrain fields)` and can land NOW
   for a dramatic quality jump. This is the single highest-value unlock in the whole review.

2. **The terrain lighting fill has NO occlusion term — valleys don't darken.** The analytic EMISSION sky-fill
   is a pure function of vertex normal; a valley floor gets the same sky light as an open plain. SSAO is
   screen-space (useless at terrain scale) and SDFGI was parked (camera-locked, nothing to bounce on smooth
   terrain). The fix is one coherent build: **generalize the already-shipped `horizon_shadow()` sun-march into
   a multi-direction visibility primitive** → sky-openness AO (real valley darkening) + a better-caster
   long-range shadow (relight #2) in one stroke. Makes SSAO/SDFGI moot on this terrain.

3. **Perf is healthy; the cheap levers are exhausted.** Frame ≈5.7 ms avg on a 5090 (under budget). Shadow
   atlas dial-down + shadow distance + cloud temporal stride are all MEASURED no-ops. SSAO is the only cheap
   lever (0.8 ms / ~2 ms-worst) and it's currently INVISIBLE (smooth placeholder terrain, no crevices). Real
   further wins are architectural (cheaper far-chunk geometry + fragment).

4. **The repo does not build from a clean clone.** Committed `Cli.cs`/`TerrainLab.cs` reference the water
   chat's UNCOMMITTED hydrology files → a fresh `dotnet build` fails (4 missing types). Latent landmine for
   CI / teammates / `git clean`. Fix with a committed interface stub seam (don't touch the water files).

---

## AREA 1 — Ground surfacing (BIGGEST quality gap; mostly UNBLOCKED)

Current reality (corrects the handoff's "blank placeholder"): a minimal textured slice SHIPPED — 5 PBR
materials (`ground.gdshader` fragment, `use_textures=true` default) blended by height band + slope→rock,
triplanar **only on rock**, normal maps on **only 2 of 5** materials, Toksvig spec-AA, **no POM/height, no
anti-tiling, single 12 m tile**. It passed a narrow "readable landforms" bar and was told to STOP.

Ranked vectors (none need hydrology except #7):
1. **Texture-array placement core** (reset spec Units 1-4) — `sampler2DArray` (32+ slots) + per-pixel
   placement from (height/slope/curvature/aspect/warped-noise) + top-4 blend. Removes the 5-material ceiling,
   makes it biome-ready. **L effort, no hydrology dep.** The foundation.
2. **Re-host histogram anti-tiling** — the ONE user-PASSED ground feature, STRIPPED in the slice. Regression
   recovery; fixes the most-visible artifact (repetition). **M, none.**
3. **Full normal-maps (all mats) + triplanar everywhere + per-material tiling** — 3 of 5 mats are flat-shaded
   now; sand/snow want different scales. The legibility lever. **S-M, none, works on the current 5-sampler path.**
4. **Real height maps + POM/relief** (reset Unit 5; the Poisson `HeightCompute` bake already exists). The
   "textured → AAA" close-up payoff. **M (bake) + L (POM), none.**
5. **Within-area variation / macro breakup** — kills the uniform-patch read; free byproduct of #1's warped
   placement noise, or re-host GM3-A interim. **S/free, none.**
6. **Distance detail/blend LOD** — replace the (default-off, wrong) dissolve-to-mean with top-N→top-1 blend
   collapse far out. Perf headroom + modest far quality. **S, none.**
7. **Wetness / riverbank / moisture-biome materials** — the ONLY genuinely hydrology-gated vector. **M, needs substrate.**

Authoritative arc: `docs/superpowers/specs/2026-06-21-ground-material-system-reset-design.md`.

## AREA 2 — Terrain lighting / shadows / GI

The through-line: terrain's only "where light is blocked" sources are the CSM map (~5% of pixels), the
`horizon_shadow()` macro-march, and SSAO@0.6. The EMISSION fill has zero occlusion.
1. **Analytic sky-visibility (horizon-openness) AO on the fill** — multi-azimuth (4-8 dir) coarse macro-march →
   open-sky cone → valley/basin darkening. The missing dominant AO cue on smooth terrain. **M, High quality.**
2. **Relight #2 long-range shadows — fix the CASTER RESOLUTION.** `field_macro_height` is continent+uplift
   ONLY, so hills/ridges can't cast. Add a cheap mid-freq occluder (one fbm octave + ridge) to the march. The
   signature low-sun vista cue. **M, High, watch motion shimmer.**
3. **Enrich the analytic fill** (occlusion from #1 + opposing-slope colored bounce); **keep SDFGI OFF** until
   erosion canyons / flora give it geometry to bounce on. **M, Med-High.**
4. **Retire/replace SSAO** with #1's analytic AO (SSAO is screen-space → useless at terrain scale). **S, Med.**
5. **Occluder-distance-aware penumbra** in `horizon_shadow` to match CSM contact softness. **S-M, Med.**
6. **Overcast-couple the fill** (colors flatten to grey, bounce attenuates under cloud — currently it doesn't). **S, Med.**
7. Fix fill default mismatch (shader 1.0/1.0 vs eye-gated 0.40/2.40); fold `ground_albedo` into ALBEDO. **S, Low.**

Headline: #1+#2+#4 are ONE build — generalize the shipped sun-march into a multi-direction visibility primitive.

## AREA 3 — Terrain geometry / CDLOD

1. **Per-vertex field eval is 5× redundant** — 4-tap finite-difference normal → analytic gradient (the field
   already computes derivatives). ~5×→1.4× field cost AND exact normals (quality GAIN). Touches the shared
   `field_math.gdshaderinc`; gate `--fieldcheck`/`--popcheck`. **M.** The enabler for the two below.
2. **Adaptive grid resolution per LOD** — every chunk renders full GridN=65 incl. fog-occluded far rings;
   65/33/17 variant sets by level. Makes the ARC-B "more rings" north-star affordable. **M-L, graveyard-adjacent
   (gate hard with stitch/morph checks), behind a toggle.**
3. **Batch/persist the async AABB probe** (per-chunk StorageBuffer create+readback+free churn). Pure perf. **M, low risk.**
4. **Adaptive `MaxChunkOps` birth budget** (scale with velocity/backlog; vel already plumbed). **S, low.**
5. **Octave-gate ceiling**: field kills detail finer than ~8 m (`analytic_spacing=4`), so the finest 2 m mesh
   has nothing fine to show — close-range smoothness ceiling. Ungate finer octaves at fine LODs (eye-gate
   shimmer). **M, look change.**
6. `DistanceToCellXZ` ignores altitude → high-camera wastes budget on under-camera chunks. **S, low-confidence.**

## AREA 4 — Code architecture / correctness / tech debt

1. **Standalone-build break (HIGHEST)** — committed code refs uncommitted water types; clean clone won't
   compile. Fix: committed `IWaterRenderer`/`IWaterRegionBinder` interface + `NullWater` default impl; redirect
   the 2 call sites; leave the water files untracked. **S-M.**
2. **CLI dispatcher is the new god-surface** — ~110 hand-rolled `else if` flag branches + ~95 `_xxxCli` fields,
   3 unset-sentinel idioms, parse-order hazard only partially closed (`MatchFlag` on ~6 of ~110). Replace with
   a flag registry (data table) — closes the hazard by construction. **M, convert in batches behind --*check.**
3. **Self-check + CI gaps** — gate checks exit-code well, but `--lightcheck`/`--cloudstats` are print-only
   (always exit 0); NO build/CI gate exists (which is why #1 went unnoticed). Add headless `dotnet build` +
   `--*check` lane. **S.**
4. **44 hardcoded `/root/TerrainLabRoot/...` lookups** across 8 files, some per-frame — cache refs in `_Ready`. **M, low.**
5. **Muddy working tree** — 4 orphan `.cs.uid` deletions (safe to commit) + cross-chat modified files (diff
   per-lane before committing; don't bundle water WIP). **S.**
6. **Residual dead code** — `FlyCamera.cs` orphaned under `scripts/workbench/`; god-ray occ-mode-3 + `hex_contrast`
   dead control (defer to ground lane). **S, low.**

---

## RECOMMENDED SEQUENCING (pillar-led: quality = perf = AAA = long-term-best)

The compounding, lowest-risk, highest-quality path:
- **A. Quick hygiene (S, do anytime):** SSAO cut (free, invisible now) · standalone-build seam (#A4.1) · commit
  the orphan `.uid` deletions. Unblocks clean builds + banks perf.
- **B. Ground surfacing slice (the biggest visible jump, UNBLOCKED):** re-host anti-tiling + full
  normals/triplanar/per-material tiling on the current path → then the texture-array core. No hydrology.
- **C. Lighting visibility primitive (the biggest realism lever):** multi-direction macro-march → sky-openness
  AO (replaces SSAO) + better-caster relight #2. Makes valleys read.
- **D. Geometry analytic-gradient normals:** quality gain + perf + enables adaptive far-LOD (north-star).

B and C are the two that most move "AAA". A is cheap hygiene. D is the perf+quality foundation. Water/biomes/
erosion remain downstream of their own arcs.
