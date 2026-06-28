# Roads & Paths — Spec (design-ahead, build gated)

Scope: contiguous, terrain-conforming, class-adaptive (trail→highway) road network on the WorldGraph,
linking POIs and respecting water/caves/flora, inside ~0.3 ms med-q (`02`). Default-off behind a
`roads` toggle. Sequences **after** drainage + POIs exist (roads connect them).

## Pipeline (presolve → bake → stream)
```
WorldGraph macro-cell:  POI anchors + border waypoints ──A* (slope/water/surface cost)──> road graph (splines + class)
chunk birth (tier-1):   sample road graph ─> cut&fill height delta + road splat mask  (pay-once)
near camera (tier-3):   ribbon mesh (curbs/bridges/junctions) along spline            (despawn far)
```

## Units (each build → eye-gate, one phase past last pass)

### R1 · Routing + contiguity (the spine)
- Slope-weighted A* over cached coarse height between POI anchors + **deterministic border
  waypoints** (hashed from shared seed so neighbor tiles agree → contiguous infinite roads).
- Cost = dist × slope penalty × surface penalty (water/cliff avoid) − valley/ridge bonus, scaled by
  road class. Output: road graph (splines + class + width) into the solved record.
- **Gate:** `--roadcheck` — a road crosses ≥3 macro-cells with no border discontinuity (numeric).

### R2 · Cut-and-fill conform + splat (the look spine)
- On chunk birth: blur the chunk height under the road + grade the roadbed (cut through hills, fill
  dips) as a height delta applied **before** normal/morph computation (composes with field cache);
  write the road surface material into the splat mask.
- **Gate:** a draped dirt road reads graded + conformed (not floating, not a flat recolor), morph/
  stitch/field checks still PASS.

### R3 · Road classes (adaptability = data)
- `data/road_classes.json` (objectlist): width, surface mat, max grade, cut/fill strength, has-mesh,
  has-curbs, decoration density, A* cost weights. Trail → path → street → highway.
- **Gate:** the four classes read distinct (trail loosely drapes; highway flattens + widens).

### R4 · Ribbon mesh + junctions + bridges (tier-3 detail)
- Generate spline ribbon mesh near camera for raised/detailed roads (curbs, asphalt, bridges);
  junction meshing where splines meet; bridges over water (A* causeway decision), tunnels hand off
  to caves.
- **Gate:** mesh roads + junctions + a bridge read clean, no cracks at the splat↔mesh seam.

### R5 · Wear + roadside detail
- Ditches/verges, edge wear, roadside flora density (flora reads road mask), debris/decoration.
- **Gate:** roads read lived-in, not CAD-clean.

## Data contract (game-agnostic)
- `road_classes.json` — class definitions (R3). `roads` module toggle (`04`) on World/Modules tab.
- Quality tier scales: mesh distance, junction detail, roadside density, A* resolution.

## Dependencies & integration (`pois-integration`)
- **Reads:** cached coarse height + drainage (avoid/bridge water) + POI anchors (endpoints) + biome
  (surface style) from the solved record. **Provider order: after drainage, before POI finalize.**
- **Writes:** road mask into the solved record → flora clears roadbed + adds verges; caves avoid
  road undersurface; POIs orient to roads. Roads-as-POIs (ruined highway) via the POI system.
- **Coordinates with:** caves (tunnels), water (bridges), field cache (height-delta composition).

## Performance plan (`02`: ~0.3 ms med-q)
- Routing = presolved per macro-cell (off render thread, cached). Bake = pay-once on birth. Surface
  = splat (free) + mesh only near camera. No per-frame pathfinding. Throttle birth bake + mesh gen.
- A* is **serial** → C# first; the likely Rust candidate if the presolve scheduler stalls (measure
  per the ladder — WG15's lesson: serial routing is exactly what Rust helps and GPU doesn't).

## STOP criterion
Stop at R2: a contiguous, terrain-conformed dirt road across multiple macro-cells, eye-gated, before
adding classes/mesh/junctions. Roads have no value without POIs to connect — do not build R1 before
the POI anchor system exists (sequence in `00`).
