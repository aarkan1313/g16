# Biomes & Macro Variety — Spec (design-ahead, build gated)

Scope: a data-driven climate field (temperature × moisture → Whittaker biome + variant) baked per
macro-cell as the **first WorldGraph provider** (`04`), consumed by palette/flora/POI/road/cave/
weather. Presolve/cached → off the per-frame budget. Default-on once gated (it's the thing that makes
regions differ; little reason to disable, but still a module toggle for game-agnostic use).

## Pipeline (presolve → sample)
```
WorldGraph macro-cell (first provider):
  temperature = lat/base + altitude_lapse(coarseHeight) + domainWarpNoise
  moisture    = flow_accum(drainage) + dist_to_water + rainShadow(terrain, prevailingWind)
  biome_id    = Whittaker_lookup(temperature, moisture)   // data table
  variant     = variant_pick(biome_id, noise)             // sub-look
  ─> biomeField {temp, moist, biome_id, variant} into solved record (coarse, cached)
consumers sample biomeField with a blend radius → cross-faded palettes/flora/rules
```
Note: biome and drainage are mutually coupled (moisture needs flow_accum; rain-shadow needs wind).
Resolve by running a cheap **first-pass terrain-only climate** (temp + dist-to-water) → drainage →
then **refine moisture** with flow_accum. Documented two-step, same macro-cell solve.

## Units (each build → eye-gate)

### BIO1 · Climate field + Whittaker lookup
- Bake temp + moisture (two-step w/ drainage) + biome_id per macro-cell. Data: `data/biomes.json`
  (Whittaker table: temp/moist bins → biome id; per-biome metadata).
- **Gate:** `--biomecheck` — field is continuous across macro-cell borders (skirt-stitch), biome
  distribution matches the table (numeric); a debug-color overlay reads sensible (deserts low/hot,
  forests wet, snow high/cold).

### BIO2 · Palette + consumer wiring (prove the fan-out)
- Wire the biome field into the existing `ground_palette.json` per-region selection (ROADMAP says
  it's already built for this) → ground look changes per biome with blended borders.
- **Gate:** flying across a biome border, the ground palette cross-fades correctly, no hard seam.

### BIO3 · Variants + region-scale warp + rare regions
- Per-biome variant tables (dunes/rocky/salt-flat), domain-warp scale for region-size variation,
  rare-region weights (volcanic/crystal). All data.
- **Gate:** regions of the same biome read varied; exploration surfaces occasional special regions.

### BIO4 · Sharp-vs-blended borders + altitude coupling
- Per-border-type blend width (coastline/treeline sharp; climate gradients soft); strong altitude
  lapse → snowy caps on tall peaks in warm biomes (feeds Phase-C snow-on-ground).
- **Gate:** coastlines/treelines read crisp; mountain caps snow correctly.

## Data contract (game-agnostic)
- `biomes.json` — Whittaker table + per-biome: palette ref, flora species mix ref, POI type weights,
  road surface, cave style, weather preset ref, variant table, border-blend widths, rare weight.
- `biomes` module toggle (`04`); quality tier scales blend sampling cost + variant detail.

## Dependencies & integration
- **Reads:** cached coarse height (altitude lapse), drainage flow_accum (moisture), prevailing wind
  (Weather axis, rain-shadow). **Writes:** biomeField (first field in the solved record).
- **Consumed by:** palette, flora (species), POIs (types), roads (surface), caves (style), weather
  (per-biome presets) — the fan-out that makes biome the keystone's first tenant.
- **Coordinates with:** drainage (mutual coupling, two-step solve), Weather axis (wind in, presets
  out).

## Performance plan (`02`)
- Presolved + cached per macro-cell → off per-frame budget. Consumers sample a coarse cached field
  (a texture tap), negligible per-frame. The cost is the presolve, amortized + thrown N cells ahead.
- Pure per-cell GPU compute (Whittaker lookup, noise, lapse) — no serial part; no Rust expected.

## STOP criterion
Stop after BIO2: the biome field bakes continuously and visibly drives the ground palette across
borders. Variants/rare-regions/border-tuning (BIO3/4) are polish after the fan-out is proven. Build
biomes after the WorldGraph + drainage exist (it needs flow_accum), before flora/POIs lean on it.
