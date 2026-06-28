# WG16 AAA-Engine Research Dossier — 2026-06-28

A grounded-but-ambitious research dossier + master roadmap + per-subsystem specs for taking WG16
toward a best-in-class, **game-agnostic, data-driven, modular** AAA world engine inside the **8 ms**
budget — extending the existing Phase A→B→C structure and the discipline rule. Design-ahead, not
build-ahead. Each file is kept under ~300 lines.

## Read order
1. [`01-current-state-audit.md`](01-current-state-audit.md) — honest baseline: what's shipped /
   mockup / missing, and the real perf state (~10 ms flying, over budget, before new features).
2. [`03-world-architecture-fork.md`](03-world-architecture-fork.md) — **the decision that gates
   everything**: infinite vs bounded (Factorio) vs **hybrid (recommended)**; the new `WorldGraph`.
3. [`02-performance-strategy.md`](02-performance-strategy.md) — how "all features on, medium-high,
   under 8 ms" closes: pay-once/cache/presolve + per-module budget allocation.
4. [`04-module-knobs-framework.md`](04-module-knobs-framework.md) — the always-off heavy-module
   infra: tiers, the `IWorldModule`/`IWorldProvider` contract, the WorldGraph + WorldDelta.
5. Per-subsystem `research.md` (techniques + AAA citations) + `spec.md` (units, gates, STOP):
   - [`biomes-macro-variety/`](biomes-macro-variety/) — the keystone's first provider (regions differ)
   - [`pois-integration/`](pois-integration/) — **the integration/constraint layer** + the POI system
   - [`roads-paths/`](roads-paths/) — contiguous, conforming, trail→highway
   - [`trees-flora/`](trees-flora/) — realistic procedural flora + impostor LOD
   - [`caves-underground/`](caves-underground/) — SDF/dual-contouring caves + digging (highest risk)
   - [`weather/`](weather/) — dynamic weather + wind + precipitation + wetness (deepen existing)
6. [`00-MASTER-ROADMAP.md`](00-MASTER-ROADMAP.md) — **the capstone**: dependency graph, Phase A/B/C
   sequencing, running budget table, immediate next actions.

## The three big takeaways
- **Architecture:** build the **hybrid** (`03`) — infinite terrain + lazily-presolved/cached
  macro-cells for global systems. It's a superset of bounded + pure-infinite, so the choice isn't
  final and each module picks its tier.
- **Performance:** the cheap dials are exhausted; the path under 8 ms is **architectural** — pay
  once (bake/cache/presolve), budget every module, throttle birth work (`02`).
- **Cohesion:** what makes a *world* (not five generators) is the **integration layer** —
  one per-macro-cell solved record + footprint masks, providers in a fixed dependency order
  (biome→drainage→roads→POIs, flora reads all). Build it as the Phase-B keystone (`pois-integration`).

## Status
This is a planning artifact. Nothing here is built. Each spec is gated and default-off by design;
building proceeds one phase past the last passed eye-gate, per the discipline rule.
