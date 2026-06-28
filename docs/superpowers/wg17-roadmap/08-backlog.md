# WG17 — Backlog & Future (not yet specced)

Modules/features the migration audit + AAA dossier identify as next, after the currently-staged slices ship.
None are specced yet; each gets its own spec → plan → kickoff when its turn comes.

## Near-term migration targets (from the WG16 audit)

- **Water / Erosion / Hydrology** — the cleanest WG16 module (21/24 files pure `Erosion.Core`). Reuses the
  `IHeightSource` seam already built in the terrain slice (one knot to sever: `RegionWaterSolver` → inject
  `IHeightSource` instead of `FieldCompute`). Offline per-region baked-delta pipeline: breach→erode→breach→
  hydrology→carve → a delta texture the terrain shader samples. (See migration audit §3.)
- **Material Board / Workbench** — the standalone material-judging tools (fully decoupled in WG16). Useful for
  curating the surfacing palette. Small, independent.

## World-cohesion features (from the AAA engine dossier, `docs/superpowers/research-2026-06-28-aaa-engine/`)

These are the "make it a believable world" layer — design-ahead only in WG16, nothing built. Hybrid world
architecture: infinite procedural + a presolved WorldGraph of macro-cells; integration layer (biome →
drainage → roads → POIs) via a solved record + masks; under-8ms budget via pay-once/cache/presolve.

- **Biomes** — whole-region material/feature sets; the surfacing rules are already biome-READY (optional input).
- **Trees / vegetation** — scatter + LOD over the infinite terrain.
- **Caves / underground**.
- **Roads / POIs** — derived from the drainage + biome layers.
- **Dynamic weather** — there's a separate WG16 weather-lab spec (`weather-lab-project` memory); could port.

## Deferred within shipped/staged slices

- **Terrain Slice 4 (deep optimization)** — terrain shipped at 4.2ms with headroom; a dedicated hotspot pass
  (identity-keyed pool already in; birth-budget vs cache, ring/lookahead) only if profiling later demands it.
- **Shadows (deliberate)** — terrain is shadowless by design; a world-anchored heightfield-march shadow owner,
  registered through `ShadowRegistry` (≤1 owner), is its own future slice. Never re-enable engine CSM default-on.
- **Clouds: vertical decks** — flat-slab → real vertical distribution (its own project).
- **Control surface: per-module panels** — added incrementally as each module integrates (that's the modularity).
- **Product settings menu** — the config model is designed to later back a player-facing settings UI.

## How to pick the next one

Prefer: (1) finish executing the staged slices (B/C/D, surfacing, control surface) before opening new design
fronts; (2) then Water (highest-value, seam already exists); (3) then world-cohesion per the dossier order
(biomes first, since other features derive from it). Quality = performance = AAA-ish (the pillar) governs all.
