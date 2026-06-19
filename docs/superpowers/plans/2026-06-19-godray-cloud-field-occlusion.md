# God-Ray Cloud Occlusion from the Shadow Map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the god-ray shader's mood-dependent LUMINANCE cloud-occlusion heuristic with a sample of the existing `cloud_shadow_tex` (real cloud-density sun transmittance), so god rays work across all moods/weather/cloud setups without per-scene tuning.

**Architecture:** The screen-space god-ray pass (`shaders/godray_screen.gdshader` + `scripts/lab/GodRaysScreen.cs`) currently builds its occlusion buffer from screen luminance near the sun. The cloud system already bakes `cloud_shadow_tex` — a 512×512 top-down map, R = sun transmittance at world column (x,z) (1=open, 0=cloud-occluded), mood-independent, the same map the terrain samples. This plan binds that map (Texture2Drd) to the god-ray material, reconstructs each pixel's world-XZ from depth (terrain) or by projecting the view ray to the cloud altitude (sky), and samples transmittance there as the occluder signal. Luminance stays as a fallback `occ_mode` for the synthetic test scene.

**Tech Stack:** Godot 4.6.2 (Forward+/Vulkan), C#, GDShader (`shader_type spatial`), the existing CloudVolume shadow bake + Texture2Drd binding, the lab control registry.

## Global Constraints

- Pillars (memory `wg16-pillars`): quality = AAA = long-term-best, regardless of time cost.
- Judge visuals IN MOTION, never from a still (memory `wg16-mipmap-fuzz-gotcha`); the headline acceptance is "no per-mood re-tuning," verified across ≥3 moods.
- Launch WINDOWED, ABSOLUTE path, `--` before user args (memory `wg16-launch-absolute-path`): `Godot_v4.6.2...exe --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- <userargs>`. Without `--`, `OS.GetCmdlineUserArgs()` is empty and flags silently drop.
- Compute bakes need a real RenderingDevice — run WINDOWED, not `--headless` (memory `headless-no-local-rendering-device`).
- `--shadowcheck` must still PASS (this must NOT touch the cloud density field). `dotnet build WG16.csproj -c Debug` clean.
- A control "param" naming a missing uniform silently no-ops (memory `lab-registry-param-gotcha`).
- GDShader forbids early `return` in `fragment()` — compute one out-value, assign once (this bit us before).
- The Texture2Drd RID is only valid after `CloudVolume.ComputeReady` — do NOT sample before it (terrain gates on this; mirror the gate).
- Reverse-Z (Godot 4.3+): far plane / sky ≈ depth **0.0**, near = 1.0.
- Verification helpers already in the lab: `--preset=N` (5=Showcase,6=Subtle,7=Dramatic), `--godrays=1`, `--lookatsun` (aim camera at sun), `--godraydbg=1` (occlusion-mask view) / `=2` (sun marker), `--mood=N` (0 Golden Hour,1 Soft Overcast,2 Harsh Midday,3 Blue Dawn,4 Dramatic Storm,5 Clear Alpine).
- Heads-up: another chat may be working in this tree. Stage only god-ray files explicitly (NO `git add -A`).

## Reference — current relevant code (READ before editing)

`shaders/godray_screen.gdshader` `occlusion()` (the function to change):
```glsl
// 1.0 = light reaches here (open sky), 0.0 = occluder (terrain, or cloud near the sun).
float occlusion(vec2 uv) {
	float d = texture(depth_tex, uv).r;
	if (d >= 0.0005) { return 0.0; }       // solid occluder in front of the sky
	if (occ_mode != 2) { return 1.0; }     // depth-only mode (test scene): open sky = light
	// HYBRID cloud term: sky pixel darker than open sky + within cloud_radius of the sun = cloud.
	float distToSun = distance(uv, sun_screen_uv);
	if (distToSun > cloud_radius) { return 1.0; }
	vec3 c = texture(screen_tex, uv).rgb;
	float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
	float lit = smoothstep(cloud_lum - 0.12, cloud_lum, lum);
	return lit;
}
```
The shader already has `uniform sampler2D depth_tex : hint_depth_texture;`, `uniform int occ_mode`,
`uniform float cloud_radius`, `uniform float cloud_lum`, `uniform vec2 sun_screen_uv`.

