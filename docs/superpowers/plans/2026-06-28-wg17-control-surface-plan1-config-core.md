# WG17 Control Surface — Plan 1 of 2: Config Core + TerrainWorld + Addon Packaging

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the API/engine spine — the single-source-of-truth `TerrainConfig` Resource (with per-module sub-configs + a unified `changed` signal), the `TerrainWorld` node that reads it and routes changes to subsystems, CLI→config override, preset save/load, and the Godot-addon packaging so a game adopts it lightly.

**Architecture:** One `TerrainConfig` Resource is the sole source of truth. The engine reads it and holds no duplicate state. `TerrainWorld` connects `Config.changed → Route(key)`, a thin switchboard mapping a changed key to one subsystem call. CLI flags and presets write the same config. Everything ships under `addons/wg17_terrain/`.

**Tech Stack:** Godot 4.6 / C#, Godot `Resource` (.tres) + signals.

**Plan set (control surface = 2 plans):** **Plan 1 (this) = config core + TerrainWorld + addon + checks-spine.** Plan 2 = UI panels + widgets + ConfigSyncCheck/ExportCheck.

**Reference:** Spec `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-control-surface-design.md`.

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k`. **Addon root:** `addons/wg17_terrain/`. **Namespace:** `Te10k.Engine` (config+runtime), `Te10k.Engine.Checks`.
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild:** `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit. **Launch:** absolute `--path`; bare `--` for flags.
- **SINGLE SOURCE OF TRUTH (the rule):** every feature toggle + param is a property on a sub-config. The engine READS config and holds NO duplicate enable/param. State changes ONLY by setting a config property, which fires `changed`. No second copy anywhere.
- **Self-contained addon:** everything the engine needs lives under `addons/wg17_terrain/`; no `res://` refs pointing outside it (ExportCheck in Plan 2 enforces).
- **Resource gotcha:** Godot Resources are shared references — `Clone()` (deep) before mutating for presets, so loading a preset doesn't mutate the original `.tres`.
- **Depends on:** the shipped terrain slice (the subsystem to configure). This plan integrates the Field/CDLOD subsystem as the one proven module; others plug in later.

---

## File Structure (this plan)

```
addons/wg17_terrain/plugin.cfg                  # Task 6 — addon manifest
addons/wg17_terrain/config/FieldConfig.cs       # Task 2 — field sub-config (the proven module)
addons/wg17_terrain/config/SkyConfig.cs         # Task 2 — sky sub-config (toggles for the drift test)
addons/wg17_terrain/config/LightingConfig.cs    # Task 2 — lighting sub-config (stub fields ok)
addons/wg17_terrain/config/SurfacingConfig.cs   # Task 2 — surfacing sub-config (stub fields ok)
addons/wg17_terrain/config/TerrainConfig.cs     # Task 1 — root config: holds sub-configs, unified changed(key), ApplyOverride, Clone, Save/Load
addons/wg17_terrain/runtime/TerrainWorld.cs     # Task 3 — engine entry node: reads config, builds subsystems, Route(key) switchboard
addons/wg17_terrain/presets/Default.tres        # Task 5 — a saved config
src/app/Main.cs (or modify the main scene)      # Task 4 — adds TerrainWorld; parses CLI → config
```

---

### Task 1: TerrainConfig root resource + unified changed signal

**Files:** Create `addons/wg17_terrain/config/TerrainConfig.cs`

**Interfaces:**
- Produces: `partial class TerrainConfig : Resource` with `[Export] FieldConfig Field`, `[Export] SkyConfig Sky`, `[Export] LightingConfig Lighting`, `[Export] SurfacingConfig Surfacing`; `[Signal] delegate void ChangedKeyEventHandler(string key)`; `void ApplyOverride(string key, Variant value)`; `TerrainConfig Clone()`; `static TerrainConfig Load(string path)`; `void Save(string path)`. Subscribes to each sub-config's own `changed` and re-emits `ChangedKey($"{module}.{key}")`.

- [ ] **Step 1: Write TerrainConfig.cs**

