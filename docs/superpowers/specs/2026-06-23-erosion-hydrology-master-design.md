# Erosion + Hydrology — Master System Design (the roadmap we follow)

Date: 2026-06-23. Status: MASTER SPEC (brainstormed; pillars-driven, user-delegated). Lane: terrain shape + water.
This is the DURABLE ROADMAP for erosion and water together. It supersedes the narrow E1-only framing of
`2026-06-22-erosion-e1-sim-core-design.md` (E1's pipe-model code is KEPT and folds in as Arc-1's starting point).
Per the user: "as long as we have a proper roadmap and we stick with it." This is that roadmap.

## The one idea everything follows from

**Erosion and water are not two systems — they are one drainage substrate consumed at two runtime tiers.**

- **Erosion** computes WHERE water goes and how it carves: the **drainage substrate** = carved height +
  flow-accumulation + channel mask + lake/basin map. This is the single shared bridge.
- **Static water** (always-on, infinite, cheap) RENDERS rivers/lakes/streams as a surface from that substrate.
- **Live water** (optional, toggled, bounded near camera) runs a real GPU shallow-water sim SEEDED by and
  BOUNDED to the same channels, relaxing back to the static state when off.

Both water tiers read the same substrate, so they always agree with the terrain and with each other. Live is
feasible precisely because it is camera-local, not global. The substrate is the contract; the tiers are layers.

## Pillars + the guardrail

- **AAA + best-long-term, regardless of cost.** Lead with the most-correct model (real stream-power drainage,
  GPU shallow-water), not the easy one.
- **The graveyard discipline (erosion killed WG1–15).** Prove each arc GREAT before the next; one coherent
  coupled model (NOT a stack of fighting solvers); base-field bones untouched; eye-gated in motion; if an arc
  can't be made great after fair effort, STOP and surface it — don't grind versions.
- **Modular + toggleable.** Static water works with live OFF; live is a layer ON TOP, not a rewrite. Every
  arc is behind a toggle defaulting to the safe state.
- **Design the seams now, build in order.** This master spec fixes the substrate data contract + the arc
  interfaces up front so later arcs are wiring, not re-architecture. Each arc still gets its own spec→plan→
  build→eye-gate.

## The shared DRAINAGE SUBSTRATE (the contract — fixed now, so it never churns)

The substrate is the set of per-cell fields the erosion sim outputs and BOTH water tiers + later arcs consume.
Defined once here; every arc reads/writes only through it.

| Field | Meaning | Producer | Consumers |
|-------|---------|----------|-----------|
| `height` (carved) | bedrock after erosion (base field + erosion delta) | Arc 1 | terrain display, both water tiers, collision |
| `flow_accum` | upstream drainage area per cell (how much water passes here) | Arc 1 | channel mask, river width, live-water seeding, biome moisture, flora |
| `channel_mask` | is this cell a river/stream (thresholded flow_accum) + which order | Arc 1 | static water mesh, live-water bounds |
| `water_level` | standing-water surface elevation (lakes/basins; ≥ height where wet) | Arc 1 | static lake/river surface, live-water rest state |
| `sediment` / `material` | deposited material (banks, alluvium) — also feeds surfacing/biomes later | Arc 1 | banks, ground material blend, flora |

**Storage:** coarse, low-frequency → small on disk (the bake, Arc-1 phase 2). The detail (gully texture, bank
roughness) is synthesized per-chunk at runtime conditioned on the coarse substrate (the bake+synth split the
parent erosion arc already chose — MBs not TBs, infinite-friendly). The S3 chunk contract already RESERVES the
"carvable height delta" slot for this; the substrate fills it.

## The three arcs (decomposition + build order)

### ARC 1 — Erosion + Drainage Substrate  ← BUILD FIRST (folds in the existing E1)
**Delivers:** the coupled GPU erosion sim that carves convincing valleys/rivers AND outputs the full drainage
substrate above. This is E1 evolved: keep the race-free pipe-model coupled loop already built, but **fix the
river-forming gap** — the current erosion scales with LOCAL water depth/velocity, which gives diffuse hillslope
erosion, NOT dendritic rivers. Real dendritic networks need erosion to scale with **accumulated upstream
drainage area** (stream-power E ∝ Aᵐ·Sⁿ). So Arc 1 adds a **flow-accumulation pass** (each cell sums upstream
flow) and feeds it into the incision law. That positive feedback (big flow → deeper channel → captures more
flow) is what makes branching rivers. Plus lake/basin detection (fill depressions → `water_level`) and the
channel mask.
- **Sub-phases:** (1) sim core + substrate, live lab, judged great in motion [the current E1 lab, evolved];
  (2) bake the coarse substrate small; (3) per-chunk detail synthesis on the S3 chunk system.
- **Gate:** user watches it carve LIVE — dendritic valleys + rivers that drain logically, converge, tunable;
  the `flow_accum`/`channel_mask` debug views show real branching networks (not uniform speckle).
