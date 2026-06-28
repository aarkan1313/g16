# WG17 Control Surface — Plan 2 of 2: Modular UI + Anti-Drift & Export Gates

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the modular control UI that renders FROM the config and writes TO it (holding no state of its own), with a feature-agnostic host and per-module panels — plus the `ConfigSyncCheck` (the structural guard against state drift) and `ExportCheck` (self-containment).

**Architecture:** `ControlPanel` is a dumb host that stacks `IModulePanel`s in tabs and calls `RefreshFrom(config)` on `Config.changed`. Each module panel builds widgets bound to its config keys; widgets WRITE config on input and READ config on refresh. No widget stores authoritative state → the UI cannot drift from reality.

**Tech Stack:** Godot 4.6 / C#, Control nodes, `TerrainConfig` (Plan 1).

**Plan set:** Plan 1 = config core + TerrainWorld + addon (prerequisite). **Plan 2 (this) = UI + checks.**

**Reference:** Spec `…specs/2026-06-28-wg17-control-surface-design.md` (§6 UI, §7 gates). Consumes Plan 1's `TerrainConfig`/sub-configs/`TerrainWorld`.

## Global Constraints

- **Target repo:** `C:\Wg16\WG17\terrainengine-10k`. **Addon root:** `addons/wg17_terrain/ui/`. **Namespace:** `Te10k.Engine.Ui` (+ `Te10k.Engine.Checks`).
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild after every .cs edit; absolute `--path`; bare `--` for flags.**
- **THE UI RULE:** the UI holds NO authoritative state. Every widget binds to a config key: user input WRITES the config (fires `changed`); `RefreshFrom(config)` READS the config back into the widget. `ControlPanel` knows no specific feature. This is what makes "UI reflects real state" true by construction.
- **Engine independence:** the engine (Plan 1) must run with NO UI present. UI depends on engine, never reverse.
- **Depends on Plan 1** (config + TerrainWorld + addon exist).

---

## File Structure (this plan)

```
addons/wg17_terrain/ui/IModulePanel.cs        # Task 1 — the panel contract
addons/wg17_terrain/ui/widgets/ConfigToggle.cs, ConfigSlider.cs, ConfigEnum.cs, ConfigColor.cs  # Task 1
addons/wg17_terrain/ui/ControlPanel.cs        # Task 2 — dumb host: tabs + RefreshFrom on changed
addons/wg17_terrain/ui/FieldPanel.cs          # Task 3 — the proven module panel (end-to-end)
addons/wg17_terrain/ui/SkyPanel.cs            # Task 3 — toggles (for the drift test)
addons/wg17_terrain/ui/PresetBar.cs           # Task 4 — preset dropdown
addons/wg17_terrain/checks/ConfigSyncCheck.cs # Task 5 — anti-drift gate
addons/wg17_terrain/checks/ExportCheck.cs     # Task 6 — self-containment gate
```

---

### Task 1: Panel contract + config-bound widgets

**Files:** Create `ui/IModulePanel.cs`, `ui/widgets/{ConfigToggle,ConfigSlider,ConfigEnum,ConfigColor}.cs`

**Interfaces:**
- Consumes: `TerrainConfig` + sub-configs (Plan 1).
- Produces: `interface IModulePanel { string Title { get; } void BuildInto(Control container, TerrainConfig cfg); void RefreshFrom(TerrainConfig cfg); }`; widgets each with `Bind(TerrainConfig cfg, string key, getter, setter)` that WRITE on input and READ on `RefreshFrom`.

- [ ] **Step 1: Write IModulePanel.cs**

```csharp
namespace Te10k.Engine.Ui;
using Godot; using Te10k.Engine;
public interface IModulePanel
{
    string Title { get; }
    void BuildInto(Control container, TerrainConfig cfg);
    void RefreshFrom(TerrainConfig cfg);   // re-read widget values from config (anti-drift)
}
```

- [ ] **Step 2: Write ConfigToggle (the key widget for the drift bug)**

