# WG17 Slice D (Godrays) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port WG16's screen-space god rays into WG17 — radial light-scatter from the sun's screen position, occluded by cloud luminance — carrying intact the fixes that killed the wash/ring artifacts. The smallest sky-stack slice; rides on Clouds.

**Architecture:** `GodRaysScreen` is a clip-space fullscreen quad (`render_priority` 127, NOT a CompositorEffect). It reads the sun's screen UV (sun world dir unprojected via the camera) + the cloud luminance as the occlusion source, does a radial blur scatter with a tangential high-pass (the wash/ring fix), and additively blends over the frame.

**Tech Stack:** Godot 4.6 / C#, GDShader screen-space quad.

**Reference:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceD-godrays-design.md`
- WG16 source to port: `scripts/lab/GodRaysScreen.cs`, `shaders/godray_screen.gdshader`
- The wash/ring fix history: `docs/archive/handoffs/2026-06-19-godray-wash-and-ring.md` (carry the tangential high-pass).

## Global Constraints

- **Target repo:** `C:\Wg16\WG17\terrainengine-10k`. **Namespace:** `Te10k.Godrays`.
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild after every .cs edit; absolute `--path`; bare `--` for flags.**
- **Screen-space quad with `render_priority = 127` (after aerial=120) — NOT a CompositorEffect** (bug #118292 / the proven non-racing path).
- **Occlusion = CLOUD LUMINANCE, not scene albedo/emission** (the root-cause confusion in WG16). The sun must be OCCLUDED by clouds for shafts.
- **Carry the tangential high-pass** — it killed the full-screen wash + the bright ring around the sun. Load-bearing; port intact, do NOT regress.
- **Do NOT bring back** the deleted cloud-shadow-map godray mode (removed in WG16's shadow audit). Screen-luminance occlusion only.
- **View-dependence is CORRECT here:** godrays are a screen-space camera effect — unlike Lighting/Atmosphere/Clouds-lighting, they legitimately change with camera. The HUD must NOT claim view-lock for this layer.
- **Depends on:** Slice C (clouds, for the occlusion silhouette) + Slice A (sun direction). Without clouds, godrays is a no-op (nothing occludes the sun) — build last.

---

## File Structure

```
src/godrays/GodRaysScreen.cs        # Tasks 1-2 — screen quad: sun-UV unproject, occlusion, radial scatter, blend
src/godrays/shaders/godray_screen.gdshader   # Task 1 — radial scatter + occlusion + tangential high-pass
```

---

### Task 1: Port GodRaysScreen + shader

**Files:** Create `src/godrays/GodRaysScreen.cs`, `src/godrays/shaders/godray_screen.gdshader`

**Interfaces:**
- Consumes: sun world direction (Slice A `ILuminaryFeed` or the scene sun); cloud luminance (Slice C dome / a luminance read); the active camera (for unproject).
- Produces: `class GodRaysScreen : Node3D` — a clip-space fullscreen quad at `render_priority` 127; `void SetEnabled(bool)`, `void SetSunWorldDir(Vector3)`, `void SetStrength(float)`, `void SetCloudOcclusion(Texture)` (or reads the cloud dome). Additive blend.

- [ ] **Step 1: Copy + re-namespace the shader and script**

Copy `shaders/godray_screen.gdshader` → `src/godrays/shaders/`; `scripts/lab/GodRaysScreen.cs` → `src/godrays/`; namespace `Te10k.Godrays`; fix `res://` paths. **Carry the tangential high-pass** on the occlusion buffer verbatim (it's the wash/ring fix). Keep the radial-blur scatter (GPU Gems 3) and additive composite. Confirm occlusion is sampled from cloud luminance, not albedo/emission.

- [ ] **Step 2: Set render_priority + remove any harness coupling**

Ensure the quad material's `render_priority = 127`. Strip any `ILabControls`/UI knob coupling — expose plain setters. Remove any dormant cloud-shadow-map input path (screen-luminance only).

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(godrays): port screen-space radial scatter (tangential high-pass intact, priority 127)"
```

---

### Task 2: Wire sun + cloud occlusion + EYE-GATE

**Files:** Modify `src/godrays/GodRaysScreen.cs`; modify the driver/scene

**Interfaces:** Per frame: sun world dir → `SetSunWorldDir` (from `ILuminaryFeed`/scene sun); cloud occlusion source bound from Slice C; quad added to the scene.

- [ ] **Step 1: Wire sun screen-UV + cloud occlusion**

In `GodRaysScreen`, unproject the sun world direction to screen UV via the active camera each frame; when the sun is off-screen / behind, fade strength to 0. Bind the cloud luminance (Slice C dome) as the occlusion source. Verify the real cloud/luminary handles in the merged slices; stub + TODO if absent.

- [ ] **Step 2: EYE-GATE — shafts only when occluded, no wash/ring**

Launch with clouds on, sun partway behind a cloud:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected:
- Point camera near the occluded sun → **crisp radial shafts** from the sun's screen position.
- Clear the clouds / unocclude the sun → **shafts vanish** (no wash, **no bright ring**).
- Turn so the sun goes off-screen / behind → shafts **fade smoothly**, no pop.
- Shafts track the sun's SCREEN position as the camera moves (view-dependent BY DESIGN — correct for this layer).

- [ ] **Step 3: Profile + slice marker + migration note**

Record frame ms (godrays on/off). Commit marker; append "WG17 Slice D outcome — sky stack A–D complete" to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md` in WG16.
```bash
git commit --allow-empty -m "milestone(godrays): Slice D eye-gate PASS — shafts when occluded, no wash/ring (<X>ms) — sky stack complete"
```

---

## Self-Review

**Spec coverage:** §3 GodRaysScreen quad priority 127 / not CompositorEffect → Tasks 1–2; §4 sun-UV + cloud occlusion → Task 2; §5 carry tangential high-pass, occlusion=clouds-not-albedo, no shadow-map mode → Task 1; §6 eye-gate (shafts-when-occluded, no wash/ring, off-screen fade, view-dependent-by-design) → Task 2; §8 DoD → Task 2. ✓

**Placeholder scan:** cloud/luminary handle reconciliation is explicit (verify in merged slices / stub+TODO). `<X>ms` measured; `.../` abbreviates the Godot path in Global Constraints. No vague items. ✓

**Type consistency:** `SetSunWorldDir`/`SetEnabled`/`SetStrength`/`SetCloudOcclusion`, `render_priority 127` consistent. Occlusion source = cloud luminance throughout. ✓

**Note:** pure port carrying existing fixes — no numeric check (godrays is inherently visual); the eye-gate is the gate. The one subtlety encoded everywhere: godrays ARE view-dependent and that's correct, so this slice's HUD/gate explicitly does NOT assert view-lock (unlike B/C).
