# Roads & Paths — Research

The user wants: **adaptable** — animal trails → small paths → streets → highways — with **material,
depth, width tunable + procedural**, **contiguous across the infinite world**, optional (a choice),
and **linking POIs** while respecting water/caves/forests. This is a tier-2 (presolved) + tier-3
(streamed mesh) system on the WorldGraph (`04`).

## How AAA does roads (techniques + sources)

The shipping stack converges on one pipeline (named almost verbatim in current procedural-road
tooling): **slope-weighted A* routing + water avoidance + causeways/bridges + terrain-graded
roadbeds (cut & fill) + spline-mesh rendering + splat/decal blend.** ([procedural road gen][1],
[Houdini road tool][2])

1. **Routing — slope-weighted A\*** over the terrain. Cost = distance × slope penalty × surface
   penalty (avoid water, swamp, cliffs) + bonus for following valleys/ridges. Higher road classes
   tolerate more cut/fill (highways flatten terrain; trails just drape). ([1])
2. **Centerline → spline.** Smooth the A* path into a spline (control points), then **resample** to
   a ribbon. Junctions are the hard part (where splines meet). ([spline road mesh][3], [junctions][4])
3. **Terrain conforming = "cut and fill."** The Houdini reference: **blur the heightfield under the
   road + integrate the roadbed** so the terrain grades up/down to meet the road (cuts through hills,
   fills across dips), preventing high angles. This is a **height delta written back to the
   terrain**, not just a draped mesh. ([Houdini integrate road][2], [video][5])
4. **Rendering — two complementary methods:**
   - **Splat/decal** — paint the road surface material into the terrain splat (or project a decal).
     Cheap, conforms perfectly, no extra mesh, great for trails/dirt. ([Houdini splat][2])
   - **Ribbon mesh** — a generated mesh strip along the spline for raised/detailed roads (cobble,
     asphalt with curbs, bridges). ([3])
   AAA uses both: decal/splat for the surface blend + mesh for 3D detail (curbs, rails, bridges).

## How this maps onto WG16 (the fit is good)

Roads are a **near-perfect fit for the hybrid `03` architecture** — they're the canonical example of
why pure infinite (`Option A`) fails: a road from A to B needs to *plan* across regions, which a
per-chunk pure function can't do. So:

- **Tier-2 presolve (WorldGraph):** per macro-cell, run slope-weighted A* over the **cached coarse
  height** between POI anchors + cross-tile border waypoints. **Border waypoints are deterministic
  from the shared seed** so both neighboring tiles agree on the crossing → **contiguous infinite
  roads** without a global solve. Output: a road graph (splines + class + width) into the solved
  record.
- **Tier-1 per-chunk bake:** when a chunk is born, sample the road graph crossing it → write
  (a) the **cut-and-fill height delta** into the chunk's height (terrain conforming), (b) the **road
  material** into the splat mask. Both pay-once on birth.
- **Tier-3 streamed mesh:** generate the ribbon mesh (curbs/bridges) along the spline near the
  camera; despawn far. Decal/splat handles the surface; mesh handles 3D detail only where needed.

## Adaptability (trails → highways) = data, not code

A **road class** is a data record (objectlist, `04`): width, surface material, max grade, cut/fill
strength, has-curbs, has-mesh, decoration density. Animal trail = narrow, no cut/fill, splat-only,
follows terrain loosely. Highway = wide, heavy cut/fill, mesh + curbs + bridges, near-flat grade.
The A* cost weights and the bake read the class → same pipeline, different knobs. Game-agnostic.

## Contiguity across infinity (the headline requirement)

Solved by the WorldGraph macro-cell + deterministic border-waypoint protocol (above). A road is a
sequence of macro-cell A* solves stitched at agreed border points. No tile needs the whole world —
only itself + a 1-cell skirt. This is the same skirt-stitch protocol drainage uses (`03`), so roads
and rivers share infrastructure.

## Integration (the "link POIs, respect water/caves/forests" requirement)

This is where roads touch everything — handled by the **provider order** (`04`):
`biome → drainage → roads → POIs`. Roads run **after** drainage (so they avoid/bridge water) and
**before** POIs claim final slots (POIs anchor the road graph; roads connect them). Flora reads the
road mask to **clear trees** from the roadbed + add roadside density. Caves write cave-mouth
footprints roads avoid. **Roads can themselves be POIs** (a ruined highway, a landmark bridge). See
`pois-integration` for the full handshake.

## Risks / open questions
- **Junctions** are the classic hard case (where splines meet) — needs careful mesh + splat blend.
- **Bridges/tunnels** over water/through cliffs — A* "causeways" decide bridge vs detour by cost;
  bridges are mesh + the cut/fill skips; tunnels hand off to the caves module.
- **Cut-and-fill vs the field cache** — the road height delta must compose with the cached chunk
  height (apply delta in the bake, before normal computation) without breaking morph/stitch gates.
- **Visual quality** — roads that just recolor splat read flat; the cut/fill grading + roadside
  detail (ditches, verges, wear) is what sells them. Eye-gate the *graded* road, not the decal alone.

## Recommendation
Build routing + cut-and-fill + splat first (the contiguous, conforming spine), eye-gate a draped
dirt road across terrain, THEN add classes, mesh ribbons, junctions, bridges. Default-off; this is a
lower-risk subsystem than caves but its value is entirely in integration, so it sequences **after**
POIs + drainage exist to connect.

## Sources
[1]: https://www.researchgate.net/publication/229707505_Procedural_Generation_of_Roads
[2]: https://www.artstation.com/artwork/kD69w0
[3]: https://www.researchgate.net/figure/Spline-based-road-mesh-generation_fig12_320722498
[4]: https://www.diva-portal.org/smash/get/diva2:1675311/FULLTEXT02
[5]: https://www.youtube.com/watch?v=hWGCs4MLGqQ
