# POIs & Integration Layer — Spec (design-ahead, build gated)

Two deliverables: **(A) the integration/constraint layer** (the WorldGraph provider contract +
solved record + footprint masks — the connective tissue every subsystem uses), and **(B) the POI
system** (its first consumer). (A) is the Phase-B keystone; build it first, POIs prove it. Budget
~0.4 ms med-q for POIs (`02`); the integration layer is presolve/cache (off the per-frame budget).

## Part A — the integration layer (build as part of the WorldGraph)

### A1 · Solved record + provider contract
- Define `WorldGraph.Cell.SolvedRecord` = { coarseHeight, biomeField, flowAccum/channelMask/
  waterLevel, roadGraph, poiSlots, footprintMasks }. Each provider (`IWorldProvider`) declares the
  field it writes + its dependencies; the WorldGraph runs them in dependency order per macro-cell.
- **Provider order (fixed):** `biome → drainage → roads → POIs`, with `flora` reading all masks and
  `caves` (POI-anchored) after POIs.
- **Gate:** `--providercheck` — providers run in order, each reads prior outputs, disabled providers
  skip cleanly; record round-trips through the cache byte-identical.

### A2 · Footprint masks (the cooperation primitive)
- A small set of per-chunk masks written during bake: `roadMask`, `waterMask`, `poiFootprint`,
  `caveKeepout`, `clearingMask`. Each subsystem reads the masks it cares about (flora clears
  roadMask+poiFootprint; caves honor caveKeepout; terrain stamps poiFootprint).
- **Gate:** flip a single POI → roads route to it, flora clears it, water avoids it, terrain grades
  to it — all from masks, no per-pair glue (eye-gate the cooperation).

### A3 · Conflict policy
- Slot contention, unreachable POI, biome-forbidden required POI → resolved by provider order +
  per-item `priority` + a documented fallback (drop lowest-priority / relocate / spawn connector).
- **Gate:** stress a dense macro-cell; no crashes, no orphaned/overlapping POIs, deterministic.

## Part B — the POI system (first consumer of A)

### B1 · Procedural POI placement
- Per macro-cell: blue-noise candidate slots → filter by `poi_types.json` rules (biome/slope/
  water-distance/spacing) → assign types → reserve. Deterministic from seed; cached.
- **Gate:** POIs scatter naturally (well-spaced, rule-respecting), distinct per biome.

### B2 · Procedural POI instancing + terrain stamp
- Near camera, spawn from prefab/rule set (tier-3); stamp/grade terrain under footprint on chunk
  birth (reuse road cut/fill); blend edges. WFC/model-synthesis for intra-POI layout (optional, per
  type).
- **Gate:** a procedural POI reads intentional + sits in the terrain (not floating/clipping), seam OK.

### B3 · Handcrafted POIs (authored)
- An in-engine **POI lab** to author + register prefab POIs (scene + footprint + stamp profile +
  placement rule); claim specific slots or hand-placed coords. Horizon-style: authored structure +
  procedural decoration around it.
- **Gate:** an authored POI stamps in cleanly + its surroundings (flora/roads) react correctly.

### B4 · POIs-as-other-modules
- A POI type can reference another module's authored content: a dungeon (caves), a sacred lake
  (water), a ruined highway (roads), an ancient grove (flora). The POI just reserves the slot +
  triggers that module's authored path.
- **Gate:** at least one cross-module POI (e.g. a cave-entrance POI) works end to end.

## Data contract (game-agnostic)
- `poi_types.json` (objectlist) — id, kind (proc/handcrafted/cross-module), footprint, placement
  rules, prefab/scene ref, road-link priority, stamp profile, intra-layout method.
- `pois` module toggle (`04`); quality tier scales POI density + instance/impostor distance.

## Dependencies & integration
- **A is depended on by everything** (roads/flora/caves/water all read the solved record + masks).
- **B reads:** biome, drainage, roads (anchors), masks. **B writes:** poiFootprint + slots.
- **Coordinates with:** all modules via masks; the WorldDelta for placed/destroyed POIs.

## Performance plan (`02`)
- Placement = presolved/cached (off per-frame budget). Instancing = tier-3 streamed + impostors.
  Authored POIs = static scenes streamed. Terrain stamp = pay-once on birth. Throttle spawns.
- Serial parts (constraint solve, WFC) → C# first; Rust only if measured.

## STOP criterion
Stop after A1+A2: the provider contract + masks demonstrably make a single POI cooperate with roads/
flora/water/terrain. That cooperation IS the deliverable — POI richness (B3/B4) comes after the
contract is eye-gated. Build A before any other Phase-B content subsystem (it's the keystone in `00`).