`CloudVolume` (consume, do not modify): `ShadowTexture : Texture2Drd?` (CloudVolume.cs:~532),
`RegionSize : float` (~533), `ComputeReady : bool` (~474). World-XZ→UV: `uv = world.xz / region + 0.5`.

`GodRaysScreen.Attach(Camera3D cam, DirectionalLight3D sun)` and the `_Process` that already computes
`sun_screen_uv`/`sun_gate`. The lab wires it in `TerrainLabUI.cs` AttachClouds (where `_cloud.ShadowTexture`
is already bound to the terrain — copy that pattern).

---

## File Structure

- Modify: `shaders/godray_screen.gdshader` — add cloud-shadow uniforms + matrix uniforms; new `occ_mode==3` path that samples `cloud_shadow_tex` via reconstructed world-XZ.
- Modify: `scripts/lab/GodRaysScreen.cs` — bind shadow tex + region + the view/projection-inverse matrices per frame; `SetShadowTexture`; gate on `ComputeReady`; default `occ_mode=3` for the real scene.
- Modify: `scripts/lab/TerrainLabUI.cs` — pass `_cloud.ShadowTexture`/`RegionSize` to `_godraysScreen` in AttachClouds; flip its occlusion on once `ComputeReady` (mirror the terrain gate in `_Process`).
- (No new files. No cloud-system changes. Test scene `GodRayTest.cs` keeps `occ_mode=0`, untouched.)

---

## Task 1: Shader — sample the cloud shadow map via reconstructed world-XZ

**Files:**
- Modify: `shaders/godray_screen.gdshader`

**Interfaces:**
- Produces: shader uniforms `sampler2D cloud_shadow_tex`, `float cloud_shadow_region`, `bool cloud_shadow_on`, `mat4 inv_view_proj`, `vec3 cam_world`, `float cloud_altitude`; an `occ_mode==3` branch in `occlusion()`.

- [ ] **Step 1: Add the uniforms**

In `shaders/godray_screen.gdshader`, after the existing `depth_tex` uniform, add:
```glsl
// Cloud occlusion from the REAL cloud field (mood-independent): sample the cloud shadow map
// (top-down sun transmittance over world XZ) at this pixel's world position. occ_mode==3.
uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float cloud_shadow_region = 8192.0;   // CloudVolume.RegionSize
uniform bool  cloud_shadow_on = false;         // false until CloudVolume.ComputeReady (RID live)
uniform mat4  inv_view_proj;                    // inverse(projection*view), for depth→world reconstruction
uniform vec3  cam_world = vec3(0.0);            // camera world pos (for the sky view-ray origin)
uniform float cloud_altitude = 1850.0;          // cloud-deck height to project sky view-rays to (world Y)
```

- [ ] **Step 2: Add a world-position reconstruction helper + the occ_mode==3 branch**

