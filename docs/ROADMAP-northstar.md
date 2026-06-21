# Infinite Procedural World Generator — WG16 Roadmap (status-audited, trimmed)

**A north star, not a backlog.** This maps the genre's feature surface and marks what WG16 has actually
built. Use it to *pick the next pillar deliberately* — never as a top-to-bottom march (that's how WG1–15 died).

**Legend:** `[x]` built · `[~]` partial / behind a toggle / in the old path · `[ ]` not started ·
`[✗]` evaluated & rejected · **⚠** terrain-LOD / infinite-chunk *graveyard* (own roadmap only).

**Pillars:** quality = performance = AAA-ish = best-long-term, regardless of time cost; lead with the
most-correct option; one focused pillar at a time; build behind a toggle, judge by eye, never big-bang.

**Where WG16 is (2026-06-21):** the **sky/atmosphere pillar is built and AAA**; the **ground material
reset (ground-v2) just landed** (behind a toggle, awaiting the parity eye-gate); the **5-layer heightfield
base is proven** ("the bones"). Everything else is north-star.

**⚠ GUARDRAIL:** terrain LOD / clipmap / infinite-chunk streaming killed WG1–15. The current single-region
no-LOD mesh is a deliberate, owned trade-off (structurally over the 8 ms budget). Touch that block ONLY with
a full terrain roadmap that starts from *why the clipmap failed*; never unilaterally add a clipmap.

**Next sequence (one pillar at a time, each its own spec → plan → eye-gate):**
1. **Finish the ground reset** — pass the parity gate, then Unit 5 surface depth, Unit 6 lighting-under-sky.
2. **Biomes / climate** — biggest unbuilt multiplier; the ground is already biome-ready (a manifest = a biome).
3. **Erosion / hydrology** — reshapes the bones (carves valleys + rivers); its own arc.
4. **Scatter framework** — the reusable backbone for flora/rocks/props/objects, once biomes give it rules.

---

## Terrain Generation — the base "bones" (5-layer GPU heightfield)

- [x] Layered noise (continent → uplift → hills → ridges → macro), domain-warped
- [x] Continental / regional / local scales; mountains, ranges, rolling hills, ridges
- [x] Height / slope / curvature / aspect derivation
- [~] Emergent cliffs & valleys (slope/ridge-driven, not carved); plains; noise masks
- [ ] Authored landforms: plateaus/mesas, canyons, ravines, basins, craters, dunes, terraces, coastal, ocean floor
- [ ] **Erosion (own arc):** thermal + hydraulic — carves valleys/gullies; consumes & reshapes the heightfield
- [ ] Terrain smoothing, structure-flattening, runtime deformation hooks, authored override zones

## Terrain Materials & Surface — the just-built ground-v2 core (awaiting parity eye-gate)

- [x] Height/slope per-pixel placement + top-4 height-aware blend (no baked splat → no 4 m facets)
- [x] Triplanar; texture arrays; macro/micro variation; histogram anti-tiling
- [~] Biome-based blending (manifest is biome-ready; not wired to a biome field yet)
- [~] Surface depth — POM in the old path; ground-v2 **Unit 5** next (real relief)
- [~] Snow placed by elevation band (not dynamic accumulation)
- [ ] Moisture blending; wetness / mud / puddles; sand/moss accumulation; burned; leaf-litter; road masks; decals
- [✗] Runtime virtual texturing — rejected as a Phase-B cache layer, not the foundation

## Biomes & Climate — the next big multiplier (ground is biome-ready)

- [~] Biome = a reusable manifest (data-driven material set + placement rules) — infra exists
- [ ] Biome placement by temperature / moisture / elevation / latitude / slope / water-dist / geology
- [ ] Biome structure: primary / sub / micro, transitions, hard+soft borders, rarity, size, overrides, registration API
- [ ] Biome-specific terrain / materials (part-ready) / flora / weather / POIs; seasonal variants
- [ ] Climate fields: temperature, moisture, rain-shadow, wind, altitude/coastal/microclimate; climate→biome/weather; query

## Water & Hydrology — own arc (pairs with erosion)

- [ ] Bodies: oceans/seas/lakes/ponds, rivers/streams/tributaries/deltas, waterfalls/rapids, wetlands, springs/oases, floodplains
- [ ] Sim: sea level, watershed, downhill-flow validation, river erosion + banks, beaches/shorelines, underwater terrain, depth/current maps, ice
- [ ] Infrastructure responds to water (bridges / roads / fords)

## Scatter, Flora & Fauna — the reusable placement backbone (none built; a flora-unit zip exists but is un-integrated)

- [ ] **Generic scatter framework:** seeded/deterministic, density maps, weighted selection, blue-noise/clustered/grid/spline/edge distributions, height/slope/biome/material/distance filters, min-separation, exclusion/reservation masks, normal-align, scale+rot, priority layers, cross-chunk consistency, multimesh output, custom-rule API
- [ ] Flora: grass/flowers/shrubs/trees (procedural + authored), species/mixtures/age, forest density/edges/clearings, tree-lines, deadfall/stumps/litter, seasonal color, wind anim, collision, harvest/regrowth/fire/succession hooks
- [ ] Ground props/clutter: rocks/boulders/debris/mushrooms/crystals/resource-nodes, density controls, prop LOD/culling, destruction/collection hooks
- [ ] Fauna data (no AI): spawn regions, habitat/territory/migration maps, nest/den, food-water + carrying-capacity, time/season spawn rules, creature tables, integration API

## Structures — Roads, Infrastructure, POIs & Settlements (none built)

- [ ] Roads: hierarchy (highway→trail), spline + terrain-conforming geometry, cut/fill/embankments, intersections/switchbacks, bridges/tunnels/fords/ferries/stairs/walls, props/signs, connectivity + slope/turn rules, connect settlements/POIs, nav metadata, authored routes
- [ ] Infrastructure: rail, power lines, pipelines, canals, walls/fences/gates, docks/piers, drainage, utility corridors
- [ ] POIs: types (landmarks/ruins/camps/towers/shrines/farms/mines/dungeons/forts/castles/industrial), rarity+spacing+orientation, building pads, road/water connection, modular assembly + interiors + spawn/loot hooks, authored stamping, exclusion zones
- [ ] Settlements: location scoring (water/roads/resources/defensibility), districts/streets/plots/walls/outskirts, names, hierarchy, faction styles, abandoned variants, growth + trade-route sim

## Sky, Celestial & Atmosphere — BUILT AAA pillar

- [x] Sky, clouds, sun, moon (+ phases), stars, Milky Way, day/night, god rays, time-of-day presets
- [x] Physical atmosphere (transmittance / multiscatter / skyview), aerial perspective / horizon haze, dynamic ambient + exposure
- [~] Sunrise/sunset color variation; configurable star density; time-scale; volumetric & height fog (supported); weather-driven lighting; shadow-distance (GI proxy)
- [ ] Multiple moons; latitude/seasonal sun path; eclipses, aurora, shooting stars, comets; celestial-event hooks; custom calendars
- [ ] Reflection-probe management (radiance cubemap researched); biome-specific atmosphere; interior/exterior exposure; night light-pollution
- [✗] Constellations — explicit user call: none

## Weather & Seasons — cloud-side built; precipitation & seasons not

- [x] Dynamic cloud coverage; cloud shadows; fog
- [~] Ground fog; wind (cloud parallax + cirrus streaks); overcast visibility
- [ ] Precipitation: rain/snow/sleet/hail, thunderstorms/lightning, sand/dust storms, blizzards, mist
- [ ] Weather systems: fronts, regional/biome weather, transitions, forecasting, gusts, accumulation/melt/puddles, gameplay hooks
- [ ] Seasons: count, temperature/precip/daylight, vegetation/material/snow-line shifts, frozen water, flood/drought, transitions

## World Streaming & LOD   ⚠ GRAVEYARD — own roadmap only (killed WG1–15)

> Single-region no-LOD mesh is deliberate and owned (over the 8 ms budget). Do a full terrain roadmap from
> *why the clipmap failed* before ANY mesh-LOD work. Never unilaterally add a clipmap.

- [~] Single displaced 5-layer region (no streaming); configurable region size/scale; deterministic field seed (8 seeds)
- [~] Distance-based material complexity (the material shader already LODs far)
- [ ] ⚠ Infinite chunks (+neg coords), floating origin, async/multithreaded chunk gen, load/unload distances, predictive loading, caching/pooling/queues, memory + frame budgets, seam stitching, mesh/veg/structure LOD, HLOD, impostors, skirts
- [ ] Multi-channel world seed, gen dependency pipeline, cross-chunk ownership, world presets/regeneration, gen-version tracking

## Interaction, Navigation & Physics (none built — look-lab has no gameplay layer)

- [ ] Interaction: terrain deform/dig, build placement, destruction, resource depletion, fire/flood/damage hooks, persistent tracks/roads, world-state events, chunk-mod tracking
- [ ] Navigation: per-chunk navmesh (async, cross-border), layers (land/water/air), cost/difficulty/impassable maps, cover/open/LoS metadata, spawn maps, patrol routes, smart links
- [ ] Physics: terrain collision (+LOD/radius/distant), veg/structure collision, water buoyancy, threaded gen, sleeping, debug viz

## Data Layers & Query API (data exists; no formal API)

- [x] Height map (baked Rf); [~] slope / normal derived per-fragment
- [ ] Maps: biome, temperature, moisture, water, road, settlement, resource, traversability, danger, buildability, visibility, custom
- [~] Sample height/normal/slope at position (sampleable; no formal API)
- [ ] Query API: biome/climate/water at pos; find nearest road/river/POI/settlement; valid-spawn / buildable; sample layer; region & chunk control; register passes/rules; gen/load/unload signals

## Persistence & Multiplayer (none built)

- [~] Lab presets persist params/seed (user://)
- [ ] World save: seed+version, delta-from-procedural (terrain/veg/resources/structures/POIs/player-built), per-chunk files + compression, background queue, migration, snapshots, chunk reset/lock
- [ ] Multiplayer: server-authoritative + deterministic clients, gen-version checks, chunk-stream sync, change/delta replication, server-only ranges, headless-server *(constraint: local-RD compute bakes can't run --headless)*, streaming priorities, late-join sync

## Tooling — Editor · Debug · Performance · Testing (WG16 has real tooling)

- [~] Live look-lab: preview window, data-driven tunable controls (tabs/locks/randomizer), preset save/load, terrain-layer + material editing
- [~] Debug viz: placement/height/slope viz, splat/deck/shadow overlays, on-screen ms/fps, CLI self-checks (--histcheck/--groundarraycheck/--cloudstats/--lightcheck/--profmove)
- [x] Perf: GPU-assisted bakes, texture arrays, shared materials · [~] temporal amortization, cached bakes, GI proxy, configurable 8 ms budget, graceful degradation
- [ ] Editor (full): seed browser/bookmarks, biome/climate/scatter/road/POI editors, gen-pipeline editor, exclusion/biome volumes, authored landmarks, regenerate/bake/export/import
- [ ] Debug (full): chunk overlays/labels, LOD display, biome/temp/moisture heatmaps, road/river/POI/nav displays, object/memory stats, seed inspector, gen history, determinism tool
- [ ] Perf (rest): multithreaded CPU gen, mesh batching, occlusion/distance culling, HLOD⚠, impostors, reusable buffers, platform profiles
- [ ] Testing — *project convention: NO TDD (GPU/visual); verification is the live eye-gate + mechanical CLI self-checks.* Future: determinism / seam / save-load / MP-consistency / stress tests

## Docs & Extensibility (strongly data-driven)

- [x] Data-driven config (json manifests/controls/presets — tune with no C# change); reusable resources; C# API; swappable material library
- [~] Generation-order docs (specs / plans / handoffs per system)
- [ ] GDScript API; custom generator / chunk-renderer / storage interfaces; example + perf-demo projects; API docs; extension examples; migration guide

## Advanced / Long-term

- [ ] Tectonics, geological-age, full watershed sim; procedural history (civilizations, ruins-from-settlements, growth, economy/trade); region & landmark names; generated map tiles; discovery / fog-of-war; planetary / spherical + wrapped topology; alternate dimensions; seed-blending; ML-generated presets

---

**The two foundations to build next, by pillars:** the **biome/climate data layers** and a **generic scatter
framework**. Both build directly on what's already proven (the heightfield + the biome-ready material manifest),
both unlock whole downstream sections (flora, weather, POIs, props), and neither requires entering the graveyard.
