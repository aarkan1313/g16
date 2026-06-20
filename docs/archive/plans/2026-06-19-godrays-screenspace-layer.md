# Screen-Space God Rays (Crisp Beam Layer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the crisp, visible "fan of beams" god-ray layer using the GPU Gems 3 Ch.13 screen-space radial light-scattering post-process — the AAA technique that actually reads as god rays (the froxel-fog layer already built is the SOFT base; this is the DRAMA layer on top).

**Architecture:** A full-screen post-process radial-blurs an *occlusion buffer* (bright sky/sun = light, dark clouds/terrain = occluders) outward from the sun's projected screen position and adds it back to the frame. Because WG16's clouds + sun disc are drawn by the sky shader, the rendered frame the post-process reads MUST contain the sky — which `hint_screen_texture` may NOT (it's copied after the opaque pass, before sky/transparent). **Task 1 is a decisive cheap experiment to determine which capture path actually contains the sun+clouds**, before committing to either the light (spatial-quad reading `hint_screen_texture`) or heavy (SubViewport rendering the full scene) architecture. NO CompositorEffect (project avoids it — RID race, memory `compute-to-material-callonrenderthread`).

**Tech Stack:** Godot 4.6.2 (Forward+/Vulkan), C#, GDShader (`canvas_item` or `spatial`), the existing `/root/TerrainLabRoot/{Camera,Sun,Env}` scene, the lab control registry + cloud preset system.

## Global Constraints

- Pillars (memory `wg16-pillars`): quality = AAA = long-term-best, regardless of time cost; lead with the better option.
- Judge visuals IN MOTION, never from a still (memory `wg16-mipmap-fuzz-gotcha`). Each visual task ends with a user eye-gate.
- Launch WINDOWED, ABSOLUTE path, `--` before user args (memory `wg16-launch-absolute-path`): `Godot...exe --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- <userargs>`. Without `--`, `OS.GetCmdlineUserArgs()` is empty and `--mood`/`--preset`/`--godrays` silently drop (confirmed this session).
- Compute bakes (cloud shadow map) need a real RenderingDevice — run windowed, not `--headless`.
- `--shadowcheck` must still PASS after all changes (this layer must not touch the cloud density field). `dotnet build WG16.csproj` clean.
- A control "param" naming a missing uniform silently no-ops (memory `lab-registry-param-gotcha`).
- The froxel god-ray layer (`GodRaysVolumetric`, toggle `cloud_godrays`) is the SOFT base, already built; it stays. This screen-space layer is SEPARATE, behind its own toggle, composes additively on top.
- Reverse-Z (Godot 4.3+): far/sky = depth 0.0 (near = 1.0). Any depth-threshold logic from old tutorials is inverted. (We avoid depth entirely — luminance mask — so this is just an awareness note.)
- `Camera3D.UnprojectPosition` returns garbage for points BEHIND the camera — always gate with `IsPositionBehind` first (Godot 4.6 docs pair them).
- Scene node paths: camera `/root/TerrainLabRoot/Camera`, sun `/root/TerrainLabRoot/Sun`, env `/root/TerrainLabRoot/Env`, existing `/root/TerrainLabRoot/UILayer` (CanvasLayer).

---

## File Structure

- Create: `shaders/godray_screen.gdshader` — the radial-scatter post-process shader (type decided by Task 1: `canvas_item` if SubViewport path, `spatial` if `hint_screen_texture` path works).
- Create: `scripts/lab/GodRaysScreen.cs` — owns the post-process node(s) + per-frame sun-screen-UV + gate computation; public `Attach(cam, sun)`, `SetEnabled(bool)`, `SetStrength(float)`; consumes camera + sun, never touches clouds.
- Modify: `scripts/lab/TerrainLabUI.cs` — construct + attach the module.
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` — route a new toggle + strength control to it.
- Modify: `data/lab_controls.json` — add `cloud_godrays_screen` toggle + `godray_screen_strength`.
- Modify: `data/cloud_presets.json` — extend "God Ray Showcase" to also enable the screen-space layer.

---

## Task 1: DECISIVE EXPERIMENT — what's in the captured frame?

**Goal:** Determine whether `hint_screen_texture` (spatial quad, cheap) contains the sun+clouds, OR whether we need a SubViewport (heavy) that renders the full scene. This single result picks the architecture for every later task. Build the CHEAP path first and look.

**Files:**
- Create: `shaders/godray_screen.gdshader` (temporary passthrough)
- Create: `scripts/lab/GodRaysScreen.cs` (minimal: just add the quad)
- Modify: `scripts/lab/TerrainLabUI.cs` (construct + attach, enabled via a temporary CLI flag)

- [ ] **Step 1: Passthrough spatial-quad shader reading hint_screen_texture**

Create `shaders/godray_screen.gdshader`:
```glsl
shader_type spatial;
render_mode unshaded, cull_disabled, depth_test_disabled, depth_draw_never;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;

void vertex() {
	// Fullscreen quad in clip space (ignore camera transform). z=1 = far plane under Reverse-Z;
	// harmless with depth_test_disabled. (Godot issue #58337: use VERTEX.xy form, not vec4(VERTEX,1).)
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	// PASSTHROUGH for the Task-1 experiment: just show what the screen texture contains.
	// If we see sky + sun disc + clouds → hint_screen_texture works (cheap spatial path).
	// If sky/clouds are MISSING (black/garbage where sky should be) → need the SubViewport path.
	ALBEDO = texture(screen_tex, SCREEN_UV).rgb;
}
```

- [ ] **Step 2: Minimal GodRaysScreen module that mounts the quad on the camera**

Create `scripts/lab/GodRaysScreen.cs`:
```csharp
using Godot;

namespace WG16.Lab;

/// Screen-space god rays (GPU Gems 3 radial scatter) — the CRISP beam layer atop the soft froxel
/// base (GodRaysVolumetric). Task 1 is a passthrough experiment to learn what the captured frame
/// contains; the real radial blur + occlusion mask + sun-UV gating come in later tasks.
public partial class GodRaysScreen : Node3D
{
    private MeshInstance3D _quad = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private DirectionalLight3D? _sun;
    private bool _on;

    public GodRaysScreen()
    {
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_screen.gdshader") };
        _quad = new MeshInstance3D
        {
            Name = "GodRayScreenQuad",
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },   // clip-space fullscreen via the vertex shader
            MaterialOverride = _mat,
            Extras = { },          // (no-op; placeholder so the initializer reads clearly)
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        // draw last (after scene); high render priority keeps it atop other transparent draws.
        _mat.RenderPriority = 127;
        AddChild(_quad);
    }

    public void Attach(Camera3D cam, DirectionalLight3D sun) { _cam = cam; _sun = sun; _quad.Reparent(cam, false); }

    public void SetEnabled(bool on) { _on = on; _quad.Visible = on; }
    public bool On => _on;
}
```
NOTE: `Extras = { }` is invalid C# — remove it; it's shown here only to flag "no extra init needed." The real initializer is just the four set properties. (Plan self-review caught this; the implementer must omit that line.)

- [ ] **Step 3: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded"`
Expected: `Build succeeded.` 0 errors. (If `QuadMesh`/`Reparent`/`RenderPriority` API names are off, correct against the Godot 4.6 C# API.)

- [ ] **Step 4: Wire it in + a temporary CLI flag to enable**

In `scripts/lab/TerrainLabUI.cs` `_Ready` (after `_godrays = new GodRaysVolumetric...`), add:
```csharp
        _godraysScreen = new GodRaysScreen { Name = "GodRaysScreen" };
```
In `AttachClouds` (after the `_godrays` attach block), add:
```csharp
        if (_godraysScreen != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_godraysScreen);
            _godraysScreen.Attach(GetNode<Camera3D>("/root/TerrainLabRoot/Camera"),
                                  GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
            _godraysScreen.SetEnabled(true);   // TEMP: force-on for the Task-1 experiment
        }
```
Add the field to `TerrainLabUI.Clouds.cs` next to `_godrays`:
```csharp
    private GodRaysScreen? _godraysScreen;
```

- [ ] **Step 5: Launch and LOOK (the experiment)**

Run:
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --preset=5
```
USER EYE-GATE — the passthrough quad is full-screen. Report: do you see the **normal scene (sky + sun disc + clouds + terrain)**, or is the **sky/clouds missing** (black, or only terrain visible)?
- **Scene fully visible** → `hint_screen_texture` works → CHEAP spatial-quad path. Proceed with Task 2A.
- **Sky/clouds missing** → need the SubViewport path. Proceed with Task 2B.

- [ ] **Step 6: Commit the experiment scaffold**

```bash
cd /c/Wg16/wg-16-project && git add -A
git commit -m "Screen-space god rays: Task-1 capture experiment (passthrough quad)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2A: Spatial-quad path (ONLY if Task 1 showed the full scene)

**Files:** Modify `shaders/godray_screen.gdshader`, `scripts/lab/GodRaysScreen.cs`.

**Interfaces:**
- Consumes: `screen_tex` (hint_screen_texture).
- Produces: `GodRaysScreen` with the radial-scatter shader; uniforms `sun_screen_uv` (vec2), `sun_gate` (float), `density`/`decay`/`weight`/`exposure`/`mask_threshold`/`mask_softness` floats fed by the module.

- [ ] **Step 1: Replace the passthrough fragment with the radial-scatter + luminance mask**

In `shaders/godray_screen.gdshader`, keep the `vertex()` and add uniforms + replace `fragment()`:
```glsl
uniform vec2  sun_screen_uv = vec2(0.5, 0.5);
uniform float sun_gate : hint_range(0.0, 1.0) = 0.0;
uniform vec3  ray_tint : source_color = vec3(1.0, 0.95, 0.85);
uniform float density   : hint_range(0.0, 1.0) = 0.9;
uniform float decay     : hint_range(0.0, 1.0) = 0.96;
uniform float weight    : hint_range(0.0, 0.3) = 0.06;
uniform float exposure  : hint_range(0.0, 2.0) = 0.4;
uniform float mask_threshold : hint_range(0.0, 2.0) = 0.7;
uniform float mask_softness  : hint_range(0.0, 1.0) = 0.3;
const int NUM_SAMPLES = 48;

float occlusion(vec2 uv) {
	vec3 c = texture(screen_tex, uv).rgb;
	float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
	return smoothstep(mask_threshold, mask_threshold + mask_softness, lum);  // bright sky/sun → 1, dark → 0
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 delta = (uv - sun_screen_uv) * (1.0 / float(NUM_SAMPLES) * density);
	float illum = 1.0;
	float scatter = 0.0;
	vec2 coord = uv;
	for (int i = 0; i < NUM_SAMPLES; i++) {
		coord -= delta;
		scatter += occlusion(coord) * (illum * weight);
		illum *= decay;
	}
	scatter *= exposure * sun_gate;
	ALBEDO = texture(screen_tex, uv).rgb + ray_tint * scatter;   // additive composite over the scene
}
```

- [ ] **Step 2: Per-frame sun-UV + gate in the module**

In `scripts/lab/GodRaysScreen.cs`, remove the TEMP force-on, add `_Process`:
```csharp
    public override void _Process(double delta)
    {
        if (!_on || _cam == null || _sun == null) { return; }
        // DirectionalLight shines along -Z; the sun (source) is at +Basis.Z (same convention as PushSunToCloud).
        Vector3 toSun = _sun.GlobalTransform.Basis.Z.Normalized();
        Vector3 sunWorld = _cam.GlobalPosition + toSun * 100000f;
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
        float align = fwd.Dot(toSun);
        float gate = 0f;
        Vector2 uv = new Vector2(0.5f, 0.5f);
        if (!_cam.IsPositionBehind(sunWorld) && align > 0f)
        {
            Vector2 px = _cam.UnprojectPosition(sunWorld);
            Vector2 vp = _cam.GetViewport().GetVisibleRect().Size;
            uv = px / vp;
            float edge = 0.15f;
            float fx = Mathf.Clamp(Mathf.Min(uv.X, 1f - uv.X) / edge, 0f, 1f);
            float fy = Mathf.Clamp(Mathf.Min(uv.Y, 1f - uv.Y) / edge, 0f, 1f);
            gate = fx * fy * Mathf.Clamp(align, 0f, 1f);
        }
        _mat.SetShaderParameter("sun_screen_uv", uv);
        _mat.SetShaderParameter("sun_gate", gate);
    }

    public void SetStrength(float s) => _mat.SetShaderParameter("exposure", Mathf.Clamp(s * 0.1f, 0f, 2f));
```

- [ ] **Step 3: Build + verify the sun-direction sign + gate**

Build (expect `Build succeeded.`). Then launch (`--preset=5`), USER EYE-GATE: as you turn toward the sun, beams fan out from the sun's screen position; as you turn away / sun goes off-screen, they fade out cleanly (no smear when the sun is behind you). If the rays originate from the OPPOSITE side of the sun, flip the `toSun` sign (`-_sun.GlobalTransform.Basis.Z`). Tune `mask_threshold` to your sky luminance, `exposure`/`weight` for intensity. Commit when it reads.

→ Skip Task 2B. Go to Task 3.

---

## Task 2B: SubViewport path (ONLY if Task 1 showed sky/clouds MISSING)

**Files:** Modify `shaders/godray_screen.gdshader` (to `shader_type canvas_item`), `scripts/lab/GodRaysScreen.cs` (build a SubViewport rendering the same World3D + a ColorRect).

**Interfaces:**
- Consumes: the main camera's `World3D`; a synced second camera.
- Produces: same uniform surface (`sun_screen_uv`, `sun_gate`, params) as 2A, but the shader reads the SubViewport texture instead of `hint_screen_texture`.

- [ ] **Step 1: Rebuild GodRaysScreen around a SubViewport + ColorRect**

Replace `scripts/lab/GodRaysScreen.cs` body so the node owns:
- a `SubViewport` (`RenderTargetUpdateMode.Always`, size = main viewport size, `World3D` = the main camera's world via `Viewport.World3D`),
- a `Camera3D` child of the SubViewport, kept in lockstep with the main camera each `_Process` (copy `GlobalTransform`, `Fov`, `Near`, `Far`),
- a `ColorRect` (full-rect) under the existing `/root/TerrainLabRoot/UILayer` CanvasLayer (or a new CanvasLayer at layer 100) whose `ShaderMaterial` reads the SubViewport texture via a `hint_default_black` `sampler2D` set to `subViewport.GetTexture()`.
Exact code:
```csharp
using Godot;
namespace WG16.Lab;
public partial class GodRaysScreen : Node3D
{
    private SubViewport _vp = null!;
    private Camera3D _vpCam = null!;
    private ColorRect _rect = null!;
    private ShaderMaterial _mat = null!;
    private Camera3D? _cam;
    private DirectionalLight3D? _sun;
    private bool _on;

    public GodRaysScreen()
    {
        _vp = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
        _vpCam = new Camera3D { Name = "GodRayVpCam", Current = true };
        _vp.AddChild(_vpCam);
        AddChild(_vp);
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/godray_screen.gdshader") };
        _rect = new ColorRect { Name = "GodRayScreenRect", Material = _mat, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    }

    public void Attach(Camera3D cam, DirectionalLight3D sun)
    {
        _cam = cam; _sun = sun;
        _vp.Size = (Vector2I)cam.GetViewport().GetVisibleRect().Size;
        _vp.World3D = cam.GetWorld3D();                       // render the REAL world (sky+clouds+terrain)
        var layer = new CanvasLayer { Name = "GodRayScreenLayer", Layer = 100 };
        layer.AddChild(_rect);
        cam.GetTree().Root.GetNode("TerrainLabRoot").AddChild(layer);
        _mat.SetShaderParameter("scene_tex", _vp.GetTexture());
    }

    public override void _Process(double delta)
    {
        if (!_on || _cam == null || _sun == null) { return; }
        _vpCam.GlobalTransform = _cam.GlobalTransform;
        _vpCam.Fov = _cam.Fov; _vpCam.Near = _cam.Near; _vpCam.Far = _cam.Far;
        // ... identical sun-UV + gate computation as Task 2A Step 2 ...
        // (compute uv + gate, then:)
        // _mat.SetShaderParameter("sun_screen_uv", uv); _mat.SetShaderParameter("sun_gate", gate);
    }
    public void SetEnabled(bool on) { _on = on; _rect.Visible = on; _vp.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled; }
    public bool On => _on;
    public void SetStrength(float s) => _mat.SetShaderParameter("exposure", Mathf.Clamp(s * 0.1f, 0f, 2f));
}
```
(Fill the `_Process` sun-UV/gate body with the exact code from Task 2A Step 2.)

- [ ] **Step 2: canvas_item shader**

Rewrite `shaders/godray_screen.gdshader` as `shader_type canvas_item; render_mode unshaded;` with `uniform sampler2D scene_tex : filter_linear, hint_default_black;`, the same `occlusion()` + radial loop from Task 2A Step 1, and `COLOR = texture(scene_tex, SCREEN_UV) + vec4(ray_tint * scatter, 0.0);` (the ColorRect IS the final image, so output scene+scatter).

- [ ] **Step 3: Build + eye-gate**

Build (expect `Build succeeded.`). The Task-1 risk #1 is resolved by construction (SubViewport = full frame). Launch `--preset=5`, USER EYE-GATE: beams fan from the sun, occluded by clouds/terrain, fade off-screen. Verify the SubViewport renders the real world (not black/empty — if empty, the `World3D` assignment or vp-camera sync is wrong). Tune as 2A. Commit.

---

## Task 3: Unified controls + preset integration

**Files:** Modify `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Clouds.cs`, `scripts/lab/TerrainLabUI.cs` (remove the Task-1 TEMP force-on), `data/cloud_presets.json`.

**Interfaces:**
- Consumes: `GodRaysScreen.SetEnabled(bool)`, `SetStrength(float)`.
- Produces: Clouds-tab "god rays (screen)" toggle + "screen ray strength" slider; showcase preset enables both layers.

- [ ] **Step 1: Remove the Task-1 TEMP force-on**

In `scripts/lab/TerrainLabUI.cs` AttachClouds, delete the `_godraysScreen.SetEnabled(true);   // TEMP` line (the control will drive it).

- [ ] **Step 2: Add the two controls**

In `data/lab_controls.json`, after the existing `cloud_godray_strength` entry, add:
```json
    { "id": "cloud_godrays_screen", "label": "god rays (screen)", "tab": "Clouds", "type": "cloud", "cloud": "godrays_screen", "default": false, "rand": false },
    { "id": "cloud_godray_screen_strength", "label": "screen ray strength", "tab": "Clouds", "type": "cloudf", "cloud": "godray_screen_strength", "min": 0.0, "max": 12.0, "default": 4.0, "rand": false },
```

- [ ] **Step 3: Route them**

In `scripts/lab/TerrainLabUI.Clouds.cs`:
- In `ApplyCloudFloat`, after the `godray_strength` line: `if (knob == "godray_screen_strength") { _godraysScreen?.SetStrength(v); return; }`
- In `ApplyCloudBool`, after the `godrays` line: `if (knob == "godrays_screen") { _godraysScreen?.SetEnabled(on); return; }`

- [ ] **Step 4: Build + extend the showcase preset**

Build (expect `Build succeeded.`). In `data/cloud_presets.json` "God Ray Showcase" `values`, add `"cloud_godrays_screen": 1, "cloud_godray_screen_strength": 5.0`.

- [ ] **Step 5: Final eye-gate (both layers) + shadowcheck + commit**

Launch `--preset=5`. USER EYE-GATE: the showcase now shows soft volumetric base (froxel) + crisp screen-space beams together; toggling each control independently works; turning toward/away from sun behaves. Run `--shadowcheck --clouds=1` → expect PASS (this layer doesn't touch clouds). Commit:
```bash
git add -A && git commit -m "Screen-space god rays: unified controls + showcase preset (both layers)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
Update `docs/HANDOFF.md` + `docs/ROADMAP.md`: god rays = froxel soft base + screen-space crisp beams (GPU Gems 3), both behind Clouds-tab toggles, default off.

---

## Self-Review

**Spec coverage:** Goal = crisp screen-space god-ray beam layer (AAA two-layer model). Task 1 picks the correct capture architecture by experiment (avoids a 4th blind failure); 2A/2B implement whichever the experiment selects; Task 3 unifies controls + preset + eye-gate. The froxel base is untouched (it's the soft layer). Covered.

**Placeholder scan:** No TBD/TODO. One deliberate flag: Task 1 Step 2 shows an invalid `Extras = { }` line with an explicit instruction to OMIT it (a teaching note, not a placeholder — the surrounding real code is complete). Task 2B Step 1 `_Process` body says "identical to 2A Step 2" but ALSO gives the exact two SetShaderParameter calls and points to the exact source — acceptable since 2A and 2B are mutually exclusive (only one is executed) and the referenced code is in the same document. All shaders + C# shown in full.

**Type consistency:** `GodRaysScreen` throughout (distinct from `GodRaysVolumetric`). Public surface `Attach(Camera3D, DirectionalLight3D)`, `SetEnabled(bool)`, `SetStrength(float)`, `On` — consistent across Tasks 1/2A/2B/3. Control knob strings `godrays_screen` + `godray_screen_strength` match between lab_controls.json (Task 3 Step 2) and the routers (Task 3 Step 3) and the preset (Task 3 Step 4). Shader uniform names (`screen_tex` OR `scene_tex` depending on path, `sun_screen_uv`, `sun_gate`, `density`, `decay`, `weight`, `exposure`, `mask_threshold`, `mask_softness`, `ray_tint`) match between shader and `SetShaderParameter` calls within each path.

**Risk coverage:** capture-path uncertainty → Task 1 experiment FIRST. Sun-direction sign → explicit live check in 2A Step 3 / 2B Step 3. UnprojectPosition-behind-camera → `IsPositionBehind` gate. Mood-driven sky luminance vs mask_threshold → tunable + called out. Perf (48-tap blur, or double render in 2B) → NUM_SAMPLES=48 + half-res note (2B can set `_vp.Size` to half and the ColorRect upsamples). No CompositorEffect.
