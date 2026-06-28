# WG17 Control Surface — Config + UI + Addon Packaging (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `TerrainLabUI` god-class + its decomposition post-mortem
(memory `terrainlabui-decomposition`; `docs/superpowers/specs/2026-06-24-terrainlabui-decomposition-design.md`).
**Scope:** the engine's control surface — a single-source-of-truth config, a modular UI that renders from it,
and Godot-addon packaging so a game can drop the engine in lightly. Supersedes the migration plan's
"minimal driver per module" with one properly-built spine.

---

## 1. Why This Exists (the two WG16 failures it fixes)

WG17 is a **terrain engine that games consume to build procedural worlds.** It needs a control surface that
is (a) API-first so a game drives it in code, (b) backed by a dev/debug UI, and (c) packaged so a game adopts
it with little work. WG16 had a control surface (`TerrainLabUI`) that failed two ways:

1. **God-file.** ~3,200 LOC; decomposition got it to ~1,940 but hit an irreducible floor — a central
   panel-builder wiring hub + a giant `ApplyControl` type-switch + CLI appliers. Adding a control meant
   touching the central dispatch. Not modular.
2. **State drift.** The UI kept its OWN copy of "is this on" (the "cloud on-state mirrors, frame-loop-coupled"
   in the post-mortem). So a feature set by CLI or code didn't show as active in the UI, and vice versa —
   two sources of truth that disagreed. **This is the bug the user explicitly wants gone.**

**The fix (one idea):** a single `TerrainConfig` Resource is the ONLY source of truth for every feature toggle
and parameter. The engine reads it; the UI renders from it and writes to it; CLI and game code write it.
Any change emits a signal; the UI rebuilds the affected widget FROM the config. The UI holds no state of its
own, so it cannot disagree with reality. Modularity falls out: each feature owns a config block + a control
widget; the panel is a dumb host. The engine ships as a self-contained Godot addon.

---

## 2. Success Criteria

1. **Single source of truth.** A feature is "on" iff its config field says so. No subsystem or widget holds a
   second copy. CLI, game code, and UI all read+write the one `TerrainConfig`.
2. **UI reflects real state, always.** Set a feature via CLI/code → the UI shows it active. Toggle in the UI →
   the engine changes. Load a preset → every widget AND the world update together. No drift, ever.
3. **Modular, no god-file.** Adding a feature = add a `*Config` sub-resource + a `*Panel` (two small files),
   never edit a central dispatch. The `ControlPanel` host knows about no specific feature.
4. **API-first.** A consuming game can drive everything in code via `TerrainConfig` (set fields, load presets)
   without the UI. The engine does not depend on the UI; the UI depends on the engine.
5. **Lightly exportable.** The engine is a self-contained Godot addon: a game copies `addons/wg17_terrain/`,
   adds a `TerrainWorld` node, assigns a `TerrainConfig` (inspector or code) — done.
6. **Presets.** Save/load named configs as `.tres` resources; loading swaps the world's look in one assignment.

**Non-goals (designed-for, not built now):** a panel for every module (added as each subsystem integrates);
a product-grade player settings menu; controller/gamepad UI navigation; disk-watch preset hot-reload.

---

## 3. Architecture

```
                 CLI flags ─┐        ┌─ game code (engine API)
                            ▼        ▼
                    ┌───────────────────────────┐
   preset .tres ──► │  TerrainConfig (Resource) │   THE single source of truth
                    │  sub-configs + changed(key)│
                    └─────────────┬─────────────┘
                       emits `changed(key)`
                ┌──────────────────┴───────────────────┐
                ▼                                       ▼
          TerrainWorld (Node)                     ControlPanel (UI)
       reads config, builds + drives          renders FROM config, writes TO it
       subsystems; maps changed(key)→         (holds NO state of its own)
       the right subsystem call
```

