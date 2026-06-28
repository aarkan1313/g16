# WG17 Slice B (Atmosphere) — Plan 2 of 2: Aerial Perspective (AT-2) + Integration + Gates

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build aerial perspective FRESH and CORRECTLY — the part that was broken in WG16 (a full-screen blue band). A 64×64×32 froxel LUT, applied to scene geometry gated by depth, with the sky/background EXCLUDED. Plus integration (ILuminaryFeed wiring) and the gates that prove the blue-band bug is gone.

**Architecture:** `AtmosphereCompute` adds a 3D aerial-perspective froxel volume (inscatter + transmittance along the camera frustum). `AerialPerspective` is a screen-space quad (render_priority 120, NOT a CompositorEffect) that reads scene depth, selects the froxel Z-slice, blends `geometry*transmittance + inscatter` — and EARLY-OUTS on background pixels so the sky is never tinted.

**Tech Stack:** Godot 4.6 / C#, GLSL compute (3D froxel), GDShader screen-space quad reading DEPTH_TEXTURE.

**Plan set:** Plan 1 = LUT core/sky (prerequisite). **Plan 2 (this) = aerial + integration + gates.**

**Reference:** Spec `…specs/2026-06-28-wg17-sliceB-atmosphere-design.md` (§5 the AT-2 rule, §6 AerialDepthCheck). Hillaire aerial LUT = 64×64×32 froxel over a fixed near→far range.

## Global Constraints

