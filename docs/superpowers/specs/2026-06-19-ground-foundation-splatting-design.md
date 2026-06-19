# Ground Foundation — Rule-Based Splatting + Curated Palette (Design)

Date: 2026-06-19 · Status: awaiting user review · Lane: surface/look (the splat BAKE + palette;
fragment compositing largely unchanged)

> **Supersedes the build ORDER of the ground-presentation arc** (`2026-06-17-ground-presentation-arc-design.md`).
> That arc treated material *placement* + *palette* as late units (4 + part of 5). Live judging proved
> the opposite: the ground "doesn't look good enough to judge detail" because the **foundation —
> meaningful placement + a palette that reads — was never built**. This spec pulls that foundation
> forward and makes it the gate; the detail/depth/color-tint units resume on top of it afterward.

## Why (the diagnosis, confirmed live)

The ground reads "random" and "drab." The machinery already exists — a GPU-baked splat mask
(`SplatCompute` → `splat_weights.glsl`), 7 height/slope zones, relief height-blend, macro color
drift, contact/crevice shading, a faithful Burley+GGX `light()` — but it does **arbitrary** things:

1. **Placement has no meaning.** The companion material blended into each zone is literally
   `clamp(dominant − 1, 0, 6)` — *the previous zone by array index*, not "what belongs next to
   this." That is the core "random" smell.
2. **The palette is near-monochrome.** The 7 zones pick a grey subset (grass, dry lichen,
   scree+lichen, loose scree, dark slate, alpine scree, snow) — almost no hue *or* value contrast,
   so it reads as grey mush with no "lush valley → barren rock → white peak."
3. **Placement logic is altitude+slope bands only.** No aspect (sun exposure), no flow/wetness, no
   real curvature — so material doesn't follow water and exposure the way real terrain does.

The detail/parallax/color units (arc Units 2, 3, 5) are polish on an unbuilt base. The fix is to
build the base: **a meaningful, multi-signal material-placement system + a curated photoreal
palette** — the AAA way to do terrain splatting.

## Goal / definition of done

The current 8 km region's ground reads as believable, photoreal-within-our-textures alpine terrain
**in motion at close / mid / far**: green where it should be, rock on steep faces, scree on convex
breaks and below cliffs, soil/sediment in hollows and drainage, alpine/snow up high scaled by
altitude + aspect — **coherent, not random; rich, not drab**. "Basically done" for this region.
Built with clean seams so chunks → infinite → more biomes extend it later. Holds the perf line
(target **160+ fps** in the final generator; baked-once design keeps the fragment ~flat).

## Scope (locked with the user)

- **IN:** this single region. The signal extraction + placement rules + curated palette, all in the
  **bake** (`SplatCompute` / `splat_weights.glsl`) + a data-driven palette/rules file. Reuse the
  fragment compositing (top-2 blend through `ar_sample_wp` + relief height-blend) and the existing
  detail/macro/contact/light layers.