**Addon layout (the drop-in unit):**
```
addons/wg17_terrain/
├── plugin.cfg                  # registers the addon (+ optionally an editor plugin for the panel)
├── config/
│   ├── TerrainConfig.cs        # Resource: holds the sub-configs; re-emits a unified changed(key) signal;
│   │                           #   serializes to .tres (presets); Clone(); ApplyOverride(key, value) for CLI.
│   ├── FieldConfig.cs          # Resource — field/gen params
│   ├── SurfacingConfig.cs      # Resource — palette + rule knobs + the surf_* toggles
│   ├── LightingConfig.cs       # Resource — time/sun/exposure/weather/grade
│   ├── SkyConfig.cs            # Resource — atmosphere/clouds/godrays enables + knobs
│   └── WaterConfig.cs          # Resource — placeholder until water migrates
├── runtime/
│   └── TerrainWorld.cs         # the node a game adds. [Export] TerrainConfig Config. Builds subsystems on
│                               #   ready; connects Config.changed → Route(key) switchboard → subsystem call.
├── ui/
│   ├── ControlPanel.cs         # thin host (~100 LOC): collects IModulePanel children, stacks them in tabs.
│   ├── IModulePanel.cs         # contract: BuildInto(VBox, TerrainConfig); RefreshFrom(TerrainConfig).
│   ├── FieldPanel.cs / SurfacingPanel.cs / LightingPanel.cs / SkyPanel.cs
│   ├── widgets/ConfigToggle.cs / ConfigSlider.cs / ConfigEnum.cs / ConfigColor.cs
│   └── PresetBar.cs            # save/load .tres preset dropdown
└── presets/Default.tres, Alpine.tres, …   # saved TerrainConfig snapshots

src/app/  (the dev project's own, NOT the addon)
└── Main.cs / main scene        # adds a TerrainWorld + (optionally) a ControlPanel; parses CLI → config
```

**Layering rules:**
- The engine (`runtime/`, the subsystems) reads `TerrainConfig` and NEVER holds a duplicate enable/param.
- The UI (`ui/`) reads+writes `TerrainConfig` and holds NO state — every widget is bound to a config key and
  re-reads on `changed`.
- `ControlPanel` knows no specific feature; module panels are self-contained and self-register.
- `addons/wg17_terrain/` references nothing outside itself (export self-containment).

---

## 4. The Config: single source of truth (the core mechanism)

`TerrainConfig` is a Godot `Resource` composed of sub-config `Resource`s (one per module). Key behaviors:

- **Every toggle + param is a property** on a sub-config (e.g. `Sky.CloudsEnabled : bool`, `Lighting.SunEnergy
  : float`, `Field.Seed : int`). Godot serializes the whole tree to `.tres` for free → presets.
- **`changed(StringName key)` signal.** Each sub-config emits when a property is set; `TerrainConfig` re-emits a
  unified `changed("sky.clouds_enabled")`. Setting a property is the ONLY way state changes — there is no other
  copy to update.
- **`ApplyOverride(string key, Variant value)`** — how CLI flags write the config (`--clouds=1` →
  `ApplyOverride("sky.clouds_enabled", true)`), so CLI uses the same path as everything else.
- **Clone() + Load/Save** — presets are `ResourceLoader.Load<TerrainConfig>(path)` / `ResourceSaver.Save`.

**Why this kills drift:** there is exactly one boolean for "clouds on." The engine enables clouds from it; the
UI checkbox reflects it; CLI sets it. Change it anywhere → `changed` fires → engine + UI both react from the
same value. The UI literally has nothing of its own to be stale.

---

## 5. The Engine Entry: TerrainWorld

`TerrainWorld : Node3D` is what a game adds to its scene. Responsibilities:
- `[Export] TerrainConfig Config` — assigned in the inspector or in code. On `_Ready`, builds the subsystems
  (Field/CDLOD, Lighting, Sky, Surfacing — whichever are present) configured FROM `Config`.
- Connects `Config.changed` → `Route(key)`: a thin switchboard mapping a changed key to the right subsystem
  call (`"sky.clouds_enabled" → _sky?.SetCloudsEnabled(Config.Sky.CloudsEnabled)`). This is the ONE place
  config maps to subsystems — a switchboard, not a god-dispatch, and it owns no state.
- Works with NO UI present (a game that never opens the panel still gets a fully configured world).

**Reuse:** a consuming game's entire integration is: copy the addon folder, add a `TerrainWorld`, assign a
`TerrainConfig` (or a preset `.tres`). Driving it later = set config properties / load a preset in code.

---

## 6. The UI: modular panels, render-from-config

