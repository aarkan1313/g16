# Biomes & Macro Variety — Research

The user's ROADMAP already names this as the thing that makes it a *world*: "it doesn't matter if
one mountain looks good — it's not a world." Biomes are the **first provider** in the WorldGraph
order (`04`) — they drive palettes, flora species, POI types, road surfaces, cave styles. Without
them every region is the same mountain tiled; with them, regions genuinely differ.

## How biomes are generated (techniques + sources)

- **Temperature × moisture → Whittaker diagram.** The canonical model: a biome is a lookup in a
  discretized Whittaker diagram keyed by (temperature, moisture). Desert = hot+dry, rainforest =
  hot+wet, tundra = cold+dry, taiga = cold+wet, etc. A 2D lookup table → fully data-driven.
  ([AutoBiomes][1], [Vagabond][2])
- **Climate fields:**
  - *Temperature* = latitude gradient (or a base) **+ altitude lapse** (higher = colder) **+
    domain-warped noise** for natural variation (warp a linear gradient with Perlin → not banded).
    ([Vagabond][2])
  - *Moisture* = **the drainage flow_accum** (WG16 already computes this!) + distance-to-water +
    **rain-shadow** (mountains block moisture on their lee side — a function of terrain + prevailing
    wind from the Weather axis). ([climate sim][1])
- **Region partition — continuous field vs Voronoi:**
  - *Voronoi* (Polygon Map Generation / Red Blob) gives crisp regions, good for political/island
    maps, but hard borders. ([Red Blob][3])
  - *Continuous field* (sample temp+moist per point, lookup) gives smooth, organic borders — better
    for a natural infinite world. **Blend** at borders so flora/palette cross-fade. WG16 should use
    continuous fields with blended borders (matches the existing splat-blend philosophy).
- **Macro variety beyond biome type:** per-region *variants* (a "desert" can be dunes vs rocky vs
  salt-flat), warped region scale so regions aren't uniform-sized, and rare/special regions
  (volcanic, crystal) as low-probability draws — the thing that makes exploration rewarding.

## How this maps onto WG16 (the fit is excellent — most inputs already exist)

- **Tier-2 presolve, first provider:** per macro-cell, bake a **biome field** (temp + moisture +
  biome id + variant) at coarse res into the solved record. Cheap, pay-once, cached.
  - Temperature: latitude/base + altitude lapse (from cached coarse height) + domain-warped noise.
  - Moisture: **flow_accum from drainage** (already planned) + distance-to-water + rain-shadow
    (terrain + prevailing wind from Weather). This is why drainage runs *with* biomes, not after.
  - Biome id: Whittaker lookup table in `data/biomes.json` (fully data-driven).
- **Consumed by everyone:** ground palette (the existing `ground_palette.json` is *already* built for
  per-region palette selection — ROADMAP says so), flora species mix, POI types, road surface, cave
  style. Biome is the single field that fans out into every other module's data choices.
- **Blended borders:** sample the biome field with a blend radius so palettes/flora cross-fade across
  region boundaries (no hard seam) — reuse the splat-blend approach.

## Macro variety = data + warping (not more code)

- **Whittaker table** (data) defines biome types. **Variant tables** per biome (data) define
  sub-looks. **Domain-warp scale** (data) controls region size variation. **Rare-region weights**
  (data) sprinkle special biomes. All hot-reloadable; a game ships its own biome set. The engine
  just samples temp+moisture and looks up.

## The connective role (why biomes are the keystone's first tenant)

Provider order: `biome → drainage → roads → POIs`, with flora reading biome. Biome must be first
because everything downstream branches on it: drainage style (arid vs wet), road surface (sand vs
stone), POI types (oasis vs ruin), flora species, cave look, even weather presets per region. It's
the **macro-cell's identity**. This is the connective tissue the user means by "regions genuinely
differ" — one field, consumed everywhere, all data-driven.

## Risks / open questions
- **Border blending vs sharp features** — most borders should blend, but some want to be sharp (a
  coastline, a treeline, a desert edge) — needs a per-border-type blend width (data).
- **Temperature/altitude coupling** — a tall mountain in a hot biome should get a snowy cap; the
  lapse rate must be strong enough (this also feeds snow-on-ground, Phase C).
- **Determinism across macro-cells** — the biome field must be continuous across cell borders (the
  skirt-stitch protocol, `03`) so a forest doesn't cut off at a tile edge.
- **Coupling with weather** — rain-shadow needs prevailing wind; weather presets may be per-biome.
  Coordinate provider inputs (Weather axis feeds biome moisture; biome feeds weather presets).

## Recommendation
Build biomes **early in Phase B, right after the WorldGraph + integration layer + drainage exist** —
because flora/POIs/roads all need the biome field to be interesting. A minimal version (temp+moisture
+ Whittaker + palette swap) proves the field + the consumer wiring; variants/rare-regions/sharp
borders are polish after. This is low-risk, high-leverage, and most of its inputs (coarse height,
flow_accum, ground palette system) already exist.

## Sources
[1]: https://link.springer.com/article/10.1007/s00371-020-01920-7
[2]: https://pvigier.github.io/2019/05/12/vagabond-map-generation.html
[3]: http://www-cs-students.stanford.edu/~amitp/game-programming/polygon-map-generation/
