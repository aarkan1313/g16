# World-Space Cloud Layer Rebuild — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. NOTE: this plan's gate is the USER's live eye in motion (no automated test asserts "looks right") — mechanical checks (builds, runs windowed, no errors, shadow UV math) are the automatable part; flag the visual gate for the user.

**Goal:** Re-anchor the volumetric clouds from a direction-only dome at the world origin to a real **world-space cloud slab** sampled at world XZ, so the visible clouds and the ground shadow map share one coordinate frame by construction — fixing both the shadow mismatch and the camera-motion jitter — then fix the god-ray fog and retune the presets against the corrected system.

**Architecture:** The raymarch already samples the density field in world space (`lp = p - baseR`, world meters). The bug is purely the **ray origin + sky lookup**: the sky raymarch shoots from a fixed `(0, PLANET_R+1, 0)` and the sky shader paints by `EYEDIR` only, so the cloud image is origin-anchored while the shadow map is world-XZ-anchored. Fix = make the cloud raymarch shoot from the **camera world position** through each view direction, intersect a **flat world-space cloud slab** (planes at `altitude` and `altitude+thickness`, not a planet-radius sphere), and march the same density field — so a cloud at world XZ casts its shadow at that same world XZ (offset by sun angle, like a real shadow). The terrain `light()` already samples the shadow map at world XZ (`terrain_lab.gdshader:541`), so the ground side is already correct; only the cloud render path changes. Jitter is killed because cloud content becomes camera-relative (no origin-vs-camera parallax swim).

**Tech Stack:** Godot 4.6 mono, GLSL compute on the main RenderingDevice via `RenderingServer.CallOnRenderThread` (the proven `CloudVolume` pattern), `Texture2Drd` outputs sampled by the sky shader + terrain material.

---

## File Structure

- `shaders/cloud_raymarch.glsl` — MODIFY: ray origin = camera world pos (new uniform); intersect a flat slab via plane math instead of `ray_sphere`; the lat-long output stays (sky shader still samples by direction) but now represents clouds *from the camera*, not from origin.
- `shaders/cloud_sky.gdshader` — MODIFY: pass camera world position so the dome lookup is consistent; (lat-long-by-direction sampling stays — the fix is that the texture is now camera-anchored). Minimal change; main work is the raymarch + C# pushing `cam_world`.
- `scripts/lab/CloudVolume.cs` — MODIFY: push camera world position into the raymarch param buffer each frame (already has `SetCameraWorld`-style plumbing pattern from the terrain); confirm `RegionM` drives the slab XZ extent the shadow map uses; god-ray hard-off path.
- `shaders/cloud_shadow.glsl` — VERIFY ONLY: already world-XZ + flat-ish (`ray_sphere` at planet radius ≈ locally flat over a region); confirm the slab altitude/thickness match the raymarch so cloud bottoms align. Adjust if the raymarch slab math diverges.
- `shaders/cloud_godray_fog.gdshader` — MODIFY: fix the black-wedge bug (fog density gating produces giant dark slabs); clamp/correct so OFF = truly inert and ON = subtle shafts.
- `data/cloud_presets.json` — MODIFY: retune the 5 presets against the corrected system.
- `scripts/lab/CloudParams.cs` — possibly MODIFY defaults if the corrected system wants different baseline drift/altitude.

## Camera-world plumbing note (verified seam)

`CloudVolume.Attach(env, cam, regionSizeM)` already receives the `Camera3D`. `TerrainLab`/`TerrainLabUI` already pushes `cam_world` to the terrain shader (`terrain_lab.gdshader:42`). The cloud raymarch needs the same value each frame in its param buffer. The slab XZ frame = the same `RegionM`-centered-at-origin frame the shadow map and terrain `light()` use (`wp.xz / region + 0.5`).

---

### Task 1: Add camera world position to the raymarch param buffer

**Files:**
- Modify: `scripts/lab/CloudVolume.cs` (`BuildParams`, `ParamFloats`, a `SetCameraWorld` setter)
- Modify: `shaders/cloud_raymarch.glsl` (params struct)

- [ ] **Step 1: Add a camera-world field to CloudVolume**

In `CloudVolume.cs`, add near the sun fields:
```csharp
private Vector3 _camWorld = Vector3.Zero;
public void SetCameraWorld(Vector3 p) { _camWorld = p; }
```

