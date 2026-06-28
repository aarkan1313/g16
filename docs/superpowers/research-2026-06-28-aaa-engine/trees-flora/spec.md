# Trees & Flora — Spec (design-ahead, build gated)

Scope: a data-driven, biome-rule-placed, impostor-LOD'd flora system (grass + shrubs + trees) that
plugs into the `WorldGraph`/`ScatterStreamer` infra (`04`) inside ~1.2 ms med-q (`02`). Built behind
a `flora` master toggle, default-off until eye-gated.

## Units (each its own build → eye-gate, one phase past the last pass)

### F1 · Flora lab + species library (offline-ish authoring)
- A `flora_lab.tscn` sibling to the material board: author/preview a species via space-colonization
  params (crown envelope, kill distance, segment length, leaf model, bark material).
- Bake per species: a mesh **LOD chain** (L0 full → L2 reduced) + a **spherical impostor atlas**
  (N views around a sphere, with albedo + normal + depth channels).
- Output: `data/flora_species.json` (objectlist, schema in `item_schemas.json`) + baked assets.
- **Gate:** a single tree at 3 distances reads realistic (mesh → impostor transition acceptable).

### F2 · Placement bake (tier-1, per-chunk on birth)
- GPU compute pass (`flora_placement.glsl`) reads cached height/normal + biome/moisture field
  (from `biomes-macro-variety`) → emits instance transforms per species into a buffer, with
  clustering (groves via low-freq mask), slope/moisture/aspect rules, Poisson-ish jitter, density
  from biome rule. Mirrors `ChunkFieldCache` (request-on-birth, dedup, throttle, TryTake).
- Data: `data/flora_rules.json` — per-biome species mix, density, cluster scale, slope/moisture
  limits, edge rules. Hot-reloadable.
- **Gate:** placement reads natural (groves not grid), correct per biome/slope; `--floracheck`
  numeric (counts/density per biome) for correctness.

### F3 · Render streamer (tier-3, MultiMesh + impostors)
- Extend the shared `ScatterStreamer`: per (species, LOD) `MultiMeshInstance3D` populated from F2
  buffers near camera; impostor `MultiMesh` (1 quad) beyond `impostor_dist`; despawn on retire.
- GPU frustum/distance cull; dither cross-fade at the mesh↔impostor seam.
- **Gate:** a flown forest reads dense + realistic, no visible pop, in motion.

### F4 · Grass / ground clutter (tier-1 + tier-3, per-cell)
- Per-cell instanced blades, all-or-nothing in frustum; density curve fades to ground-texture
  blend; color sampled from terrain palette so grass matches ground.
- **Gate:** grass reads continuous with the ground, no harsh density edge.

### F5 · Wind + lighting integration
- Vertex wind (sine-sum / flow tex) amplitude by hierarchy; **driven by the Weather axis** (wind
  vector from `weather/`). Leaf subsurface + trunk-base AO + ground contact shadow (coordinate with
  the shadow rebuild).
- **Gate:** motion + lighting read alive, not static cards.

## Data contract (game-agnostic)
- `flora_species.json` (objectlist) — id, model/impostor asset paths, LOD distances, bark/leaf mats.
- `flora_rules.json` — per-biome: species weights, density, cluster scale, slope/moisture/aspect
  limits, edge-effect rules (near water/road/POI). Consumed by F2.
- Registry: a `flora` module toggle (`04`) on the World/Modules tab; quality tier scales impostor
  distance / grass radius / max instances / LOD bias.

## Dependencies & integration
- **Reads:** biome + moisture field (`biomes-macro-variety`), cached height/normal (field cache),
  water/road/POI masks (for edge effects + avoidance) from the WorldGraph solved record.
- **Writes:** trunk-footprint mask back into the WorldGraph so roads/POIs avoid trees (or clear
  them) — the integration handshake (`pois-integration`).
- **Coordinates with:** shadow rebuild (flora shadows), Weather axis (wind).

## Performance plan (`02`)
- Placement = pay-once on birth (no per-frame placement). Far = impostors (1 quad). Spend = near
  meshes + grass. Throttle birth bake + MultiMesh upload (spike control). Tier-scaled densities.
- Measure before any Rust; placement is pure GPU compute (no serial part).

## STOP criterion
Stop when a flown, biome-varied, windy forest+grass reads realistic in motion within budget on the
target tier, with the toggle defaulting off until the user's eye passes F3+F4 together (the forest
gate). Do not build F5 polish before F3/F4 pass.