Root resource holding the four sub-config resources (created in Task 2 — write this first with the wiring, it'll compile once Task 2 lands; do Task 2 immediately after). On `_Init`/ctor, instantiate sub-configs if null and connect each sub-config's `Changed(key)` to a handler that re-emits `EmitSignal(SignalName.ChangedKey, $"{module}.{key}")`. `ApplyOverride(key, value)`: split `"module.field"`, route to the sub-config's setter. `Clone()`: `(TerrainConfig)Duplicate(true)` (deep). `Load`/`Save`: `ResourceLoader.Load`/`ResourceSaver.Save`.

- [ ] **Step 2: Build (will fail until Task 2 — expected)**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: FAIL (FieldConfig etc. undefined). Proceed to Task 2.

---

### Task 2: Per-module sub-configs

**Files:** Create `addons/wg17_terrain/config/{FieldConfig,SkyConfig,LightingConfig,SurfacingConfig}.cs`

**Interfaces:**
- Produces: four `partial class *Config : Resource`, each with `[Signal] delegate void ChangedEventHandler(string key)` and `[Export]` properties whose setters emit `Changed(nameof(prop))`. Minimum real fields: `FieldConfig.Seed:int`, `FieldConfig.AmplitudeM:float`; `SkyConfig.CloudsEnabled:bool`, `SkyConfig.AtmosphereEnabled:bool`, `SkyConfig.GodraysEnabled:bool`; `LightingConfig.TimeOfDay:float`, `SunEnergy:float`; `SurfacingConfig.Enabled:bool`.

- [ ] **Step 1: Write the four sub-config classes**

Each property uses an explicit backing field + setter that emits the `Changed` signal, e.g.:
```csharp
public partial class SkyConfig : Resource
{
    [Signal] public delegate void ChangedEventHandler(string key);
    private bool _cloudsEnabled;
    [Export] public bool CloudsEnabled { get => _cloudsEnabled;
        set { if (_cloudsEnabled == value) return; _cloudsEnabled = value; EmitSignal(SignalName.Changed, "clouds_enabled"); } }
    // … AtmosphereEnabled, GodraysEnabled likewise
}
```
Mirror the pattern for Field/Lighting/Surfacing. Keep fields minimal but real (enough to drive the proven module + the drift test).

- [ ] **Step 2: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded` (TerrainConfig + sub-configs now compile).

- [ ] **Step 3: Commit**

```bash
git add addons/wg17_terrain/config/*.cs && git commit -m "feat(engine): TerrainConfig single-source-of-truth + per-module sub-configs (changed signals)"
```

---

### Task 3: TerrainWorld — read config, build subsystems, Route switchboard

**Files:** Create `addons/wg17_terrain/runtime/TerrainWorld.cs`

**Interfaces:**
- Consumes: `TerrainConfig` (Task 1); the shipped terrain subsystem (Field/CDLOD `Terrain`/`TerrainSurfacer` etc.).
- Produces: `partial class TerrainWorld : Node3D` with `[Export] TerrainConfig Config`; on `_Ready` builds the subsystems FROM `Config` and connects `Config.ChangedKey += Route`; `void Route(string key)` switchboard mapping a key to one subsystem call. Holds NO duplicate state.

- [ ] **Step 1: Write TerrainWorld.cs**

```csharp
public partial class TerrainWorld : Node3D
{
    [Export] public TerrainConfig Config;
    // subsystem refs (the shipped terrain; others as they integrate)
    public override void _Ready()
    {
        Config ??= new TerrainConfig();
        BuildSubsystems();                 // construct Field/CDLOD etc. configured FROM Config
        Config.ChangedKey += Route;        // single connection point
        ApplyAll();                        // push current config to subsystems once
    }
    private void Route(string key)
    {
        switch (key)
        {
            case "field.seed":          _terrain.SetSeed(Config.Field.Seed); break;
            case "field.amplitude_m":   _terrain.SetAmplitude(Config.Field.AmplitudeM); break;
            case "surfacing.enabled":   _surfacer?.SetEnabled(Config.Surfacing.Enabled); break;
            case "sky.clouds_enabled":  _sky?.SetCloudsEnabled(Config.Sky.CloudsEnabled); break;
            // … one line per key; the ONLY config→subsystem map. A switchboard, not a god-dispatch.
        }
    }
    private void ApplyAll() { /* call every Route case once from current Config */ }
}
```
Wire the Field/CDLOD subsystem (shipped) for real; stub `_sky`/`_surfacer` calls as null-safe (`?.`) until those modules integrate — the route lines exist so the drift test has real keys to exercise.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add addons/wg17_terrain/runtime/TerrainWorld.cs && git commit -m "feat(engine): TerrainWorld reads config + Route switchboard (no duplicate state)"
```

---

### Task 4: CLI → config (same path as everything)