A `CheckBox` wrapper: `Bind(Func<bool> get, Action<bool> set)`. On `Toggled` → `set(pressed)` (writes config → fires changed). `RefreshFrom` → `ButtonPressed = get()` WITHOUT re-emitting (set a guard so refresh doesn't loop). This two-way binding with a refresh-from-source is the anti-drift core. Mirror for `ConfigSlider` (HSlider + value label), `ConfigEnum` (OptionButton), `ConfigColor` (ColorPickerButton).

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add addons/wg17_terrain/ui/IModulePanel.cs addons/wg17_terrain/ui/widgets/*.cs
git commit -m "feat(ui): IModulePanel contract + config-bound widgets (write-on-input, read-on-refresh)"
```

---

### Task 2: ControlPanel — the dumb host

**Files:** Create `ui/ControlPanel.cs`

**Interfaces:**
- Consumes: `IModulePanel`, `TerrainConfig`.
- Produces: `partial class ControlPanel : Control` with `[Export] NodePath WorldPath` (to get the `TerrainWorld`'s `Config`); registers a list of `IModulePanel`s, builds each into a `TabContainer` tab, and on `Config.ChangedKey` calls every panel's `RefreshFrom(cfg)`.

- [ ] **Step 1: Write ControlPanel.cs**

On `_Ready`: get the `TerrainWorld` via `WorldPath`, grab its `Config`; create a `TabContainer`; for each registered `IModulePanel`, add a tab and call `BuildInto(tab, cfg)`; connect `cfg.ChangedKey += _ => RefreshAll()` where `RefreshAll` calls `RefreshFrom(cfg)` on every panel. The host knows NOTHING about specific features — it just hosts panels. Keep it ~100 LOC.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add addons/wg17_terrain/ui/ControlPanel.cs
git commit -m "feat(ui): ControlPanel dumb host — tabs + RefreshFrom on config.changed"
```

---

### Task 3: The proven module panels (Field + Sky)

**Files:** Create `ui/FieldPanel.cs`, `ui/SkyPanel.cs`

**Interfaces:**
- Consumes: widgets (Task 1), `FieldConfig`/`SkyConfig` (Plan 1).
- Produces: `FieldPanel : IModulePanel` (sliders for Seed/AmplitudeM bound to `cfg.Field`) and `SkyPanel : IModulePanel` (toggles for Clouds/Atmosphere/Godrays bound to `cfg.Sky`). Registered with `ControlPanel`.

- [ ] **Step 1: Write FieldPanel + SkyPanel**

`FieldPanel.BuildInto`: add a `ConfigSlider` bound to `() => cfg.Field.AmplitudeM` / `v => cfg.Field.AmplitudeM = v`, and one for Seed. `RefreshFrom`: call each widget's refresh. `SkyPanel.BuildInto`: a `ConfigToggle` per `cfg.Sky.CloudsEnabled` / `AtmosphereEnabled` / `GodraysEnabled`. Register both in `ControlPanel` (a small static list or an `[Export]` of panel scenes — keep it explicit and simple).

- [ ] **Step 2: Wire ControlPanel into the scene + EYE-GATE the round-trip**

Add a `ControlPanel` (CanvasLayer + Control) to the main scene pointing at the `TerrainWorld`. Build + launch:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected: panel shows tabs (Field, Sky); dragging the AmplitudeM slider changes the terrain live; toggling a Sky checkbox flips the (null-safe) subsystem; the widgets reflect the config.

- [ ] **Step 3: Commit**

```bash
git add addons/wg17_terrain/ui/FieldPanel.cs addons/wg17_terrain/ui/SkyPanel.cs
git commit -m "feat(ui): Field + Sky module panels (config-bound, registered with host)"
```

---

### Task 4: PresetBar (save/load .tres in the UI)

**Files:** Create `ui/PresetBar.cs`

**Interfaces:**
- Consumes: `TerrainConfig.Save/Load` (Plan 1), the `TerrainWorld`.
- Produces: `PresetBar : Control` — a dropdown of `presets/*.tres` + Save/Load buttons; loading assigns the preset's values into the world's `Config` (one assignment → `changed` fires per key → engine + all widgets update).

- [ ] **Step 1: Write PresetBar.cs**

Scan `res://addons/wg17_terrain/presets/` for `.tres`; populate an `OptionButton`. Load: `TerrainConfig.Load(path)` then copy its values into the live config (or replace the world's Config + re-run ApplyAll + RefreshAll) so one action updates everything. Save: `config.Save(presets/<name>.tres)`.

- [ ] **Step 2: Build + EYE-GATE preset load**

Launch; load `Default` → confirm every widget AND the world update together from the one load. Commit:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add addons/wg17_terrain/ui/PresetBar.cs
git commit -m "feat(ui): PresetBar — load/save .tres updates engine + all widgets together"
```

---

### Task 5: ConfigSyncCheck — the anti-drift gate

**Files:** Create `addons/wg17_terrain/checks/ConfigSyncCheck.cs`

**Interfaces:**
- Consumes: `TerrainConfig`, `TerrainWorld`, the panels.
- Produces: `static bool Run(TerrainWorld world, ControlPanel panel)` — the three-way assertion from spec §7.

- [ ] **Step 1: Write ConfigSyncCheck.cs**

```
1. CLI→UI:  world.Config.ApplyOverride("sky.clouds_enabled", true);
            assert the Sky subsystem is enabled (or the route ran) AND the SkyPanel's clouds toggle reads checked.
2. UI→engine: set world.Config.Sky.CloudsEnabled = false (simulating the widget write);
            assert the subsystem disabled.
3. preset→all: var p = TerrainConfig.Load(".../Default.tres"); apply it;
            assert every panel widget AND every routed subsystem value equals the preset.
```
Print `CONFIG-SYNC PASS cli→ui=OK ui→engine=OK preset→all=OK` (or FAIL with which leg broke). Wire `--configsynccheck`.

- [ ] **Step 2: Run**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --configsynccheck
```
Expected: `CONFIG-SYNC PASS cli→ui=OK ui→engine=OK preset→all=OK`, exit 0. **This passing IS the proof the WG16 drift bug is gone.**

- [ ] **Step 3: Commit**

```bash
git add addons/wg17_terrain/checks/ConfigSyncCheck.cs
git commit -m "feat(engine): ConfigSyncCheck — UI⇄config⇄engine never drift (PASS)"
```

---

### Task 6: ExportCheck + the addon drop-in verification

**Files:** Create `addons/wg17_terrain/checks/ExportCheck.cs`

**Interfaces:**
- Produces: `static bool Run()` — scans `addons/wg17_terrain/` source/scene/shader files for `res://` references that point OUTSIDE the addon folder; asserts none. Prints `EXPORT PASS self-contained=YES` or lists the offending external refs.

- [ ] **Step 1: Write ExportCheck.cs**

Walk `res://addons/wg17_terrain/` files; regex `res://` paths; assert each resolved path starts with `res://addons/wg17_terrain/`. Any external ref = FAIL with the file + ref printed (so they can be moved into the addon). Wire `--exportcheck`.

- [ ] **Step 2: Run + fix any external refs**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --exportcheck
```
Expected: `EXPORT PASS self-contained=YES`. If FAIL, move the referenced asset/script under the addon and re-run.

- [ ] **Step 3: EYE-GATE the full surface + slice marker + migration note**

Launch the panel: toggle features (watch the world react), drag sliders (live), load a preset (everything updates together), and relaunch with `-- --clouds=1` to confirm the panel opens showing clouds already active (the headline anti-drift demo). Record frame ms (UI overhead trivial). Commit + append a "WG17 control surface outcome" note to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md`.
```bash
git add addons/wg17_terrain/checks/ExportCheck.cs
git commit -m "feat(engine): ExportCheck self-containment + control-surface eye-gate PASS"
git commit --allow-empty -m "milestone(control-surface): config-driven UI, no drift, drop-in addon"
```

---

## Self-Review

**Spec coverage (Plan 2 = UI + gates):** §6 ControlPanel dumb host + IModulePanel + config-bound widgets + module panels + PresetBar → Tasks 1–4; §7 ConfigSyncCheck (cli→ui, ui→engine, preset→all) → Task 5; ExportCheck self-containment → Task 6; eye-gate (toggle/slider/preset/CLI-shows-active) → Task 6 Step 3; §2 UI-holds-no-state + engine-runs-without-UI → Tasks 1–2 (binding rule) + Plan 1 (engine independence). ✓

**Placeholder scan:** widget behaviors specified (write-on-input, read-on-refresh, refresh-guard against loop); the three ConfigSyncCheck legs are concrete assertions, not "test sync". `.../` abbreviates the Godot path. No TBD. ✓

**Type consistency:** `IModulePanel` (Title/BuildInto/RefreshFrom) consistent across host + panels; widgets bind to `cfg.Field.*`/`cfg.Sky.*` matching Plan 1 sub-config properties; `ConfigSyncCheck` keys (`"sky.clouds_enabled"`) match Plan 1's Route cases + ApplyOverride. `TerrainConfig.Save/Load` matches Plan 1. ✓

**The point:** Task 5's `ConfigSyncCheck` passing is the literal proof that the user's reported bug ("set by CLI but UI doesn't show active, and vice versa") cannot happen — because the UI renders from the one config the engine runs from, with no second copy. Task 6 proves the engine drops into a game as one self-contained folder.
