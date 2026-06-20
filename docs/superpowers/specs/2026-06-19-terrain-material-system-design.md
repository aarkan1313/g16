# Terrain Material System — Design (the real build, not a framework)

Date: 2026-06-19 · Status: design approved (user, live) — awaiting spec review + plan · Lane: surface/look
(material-evaluation pipeline in `terrain_lab.gdshader` + the splat bake)

> **Why this supersedes the piecemeal approach.** The ground was "only frameworked — no real work
> done" (user, live, 2026-06-19). The existing roadmap (ground-presentation arc + foundation reorder)
> planned *most* layers as separate units, but it **never had a plan for the one layer that's most
> visibly broken: blend/compositing quality.** Every prior plan assumed the splat framework blends
> fine — it doesn't. Result: **blocky** material boundaries (nearest dom/sec on a grid) and a **flat,
> painted** surface (no relief). This spec builds the material pipeline as a *real, coherent system*,
> fills the blend-quality gap, and sequences the rest.

## The problem, seen live (2026-06-19)
- **Blocky splat:** material regions are axis-aligned/stair-stepped patches. Cause: the bake emits
  per-texel dominant/secondary **indices** sampled `filter_nearest`, blended by a roughness-*proxy*
  heightblend hack. Boundaries snap to the 4 m splat grid + the runner-up role flips between texels.
- **Flat surface:** materials read like a painted texture — **no parallax/relief**, normals barely
  driving the BRDF.
- Symptom verdict: it's "small tunes on a framework," not a built system.

## The proper AAA setup (validated against the pillars)
A real-time **weight-blended material pipeline** — the standard AAA terrain approach (UE Landscape /
Unity HDRP terrain). Confirmed with the user as the correct pillars/AAA/performant/best-long-term
setup *for what we build now*, with two higher-ceiling upgrades explicitly **designed-for via seams,
not skipped** (see "Ceiling seams").

### The layered pipeline (clean seams, each a bounded unit)
```
material sample  → placement field → BLEND / COMPOSITING → relief → breakup → macro → light()
(triplanar +       (bake: per-texel  (smooth weights +      (parallax  (slope/   (value/
 anti-tile +        material WEIGHTS  height-map interlock + occlusion  curv/     hue
 near/far detail)   from signals)     organic breakup)      + normals) cavity)    break)
```

| Layer | What a real system does | Today | This spec |
|---|---|---|---|
| Material sample | per-material triplanar + anti-tile + near/far detail → `{alb,nrm,rgh,ao,height}` | partial (no height) | add a **height channel** (derived first — see seams) |
| Placement field (bake) | per-texel material **weights** from terrain signals | G1 emits blocky indices | evolve to **smooth weights** (de-block at the source) |
| **Blend / compositing** | smooth weight blend + **height-map interlocking** + organic breakup | **hollow (the gap)** | **BUILD — the core** |
| Surface relief | **parallax-occlusion** + real normals → depth | missing | **BUILD — the core** (LOD-gated near) |
| Procedural breakup | slope/curv/cavity masks → dirt-in-crevices, exposed edges, wear | crude crevice darken | Unit 4 (existing plan), after core |
| Macro variation | large-scale value/hue break surviving GI+AgX | weak | Unit 5 (existing plan), after core |

## Build order (compositing-core first — user-approved)
1. **Blend/compositing quality** (the gap) + **Surface relief** — the two visibly-broken layers; the
   biggest "it looks built" payoff. Placement (G1) feeds it.
2. **Procedural breakup** (Unit 4 plan) — adds dirt/rock-edge variety on top.
3. **Macro color/value** (Unit 5 plan) — final richness, GI+AgX-validated.
Each eye-gated live at close/mid/far; behind toggles defaulting to the current look; perf-profiled.

## Blend/compositing — how (the core technique)
- **Smooth weights, not nearest indices.** De-block at the source: the placement read should produce
  smoothly-varying per-material weights (e.g. bake higher-frequency/dithered weights, or sample +
  blend so boundaries aren't grid-locked) + a world-space breakup so edges are organic. The
  `splat_warp` knob added this session is a stopgap inside this layer, not the system.
- **Height-map interlocking** (the real heightblend): the material whose **height** is greater wins
  per-pixel within a soft band → rock pokes through gravel, gravel settles into grass — organic,
  relief-driven boundaries (replaces the roughness-proxy hack). Needs a per-material height channel.
- **Top-2 materials per pixel** (perf): dominant + secondary, blended by height+weight. Top-3+ is a
  later lever only if needed.

## Surface relief — how
- **Parallax-occlusion mapping** from the material height channel → real apparent depth. **LOD-gated
  to the near band** (`distanceWeight`) — POM is the one expensive part; off at distance.
- Ensure normal + roughness + AO genuinely drive the custom `light()` BRDF.

## Ceiling seams (more-proper long-term — designed-for, deferred, NOT skipped)
1. **Real height/displacement maps (quality ceiling).** Proper interlock + POM want authored/generated
   height maps; the library has albedo/normal/roughness/AO only. **Start by deriving height**
   (albedo-luminance / inverted-roughness); the pipeline is identical, quality scales when real maps
   land. Decision deferred to the user.
2. **Virtual-texture / RVT-style caching (perf ceiling for infinite world).** The top tier bakes the
   blended material into a cached texture so per-pixel cost decouples from blend complexity — *the*
   answer to "infinite world at 8 ms with rich materials." It caches the output of **exactly this
   pipeline**, and needs the chunk system (absent). Correct order: build the pipeline now (needed
   either way), leave seams, add RVT when chunking lands. Building it first would be backwards.

## Performance posture (hard target: 8 ms total, world generator, long-term)
- **Top-2 blend**, **POM LOD-gated near only**, **placement + breakup masks baked once** (compute, not
  per-frame). These are design rules, not afterthoughts.
- Profile **in motion** (`--profmove`) — static profiling hides SDFGI/shadow/cloud motion cost.
- The terrain mesh floor + SDFGI-in-motion are separate arcs (CDLOD; SDFGI config); the GI/shadow
  **proxy** (default 512², built this session) already recovered ~2× flying fps.

## Scope (user's roadmap)
Make **this region** look genuinely built FIRST → multiple chunks → infinite/procedural → more biomes
last. The climate/biome field selects palette+ruleset later; this spec uses one alpine ruleset.

## What's done / reused (don't rebuild)
- **G1 placement engine** (rule-based, signal-driven) — APPROVED ("basics work"). Feeds the blend.
- **Unit 1 anti-repetition** — done/approved. **Unit 2 distance-detail** — built, shelved (re-enable
  inside the material-sample layer once the core is good).
- **Existing unit plans** `ground-unit{3,4,5}` cover relief / breakup / color — reuse as the layer
  plans (Unit 3 = relief, pulled into the core).
- **GI/shadow proxy** (perf, default 512²) — keep.

## Risk / undo
Large material-section rewrite, but Presenter-only (base field, lighting, clouds untouched), layered +
toggleable, `git checkout` reverts. Biggest risk: perf (mitigated by top-2 + LOD-gated POM + baked
masks + in-motion profiling) and subjectivity (mitigated by live three-range eye-gating per layer).

## NOT doing now (YAGNI / boundaries)
- No RVT/virtual-texturing yet (seam only). No real height maps yet (derive). No chunks/infinite/biomes
  (later). No top-3+ blend. Not touching base field / lighting / clouds / god rays (other chat).