- **OUT, later, in this order (user's roadmap):** multiple chunks → infinite/procedural → more
  biomes. The world climate/moisture/biome-selection field is later; this spec uses a *cheap local
  moisture proxy* with a clean swap-in seam.
- **NOT touching:** the base-field math, lighting/clouds, other scenes. Terrain LOD, erosion, flora
  are separate arcs (couplings noted below, not built here).

## Architecture — the foundation lives in the BAKE

All "meaning" moves into the bake; the fragment is unchanged in spirit. Baked once → fragment cost
stays flat → protects the perf target.

```
heightfield ──► [BAKE (extends SplatCompute / splat_weights.glsl)]
                  1. extract per-texel SIGNALS (altitude, slope, signed curvature,
                     aspect→sun-exposure, moisture/flow proxy, cavity/AO)
                  2. evaluate PLACEMENT RULES → a weight per surface ROLE
                  3. resolve top-2 roles → splat texture(s):
                        R=dominant role  G=secondary role  B=intra mix  A=boundary blend
                     (+ optional 2nd RGBA: aspect / moisture / cavity for contact & later units)
                                         │
                                         ▼  (sampled in fragment, filter chosen per channel)
fragment (terrain_lab.gdshader): read splat → blend the TWO role materials via ar_sample_wp
  (Unit-1 bombing) + relief height-blend → existing macro color / contact / detail(Unit 2, off)
  → custom light(). Splat path stays the default; legacy band path kept for A/B.
```

## The 7 slots become ROLES (data-driven palette)

Keep the 7 material slots (`z0..z6`, 21 samplers — **no new sampler churn**) but redefine each as a
surface **role** placed by meaning, mapped to a curated library material via a new data file
`data/ground_palette.json` (role → material name + per-role rule params), so palette and rules are
**tunable live without recompiling** (same data-driven ethos as `lab_controls.json`). Slot *count*
stays a clean knob (bump to 8 if a region needs e.g. a distinct wet/cliff-base role); **start at 7.**

Initial roles (alpine region) and candidate materials from the 108 (final picks chosen **live
against the lit result** — value + hue contrast that survives GI + AgX):

| # | Role | Candidates |
|---|------|-----------|
| 0 | meadow / grass | `m8_grass_calm`, `scrub_dense`, `02_berry_shrubs` |
| 1 | soil / dirt | `dirt`, `21_deciduous_decay`, `13_sun_baked_clay` |
| 2 | gravel / sediment | `12_wet_pebbles`, `12_dry_riverbed`, `16_glacial_till` |
| 3 | rock / bedrock | `01_weathered_grey_bedrock`, `rock_dark`, `09_mica_schist` |
| 4 | scree / talus | `02_coarse_talus`, `13_dry_loose_scree`, `wgv3_alpine_scree` |
| 5 | alpine / tundra | `m14_tundra_moss`, `17_tundra_dwarf`, `06_frost_shattered_alpine` |
| 6 | snow | `01_fresh_powder`, `03_firn_dense` |

The library is alpine/volcanic-heavy but carries enough greens / dirt / sand / moss / sediment for a
varied region — so this is a **selection + placement** fix, **no new assets** for this region.

## The signal set (computed once, at bake time)

| Signal | State | Drives |
|--------|-------|--------|
| altitude (normalized height) | have | snowline, alpine band |
| slope (`1 − n.y`) | have | rock vs grass, scree |
| curvature, **signed** (Laplacian) | refine | concave hollows (soil) vs convex ridges (exposed rock) |
| **aspect → sun-exposure** | NEW | snow/alpine on shaded faces; drier on sun faces (needs a `sun_azimuth` bake param) |
| **moisture / flow proxy** | NEW | green + sediment follow drainage. Cheap: concavity + `(1−altitude)` + flow-direction convergence. **Swap-in seam** for real erosion drainage (E2) later. |
| cavity / AO | derive from curvature | crevice dirt (feeds contact shading) |

## Placement rules (the material graph)

Each role's weight = a product/sum of shaping functions over the signals (all params live-tunable in
`ground_palette.json`). Representative rules:

- **snow:** `smoothstep(snowline ± aspect_bias·exposure)` × `(1 − steepness)` — sheds off steep and
  sun-facing slopes.
- **alpine/tundra:** altitude band just below snow, exposed, low-to-moderate slope.
- **rock/bedrock:** `smoothstep(slope)` — steep = rock; plus convex high-curvature ridges.
- **scree/talus:** convex breaks + moderate-to-steep slope + below cliffs (altitude-relative),
  accumulating at slope bases.
- **meadow/grass:** low slope + low-mid altitude + moisture + not-too-dry.
- **soil/dirt:** concave hollows + moderate moisture.
- **gravel/sediment:** flow convergence (drainage) + low slope (valley floors, channels).

**Resolve:** compute all role weights per texel, take the **top-2** (dominant + secondary) and write
`(dom, sec, mix, boundary)` exactly as the current splat format — but `secondary`/`mix` now come from
the **rules** (the 2nd-strongest role), not index-adjacency. The existing mid-scale noise that breaks
a single fill into patches is kept (it now mixes two *meaningful* roles). Top-2 (not top-3/4) keeps
the fragment cost identical to today — a deliberate perf choice; top-3 is a noted future lever.

## Compositing (fragment — minimal change)

Read the splat (now meaningful) → blend the two role materials via `ar_sample_wp` + `heightblend_w`
(as today) → existing macro color, contact/crevice (now able to consume baked cavity/aspect), detail
(Unit 2, default off) → custom `light()`. Splat stays the **default** path; the live `zone_weights`
band path remains as legacy A/B. Normal/roughness stay on the perf-pass plain branched triplanar.

## Data-driven + live toggles (bisectable, per pillars)

