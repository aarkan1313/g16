# 03 · World-Architecture Fork (the decision that gates everything)

**The single most important decision in this dossier.** Caves, digging, roads contiguous across
infinity, handcrafted POIs, presolved drainage — these collide with *pure* infinite streaming in
different ways. How we resolve that collision determines how every subsystem below is built. The
user's steer: **prefer infinite; accept prebake/presolve/cache freely; bounded-world only as a
last resort — but analyze it honestly.** This doc does that, then recommends.

## The core tension

WG16's terrain today is a **pure function of position** (`field_height(x,z)` → no state, no
history, infinite, deterministic). That's why CDLOD streaming works: any chunk can be regenerated
from its coordinates alone, so there's nothing to store or stream except the camera-local window.

Every feature the user wants breaks the pure-function property in one of three ways:

| Feature | What breaks purity |
|---|---|
| **Drainage / rivers** | A cell's water depends on the whole upstream basin — a **global, order-dependent solve**, not a local function. |
| **Roads** | A road from A to B depends on A, B, and the terrain *between* them — **non-local pathfinding**, and must be *contiguous* (the same road across many chunks). |
| **POIs** | Placement is global (spacing, biome rules, road links); handcrafted chunks are **authored data**, not generated. |
| **Caves / tunnels** | 3D geometry, not a heightfield; large memory; tunnels are non-local paths like roads. |
| **Digging / deformation** | Player **edits** = persistent deltas = state that must be stored and survive regeneration. |

So the real question isn't "infinite vs bounded." It's: **where is the boundary between what's a
pure local function (regenerate freely) and what's a presolved/persisted global structure (compute
once, store, stream)?** Three architectures answer it differently.

## Option A — Pure infinite streaming (today, extended)

Everything stays a pure function of position. Global systems are **approximated locally**: rivers
from per-chunk flow synthesis (no true basin solve), roads from local heuristics, POIs from
hash-based placement, caves from 3D noise SDF. No persistence except a small player-edit delta.

- **Pros:** truly infinite; tiny memory; matches the proven CDLOD spine exactly; no spawn wait.
- **Cons:** global correctness is faked. Rivers won't obey real basins (the exact WG15 failure —
  "didn't make sense when water was added"). Roads can't *plan* A→B across regions. Handcrafted
  POIs are awkward (authored data isn't a function of position). Digging needs a delta store anyway.
- **Verdict:** great for terrain/flora/caves-as-noise; **structurally wrong for drainage, planned
  roads, and curated POIs.** Keeps the things that hurt WG15.

## Option B — Bounded world, generated at spawn (Factorio model)

Pick a world size at spawn (e.g. 16×16 km up to 64×64 km), run all **global presolves once**
(drainage, road graph, POI placement, biome map) into a cached world-state, then stream from that
cache. Factorio, Valheim (seed→bounded), Dwarf Fortress (worldgen pass) all do this.

- **Pros:** **every global system becomes correct and cheap** — drainage solved on the real full
  field, road network planned end-to-end, POIs placed with global constraints, all baked. Per-frame
  cost drops because the hard work is presolved. Digging/edits persist naturally (there's a world
  store). This is the **only option where "all features ON, correct, under 8 ms" is clearly
  achievable** — the 8 ms then pays for *rendering* a presolved world, not *solving* it live.
- **Cons:** not infinite; a spawn-time generation pass (seconds→minutes by size); a world-state
  store (disk + memory). The user explicitly wants to avoid this **except as last resort.**
- **Verdict:** the safe AAA answer; the honest fallback if Option C can't hold quality.

## Option C — Hybrid: infinite terrain + bounded "solved regions" (RECOMMENDED)

Keep the infinite pure-function terrain. Layer global systems as a **coarse, lazily-materialized
region grid** on top — large tiles (e.g. 4×4 km "macro-cells") that, *on first approach*, run a
bounded presolve for that tile and its neighbors, then cache the result to disk. The world stays
infinite; correctness is bounded to a tile + skirt; the presolve amortizes over exploration instead
of all-at-once at spawn.

This is the No Man's Sky pattern (generate-on-first-visit + cache + deterministic seed so a cache
miss regenerates identically) crossed with hierarchical solving: **global structure is solved at
macro-cell resolution; local detail is the existing per-chunk pure function.**

- **How each system maps:**
  - **Drainage:** solve flow/basins per macro-cell at coarse res (e.g. 256² over 4 km = 16 m/cell)
    with a one-cell skirt for cross-border continuity; per-chunk detail carves toward the cached
    coarse bed (the proven "carve toward monotonic bed" method). Basins are correct *within a
    macro-cell*; cross-tile rivers stitch at skirts (good enough; true ocean-scale basins are the
    one thing only Option B nails — acceptable).
  - **Roads:** plan the road graph per macro-cell over the cached coarse height + POI anchors; A*
    with terrain-cost; stitch at tile borders via deterministic border waypoints (both tiles agree
    on the crossing point from the shared seed). Contiguous, planned, infinite.
  - **POIs:** deterministic candidate placement per macro-cell (Poisson/hash) + biome/road rules +
    **handcrafted chunks dropped as authored tiles** keyed to candidate slots. Curated content in
    an infinite world.
  - **Caves:** 3D SDF as a pure function (infinite, free) for ambient caves; macro-cell-anchored
    *authored* cave systems for POIs. Both stream as sparse 3D chunks only where the player is.
  - **Digging/edits:** a sparse world-delta store keyed by chunk coord, applied over the regenerated
    base. Same store the macro-cell caches live in.
- **Pros:** infinite AND globally-correct-enough; presolve cost is bounded + amortized + cached;
  one persistence layer serves caches + edits; degrades gracefully (a tile not yet solved shows the
  pure-function approximation until its presolve lands).
- **Cons:** the most engineering (a macro-cell solve+cache scheduler, a skirt-stitch protocol, a
  delta store). A brief "popcorn" risk if a presolve lands visibly — mitigated by solving N tiles
  ahead (the same velocity-predictive loading CDLOD already does) and by the pure-function fallback
  underneath.

## Recommendation

**Build Option C (hybrid).** It's the only one that satisfies all three of the user's stated wants
— infinite, all-features-correct, under 8 ms — and it's a *superset*: at macro-cell size = world
size it degenerates to Option B (bounded), and with presolves disabled it degenerates to Option A
(pure infinite). So we don't have to choose finally now — **we build the macro-cell + cache +
delta-store infrastructure once, and each subsystem picks its tier** (pure function, or
macro-cell-presolved, or authored). That infrastructure is `04-module-knobs-framework.md`'s job.

**Fallback rule:** if a specific system's quality can't hold at hybrid macro-cell resolution within
budget (most likely: ocean-scale river basins, or very dense cave networks), that *one system*
falls back to Option B semantics behind a "bounded world" toggle — not the whole engine. The user's
"last resort" is thus scoped to individual modules, not the architecture.

## What this means concretely (the one new keystone to build)

A **`WorldGraph`** layer: a coarse macro-cell grid (size data-driven), a presolve scheduler
(velocity-predictive, render-thread-friendly like `ChunkFieldCache`), a disk cache keyed by
`(seed, macroCell, version)` that regenerates identically on miss, and a sparse **`WorldDelta`**
store for edits. Every global system (drainage, roads, POIs, biomes) is a **provider** that writes
into a macro-cell's solved record; every local system (terrain detail, flora, ambient caves) stays a
pure per-chunk function that *reads* the nearest solved record. This is the single piece of new
infrastructure the whole dossier depends on. See `04` for the module/provider contract and `00` for
where it sequences (it's the Phase-B keystone, alongside the existing CDLOD spine).
