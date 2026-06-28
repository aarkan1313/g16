# WG17 Slice A (Lighting) — Plan 1 of 3: Data Core + Bootstrap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the pure-data lighting foundation in WG17 — the axis state classes, the N-luminary model + budgeter, the preset loaders, and the headless round-trip check — porting WG16's proven, scene-free code as-is.

**Architecture:** These files have ZERO Godot scene writes (only `Vector3`/`Color`/`Json` value types). They port nearly verbatim from WG16 with a namespace change. This plan is the bedrock the composer (Plan 2) and driver (Plan 3) build on.

**Tech Stack:** Godot 4.6 / C# (.NET 8, nullable), `Godot.Json` for preset loading.

**Plan set (this is 1 of 3):**
- **Plan 1 (this):** data core — `LightingState`, `Luminary`, `SkyPresets`, `LuminaryPresetCheck`, data JSON.
- **Plan 2:** seams + composer — `ILightingTarget`, `ILuminaryFeed`, `ShadowRegistry`, `LightingComposer`, `ShadowOwnerCheck`.
- **Plan 3:** driver + scene + eye-gate — `LightingDriver`, scene wiring, CLI flags, day/night eye-gate.

**Reference sources (port FROM):**
- `C:\Wg16\wg-16-project\scripts\lab\LightingState.cs`, `Luminary.cs`, `SkyPresets.cs`, `LuminaryPresetCheck.cs`
- `C:\Wg16\wg-16-project\data\{time,weather,grade,sun,celestial,fantasy}_presets.json`, `lighting_moods.json`, `luminaries.json`
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceA-lighting-design.md`

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k` — all created files here, NOT in WG16.
- **Namespace:** `Te10k.Lighting` for all lighting classes; `Te10k.Lighting.Checks` for checks.
- **C# rebuild gotcha:** run `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit before launching — Godot does NOT rebuild C# on launch.
- **Launch path gotcha:** launch with absolute `--path C:/Wg16/WG17/terrainengine-10k`, never `.`.
- **CLI flag gotcha:** user flags after the scene need a bare `--` separator or they silently no-op.
- **Layering rule:** files in this plan do NO scene writes, NO `GetNode`, NO `/root/...` paths. Pure data/logic only.
- **Fix-on-port:** drop WG16's `MoodToStates()` legacy mood-dict→axes shim (Task 5). No EMISSION/hardcoded-ambient logic lives in these files anyway.
- **Depends on:** the WG17 C# project existing. The terrain slice's Task 1 bootstraps the `.csproj`; if that hasn't merged yet, Task 1 below creates it.

---

## File Structure (this plan)

```
src/lighting/LightingState.cs        # Task 2 — Time/TimeKey/SunDisc/Moon/Stars/Weather/Grade + LightingPresets
src/lighting/Luminary.cs             # Task 3 — Luminary + LuminaryKind/Caps/Allocation + LuminaryBudget
src/lighting/SkyPresets.cs           # Task 5 — sun/celestial/fantasy/mood preset libraries (no mood-dict shim)
src/lighting/checks/LuminaryPresetCheck.cs   # Task 4 — round-trip data gate
data/*.json                          # Task 1 — copied preset files
```

---

### Task 1: Ensure C# project + copy data files

**Files:**
- Create (if missing): `Terrainengine10k.csproj`
- Create: `data/time_presets.json`, `weather_presets.json`, `grade_presets.json`, `sun_presets.json`, `celestial_presets.json`, `fantasy_presets.json`, `lighting_moods.json`, `luminaries.json` (copied from WG16)

**Interfaces:**
- Produces: a buildable C# project; preset JSON present at `res://data/`.

- [ ] **Step 1: Check whether the csproj exists**

Run (Bash):
```bash
ls "C:/Wg16/WG17/terrainengine-10k/Terrainengine10k.csproj" 2>/dev/null && echo EXISTS || echo MISSING
```
If EXISTS (terrain slice merged it), skip Step 2.

- [ ] **Step 2: Create the csproj if missing**

Create `C:/Wg16/WG17/terrainengine-10k/Terrainengine10k.csproj`:
```xml
<Project Sdk="Godot.NET.Sdk/4.6.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```
Also ensure `project.godot` `config/features` includes `"C#"`.