- [ ] **Step 2: Push it into BuildParams**

`ParamFloats` is currently 48. Add a `vec4 cam_world` (4 floats) → bump to 52. In `BuildParams`, after the `sky_horizon` block (keep std430 16-byte alignment — append as a full vec4):
```csharp
F(_camWorld.X); F(_camWorld.Y); F(_camWorld.Z); F(0f);   // cam_world (world-space ray origin)
```
Place it at a 16-byte boundary; simplest is right after the four color vec4s (sun_dir, sun_color, sky_top, sky_horizon) and before the scalar run. Adjust the GLSL struct order to match EXACTLY.

- [ ] **Step 3: Add the field to the GLSL params struct**

In `cloud_raymarch.glsl` `ParamsBuf`, after `vec4 sky_horizon;`:
```glsl
    vec4 cam_world;      // xyz world-space ray origin (the camera), w unused
```
Keep every other field in the same order; update `_pad0/_pad1` count so the struct still lands on the C# `ParamFloats` total (52 → struct is 4 vec4 + cam_world vec4 + the scalar tail; recount pads so `ParamFloats*4 == sizeof(struct)`).

- [ ] **Step 4: Push cam world each frame from the UI/presenter**

Find where the terrain `cam_world` is pushed (grep `cam_world` in `scripts/lab/`). In that same per-frame spot, add:
```csharp
_cloudVolume?.SetCameraWorld(camWorldPos);
```

- [ ] **Step 5: Build**

Run: `dotnet build WG16.csproj`
Expected: build succeeds (exit 0).

- [ ] **Step 6: Commit**

```bash
git add scripts/lab/CloudVolume.cs shaders/cloud_raymarch.glsl
git commit -m "Clouds: pipe camera world pos into the raymarch param buffer"
```

---

