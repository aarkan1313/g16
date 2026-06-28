# 04 · Module & Knobs Framework — the always-off heavy-module infrastructure

The user wants caves, digging, roads, POIs etc. as **modules/knobs that aren't always active** —
"a huge infra and performance thing we don't always want on." This doc formalizes that: a single,
data-driven contract so any subsystem plugs in as a **toggleable, budgeted, tier-aware module**
that costs ≈0 when off, and so the global ones (drainage/roads/POIs/biomes) share ONE presolve +
cache + delta infrastructure (the `WorldGraph` from `03`). It extends the existing registry — it is
not a new framework, it's the existing one grown a contract.

## What exists today (build on, don't replace)

- **Registry:** `data/lab_controls.json` → `LabControl` records (`id/tab/type/param|setter|field|
  scene|cloud/min/max/default/rand/rebake`). Adding a knob is a JSON line; non-shader knobs need a
  matching C# case. The `objectlist` type + `item_schemas.json` already supports itemized,
  schema-driven data lists (luminaries use it) — **this is exactly the shape POIs/roads/biomes
  need.**
- **Streaming birth contract:** `ChunkFieldCache` — request-on-birth, dedup by key, render-thread
  async dispatch, throttled `MaxRequestsPerFrame`, `TryTake` consume, ready-delay to dodge GPU/CPU
  races. **Every streamed module copies this.**
- **Local-RD compute + `Std430Writer`** for GPU passes; `CallOnRenderThread` + `Texture2Drd` for
  per-frame compute→material.

## The three module tiers (every subsystem declares one)

| Tier | Cost model | Examples | Plugs into |
|---|---|---|---|
| **Pure-function** | regenerate from position, zero storage | terrain detail, ambient caves (3D noise SDF), grass scatter | per-chunk birth bake (field-cache pattern) |
| **Presolved-cached** | solve once per macro-cell, cache to disk, sample | drainage, road graph, POI layout, biome field | `WorldGraph` provider (below) |
| **Streamed-instances** | spawn meshes/impostors near player, despawn far | trees, POI structures, road segments, water meshes | a generic `ScatterStreamer` (new, shared) |

A module may use several tiers: e.g. **roads** = presolved graph (tier 2) → streamed road mesh
(tier 3); **flora** = per-chunk placement bake (tier 1) → streamed instances/impostors (tier 3).

## The module contract (one interface, every subsystem implements)

```
interface IWorldModule {
    string   Id { get; }              // registry id, e.g. "caves"
    bool     Enabled { get; }         // the master toggle (default in JSON)
    int      Budget { get; }          // declared ms budget at current quality tier
    void     OnQualityTier(Tier t);   // scale density/radius/resolution
    // tier 2 only:
    void     SolveMacroCell(WorldGraph.Cell cell);   // write solved record (off render thread)
    // tier 1/3:
    void     OnChunkBirth(ChunkKey k);   // throttled, render-thread async
    void     OnChunkRetire(ChunkKey k);  // free instances/buffers
}
```

**The ≈0-when-off guarantee:** if `Enabled` is false, the module registers no birth/solve callbacks
and allocates nothing — the dispatcher simply skips it. Underground modules add a second gate:
caves only run `OnChunkBirth` when the camera is below the surface within their radius, so the
surface frame pays nothing. This is the "not always active" the user asked for, made literal.

## The `WorldGraph` (the new shared keystone — tier-2 home)

A coarse macro-cell grid (cell size data-driven, e.g. 4 km) with:

- **Presolve scheduler** — velocity-predictive, throttled, off the render thread; mirrors the CDLOD
  loader. Solves cell + 1-cell skirt N tiles ahead of the camera.
- **Cache** keyed `(seed, cellXZ, version)` — disk + LRU memory. **Miss regenerates identically**
  (deterministic from seed), so the cache is an optimization, not a source of truth (No Man's Sky
  pattern). A `version` bump invalidates stale caches when a solver changes.
- **Solved record** per cell — a struct of provider outputs: `coarseHeight`, `flowAccum`,
  `channelMask`, `waterLevel`, `roadGraph`, `poiSlots`, `biomeField`. Providers (drainage, roads,
  POIs, biomes) each own one field; local modules **read** the nearest record.
- **`WorldDelta`** — sparse per-chunk edit store (digging, placed objects), applied over the
  regenerated base. Same persistence layer as the cache.

Providers register against the WorldGraph the same way controls register against the lab: a schema +
a solver. This keeps the "new feature = a registry line + a unit" rule intact even for global
systems.

## Registry extensions needed (small, additive)

1. **A `module` control type** — a master toggle bound to `IWorldModule.Enabled` + a budget readout.
   One row per module on a new **"World / Modules"** lab tab.
2. **A `tier`/`quality` global** — Ultra/High/Medium/Low enum that fans out to every module's
   `OnQualityTier` (drives the per-tier 8 ms target from `02`).
3. **Reuse `objectlist`** for POI definitions, road class definitions, biome definitions, flora
   species — each gets an `item_schemas.json` entry. No new type needed; this is the win of the
   existing design.
4. **A `provider` registration** (C#-side, not JSON) — solvers declare which solved-record field
   they write, so the WorldGraph can order them (biome → drainage → roads → POIs is the dependency
   chain) and skip disabled ones.

## Dependency order among providers (matters for correctness)

```
biomeField ──> drainage(moisture from flow) ──> roads(avoid water, link POIs) ──> POIs(placed last, claim slots)
        └────────────> flora(biome + moisture + slope) ───────────────────────────┘
caves: pure-function ambient + POI-anchored authored (runs after POIs claim slots)
```

The WorldGraph runs providers in this order per macro-cell; each reads the prior outputs from the
same solved record. This is why they must share one solve pass, not five independent ones — and why
`03`'s hybrid is the architecture, not five bolt-on systems.

## What this buys

- **Game-agnostic:** a consuming game enables only the modules it wants; the rest cost nothing.
- **Tunable/data-driven:** every module's density/size/rules live in JSON, hot-reloadable, with
  named presets via the existing preset machinery.
- **Budgeted:** each module declares its ms; the sum is checkable against the tier's 8 ms target;
  over-budget modules are visible, not mysterious.
- **One persistence story:** caches + edits + authored chunks all live in the WorldDelta/cache
  layer — no per-system save format.
- **Honors the discipline rule:** each module is built + eye-gated independently behind its toggle,
  defaulting off until it passes.