In `shaders/godray_screen.gdshader`, replace the entire `occlusion()` function with:
```glsl
// Reconstruct world position from a screen UV + its depth (Reverse-Z: far/sky ≈ 0.0).
vec3 world_from_depth(vec2 uv, float depth) {
	vec4 ndc = vec4(uv * 2.0 - 1.0, depth, 1.0);
	vec4 wp = inv_view_proj * ndc;
	return wp.xyz / wp.w;
}

// 1.0 = light reaches here (open sky), 0.0 = occluder (terrain, or cloud).
float occlusion(vec2 uv) {
	float d = texture(depth_tex, uv).r;
	bool isSky = d < 0.0005;            // Reverse-Z: sky/far ≈ 0.0
	if (!isSky) { return 0.0; }         // solid geometry in front of the sky = hard occluder
	if (occ_mode == 0) { return 1.0; }  // depth-only (test scene): open sky = light

	if (occ_mode == 3 && cloud_shadow_on) {
		// CLOUD FIELD occlusion: find this sky pixel's world-XZ on the cloud deck (where its view ray
		// pierces cloud_altitude), sample the shadow map's sun transmittance there. 1=open gap, 0=cloud.
		vec3 dir = normalize(world_from_depth(uv, 0.5) - cam_world);   // view dir for this pixel
		float t = (dir.y > 1e-3) ? (cloud_altitude - cam_world.y) / dir.y : -1.0;
		if (t <= 0.0) { return 1.0; }   // ray doesn't reach the deck (looking down/parallel) → open
		vec2 hitXz = (cam_world + dir * t).xz;
		vec2 suv = hitXz / cloud_shadow_region + 0.5;
		if (any(lessThan(suv, vec2(0.0))) || any(greaterThan(suv, vec2(1.0)))) { return 1.0; } // off-map → open
		return texture(cloud_shadow_tex, suv).r;   // transmittance: 1 open, 0 cloud-occluded
	}

	// occ_mode == 1/2 fallback: luminance heuristic near the sun (legacy / test scene).
	float distToSun = distance(uv, sun_screen_uv);
	if (distToSun > cloud_radius) { return 1.0; }
	vec3 c = texture(screen_tex, uv).rgb;
	float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
	return smoothstep(cloud_lum - 0.12, cloud_lum, lum);
}
```

- [ ] **Step 3: Verify no early-return crept in + it compiles**

