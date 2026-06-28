# Caves & Underground — Spec (design-ahead, build gated)

Scope: digging/deformation + ambient procedural caves + POI-anchored authored caves, as a heavy
**default-off** module that costs ~0 on the surface and ~0.6 ms med-q only underground (`02`). This
is the highest-risk subsystem; build in confidence order and lean on the per-module bounded fallback
(`03`) if infinite proves too costly.

## The representation (one SDF, three layers, one mesher)
```
density(p) = (p.y - field_height(p.xz))     // surface SDF (above/below ground)
           - ambientCaves(p)                 // tier-1 worms + 3D noise voids (pure function)
           - authoredCaves(p)                // tier-2 POI volumes (from WorldGraph slots)
           ± worldDelta(p)                    // dig edits (sparse, persisted)
```
Meshed by **Dual Contouring** in 3D chunks, but **only** where the SDF is non-trivial near the
player (a cave/dig within the underground radius). Elsewhere the existing heightfield mesh renders.

## Units (confidence order — each build → eye-gate)

### C1 · Dig delta + DC patch (proves mesher + delta + seam)
- Brush edit (add/subtract density in a radius) → `WorldDelta` (`04`) keyed by chunk.
- A dirtied chunk switches from heightfield-mesh to **DC-mesh**; re-mesh on the render-thread async
  pipeline (request/dedup/throttle/TryTake, `ChunkFieldCache` contract). Collision shape on re-mesh.
- **The critical gate: the heightfield↔DC seam at the edit boundary does not crack** (`--caveseamcheck`
  numeric + eye). Solve before anything else underground.
- Surface deformation that stays single-valued (craters) takes the cheap heightfield-delta path.

### C2 · Ambient procedural caves (proves infinite SDF streaming)
- `ambientCaves(p)` = 3D Perlin/Simplex **worms** (3-axis noise path, noise-varied radius) +
  iso-threshold chambers; pure function, infinite, deterministic. Data: `data/cave_params.json`
  (worm frequency/radius/density, chamber threshold, depth band).
- Underground streaming shell: DC-mesh the 3D chunks within the underground radius only; LOD with DC
  seam handling; despawn on exit. **Underground gate**: no 3D work when above ground + outside cave
  radius (the ~0-on-surface guarantee).
- **Gate:** explorable natural cave network underground, no cracks, within budget; surface frame
  cost unchanged (`--profmove` above ground = today's numbers).

### C3 · POI-anchored authored caves
- Authored cave volumes (designed dungeons) dropped at WorldGraph POI slots (`pois-integration`);
  meshed by the same DC pipeline; blended into ambient caves at their mouths.
- **Gate:** an authored cave reads intentional + connects cleanly to ambient caves + the surface.

### C4 · Underground lighting + atmosphere
- No sun underground → local lights + ambient floor; **revive the parked GI proxy/SDFGI** for cave
  bounce (ROADMAP already flags this as the revival trigger). Optional depth-fog/darkness by depth.
- **Gate:** caves read atmospheric, lit, not flat-black or flat-bright.

## Data contract (game-agnostic)
- `cave_params.json` — ambient worm/chamber knobs, depth band, density, per-biome modifiers.
- `caves` module toggle (`04`) on World/Modules tab; quality tier scales underground stream radius +
  DC chunk resolution + LOD distances.
- Authored caves = static volume assets keyed to POI slots (objectlist).

## Dependencies & integration
- **Reads:** `field_height` (surface SDF), biome field (cave style per biome), POI slots (authored).
- **Writes:** cave-mouth + dig footprints into the WorldGraph so roads/POIs/flora respect them
  (a road shouldn't run through a sinkhole; a cave mouth is itself a POI candidate).
- **Coordinates with:** lighting/GI revival (C4), water (caves below water table → flooding is a
  Phase-C stretch), collision.

## Performance plan (`02`: ~0.6 ms med-q, underground only)
- Pay-once: DC mesh baked on chunk birth/dirty, not per frame. Sparse: only the underground shell.
- Surface cost = 0 via the underground gate. Spike control: throttle DC re-mesh + collision build.
- Measure before Rust; DC meshing is parallelizable (GPU compute candidate), worm eval is pure GPU.
  The likely serial part is collision-shape build — measure, port only if it stalls.

## Bounded fallback (`03`)
If infinite ambient caves blow memory/perf, scope to **POI-anchored + dig-only** (C1+C3, no C2), or
to bounded "solved regions" with caves presolved per macro-cell. Decided by measurement at C2, not
upfront. The whole module stays default-off until C1+C2 pass the user's eye together.

## STOP criterion
Stop at the first confidence step that passes (C1 = digging works + no seam crack). Do NOT build C2
ambient caves until C1's mesher/delta/seam is eye-gated solid — this is the subsystem where building
ahead is most dangerous.
