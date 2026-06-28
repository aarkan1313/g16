# Caves & Underground — Research

The user wants: **caves + tunnels (real 3D geometry), digging (ground deformation), and legit
tunnels** — as a **heavy, not-always-on module** ("a huge infra and performance thing we don't
always want on"). This is the biggest architectural departure in the dossier, because WG16 today is
a pure heightfield — there is no "below the surface."

## The fundamental shift: heightfield → signed distance field (only where needed)

A heightfield can't represent overhangs, tunnels, or carved-out volume — by definition one height
per (x,z). Caves require a **3D volumetric representation** in the regions that have them. The
standard answer: an **SDF / density field** sampled in 3D, meshed with an isosurface extractor.

**Isosurface extraction (the meshing choice):**
- **Marching Cubes** — ubiquitous, fast, free implementations, but **blocky on sharp features** and
  spews duplicate vertices. ([0fps][1])
- **Surface Nets** — smooth, cheap, but can't hold 90° sharp edges. ([web][2])
- **Dual Contouring** — vertices *inside* cells (uses Hermite data / normals) → **recovers sharp
  edges** (carved corners, mine walls) and avoids the duplicate-vertex explosion; the AAA choice for
  destructible voxel terrain with caves and carved features. Has known **seam/LOD** handling for
  chunked terrain. ([Nick Gildea][3], [Upvoid][4])

**Verdict:** Dual Contouring for the cave/dig volume — it's the one that holds carved corners and
chunks cleanly with LOD. Marching Cubes is the fast fallback if DC tooling proves too heavy.

**Cave SHAPE generation:**
- **3D Perlin/Simplex "worms"** — a worm walks a path driven by 3 noise generators (one per axis),
  carving a sphere of noise-varied radius at each step → natural winding tunnels. Frequency tunes
  straightness vs randomness. Cheap, infinite, pure-function. Minecraft/No Man's Sky lineage.
  ([Caveworm][5])
- **3D noise iso-threshold** for chambers/cheese caves (large voids where 3D noise < threshold).
- **Authored cave systems** for POIs (a designed dungeon dropped as a volume) — see
  `pois-integration`.

## How this fits WG16 without breaking the heightfield spine

The keystone insight: **the surface stays a heightfield; the SDF volume is sparse and lazy.** The
density field is defined as `surfaceSDF(p) = p.y - field_height(p.xz)` (above/below ground) **minus**
cave carves (worms + noise voids) **plus/minus** the dig delta. We only **mesh** the SDF in 3D
chunks that (a) contain a cave/dig within the player's underground radius, OR (b) are being actively
edited. Everywhere else, the cheap heightfield mesh renders the surface exactly as today.

So caves are a **tier-1 pure-function** field (worms + noise = infinite, deterministic, free to
regenerate) for *ambient* caves, **plus** a **tier-2 authored** layer for POI dungeons, **plus** a
**WorldDelta** layer for digging (`04`). Three layers, one SDF, one mesher.

## Digging / ground deformation

- **Surface deformation** (craters, terracing) = a height *delta* added to `field_height` — cheap,
  stays in the heightfield path if it doesn't create overhangs.
- **True tunnels / overhangs** = SDF carves stored in the **WorldDelta** (`04`), which flips the
  affected chunk from heightfield-mesh to DC-mesh. Edits are sparse, persisted, applied over the
  regenerated base (No Man's Sky cache-miss-regenerates pattern).
- **Editing UX** = a brush (add/subtract density in a radius) → mark chunk dirty → re-mesh that
  chunk (and neighbors at seams) on the render-thread async pipeline. Same throttle/dedup contract.

## The "not always on" win (literal, per `04`)

When `caves.Enabled == false`: no SDF eval, no 3D mesher, no delta store — the engine is exactly
today's heightfield. When enabled but the camera is above ground and outside any cave radius: the
underground gate skips all 3D work — **surface frames pay ~0.** Cost appears only when you're near
or inside a cave/dig (the `02` ~0.6 ms budget is for *that* regime, not the whole world). This is
exactly the heavy-module model the user described.

## Risks / open questions (this is the hardest subsystem)

- **Memory** — 3D chunks are far heavier than 2D; must sparse-stream only the underground shell near
  the player + LOD aggressively (DC supports chunked LOD with seam handling).
- **Seams** — heightfield-mesh ↔ DC-mesh boundary at the cave mouth must not crack (the DC seam/LOD
  problem, plus a new 2D↔3D seam). The hardest correctness gate here.
- **Lighting underground** — no sun; needs local lights / ambient + the GI proxy revival (ROADMAP
  already parks SDFGI "revive with caves"). Coordinate with the lighting lane.
- **Persistence scale** — a heavily-dug world's delta store can grow large; LRU + region files.
- **Collision** — DC mesh needs collision shapes for the player; cost on re-mesh.
- **This is where the bounded-world fallback (`03`) is most likely** — if ambient caves everywhere
  prove too memory/perf heavy infinite, scope caves to bounded "solved regions" or POI-anchored only.

## Recommendation
Build caves in two confidence steps: **(1) digging/deformation as a heightfield delta + sparse DC
patch** (proves the mesher + delta + seam) before **(2) ambient procedural cave networks** (proves
infinite SDF streaming). POI-anchored authored caves ride on (1)'s mesher. Default-off throughout;
this is the module most likely to need the per-module bounded fallback.

## Sources
[1]: https://0fps.net/2012/07/12/smooth-voxel-terrain-part-2/
[2]: https://groups.google.com/g/curv/c/rCCaSLflgrU
[3]: http://ngildea.blogspot.com/2014/09/dual-contouring-chunked-terrain.html
[4]: https://upvoid.com/devblog/2013/05/terrain-engine-part-1-dual-contouring/
[5]: https://github.com/Maxopoly/Caveworm
