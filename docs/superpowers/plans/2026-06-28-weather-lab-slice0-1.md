# Weather Lab — Slice 0 + Slice 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a standalone `weather-lab/` Godot project that renders WG16's real infinite CDLOD world under the look-complete sky (Slice 0), then add a pure-C# weather brain whose state makes the sky visibly "weather" Clear↔Overcast↔Rain over time (Slice 1).

**Architecture:** Mirror `erosion-lab/`: a pure-C# `Weather.Core` (the brain, zero Godot, xunit-tested) + `Weather.Tests` + a thin `WeatherLab` Godot harness. The harness *copies* WG16's terrain cluster and sky cluster (verbatim, keeping their `WG16.Lab`/`WG16.Field` namespaces) and leaves the `TerrainLabUI` god-class behind, replacing it with a thin `ILightingHost` shim + a small `Main.cs`. The brain emits one `WeatherState` snapshot per frame; render modules only *consume* it.

**Tech Stack:** Godot 4.6 (Forward+), C# / .NET 8, `Godot.NET.Sdk/4.6.2`, xunit. GPU: compute (field bake, cloud raymarch, atmosphere LUTs) via local `RenderingDevice` + `RenderingServer.CallOnRenderThread`.

## Global Constraints

Every task implicitly includes these (verbatim from the spec + project conventions):

- **Godot 4.6, C#, Forward+, net8.0, `Godot.NET.Sdk/4.6.2`, `<Nullable>enable</Nullable>`.**
- **`Weather.Core` has ZERO Godot dependency** (`Microsoft.NET.Sdk` only). No `using Godot;`. Use `System.MathF`/`System.Numerics`, never `Mathf`/`Vector3`.
- **Verification convention (project): NO TDD for GPU/visual.** Visual/harness tasks verify by `dotnet build` + a **live windowed eye-gate** + a **mechanical CLI self-check that exits with a code** (0 pass / non-0 fail). **xunit/TDD is used ONLY for `Weather.Core`** (pure logic).
- **C# edits need `dotnet build` to take effect** (stale-DLL gotcha — the Godot player does not rebuild C#). **GPU compute bakes need a *windowed* run** (`--headless` has no local `RenderingDevice`).
- **CLI user flags need a bare `--` separator** (`godot ... -- --flag`), else `OS.GetCmdlineUserArgs()` is empty and flags silently no-op. **`--shot=` needs an OS filesystem path, not `user://`.**
- **All weather spatial sampling is world-anchored** (sample true world-XZ), **deterministic** (pure function of `(seed, time, worldXZ)`), and **seamless across the 8192 m renderOrigin snap** (reconstruct world-XZ identically to the terrain path).
- **The brain is a plain C# object (not a Godot `Node`)**; easing is exponential-decay `s = target + (s−target)·2^(−rate·dt)` (frame-rate independent); `precipType` is **derived**, never authored.
- **Each render module is default-OFF behind a toggle until eye-gated.**
- **Do NOT touch the look-complete lights.** (Future lightning/weather lighting = additive terms only; not in this plan.)
- **Naming hazard:** `WG16.Lab.WeatherState` already exists (fog state in `LightingState.cs`). The brain's snapshot is `Weather.Core.WeatherState`; **fully-qualify it in any harness file that also uses `WG16.Lab`.**

---

## File Structure

New repo `weather-lab/` (sibling of `erosion-lab/` and `wg-16-project/`):

```
weather-lab/
  .gitignore
  Weather.Core/
    Weather.Core.csproj          # Microsoft.NET.Sdk, no Godot
    WeatherState.cs              # snapshot struct + PrecipType enum + Clear/Overcast/Rain presets
    WeatherMath.cs               # ExpEase, derive precip, integrators (pure static)
    WeatherField.cs              # deterministic tileable value-noise (synoptic pressure / frontal activity)
    WeatherSim.cs                # holds live state; eases toward a target; ticks integrators
    WeatherPreset.cs             # target blackboard + preset library
  Weather.Tests/
    Weather.Tests.csproj         # xunit, refs Weather.Core
    WeatherMathTests.cs
    WeatherSimTests.cs
    WeatherFieldTests.cs
  WeatherLab/
    WeatherLab.csproj            # Godot.NET.Sdk/4.6.2, refs Weather.Core
    project.godot
    Main.tscn                    # Node3D root: Camera(FlyCamera) + Sun + Env
    Main.cs                      # harness: build terrain + sky, drive ticks, weather clock
    ThinLightingHost.cs          # implements WG16.Lab.ILightingHost (the de-glue shim)
    SkyCoupling.cs               # maps Weather.Core.WeatherState -> CloudVolume knobs
    FlyCamera.cs                 # copied from wg-16-project/scripts/workbench/FlyCamera.cs
    scripts/lab/   ...           # COPIED terrain + sky clusters (keep WG16.Lab namespace)
    scripts/field/ ...           # COPIED FieldParams, FieldCompute (keep WG16.Field namespace)
    shaders/       ...           # COPIED ground + field + cloud + atmosphere shaders
    data/          ...           # COPIED field_params.json, cloud_*.json, lighting_moods.json, sun_presets.json
  docs/superpowers/specs/2026-06-28-weather-lab-design.md   # moved from wg-16-project
  docs/superpowers/plans/2026-06-28-weather-lab-slice0-1.md # this file, moved here
```

---

## SLICE 0 — Scaffold: the real infinite world under the AAA sky, standalone

### Task 0.1: Lab skeleton + green build

**Files:**
- Create: `weather-lab/.gitignore`
- Create: `weather-lab/Weather.Core/Weather.Core.csproj`
- Create: `weather-lab/Weather.Tests/Weather.Tests.csproj`
- Create: `weather-lab/WeatherLab/WeatherLab.csproj`
- Create: `weather-lab/WeatherLab/project.godot`
- Create: `weather-lab/WeatherLab/Main.tscn`, `weather-lab/WeatherLab/Main.cs`
- Copy: `wg-16-project/scripts/workbench/FlyCamera.cs` → `weather-lab/WeatherLab/FlyCamera.cs`
- Move: the spec + this plan into `weather-lab/docs/superpowers/{specs,plans}/`

**Interfaces:**
- Produces: a buildable 3-project solution; `WeatherLab` opens to an empty Node3D scene you can fly around (no terrain yet).

- [ ] **Step 1: Create folders and `.gitignore`**

```
# weather-lab/.gitignore
bin/
obj/
.godot/
*.user
```

- [ ] **Step 2: Write the three `.csproj` files (exact content)**

`weather-lab/Weather.Core/Weather.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

`weather-lab/Weather.Tests/Weather.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Weather.Core\Weather.Core.csproj" />
  </ItemGroup>
</Project>
```

`weather-lab/WeatherLab/WeatherLab.csproj`:
```xml
<Project Sdk="Godot.NET.Sdk/4.6.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Weather.Core\Weather.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `project.godot`**

```
config_version=5

[application]
config/name="WeatherLab"
run/main_scene="res://Main.tscn"
config/features=PackedStringArray("4.6", "C#", "Forward Plus")

[dotnet]
project/assembly_name="WeatherLab"

[rendering]
renderer/rendering_method="forward_plus"
```

- [ ] **Step 4: Copy `FlyCamera.cs`**

