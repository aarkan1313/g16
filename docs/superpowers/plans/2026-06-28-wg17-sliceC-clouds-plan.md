# WG17 Slice C (Clouds) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port WG16's volumetric clouds into WG17 — faithful to the proven raymarch/noise architecture — while FIXING the three flagged look-bugs (double-alpha composite, sun-extinction crushing direct light, dead noise octave), and wiring clouds to the Lighting + Atmosphere feeds.

**Architecture:** `CloudVolume` owns RD + render-thread dispatch, raymarches a Perlin-Worley density field into a lat-long dome `Texture2Drd` (assigned once), composited on the sky. Sun/moon from Lighting's `ILuminaryFeed`; cloud ambient from Atmosphere's `IAtmosphereFeed`; coverage pushed back to the composer as a single `Overcast` scalar.

**Tech Stack:** Godot 4.6 / C#, GLSL compute (3D noise + raymarch), GDShader sky composite.

**Reference:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceC-clouds-design.md`
- WG16 source to port: `scripts/lab/CloudVolume.cs`, `CloudNoiseCompute.cs`, `CloudParams/Layers/Weather/Presets.cs`, `CloudLightCheck.cs`; `shaders/cloud_sky.gdshader`, `cloud_raymarch.glsl`, `cloud_noise_3d.glsl`, `cloud_density.gdshaderinc`; `data/cloud_*.json`
- The 3 fixes are documented in `docs/cloud-system-overview.md` §"root-cause fixes" (port the FIXED versions; verify they hold).

## Global Constraints

- **Target repo:** `C:\Wg16\WG17\terrainengine-10k`. **Namespace:** `Te10k.Clouds` (+ `.Checks`).
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild after every .cs edit; absolute `--path`; bare `--` for flags; compute windowed.**
- **GPU pattern:** cloud dome `Texture2Drd` assigned ONCE; contents updated via compute; RID never reassigned per frame (bug #118292). RenderingDevice ONLY in `CloudVolume`/`CloudNoiseCompute`. Toggle via `cloud_enabled` uniform — NEVER swap the Sky resource.
- **One-way data flow:** read sun/moon via `ILuminaryFeed`, ambient via `IAtmosphereFeed`. The ONLY back-edge is `Overcast` (a single float the driver pulls into `composer.Overcast`). No host callbacks; no ILabControls (presets operate on plain state).
- **Fix-on-port (the 3 bugs, §5 of spec):** double-alpha composite, sun-extinction crush, dead noise octave. Do NOT attempt flat-slab→vertical-deck realism (explicit future).
- **Depends on:** Slice A (`ILuminaryFeed`) + Slice B (`IAtmosphereFeed`). If B absent, fall back to flat ambient (graceful). Verify the real feed signatures against the merged Slice A/B files; if absent, stub with a reconcile TODO.

---

## File Structure

```
src/clouds/CloudParams.cs       # Task 1 — knob defaults + JSON load
src/clouds/CloudLayers.cs       # Task 1 — per-deck struct, parse, pack→GPU buffer
src/clouds/CloudWeather.cs      # Task 1 — 2D weather field bake (coverage/type/density)
src/clouds/CloudNoiseCompute.cs # Task 2 — 3D Perlin-Worley + detail bake (one-time)
src/clouds/CloudVolume.cs       # Tasks 3-5 — RD owner, raymarch dispatch, dome Texture2Drd, feeds, Overcast
src/clouds/CloudPresets.cs      # Task 6 — named looks (plain state, no ILabControls)
src/clouds/checks/CloudLightCheck.cs  # Task 5 — per-deck lighting + assign-once gate
src/clouds/shaders/cloud_noise_3d.glsl, cloud_raymarch.glsl, cloud_density.gdshaderinc, cloud_sky.gdshader
data/cloud_params.json, cloud_layers.json, cloud_presets.json
```

---

### Task 1: Cloud data classes + JSON

**Files:** Create `CloudParams.cs`, `CloudLayers.cs`, `CloudWeather.cs`; copy `data/cloud_*.json`

**Interfaces:** Produces `CloudParams` (knobs + Load), `CloudLayers` (struct/parse/Pack to GPU buffer), `CloudWeather` (2D field bake).

- [ ] **Step 1: Copy + re-namespace the three data classes**

Copy `scripts/lab/{CloudParams,CloudLayers,CloudWeather}.cs` → `src/clouds/`; namespace → `Te10k.Clouds`. Keep the std430-correct packing (use a `Std430Writer` equivalent — port `scripts/lab/Std430Writer.cs` if `CloudLayers.Pack` depends on it). Copy `data/cloud_params.json`, `cloud_layers.json`, `cloud_presets.json`.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(clouds): port cloud data classes + JSON (params/layers/weather)"
```

