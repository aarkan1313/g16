# God Rays — Strip & Rebuild (Canonical Sun-Shadow + Cloud Caster) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove all three competing god-ray implementations cleanly, then rebuild ONE coherent system: physically-correct volumetric shafts driven by the DirectionalLight's real shadow, with clouds occluding the sun via a shadow-casting proxy (the existing cloud-shadow map as an alpha-cutout caster). A crisp screen-space radial layer is scoped as a later, separately-eye-gated arc.

**Architecture:** The canonical AAA outdoor god-ray path (confirmed against Godot's `volumetric_fog_process.glsl` + Frostbite/Wronski research, see memory `godray-emission-vs-albedo-rootcause`): global volumetric fog at low density is lit per-froxel by `(sun · sun_shadow · HG_phase · volumetric_fog_energy) · ALBEDO · DENSITY`. Shafts are the *contrast* between lit and shadowed froxels — so the clouds must appear in the **sun's shadow map**. We achieve that with a single shadow-casting quad oriented to the sun, alpha-cut by the cloud-shadow transmittance map, so cloud gaps let light through and cloud bodies cast shadow onto BOTH terrain and fog. No custom fog-emission shader; no FogVolume-as-light-source; no in-march sky shafts.

**Tech Stack:** Godot 4.6.2 (Forward+/Vulkan), C# (.NET), GDShader (`shader_type spatial` for the caster), the existing CloudVolume compute (shadow-map bake), the existing lab UI control registry.

## Global Constraints

- Pillars (memory `wg16-pillars`): quality = AAA-ish = long-term-best, regardless of time cost; lead with the better option.
- Judge visuals IN MOTION, never from a still (memory `wg16-mipmap-fuzz-gotcha`). Each visual task ends with a user eye-gate, not an auto-shot.
- Launch WINDOWED with the ABSOLUTE project path (memory `wg16-launch-absolute-path`): `Godot_v4.6.2...exe --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- <userargs>`. The `--` delimiter is REQUIRED or `OS.GetCmdlineUserArgs()` returns empty and `--mood`/`--godrays`/`--preset` are silently dropped.
- Compute bakes (the cloud shadow map) need a real RenderingDevice — run windowed, NOT `--headless` (memory `headless-no-local-rendering-device`).
- std430 param-buffer hazard: the cloud raymarch param buffer is hand-packed (`Std430Writer`). NEVER remove a `.F()` field in `CloudVolume.BuildParams` without removing the matching field in `cloud_raymarch.glsl`'s `ParamsBuf` in the SAME task, or the cloud march reads garbage (memory `std430-packing-helper`).
- Health guard: `--shadowcheck` must still PASS after the strip (god rays must not touch the cloud density field the shadow shaders mirror). `dotnet build WG16.csproj` clean.
- A control "param" naming a missing shader uniform silently no-ops (memory `lab-registry-param-gotcha`); non-shader controls need field/setter/scene/id + a C# case.
- Backup before destructive work: a git tag + a zip, per the existing checkpoint convention.

---

## File Structure

**Phase 1 — Strip (delete the three competing designs):**
- Delete: `scripts/lab/GodRays.cs` (FogVolume path — pure god-ray)
- Delete: `shaders/godray_fog.gdshader` (FogVolume path — pure god-ray)
- Modify: `shaders/cloud_raymarch.glsl` (excise in-march god-ray block + 2 uniforms)
- Modify: `scripts/lab/CloudVolume.cs` (excise god-ray fields + 2 param-buffer floats + knob case)
- Modify: `scripts/lab/TerrainLabUI.cs` (remove GodRays construct/attach/CLI)
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (remove routers + field)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (simplify volfog_d slider; remove SetSun push)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (remove overcast god-ray energy block)
- Modify: `scripts/lab/TerrainLabUI.Moods.cs` (decouple volfog enable from `_godrays`)
- Modify: `data/lab_controls.json` (remove 2 control entries)
- Modify: `scenes/terrain_lab.tscn` (zero the dead `light_volumetric_fog_energy`)
- Delete: `docs/godray-redesign-spec.md` (superseded by THIS plan)

**Phase 2 — Rebuild (canonical volumetric base):**
- Create: `shaders/cloud_shadow_caster.gdshader` (`shader_type spatial`, depth-only, alpha-cut by cloud transmittance)
- Create: `scripts/lab/GodRaysVolumetric.cs` (owns: the caster MeshInstance3D + the env volfog config + sun shadow/energy config; consumes the cloud-shadow texture + sun + region from CloudVolume; non-destructive enable/disable)
- Modify: `scripts/lab/TerrainLabUI.cs` (construct + attach the new module)
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (route the unified controls to it)
- Modify: `data/lab_controls.json` (re-add unified controls: `cloud_godrays` toggle + `godray_strength`)

**Phase 3 — Screen-space crisp layer (SEPARATE ARC — own plan after Phase 2 eye-gate):**
- Not detailed here. Stub noted in Task 12.

---

## PHASE 1 — STRIP

### Task 1: Backup checkpoint

**Files:** none (git + zip only)

- [ ] **Step 1: Confirm clean-ish tree and current branch**

Run:
```bash
cd /c/Wg16/wg-16-project && git status --short && git rev-parse --abbrev-ref HEAD
```
Expected: lists the modified god-ray files from this session; prints the branch name.

- [ ] **Step 2: Commit current WIP so the tag captures a known state**

```bash
cd /c/Wg16/wg-16-project
git add -A
git commit -m "wip: god-ray session state before strip-and-rebuild

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
Expected: a commit is created (or "nothing to commit" if already clean — fine).

- [ ] **Step 3: Tag + zip backup**

```bash
cd /c/Wg16/wg-16-project
git tag backup-godrays-pre-strip-2026-06-19
powershell.exe -NoProfile -Command "Compress-Archive -Path 'C:\Wg16\wg-16-project\*' -DestinationPath 'C:\tmp\wg16-backup-pre-godray-strip-2026-06-19.zip' -Force -CompressionLevel Fastest"
```
Expected: tag created; zip exists at `C:\tmp\wg16-backup-pre-godray-strip-2026-06-19.zip`.

- [ ] **Step 4: Verify the backup zip exists and is non-trivial**

Run:
```bash
powershell.exe -NoProfile -Command "(Get-Item 'C:\tmp\wg16-backup-pre-godray-strip-2026-06-19.zip').Length"
```
Expected: a byte count > 1000000 (project is multi-MB).

---

### Task 2: Excise the in-march god-ray block from the cloud raymarch (paired std430 edit)

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` (remove uniforms lines ~36-37; gate/phase lines ~297-302; in-scatter block lines ~347-355)
- Modify: `scripts/lab/CloudVolume.cs` (remove the two `.F(...)` god-ray fields ~437-438; the fields ~79-85; the knob case ~533)

**Interfaces:**
- Produces: a cloud raymarch with NO `godrays`/`godray_strength` uniforms, and a `CloudVolume` whose `BuildParams` writes exactly the fields the shader still declares (std430 in sync).

- [ ] **Step 1: Write the failing test (shadowcheck is the health guard)**

There is no unit-test framework; the project's health guard is the in-engine `--shadowcheck` (CloudShadowCheck.Run prints PASS/FAIL by correlating shadow vs cloud-overhead). We use it as the regression gate: it must still PASS after this paired edit (proves the param buffer is still aligned — a misaligned buffer corrupts the march and the correlation collapses to FAIL).

Establish the BASELINE now (before editing):
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --shadowcheck --clouds=1 2>&1 | grep -iE "shadowcheck|PASS|FAIL"
```
Expected: a line containing `PASS`. (Record it; this is the pre-edit baseline.)

- [ ] **Step 2: Remove the two god-ray uniforms from the shader's ParamsBuf**

In `shaders/cloud_raymarch.glsl`, delete these two lines (the ParamsBuf struct fields, ~line 36-37):
```glsl
    float godrays;       // 0/1 enable in-march in-scatter (crepuscular rays)
    float godray_strength;
```
(Do not delete any surrounding fields. Note exact remaining order for Step 4.)

- [ ] **Step 3: Remove the gate/phase setup and the in-scatter block from the shader**

In `shaders/cloud_raymarch.glsl`, delete the gate+phase setup (~lines 297-302):
```glsl
        // GOD RAYS (roadmap #6): in-march in-scatter. Only meaningful near the sun direction
        // (where crepuscular rays are seen) → grGate bounds cost. Uses CLOUD optical depth toward
        // the sun (NOT the scene's directional-light volumetric shadow — that collision is what
        // made the old removed FogVolume's black wedges). Default OFF (P.godrays).
        float grGate = (P.godrays > 0.5) ? pow(max(cosA, 0.0), 3.0) : 0.0;
        float grPhase = hg(cosA, 0.55);
```
And delete the in-scatter accumulation block in the empty-space branch (~lines 347-355):
```glsl
                // GOD RAYS: air in-scatter between clouds. Bright where the sun reaches through a
                // gap, dark where a cloud occludes it (sunVis) → crepuscular shafts. Adds light
                // only (no alpha), weighted by view transmittance T so it sits behind clouds.
                if (grGate > 0.001){
                    float odSun = light_optical_depth(p, L, windOff);
                    float sunVis = exp(-odSun * 3.0);
                    scattered += T * sunCol * (P.godray_strength * 0.03 * grGate * grPhase * sunVis * (coarseStep * 0.001));
                }
```
Leave the `else { ... t += coarseStep; }` empty-space skip itself intact (the `t += coarseStep;` line stays — only the `if (grGate...)` block inside is removed). Verify the brace structure of the loop still balances after removal.

- [ ] **Step 4: Remove the matching std430 fields in CloudVolume.BuildParams**

In `scripts/lab/CloudVolume.cs`, delete the two chained writer calls (~line 437-438):
```csharp
            .F(_godraysOn ? 1f : 0f)                    // god-ray in-scatter enable (roadmap #6)
            .F(_godrayStrength)
```
CRITICAL: these two fields sat between `.F(dbgdeck...)` (the field above them) and the `tail` vec4 (wind/cell_scale/layer_count). After deletion the writer must produce the same layout the edited shader now expects (dbgdeck → tail directly). Confirm by reading the lines immediately above and below to ensure no other field is disturbed.

- [ ] **Step 5: Remove the now-orphaned god-ray fields + knob case in CloudVolume**

In `scripts/lab/CloudVolume.cs`, delete the fields (~lines 79-85):
```csharp
    // God rays are rebuilt as in-march in-scatter (refactor T7) — no FogVolume. This
    // flag is read into the raymarch param buffer (0/1 multiply on the in-scatter term).
    private bool _godraysOn = false;   // OFF by default

    public bool GodraysOn => _godraysOn;
    public void SetGodraysEnabled(bool on) { _godraysOn = on; }
    private float _godrayStrength = 1.0f;
```
And delete the knob case (~line 533):
```csharp
            case "godray_strength": _godrayStrength = v; break;
```
(Removing `GodraysOn`/`SetGodraysEnabled` will break references in TerrainLabUI.Process.cs and .Clouds.cs — those are removed in Task 4/5. To keep the build green incrementally, do Tasks 2,4,5 before building; build verification is Step 6 of Task 5.)

- [ ] **Step 6: (Deferred) build + shadowcheck** — verified at the end of the strip (Task 6) since intermediate states won't compile. Proceed to Task 3.

---

### Task 3: Delete the two pure-god-ray files (FogVolume path)

**Files:**
- Delete: `scripts/lab/GodRays.cs`
- Delete: `shaders/godray_fog.gdshader`
- Delete: `shaders/godray_fog.gdshader.uid` (if present)
- Delete: `shaders/cloud_godray_fog.gdshader.uid` (stale uid from an earlier name, if present)

**Interfaces:**
- Produces: removal of the `GodRays` class (all `_godrays` references in TerrainLabUI must be removed in Task 4).

- [ ] **Step 1: Delete the files**

```bash
cd /c/Wg16/wg-16-project
git rm scripts/lab/GodRays.cs shaders/godray_fog.gdshader
git rm --ignore-unmatch shaders/godray_fog.gdshader.uid shaders/cloud_godray_fog.gdshader.uid
```
Expected: files staged for deletion. (`--ignore-unmatch` tolerates absent .uid files.)

- [ ] **Step 2: Confirm no other code references the GodRays type beyond the known sites**

Run:
```bash
cd /c/Wg16/wg-16-project && grep -rn "GodRays\|_godrays\b" scripts/ --include=*.cs
```
Expected: hits ONLY in `TerrainLabUI.cs`, `TerrainLabUI.Clouds.cs`, `TerrainLabUI.Apply.cs` (all handled in Task 4). If hits appear elsewhere, handle them in Task 4 too.

---

### Task 4: Remove GodRays wiring from TerrainLabUI core + Apply

**Files:**
- Modify: `scripts/lab/TerrainLabUI.cs` (remove construct ~59; attach block ~101-109; CLI apply ~122)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (simplify `volfog_d` ~116-121; remove SetSun push ~139)

**Interfaces:**
- Consumes: nothing (pure removal).
- Produces: a TerrainLabUI with no `_godrays` field references (the field itself is in .Clouds.cs, removed in Task 5).

- [ ] **Step 1: Remove the GodRays construction in `_Ready`**

In `scripts/lab/TerrainLabUI.cs` (~line 59), delete:
```csharp
        _godrays = new GodRays { Name = "GodRays" };   // Component A: volumetric shafts (consumes the cloud shadow map)
```

- [ ] **Step 2: Remove the attach block in `AttachClouds`**

In `scripts/lab/TerrainLabUI.cs` (~lines 96-108), delete the whole comment + `if (_godrays != null) { ... }` block that adds the node, calls `Attach`, `SetShadowTexture`, `SetCloudHeight`.

- [ ] **Step 3: Remove the CLI apply line**

In `scripts/lab/TerrainLabUI.cs` (~line 122), delete:
```csharp
        if (_godraysOnCli >= 0) { _godrays?.SetEnabled(_godraysOnCli == 1); }   // --godrays drives the volumetric shafts now
```
(Keep `_godraysOnCli` parsing in Cli.cs for now — it will be repurposed in Phase 2 Task 11. A parsed-but-unused field is harmless.)

- [ ] **Step 4: Simplify the `volfog_d` slider in Apply.cs**

In `scripts/lab/TerrainLabUI.Apply.cs` (~lines 116-121), replace the GodRays-aware case with the plain env write:
```csharp
            case "volfog_d":        env.VolumetricFogDensity = v; break;
```
(Removes the `_godrays.RedirectBaseDensity` redirection and the stale retired-control comment.)

- [ ] **Step 5: Remove the SetSun push to god rays in Apply.cs**

In `scripts/lab/TerrainLabUI.Apply.cs` (~line 139), delete:
```csharp
        _godrays?.SetSun(toSun, sun.LightColor);   // volumetric shafts track the same sun
```
(The `PushSunToCloud` method keeps its `_cloud?.SetSun(...)` and disc-energy lines — only the god-ray line goes.)

---

### Task 5: Remove GodRays routing from TerrainLabUI.Clouds + Process + Moods

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (field ~16; float router ~19-20; bool router ~26-28)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (overcast god-ray energy block ~46-55)
- Modify: `scripts/lab/TerrainLabUI.Moods.cs` (volfog enable gate ~94-96)

**Interfaces:**
- Produces: a build-clean codebase with ALL god-ray code removed. After this task the project compiles.

- [ ] **Step 1: Remove the `_godrays` field + routers in Clouds.cs**

In `scripts/lab/TerrainLabUI.Clouds.cs`:
- Delete the field (~line 16): `private GodRays? _godrays;   // Component A: ...`
- In `ApplyCloudFloat`, delete the godray_strength router (~lines 19-20):
```csharp
        // god rays are their own subsystem now (FogVolume), NOT the old in-cloud in-scatter.
        if (knob == "godray_strength") { _godrays?.SetStrength(v); return; }
```
- In `ApplyCloudBool`, delete the godrays router (~lines 26-28):
```csharp
        // "god rays" toggle now drives the volumetric FogVolume system (redesign 2026-06-18),
        // unifying what used to be two knobs (Light-tab uniform fog + in-cloud in-scatter).
        if (knob == "godrays") { _godrays?.SetEnabled(on); _cloud?.SetGodraysEnabled(on); return; }
```

- [ ] **Step 2: Remove the overcast god-ray energy block in Process.cs**

In `scripts/lab/TerrainLabUI.Process.cs` (~lines 46-55), delete the entire `// God-ray scatter energy ...` comment block and the `if (_cloud.GodraysOn) { ... }` block (it references the now-deleted `GodraysOn`). Leave the rest of `UpdateOvercast` (ambient/sun/fog tint) intact.

- [ ] **Step 3: Decouple volfog enable from `_godrays` in Moods.cs**

In `scripts/lab/TerrainLabUI.Moods.cs` (~lines 93-96), the current line gates volumetric fog on `_godrays`. Since god rays are gone (and will be re-added in Phase 2 owning their own volfog state), set volumetric fog OFF here (the documented default — it was "the main can't-see-anything culprit"):
```csharp
        // Volumetric fog OFF by default (it was the main 'can't see anything' culprit). The
        // rebuilt god-ray system (Phase 2) owns turning it on while active.
        env.VolumetricFogEnabled = false;
```

- [ ] **Step 4: Build**

Run:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded|Build FAILED"
```
Expected: `Build succeeded.` 0 errors. (Pre-existing nullable warnings are fine.) If errors name a leftover `_godrays`/`GodraysOn`/`SetGodraysEnabled`, remove that reference and rebuild.

- [ ] **Step 5: Commit the code strip**

```bash
cd /c/Wg16/wg-16-project
git add -A
git commit -m "Strip all 3 god-ray implementations (FogVolume + in-march + wiring)

Removes GodRays.cs, godray_fog.gdshader, the in-march in-scatter block in
cloud_raymarch.glsl (+ paired std430 fields in CloudVolume.BuildParams), and all
UI/CLI routing. Cloud rendering, terrain, and the shadow-map bake are untouched.
Rebuild on the canonical sun-shadow + cloud-caster path follows (Phase 2).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Verify the strip is clean (build + shadowcheck + visual sanity)

**Files:** none (verification only)

- [ ] **Step 1: shadowcheck still PASS (proves std430 buffer still aligned)**

Run:
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --shadowcheck --clouds=1 2>&1 | grep -iE "shadowcheck|PASS|FAIL"
```
Expected: `PASS` (same as the Task 2 Step 1 baseline). A FAIL here means the param buffer is misaligned — re-check Task 2 Steps 2/4 removed exactly the paired fields.

- [ ] **Step 2: Remove the leftover god-ray data control entries**

In `data/lab_controls.json`, delete the two entries (~lines 137-138 / wherever they are now):
```json
    { "id": "cloud_godrays", "label": "god rays (volumetric)", "tab": "Clouds", "type": "cloud", "cloud": "godrays", "default": false, "rand": false },
    { "id": "cloud_godray_strength", "label": "god ray strength", "tab": "Clouds", "type": "cloudf", "cloud": "godray_strength", "min": 0.0, "max": 12.0, "default": 4.0, "rand": false },
```
(They will be re-added in Phase 2 Task 11 pointing at the new module. Removing now avoids a control whose knob routes nowhere — a silent no-op per memory `lab-registry-param-gotcha`.)

- [ ] **Step 3: Zero the dead sun fog energy in the scene**

In `scenes/terrain_lab.tscn` (~line 81), change:
```
light_volumetric_fog_energy = 0.4
```
to
```
light_volumetric_fog_energy = 1.0
```
(Godot's default; the rebuilt module sets it explicitly while active. Setting to the default keeps the .tscn diff minimal and avoids a stale tuned value.)

- [ ] **Step 4: Delete the superseded spec doc**

```bash
cd /c/Wg16/wg-16-project && git rm docs/godray-redesign-spec.md
```

- [ ] **Step 5: Launch windowed, clouds on, NO god rays — confirm clean scene (eye-gate)**

```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --mood=5 --clouds=1
```
USER EYE-GATE: terrain + clouds render normally, NO uniform fog, NO god-ray controls in the Clouds tab. Confirm nothing regressed (clouds shade correctly, ground cloud-shadows still work). Approve before Phase 2.

- [ ] **Step 6: Commit the cleanup**

```bash
cd /c/Wg16/wg-16-project
git add -A
git commit -m "Strip god-ray data controls, dead scene energy, superseded spec

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## PHASE 2 — REBUILD (canonical volumetric base: sun shadow + cloud caster)

### Task 7: The cloud-shadow-caster shader (depth-only, alpha-cut by cloud transmittance)

**Files:**
- Create: `shaders/cloud_shadow_caster.gdshader`

**Interfaces:**
- Produces: a `shader_type spatial` material that, applied to a large quad oriented perpendicular to the sun, writes the directional shadow atlas ONLY where clouds are dense (transmittance below a threshold), so cloud gaps pass light. Uniforms: `sampler2D cloud_shadow_tex`, `float shadow_region`, `float cut_threshold`, `float softness`.

- [ ] **Step 1: Write the shader**

Create `shaders/cloud_shadow_caster.gdshader`:
```glsl
shader_type spatial;
// Cloud-shadow CASTER. A large quad (oriented ⟂ to the sun, high above the scene) wears this.
// It is INVISIBLE to the camera and writes ONLY depth — but because it's alpha-cut by the cloud
// transmittance map, it casts the CLOUD SHAPE into the DirectionalLight's shadow atlas. Godot's
// volumetric fog samples that same atlas per froxel, so cloud gaps become bright shafts and cloud
// bodies stay dim — true 3D shafts on BOTH terrain and fog, with NO custom fog-light hack.
//
// Why alpha-cut (not a fog emission fake): the shaft IS a shadow. The canonical engine path makes
// shafts from the sun shadow map; we just put the clouds INTO that map. (See memory
// godray-emission-vs-albedo-rootcause.) Soft edges via ALPHA_HASH so silhouettes aren't cut-out hard.
render_mode unshaded, shadows_only, cull_disabled, depth_draw_opaque;

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;   // world metres across the cloud map (XZ, centered on origin)
uniform float cut_threshold = 0.5;      // transmittance below this = "cloud" = casts shadow
uniform float softness = 0.15;          // hash-dither width around the threshold for soft edges

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Sample the cloud transmittance at this caster point's world XZ. The caster plane is large &
	// world-aligned, so world.xz maps directly into the cloud map (same convention as the terrain).
	vec2 uv = v_world.xz / shadow_region + 0.5;
	float vis = 1.0;
	if (all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)))) {
		vis = texture(cloud_shadow_tex, uv).r;   // 1 = sun reaches (gap), 0 = under cloud
	}
	// Cloudy (low vis) → opaque (cast shadow). Gap (high vis) → discard (let light through).
	// ALPHA_HASH turns the smooth threshold into a stochastic dither → soft shadow edge after the
	// shadow blur + fog ESM term, instead of a hard binary silhouette.
	ALPHA = clamp((cut_threshold - vis) / max(softness, 1e-3) + 0.5, 0.0, 1.0);
	ALPHA_HASH_SCALE = 1.0;
}
```

- [ ] **Step 2: Compile-check the shader by launching (it won't be used yet)**

The shader is validated when the material is created in Task 8. No standalone test. Proceed.

---

### Task 8: GodRaysVolumetric module (caster node + env volfog config + sun config)

**Files:**
- Create: `scripts/lab/GodRaysVolumetric.cs`

**Interfaces:**
- Consumes: `cloud_shadow_tex` (Texture2D from `CloudVolume.ShadowTexture`), `region` (`CloudVolume.RegionSize`), the sun `DirectionalLight3D`, the `Environment`.
- Produces: public surface `Attach(Environment env, DirectionalLight3D sun)`, `SetShadowTexture(Texture2D? tex, float region)`, `SetEnabled(bool on)`, `SetStrength(float s)`, `SetSunDir(Vector3 toSun)`. Non-destructive: saves/restores env volfog + sun shadow/energy on toggle.

- [ ] **Step 1: Write the module**

Create `scripts/lab/GodRaysVolumetric.cs`:
```csharp
using Godot;

namespace WG16.Lab;

/// God rays — CANONICAL volumetric base (rebuild 2026-06-19). Shafts are the DirectionalLight's
/// REAL shadow scattering through global volumetric fog. The clouds occlude the sun via a
/// shadow-casting quad (cloud_shadow_caster.gdshader) alpha-cut by the cloud transmittance map, so
/// cloud gaps become bright shafts and cloud bodies cast shadow on BOTH terrain and fog. NO custom
/// fog-emission shader, NO FogVolume-as-light. See memory godray-emission-vs-albedo-rootcause.
///
/// Owns: the caster MeshInstance3D (perpendicular-to-sun quad, high up, shadows_only). Configures
/// the Environment volfog (low density, forward anisotropy, no ambient inject) + the sun (shadow ON,
/// high volumetric_fog_energy) while active, restoring prior values on disable. Consumes the cloud
/// shadow texture + region + sun from the UI; never touches cloud internals.
public partial class GodRaysVolumetric : Node3D
{
    private MeshInstance3D _caster = null!;
    private ShaderMaterial _casterMat = null!;
    private Godot.Environment? _env;
    private DirectionalLight3D? _sun;

    private bool _on;
    private bool _stateSaved;
    private bool _savedVolfog, _savedSunShadow;
    private float _savedLength, _savedDensity, _savedAnis, _savedAmbInject, _savedSunVolEnergy;
    private Color _savedAlbedo;

    // Canonical outdoor tuning (memory godray-emission-vs-albedo-rootcause):
    private const float EnvDensity = 0.02f;        // LOW — thin medium, see through it; only shafts pop
    private const float EnvLength = 3000f;         // froxel range; tighter = sharper near shafts
    private const float EnvAnisotropy = 0.85f;     // forward scatter → air blazes toward the sun
    private const float EnvAmbientInject = 0.0f;   // keep lit/shadow contrast (no ambient fill)
    private static readonly Color EnvAlbedo = new Color(1f, 0.97f, 0.92f);  // warm single-scatter
    private const float SunVolEnergy = 48f;        // shaft brightness = this × the thin medium
    private const float CasterAltitude = 2500f;    // quad height above terrain mid (above cloud base)
    private const float CasterSize = 16000f;       // covers the whole shadow region with sun-angle slack

    public GodRaysVolumetric()
    {
        _casterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cloud_shadow_caster.gdshader") };
        var mesh = new PlaneMesh { Size = new Vector2(CasterSize, CasterSize) };
        _caster = new MeshInstance3D
        {
            Name = "CloudShadowCaster",
            Mesh = mesh,
            MaterialOverride = _casterMat,
            // shadows_only render_mode means it never draws color; cast shadows on, receive off.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
            Visible = false,   // default OFF
        };
        AddChild(_caster);
    }

    public void Attach(Godot.Environment env, DirectionalLight3D sun) { _env = env; _sun = sun; }

    public void SetShadowTexture(Texture2D? tex, float region)
    {
        if (tex != null) { _casterMat.SetShaderParameter("cloud_shadow_tex", tex); }
        _casterMat.SetShaderParameter("shadow_region", region);
    }

    /// Orient the caster quad perpendicular to the sun and position it high along the sun ray, so
    /// its cloud-cut shadow projects straight down the sun direction onto terrain + fog.
    public void SetSunDir(Vector3 toSun)
    {
        if (toSun.LengthSquared() < 1e-4f) { return; }
        toSun = toSun.Normalized();
        // place the quad up-sun, above the scene; face it down the sun ray.
        _caster.GlobalPosition = new Vector3(0f, CasterAltitude, 0f);
        // PlaneMesh normal is +Y; rotate so +Y points toward the sun.
        Vector3 up = Vector3.Up;
        if (Mathf.Abs(up.Dot(toSun)) > 0.999f) { _caster.GlobalRotation = Vector3.Zero; }
        else
        {
            Vector3 axis = up.Cross(toSun).Normalized();
            float ang = Mathf.Acos(Mathf.Clamp(up.Dot(toSun), -1f, 1f));
            _caster.GlobalTransform = new Transform3D(new Basis(axis, ang), _caster.GlobalPosition);
        }
    }

    public void SetStrength(float s) => _casterMat.SetShaderParameter("cut_threshold", Mathf.Clamp(0.5f + (s - 4f) * 0.05f, 0.05f, 0.95f));

    public bool On => _on;

    public void SetEnabled(bool on)
    {
        _on = on;
        _caster.Visible = on;
        if (_env == null) { return; }
        if (on)
        {
            if (!_stateSaved)
            {
                _savedVolfog = _env.VolumetricFogEnabled;
                _savedLength = _env.VolumetricFogLength;
                _savedDensity = _env.VolumetricFogDensity;
                _savedAlbedo = _env.VolumetricFogAlbedo;
                _savedAnis = _env.VolumetricFogAnisotropy;
                _savedAmbInject = _env.VolumetricFogAmbientInject;
                if (_sun != null) { _savedSunShadow = _sun.ShadowEnabled; _savedSunVolEnergy = _sun.LightVolumetricFogEnergy; }
                _stateSaved = true;
            }
            _env.VolumetricFogEnabled = true;
            _env.VolumetricFogLength = EnvLength;
            _env.VolumetricFogDensity = EnvDensity;
            _env.VolumetricFogAlbedo = EnvAlbedo;
            _env.VolumetricFogAnisotropy = EnvAnisotropy;
            _env.VolumetricFogAmbientInject = EnvAmbientInject;
            if (_sun != null) { _sun.ShadowEnabled = true; _sun.LightVolumetricFogEnergy = SunVolEnergy; }
        }
        else if (_stateSaved)
        {
            _env.VolumetricFogEnabled = _savedVolfog;
            _env.VolumetricFogLength = _savedLength;
            _env.VolumetricFogDensity = _savedDensity;
            _env.VolumetricFogAlbedo = _savedAlbedo;
            _env.VolumetricFogAnisotropy = _savedAnis;
            _env.VolumetricFogAmbientInject = _savedAmbInject;
            if (_sun != null) { _sun.ShadowEnabled = _savedSunShadow; _sun.LightVolumetricFogEnergy = _savedSunVolEnergy; }
            _stateSaved = false;
        }
    }
}
```

- [ ] **Step 2: Build**

Run:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded|Build FAILED"
```
Expected: `Build succeeded.` 0 errors. Fix any API-name errors (e.g. exact enum name `GeometryInstance3D.ShadowCastingSetting.ShadowsOnly`) by checking the Godot 4.6 C# API and rebuild.

---

### Task 9: Wire the new module into TerrainLabUI (construct + attach)

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (add `_godrays` field of new type)
- Modify: `scripts/lab/TerrainLabUI.cs` (construct in `_Ready`, attach in `AttachClouds`)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (push sun dir to the module in `PushSunToCloud`)

**Interfaces:**
- Consumes: `GodRaysVolumetric` from Task 8.
- Produces: a live, attached `_godrays` instance fed the cloud shadow texture + sun.

- [ ] **Step 1: Add the field**

In `scripts/lab/TerrainLabUI.Clouds.cs`, near the `_cloud` field, add:
```csharp
    private GodRaysVolumetric? _godrays;   // canonical volumetric god-ray base (sun shadow + cloud caster)
```

- [ ] **Step 2: Construct it in `_Ready`**

In `scripts/lab/TerrainLabUI.cs`, where `_cloud` is constructed (~line 58), add after it:
```csharp
        _godrays = new GodRaysVolumetric { Name = "GodRaysVolumetric" };
```

- [ ] **Step 3: Attach it in `AttachClouds`**

In `scripts/lab/TerrainLabUI.cs`, in `AttachClouds` after the cloud shadow texture is available (after the `_cloud.ShadowTexture` block), add:
```csharp
        if (_godrays != null)
        {
            GetNode("/root/TerrainLabRoot").AddChild(_godrays);
            _godrays.Attach(GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment,
                            GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun"));
            _godrays.SetShadowTexture(_cloud.ShadowTexture, _cloud.RegionSize);
        }
```

- [ ] **Step 4: Push the sun direction to the module**

In `scripts/lab/TerrainLabUI.Apply.cs`, in `PushSunToCloud` (after the `_cloud?.SetSun(...)` calls), add:
```csharp
        _godrays?.SetSunDir(toSun);   // orient the cloud-shadow caster down the sun ray
```

- [ ] **Step 5: Build**

Run:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded|Build FAILED"
```
Expected: `Build succeeded.` 0 errors.

---

### Task 10: Re-add the unified controls + CLI, route to the new module

**Files:**
- Modify: `data/lab_controls.json` (re-add `cloud_godrays` toggle + `godray_strength`)
- Modify: `scripts/lab/TerrainLabUI.Clouds.cs` (route both to `_godrays`)
- Modify: `scripts/lab/TerrainLabUI.cs` (re-add the `--godrays` CLI apply, now to `_godrays`)

**Interfaces:**
- Consumes: `_godrays.SetEnabled`, `_godrays.SetStrength`.
- Produces: the Clouds-tab "god rays (volumetric)" checkbox + "god ray strength" slider + `--godrays=1` CLI all driving the ONE rebuilt system.

- [ ] **Step 1: Re-add the two control entries**

In `data/lab_controls.json` (Clouds tab section), add:
```json
    { "id": "cloud_godrays", "label": "god rays (volumetric)", "tab": "Clouds", "type": "cloud", "cloud": "godrays", "default": false, "rand": false },
    { "id": "cloud_godray_strength", "label": "god ray strength", "tab": "Clouds", "type": "cloudf", "cloud": "godray_strength", "min": 0.0, "max": 12.0, "default": 4.0, "rand": false },
```

- [ ] **Step 2: Route the controls in Clouds.cs**

In `scripts/lab/TerrainLabUI.Clouds.cs`:
- In `ApplyCloudFloat`, add (before `_cloud?.SetKnob`):
```csharp
        if (knob == "godray_strength") { _godrays?.SetStrength(v); return; }
```
- In `ApplyCloudBool`, add (before `_cloud?.SetKnobBool`):
```csharp
        if (knob == "godrays") { _godrays?.SetEnabled(on); return; }
```

- [ ] **Step 3: Re-add the CLI apply (now to the new module)**

In `scripts/lab/TerrainLabUI.cs`, in `AttachClouds` (near the other CLI overrides, after the attach block), add:
```csharp
        if (_godraysOnCli >= 0) { _godrays?.SetEnabled(_godraysOnCli == 1); }
```

- [ ] **Step 4: Build**

Run:
```bash
cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -c Debug -v quiet --nologo 2>&1 | grep -iE "error|Build succeeded|Build FAILED"
```
Expected: `Build succeeded.` 0 errors.

- [ ] **Step 5: Commit the rebuild scaffolding**

```bash
cd /c/Wg16/wg-16-project
git add -A
git commit -m "Rebuild god rays: canonical sun-shadow + cloud-caster volumetric base

New GodRaysVolumetric module + cloud_shadow_caster.gdshader: clouds cast into the
DirectionalLight shadow atlas (alpha-cut quad), global volfog lit by that real
shadow produces 3D cloud-shaped shafts on terrain + fog. Unified controls (Clouds
tab toggle + strength + --godrays) drive this ONE system.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: Eye-gate the volumetric base in motion + tune

**Files:** none (verification + tuning constants in `GodRaysVolumetric.cs` / caster shader as needed)

- [ ] **Step 1: Launch under good conditions, god rays ON**

```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --mood=4 --preset=2 --godrays=1
```
(Dramatic Storm low sun + Broken clouds = gaps + cloud.)

- [ ] **Step 2: USER EYE-GATE (in motion)**

Fly the camera; look toward and away from the sun; orbit. Confirm:
- Distinct shafts angle down through cloud GAPS onto the terrain/valley (not uniform fog).
- Shafts align with the bright patches of the ground cloud-shadow (same map drives both).
- Air brightens toward the sun (forward anisotropy); terrain visible through thin haze.
- Toggling the Clouds-tab checkbox OFF cleanly restores the prior look (non-destructive).
- `god ray strength` slider visibly changes shaft contrast.

If shafts are too hard-edged → raise caster `softness`. Too dim → raise `SunVolEnergy`. Too foggy → lower `EnvDensity`. Wedge/banding at low sun → check caster orientation + `directional_shadow_max_distance` covers `CasterAltitude`. Tune, rebuild, re-gate until approved.

- [ ] **Step 3: shadowcheck still PASS + perf note**

```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path C:\Wg16\wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- --shadowcheck --clouds=1 2>&1 | grep -iE "PASS|FAIL"
```
Expected: `PASS`. Also note fps with god rays on vs off (windowed HUD) for the perf table.

- [ ] **Step 4: On approval, commit final tuning + update docs**

```bash
cd /c/Wg16/wg-16-project
git add -A
git commit -m "God rays volumetric base: tuned + eye-gate approved

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```
Update `docs/ROADMAP.md` + `docs/HANDOFF.md`: replace the old in-march/FogVolume god-ray notes with the canonical sun-shadow + cloud-caster description and the perf numbers from Step 3.

---

### Task 12: (STUB — separate arc) Screen-space crisp layer

Not implemented in this plan. After Task 11 is eye-gate approved, write a NEW plan for the GPU Gems 3 Ch.13 screen-space radial-scatter post-process (crisp drama layer) that composites additively on top of this volumetric base. Reference: https://developer.nvidia.com/gpugems/gpugems3/part-ii-light-and-shadows/chapter-13-volumetric-light-scattering-post-process. Gate it to the sun on/near screen; fade when behind camera. Drive it from the SAME `cloud_godrays` control group (add a sub-toggle + strength).

---

## Self-Review

**Spec coverage:** The user's directive was "back up, strip all god-ray stuff cleanly without hurting anything else, rebuild on what's good/AAA." Coverage: Task 1 (backup) ✓; Tasks 2-6 (strip all three designs + data/scene/docs, with the std430 paired-edit hazard handled, build + shadowcheck gates) ✓; Tasks 7-11 (rebuild the chosen canonical sun-shadow + cloud-caster base, eye-gated) ✓; Task 12 (screen-space layer scoped as a follow-up arc per the staged AAA-hybrid decision) ✓.

**Placeholder scan:** No TBD/TODO/"handle edge cases". Every code step shows the actual code; every command shows expected output. The one intentional non-detailed item (Task 12) is explicitly a stub for a separate plan, not a placeholder inside an active task.

**Type consistency:** New module is `GodRaysVolumetric` throughout (distinct from the deleted `GodRays`). Public methods referenced in wiring (`Attach(env,sun)`, `SetShadowTexture(tex,region)`, `SetEnabled(bool)`, `SetStrength(float)`, `SetSunDir(Vector3)`, `On`) match their definitions in Task 8. Control knob strings (`godrays`, `godray_strength`) match between `lab_controls.json` (Task 10 Step 1) and the routers (Task 10 Step 2). The `_godraysOnCli` field is parsed in Cli.cs (kept during strip Task 4 Step 3) and re-consumed in Task 10 Step 3.

**Hazard coverage:** std430 paired edit (Task 2, both shader + C# in one task, gated by shadowcheck). Build won't compile mid-strip → Tasks 2/4/5 batched before the first build (noted in Task 2 Step 6). `--` CLI delimiter + windowed launch + absolute path in Global Constraints and every launch command.