**Files:** Create/Modify `src/app/Main.cs` (the dev project's entry) + main scene

**Interfaces:**
- Consumes: `TerrainConfig.ApplyOverride`, `TerrainWorld`.
- Produces: the main scene has a `TerrainWorld`; `Main.cs` parses CLI flags into `config.ApplyOverride(...)` BEFORE the world builds (or right after, triggering Route).

- [ ] **Step 1: Write the CLI→config mapping**

In `Main.cs` `_Ready` (runs before/with `TerrainWorld`): read `OS.GetCmdlineUserArgs()`; map flags to overrides, e.g. `--clouds=1 → config.ApplyOverride("sky.clouds_enabled", true)`, `--seed=N`, `--amplitude=F`, `--surfacing=0/1`. Assign the config to the `TerrainWorld` before/at its `_Ready`. CLI uses the SAME `ApplyOverride` path — no separate apply logic.

- [ ] **Step 2: Build + launch sanity**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --seed=42
```
Expected: terrain builds from the config; `--seed=42` produces a different field than default (visible / logged).

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(engine): CLI flags write config via ApplyOverride (one path)"
```

---

### Task 5: Presets (save/load .tres)

**Files:** Create `addons/wg17_terrain/presets/Default.tres`; add save/load entry points

**Interfaces:**
- Produces: a `Default.tres` saved `TerrainConfig`; `TerrainConfig.Save/Load` used to round-trip.

- [ ] **Step 1: Save a Default preset + round-trip test path**

Add a throwaway `--savepreset=Default` path (or do it once in `_Ready`) that calls `config.Save("res://addons/wg17_terrain/presets/Default.tres")`. Add `--loadpreset=Default` that loads it and assigns to the world (firing Route for each key via an ApplyAll). Verify the loaded config reproduces the saved look.

- [ ] **Step 2: Build + verify + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --savepreset=Default
git add -A && git commit -m "feat(engine): preset save/load as .tres (Default preset)"
```

---

### Task 6: Addon packaging (plugin.cfg + self-containment)

**Files:** Create `addons/wg17_terrain/plugin.cfg`

**Interfaces:**
- Produces: a valid Godot addon manifest so the folder is a drop-in unit.

- [ ] **Step 1: Write plugin.cfg**

```ini
[plugin]
name="WG17 Terrain"
description="Procedural infinite terrain engine: field/CDLOD, lighting, sky, surfacing, config-driven."
author="josep"
version="0.1.0"
script="Wg17TerrainPlugin.cs"
```
Add a minimal `Wg17TerrainPlugin.cs : EditorPlugin` if an editor-side registration is wanted (optional this slice — a runtime addon works without it). Enable the plugin in `project.godot`.

- [ ] **Step 2: Confirm engine assets live under the addon**

Verify the engine's shaders/scripts the runtime needs are under `addons/wg17_terrain/` (or note which still live in `src/`/`shaders/` to be moved — the full move can be incremental, but record what's outside; ExportCheck in Plan 2 measures this).

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(engine): WG17 terrain Godot addon manifest (drop-in packaging)"
```

---

## Self-Review

**Spec coverage (Plan 1 = engine spine):** §3 TerrainConfig+TerrainWorld+addon layout → Tasks 1,2,3,6; §4 config mechanism (properties+changed+ApplyOverride+Clone+Save/Load) → Tasks 1–2,5; §5 TerrainWorld Route switchboard, no duplicate state → Task 3; CLI same-path → Task 4; presets → Task 5; §2 single-source-of-truth + lightly-exportable → Tasks 1–3,6. UI + the two named checks are Plan 2. ✓

**Placeholder scan:** sub-config "minimum real fields" are enumerated (not vague); null-safe `?.` stubs for not-yet-integrated subsystems are explicit with reason. `.../` abbreviates the Godot path in Global Constraints. No TBD. ✓

**Type consistency:** `TerrainConfig` (Field/Sky/Lighting/Surfacing sub-configs, `ChangedKey` signal, `ApplyOverride`/`Clone`/`Save`/`Load`) consistent across tasks; sub-config `Changed(key)` → re-emit `ChangedKey("module.key")` matches `TerrainWorld.Route` key strings (`"sky.clouds_enabled"` etc.). CLI `ApplyOverride` keys match the Route cases. ✓

**Note:** the spine is config-parsers + a switchboard; verified by the Plan 2 `ConfigSyncCheck` (cli→ui→engine→preset) + the eye-gate. The single-source-of-truth rule is structural (engine has no duplicate state), which is what makes the drift bug impossible.