Copy `wg-16-project/scripts/workbench/FlyCamera.cs` to `weather-lab/WeatherLab/FlyCamera.cs` verbatim. (It's a self-contained `Camera3D` fly controller; confirm it has no `WG16.Lab` dependency — if its namespace is `WG16.*`, keep it.)

- [ ] **Step 5: Write a placeholder `Main.cs` + `Main.tscn`**

`Main.cs`:
```csharp
using Godot;

public partial class Main : Node3D
{
    public override void _Ready()
    {
        GD.Print("WeatherLab: skeleton up. (terrain + sky added in Task 0.2 / 0.3)");
    }
}
```

`Main.tscn` — **⚠ the root node MUST be named `TerrainLabRoot`** (the copied `LightingComposer` resolves nodes via the hardcoded absolute paths `/root/TerrainLabRoot/Env` and `/root/TerrainLabRoot/Sun`; naming the scene root `TerrainLabRoot` makes those resolve, fixing a runtime `GetNode` crash the compiler can't see). Children `Camera` (FlyCamera) / `Sun` / `Env` sit directly under it:
```
[gd_scene load_steps=4 format=3]

[ext_resource type="Script" path="res://Main.cs" id="1"]
[ext_resource type="Script" path="res://FlyCamera.cs" id="2"]

[sub_resource type="Environment" id="env"]
background_mode = 1
ambient_light_source = 2
ambient_light_energy = 0.4

[node name="TerrainLabRoot" type="Node3D"]
script = ExtResource("1")

[node name="Camera" type="Camera3D" parent="."]
transform = Transform3D(1, 0, 0, 0, 0.9, 0.43, 0, -0.43, 0.9, 0, 1400, 1200)
far = 90000.0
script = ExtResource("2")

[node name="Sun" type="DirectionalLight3D" parent="."]
transform = Transform3D(1, 0, 0, 0, 0.82, 0.57, 0, -0.57, 0.82, 0, 0, 0)
shadow_enabled = true

[node name="Env" type="WorldEnvironment" parent="."]
environment = SubResource("env")
```
> The `project.godot` `run/main_scene` is still `res://Main.tscn`; only the root *node name* changes. `Main.cs` (the script) is attached to `TerrainLabRoot`, so `GetNode<Camera3D>("Camera")` still resolves (relative child), and the scene instances at `/root/TerrainLabRoot` so the composer's absolute paths resolve too.

- [ ] **Step 6: `git init` + build all three projects**

Run (from `weather-lab/`):
```bash
git init && git add -A && git commit -m "weather-lab: skeleton (3 projects, FlyCamera, empty scene)"
dotnet build Weather.Core/Weather.Core.csproj
dotnet build Weather.Tests/Weather.Tests.csproj
dotnet build WeatherLab/WeatherLab.csproj
```
Expected: all three `Build succeeded`. (If `Godot.NET.Sdk/4.6.2` isn't restorable offline, confirm the same SDK builds `erosion-lab/ErosionLab`.)

- [ ] **Step 7: Eye-gate the empty scene**

Run windowed: `<godot4.6> --path <abs>/weather-lab/WeatherLab`
Expected: a window opens, the `_Ready` print appears, WASD+mouse flies the camera in empty space. Commit if not already.

---

### Task 0.2: Copy the terrain cluster → fly the infinite world

**Files:**
- Copy (verbatim, keep namespaces) into `weather-lab/WeatherLab/scripts/lab/`: `CdlodTerrain.cs`, `CdlodQuadtree.cs`, `CdlodMesh.cs`, `ChunkFieldCache.cs`, `ChunkAabbProvider.cs`, `Std430.cs`, `TerrainTestPaths.cs`, `TerrainLab.cs`. *(`ChunkSlot` and `CdlodChunk` are nested classes — no separate files.)*
- Copy into `weather-lab/WeatherLab/scripts/field/`: `FieldParams.cs`, `FieldCompute.cs`.
- Copy into `weather-lab/WeatherLab/shaders/`: `ground.gdshader`, `field_height.glsl`, `field_math.gdshaderinc`, `field_bake.glsl` (+ any `.gdshaderinc`/`.glsl` they `#include` — resolve by build/load error).
- Copy into `weather-lab/WeatherLab/data/`: `field_params.json`.
- **Trim in the copied `TerrainLab.cs`:** delete the `BindWaterRegion(WG16.Hydrology.WorldWaterRegion, WG16.Hydrology.WaterParams)` method — it is dead in the weather lab and is the *only* thing that pulls the ~10-file `WG16.Hydrology` cluster (audit-confirmed). Leave everything else.
- Modify: `weather-lab/WeatherLab/Main.cs`.

**Interfaces:**
- Consumes (verified against the real files):
  - `WG16.Field.FieldParams.Load()` (reads `res://data/field_params.json`); `WG16.Field.FieldCompute` (`IDisposable`, owns a local `RenderingDevice` — **windowed only**).
  - `WG16.Lab.TerrainLab` (a `MeshInstance3D` presenter that owns the `CdlodTerrain`): build entry `void Build(FieldCompute fc, FieldParams p)` (does **not** self-build on `_Ready`); `void SetCdlod(bool)` (enables the quadtree — `CdlodActive` is false until this); `void SetCameraWorld(Vector3)` (pushes `cam_world` to the ground shader); `void CdlodTick(Vector3 camPos, Vector3 velXZ)`; `Vector3 CdlodRenderOrigin`; `bool CdlodActive`; `void SetTexturesOn(bool)`.
- Produces: `Main` builds + drives the terrain. *(`CdlodTerrain.Setup(...)` is **internal** — `TerrainLab.Build` orchestrates it; never call it from the harness or you double-init the quadtree.)*

- [ ] **Step 1: Copy the terrain cluster files** (the file lists above), keeping `namespace WG16.Lab;` / `namespace WG16.Field;`. Then delete the `BindWaterRegion` method from the copied `TerrainLab.cs`.

- [ ] **Step 2: Confirm no god-class dependency, then build to find the closure**

Run: `grep -rl "TerrainLabUI" weather-lab/WeatherLab/scripts` → expected: no hits. Then:
```bash
dotnet build weather-lab/WeatherLab/WeatherLab.csproj
```
Resolve each `CS0246 (type not found)` by copying the one missing file it names from `wg-16-project` (same relative folder), then rebuild. Repeat until `Build succeeded`. **Expected residual closure (audit-confirmed):** `TerrainTestPaths.cs` (referenced unconditionally by `TerrainLab.Build`) and its own small closure; `Std430` (shared util). If a `WG16.Hydrology.*` type is still named, you missed deleting `BindWaterRegion` — delete it (don't copy hydrology). Copy real terrain types; only trim the two dead surfaces named above.

- [ ] **Step 3: Wire the terrain into `Main.cs`**

Replace `Main.cs` body with the wiring below. **Key (audit): `TerrainLab` does not self-build — you must construct a `FieldCompute`, call `Build(fc, params)`, then `SetCdlod(true)`; and push `SetCameraWorld` *before* `CdlodTick` each frame. `SetTexturesOn(false)` uses the height/slope colour ramp so the ground isn't black (the textured path needs `assets/materials/`, deferred to a later slice).**

```csharp
using Godot;
using WG16.Lab;
using WG16.Field;

public partial class Main : Node3D
{
    private TerrainLab _terrain = null!;
    private Camera3D _cam = null!;
    private Vector3 _camVelSmoothed;
    private Vector3 _lastCamVelPos;
    private bool _camVelInit;

    public override void _Ready()
    {
        _cam = GetNode<Camera3D>("Camera");
        _cam.Far = 90000f;

        // TerrainLab is the MeshInstance3D presenter that owns the CdlodTerrain.
        // It does NOT self-build: construct a windowed FieldCompute + load params, then Build.
        _terrain = new TerrainLab { Name = "TerrainLab" };
        AddChild(_terrain);
        var fp = FieldParams.Load();
        using (var fc = new FieldCompute())   // local RenderingDevice; windowed only; IDisposable
            _terrain.Build(fc, fp);
        _terrain.SetTexturesOn(false);        // height/slope colour ramp (no assets/materials/ yet)
        _terrain.SetCdlod(true);              // enable the infinite quadtree streaming

        GD.Print("WeatherLab: terrain up. Fly with WASD+QE, mouse to look.");
    }

    public override void _Process(double delta)
    {
        if (_terrain == null || _cam == null) return;

        Vector3 renderOrigin = _terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero;
        Vector3 camPos = _cam.Position + renderOrigin;

        if (_camVelInit && delta > 1e-5)
        {
            Vector3 inst = (camPos - _lastCamVelPos) / (float)delta;
            inst.Y = 0f;
            _camVelSmoothed = _camVelSmoothed.Lerp(inst, 0.12f);
        }
        _lastCamVelPos = camPos;
        _camVelInit = true;

        _terrain.SetCameraWorld(camPos);              // push cam_world BEFORE the tick (anti-repeat LOD)
        _terrain.CdlodTick(camPos, _camVelSmoothed);

        if (_terrain.CdlodActive)
            _cam.Position = camPos - _terrain.CdlodRenderOrigin;
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build weather-lab/WeatherLab/WeatherLab.csproj` → Expected: `Build succeeded`.

- [ ] **Step 5: Eye-gate (windowed — compute bakes need a real RenderingDevice)**

Run: `<godot4.6> --path <abs>/weather-lab/WeatherLab`
Expected: the infinite CDLOD terrain renders **in the height/slope colour ramp** (placeholder ground — textures are a deliberately deferred slice; `SetTexturesOn(false)`); flying forward streams new chunks; no crash on the renderOrigin snap (cross ~8192 m and watch for a pop — there should be none, or note it for later). *The "look-complete" part is the SKY (Task 0.3); the ground is intentionally placeholder until a texture slice copies `assets/materials/`.*

- [ ] **Step 6: Commit**

```bash
cd weather-lab && git add -A && git commit -m "weather-lab: copy terrain cluster, render infinite CDLOD world in Main"
```

---

### Task 0.3: Copy the sky cluster + thin `ILightingHost` → look-complete sky

**Files:**
- Copy into `weather-lab/WeatherLab/scripts/lab/`: `CloudVolume.cs`, `AtmosphereCompute.cs`, `LightingComposer.cs`, `LightingState.cs`, `Luminary.cs`, `CloudParams.cs`, `CloudLayers.cs`, `CloudWeather.cs`, `CloudNoiseCompute.cs`. *(`Std430.cs` already copied in Task 0.2.)*
  - **Do NOT copy `SkyPresets.cs` / `CloudPresets.cs`** (audit): they are registry-coupled orchestration (`SkyPresets` ctor needs `ILabControls`) and would drag the `ILabControls`/`LabControl`/registry-loader UI web back in — the exact god-class glue this lab leaves behind. `Main` never uses them; the fixed sun angle in `ThinLightingHost.OrientSun` replaces them. `LightingState.cs` holds all 7 state classes (`TimeState`/`SunDiscState`/`WeatherState`/`GradeState`/`MoonState`/`StarsState` + `LightingPresets`); `Luminary.cs` holds `Luminary`/`LuminaryKind`/`LuminaryCaps`/`LuminaryAllocation`/`LuminaryBudget`.
- Copy into `weather-lab/WeatherLab/shaders/`: `cloud_sky.gdshader` + the cloud compute/includes (`cloud_raymarch.glsl`, `cloud_density.gdshaderinc`, `cloud_noise_3d.glsl`) + atmosphere shaders `atmosphere_transmittance.glsl`, `atmosphere_multiscatter.glsl`, `atmosphere_skyview.glsl`, `atmosphere_cloudlight.glsl`, **`atmosphere_aerial_v2.glsl`** *(audit: `AtmosphereCompute.Attach` compiles all five — a missing aerial shader throws on the render thread and atmosphere never goes `Ready`)*. Resolve any further names by build/load error.
- Copy into `weather-lab/WeatherLab/data/`: `cloud_params.json`, `cloud_layers.json`, `time_presets.json`, `grade_presets.json`, `weather_presets.json` *(audit: `LightingState.LightingPresets.Load()` reads these three; `lighting_moods.json`/`sun_presets.json` are read only by the UI/SkyPresets path we drop, so omit them)*.
- Create: `weather-lab/WeatherLab/ThinLightingHost.cs`
- Modify: `weather-lab/WeatherLab/Main.cs`

**Interfaces:**
- Consumes (verified signatures):
  - `WG16.Lab.ILightingHost` (in `LightingComposer.cs`) — members: `Node SceneOwner {get;}`, `CloudVolume? Cloud {get;}`, `float Overcast {get;}`, `bool AtmosphereOn {get;}`, `bool VolumetricFogOn {get;}`, `float CdlodViewDistance {get;}`, `float FogViewScale {get;}`, `AtmosphereCompute? Atmosphere {get;}`, `TerrainLab? Terrain {get;}`, `void OrientSun(DirectionalLight3D sun)`, `void SyncLightControlsToScene()`.
  - `new LightingComposer(ILightingHost host)`; `.Time` (`TimeState`), `.Weather` (`WG16.Lab.WeatherState`), `.Compose()`, `.MoodToStates(Godot.Collections.Dictionary)`, `.DriveTime(float hour)`, `.ApplyOvercastScaling()`, `.LoadLuminaries(List<Luminary>)`.
  - `CloudVolume` (a `Node` — Godot auto-calls its `_Process`; **do not call `_Process` manually**): `Attach(Godot.Environment env, Camera3D cam, float regionSizeM)`, `SetCameraWorld(Vector3)`, `SetKnob(string,float)` (incl. `"coverage"`, `"density"`, `"cloud_type"`), `Overcast()`, `SetSun(Vector3 dir, Color color, float energy)`.
  - `AtmosphereCompute` (a `Node` — auto-processes): `Attach()`, `SetEnabled(bool)`, `SetSun(Vector3 toSun)`, `Texture2Drd? SkyViewTexture {get;}`, `bool Ready {get;}`.
- Produces: `Main` renders the look-complete sky over the terrain via a thin host (no `TerrainLabUI`). **Runtime de-glue note:** `LightingComposer` resolves `/root/TerrainLabRoot/{Env,Sun}` by absolute path — handled by naming the scene root `TerrainLabRoot` in Task 0.1 (no per-frame crash).

- [ ] **Step 1: Copy the core sky files** (the list above), keeping `namespace WG16.Lab;`.

- [ ] **Step 2: Create `ThinLightingHost.cs`** (the de-glue shim — exact content)

```csharp
using Godot;
using System.Collections.Generic;
using WG16.Lab;

// Thin replacement for the TerrainLabUI god-class as LightingComposer's host.
// Implements the 11 ILightingHost members against plain scene nodes + a fixed
// sun angle/azimuth. No UI, no presets indirection.
public sealed class ThinLightingHost : ILightingHost
{
    private readonly Node _owner;
    private readonly DirectionalLight3D _sun;
    public CloudVolume? CloudRef;
    public AtmosphereCompute? AtmosphereRef;
    public TerrainLab? TerrainRef;
    public float SunAngleDeg = 50f;     // elevation
    public float SunAzimuthDeg = 130f;  // compass
    public float OvercastValue;

    public ThinLightingHost(Node owner, DirectionalLight3D sun) { _owner = owner; _sun = sun; }

    public Node SceneOwner => _owner;
    public CloudVolume? Cloud => CloudRef;
    public AtmosphereCompute? Atmosphere => AtmosphereRef;
    public TerrainLab? Terrain => TerrainRef;
    public float Overcast => OvercastValue;
    public bool AtmosphereOn => AtmosphereRef != null && AtmosphereRef.Ready;
    public bool VolumetricFogOn => false;
    public float CdlodViewDistance => 0f;   // fog<->radius coupling off in the lab for now
    public float FogViewScale => 1f;

    public void OrientSun(DirectionalLight3D sun)
    {
        float el = Mathf.DegToRad(SunAngleDeg);
        float az = Mathf.DegToRad(SunAzimuthDeg);
        sun.Rotation = new Vector3(-el, az, 0f);
        // Push the same sun to cloud + atmosphere so the sky shares the light dir.
        Vector3 toSun = -sun.GlobalTransform.Basis.Z;
        CloudRef?.SetSun(toSun, sun.LightColor, sun.LightEnergy);
        AtmosphereRef?.SetSun(toSun);
    }

    public void SyncLightControlsToScene() { /* no UI in the lab */ }
}
```

- [ ] **Step 3: Build to resolve the sky dependency closure**

```bash
dotnet build weather-lab/WeatherLab/WeatherLab.csproj
```
Resolve each `CS0246` by copying the one missing **render/data** file it names. With `SkyPresets`/`CloudPresets` excluded, the closure should be small and should NOT name `ILabControls`/`LabControl` — **if it does, something you copied still references `SkyPresets`; remove that reference rather than copying the registry.** Rebuild until `Build succeeded`. Record which files you copied (for the audit).

- [ ] **Step 4: Wire the sky into `Main.cs`** (add to the existing terrain `Main`)

Add fields and extend `_Ready`/`_Process`. (`LightingComposer.Weather` is `WG16.Lab.WeatherState`; the brain comes later and is `Weather.Core.WeatherState` — keep them distinct.)

```csharp
// add fields:
private CloudVolume _cloud = null!;
private AtmosphereCompute? _atmosphere;
private LightingComposer _lighting = null!;
private ThinLightingHost _host = null!;
private bool _atmosphereOn = false;   // clouds-only by default (audit: clouds carry the sky look; atmosphere is opt-in A/B)

// in _Ready(), AFTER the terrain is built:
var sun = GetNode<DirectionalLight3D>("Sun");
var env = GetNode<WorldEnvironment>("Env");

_host = new ThinLightingHost(this, sun) { TerrainRef = _terrain };
_lighting = new LightingComposer(_host);

_cloud = new CloudVolume { Name = "CloudVolume" };
AddChild(_cloud);                       // engine drives _cloud._Process — do NOT call it manually
_cloud.Attach(env.Environment, _cam, 8192f);
_host.CloudRef = _cloud;

if (_atmosphereOn)
{
    _atmosphere = new AtmosphereCompute { Name = "AtmosphereCompute" };
    AddChild(_atmosphere);              // also auto-processes
    _atmosphere.Attach();              // compiles 5 atmosphere shaders incl. atmosphere_aerial_v2.glsl
    _atmosphere.SetEnabled(true);
    _host.AtmosphereRef = _atmosphere;
}

// fixed look: mid-afternoon, light cloud
_lighting.Time.TimeOfDay = 14.5f;
_host.OrientSun(sun);
_cloud.SetKnob("coverage", 0.3f);
_lighting.Compose();                    // resolves /root/TerrainLabRoot/{Env,Sun} (root renamed in Task 0.1)

// in _Process(delta), AFTER the terrain tick — push only the inputs the harness owns.
// CloudVolume/AtmosphereCompute self-process as child nodes; calling their _Process here
// would DOUBLE-tick them (drift + wasted GPU). Just feed the camera + overcast coupling.
_cloud.SetCameraWorld(camPos);
_host.OvercastValue = _cloud.Overcast();
_lighting.ApplyOvercastScaling();
```

> **Atmosphere is OFF by default** (`_atmosphereOn = false`) — the audit confirmed the volumetric clouds + `cloud_sky.gdshader` gradient carry a strong sky look on their own, and full AT-3 physical cloud lighting needs `CloudVolume.SetCloudAtmoColors(...)` fed from the LUT readback (not wired in this slice). To A/B atmosphere later: flip `_atmosphereOn = true`, ensure `atmosphere_aerial_v2.glsl` is copied, and verify `_atmosphere.Ready` goes true in the windowed run (it bakes via `CallOnRenderThread`; a render-thread shader-load failure leaves it never-`Ready`).

- [ ] **Step 5: Build, then eye-gate (windowed)**

```bash
dotnet build weather-lab/WeatherLab/WeatherLab.csproj
```
Run: `<godot4.6> --path <abs>/weather-lab/WeatherLab`
Expected: the infinite world now renders **under the look-complete cloud sky** (volumetric clouds + sun disc + sky gradient; atmosphere LUTs are the opt-in A/B). Fly around; the sky is camera-anchored (note: `cloud_params.json` `CameraParallax≈0.4` decouples cloud XZ from the camera by design — the clouds are not 1:1 world-locked to terrain, and snap-seamlessness comes from `CloudVolume`'s own visual-origin guard) and the terrain streams. No per-frame `GetNode` crash (confirms the `TerrainLabRoot` rename).

- [ ] **Step 6: Commit**

```bash
cd weather-lab && git add -A && git commit -m "weather-lab: copy sky cluster + thin ILightingHost; look-complete sky over the infinite world"
```

---

## SLICE 1 — The C# brain + SkyCoupling: the sky weathers over time

> `Weather.Core` is pure logic → **real TDD (xunit)**. The harness `SkyCoupling`/clock task → build + eye-gate + `--weathercheck`.

### Task 1.1: `WeatherState` snapshot + `PrecipType` (Core, TDD)

**Files:**
- Create: `weather-lab/Weather.Core/WeatherState.cs`
- Test: `weather-lab/Weather.Tests/WeatherStateTests.cs`

**Interfaces:**
- Produces: `Weather.Core.PrecipType` enum; `Weather.Core.WeatherState` struct with fields `CloudCoverage, CloudType, PrecipType, PrecipIntensity, WindDirRad, WindSpeed, Gustiness, TemperatureC, Humidity, FogDensity, VisibilityM, LightningRate, Wetness, SnowDepth`; static presets `Clear`, `Overcast`, `Rain`.

- [ ] **Step 1: Write the failing test**

```csharp
using Weather.Core;
using Xunit;

public class WeatherStateTests
{
    [Fact]
    public void Clear_preset_is_dry_and_bright()
    {
        var s = WeatherState.Clear;
        Assert.Equal(PrecipType.None, s.PrecipType);
        Assert.True(s.CloudCoverage <= 0.15f);
        Assert.True(s.VisibilityM >= 20000f);
        Assert.Equal(0f, s.PrecipIntensity);
    }

    [Fact]
    public void Rain_preset_is_wet_and_overcast()
    {
        var s = WeatherState.Rain;
        Assert.Equal(PrecipType.Rain, s.PrecipType);
        Assert.True(s.CloudCoverage >= 0.7f);
        Assert.True(s.PrecipIntensity > 0f);
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherStateTests`
Expected: FAIL — `WeatherState` / `PrecipType` not defined.

- [ ] **Step 3: Implement**

```csharp
namespace Weather.Core;

public enum PrecipType { None, Rain, Sleet, Snow, Hail, Dust }

public struct WeatherState
{
    public float CloudCoverage;     // 0..1
    public float CloudType;         // 0..1 stratus->cumulus->cb
    public PrecipType PrecipType;
    public float PrecipIntensity;   // 0..1
    public float WindDirRad;
    public float WindSpeed;         // 0..1 normalized
    public float Gustiness;         // 0..1
    public float TemperatureC;
    public float Humidity;          // 0..1
    public float FogDensity;        // 0..1
    public float VisibilityM;
    public float LightningRate;     // strikes/min
    public float Wetness;           // 0..1 integrator
    public float SnowDepth;         // 0..1 integrator

    public static WeatherState Clear => new()
    {
        CloudCoverage = 0.05f, CloudType = 0.1f, PrecipType = PrecipType.None, PrecipIntensity = 0f,
        WindDirRad = 0.4f, WindSpeed = 0.15f, Gustiness = 0.1f, TemperatureC = 18f, Humidity = 0.3f,
        FogDensity = 0.0f, VisibilityM = 40000f, LightningRate = 0f, Wetness = 0f, SnowDepth = 0f,
    };

    public static WeatherState Overcast => new()
    {
        CloudCoverage = 0.85f, CloudType = 0.35f, PrecipType = PrecipType.None, PrecipIntensity = 0f,
        WindDirRad = 0.4f, WindSpeed = 0.35f, Gustiness = 0.25f, TemperatureC = 12f, Humidity = 0.7f,
        FogDensity = 0.1f, VisibilityM = 12000f, LightningRate = 0f, Wetness = 0f, SnowDepth = 0f,
    };

    public static WeatherState Rain => new()
    {
        CloudCoverage = 0.95f, CloudType = 0.7f, PrecipType = PrecipType.Rain, PrecipIntensity = 0.6f,
        WindDirRad = 0.4f, WindSpeed = 0.55f, Gustiness = 0.45f, TemperatureC = 9f, Humidity = 0.95f,
        FogDensity = 0.25f, VisibilityM = 4000f, LightningRate = 0f, Wetness = 0.8f, SnowDepth = 0f,
    };
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherStateTests` → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd weather-lab && git add Weather.Core/WeatherState.cs Weather.Tests/WeatherStateTests.cs && git commit -m "Weather.Core: WeatherState snapshot + PrecipType + presets"
```

---

### Task 1.2: `WeatherMath` — exp easing, precip derivation, integrators (Core, TDD)

**Files:**
- Create: `weather-lab/Weather.Core/WeatherMath.cs`
- Test: `weather-lab/Weather.Tests/WeatherMathTests.cs`

**Interfaces:**
- Produces: `static class WeatherMath` with `float ExpEase(float current, float target, float rate, float dt)`; `PrecipType DerivePrecip(float tempC, float humidity, float cloudType, float rainSnowThresholdC)`; `float IntegrateWetness(float prev, float precipIntensity, float dt, float evapRate)`; `float IntegrateSnow(float prev, bool isSnow, float tempC, float dt, float accumK, float degreeDayFactor)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Weather.Core;
using Xunit;

public class WeatherMathTests
{
    [Fact]
    public void ExpEase_is_framerate_independent()
    {
        // one big step vs two half steps must converge to the same value
        float oneStep = WeatherMath.ExpEase(0f, 1f, 2.0f, 1.0f);
        float half = WeatherMath.ExpEase(0f, 1f, 2.0f, 0.5f);
        float twoHalf = WeatherMath.ExpEase(half, 1f, 2.0f, 0.5f);
        Assert.True(System.MathF.Abs(oneStep - twoHalf) < 1e-5f);
    }

    [Fact]
    public void ExpEase_moves_toward_target_and_never_overshoots()
    {
        float v = 0f;
        for (int i = 0; i < 1000; i++) v = WeatherMath.ExpEase(v, 1f, 1.5f, 0.05f);
        Assert.True(v > 0.99f && v <= 1f);
    }

    [Theory]
    [InlineData(-5f, 0.9f, 0.6f, PrecipType.Snow)]
    [InlineData(9f, 0.95f, 0.7f, PrecipType.Rain)]
    [InlineData(20f, 0.1f, 0.1f, PrecipType.None)]   // too dry / too thin
    public void DerivePrecip_follows_temperature_and_moisture(float t, float h, float ct, PrecipType expected)
    {
        Assert.Equal(expected, WeatherMath.DerivePrecip(t, h, ct, rainSnowThresholdC: 1.0f));
    }

    [Fact]
    public void Wetness_accumulates_under_rain_and_dries_when_dry()
    {
        float w = WeatherMath.IntegrateWetness(0f, precipIntensity: 0.8f, dt: 1f, evapRate: 0.1f);
        Assert.True(w > 0f);
        float dried = WeatherMath.IntegrateWetness(w, precipIntensity: 0f, dt: 100f, evapRate: 0.1f);
        Assert.True(dried < w);
    }

    [Fact]
    public void Snow_melts_above_freezing()
    {
        float s = WeatherMath.IntegrateSnow(0.5f, isSnow: false, tempC: 10f, dt: 1f, accumK: 0.2f, degreeDayFactor: 0.05f);
        Assert.True(s < 0.5f);
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherMathTests` → Expected: FAIL — `WeatherMath` undefined.

- [ ] **Step 3: Implement**

```csharp
using System;

namespace Weather.Core;

public static class WeatherMath
{
    // Frame-rate-independent exponential decay toward target.
    public static float ExpEase(float current, float target, float rate, float dt)
        => target + (current - target) * MathF.Pow(2f, -rate * dt);

    public static PrecipType DerivePrecip(float tempC, float humidity, float cloudType, float rainSnowThresholdC)
    {
        if (humidity < 0.6f || cloudType < 0.3f) return PrecipType.None;
        if (tempC <= rainSnowThresholdC - 1.5f) return PrecipType.Snow;
        if (tempC <= rainSnowThresholdC + 1.0f) return PrecipType.Sleet;
        return PrecipType.Rain;
    }

    public static float IntegrateWetness(float prev, float precipIntensity, float dt, float evapRate)
    {
        float wet = prev + precipIntensity * dt;          // accumulate while raining
        wet -= evapRate * dt;                              // evaporate
        return Math.Clamp(wet, 0f, 1f);
    }

    public static float IntegrateSnow(float prev, bool isSnow, float tempC, float dt, float accumK, float degreeDayFactor)
    {
        float s = prev + (isSnow ? accumK * dt : 0f);      // accumulate
        float melt = degreeDayFactor * MathF.Max(0f, tempC) * dt;  // degree-day melt
        s -= melt;
        return Math.Clamp(s, 0f, 1f);
    }
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherMathTests` → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd weather-lab && git add Weather.Core/WeatherMath.cs Weather.Tests/WeatherMathTests.cs && git commit -m "Weather.Core: WeatherMath (exp ease, precip derivation, integrators)"
```

---

### Task 1.3: `WeatherField` — deterministic world-anchored value noise (Core, TDD)

**Files:**
- Create: `weather-lab/Weather.Core/WeatherField.cs`
- Test: `weather-lab/Weather.Tests/WeatherFieldTests.cs`

**Interfaces:**
- Produces: `static class WeatherField` with `(float synopticPressure, float frontalActivity) Sample(uint seed, float worldX, float worldZ, float timeHours)` — pure, deterministic, returns `synopticPressure ∈ [-1,1]`, `frontalActivity ∈ [0,1]`. Low-frequency; wind-advected by folding time into the sample coordinate (domain scroll) — but the advection vector is applied by the *caller* (the sim); `Sample` is the raw field.

- [ ] **Step 1: Write the failing test**

```csharp
using Weather.Core;
using Xunit;

public class WeatherFieldTests
{
    [Fact]
    public void Sample_is_deterministic()
    {
        var a = WeatherField.Sample(42u, 1000f, 2000f, 5.0f);
        var b = WeatherField.Sample(42u, 1000f, 2000f, 5.0f);
        Assert.Equal(a.synopticPressure, b.synopticPressure, 6);
        Assert.Equal(a.frontalActivity, b.frontalActivity, 6);
    }

    [Fact]
    public void Different_seed_gives_different_weather()
    {
        var a = WeatherField.Sample(1u, 1000f, 2000f, 5.0f);
        var b = WeatherField.Sample(2u, 1000f, 2000f, 5.0f);
        Assert.NotEqual(a.synopticPressure, b.synopticPressure, 4);
    }

    [Fact]
    public void Outputs_are_in_range()
    {
        for (int i = 0; i < 500; i++)
        {
            float x = i * 137.0f, z = i * 91.0f, t = i * 0.37f;
            var (p, f) = WeatherField.Sample(7u, x, z, t);
            Assert.InRange(p, -1f, 1f);
            Assert.InRange(f, 0f, 1f);
        }
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherFieldTests` → Expected: FAIL.

- [ ] **Step 3: Implement** (value-noise FBM; low world frequency so weather spans kilometres; time as a third hashed axis)

```csharp
using System;

namespace Weather.Core;

public static class WeatherField
{
    // ~8 km horizontal period for the lowest octave; weather varies over kilometres.
    private const float Space = 1f / 8000f;
    private const float TimeScale = 0.05f;

    public static (float synopticPressure, float frontalActivity) Sample(uint seed, float worldX, float worldZ, float timeHours)
    {
        float u = worldX * Space, v = worldZ * Space, w = timeHours * TimeScale;
        float p = Fbm(u, v, w, seed) * 2f - 1f;                 // [-1,1]
        float f = MathF.Abs(Fbm(u + 11.3f, v - 7.1f, w, seed ^ 0x9E3779B9u)); // [0,1]
        return (Math.Clamp(p, -1f, 1f), Math.Clamp(f, 0f, 1f));
    }

    private static float Fbm(float x, float y, float z, uint seed)
    {
        float sum = 0f, amp = 0.5f, norm = 0f, freq = 1f;
        for (int i = 0; i < 4; i++)
        {
            sum += amp * VNoise(x * freq, y * freq, z * freq, seed + (uint)i * 1013u);
            norm += amp; amp *= 0.5f; freq *= 2f;
        }
        return sum / MathF.Max(norm, 1e-5f);
    }

    private static float VNoise(float x, float y, float z, uint seed)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y), iz = (int)MathF.Floor(z);
        float fx = x - ix, fy = y - iy, fz = z - iz;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
        float c000 = Hash(ix, iy, iz, seed),     c100 = Hash(ix + 1, iy, iz, seed);
        float c010 = Hash(ix, iy + 1, iz, seed), c110 = Hash(ix + 1, iy + 1, iz, seed);
        float c001 = Hash(ix, iy, iz + 1, seed), c101 = Hash(ix + 1, iy, iz + 1, seed);
        float c011 = Hash(ix, iy + 1, iz + 1, seed), c111 = Hash(ix + 1, iy + 1, iz + 1, seed);
        float x00 = Lerp(c000, c100, fx), x10 = Lerp(c010, c110, fx);
        float x01 = Lerp(c001, c101, fx), x11 = Lerp(c011, c111, fx);
        return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Hash(int x, int y, int z, uint seed)
    {
        uint h = seed;
        h ^= (uint)x * 0x8DA6B343u; h ^= (uint)y * 0xD8163841u; h ^= (uint)z * 0xCB1AB31Fu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0x1000000;   // [0,1)
    }
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherFieldTests` → Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd weather-lab && git add Weather.Core/WeatherField.cs Weather.Tests/WeatherFieldTests.cs && git commit -m "Weather.Core: deterministic world-anchored WeatherField"
```

---

### Task 1.4: `WeatherPreset` + `WeatherSim` — target-easing brain (Core, TDD)

**Files:**
- Create: `weather-lab/Weather.Core/WeatherPreset.cs`, `weather-lab/Weather.Core/WeatherSim.cs`
- Test: `weather-lab/Weather.Tests/WeatherSimTests.cs`

**Interfaces:**
- Consumes: `WeatherState`, `WeatherMath`, `WeatherField`.
- Produces:
  - `WeatherPreset` = a target `WeatherState` + a name. `WeatherPreset.Library` = `{ Clear, Overcast, Rain }`.
  - `class WeatherSim` with: ctor `WeatherSim(uint seed, WeatherState initial)`; `WeatherState State {get;}`; `void Tick(float dt, WeatherState target)` (ease all scalar fields toward target, derive precip, run integrators); `WeatherState Reconstruct(float timeHours, float worldX, float worldZ)` (deterministic target from the field — used by the harness clock and by determinism tests).

- [ ] **Step 1: Write the failing test**

```csharp
using Weather.Core;
using Xunit;

public class WeatherSimTests
{
    [Fact]
    public void Eases_from_clear_toward_rain_without_jumping()
    {
        var sim = new WeatherSim(1u, WeatherState.Clear);
        float prevCoverage = sim.State.CloudCoverage;
        float maxStep = 0f;
        for (int i = 0; i < 600; i++) // 10s at 60fps
        {
            sim.Tick(1f / 60f, WeatherState.Rain);
            maxStep = System.MathF.Max(maxStep, System.MathF.Abs(sim.State.CloudCoverage - prevCoverage));
            prevCoverage = sim.State.CloudCoverage;
        }
        Assert.True(sim.State.CloudCoverage > 0.8f);          // reached rain
        Assert.True(maxStep < 0.05f);                          // no per-frame pop
    }

    [Fact]
    public void Derives_rain_precip_once_cold_and_humid()
    {
        var sim = new WeatherSim(1u, WeatherState.Clear);
        for (int i = 0; i < 2000; i++) sim.Tick(1f / 60f, WeatherState.Rain);
        Assert.Equal(PrecipType.Rain, sim.State.PrecipType);
        Assert.True(sim.State.Wetness > 0.3f);
    }

    [Fact]
    public void Reconstruct_is_deterministic_for_seed_time_pos()
    {
        var a = new WeatherSim(5u, WeatherState.Clear).Reconstruct(12f, 1000f, 500f);
        var b = new WeatherSim(5u, WeatherState.Clear).Reconstruct(12f, 1000f, 500f);
        Assert.Equal(a.CloudCoverage, b.CloudCoverage, 5);
        Assert.Equal(a.TemperatureC, b.TemperatureC, 5);
    }
}
```

- [ ] **Step 2: Run, verify it fails**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherSimTests` → Expected: FAIL.

- [ ] **Step 3: Implement**

`WeatherPreset.cs`:
```csharp
namespace Weather.Core;

public readonly struct WeatherPreset
{
    public readonly string Name;
    public readonly WeatherState Target;
    public WeatherPreset(string name, WeatherState target) { Name = name; Target = target; }

    public static readonly WeatherPreset[] Library =
    {
        new("Clear", WeatherState.Clear),
        new("Overcast", WeatherState.Overcast),
        new("Rain", WeatherState.Rain),
    };
}
```

`WeatherSim.cs`:
```csharp
using System;

namespace Weather.Core;

public sealed class WeatherSim
{
    private readonly uint _seed;
    private WeatherState _s;
    public WeatherState State => _s;

    // per-field easing rates (1/sec): clouds build slowly, fog/wind faster.
    // RCoverage=0.30 (NOT 0.25) so coverage eases 0.05->0.95 past 0.80 within the
    // test's 10 s window (0.95+(0.05-0.95)*2^(-0.30*10)=0.8375); first-frame step
    // stays 0.0031 (<0.05, no pop). Audit ran the suite: 0.25 fails, 0.30 = 15/15 green.
    private const float RCoverage = 0.30f, RType = 0.3f, RWind = 0.5f, RTemp = 0.15f,
                        RHumidity = 0.3f, RFog = 0.4f, RVis = 0.3f;

    public WeatherSim(uint seed, WeatherState initial) { _seed = seed; _s = initial; }

    public void Tick(float dt, WeatherState target)
    {
        _s.CloudCoverage  = WeatherMath.ExpEase(_s.CloudCoverage,  target.CloudCoverage,  RCoverage, dt);
        _s.CloudType      = WeatherMath.ExpEase(_s.CloudType,      target.CloudType,      RType,     dt);
        _s.WindDirRad     = WeatherMath.ExpEase(_s.WindDirRad,     target.WindDirRad,     RWind,     dt);
        _s.WindSpeed      = WeatherMath.ExpEase(_s.WindSpeed,      target.WindSpeed,      RWind,     dt);
        _s.Gustiness      = WeatherMath.ExpEase(_s.Gustiness,      target.Gustiness,      RWind,     dt);
        _s.TemperatureC   = WeatherMath.ExpEase(_s.TemperatureC,   target.TemperatureC,   RTemp,     dt);
        _s.Humidity       = WeatherMath.ExpEase(_s.Humidity,       target.Humidity,       RHumidity, dt);
        _s.FogDensity     = WeatherMath.ExpEase(_s.FogDensity,     target.FogDensity,     RFog,      dt);
        _s.VisibilityM    = WeatherMath.ExpEase(_s.VisibilityM,    target.VisibilityM,    RVis,      dt);

        _s.PrecipType = WeatherMath.DerivePrecip(_s.TemperatureC, _s.Humidity, _s.CloudType, rainSnowThresholdC: 1.0f);
        _s.PrecipIntensity = WeatherMath.ExpEase(
            _s.PrecipIntensity,
            _s.PrecipType == PrecipType.None ? 0f : target.PrecipIntensity,
            RCoverage, dt);

        bool isSnow = _s.PrecipType == PrecipType.Snow;
        _s.Wetness   = WeatherMath.IntegrateWetness(_s.Wetness, isSnow ? 0f : _s.PrecipIntensity, dt, evapRate: 0.03f);
        _s.SnowDepth = WeatherMath.IntegrateSnow(_s.SnowDepth, isSnow, _s.TemperatureC, dt, accumK: 0.05f, degreeDayFactor: 0.02f);
    }

    // Deterministic target from the world-anchored field: blends the nearest presets
    // by the synoptic-pressure axis (continuous target — no popping).
    public WeatherState Reconstruct(float timeHours, float worldX, float worldZ)
    {
        var (p, _) = WeatherField.Sample(_seed, worldX, worldZ, timeHours);
        // map pressure [-1,1] -> [Clear .. Overcast .. Rain]
        float t = (p + 1f) * 0.5f;          // [0,1]
        if (t < 0.5f) return Blend(WeatherState.Clear, WeatherState.Overcast, t / 0.5f);
        return Blend(WeatherState.Overcast, WeatherState.Rain, (t - 0.5f) / 0.5f);
    }

    private static WeatherState Blend(WeatherState a, WeatherState b, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return new WeatherState
        {
            CloudCoverage = Lerp(a.CloudCoverage, b.CloudCoverage, k),
            CloudType     = Lerp(a.CloudType, b.CloudType, k),
            PrecipType    = k > 0.5f ? b.PrecipType : a.PrecipType,
            PrecipIntensity = Lerp(a.PrecipIntensity, b.PrecipIntensity, k),
            WindDirRad    = Lerp(a.WindDirRad, b.WindDirRad, k),
            WindSpeed     = Lerp(a.WindSpeed, b.WindSpeed, k),
            Gustiness     = Lerp(a.Gustiness, b.Gustiness, k),
            TemperatureC  = Lerp(a.TemperatureC, b.TemperatureC, k),
            Humidity      = Lerp(a.Humidity, b.Humidity, k),
            FogDensity    = Lerp(a.FogDensity, b.FogDensity, k),
            VisibilityM   = Lerp(a.VisibilityM, b.VisibilityM, k),
            LightningRate = 0f, Wetness = 0f, SnowDepth = 0f,
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
```

- [ ] **Step 4: Run, verify pass**

Run: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj --filter WeatherSimTests` → Expected: PASS. Then run the FULL suite: `dotnet test weather-lab/Weather.Tests/Weather.Tests.csproj` → all green.

- [ ] **Step 5: Commit**

```bash
cd weather-lab && git add Weather.Core/WeatherPreset.cs Weather.Core/WeatherSim.cs Weather.Tests/WeatherSimTests.cs && git commit -m "Weather.Core: WeatherSim target-easing brain + preset library"
```

---

### Task 1.5: `SkyCoupling` + weather clock + `--weathercheck` (Harness, eye-gate)

**Files:**
- Create: `weather-lab/WeatherLab/SkyCoupling.cs`
- Modify: `weather-lab/WeatherLab/Main.cs`

**Interfaces:**
- Consumes: `Weather.Core.WeatherSim`, `Weather.Core.WeatherState`, `WG16.Lab.CloudVolume`.
- Produces: a per-frame loop that ticks the brain toward a cycling target and pushes the state into the sky; a `--weathercheck` CLI self-check that asserts the coverage eased toward the target and exits 0/1.

- [ ] **Step 1: Create `SkyCoupling.cs`** (the first render module — a pure consumer of `WeatherState`)

```csharp
using Godot;
using WG16.Lab;

// Maps a Weather.Core.WeatherState snapshot onto the existing cloud sky uniforms.
// Pure consumer: it never computes weather, only renders it.
public sealed class SkyCoupling
{
    private readonly CloudVolume _cloud;
    public bool Enabled = true;  // default-on here because it's the slice's deliverable; A/B via key in a later slice
    public SkyCoupling(CloudVolume cloud) { _cloud = cloud; }

    public void Apply(in Weather.Core.WeatherState s)
    {
        if (!Enabled) return;
        _cloud.SetKnob("coverage", s.CloudCoverage);
        _cloud.SetKnob("cloud_type", s.CloudType);
        _cloud.SetKnob("density", Mathf.Lerp(0.4f, 1.0f, s.CloudCoverage)); // thicker as it covers
    }
}
```

- [ ] **Step 2: Wire the brain + coupling + clock into `Main.cs`**

Add fields and extend `_Ready`/`_Process`. A simple demo clock cycles Clear→Overcast→Rain→Clear every ~40 s so the eye-gate shows the sky weathering. (Fully-qualify `Weather.Core.WeatherState` to avoid the `WG16.Lab.WeatherState` clash.)

```csharp
// fields:
private Weather.Core.WeatherSim _sim = null!;
private SkyCoupling _sky = null!;
private float _weatherClock;
private bool _weatherCheck;   // --weathercheck
private float _checkElapsed;  // accumulated dt for the check (NOT a frame count)

// in _Ready(), AFTER the sky is wired:
_sim = new Weather.Core.WeatherSim(1u, Weather.Core.WeatherState.Clear);
_sky = new SkyCoupling(_cloud);
foreach (var a in OS.GetCmdlineUserArgs())
    if (a == "--weathercheck") _weatherCheck = true;

// in _Process(delta), AFTER the sky tick:
// --weathercheck: isolate the transition — drive toward Rain for 10 s of ELAPSED time,
// then assert + exit. Early-return so the normal clock path does NOT also tick the sim.
// GATE ON ACCUMULATED dt, NOT FRAME COUNT: the scene runs well above 60 fps, so a
// 600-frame gate is only ~3 s and under-eases coverage (verified: 600 frames → 0.47, FAIL).
// 10 s elapsed → 0.95+(0.05-0.95)*2^(-0.30*10)=0.84 > 0.7 → PASS (matches the unit test).
if (_weatherCheck)
{
    _sim.Tick((float)delta, Weather.Core.WeatherState.Rain);
    _sky.Apply(_sim.State);
    _checkElapsed += (float)delta;
    if (_checkElapsed > 10f)
    {
        bool ok = _sim.State.CloudCoverage > 0.7f && _sim.State.PrecipType == Weather.Core.PrecipType.Rain;
        GD.Print($"[weathercheck] t={_checkElapsed:F1}s coverage={_sim.State.CloudCoverage:F2} precip={_sim.State.PrecipType} -> {(ok ? "PASS" : "FAIL")}");
        GetTree().Quit(ok ? 0 : 1);
    }
    return;
}

_weatherClock += (float)delta;
// cycle target: Clear (0-13s) -> Overcast (13-26s) -> Rain (26-40s) -> repeat
float phase = _weatherClock % 40f;
Weather.Core.WeatherState target =
    phase < 13f ? Weather.Core.WeatherState.Clear :
    phase < 26f ? Weather.Core.WeatherState.Overcast :
                  Weather.Core.WeatherState.Rain;
_sim.Tick((float)delta, target);
_sky.Apply(_sim.State);
```
> **Determinism demonstration (optional, spec §3.3):** the demo clock above uses fixed presets so the eye-gate is unambiguous. To actually *fly the deterministic world-anchored field*, swap the target line for `var target = _sim.Reconstruct(_weatherClock / 3600f, camPos.X, camPos.Z);` — weather then varies by world position + time from `(seed, time, worldXZ)`, demonstrating the keyframe-reconstructable property the spec requires. Keep the preset cycle as the default first eye-gate; add `Reconstruct` driving behind a `--worldweather` flag.

- [ ] **Step 3: Build**

Run: `dotnet build weather-lab/WeatherLab/WeatherLab.csproj` → Expected: `Build succeeded`.

- [ ] **Step 4: Mechanical self-check (exit code)**

Run: `<godot4.6> --path <abs>/weather-lab/WeatherLab -- --weathercheck`
Expected: prints `[weathercheck] coverage=0.xx precip=Rain -> PASS` and the process exits with code 0. (Check `echo $?` = 0.)

- [ ] **Step 5: Eye-gate (windowed)**

Run: `<godot4.6> --path <abs>/weather-lab/WeatherLab`
Expected: over ~40 s the sky visibly cycles clear → overcast → heavy/rainy cloud and back, smoothly (no popping), while you fly the infinite world. This is the Slice-1 deliverable: **the sky weathers over time, driven by the C# brain.**

- [ ] **Step 6: Commit**

```bash
cd weather-lab && git add WeatherLab/SkyCoupling.cs WeatherLab/Main.cs && git commit -m "weather-lab: SkyCoupling + weather clock; sky weathers Clear/Overcast/Rain over time (--weathercheck)"
```

---

## Self-Review

**Spec coverage (spec §→task):**
- §2 repo shape → Task 0.1. §2.1 copy-set (terrain) → Task 0.2; (sky + de-glue) → Task 0.3.
- §3.1 `WeatherState` → Task 1.1. §3.2 sim/easing/derive/integrators → Tasks 1.2, 1.4. §3.3 determinism → Tasks 1.3, 1.4 (`Reconstruct` test). §3.4 world-anchored field → Task 1.3.
- §4.1 `SkyCoupling` → Task 1.5. §4.2–4.6 (precip/wetness/fog/lightning/wind) → **deferred to follow-on plans** (per spec: precip perf must be profiled in the running lab first).
- §5 harness/CLI/self-check → Tasks 0.1, 1.5 (`--weathercheck`). §6 slices 0–1 → this plan; slices 2–4 → follow-on. §7 perf framing → measured during eye-gates; §8 risks → carried as build/eye-gate steps (de-glue closure in 0.2/0.3; renderOrigin snap watched in 0.2).

**Placeholder scan:** Core tasks have full test + impl code. Harness tasks reference copied files by exact name + verified public signatures; the one genuine unknown (the sky dependency *closure* and `TerrainLab`'s exact build-entry name) is handled by an explicit **compile-driven resolution step** (copy the file the compiler names) rather than a vague "wire it up" — this is the correct method for a verbatim extraction, not a placeholder.

**Type consistency:** `Weather.Core.WeatherState` fields are referenced identically across Tasks 1.1/1.4/1.5. `WeatherMath` method names match their call sites in `WeatherSim`. `ILightingHost`'s 11 members in `ThinLightingHost` (Task 0.3) match the verified interface. `CloudVolume.SetKnob`, `CdlodTerrain.Tick/RenderOrigin`, `LightingComposer(ILightingHost)` are the verified signatures.

**Audit applied (2026-06-28, 4-auditor workflow incl. a run that *compiled + ran* the Core suite):**
- **Core tests PROVEN:** the `Weather.Core` solution was materialized and `dotnet test`-run → 14/15, the one failure being `Eases_from_clear_toward_rain` (coverage reached 0.7909 < 0.8 at `RCoverage=0.25`). Fixed to `0.30` → re-run **15/15 green**. (The earlier "all green" self-claim was false; it is now verified.)
- **Slice-0 blockers fixed:** `TerrainLab.Build(FieldCompute, FieldParams)` + `SetCdlod(true)` + `SetCameraWorld` are now explicit (it does not self-build); the scene root is `TerrainLabRoot` so the copied `LightingComposer`'s hardcoded `/root/TerrainLabRoot/{Env,Sun}` paths resolve (was a per-frame crash); `BindWaterRegion` is deleted to cut the `WG16.Hydrology` cluster; `TerrainTestPaths.cs` added to the copy-set; `SkyPresets`/`CloudPresets` dropped (registry glue); `atmosphere_aerial_v2.glsl` added; data list corrected to `time/grade/weather_presets.json`; the double `_Process` manual calls removed; ground uses `SetTexturesOn(false)` placeholder.
- **Atmosphere is opt-in** (`_atmosphereOn=false`): clouds-only is the verified-sufficient Slice-0 sky; atmosphere LUT physical cloud-lighting is a follow-up.

**Residual unknowns to verify at execution (compile/load-driven, can't be seen statically):** (1) `TerrainTestPaths.cs`'s own small closure; (2) the exact cloud/atmosphere shader include filenames; (3) whether `AtmosphereCompute.Ready` goes true once enabled (render-thread shader-load) — gated behind the opt-in flag.

**Forward-pointers (carried into follow-on plans, not Slice 0/1 tasks):** spec §4.3 Toksvig-order wetness rule + §8 `--snapdiff` straddle-shot belong to the wetness/precip slices; spec §3.3 keyframe-reconciliation is *demonstrated* (not just unit-tested) via the optional `Reconstruct`-driven `--worldweather` path in Task 1.5.
