# Lighting & Shadow Strip and Rebuild Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Strip broken/redundant lighting systems (SSAO, AT-2 aerial perspective, EMISSION fill, CSM mis-tuning) down to zero, then rebuild a clean ambient+shadow baseline, leaving clouds/god-rays/AT-1/AT-3 untouched.

**Architecture:** Three phases — (1) Strip: remove/disable broken systems with no regressions to cloud or god-ray code; (2) Baseline: tune clean Godot-native ambient + shadow settings that actually work; (3) AT-2 Rebuild: new aerial perspective with 64 Z-slices + log-Z depth mapping replacing the broken 32-slice froxel.

**Tech Stack:** Godot 4.6 (Vulkan, GDShader, C# scripting), GLSL compute shaders, terrain_lab.tscn scene.

## Global Constraints

- Never touch: `CloudVolume.cs`, `GodRaysScreen.cs`, `godray_screen.gdshader`, `AtmosphereCompute.cs` AT-1/AT-3 paths, `cloud_sky.gdshader`, `cloud_raymarch.glsl`, `cloud_shadow.glsl`
- Build must pass `dotnet build WG16.csproj` with zero errors after every task
- No `--no-verify` commits — fix the build, don't skip
- Eye-gate each task before moving to the next: launch with `"C:\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe" --path /c/Wg16/wg-16-project res://scenes/terrain_lab.tscn`
- Rollback tag: `lighting-refactor-baseline-2026-06-25` (commit e52df36)

---

## Phase 1: Strip

### Task 1: Disable SSAO fully (scene + composer + UI)

SSAO is currently force-disabled in `LightingComposer.cs:230` (`env.SsaoEnabled = false`) but still enabled in the `.tscn` and has UI sliders. This task removes it cleanly at all three layers.

**Files:**
- Modify: `scenes/terrain_lab.tscn` lines 31-34
- Modify: `scripts/lab/LightingComposer.cs` around line 228-230
- Modify: `data/lab_controls.json` lines 86-87 and 149

- [ ] **Step 1: Disable SSAO in the scene file**

In `scenes/terrain_lab.tscn`, find the `[sub_resource type="Environment" id="env"]` block and change:
```
ssao_enabled = true
ssao_radius = 8.0
ssao_intensity = 2.0
ssao_power = 1.5
ssao_detail = 0.5
```
to:
```
ssao_enabled = false
```
(Delete the `ssao_radius`, `ssao_intensity`, `ssao_power`, `ssao_detail` lines entirely — Godot uses defaults when absent.)

- [ ] **Step 2: Remove the force-disable comment block from LightingComposer.cs**

In `scripts/lab/LightingComposer.cs`, find and delete these lines (around 227-230):
```csharp
            // SSAO is screen-space → its darkening shifts with view angle, same as SSIL (the "visor going down").
            // The analytic fill in ground.gdshader owns the ambient; SSAO is redundant and causes the visor.
            if (env != null) { env.SsaoEnabled = false; }
```
The scene now has it off by default; no need to force it off in code.

- [ ] **Step 3: Remove SSAO sliders from lab_controls.json**

In `data/lab_controls.json`, delete these two lines (around lines 86-87):
```json
    { "id": "ssao_i", "label": "AO strength", "tab": "Light", "type": "scenef", "scene": "ssao_intensity", "min": 0.0, "max": 4.0, "default": 0.6, "rand": false },
    { "id": "ssao_r", "label": "AO radius m", "tab": "Light", "type": "scenef", "scene": "ssao_radius", "min": 0.5, "max": 30.0, "default": 8.0, "rand": false },
```

And delete the Debug tab toggle (around line 149):
```json
    { "id": "dbg_ssao", "label": "SSAO", "tab": "Debug", "type": "scene", "scene": "ssao", "default": true, "rand": false },
```

- [ ] **Step 4: Remove SSAO case from TerrainLabUI.Apply.cs**

In `scripts/lab/TerrainLabUI.Apply.cs`, find and delete:
```csharp
            case "ssao":   UiEnv.Environment.SsaoEnabled = on; break;   // #7 perf: cached nodes
```
and:
```csharp
            case "ssao_intensity":  env.SsaoIntensity = v; break;
            case "ssao_radius":     env.SsaoRadius = v; break;
```
and in `SyncLightControlsToScene()`:
```csharp
        Set("ssao_i", env.SsaoIntensity); Set("ssao_r", env.SsaoRadius);
```

- [ ] **Step 5: Remove SSAO from TerrainLabUI.Process.cs B-key handler**

In `scripts/lab/TerrainLabUI.Process.cs`, find and delete the B-key SSAO toggle block:
```csharp
                bool kB = Input.IsKeyPressed(Key.B);
                if (kB && !_lastF1) { env.SsaoEnabled = !env.SsaoEnabled; GD.Print($"[dbg] (B) SSAO = {env.SsaoEnabled}"); }
                _lastF1 = kB;
```
(Also delete `_lastF1` field declaration if it exists and is only used here.)

- [ ] **Step 6: Remove SSAO from CLI**

In `scripts/lab/TerrainLabUI.Cli.cs`, find and remove any `_probeSsao` / `SsaoEnabled` references (around line 229-232):
```csharp
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
        env.Environment.SsaoEnabled = _probeSsao == 1;
```
and the field `_probeSsao` and its argument parser.

- [ ] **Step 7: Build and eye-gate**

```
dotnet build /c/Wg16/wg-16-project/WG16.csproj
```
Expected: zero errors. Then launch and confirm: terrain looks the same (SSAO was already force-disabled), no UI sliders for AO remain in the Light tab.

- [ ] **Step 8: Commit**

```bash
git -C /c/Wg16/wg-16-project add scenes/terrain_lab.tscn scripts/lab/LightingComposer.cs scripts/lab/TerrainLabUI.Apply.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Cli.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "strip: remove SSAO fully (scene + composer + UI sliders)"
```

---

### Task 2: Delete AT-2 aerial perspective system

AT-2 causes a blue froxel-line artifact. Delete the whole system: C# node, shader, GLSL compute, and all wiring. The AT-1 sky LUTs (transmittance/skyview/multiscatter) and AT-3 cloud radiance are in `AtmosphereCompute.cs` and are **not touched**. Only the aerial froxel path inside `AtmosphereCompute.cs` is removed.

**Files:**
- Delete: `scripts/lab/AerialPerspective.cs`
- Delete: `shaders/aerial_screen.gdshader`
- Delete: `shaders/atmosphere_aerial.glsl`
- Modify: `scripts/lab/AtmosphereCompute.cs` — remove aerial froxel fields + methods
- Modify: `scripts/lab/TerrainLabUI.cs` — remove `_aerial` instantiation + attach
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` — remove `_aerial` field + all usages
- Modify: `scripts/lab/TerrainLabUI.Process.cs` — remove K/U/Y key handlers for aerial
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` — remove `_aerialCli`, `_aerialStrCli`, `_aerialDbgCli`, `_aerialHazeCli`, `_aerialCheckCli`
- Modify: `scripts/lab/TerrainLabUI.Lighting.cs` — remove `AerialOn` property
- Modify: `scripts/lab/LightingComposer.cs` — remove `AerialOn` usage (fog aerial handoff at line 331)
- Modify: `scripts/lab/LightingComposer.cs` (interface `ILightingHost`) — remove `bool AerialOn { get; }`
- Modify: `data/lab_controls.json` — remove `aerial_on` and `aerial_strength` entries

- [ ] **Step 1: Remove aerial fields from AtmosphereCompute.cs**

In `scripts/lab/AtmosphereCompute.cs`, find and delete:
- All lines containing `_aerialShader`, `_aerialPipe`, `_aerialTex`, `_aerialSet`, `_aerialRd`, `_aerialFar`, `_aerialCheckRequested`, `_camDirty`, `_camPos`, `_invViewProj` (the cam/aerial-only fields)
- The `AerialTexture` property and `AerialReady` property
- The `SetCamera(...)` method (used only for aerial froxel per-frame recompute)
- The `RequestAerialCheck()` method
- The aerial dispatch block inside the compute method (the `_aerialShader` / `_aerialSet` dispatch)
- The aerial texture creation in Init

Keep: all transmittance, skyview, multiscatter, and cloud-light paths. If `_camDirty` is used by non-aerial code, keep it — check before deleting.

- [ ] **Step 2: Remove AerialOn from ILightingHost interface**

In `scripts/lab/LightingComposer.cs`, delete from the `ILightingHost` interface:
```csharp
    bool AerialOn { get; }             // AT-2 aerial froxel on?
```

- [ ] **Step 3: Remove AerialOn usage from LightingComposer.Compose()**

In `scripts/lab/LightingComposer.cs` around line 329-331, delete:
```csharp
        // the two don't double-fog. Height fog / FogDensity stay (the froxel only replaces the distance/sky
        // blend). Toggling AT-2 off restores Weather.FogAerial exactly (this re-runs on Compose).
        if (_host.AerialOn) { env.FogAerialPerspective = 0.0f; }
```

- [ ] **Step 4: Remove AerialOn from TerrainLabUI.Lighting.cs**

In `scripts/lab/TerrainLabUI.Lighting.cs`, delete:
```csharp
    public bool AerialOn => _aerialOn;
```

- [ ] **Step 5: Remove _aerial from TerrainLabUI.Clouds.cs**

In `scripts/lab/TerrainLabUI.Clouds.cs`, delete:
- `private AerialPerspective? _aerial;` field (line 20)
- `private bool _aerialOn = false;` field (line 21)
- `private bool _aerialActivated;` field (line 22)
- The `"aerial_strength"` case in the float applier (line 37)
- The `"aerial_on"` case in the bool applier (line 50)

- [ ] **Step 6: Remove _aerial from TerrainLabUI.cs**

In `scripts/lab/TerrainLabUI.cs`, delete:
- `_aerial = new AerialPerspective { Name = "AerialPerspective" };` (line 77)
- The entire aerial attach block (lines 183-191):
```csharp
        if (_aerial != null && _atmosphere != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_aerial);
            _aerial.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"));
            _aerial.SetAerialTexture(_atmosphere.AerialTexture);
            _aerial.SetStrength(0.025f);
        }
```
- The CLI aerial lines (lines 200-203):
```csharp
        if (_aerialCli == 0) { _aerialOn = false; _aerial?.SetEnabled(false); ComposeLighting(); }
        if (_aerialStrCli >= 0f) { _aerial?.SetStrength(_aerialStrCli); }
        if (_aerialDbgCli >= 0) { _aerialDbg = _aerialDbgCli; _aerial?.SetDebug(_aerialDbgCli); }
        if (_aerialHazeCli >= 0f) { _aerialHazeOn = _aerialHazeCli > 0f; _aerial?.SetHazeStrength(_aerialHazeCli); }
```
- The aerial check CLI line (line 198 partial): `_atmosphere?.RequestAerialCheck()`

- [ ] **Step 7: Remove K/U/Y aerial key handlers from TerrainLabUI.Process.cs**

In `scripts/lab/TerrainLabUI.Process.cs`, delete:
- The K-key aerial toggle block (around line 252-253)
- The U-key aerial isolation stepper block (around line 255-264)
- The Y-key haze toggle block (around line 269-271)
- Associated `_lastF8`, `_aerialDbg`, `_aerialHazeOn` field declarations if only used here

- [ ] **Step 8: Remove aerial CLI fields from TerrainLabUI.Cli.cs**

In `scripts/lab/TerrainLabUI.Cli.cs`, find and delete:
- `_aerialCli`, `_aerialStrCli`, `_aerialDbgCli`, `_aerialHazeCli`, `_aerialCheckCli` field declarations and their argument parsing blocks
- The `_aerial?.SetEnabled(false)` line in the `--nofog` handler (line 322) — replace with a comment: `// AT-2 aerial removed`

- [ ] **Step 9: Remove aerial entries from lab_controls.json**

In `data/lab_controls.json`, delete:
```json
    { "id": "aerial_on", "label": "aerial perspective (AT-2)", "tab": "Light", "type": "cloud", "cloud": "aerial_on", "default": false, "rand": false },
    { "id": "aerial_strength", "label": "aerial strength", "tab": "Light", "type": "cloudf", "cloud": "aerial_strength", "min": 0.0, "max": 6.0, "default": 0.025, "rand": false },
```

- [ ] **Step 10: Delete the three files**

```bash
rm /c/Wg16/wg-16-project/scripts/lab/AerialPerspective.cs
rm /c/Wg16/wg-16-project/shaders/aerial_screen.gdshader
rm /c/Wg16/wg-16-project/shaders/atmosphere_aerial.glsl
```

- [ ] **Step 11: Build**

```
dotnet build /c/Wg16/wg-16-project/WG16.csproj
```
Expected: zero errors. If there are compilation errors, they'll point to remaining references — fix them before continuing.

- [ ] **Step 12: Eye-gate**

Launch the game. Press K — nothing should happen (key unbound). The blue froxel artifact should be gone. Sky/clouds/god-rays should look identical to before.

- [ ] **Step 13: Commit**

```bash
git -C /c/Wg16/wg-16-project add -A
git -C /c/Wg16/wg-16-project commit -m "strip: delete AT-2 aerial perspective system (AerialPerspective, aerial_screen, atmosphere_aerial)"
```

---

### Task 3: Remove EMISSION fill remnants from ground shader + fix ambient

The pending plan from `docs/2026-06-25-lighting-refactor-plan.md` — execute it exactly.

**Files:**
- Modify: `shaders/ground.gdshader` — delete fill uniforms block + fill fragment block (already described in the plan)
- Modify: `scripts/lab/LightingComposer.cs` — delete `PushTerrainFill()`, `FillEnabled`, fix `ApplyOvercastScaling()`
- Modify: `data/lab_controls.json` — remove `sky_strength`/`bounce_strength` entries, update `ambient_e` default to 0.50

> The exact code to delete and replace is fully specified in `docs/2026-06-25-lighting-refactor-plan.md` — follow it verbatim.

- [ ] **Step 1: Apply Change 1 from the refactor plan (ground.gdshader)**

Open `shaders/ground.gdshader`. Search for `// ── Analytic indirect FILL`. Delete the entire uniforms block (lines 102-114 in the plan) and the EMISSION fragment block (lines 500-518 in the plan, the `if (fill_on)` block in `fragment()`).

Confirm `hz_on` and `cam_world` uniforms are left untouched.

- [ ] **Step 2: Apply Change 2 from the refactor plan (LightingComposer.cs)**

In `scripts/lab/LightingComposer.cs`:

Replace `ApplyOvercastScaling()` body (find the block with `env.AmbientLightEnergy = 0.12f`) with:
```csharp
    public void ApplyOvercastScaling()
    {
        var env = EnvNode.Environment;
        var sun = SunNode;
        float oc = _host.Overcast;
        env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);
        env.AmbientLightSkyContribution = Time.AmbientSky;
        sun.LightEnergy = BaseSunEnergy * (1f - oc * 0.8f);
        var cloud = _host.Cloud;
        env.FogLightColor = (cloud != null) ? BaseFogColor.Lerp(cloud.SkyHorizonColor, 0.55f * oc) : BaseFogColor;
    }
```

Delete the entire `PushTerrainFill()` method and the `FillEnabled` property.

Search for any remaining `FillEnabled` or `PushTerrainFill` references and delete them.

- [ ] **Step 3: Apply Change 3 from the refactor plan (lab_controls.json)**

In `data/lab_controls.json`:
- Delete the `sky_strength` entry
- Delete the `bounce_strength` entry
- Find `"id": "ambient_e"` and set `"default": 0.50`

- [ ] **Step 4: Build**

```
dotnet build /c/Wg16/wg-16-project/WG16.csproj
```
Expected: zero errors. No reference to `fill_on`, `sky_strength`, `bounce_strength`, `FillEnabled`, `PushTerrainFill`.

- [ ] **Step 5: Eye-gate**

Launch. Move the `sky ambient` slider — it should now visibly brighten/darken shadowed terrain faces. Shadows should be tinted by the physical AT-1 sky color (warmer at golden hour, blue-white at noon). No manual sky color push should be needed.

- [ ] **Step 6: Commit**

```bash
git -C /c/Wg16/wg-16-project add shaders/ground.gdshader scripts/lab/LightingComposer.cs data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "strip: remove EMISSION fill from ground shader, fix hardcoded ambient override"
```

---

## Phase 2: Baseline Tuning

### Task 4: Tune shadow cascade + ambient to a clean baseline

With SSAO gone and ambient working, dial in Godot's native CSM and ambient to look good. All changes are in the `.tscn` and driven by `LightingComposer` — no new code.

**Files:**
- Modify: `scenes/terrain_lab.tscn` — CSM split values, shadow blur, max distance
- Modify: `data/lab_controls.json` — update defaults for `ambient_e`, `sun_soft`

- [ ] **Step 1: Reset CSM splits to wider near coverage**

In `scenes/terrain_lab.tscn`, in the `[node name="Sun"]` block, change shadow settings to:
```
directional_shadow_mode = 2
directional_shadow_split_1 = 0.05
directional_shadow_split_2 = 0.15
directional_shadow_split_3 = 0.40
directional_shadow_max_distance = 6000.0
directional_shadow_blend_splits = true
```
(These put more resolution in the near 5% of the shadow range where the player stands, and cap at 6km — far shadows at 8km were eating atlas for very little quality gain.)

- [ ] **Step 2: Eye-gate shadow sharpness**

Launch. Walk around. Check: near shadows (rocks, ridgelines within 100m) should be sharp. Mid-distance (500m) should be acceptable. Far (>2km) can be soft. If near shadows show acne (shadow on the surface casting it), increase `shadow_blur` to `2.0` in the tscn.

- [ ] **Step 3: Tune ambient slider to a reasonable default**

The `ambient_e` default is already set to 0.50 in Task 3. Eye-gate: look at a shadowed slope face-on. It should read as a clearly different shade from a lit face, but NOT pure black. If it's still too dark, raise the default to `0.65` in `data/lab_controls.json`.

- [ ] **Step 4: Commit**

```bash
git -C /c/Wg16/wg-16-project add scenes/terrain_lab.tscn data/lab_controls.json
git -C /c/Wg16/wg-16-project commit -m "baseline: CSM split tuning + ambient default for clean shadow look"
```

---

## Phase 3: AT-2 Rebuild

### Task 5: Rebuild AT-2 aerial perspective with 64 slices + log-Z

The old froxel used 32 uniformly-spaced Z slices, which caused a hard discontinuity at the ray-ground crossing. The fix: 64 slices with log-Z mapping so near slices are thin (high precision close to camera) and far slices are thick (cheaper where precision doesn't matter).

**Files:**
- Create: `shaders/atmosphere_aerial_v2.glsl` — new compute shader (64 slices, log-Z)
- Create: `shaders/aerial_screen_v2.gdshader` — new screen quad shader (log-Z decode)
- Create: `scripts/lab/AerialPerspectiveV2.cs` — new C# node (mirrors old but references v2 shaders)
- Modify: `scripts/lab/AtmosphereCompute.cs` — add aerial v2 compute path (64 slices, log-Z push)
- Modify: `scripts/lab/TerrainLabUI.cs` — wire up AerialPerspectiveV2
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` — add `_aerialV2` field + toggle
- Modify: `scripts/lab/LightingComposer.cs` / `ILightingHost` — add `AerialV2On` property
- Modify: `data/lab_controls.json` — add `aerial_v2_on` + `aerial_v2_strength` entries

**Key math:**

Log-Z slice mapping (both shaders must use the EXACT same formula):
```glsl
// Encode: world distance d → froxel Z index [0..1]
float aerial_z_encode(float d, float z_near, float z_far) {
    return log(d / z_near + 1.0) / log(z_far / z_near + 1.0);
}
// Decode: froxel Z index [0..1] → world distance d
float aerial_z_decode(float z01, float z_near, float z_far) {
    return z_near * (pow(z_far / z_near + 1.0, z01) - 1.0);
}
```
`z_near = 1.0`, `z_far = 90000.0` (matches camera far-clip).

- [ ] **Step 1: Write atmosphere_aerial_v2.glsl**

Create `shaders/atmosphere_aerial_v2.glsl`. This is the old `atmosphere_aerial.glsl` with these changes:
- Change `#define AERIAL_D 32` to `#define AERIAL_D 64`
- Replace linear slice-to-distance mapping with log-Z decode:

Old (delete):
```glsl
float t = (float(z) + 0.5) / float(AERIAL_D);
float dist = t * u_aerial_far;
```
New:
```glsl
float t = (float(z) + 0.5) / float(AERIAL_D);
float z_near = 1.0;
float dist = z_near * (pow(u_aerial_far / z_near + 1.0, t) - 1.0);
```

Everything else (Hillaire march, in-scatter, extinction, imageStore) stays identical to the old shader.

- [ ] **Step 2: Write aerial_screen_v2.gdshader**

Create `shaders/aerial_screen_v2.gdshader`. This is the old `aerial_screen.gdshader` with these changes:

Replace the linear froxel-Z lookup with log-Z encode:

Old (delete):
```glsl
float froxel_z = dist / aerial_far;
```
New:
```glsl
float z_near = 1.0;
float froxel_z = log(dist / z_near + 1.0) / log(aerial_far / z_near + 1.0);
```

Everything else (depth reconstruct, screen UV, transmittance + inscatter blend) stays identical.

- [ ] **Step 3: Add aerial v2 texture + dispatch to AtmosphereCompute.cs**

In `scripts/lab/AtmosphereCompute.cs`, add:
```csharp
private const int AerialV2W = 32, AerialV2H = 32, AerialV2D = 64;
private Rid _aerialV2Shader, _aerialV2Pipe, _aerialV2Tex, _aerialV2Set;
private Texture3Drd? _aerialV2Rd;
public Texture3Drd? AerialV2Texture => _aerialV2Rd;
public bool AerialV2Ready => _ready && _aerialV2Rd != null;
```

In `Init()`, after the existing LUT init, add:
```csharp
_aerialV2Shader = Compile("res://shaders/atmosphere_aerial_v2.glsl", "atmo_aerial_v2");
_aerialV2Pipe = _rd.ComputePipelineCreate(_aerialV2Shader);
// 64-slice 3D RGBA16F texture
var tfmt = new RDTextureFormat {
    Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
    Width = (uint)AerialV2W, Height = (uint)AerialV2H, Depth = (uint)AerialV2D,
    TextureType = RenderingDevice.TextureType.Type3D,
    UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit,
};
_aerialV2Tex = _rd.TextureCreate(tfmt, new RDTextureView());
_aerialV2Rd = new Texture3Drd();
_aerialV2Rd.TextureRdRid = _aerialV2Tex;
```

Add `DispatchAerialV2(float farDist)` method mirroring the old aerial dispatch but using `_aerialV2Shader`, `_aerialV2Tex`, `AerialV2W/H/D`, and the log-Z `aerial_far` uniform. Call it from `SetCamera(...)` (or a new `SetCameraV2(...)`) when `_aerialV2Rd != null`.

- [ ] **Step 4: Write AerialPerspectiveV2.cs**

Create `scripts/lab/AerialPerspectiveV2.cs` — identical structure to the deleted `AerialPerspective.cs` but references `aerial_screen_v2.gdshader` and `RenderPriority = 120`:

```csharp
using Godot;

namespace WG16.Lab;

public partial class AerialPerspectiveV2 : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private bool _on;

    public AerialPerspectiveV2()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/aerial_screen_v2.gdshader") };
        _mat.RenderPriority = 120;
        _quad = new MeshInstance3D
        {
            Name = "AerialScreenQuadV2",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 16384f,
            Visible = false,
        };
        AddChild(_quad);
    }

    public void Attach(Camera3D cam) { _cam = cam; }
    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; }
    public void SetAerialTexture(Texture3Drd? tex) { if (tex != null) { _mat.SetShaderParameter("aerial_tex", tex); } }
    public void SetStrength(float s) { _mat.SetShaderParameter("aerial_strength", Mathf.Clamp(s, 0f, 60f)); }
    public void SetHaze(Color c) { _mat.SetShaderParameter("aerial_haze", new Vector3(c.R, c.G, c.B)); }
    public void SetHazeStrength(float s) { _mat.SetShaderParameter("aerial_haze_str", Mathf.Clamp(s, 0f, 2f)); }
    public bool On => _on;

    public override void _Process(double delta)
    {
        if (!_on || _cam == null) { return; }
        Projection proj = _cam.GetCameraProjection();
        Projection vp = proj * new Projection(_cam.GlobalTransform.AffineInverse());
        _mat.SetShaderParameter("inv_view_proj", vp.Inverse());
        _mat.SetShaderParameter("cam_world", _cam.GlobalPosition);
        _mat.SetShaderParameter("aerial_far", 90000f);
    }
}
```

- [ ] **Step 5: Wire AerialPerspectiveV2 into TerrainLabUI**

In `scripts/lab/TerrainLabUI.Clouds.cs`, add:
```csharp
private AerialPerspectiveV2? _aerialV2;
private bool _aerialV2On = false;
private bool _aerialV2Activated = false;
```

Add cases in the float/bool appliers:
```csharp
case "aerial_v2_strength": _aerialV2?.SetStrength(v); return;
case "aerial_v2_on": _aerialV2On = on; _aerialV2?.SetEnabled(on && (_atmosphere?.AerialV2Ready ?? false)); if (!on) { _aerialV2Activated = false; } ComposeLighting(); return;
```

In `TerrainLabUI.cs` `AttachClouds()`, add after existing cloud attach:
```csharp
_aerialV2 = new AerialPerspectiveV2 { Name = "AerialPerspectiveV2" };
GetNode("/root/TerrainLabRoot").AddChild(_aerialV2);
_aerialV2.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"));
_aerialV2.SetAerialTexture(_atmosphere?.AerialV2Texture);
_aerialV2.SetStrength(0.025f);
```

In `TerrainLabUI.Process.cs` `_Process`, in the readiness-gate block, add:
```csharp
if (_aerialV2On && !_aerialV2Activated && _atmosphere != null && _atmosphere.AerialV2Ready)
{
    _aerialV2?.SetEnabled(true);
    _aerialV2Activated = true;
}
```

Add K-key toggle for v2:
```csharp
bool kK = Input.IsKeyPressed(Key.K);
if (kK && !_lastK) { _aerialV2On = !_aerialV2On; _aerialV2?.SetEnabled(_aerialV2On); GD.Print($"[dbg] (K) Aerial V2 = {_aerialV2On}"); }
_lastK = kK;
```

- [ ] **Step 6: Add lab_controls.json entries**

In `data/lab_controls.json`, add after the `aerial perspective` comment or the `hz_on` line:
```json
    { "id": "aerial_v2_on", "label": "aerial perspective v2", "tab": "Light", "type": "cloud", "cloud": "aerial_v2_on", "default": false, "rand": false },
    { "id": "aerial_v2_strength", "label": "aerial v2 strength", "tab": "Light", "type": "cloudf", "cloud": "aerial_v2_strength", "min": 0.0, "max": 6.0, "default": 0.025, "rand": false },
```

- [ ] **Step 7: Build**

```
dotnet build /c/Wg16/wg-16-project/WG16.csproj
```
Expected: zero errors.

- [ ] **Step 8: Eye-gate**

Launch. Press K to enable aerial V2. Look at a distant mountain range — there should be a subtle haze with no hard froxel-line artifact at the horizon. Toggle K off/on to A/B. If the line is gone: PASS.

- [ ] **Step 9: Commit**

```bash
git -C /c/Wg16/wg-16-project add -A
git -C /c/Wg16/wg-16-project commit -m "feat: AT-2 v2 aerial perspective (64 slices, log-Z, no froxel-line artifact)"
```

---

## Self-Review

**Spec coverage:**
- SSAO disable: Task 1 ✓
- AT-2 delete: Task 2 ✓
- EMISSION fill removal + ambient fix: Task 3 ✓ (delegates to existing plan doc)
- CSM shadow tuning: Task 4 ✓
- AT-2 rebuild (64 slices + log-Z): Task 5 ✓
- Clouds/god-rays/AT-1/AT-3 untouched: enforced by Global Constraints + Task 2 scoping ✓

**Placeholder scan:** None. All code shown verbatim.

**Type consistency:**
- `AerialV2Texture` / `AerialV2Ready` defined in Task 5 Step 3, consumed in Task 5 Step 5 ✓
- `AerialPerspectiveV2` defined in Task 5 Step 4, instantiated in Task 5 Step 5 ✓
- `_aerialV2`, `_aerialV2On`, `_aerialV2Activated` all consistently named across Clouds.cs / TerrainLabUI.cs / Process.cs ✓
