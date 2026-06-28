# Slice X — Control Surface (Config + UI + Addon)  🟡 STAGED

**What:** the engine's control surface — a single-source-of-truth config, a modular UI that renders from it,
and Godot-addon packaging so a game adopts the engine lightly. Supersedes the old "minimal driver per module".

**Docs:**
- Spec: [../specs/2026-06-28-wg17-control-surface-design.md](../specs/2026-06-28-wg17-control-surface-design.md)
- Plans: [config core + TerrainWorld + addon](../plans/2026-06-28-wg17-control-surface-plan1-config-core.md) ·
  [modular UI + gates](../plans/2026-06-28-wg17-control-surface-plan2-ui.md)
- Kickoff: [../plans/2026-06-28-wg17-control-surface-KICKOFF-PROMPT.md](../plans/2026-06-28-wg17-control-surface-KICKOFF-PROMPT.md)

**Fixes WG16's two harness failures:**
1. **God-file** → modular: one `TerrainConfig` Resource (per-module sub-configs); each feature = one `*Config`
   block + one `*Panel`; `ControlPanel` is a dumb feature-agnostic host. No central apply-dispatch.
2. **State drift** ("set by CLI but UI doesn't show active, & vice versa") → `TerrainConfig` is the SOLE source
   of truth. Engine reads it (no duplicate state); UI renders FROM it + writes TO it (no state of its own);
   CLI + game code + presets all write it; `changed` signal re-reads the UI. The UI literally can't drift.

**API-first + exportable:** the engine ships as `addons/wg17_terrain/` — a game copies one folder, adds a
`TerrainWorld` node, assigns a `TerrainConfig` (inspector or code). The UI is optional; the engine runs
without it. `TerrainWorld.Route(key)` is the one thin config→subsystem switchboard.

**Gates:** `ConfigSyncCheck` (cli→ui, ui→engine, preset→all — passing = the drift bug is structurally
impossible) + `ExportCheck` (addon has no external `res://` refs = drop-in works). Presets = `.tres` configs.

**Integration:** each module (lighting/sky/surfacing/water) gains a config block + panel as it integrates —
the modularity paying off. The shipped Field/CDLOD + Lighting wire in for real first.
