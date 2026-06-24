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

## AREA 5 — GPU-COMPUTE / code-level perf (no quality loss) — added 2026-06-24 (user: "all-out performance, gpu compute")

**The base floor (3.7 ms) is VERTEX-BOUND.** The chunk vertex shader evaluates the full multi-octave field
**5× per vertex per frame** (`ground.gdshader:248-269`: h0 + 4 finite-difference normal taps), each a
`field_height` (continent 5-oct + uplift 3× + slope_damped 6-oct + ridges 6-oct). GridN=65 × ~570 chunks × 5 =
**~12M field evals/frame** — pure recompute of a STATIC field. This is the single biggest code-level lever.

1. **★ Per-chunk field CACHE via compute (THE all-out win) — L effort, ~3 ms+ (removes most of the floor),
   quality-IDENTICAL.** Bake each chunk's height(+normal) to a `Texture2DArray` slice ON BIRTH via GPU compute
   (amortized by the MaxChunkOps budget), sample it every frame instead of re-evaluating. **Decisive de-risk:
   `ChunkAabbProvider` ALREADY IS 90% of this** — async render-thread per-chunk `field_height.glsl` dispatch,
   throttled (MaxRequestsPerFrame=8), chunk-keyed, born-generous→refined-async — it currently THROWS AWAY the
   height grid it computes (only keeps min/max). Extend its 7×7 probe → a 66×66 height+normal bake, keep the
   buffer, upload to an array slice. Geomorph-compatible via the standard 2-sample blend
   `mix(texel(u_fine), texel(u_coarse), morphK)` (both static, both in the baked fine texture). ~20 MB VRAM at
   570 chunks. Quality-identical (same field math, byte-identical param packing → `--fieldcheck` safe).
   **The ONE real decision:** softly reverses the documented "no bake — generate live" stance — BUT this is a
   per-chunk ASYNC TRANSIENT cache (not the WG15 global-offline-bake pain), and the AABB provider already
   crossed that line. Dual-path (live-eval until the bake lands, like the AABB tighten) + a `cache_ready` flag.
   Gates: `--fieldcheck`/`--popcheck`/`--morphcheck`/`--stitchcheck`. Reversible behind a flag (like TightenAabb).
2. **Analytic-gradient normal (the contained hedge) — M, ~1.5-2 ms.** Drop the 4 FD normal taps → derivatives
   from one eval (`value_noise_d`/`slope_damped_fbm` already track `dsum`). 5→~2 evals. Quality neutral-positive
   (analytic = smoother than FD). NOTE: becomes moot in the *vertex* path if #1 ships (but the bake-side normal +
   the fragment `horizon_shadow` still use it). A cheap 2-tap forward-diff (5→3, ~1.5 ms) has precedent in the
   `use_analytic` branch (`ground.gdshader:285-287`) but shifts the normal slightly (eye-gate).
3. **Cache the per-pixel `horizon_shadow` macro-march → world-XZ texture — M, ~1.5 ms (low-sun only).** It's a
   function of (world-XZ, sun) only, recomputed per-pixel (up to 16 `field_macro_height` steps); bake to a coarse
   region texture, refresh only on sun movement. Reuse the AABB-provider RD pattern. (Lighting review #2 synergy.)
4. **MSAA 2× → TAA A/B — S, ~0.3-0.8 ms.** `project.godot` msaa_3d=1; TAA may AA better (+ the speckle) cheaper.
   Look-trade → eye-gate (ghosting).
5. **Batch ChunkAabbProvider GPU round-trips (8 syncs→1) — M, ~0.2-0.5 ms birth-burst.** Per-birth
   StorageBuffer create+`BufferGetData` blocking readback+free ×8/frame → one batched dispatch + persistent ring.
6. **Ground fragment weight-gate — M, ~0.3-0.6 ms.** Always samples all 5 materials + rock triplanar even where
   weight≈0; `if (w>eps)` skip (near = pure-neutral).
7. Cloud sun-march T-threshold early-out (~0.2 ms, look-gated); cache camera node ref (<0.1 ms).

**Already-optimal (don't touch):** cloud temporal stride (=2, pixel-identical); CDLOD pool (identity-keyed,
incremental); cloud/shadow uniform-sets (reused). Cheap config dials (atlas/distance/temporal) confirmed no-ops.

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
