# 00 · Master Roadmap — WG16 toward a best-in-class, game-agnostic AAA engine

The capstone of this dossier. It sequences every subsystem into the **existing Phase A→B→C** frame
and the discipline rule, with explicit dependency ordering and the 8 ms budget held throughout. This
is **design-ahead, not build-ahead** — it adds a spec/roadmap line per system, exactly the thing the
discipline rule blesses; *building* still happens one phase past the last passed eye-gate.

> Read order: `01` (where we are) → `03` (the world-architecture decision that gates everything) →
> `02` (how it stays under 8 ms) → `04` (how modules plug in) → per-subsystem `research.md`+`spec.md`
> → this roadmap. Pillars unchanged: **quality = performance = AAA-ish = best-long-term.**

## The one decision that shapes the rest (from `03`)

Build **Option C — hybrid**: infinite pure-function terrain + a coarse **`WorldGraph`** of macro-cells
that lazily presolve + cache the *global* systems (drainage, biomes, roads, POIs) with deterministic
seed-regeneration and a sparse `WorldDelta` edit store. It's a superset: degenerates to bounded
(Factorio) at world-size cells, or to pure-infinite with presolves off. Per-module bounded fallback
only if a specific system can't hold quality+budget infinite. **The `WorldGraph` + integration layer
is the new Phase-B keystone, alongside the already-shipped CDLOD spine.**

## The dependency graph (why the order is what it is)

```
                        ┌──────────────────── Performance reclaim (`02`) ───────────────────┐
                        │  (SSAO cut, cloudsteps, analytic normals, far-cascade casters)     │
PHASE A (finish region) │  ground surfacing reset · erosion+hydrology (drainage substrate)   │
                        └───────────────────────────────────────────────────────────────────┘
                                                      │
PHASE B keystone:        WorldGraph (macro-cell presolve + cache) + Module framework (`04`)
                                                      │
            ┌───────────── integration layer (provider order + solved record + masks) ──────────────┐
            │                                                                                          │
   biomes ──┴──> drainage(moisture) ──> roads ──> POIs ──> POI-anchored caves                         │
      │                                    │         │                                                 │
      └──> flora (reads biome+masks)       │         └──> handcrafted POIs                             │
   wind field (weather W1) ───────────────┘ (rain-shadow, road exposure, flora sway)                  │
   ambient caves (pure fn, independent) ──────────────────────────────────────────────────────────────┘
                                                      │
PHASE C (climate/elements): visible water render · dynamic weather W2 · precipitation W3 ·
                            wetness W4 · snow-on-ground W5
```

Biome is first (everything branches on it). Drainage couples with biome (moisture) — two-step solve.
Roads need POIs (endpoints) + drainage (avoid water). POIs need the integration layer + everything
upstream. Flora reads biome + masks. Ambient caves are independent (pure function); POI-caves need
POIs. Wind (weather) is pulled early because flora + biomes need it.

## Phase A — finish the region (mostly in flight; add the perf reclaim)

Per existing ROADMAP, unchanged in intent. This dossier adds one explicit item:
- **A0 · Performance reclaim (`02`)** — cut SSAO (−1.8), cloudsteps=32 (−1.0), then the architectural
  levers (analytic normals, adaptive far grid, far-cascade caster cull). Target ~6 ms features-off on
  5090 → opens the ~2 ms headroom Phase B/C modules spend. Do this **before** stacking new modules.