- [ ] **Step 3: Copy the 8 preset JSON files**

Run (Bash):
```bash
mkdir -p "C:/Wg16/WG17/terrainengine-10k/data"
for f in time_presets weather_presets grade_presets sun_presets celestial_presets fantasy_presets lighting_moods luminaries; do
  cp "C:/Wg16/wg-16-project/data/$f.json" "C:/Wg16/WG17/terrainengine-10k/data/$f.json"
done
ls "C:/Wg16/WG17/terrainengine-10k/data/"
```
Expected: all 8 `.json` files listed.

- [ ] **Step 4: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "chore(lighting): C# project + lighting preset data files"
```
Expected: `Build succeeded`.

---

### Task 2: Port LightingState.cs (pure-data axes)

**Files:**
- Create: `src/lighting/LightingState.cs` (port of `scripts/lab/LightingState.cs`)

**Interfaces:**
- Produces: `TimeState`, `TimeKey`, `SunDiscState`, `MoonState`, `StarsState`, `WeatherState`, `GradeState` (all with `Clone()` where WG16 has it); `static class LightingPresets` with `List<TimeState> Time`, `List<WeatherState> Weather`, `List<GradeState> Grade`, `List<TimeKey> DayScript`, and `static void Load()`.

- [ ] **Step 1: Copy the file and change the namespace**

Copy `scripts/lab/LightingState.cs` → `src/lighting/LightingState.cs`. Change `namespace WG16.Lab;` → `namespace Te10k.Lighting;`. Keep ALL struct fields, defaults, `Clone()` methods, and the `LightingPresets.Load()` JSON parser verbatim. The `res://data/*_presets.json` paths are correct (the files were copied in Task 1).

- [ ] **Step 2: Build to confirm it compiles standalone**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded` (no other lighting files yet — this file is self-contained, only Godot value types).

- [ ] **Step 3: Commit**

```bash
git add src/lighting/LightingState.cs && git commit -m "feat(lighting): port pure-data axis states (Time/Weather/Grade/celestial)"
```

---

### Task 3: Port Luminary.cs (N-body model + budgeter)

**Files:**
- Create: `src/lighting/Luminary.cs` (port of `scripts/lab/Luminary.cs`)

**Interfaces:**
- Produces: `enum LuminaryKind { Sun, Moon }`; `class Luminary` (with `Id`, `Kind`, `Priority`, `LightEnergy`, `IsPhysicalLight`, `ContributesToAtmosphere`, arc/appearance fields, `Surface`, `Clone()`); `LuminaryCaps { MaxPhysicalLights=4, MaxAtmosphereSuns=3, MaxDiscs=16 }`; `LuminaryAllocation(int n)` with `int[] LightSlot`, `bool[] Atmosphere`, `int[] Disc`, `List<string> Notes`; `static class LuminaryBudget` with `LuminaryAllocation Allocate(IReadOnlyList<Luminary> lums, IReadOnlyList<float> weights, LuminaryCaps caps)`.

- [ ] **Step 1: Copy the file and change the namespace**

Copy `scripts/lab/Luminary.cs` → `src/lighting/Luminary.cs`. Change namespace to `Te10k.Lighting`. Keep the full `Luminary` data model, `LuminaryCaps` (caps unchanged — `MaxPhysicalLights=4` is the Godot `LIGHT0..3` hard limit), `LuminaryAllocation`, and the pure `LuminaryBudget.Allocate` ranking/demotion-logging function verbatim.

- [ ] **Step 2: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/lighting/Luminary.cs && git commit -m "feat(lighting): port N-luminary model + priority budgeter (4-light cap)"
```

---

### Task 4: Port LuminaryPresetCheck (round-trip data gate)

**Files:**
- Create: `src/lighting/checks/LuminaryPresetCheck.cs` (port of `scripts/lab/LuminaryPresetCheck.cs`)

**Interfaces:**
- Consumes: `Luminary` (Task 3).
- Produces: `static class LuminaryPresetCheck` with `static bool Run()` (or the WG16 signature) that round-trips `Luminary ↔ dict ↔ JSON ↔ dict ↔ Luminary` and returns/prints PASS/FAIL.

- [ ] **Step 1: Copy and re-namespace**