- **Keeps:** `shaders/erosion_sim.glsl` + `scripts/erosion/*` + `scenes/erosion_lab.tscn` (E1), extended.

### ARC 2 — Static Water (always-on render layer)  ← BUILD SECOND
**Delivers:** rivers, streams, and lakes RENDERED from the substrate — a water surface mesh/shader sitting in
the carved channels and basins, with width/depth from `flow_accum`/`channel_mask`/`water_level`. Flow direction
for the shader from the drainage. Cheap, infinite (per-chunk like terrain), always matches the valleys. This is
the water you see 99% of the time.
- **Gate:** rivers sit IN the carved valleys (not floating/clipping), lakes fill basins to a flat level,
  streams taper by order, reads good in motion at close/mid/far.
- **Reserves the live seam:** the water surface is driven by the substrate so the live tier can later override
  a bounded region's surface without the static tier knowing.

### ARC 3 — Live Water (optional bounded GPU sim)  ← BUILD LAST, TOGGLEABLE
**Delivers:** a real-time GPU shallow-water sim (pipe-model — the SAME family as the erosion core, reused) on a
**camera-local bounded patch**, seeded from the substrate's channels + `water_level`, that flows/ripples/
responds dynamically and **relaxes back to the static state at its boundary and when toggled off**. Default
OFF; a toggle/region enables it. Feasible because it's bounded (a fixed-size patch around the camera), not
global — modern GPU shallow-water does this at interactive rates.
- **Gate:** live patch flows convincingly, seamlessly matches the static water at its border (no visible
  seam/pop), toggles on/off cleanly, holds frame budget. If GPU perf can't make a useful patch interactive →
  that's a real stop for Arc 3 only (Arcs 1–2 still ship the full static system).
- **Hard boundary:** live water NEVER feeds back into the baked substrate (no runtime re-erosion); it's a
  visual/gameplay layer reading the substrate, so it can't desync the world or break streaming.

## Why this order (de-risk the graveyard, value early)

1. Arc 1 first: erosion is the graveyard + the substrate everything depends on. No point building water on a
   bad drainage network. Prove the carve is great before anything consumes it.
2. Arc 2 second: static water is the big visible payoff and ships the complete usable water system; it validates
   the substrate contract by consuming it for real.
3. Arc 3 last: live water is the cool-but-risky cherry; it's optional and bounded, so it can slip or be cut
   without losing the shipped static system. Building it last means the substrate + static seam are already
   proven, so live is a contained addition.

## Relationship to existing work / docs

- **Supersedes** the E1-only scope of `2026-06-22-erosion-e1-sim-core-design.md` (E1 becomes Arc 1's first
  sub-phase; its pipe-model code is kept + extended with flow-accumulation). **Keeps** the parent erosion arc
  `2026-06-17-erosion-arc-design.md`'s bake+synth split + anti-WG15 discipline (now generalized to the substrate).
- Plugs into the S3 chunk contract (the reserved carvable-height slot = the substrate's per-chunk delta).
- Surfacing (the full ground-material arc) consumes `sediment`/`material` + wetness from the substrate later —
  noted as a seam, not built here.
- Biomes consume `flow_accum` (moisture) later — seam noted.

## NOT doing (boundaries)

- NOT building all three arcs now — this is the ROADMAP; each arc is its own spec→plan→build→eye-gate.
- NOT a global live fluid sim (Arc 3 is bounded/camera-local by design).
- NOT live water feeding back into erosion/the baked world (one-way: substrate → water).
- NOT touching the base-field generation math (bones; settled).
- NOT the chunk/streaming system (built — S3); arcs plug into it.

## Build sequence (the roadmap to stick to)

Arc 1 (erosion + substrate: sim→bake→synth) → Arc 2 (static water render) → Arc 3 (live water, toggle).
Each: own spec → plan → build → user eye-gate in motion. STOP criterion in force per arc. Start = Arc 1
sub-phase 1 (evolve the existing E1 lab: add flow-accumulation so rivers actually form, then re-eye-gate).

## Self-review notes

- **Scope:** a master/decomposition spec (correct for a multi-subsystem system). Three arcs, each its own
  future spec. The substrate contract is the one thing fully fixed here (so it doesn't churn).
- **Placeholders:** none — every arc has a deliverable, gate, and the substrate fields are concretely named
  with producer/consumer. The river-forming fix (flow-accumulation stream-power) is named, not vague.
- **Consistency:** "one substrate, two water tiers" is honored in every arc; static-is-floor / live-is-bounded
  -layer is consistent; bake+synth split carried from the parent arc; bones untouched throughout.
- **Ambiguity resolved:** "both" water = static always-on + live optional-bounded on a SHARED substrate (not
  two parallel systems); erosion's river gap = add flow-accumulation stream-power (not just tuning); live never
  re-bakes the world (one-way). Build order = substrate → static → live, de-risking the graveyard first.