- **New `data/ground_palette.json`:** role → material + rule params. Loaded by `TerrainLab`;
  changing a role material or rule param **re-bakes** the splat (the bake is on-demand, not per-frame).
- **Lab controls (extend the Splat tab, or a new "Ground" tab):** per-role **material dropdowns**
  (live re-bake), **rule knobs** (snowline, slope thresholds, moisture amount, aspect bias), and a
  **master toggle** `rule-based placement` vs legacy bands. `FLAT BASELINE` already exists for full
  bisection.
- **CLI:** `--groundrules=0/1` (legacy vs new) for `--auto-shot` A/B, mirroring `--ar=`.

## Performance posture

- **Everything baked ONCE** on the GPU (extends `SplatCompute`'s local-RD + readback pattern), read
  back as the splat texture(s). **Fragment cost unchanged** (still top-2 blend) → protects 160+.
- **Baseline (this session, lab window):** clouds-off **227 fps / 4.4 ms**, clouds-on **156 fps /
  6.4 ms**. Real fullscreen res is lower (the fragment dominates: AR ~3.4 ms, mesh ~3.8 ms) — the
  mesh floor is the **terrain-LOD** arc's job, not this. This spec adds **~0** fragment cost.
- A 2nd splat RGBA (aspect/moisture/cavity) would add at most 1 extra baked texture read per fragment
  if the contact/later units need those signals live; budgeted live, only if used.

## Couplings (seams now, not built here)

- **Erosion (E2 drainage skeleton):** should later **feed** the moisture/flow + sediment rules —
  the moisture input is a swappable function, so the proxy is replaced, not reworked.
- **Climate / biome (later):** biome selection chooses *which* palette + ruleset applies per region.
  Palette + rules being data-driven **is** that seam.
- **World editing:** edits invalidate the bake → re-bake (already the splat pattern).
- **Flora (later):** the role / coverage masks should drive scatter density.
- **Arc detail units (2 distance-detail, 3 surface-depth/POM, 5 color/value tint):** still valid;
  they layer on top **after** the foundation reads good. **Unit 2 is already BUILT, default off,
  shelved** until the foundation is judged.

## Build order (this arc — each its own plan, each eye-gated)

- **G1 — Signals + rule engine in the bake.** Replace the band logic with signal extraction + the
  role rules; reuse the *current* 7 materials. **Gate:** placement is COHERENT (right material in the
  right place) in motion, *even with the drab palette* — isolates placement from palette.
- **G2 — Curated palette.** Add `ground_palette.json` + per-role dropdowns; pick contrast-rich
  materials live. **Gate:** reads photoreal, not drab.
- **G3 — Aspect + moisture/flow proxy + their rules** (snow-by-aspect, green-follows-drainage,
  sediment in channels) + cavity into contact. **Gate:** reads like real terrain following water +
  exposure.
- **Then resume** arc detail Units 2 (un-shelve) → 3 → 5 on the now-good foundation.

Each unit: build behind a toggle defaulting to the current look → `dotnet build` → headless
`--import` → `--auto-shot` A/B + `--profile` → **the user flying it live at close / mid / far**.

## Validation

Mechanical per step (build, import, auto-shot A/B at three ranges, profile ms + fps). **THE gate:**
the user flying it in motion at three ranges; `FLAT BASELINE` + per-step toggles isolate regressions.
Never judge a motion artifact from a still (project rule).

## Risk / undo

Bake-logic + palette rewrite, behind a `rule-based placement` legacy toggle; `git checkout` reverts.
Biggest risk: rules produce wrong/ugly placement — mitigated by **G1 proving coherence with the
current palette first** (placement isolated from palette), then G2 palette, then G3 advanced signals.
Perf risk low (baked once). Subjectivity risk handled by live three-range judgment per step. The
`splat_weights.glsl` `zone_weights` is duplicated in the fragment shader and kept in sync by hand —
G1 should consolidate the rule logic to one authored source to avoid drift (the bake is authoritative;
the fragment's live band path becomes legacy-only).

## NOT doing (YAGNI / boundaries)

- No chunks / streaming / infinite (later, user's order).
- No world climate/biome field yet (cheap moisture proxy + seam).
- No new material assets for this region (selection + placement fix).
- No top-3/4 material blend (perf; top-2 + good rules first).
- No virtual texturing / megatexture unless a measured need appears.