- **A1 · Ground surfacing reset** (existing) — per-pixel procedural placement + texture arrays.
- **A2 · Erosion + hydrology** (existing) — the **drainage substrate** (carved height + flow_accum +
  channel_mask + water_level + sediment). *This is the seed of the WorldGraph solved record* — design
  it so its per-region solve becomes a WorldGraph provider in Phase B (don't build it region-locked).

**Gate to leave Phase A:** the single-region look is *done* (ground + erosion + static water read
AAA) AND the frame is at/under budget. Then the priority pivots hard to scale (the user's law).

## Phase B — make it a WORLD (the keystone + the content systems)

**B-keystone (build first, everything depends on it):**
- **BK1 · WorldGraph** (`03`,`04`) — macro-cell grid + presolve scheduler (velocity-predictive,
  render-thread-friendly like `ChunkFieldCache`) + disk cache (seed-deterministic regen) + `WorldDelta`.
- **BK2 · Module + integration framework** (`04`, `pois-integration` Part A) — `IWorldModule` /
  `IWorldProvider` contract, solved record, **footprint masks**, provider dependency order, conflict
  policy, the World/Modules lab tab + quality-tier axis. **Eye-gate: one POI cooperating with terrain/
  roads/flora/water via masks alone.**
- **BK3 · ScatterStreamer** (`04`, `trees-flora`) — the shared tier-3 instance/impostor streamer
  (flora, POIs, road meshes all use it). LOD + impostors + birth-throttle contract.

**B-content (each a provider/module on the keystone; build in dependency order, each eye-gated):**
1. **Biomes** (`biomes` BIO1–2) — first provider; proves the fan-out via the ground palette.
2. **Drainage-as-provider** — port the Phase-A hydrology solve into a WorldGraph provider (E2/E3 in
   existing ROADMAP); biome moisture closes the two-step coupling.
3. **Flora** (`trees-flora` F1–F4) — biome-placed, impostor-LOD'd forests + grass. The "forest gate."
4. **Roads** (`roads-paths` R1–R3) — contiguous, conformed, class-adaptive; needs POIs for endpoints
   (so POI placement B1 lands just before/with roads).
5. **POIs** (`pois-integration` B1–B4) — placement + procedural + handcrafted + cross-module.
6. **Caves** (`caves-underground` C1–C3) — dig+DC mesher first (C1), then ambient (C2), then
   POI-anchored (C3). Highest-risk; most likely per-module bounded fallback.
7. **Wind** (`weather` W1) + **dynamic coverage** (W2) — pulled in because flora/biomes consume wind.
8. **Macro variety** (BIO3–4) — variants, rare regions, sharp borders — polish after the fan-out.

## Phase C — climate & elements (consumes Phase A/B)

- **Visible water rendering** (existing) — rivers/lakes/ocean from the drainage substrate.
- **Dynamic weather** (`weather` W2 finished) → **precipitation** (W3) → **wetness** (W4, the hard
  transition) → **snow-on-ground** (W5, with BIO4 altitude caps).
- **Underground lighting/atmosphere** (`caves` C4) — revive the parked GI proxy for cave bounce.

## How the 8 ms holds (the running budget, from `02`)

| Stage | Modules on | 5090 budget |
|---|---|---|
| End of Phase A (post-A0) | terrain+sky+ground+water, features off | ~6.0 ms |
| Phase B steady | + biomes(presolve) + flora + roads + POIs | ~7.5 ms |
| Phase C all-on | + caves(underground only) + dynamic weather/precip/wetness | ~8.0 ms |

Held because the global systems are **presolved + cached** (off per-frame), placement/meshes are
**pay-once on birth**, far content is **impostors**, and heavy modules are **default-off / gated by
locale** (caves underground-only). Quality tiers scale the target to mid-range. The boss fight is
**spikes** (birth-time work) — every module obeys the throttle+lookahead birth contract.

## Discipline (unchanged, restated for this expansion)

- Build at most ONE phase past the last passed eye-gate. This roadmap is the *design-ahead* that's
  encouraged; do not build a Phase-B content system before BK1–BK3 (the keystone) is gated.
- Every new module ships **default-off behind a registry toggle**, defaulting to the approved look,
  until its own eye-gate passes. Thin docs: a ROADMAP line + a DECISIONS entry per settled choice.
- Each subsystem has a **STOP criterion** in its spec — honor it; don't open depth past the first pass.
- The user's eye is the only look-gate; mechanical `--*check` gates correctness/cost; never judge a
  motion artifact from a still; always `--profmove`.

## Immediate next actions (when the user is ready to build)

1. **A0 perf reclaim** (cheap, measured, unblocks headroom) — and it can start now, it's behind toggles.
2. **Design-lock the drainage substrate as a future WorldGraph provider** (so Phase-A erosion isn't
   region-locked) — a small design constraint added to the existing erosion arc.
3. When Phase A's region look passes its eye-gate → **build BK1 WorldGraph** as the first Phase-B move.

Everything else is specced and sequenced above; pick the lane, build one phase, eye-gate, repeat.
