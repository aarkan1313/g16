# POIs & Integration — Research

The user's framing: POIs are **majorly data-driven**, a **mix of procedural and handcrafted
chunks/regions**, and **roads must link them** while **caves/water/forests link with them too — not
ruin them, or be POIs themselves.** This is two things: (1) a POI placement+authoring system, and
(2) the **integration/constraint layer** that makes all subsystems cooperate. The integration layer
is arguably the most important deliverable in the whole dossier — it's what makes a *world* instead
of five bolt-on generators.

## POI placement (techniques + sources)

- **Poisson-disk / blue-noise placement** — grid placement reads too regular, pure-random too
  messy; blue noise gives natural spacing with a minimum distance, and supports variable radii for
  different POI sizes. The standard for "scattered but well-spaced." ([Poisson][1], [devmag][2])
- **Deterministic candidate slots** — per macro-cell, hash a blue-noise point set → candidate POI
  slots, each tagged with biome/slope/water-distance context. Deterministic from seed = infinite +
  cacheable (No Man's Sky generate-on-visit). ([NMS][3])
- **Wave Function Collapse / model synthesis** for *intra-POI* layout (a village's building
  arrangement, a dungeon's room graph) — tile-based with local + non-local constraints, extendable
  to 3D. Use for the *inside* of a procedural POI, not world placement. ([WFC][4], [model synth][5])
- **Handcrafted via prefab masking / stamping** — a designer authors a scene (a fort, a shrine);
  the world reserves a slot ("prefab mask") and stamps it in, blending terrain to meet it. "Worlds
  that feel handcrafted while remaining infinitely procedural." ([prefab][6])

## The AAA hybrid (Horizon Zero Dawn — the reference to copy)

Guerrilla's pipeline is the proven model for "procedural + handcrafted in one world": **artists
hand-paint rule maps** that define biome characteristics + placement parameters; a **GPU procedural
placement** pass populates density deterministically within those hand-authored regions. "A few
artists create huge areas." The key distinction from No Man's Sky: **definite level structure
(authored) + procedural decoration**, not pure algorithm. WG16 wants *both* poles available —
infinite procedural POIs AND stamped handcrafted regions — selectable as data. ([HZD][7], [HZD GG][8])

## How POIs map onto WG16

- **Tier-2 presolve (WorldGraph):** per macro-cell, generate blue-noise candidate slots → filter by
  POI-type rules (biome, slope, water-distance, spacing from other POIs) → assign POI types →
  reserve slots. Handcrafted POIs claim specific slots (or hand-placed coords). Output: POI list into
  the solved record. Runs **last** in provider order (after biome/drainage/roads) so it can react to
  them.
- **Tier-3 streamed instances:** procedural POIs spawn from a prefab/rule set near the camera;
  handcrafted POIs load as static scenes; both blend terrain to meet them (stamp the heightfield
  flat/graded under the footprint on chunk birth — the road cut/fill machinery, reused).
- **Data-driven:** `data/poi_types.json` (objectlist) — id, kind (procedural/handcrafted), footprint,
  placement rules, prefab/scene ref, road-link priority, terrain-stamp profile.

## The integration / constraint layer (the real headline)

This is the connective tissue the user is really asking for. Implemented as the **WorldGraph provider
order + a shared solved-record + footprint masks** (`04`):

```
biome ─> drainage ─> roads ─> POIs        (solve order; each reads the prior)
   └──────────────> flora  ──────────┘    (flora reads all masks)
caves: ambient (pure fn) + POI-anchored (after POIs)
```

**The rules that make them cooperate (each is a mask read during the bake):**
- **Roads link POIs:** POIs publish anchor points; roads A* between anchors (`roads-paths`).
- **Water doesn't drown POIs:** drainage runs first; POI placement rejects flooded slots; a POI on a
  river gets a bridge (road causeway) not a swamp.
- **Forests don't engulf POIs:** flora reads the POI footprint mask → clears trees inside, adds a
  treeline edge around. POIs can request a clearing or a grove.
- **Caves don't undermine POIs:** ambient caves carve a keep-out under POI footprints; a POI can
  *request* a cave (a mine entrance) → authored cave anchored to its slot (`caves-underground`).
- **Terrain conforms to POIs:** stamp/grade the footprint flat on chunk birth (reused road cut/fill).
- **POIs can be anything:** a road (ruined highway), a lake (sacred pool), a cave (dungeon), a grove
  (ancient forest) can each *be* a POI — POI type just references the relevant module's authored
  content. This satisfies "or be POIs themselves."

**Why this works:** every subsystem reads/writes the **same per-macro-cell solved record + footprint
masks**, in a fixed dependency order, so cooperation is *structural* — not special-case glue code per
pair. Adding a new subsystem = it reads the masks it cares about + writes its own. Game-agnostic,
modular, exactly the existing design philosophy extended to global content.

## Risks / open questions
- **Authored-content pipeline** — how designers author + register prefab POIs (an in-engine POI lab +
  a scene-import convention). Real tooling work.
- **Seam at the POI↔terrain boundary** — stamping must not crack the CDLOD mesh (reuse road cut/fill
  gates).
- **Determinism with edits** — a placed/destroyed POI is a WorldDelta; cache-miss regenerates the
  base, delta re-applies (NMS pattern).
- **Constraint conflicts** — two POIs want the same slot, a road can't reach one, a biome forbids a
  required POI → the provider order + priority fields resolve, but needs a conflict policy (defined
  in the spec).

## Recommendation
Build the **integration layer (provider order + solved record + footprint masks) FIRST as part of
the WorldGraph**, with POIs as its first consumer, because every other subsystem depends on it. POIs
prove the contract; roads/flora/caves then plug in by reading masks. This makes POIs+integration the
**Phase-B keystone alongside the WorldGraph itself**.

## Sources
[1]: http://devmag.org.za/2009/05/03/poisson-disk-sampling/
[2]: https://www.coord.space/blog/2019/03/20/coord-cards-poisson-sampling/
[3]: https://nomanssky.fandom.com/wiki/Procedural_generation
[4]: https://www.boristhebrave.com/2020/04/13/wave-function-collapse-explained/
[5]: https://en.wikipedia.org/wiki/Model_synthesis
[6]: https://hytale.com/news/2026/1/the-future-of-world-generation
[7]: https://80.lv/articles/the-procedural-nature-of-the-horizon-zero-dawn
[8]: https://www.guerrilla-games.com/read/gpu-based-procedural-placement-in-horizon-zero-dawn