- **`ControlPanel`** (thin host): finds the registered `IModulePanel`s, builds each into a tab, and on
  `Config.changed` calls each panel's `RefreshFrom(Config)`. ~100 LOC, feature-agnostic.
- **`IModulePanel`**: `void BuildInto(Container c, TerrainConfig cfg)` (create this module's widgets) +
  `void RefreshFrom(TerrainConfig cfg)` (re-read widget values from config — the anti-drift refresh).
- **Module panels** (`FieldPanel`, `LightingPanel`, `SkyPanel`, `SurfacingPanel`): each builds its own widgets
  bound to its config keys. Self-contained — a new module adds one panel, touching nothing central.
- **Widgets** (`ConfigToggle`/`ConfigSlider`/`ConfigEnum`/`ConfigColor`): each binds to a config key; on user
  input it WRITES the config (which fires `changed`); on `RefreshFrom` it READS the config. No widget stores
  authoritative state.
- **`PresetBar`**: dropdown of `presets/*.tres`; selecting one assigns it as the world's `Config` (or copies
  its values in) → one assignment updates engine + all widgets.

---

## 7. Testing & Gates

- **`ConfigSyncCheck` (`--configsynccheck`)** — the structural guard against the WG16 drift bug:
  1. CLI→UI: `Config.ApplyOverride("sky.clouds_enabled", true)`; assert the Sky subsystem enabled clouds AND
     the SkyPanel's clouds toggle reads checked.
  2. UI→engine: simulate the toggle writing `Config.Sky.CloudsEnabled = false`; assert the subsystem disabled.
  3. preset→all: load a preset `.tres`; assert every module panel's widgets AND every subsystem match it.
  Prints `CONFIG-SYNC PASS cli→ui=OK ui→engine=OK preset→all=OK`. A FAIL is the exact "doesn't show as
  activated" bug, caught in code.
- **`ExportCheck` (`--exportcheck`)** — scans `addons/wg17_terrain/` for `res://` references pointing OUTSIDE
  the addon folder; asserts none (the "drop one folder in" promise). Prints `EXPORT PASS self-contained=YES`.
- **Eye-gate (manual):** open the panel — toggle features and watch them turn on/off in the world; drag a
  slider and see the world change live; load a preset and watch every widget + the world update together;
  launch with a CLI flag (e.g. `--clouds=1`) and confirm the panel opens with that feature shown active.

---

## 8. Dependencies, Risks, Constraints

- **Depends on:** the shipped terrain slice (a subsystem to configure). Other subsystems (lighting/sky/
  surfacing) plug their config block + panel in as they integrate; this slice builds the SPINE + one module
  wired end-to-end as the proven pattern.
- **Supersedes:** the migration plan's "minimal driver per module" — `TerrainWorld`+config is that driver,
  done once and properly. Each staged slice (Lighting/Sky/Surfacing) gains a config block + panel on integration.
- **Risk — a subsystem keeping its own enable flag** (reintroducing drift): forbidden by §3 layering; the
  subsystem reads config. `ConfigSyncCheck` catches a divergence.
- **Risk — addon not actually self-contained** (breaks light export): `ExportCheck` guards it; keep all engine
  assets/shaders/scripts under `addons/wg17_terrain/`.
- **Risk — Godot C#/Resource gotchas:** `[Export]` sub-resources + signals from a Resource work but need care
  (resources are shared references — `Clone()` for presets to avoid mutating the original). Build after every
  `.cs` edit; absolute `--path`; bare `--` for CLI flags.

---

## 9. Definition of Done

- Engine is a Godot addon (`addons/wg17_terrain/`); a fresh project adopts it via one folder + a `TerrainWorld`
  node + a `TerrainConfig` (`ExportCheck` PASS).
- `TerrainConfig` is the sole source of truth; CLI, game code, UI, and presets all flow through it.
- UI reflects real state in all directions (`ConfigSyncCheck` PASS cli→ui, ui→engine, preset→all).
- Modular: `ControlPanel` is feature-agnostic; one module fully wired (config block + panel + subsystem route)
  as the pattern; adding a module touches no central dispatch.
- Engine runs with the UI absent. Presets save/load as `.tres`.
- Clean focused commits; spec/plan ~500-line files. Migration record updated.