Copy `scripts/lab/LuminaryPresetCheck.cs` → `src/lighting/checks/LuminaryPresetCheck.cs`. Namespace → `Te10k.Lighting.Checks`. Add `using Te10k.Lighting;`. Keep the round-trip logic (incl. `Color` ↔ `[r,g,b]`) verbatim. If WG16's version references the harness/registry, strip that — the check only needs `Luminary` + JSON.

- [ ] **Step 2: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/lighting/checks/LuminaryPresetCheck.cs && git commit -m "feat(lighting): port luminary round-trip data check"
```

(The `--luminarycheck` CLI wiring lands in Plan 3 Task with the driver; this task just makes the check class exist and compile. It is pure logic and will run once a driver invokes it.)

---

### Task 5: Port SkyPresets.cs WITHOUT the mood-dict shim (fix-on-port)

**Files:**
- Create: `src/lighting/SkyPresets.cs` (port of `scripts/lab/SkyPresets.cs`, minus the legacy shim)

**Interfaces:**
- Consumes: `LightingState` types (Task 2), `Luminary` (Task 3).
- Produces: `class SkyPresets` (or static) exposing `ApplySun(int idx)`, `ApplyCelestial(int idx)`, `ApplyFantasy(int idx)`, `ApplyMood(int idx)` that load `data/{sun,celestial,fantasy}_presets.json` + `lighting_moods.json` and return/populate axis states. Moods are read as axis bundles `{time, sundisc, weather, grade}` directly — NO `MoodToStates()` dict-splitting.

- [ ] **Step 1: Copy SkyPresets.cs and re-namespace**

Copy `scripts/lab/SkyPresets.cs` → `src/lighting/SkyPresets.cs`. Namespace → `Te10k.Lighting`. Keep the four preset-library loaders (sun, celestial, fantasy, mood).

- [ ] **Step 2: Remove the legacy mood-dict shim**

In WG16, mood application went `mood dict → MoodToStates() → axes`. Delete any `MoodToStates`-style method and any code path that builds axis states from a flat `Dictionary` mood. Instead, `ApplyMood(idx)` reads `lighting_moods.json` entries as explicit axis bundles (a mood = `{ "time": {...}, "sundisc": {...}, "weather": {...}, "grade": {...} }`) and populates `TimeState`/`SunDiscState`/`WeatherState`/`GradeState` directly. If `lighting_moods.json` currently stores flat keys, normalize the loader to read nested axis objects (and, if needed, update the copied `data/lighting_moods.json` to the nested shape — document the change in the commit).

- [ ] **Step 3: Decouple from the harness registry**

WG16's `SkyPresets` routed applies through `ILabControls`. Here there is no registry: the applier methods return/populate axis state objects (the composer in Plan 2 consumes them). Remove `ILabControls` references; the methods operate on plain state.

- [ ] **Step 4: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(lighting): port sky presets, axes-native moods (drop mood-dict shim)"
```

---

## Self-Review

**Spec coverage (Plan 1 portion):**
- §3 `LightingState.cs` port-as-is → Task 2. ✓
- §3 `Luminary.cs` port-as-is + 4-light cap → Task 3. ✓
- §3 `SkyPresets.cs` port + drop mood-dict shim (§4 fix-on-port) → Task 5. ✓
- §3 `LuminaryPresetCheck` port → Task 4. ✓
- §3 data JSON copied → Task 1. ✓
- §3 layering (no scene writes in these files) → enforced by Global Constraints + the files' nature. ✓

**Placeholder scan:** none. The `--luminarycheck` wiring is explicitly deferred to Plan 3 (where the driver lives), with a note — not a vague TODO. ✓

**Type consistency:** `LuminaryBudget.Allocate(IReadOnlyList<Luminary>, IReadOnlyList<float>, LuminaryCaps)` matches the verified WG16 signature; `LuminaryCaps` fields `MaxPhysicalLights/MaxAtmosphereSuns/MaxDiscs` match. State class names match the spec §3 and §4. ✓

**Note on TDD:** this is a verbatim port of proven scene-free code; the discipline is port → compile → the round-trip check (Task 4) is the test. No red/green loop is warranted for a 1:1 copy. The behavior-bearing new code (composer, registry) gets stricter checks in Plan 2.
