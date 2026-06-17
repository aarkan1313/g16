# Volumetric Clouds + Matched Terrain Shadows — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raymarched volumetric clouds in the look-lab sky (HZD/Nubis technique), with a matched 2D cloud-shadow map that attenuates the terrain sun — overhead cloud and ground shadow from one density field.

**Architecture:** A compute shader raymarches a Perlin-Worley density field into an `rgba16f` texture (radiance+opacity), written via Godot's `RenderingDevice`/`TextureRD` and composited by a custom **sky shader**. Work is amortized over frames (subset of the texture updated per frame via an `update_position` offset) and run at reduced resolution. A second cheap pass bakes a top-down sun-transmittance map sampled by the terrain shader's existing `light()` for matched ground shadows. Noise is GPU-baked in-repo (our policy), not shipped.

**Tech Stack:** Godot 4.6 mono, C#, GLSL compute (`RenderingDevice` local + `TextureRD`), Godot sky shader, custom `light()`.

**Reference, don't copy:** Studied `clayjohn/godot-volumetric-cloud-demo-v2` (MIT) and HZD/Nubis (Schneider/Guerrilla) for *technique* — 128-step shell march, FBM shape `n.g*.625+n.b*.25+n.a*.125`, coverage remap, height-gradient cloud types, 6-sample light cone, stacked Henyey-Greenstein, Beer+powder, `update_position` temporal tiling. We write our OWN implementation fitting WG16 patterns. No code lifted.

