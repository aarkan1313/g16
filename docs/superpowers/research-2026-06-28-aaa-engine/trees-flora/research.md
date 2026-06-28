# Trees & Flora — Research

Goal: procedural trees + grass + shrubs that read **realistic** (the user's bar: "pretty damn
realistic"), placed by biome/terrain rules, contiguous across the infinite world, inside a ~1.2 ms
medium-quality budget (`02`). A prior mockup existed and was discarded; this is the real design.

## How the bar is met in shipping work (techniques + sources)

**Tree GEOMETRY generation:**
- **Space colonization** — scatter attraction points in a crown envelope; branches grow toward
  them. Produces structures that "closely resemble real tree structures," more natural than naive
  L-systems, and is parameterizable (envelope shape, kill distance, segment length → species). Good
  for *baking a finite species library*, expensive for live per-instance. ([ciphrd][1])
- **L-systems** — commercial-grade (SpeedTree lineage) and compact, but "blindly replace elements";
  making them light/collision-aware needs tuning. Best as an authoring tool, not runtime. ([web][2])
- **Infinigen** (Princeton, 2023) — fully procedural photorealistic nature from geometry rules, no
  baked assets; proves the realistic bar is reachable procedurally, but it's offline-render cost.
  Lesson: **generate a finite, high-quality species set offline; don't generate geometry per
  frame.** ([Infinigen][3])

**Tree RENDERING at open-world scale:**
- **LOD chain → impostor billboards.** Near: full mesh w/ LODs. Far: a single camera-facing quad
  sampled from a **spherical impostor atlas** (images captured around a sphere; pick the view
  closest to the eye). Cross-quads as a mid LOD. This is the universal AAA approach (SpeedTree, GPU
  Instancer). ([SpeedTree][4], [GurBu][5])
- **GPU instancing** for everything; per-instance transform from a buffer; frustum + distance cull
  on GPU. Godot's `MultiMeshInstance3D` is the native primitive.

**Grass / ground clutter:**
- **Per-cell instancing, not per-blade culling** — grass cells render all-or-nothing when in
  frustum (cuts CPU, the GPU eats density). Single LOD, single material. Density fades with
  distance to a ground-texture blend. ([SpeedTree][4])
- AAA grass (Ghost of Tsushima / RDR2 style) = GPU-placed instanced blades + wind vertex animation
  + view-distance density curve + color-from-terrain so grass matches the ground beneath it.

**Wind:** vertex-shader sine-sum / flow-texture sway, amplitude by hierarchy (trunk < branch <
leaf); ties into the Weather axis (`weather/`).

## What maps cleanly onto WG16 (the fit)

- **Placement is a pure-function tier-1 bake** (`04`): per chunk on birth, a GPU compute pass reads
  the cached height/normal/biome/moisture and emits instance transforms into a buffer — exactly the
  `ChunkFieldCache` pattern. No per-frame placement cost. Density/species from biome rules (data).
- **Rendering is a tier-3 streamed-instances job** via a shared `ScatterStreamer` (`04`):
  `MultiMesh` per (species, LOD) near the camera; impostors beyond N m; despawn on chunk retire.
- **Species library is offline-baked data** — a small set (10–30) of space-colonization-generated
  trees per biome, each with a mesh LOD chain + a baked spherical impostor atlas, stored as assets.
  Game-agnostic: a game ships its own species set; the engine consumes the data.
- **Color-from-terrain + wind-from-weather** reuse existing seams (the ground palette, the Weather
  axis), so flora integrates rather than bolts on.

## Realism levers (where "pretty damn realistic" actually comes from)

1. **Silhouette + impostor quality** — most far-view realism is the impostor atlas resolution + a
   normal/depth channel so far trees still light correctly (not flat cards). Cheap, huge payoff.
2. **Placement realism** — clustering (groves, not uniform scatter), slope/moisture/aspect rules,
   undergrowth layering (canopy → shrub → grass → detail), edge effects at biome/water/road borders.
   *Placement sells a forest more than any single tree.*
3. **Lighting integration** — translucent leaves (subsurface), AO at trunk bases, contact with the
   ground shadow. Ties to the shadow rebuild — coordinate, don't fork.
4. **Wind + parallax** — motion is realism; static forests read fake immediately.

## Budget reality (`02`: ~1.2 ms med-q)

Achievable because placement is pay-once (baked on birth) and far flora is impostors (1 quad). The
spend is near-field full meshes + grass density. Quality tier scales: impostor distance, grass
density radius, max instances/chunk, species LOD bias. The spike risk is birth-time placement bake +
MultiMesh upload — throttle per the `04` birth contract.

## Risks / open questions

- **Impostor pop** at the mesh→billboard transition — needs dither/cross-fade (eye-gate).
- **Asset pipeline** — generating + baking the species library (mesh LODs + impostor atlas) is real
  tooling work; is it a build-time tool or an in-engine lab? (Recommend an in-engine "flora lab"
  sibling to the material board.)
- **Memory** — many MultiMeshes + impostor atlases; sparse-stream only near player.
- **Collision** — trees as obstacles for roads/POIs/player; the WorldGraph must know trunk
  footprints (integration with `pois-integration`).

## Sources
[1]: https://ciphrd.com/articles/generating-a-3d-growing-tree-using-a-space-colonization-algorithm/
[2]: https://gamedev.net/forums/topic/657297-ideas-for-rendering-huge-vegetation-foliage/
[3]: https://arxiv.org/pdf/2306.09310
[4]: https://docs.speedtree.com/doku.php?id=gpu_topics
[5]: https://wiki.gurbu.com/index.php?title=GPU_Instancer%3AFeatures