### Task 2: Raymarch from the camera through a flat world-space slab

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` (`main`, slab intersection)

- [ ] **Step 1: Replace the sphere-shell entry with a flat-slab intersection**

In `main()`, replace:
```glsl
    vec3 rd = dir_from_texel(px);
    vec3 ro = vec3(0.0, PLANET_R + 1.0, 0.0);
    float baseR = PLANET_R + P.altitude;
    float topR = baseR + P.thickness;
    vec2 hitB = ray_sphere(ro, rd, baseR);
    vec2 hitT = ray_sphere(ro, rd, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd = max(hitT.y, 0.0);
```
with a flat-slab intersection anchored at the camera:
```glsl
    vec3 rd = dir_from_texel(px);
    vec3 ro = P.cam_world.xyz;                 // ray starts at the camera (world space)
    float cloudBase = P.altitude;              // world Y of the cloud bottom
    float cloudTop  = P.altitude + P.thickness;
    // intersect the upward view ray with the two horizontal planes y=cloudBase, y=cloudTop
    float tBase = (cloudBase - ro.y) / max(rd.y, 1e-4);
    float tTop  = (cloudTop  - ro.y) / max(rd.y, 1e-4);
    float tStart = max(min(tBase, tTop), 0.0);
    float tEnd   = max(tBase, tTop);
```
This makes the density samples (`p = ro + rd*t`) land at **true world XZ** under the camera — the same XZ the shadow map projects onto.

- [ ] **Step 2: Make the density sampler use world-space p directly**

`sample_density` currently does `lp = vec3(p.x, p.y - baseR, p.z)` where `baseR` was planet-radius. With the flat slab, height fraction must be relative to the cloud base in world Y. Change `height_fraction` usage and `lp`:
```glsl
// in main(), pass cloudBase/cloudTop (not baseR/topR) to sample_density:
float dens = sample_density(p, cloudBase, cloudTop, windOff);
```
and in `sample_density`, change the signature params to `float base_y, float top_y` and:
```glsl
float h = clamp((p.y - base_y) / max(top_y - base_y, 1.0), 0.0, 1.0);
vec3 lp = vec3(p.x, p.y - base_y, p.z);   // world XZ preserved → matches shadow map
```
Apply the SAME change to `light_march` (its `baseR/topR` args become `base_y/top_y`).

- [ ] **Step 3: Keep the rd.y guard, drop planet refs**

`if (rd.y > 0.02 && tEnd > tStart)` stays (only march upward rays). Remove now-unused `PLANET_R`, `ray_sphere`, `height_fraction(p,baseR,topR)` (replace with the inline world-Y version). Leave `dir_from_texel` and the lat-long output as-is — the sky shader still samples by direction, but the texture now represents the view *from the camera*.

- [ ] **Step 4: Mirror the same slab math in cloud_shadow.glsl**

In `cloud_shadow.glsl`, the sun-march must hit the SAME flat slab so cloud bottoms align with the raymarch. Replace its `ray_sphere(ro, L, baseR/topR)` with the same plane intersection (`ro` = the ground point at `ground_height`, march along `L` toward the sun):
```glsl
float cloudBase = P.altitude;
float cloudTop  = P.altitude + P.thickness;
float tBase = (cloudBase - ro.y) / max(L.y, 1e-4);
float tTop  = (cloudTop  - ro.y) / max(L.y, 1e-4);
float tStart = max(min(tBase, tTop), 0.0);
float tEnd   = max(tBase, tTop);
```
and the same `sample_density` world-Y change (signature `base_y, top_y`, `h` and `lp` from world Y). The shared density math now samples the SAME world XZ from both shaders → shadows match cloud shape AND location.

- [ ] **Step 5: Build + run windowed, watch for smooth motion + matched shadow**

Run: `dotnet build WG16.csproj` then launch `scenes/terrain_lab.tscn` windowed (`--rendering-driver vulkan`).
Expected (MECHANICAL): builds, scene runs, no shader compile errors in Output, "compute initialized" prints.
Expected (VISUAL GATE — user): clouds drift smoothly (no swim/jitter as the camera moves), and a cloud overhead casts its shadow on the ground roughly beneath it (offset by sun angle). **Flag for the user to fly it — judge in motion, never a still** (mipmap-fuzz lesson).

- [ ] **Step 6: Commit**

```bash
git add shaders/cloud_raymarch.glsl shaders/cloud_shadow.glsl
git commit -m "Clouds: world-space slab raymarch from camera; shadow shares world XZ (fixes mismatch + jitter)"
```

---

### Task 3: Fix the god-ray fog (the black wedge blades)

**Files:**
- Modify: `shaders/cloud_godray_fog.gdshader`
- Verify: `scripts/lab/CloudVolume.cs` (`SetGodraysEnabled`, `BuildGodrayVolume`)

- [ ] **Step 1: Read the god-ray fog shader and find why density goes huge/negative**

Read `shaders/cloud_godray_fog.gdshader`. The black wedges = the FogVolume's `density` going strongly positive in shadowed regions (or unbounded), painting opaque dark slabs aligned to the box faces. Identify the density expression and the `godray_on` gate.

- [ ] **Step 2: Make OFF truly inert and ON bounded**

Ensure: when `godray_on` is false, `density = 0.0` unconditionally (early-out). When true, fog density must be a small, clamped value gated by cloud-shadow transmittance — light shafts in the LIT gaps, not dark slabs in shadow. Pattern:
```glsl
// in fog(): 
if (!godray_on) { DENSITY = 0.0; }
else {
    float sun_vis = texture(cloud_shadow_tex, world_to_shadow_uv(WORLD_POSITION)).r; // 1 lit, 0 shadowed
    DENSITY = clamp(base_density * sun_vis, 0.0, max_density);  // shafts where sun reaches, none in shadow
}
```
`base_density` small (e.g. 0.02), `max_density` clamped (e.g. 0.05). The black blades came from un-clamped / inverted density; this bounds it and ties it to sun visibility.

- [ ] **Step 3: Build + run, toggle god rays ON**

Run: build, launch windowed, enable "god rays (live-tune)" in the Clouds tab.
Expected (MECHANICAL): no black wedge slabs; OFF = no change vs no-fog.
Expected (VISUAL GATE — user): subtle light shafts through cloud gaps, scene NOT darkened. Tunable. Flag for user tuning of density/`LightVolumetricFogEnergy`.

- [ ] **Step 4: Commit**

```bash
git add shaders/cloud_godray_fog.gdshader scripts/lab/CloudVolume.cs
git commit -m "God rays: clamp fog density + gate by sun visibility; OFF truly inert (kills black-wedge bug)"
```

---

### Task 4: Retune the cloud presets against the corrected system

**Files:**
- Modify: `data/cloud_presets.json`
- Reference: `data/cloud_params.json`, `scripts/lab/CloudParams.cs` (field names)

- [ ] **Step 1: Read the current presets**

Read `data/cloud_presets.json`. Note the 5 named looks (Clear/Scattered/Broken/Overcast/Stormy) and which knobs each sets.

- [ ] **Step 2: Retune for sensible, distinct looks**

With the world-space slab, coverage/density now read truer. Targets (user feedback: "Stormy is just a ton of clouds", "presets don't make sense"):
- **Clear:** coverage ~0.15, density ~0.8, type ~0.5, sparse high cumulus, bright.
- **Scattered:** coverage ~0.35, distinct puffs, gaps dominant.
- **Broken:** coverage ~0.6, more cover than gaps but still broken.
- **Overcast:** coverage ~0.9, flat (type ~0.2), low brightness, even grey — NOT towering.
- **Stormy:** coverage ~0.8 BUT dark (low brightness ~0.5, high density/opacity), towering type, lower altitude, faster drift — read as menacing weather, not "more clouds." Pair note: Stormy expects a dimmer sun/mood (document that it looks right under a storm mood, per the user's "maybe that's just tuning with different sun").
Keep drift speeds sane (the live Stormy showed 46 m/s + dir 252 — too fast; cap ~8–20 m/s so motion stays smooth).

- [ ] **Step 3: Build + run, cycle the preset dropdown**

Run: launch windowed, step through all 5 presets in the Clouds tab.
Expected (VISUAL GATE — user): each preset reads as its name and they're clearly distinct. Flag for the user.

- [ ] **Step 4: Commit**

```bash
git add data/cloud_presets.json
git commit -m "Clouds: retune the 5 presets against the world-space system (Stormy = dark+menacing not just more cloud)"
```

---

### Task 5: Docs + memory + re-review handoff

- [ ] **Step 1: Update DECISIONS.md (newest-first) with the cloud re-architecture**

Entry: world-space cloud layer replaces the origin-anchored infinity dome; root cause (dome-vs-world-XZ projection split) → both jitter + shadow-mismatch; god-ray clamp; preset retune. Reference this plan + the `cloud-shadow-dome-mismatch` memory.

- [ ] **Step 2: Update HANDOFF §6 review backlog + ROADMAP cloud follow-ups**

Mark the cloud mismatch/jitter/god-ray/preset items as fixed-pending-review; keep them in the review backlog until the user flies it.

- [ ] **Step 3: Update the memory note**

In `cloud-shadow-dome-mismatch.md`, note the chosen fix (world-space slab) was implemented; keep the diagnosis as the "why."

- [ ] **Step 4: Build, launch, present for re-review**

Launch `scenes/terrain_lab.tscn` windowed; hand the user the checklist: smooth cloud motion, matched shadow (cloud overhead ↔ shadow beneath), god rays ON = subtle shafts not black slabs, presets distinct + sensible.

- [ ] **Step 5: Commit**

```bash
git add docs/ ; git commit -m "Docs: record world-space cloud rebuild + re-review handoff"
```

---

## Self-Review

- **Spec coverage:** world-space layer (T1+T2) fixes mismatch + jitter (the shared root cause); god rays (T3); presets (T4); docs (T5). All "everything this pass" items covered.
- **Type consistency:** `ParamFloats` 48→52 with a `cam_world` vec4 must match the GLSL `ParamsBuf` struct field-for-field (the std430 gotcha — recount `_pad` so sizes equal). `sample_density`/`light_march` signature change (`baseR,topR`→`base_y,top_y`) applied in BOTH raymarch and shadow shaders identically (they MUST stay identical or shadows diverge again — that identity is the whole fix).
- **Risk:** additive-ish; `git checkout .` reverts; clouds toggle off → original sky. The std430 struct mismatch is the likeliest break — verify the byte count. Shadow map UV in terrain `light()` already world-XZ (`terrain_lab.gdshader:541`) → unchanged, confirms the target frame.
- **Gate:** the real gate is the user's eye in motion; mechanical steps only prove it builds/runs/aligns mathematically.
