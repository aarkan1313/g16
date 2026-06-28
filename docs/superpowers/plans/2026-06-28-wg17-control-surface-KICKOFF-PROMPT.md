# WG17 Control Surface — New-Chat Kickoff Prompt

Paste the block below into a fresh Claude Code chat (ideally opened in `C:\Wg16\WG17\terrainengine-10k`).

**Godot 4.6 binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`

---

```
We're building the CONTROL SURFACE for my Godot terrain engine in WG17 (C:\Wg16\WG17\terrainengine-10k),
Godot 4.6/C#. WG17 is a terrain ENGINE that games consume to build procedural worlds. WG16's control UI
(TerrainLabUI) failed two ways we're fixing: (1) it was a ~3200-LOC GOD-FILE with a central apply-dispatch;
(2) it had STATE DRIFT — the UI kept its own copy of "is this on", so a feature set by CLI/code didn't show
as active in the UI and vice versa.

The fix (one idea): a single TerrainConfig Resource is the SOLE source of truth for every toggle + param. The
engine READS it (holds no duplicate); the UI RENDERS FROM it and WRITES TO it (holds no state); CLI + game
code + presets all WRITE it. Any change emits a signal; the UI re-reads. The UI literally cannot drift because
it has no state of its own. The whole engine ships as a Godot ADDON (addons/wg17_terrain/) so a game drops in
one folder + a TerrainWorld node + a TerrainConfig — done.

Read in order:
1. Spec:   C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-control-surface-design.md
2. Plan 1 (config core + TerrainWorld + addon packaging):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-control-surface-plan1-config-core.md
3. Plan 2 (modular UI + ConfigSyncCheck + ExportCheck):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-control-surface-plan2-ui.md

Execute Plan 1 then Plan 2 task-by-task via superpowers:subagent-driven-development.

Hard rules:
- SINGLE SOURCE OF TRUTH: every toggle/param is a property on a TerrainConfig sub-config. The engine reads it,
  holds NO duplicate. The UI binds widgets to config keys: write-on-input, read-on-RefreshFrom. No second copy
  anywhere. This is what makes "UI reflects real state" true by construction.
- MODULAR: ControlPanel is a dumb feature-agnostic host; each module = one *Config + one *Panel (two small
  files). NO central apply-dispatch. Adding a feature touches nothing central.
- ADDON: everything under addons/wg17_terrain/, self-contained (ExportCheck asserts no external res:// refs).
- ENGINE RUNS WITHOUT THE UI (UI depends on engine, never reverse).
- dotnet build after every .cs edit; absolute --path; bare -- for flags. Resources are shared refs — Clone()
  deep for presets.

My gates: --configsynccheck must PASS (cli→ui, ui→engine, preset→all — this passing IS the proof the drift bug
is gone); --exportcheck must PASS (self-contained=YES). Eye-gate: open the panel, toggle features (world
reacts), drag sliders (live), load a preset (everything updates together), and relaunch with `-- --clouds=1`
to confirm the panel opens with clouds already shown ACTIVE. Record a frame-ms number.

NOTE: this supersedes the old "minimal driver per module" idea — TerrainWorld+config IS that driver, done
once and properly. Wire the shipped Field/CDLOD subsystem for real; null-safe stubs for not-yet-integrated
subsystems (lighting/sky/surfacing) so the route keys + drift test work; those modules add their config block
+ panel when they integrate.

Confirm you've read the spec + 2 plans, then show me your plan for Plan 1 Task 1 before executing.
```

---

## Notes for me (not the new chat)
- Two plans: config core/TerrainWorld/addon (the engine spine + API), then the modular UI + the two gates.
- The whole point: ConfigSyncCheck passing = the user's drift bug is structurally impossible. ExportCheck = drops into a game as one folder.
- This is the engine's product surface, so quality of the API/config matters as much as the look.
- Each future module (lighting/sky/surfacing/water) gains a *Config block + a *Panel when it integrates — that's the modularity paying off. The shipped terrain wires in for real now.
- Touches src/app + adds addons/wg17_terrain/ — coordinate if another chat is mid-edit in src/.
```