- **Target repo:** `C:\Wg16\WG17\terrainengine-10k`. **Namespace:** `Te10k.Atmosphere` (+ `.Checks`).
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild after every .cs edit; absolute `--path`; bare `--` for flags; compute windowed.**
- **GPU pattern:** aerial froxel `Texture3Drd` (or Texture2Drd-array) assigned ONCE; updated on sun/camera change; RID never reassigned per frame (bug #118292). RenderingDevice only in `AtmosphereCompute`.
- **THE AT-2 CORRECTNESS RULE (the whole point of this slice):** aerial perspective applies to SCENE GEOMETRY ONLY, gated by scene depth. Background/sky pixels (max depth / no geometry) are EXCLUDED — they keep the sky-view color and are NOT run through the froxel. A missing/broken depth+sky gate = the WG16 full-screen blue band. The screen quad MUST sample DEPTH_TEXTURE and early-out on background.
- **No CompositorEffect** — screen-space quad with `render_priority`, the non-racing path.
- **Depends on Plan 1** (transmittance + multiscatter + sky-view LUTs exist).

---

## File Structure (this plan)

```
src/atmosphere/shaders/atmosphere_aerial.glsl    # Task 1 — 64x64x32 froxel compute (inscatter+transmittance)
src/atmosphere/AtmosphereCompute.cs              # Task 1 (modify) — dispatch the aerial froxel
src/atmosphere/AerialPerspective.cs              # Task 2 — screen-space quad, depth-gated, sky-excluded
src/atmosphere/shaders/aerial_screen.gdshader    # Task 2 — reads DEPTH_TEXTURE, samples froxel, early-out on bg
src/atmosphere/checks/AerialDepthCheck.cs        # Task 3 — guards the blue-band regression
src/app/LightingDriver.cs (or AtmosphereDriver)  # Task 4 — wire ILuminaryFeed → AtmosphereCompute.SetSun
```

---

### Task 1: Aerial-perspective froxel LUT (64×64×32)

**Files:** Create `src/atmosphere/shaders/atmosphere_aerial.glsl`; Modify `AtmosphereCompute.cs`

**Interfaces:**
- Consumes: transmittance + multiscatter + sun (Plan 1).
- Produces: a 64×64×32 froxel volume `Texture3Drd AerialTex { get; }` (rgb inscatter + a transmittance — e.g. RGBA16F with A=mean transmittance), recomputed on sun/camera change. The froxel covers screen XY × a depth range `AerialMaxDistanceM` (default 32000 m), linear or log-Z slices. `void SetAerialCamera(Transform3D camXform, float fovY, float aspect)` feeds the frustum.

- [ ] **Step 1: Write the aerial froxel compute shader**

`atmosphere_aerial.glsl`: per froxel (x,y in [0,1] screen, z = slice → world distance along that screen ray within `[0, AerialMaxDistanceM]`), march from the camera to the froxel center accumulating in-scattering (Rayleigh+Mie phase × transmittance × sun) + multiscatter; store rgb inscatter + the accumulated transmittance. Near slices ≈ zero inscatter, far slices ≈ full haze. ~ z-slice-incremental marching (each slice extends the previous).

- [ ] **Step 2: Dispatch the froxel in AtmosphereCompute (on sun/camera change)**

Create the 64×64×32 RGBA16F 3D texture; wrap in `Texture3Drd`, RID assigned ONCE. Re-dispatch when `_skyDirty` OR the camera moved/rotated (a `_aerialDirty` flag set by `SetAerialCamera`). Document assign-once.

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(atmosphere): AT-2 aerial froxel LUT 64x64x32 (inscatter+transmittance, RID once)"
```

---

### Task 2: AerialPerspective screen quad — depth-gated, sky-excluded

**Files:** Create `src/atmosphere/AerialPerspective.cs`, `src/atmosphere/shaders/aerial_screen.gdshader`

**Interfaces:**
- Consumes: `AerialTex` (Task 1).
- Produces: `class AerialPerspective : Node3D` (a fullscreen quad MeshInstance3D, unshaded, `render_priority = 120`) bound to `aerial_screen.gdshader`; `void SetEnabled(bool)`, `void SetAerialTexture(Texture3Drd)`, `void SetMaxDistance(float m)`.

- [ ] **Step 1: Write aerial_screen.gdshader — THE depth gate**

`shader_type spatial; render_mode unshaded, depth_test_disabled, blend_mix;`. In fragment: read `DEPTH_TEXTURE` at SCREEN_UV, reconstruct linear view distance. **EARLY-OUT: if depth is at the far plane / no geometry (background), `discard` (or ALPHA=0) — do NOT touch sky pixels.** Else map distance → froxel z-slice, sample `AerialTex` (inscatter rgb + transmittance a), and output so the composite yields `scene*transmittance + inscatter` (use `blend_mix` with ALPHA=1-transmittance and emission=inscatter, or premultiplied). The sky is left exactly as the sky-view shader drew it.

- [ ] **Step 2: Write AerialPerspective.cs**

Fullscreen clip-space quad (a `QuadMesh` with a material at `render_priority` 120 so it draws after opaque, before godrays at 127). Bind `AerialTex` once. `SetEnabled` toggles `Visible`.

- [ ] **Step 3: Build + EYE-GATE — the blue-band test**

Add a near box + distant terrain to the scene. Build + launch:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected: **distant terrain hazes into the sky; the NEAR box is clear; the SKY shows NO flat blue band.** Point the camera at open sky → pure sky-view gradient, no tint overlay. If the whole frame is tinted → the depth gate is wrong, STOP (that's the WG16 bug).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(atmosphere): AT-2 aerial screen quad — depth-gated, sky-excluded (no blue band)"
```

---

### Task 3: AerialDepthCheck — guard the blue-band regression

**Files:** Create `src/atmosphere/checks/AerialDepthCheck.cs`

**Interfaces:**
- Produces: `static bool Run(...)` — (a) render a sky-only frame (no geometry), read back the framebuffer with aerial OFF then ON, assert background pixels changed by ≈0 (sky untinted); (b) render near+far geometry, assert far pixels shifted toward haze MORE than near pixels (depth-graded). Prints `AERIAL-DEPTH PASS sky-untinted=YES depth-graded=YES`.

- [ ] **Step 1: Write AerialDepthCheck.cs**

Set up an offscreen viewport (or use the main one) with a known camera. Frame 1: open sky only. Capture aerial-off vs aerial-on; compare a patch of background pixels — mean delta must be below a small epsilon (sky excluded). Frame 2: a near quad + a far quad. Capture aerial-on; assert `hazeDelta(far) > hazeDelta(near)` by a margin. Print PASS/FAIL with both booleans.

- [ ] **Step 2: Wire `--aerialcheck` + run**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --aerialcheck
```
Expected: `AERIAL-DEPTH PASS sky-untinted=YES depth-graded=YES`, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(atmosphere): AerialDepthCheck guards the AT-2 blue-band bug (PASS)"
```

---

### Task 4: Integration — ILuminaryFeed wiring + day-cycle eye-gate

**Files:** Modify `src/app/LightingDriver.cs` (or a small `AtmosphereDriver` if Slice A not merged); Modify scene.

**Interfaces:**
- Consumes: Slice A `ILuminaryFeed` (sun); `AtmosphereCompute`, `AerialPerspective`.
- Produces: per-frame: `AtmosphereCompute.SetSun(...)` fed from the luminary feed; `SetAerialCamera(...)` fed from the active camera; `AerialPerspective` enabled.

- [ ] **Step 1: Wire the feed**

If Slice A is merged in WG17: make `AtmosphereCompute` subscribe to the composer's `ILuminaryFeed` (verify the real signature in `src/lighting/ILightingTarget.cs`), so each `Compose()` pushes the sun and sets the dirty flag. If NOT merged: drive `SetSun` from the same day-clock the lighting driver would, with a TODO to switch to the feed. Per frame, push the camera transform to `SetAerialCamera`.

- [ ] **Step 2: Full day-cycle EYE-GATE**

```bash
"C:/Wg16/WG17/terrainengine-10k" ... -- --autotime
```
Expected: across dawn→noon→dusk the sky recolors physically; distant terrain hazes correctly; NO blue band at any time; foreground clear. Yaw/pitch at fixed time → sky unchanged (view-locked). HUD `atmosphere: on rid-stable=YES`.

- [ ] **Step 3: Profile + commit the slice marker**

Record frame ms (atmosphere on vs off; aerial on vs off). Commit:
```bash
git commit --allow-empty -m "milestone(atmosphere): Slice B eye-gate PASS — physical sky, depth-gated aerial, NO blue band (<X>ms)"
```

- [ ] **Step 4: Update the migration record (WG16 repo)**

Append a "WG17 Slice B (Atmosphere) outcome" note (rewrite done, AT-2 blue-band fixed + how, check + eye-gate results, frame ms) to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md`; commit in WG16.

---

## Self-Review

**Spec coverage (Plan 2 = AT-2 + integration):** §5 the AT-2 depth-gate/sky-exclude rule → Tasks 1–2; §6 AerialDepthCheck + the blue-band eye-gate → Tasks 2–3; §4 ILuminaryFeed wiring → Task 4; §3 render_priority 120 / no CompositorEffect → Task 2; §8 DoD (no band, depth-graded, view-locked, profile, migration note) → Tasks 2–4. ✓

**Placeholder scan:** the Slice-A-merge branch in Task 4 is explicit (verify the real interface file), not vague. `<X>ms` is a measured value. `.../` abbreviates the full Godot path in Global Constraints. ✓

**Type consistency:** `AerialTex` (Texture3Drd), `SetAerialCamera`, `SetMaxDistance`, `render_priority 120` consistent across tasks; `AtmosphereCompute.SetSun` matches Plan 1. The `AerialDepthCheck` PASS string matches spec §6. ✓

**The point:** Tasks 2–3 exist specifically to make the WG16 blue-band bug impossible to ship — the shader early-outs on background, and a numeric check + eye-gate both assert sky-untinted + depth-graded. That is the heart of this slice.