**Verification model:** GPU/visual, no unit harness. Each task gate = `dotnet build` + headless `--import` (shaders compile) + `--auto-shot` (draws, frame-time printed). **THE gate is the user flying it live in motion** — and for this feature **perf is co-equal with look** (frame time on the user's GPU). Stage builds are handed to the user to judge before the next stage. Commit per task.

**Environment (from HANDOFF):**
- Project dir: `C:\Wg16\wg-16-project`
- Godot console exe: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
- Windowed exe: same path without `_console`.
- ALWAYS `--rendering-driver vulkan`. ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`). After new .cs/shader: build → headless `--import` → launch. Auto-shot: `-- --auto-shot=<abs.png>`.

**Existing patterns to mirror:**
- `scripts/lab/SplatCompute.cs` / `scripts/field/FieldCompute.cs` — local-RD compute + readback (for the one-time noise bake + the shadow-map readback).
- `shaders/terrain_lab.gdshader` `light()` from reverted commit `d3baae6` (cloud sun-attenuation body) — re-create the inert-when-off `light()`, but fed by the shadow map.
- `data/lab_controls.json` Clouds tab + `TerrainLabUI` registry (slider/toggle→`SetFloat`/`SetBool`).
- `scenes/terrain_lab.tscn` WorldEnvironment — the Sky resource gets our sky shader.

> **NOTE on the per-frame compute path:** The one-time noise bake uses the proven local-RD readback pattern (Task 1). The PER-FRAME raymarch must instead render on the MAIN RenderingDevice and expose its output as a `Texture2Drd` to the sky shader (no per-frame GPU→CPU readback — that would stall). This is the one genuinely new infra piece (Task 3); it follows Godot's `TextureRD` compute-to-sky pattern. If `Texture2Drd` wiring proves fragile, the documented fallback is a `SubViewport` fragment-shader raymarch (slower, simpler) — flagged in Task 3.

---

## Task 1: 3D Perlin-Worley noise bake (shape + detail)

**Files:**
- Create: `shaders/cloud_noise_3d.glsl` (compute; writes a 3D noise volume to a storage buffer)
- Create: `scripts/lab/CloudNoiseCompute.cs` (dispatch + readback → two `ImageTexture3D`s: shape RGBA, detail R)

**Approach:** Mirror `SplatCompute.cs` exactly (local RD, `#[compute]` strip, SPIR-V compile, dispatch, `BufferGetData`). Output a tileable volume. Shape = a 4-channel texture: R = low-freq Perlin-Worley, G/B/A = increasing-freq Worley octaves (so the shader's FBM `g*.625+b*.25+a*.125` works). Detail = single-channel high-freq Worley. Resolution: shape `128³`, detail `32³` (Nubis sizes; tunable). Worley = cellular noise (min distance to jittered feature points on a tiled grid); Perlin-Worley = `remap(perlin, worley-1, 1, 0, 1)`.

- [ ] **Step 1: Write `cloud_noise_3d.glsl`** — compute shader, `local_size 8,8,1` over Z slices (or 4,4,4). Implement tileable 3D value/gradient noise + tileable 3D Worley (feature points modulo grid). Output `shape[idx] = vec4(perlinWorley, worleyHi1, worleyHi2, worleyHi3)` and a second dispatch (or second buffer) for `detail`. Pack to a buffer of `res³` RGBA floats. (Full GLSL written at implementation time following the Worley/Perlin-Worley formulas; ~120 lines.)

- [ ] **Step 2: Write `CloudNoiseCompute.cs`** — `Bake()` returns `(ImageTexture3D shape, ImageTexture3D detail)`. Use `Image.CreateFromData` per slice → `ImageTexture3D.Create(Rgbaf, w,h,d, false, slices)`. Mirror SplatCompute's RID lifecycle + Dispose.

- [ ] **Step 3: Build + import.**
Run: `dotnet build WG16.csproj` then headless `--import`. Expected: 0 errors, `cloud_noise_3d.glsl` reimports clean.

- [ ] **Step 4: Verify the noise visually (debug).** Temporarily bind the shape texture's a mid-Z slice to a debug quad OR print min/max/mean of the buffer in C#. Expected: full 0..1 range, cloud-like blobs (not uniform, not pure static). This is a self-check, not the user gate.

- [ ] **Step 5: Commit.**
```bash
git add shaders/cloud_noise_3d.glsl scripts/lab/CloudNoiseCompute.cs
git commit -m "Add 3D Perlin-Worley cloud noise GPU bake (shape + detail volumes)"
```

---

## Task 2: Weather/coverage field + cloud params

**Files:**
- Create: `scripts/lab/CloudParams.cs` (a record/struct of all cloud knobs, JSON-loadable like `FieldParams`)
- Modify: `data/lab_controls.json` (Clouds tab — coverage/density/type/altitude/thickness/drift/lighting/perf knobs)

**Approach:** The weather map can be a cheap 2D FBM generated in C# or a small compute (coverage in R, type in G, optionally rain in B). Start with a 2D FBM baked in `CloudNoiseCompute` (reuse it) → `ImageTexture`. Coverage default LOW (sparse — the user's explicit note).

- [ ] **Step 1: Add a 2D weather bake** to `CloudNoiseCompute` (or a tiny separate method): R=coverage FBM, G=type gradient. Return an `ImageTexture`.

- [ ] **Step 2: Write `CloudParams.cs`** — fields: `Coverage=0.35` (LOW default), `Density`, `CloudType` (0 strat→1 cumulus), `AltitudeM`, `ThicknessM`, `DriftSpeed`, `DriftDirDeg`, `HgAniso=0.6`, `Powder`, `SunAbsorption`, and perf: `RaymarchSteps=96`, `UpdateRes` (fraction), `TemporalFrames`. Loaded from `data/cloud_params.json` (create with defaults).

- [ ] **Step 3: Add Clouds-tab rows** to `lab_controls.json` (type slider/toggle, `param`→sky-shader uniform; perf knobs too). Validate JSON: `python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"`.

- [ ] **Step 4: Build.** `dotnet build WG16.csproj` → 0 errors.

- [ ] **Step 5: Commit.**
```bash
git add scripts/lab/CloudParams.cs data/cloud_params.json data/lab_controls.json
git commit -m "Add cloud params + weather field + Clouds control tab"
```

---

## Task 3: Per-frame raymarch compute → sky texture (full-res, no temporal yet)

**Files:**
- Create: `shaders/cloud_raymarch.glsl` (compute; the main raymarch, `rgba16f` out)
- Create: `scripts/lab/CloudVolume.cs` (drives the per-frame dispatch on the MAIN RD; exposes output as `Texture2Drd`)
- Create: `shaders/cloud_sky.gdshader` (sky shader; composites the cloud texture)
- Modify: `scenes/terrain_lab.tscn` (Sky → ShaderMaterial using `cloud_sky.gdshader`)

**Approach (referenced, our own):** Spherical-shell march like Nubis — cloud layer between `inner` and `outer` radii above a large planet radius; per view ray find shell entry/exit, march `RaymarchSteps` steps. Density = FBM shape × height-gradient(cloud type) × coverage-remap(weather) − detail erosion, per the Nubis pipeline. Lighting = 6-sample light cone toward sun, Beer + powder, stacked Henyey-Greenstein phase. Output `vec4(radiance, alpha)`.

- [ ] **Step 1: Write `cloud_raymarch.glsl`** — `local_size 8,8,1`. Push constants: texture_size, time, light dir/color/energy, camera basis, + cloud params via a uniform buffer. Sample shape/detail 3D textures + weather 2D. Implement density + lighting per the Nubis math (constants: shell march 96 steps default, 6 light-cone samples, HG g≈0.6, Beer `exp(-density*t)`, powder `1-exp(-2*density*t)`). Write `rgba16f`.

- [ ] **Step 2: Write `CloudVolume.cs`** — on `_Ready`/`Build`: create the raymarch shader + pipeline on `RenderingServer.GetRenderingDevice()` (MAIN RD, not local), allocate an `rgba16f` storage texture, wrap as `Texture2Drd`. In `_Process`: update push-constants (time, camera, sun), dispatch full texture, no readback. Expose the `Texture2Drd` so the sky material can bind it.

- [ ] **Step 3: Write `cloud_sky.gdshader`** — `shader_type sky;` sample the cloud `Texture2Drd` by view direction (map `EYEDIR` → the cloud texture's hemisphere parameterization), composite `cloud.rgb` over the existing sky color by `cloud.a`. Keep the current sky look when alpha=0.

- [ ] **Step 4: Wire the Sky** in `terrain_lab.tscn` (or in `TerrainLabUI`/`TerrainLab` at runtime): set the Environment's Sky to a ShaderMaterial using `cloud_sky.gdshader`, bind the cloud texture + noise textures + params. Gate behind `cloud_enabled`.

- [ ] **Step 5: Build + import + auto-shot.** Expected: compiles; auto-shot shows CLOUDS IN THE SKY above the terrain; frame-time printed. Perf may be poor (full-res, no temporal) — expected at this stage.

- [ ] **Step 6: Commit.**
```bash
git add shaders/cloud_raymarch.glsl scripts/lab/CloudVolume.cs shaders/cloud_sky.gdshader scenes/terrain_lab.tscn
git commit -m "Volumetric cloud raymarch -> sky composite (full-res, pre-temporal)"
```

- [ ] **Step 7: USER GATE — fly it live.** Hand the user a windowed build. Judge: do the clouds read as real clouds (shape, lighting, silver lining)? Sparse coverage? Tune coverage/density/type/HG live. (Perf bad here is OK — Task 4 fixes it.) **Do not proceed to Task 4 until the LOOK is approved** — no point optimizing a look the user rejects.

---

## Task 4: Reduced-res + temporal amortization (make it affordable)

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` (accept `update_position` offset)
- Modify: `scripts/lab/CloudVolume.cs` (update a subset of the texture per frame; reduced-res target + reconstruction)

**Approach (Nubis temporal tiling):** Render the cloud texture at `UpdateRes` (e.g. half) and update only a fraction of pixels per frame via an `update_position` push-constant offset cycling over `TemporalFrames` frames; the sky shader reads the persistent texture so stale pixels remain until refreshed. Camera moves slowly here → minimal ghosting. Optionally blend two copies to hide updates (clayjohn does this).

- [ ] **Step 1:** Add `update_position` push-constant to the compute; offset the write `pos`. Cycle the offset over `TemporalFrames` in `CloudVolume._Process`.
- [ ] **Step 2:** Lower the dispatch to the per-frame tile size; allocate the texture at `UpdateRes`. Verify no tearing artifacts from partial updates (blend/interpolate if needed).
- [ ] **Step 3: Build + import + auto-shot.** Frame-time should drop substantially. Expected: clouds still present, much cheaper.
- [ ] **Step 4: USER GATE — fly it.** Judge perf (frame time on the user's GPU) + temporal stability (no objectionable ghosting/popping in motion). Tune `UpdateRes`/`TemporalFrames`/`RaymarchSteps` live. **Proceed only when perf is acceptable to the user.**
- [ ] **Step 5: Commit.**
```bash
git add shaders/cloud_raymarch.glsl scripts/lab/CloudVolume.cs
git commit -m "Reduced-res + temporal amortization for cloud raymarch (affordable)"
```

---

## Task 5: Matched cloud-shadow map → terrain sun attenuation

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` OR add `shaders/cloud_shadow.glsl` (bake a top-down sun-transmittance map from the same density field)
- Modify: `scripts/lab/CloudVolume.cs` (produce + expose the shadow map; also amortized)
- Modify: `shaders/terrain_lab.gdshader` (re-add the inert-when-off `light()`, fed by the shadow map sampled in world XZ)
- Modify: `scripts/lab/TerrainLab.cs` (bind the shadow map + cloud uniforms; re-add the cloud uniform block)

**Approach:** A cheap low-res 2D map over the terrain footprint: for each texel, march the density field from cloud-base toward the sun, accumulate Beer transmittance → store sun visibility in R. Sample it in `light()` (world XZ → map UV) to scale the direct sun term — reusing the exact sun-only attenuation approach (ambient/GI untouched) from reverted `d3baae6`. Because it samples the SAME density field, the ground shadow matches the cloud overhead.

- [ ] **Step 1:** Implement the shadow bake (top-down sun-march). Low res (e.g. 256–512²), amortized at the same cadence.
- [ ] **Step 2:** Re-add to `terrain_lab.gdshader`: the cloud uniforms (`cloud_enabled`, `cloud_strength`, shadow-map sampler, world-XZ mapping) + the inert-when-off `light()` (Lambert + GGX-ish spec, `ATTENUATION*cloud`), exactly matching the approved lighting body; `cloud_enabled==false` ⇒ reproduces default.
- [ ] **Step 3:** `TerrainLab.cs` binds the shadow map + uniforms; `CloudVolume` exposes it.
- [ ] **Step 4: Build + import + auto-shot (A/B on/off).** Expected: with clouds on, ground shadows present and aligned under sky clouds; off == today's look.
- [ ] **Step 5: USER GATE — fly it.** Judge: are ground shadows SPARSE, do they MATCH the clouds overhead, drift coherently with them? Toggle off → exact prior look. **The core requirement.** Tune `cloud_strength`, coverage.
- [ ] **Step 6: Commit.**
```bash
git add shaders/cloud_raymarch.glsl shaders/cloud_shadow.glsl scripts/lab/CloudVolume.cs shaders/terrain_lab.gdshader scripts/lab/TerrainLab.cs
git commit -m "Matched cloud-shadow map -> terrain sun attenuation (clouds cast their own shadows)"
```

---

## Task 6: Tune, document, retire scaffolding

- [ ] **Step 1: Final live tuning pass** with the user across the Clouds tab (coverage low/sparse, density, type, lighting, drift, perf). Save a good default into `data/cloud_params.json` + the registry defaults.
- [ ] **Step 2: DECISIONS.md entry** (newest first): volumetric clouds built fresh, technique + why, perf knobs, matched-shadow approach.
- [ ] **Step 3: Refresh HANDOFF §6 Current State** + TECH_STACK inventory (new Cloud unit: noise bake, raymarch, sky, shadow map).
- [ ] **Step 4 (optional, on user ask):** snapshot cloud settings into the mood presets so each mood gets a matching skyscape.
- [ ] **Step 5: Commit docs.**
```bash
git add docs/ data/cloud_params.json data/lab_controls.json
git commit -m "Volumetric clouds: final tuning defaults + docs (DECISIONS/HANDOFF/TECH_STACK)"
```

---

## Self-Review notes

- **Spec coverage:** noise bake (T1), weather+params+tab (T2), raymarch+sky (T3), affordability/temporal (T4), matched shadow→light() (T5), tune+docs (T6). All spec sections + build-order stages mapped.
- **Placeholder honesty:** T1/T3 say full GLSL is written at implementation time — this is genuine (raymarch + 3D-noise GLSL is ~150 lines each, written against the quoted Nubis constants); the *structure, constants, and formulas* are specified (128/96 steps, FBM weights `.625/.25/.125`, 6 light-cone samples, HG g≈0.6, Beer/powder), not deferred as vague. Not a placeholder — a known, bounded write.
- **Type consistency:** `CloudNoiseCompute.Bake()→(shape,detail)`; `CloudVolume` drives per-frame on MAIN RD + exposes `Texture2Drd`; `CloudParams` field names = sky-shader uniforms = `lab_controls.json` `param`s. `light()` re-add matches reverted `d3baae6` body.
- **Key risk flagged (not hidden):** per-frame MAIN-RD `Texture2Drd` wiring (T3) is the one novel infra piece; SubViewport fragment-raymarch fallback documented. Perf is a co-equal gate (T3.7, T4.4). Look approved BEFORE optimizing (T3 before T4).
- **Policy:** noise GPU-baked in-repo (re-derivable), not shipped .tga — matches the "all constants/data re-derivable" rule. Referenced clayjohn/Nubis for technique; own implementation.