---

### Task 2: 3D noise bake (CloudNoiseCompute)

**Files:** Create `CloudNoiseCompute.cs`, `shaders/cloud_noise_3d.glsl`

**Interfaces:** Produces `CloudNoiseCompute` baking the shape (Perlin-Worley RGBA) + detail (R) 3D volumes once via a local RD, plus the 2D weather field; readback/handoff to the main RD at init. **Fix #3 (dead octave) lands here** if the dead octave is in the noise bake.

- [ ] **Step 1: Port the noise bake + shader**

Copy `CloudNoiseCompute.cs` + `cloud_noise_3d.glsl`; re-namespace; fix `res://` paths. **Verify the detail-noise octaves are all weighted** (Fix #3): confirm no octave uses a base-frequency that collapses to a constant. If WG16's committed version already fixed it, just confirm; else restore the octave's contribution.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(clouds): port 3D Perlin-Worley noise bake (verify no dead octave)"
```

---

### Task 3: CloudVolume raymarch → dome (with fixes #1 and #2)

**Files:** Create `CloudVolume.cs`, `shaders/cloud_raymarch.glsl`, `cloud_density.gdshaderinc`, `cloud_sky.gdshader`

**Interfaces:** Produces `CloudVolume : Node` owning RD, dispatching `cloud_raymarch.glsl` into a lat-long dome `Texture2Drd` (assigned once), composited by `cloud_sky.gdshader`. **Fix #1 (double-alpha) lands in the composite; Fix #2 (sun-extinction) lands in the raymarch direct-light term.**

- [ ] **Step 1: Port the raymarch + composite, applying fixes #1 and #2**

Copy the four shader files + `CloudVolume.cs`; re-namespace; fix paths. **Fix #1:** ensure cloud alpha/transmittance is applied ONCE — the dome stores premultiplied radiance + single alpha, and `cloud_sky.gdshader` composites with a single over-blend (not `mix` on already-premultiplied radiance). **Fix #2:** the sun light-march extinction must not crush the direct term — keep Beer-Lambert for transmittance *through* the cloud but clamp/rebalance so lit faces stay bright (WG16's fix raised the sun-march density constant off the ~0.02 that drove od~10–30). Bind the dome `Texture2Drd` once.

- [ ] **Step 2: Build + EYE-GATE the fixes**

Install the cloud sky material; `cloud_enabled=true`; feed a fixed sun + a flat ambient (atmosphere optional). Launch:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected: clouds present; **edges soft (not double-dark)** (#1); **sun-facing sides bright** (#2); **fine wispy detail** (#3 from Task 2). If clouds are uniformly grey/dim, a fix didn't take.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(clouds): raymarch→dome composite — fixes #1 double-alpha + #2 sun-extinction"
```

---

### Task 4: Feeds + Overcast back-edge

**Files:** Modify `CloudVolume.cs`; modify the driver

**Interfaces:** `CloudVolume` subscribes `ILuminaryFeed` (sun/moon) + `IAtmosphereFeed` (ambient colors); exposes `float Overcast { get; }`. Driver pulls `Overcast` into `composer.Overcast` each frame before `Compose()`.

- [ ] **Step 1: Wire inbound feeds**

`CloudVolume` reads sun/moon from `ILuminaryFeed` for the direct light-march; reads `IAtmosphereFeed.CloudZenith/HorizonSun/SunTrans` for cloud ambient (fall back to a flat ambient color if atmosphere `Ready==false`). Verify the real interfaces in `src/lighting/` and `src/atmosphere/`; stub + TODO if not merged.

- [ ] **Step 2: Wire the Overcast back-edge**

`CloudVolume.Overcast` returns the current coverage proxy. In the driver's `_Process`, before `composer.Compose()`, set `composer.Overcast = cloudVolume.Overcast`. Confirm this is the ONLY write from clouds toward lighting (a single scalar).

- [ ] **Step 3: Build + EYE-GATE overcast dimming**

Launch; raise coverage. Expected: the sun on the terrain visibly dims as coverage rises (lighting-owned dimming via the one scalar). Commit:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(clouds): ILuminary/IAtmosphere feeds + Overcast→composer (one scalar)"
```

---

### Task 5: CloudLightCheck (per-deck + assign-once gate)

**Files:** Create `src/clouds/checks/CloudLightCheck.cs`

**Interfaces:** `static bool Run(CloudVolume cv)` — the per-deck lighting numeric probe PLUS the dome `Texture2Drd` assign-once assertion (RID stable across a recompute). Prints `CLOUD-LIGHT PASS ... rid-stable=YES`.

- [ ] **Step 1: Port + extend the check**

Copy `CloudLightCheck.cs`; re-namespace; strip any harness deps. Add the assign-once assertion (capture dome RID, force a recompute, assert identical). Wire `--lightcheck`.

- [ ] **Step 2: Run**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --lightcheck
```
Expected: `CLOUD-LIGHT PASS ... rid-stable=YES`, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(clouds): CloudLightCheck — per-deck lighting + assign-once gate (PASS)"
```

---

### Task 6: CloudPresets + final eye-gate

**Files:** Create `src/clouds/CloudPresets.cs`

**Interfaces:** `CloudPresets` applies named looks to `CloudParams`/`CloudLayers` (plain state, no ILabControls).

- [ ] **Step 1: Port CloudPresets (plain state)**

Copy `CloudPresets.cs`; re-namespace; strip `ILabControls` — apply to the param/layer objects directly. Load `data/cloud_presets.json`.

- [ ] **Step 2: Full EYE-GATE**

Launch over the lit + atmosphere sky (if B merged), cycle a couple presets:
Expected: cumulus/stratus/cirrus read natural; coverage variety, no obvious tiling; the 3 fixes hold; overcast dims the sun; **yaw/pitch at fixed pose → cloud lighting unchanged** (clouds parallax with camera *movement* only — that's physical).

- [ ] **Step 3: Profile + slice marker + migration note**

Record frame ms (clouds on/off). Commit marker; append "WG17 Slice C outcome" (fixes confirmed, frame ms) to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md` in WG16.
```bash
git commit --allow-empty -m "milestone(clouds): Slice C eye-gate PASS — 3 fixes hold, overcast dims, view-locked (<X>ms)"
```

---

## Self-Review

**Spec coverage:** §3 data/noise/volume/presets → Tasks 1–3,6; §4 feeds + Overcast back-edge → Task 4; §5 the 3 fixes → Task 2 (#3), Task 3 (#1,#2), each eye-gate-confirmed; §6 CloudLightCheck + assign-once + view-lock eye-gate → Tasks 5–6; §8 DoD → Task 6. ✓

**Placeholder scan:** feed-signature reconciliation is explicit (verify against merged files / stub+TODO). `<X>ms` measured; `.../` abbreviates the Godot path in Global Constraints. ✓

**Type consistency:** `Overcast` (float, pull into `composer.Overcast`), `ILuminaryFeed`/`IAtmosphereFeed` consumption, dome `Texture2Drd` assign-once, `cloud_enabled` toggle — consistent across tasks. The 3 fixes map to specific files (noise=Task2, composite+sun-march=Task3). ✓

**Note:** this is a port-with-fixes, so the discipline is port → apply the 3 specific fixes → eye-gate each fix → numeric gate. The fixes are concrete (named files + mechanisms), not "improve the clouds."