Confirm `occlusion()` has no `return` inside `fragment()` itself (the helper + occlusion fn are fine; `fragment()` must still assign one out-color — unchanged from current). Then build:
Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded"`
Expected: `Build succeeded.` (C# unaffected; the shader compiles at scene load in Task 3's run.)

---

## Task 2: GodRaysScreen — bind the shadow map + matrices + cloud altitude per frame

**Files:**
- Modify: `scripts/lab/GodRaysScreen.cs`

**Interfaces:**
- Consumes: the Task 1 uniforms.
- Produces: `GodRaysScreen.SetShadowTexture(Texture2D? tex, float region)`, `SetCloudOcclusionReady(bool)`, `SetCloudAltitude(float)`; `_Process` feeds `inv_view_proj`, `cam_world`. Default `occ_mode=3`.

- [ ] **Step 1: Default the real scene to occ_mode 3 (cloud field)**

In `scripts/lab/GodRaysScreen.cs` constructor, change the occ_mode line from `2` to `3`:
```csharp
        _mat.SetShaderParameter("occ_mode", 3);   // cloud-field occlusion (shadow map); mood-independent
```

- [ ] **Step 2: Add the shadow-texture + altitude + readiness setters**

In `scripts/lab/GodRaysScreen.cs`, add near the other Set* methods:
```csharp
    /// Bind the cloud shadow map (Texture2Drd) + its world footprint (CloudVolume.ShadowTexture/RegionSize).
    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _mat.SetShaderParameter("cloud_shadow_tex", tex); }
        _mat.SetShaderParameter("cloud_shadow_region", region);
    }
    /// Enable cloud-field sampling only once the shadow Texture2Drd RID is live (CloudVolume.ComputeReady),
    /// else the material samples an empty RID on frame 1 (errors). Mirrors the terrain's cloud_shadow_on gate.
    public void SetCloudOcclusionReady(bool ready) => _mat.SetShaderParameter("cloud_shadow_on", ready);
    /// Cloud-deck altitude (world Y) that sky view-rays project to for the shadow-map lookup.
    public void SetCloudAltitude(float y) => _mat.SetShaderParameter("cloud_altitude", y);
```

- [ ] **Step 3: Feed inv_view_proj + cam_world each frame**

In `scripts/lab/GodRaysScreen.cs` `_Process`, after the existing `sun_screen_uv`/`sun_gate` sets, add:
```csharp
        // depth→world reconstruction needs inverse(projection*view); cam_world is the sky view-ray origin.
        Projection proj = _cam.GetCameraProjection();
        Transform3D camXf = _cam.GlobalTransform;
        Projection viewProj = proj * new Projection(camXf.AffineInverse());
        _mat.SetShaderParameter("inv_view_proj", viewProj.Inverse());
        _mat.SetShaderParameter("cam_world", _cam.GlobalPosition);
```

- [ ] **Step 4: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded"`
Expected: `Build succeeded.` 0 errors. (If `GetCameraProjection`/`Projection(Transform3D)` names differ in 4.6, correct against the API — the intent is `inv_view_proj = inverse(projection * view)`.)

---

## Task 3: Wire CloudVolume's shadow map into GodRaysScreen + gate on ComputeReady

**Files:**
- Modify: `scripts/lab/TerrainLabUI.cs` (AttachClouds: bind tex/region/altitude; `_Process`: flip ready on ComputeReady)

**Interfaces:**
- Consumes: `GodRaysScreen.SetShadowTexture/SetCloudOcclusionReady/SetCloudAltitude`, `CloudVolume.ShadowTexture/RegionSize/ComputeReady/Params.AltitudeM`.

- [ ] **Step 1: Bind the shadow map + altitude when god rays attach**

In `scripts/lab/TerrainLabUI.cs` AttachClouds, in the `if (_godraysScreen != null)` block (right after `_godraysScreen.Attach(...)`), add:
```csharp
            _godraysScreen.SetShadowTexture(_cloud.ShadowTexture, _cloud.RegionSize);
            _godraysScreen.SetCloudAltitude(_terrain.MidHeight + _cloud.Params.AltitudeM);
```

- [ ] **Step 2: Flip cloud occlusion on once the RID is live**

In `scripts/lab/TerrainLabUI.cs` `_Process`, find the existing terrain gate (`if (!_shadowEnabledOnce && _cloud != null && _cloud.ComputeReady && _cloud.Enabled)`) and add the god-ray enable inside it:
```csharp
            _godraysScreen?.SetCloudOcclusionReady(true);
```
(So both the terrain and the god-ray cloud sampling turn on the same frame the shadow RID goes live.)

- [ ] **Step 3: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded"`
Expected: `Build succeeded.` 0 errors.

- [ ] **Step 4: Verify the cloud-field occlusion mask is correct (capture)**

Run (windowed; auto-shot quits after ~1.5s):
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --preset=5 --godrays=1 --lookatsun --godraydbg=1 --auto-shot=C:/tmp/godray_verify/cf_mask.png
```
Read `C:/tmp/godray_verify/cf_mask.png`: the occlusion mask should show **cloud shapes** (dark where cloud blocks the sun, white open sky) that MATCH the visible clouds — and it should be driven by cloud density, not sky brightness. Terrain area = black (hard occluder). If the mask is all-white, `cloud_shadow_on` never flipped (check the ComputeReady gate) or the world reconstruction is wrong.

- [ ] **Step 5: Commit**

```bash
cd /c/Wg16/wg-16-project
git add shaders/godray_screen.gdshader scripts/lab/GodRaysScreen.cs scripts/lab/TerrainLabUI.cs
git commit -m "God rays: cloud occlusion from the shadow map (mood-independent), not luminance

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: The acceptance test — no per-mood re-tuning

**Files:** none (verification + commit)

- [ ] **Step 1: Capture the SAME preset under 3 very different moods, no slider changes**

Run each (windowed):
```bash
GODOT="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
"$GODOT" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --preset=5 --mood=0 --godrays=1 --lookatsun --auto-shot=C:/tmp/godray_verify/cf_golden.png
"$GODOT" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --preset=5 --mood=3 --godrays=1 --lookatsun --auto-shot=C:/tmp/godray_verify/cf_dawn.png
"$GODOT" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --preset=5 --mood=4 --godrays=1 --lookatsun --auto-shot=C:/tmp/godray_verify/cf_storm.png
```
Read all three. ACCEPTANCE: shafts read correctly (form through cloud gaps, no phantom shafts in clear sky) in ALL THREE without changing any god-ray slider. This is the whole point of the feature — the old luminance heuristic would need `cloud threshold` re-tuned per mood; the cloud-field version should not.
(NOTE `--preset` sets sun_angle; `--mood` then overrides sky/exposure but the preset's sun was applied after the mood per the AttachClouds order fix — confirm the sun is still low. If a mood stomps it, the lab harness order needs the preset re-applied; out of scope unless it breaks.)

- [ ] **Step 2: shadowcheck regression gate**

Run:
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --shadowcheck --clouds=1 2>&1 | grep -iE "PASS|FAIL"
```
Expected: `PASS` (this feature only READS the shadow map; it must not change the cloud density field).

- [ ] **Step 3: USER EYE-GATE (in motion)**

Launch live: `... -- --preset=5 --godrays=1 --lookatsun`. User flies across moods/sun angles/cloud covers and confirms: god rays look right WITHOUT touching the sliders. On approval, the feature is done.

- [ ] **Step 4: Update docs + memory**

Update `docs/HANDOFF.md` + `docs/ROADMAP.md` god-ray entries: occlusion now from the cloud shadow map (mood-independent), luminance kept as test-scene fallback. Update memory `godray-emission-vs-albedo-rootcause`: the hybrid occlusion's cloud term is now shadow-map-based, not luminance. Commit:
```bash
cd /c/Wg16/wg-16-project && git add docs/ && git commit -m "Docs: god-ray cloud occlusion now shadow-map-driven (mood-independent)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:** (1) replace luminance with shadow-map sample → Task 1 (occ_mode 3) + Task 2 (bind) + Task 3 (wire). (2) keep luminance fallback → occ_mode 1/2 retained in `occlusion()`; test scene stays occ_mode 0. (3) gate on ComputeReady → Task 3 Step 2. (4) world-XZ from depth (terrain) / view-ray-to-cloud-plane (sky) → Task 1 Step 2 (`world_from_depth` + the deck projection). (5) mood-independence acceptance → Task 4 Step 1 (3 moods, no re-tune). (6) shadowcheck PASS → Task 4 Step 2. (7) no new passes / no CompositorEffect → reuses existing shadow map. Covered.

**Placeholder scan:** No TBD/TODO. All shader + C# shown in full. One flagged uncertainty (exact 4.6 `GetCameraProjection`/`Projection(Transform3D)` API name) has an explicit "correct against the API; intent is inverse(projection*view)" instruction, not a silent gap. The sun-stomp note in Task 4 Step 1 is a known-issue heads-up, not a placeholder.

**Type consistency:** `GodRaysScreen.SetShadowTexture(Texture2D?, float)` matches the terrain's binding type (Texture2Drd is-a Texture2D). `SetCloudOcclusionReady(bool)` / `SetCloudAltitude(float)` consistent across Task 2 (def) and Task 3 (call). Uniform names (`cloud_shadow_tex`, `cloud_shadow_region`, `cloud_shadow_on`, `inv_view_proj`, `cam_world`, `cloud_altitude`) identical between Task 1 (shader) and Task 2/3 (SetShaderParameter). `occ_mode==3` consistent between shader branch (Task 1) and the constructor default (Task 2 Step 1).

**Risk coverage:** depth→world Reverse-Z (far=0.0) — `world_from_depth` uses raw NDC depth, correct for the inverse matrix; verified via debug mask (Task 3 Step 4). ComputeReady gate — Task 3 Step 2 mirrors terrain. Off-map sky rays at low sun — `occ_mode==3` branch returns 1.0 (open) off-map. Ground-XZ-vs-altitude approximation — documented in spec; the deck-projection sample is the chosen approach, sun-unshift fallback noted if visibly off.
