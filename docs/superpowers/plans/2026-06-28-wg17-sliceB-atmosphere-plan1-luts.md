# WG17 Slice B (Atmosphere) — Plan 1 of 2: LUT Core (AT-1) + Cloud-Light (AT-3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Hillaire sky-color LUTs fresh (transmittance, multiple-scattering, sky-view) so a physical sky renders on the dome, plus the AT-3 cloud-light colors. This is a REWRITE from the model — WG16's atmosphere was never validated and AT-2 was broken.

**Architecture:** `AtmosphereCompute` owns RenderingDevice, dispatches the LUT compute via `CallOnRenderThread`, binds each LUT `Texture2Drd` to the sky material exactly ONCE, and recomputes the sun-dependent LUTs only when the sun changes (dirty flag). Sky color comes from the sky-view LUT, branched in-shader on `atmosphere_on` (never swap the Sky resource).

**Tech Stack:** Godot 4.6 / C# (.NET 8), GLSL compute (RenderingDevice), GDShader sky material.

**Plan set (B is 2 plans):** **Plan 1 (this) = LUT core (AT-1) + cloud-light (AT-3).** Plan 2 = aerial perspective (AT-2) + integration + gates.

**Reference:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceB-atmosphere-design.md`
- Hillaire EGSR 2020 (the model): https://sebh.github.io/publications/egsr2020.pdf ; reference impl: https://github.com/JolifantoBambla/webgpu-sky-atmosphere
- WG16 GPU plumbing ONLY (not the math): `scripts/lab/AtmosphereCompute.cs`, `shaders/atmosphere_*.glsl`

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k`. **Namespace:** `Te10k.Atmosphere` (+ `.Checks`).
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild:** `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit. **Launch:** absolute `--path`; flags need bare `--`. Compute runs WINDOWED (local RD).
- **GPU pattern (the named constraint):** `CallOnRenderThread` dispatch; each LUT `Texture2Drd` RID assigned ONCE at init, contents updated via compute, NEVER reassigned per frame (guards Godot bug #118292). RenderingDevice ONLY in `AtmosphereCompute`.
- **Rewrite, not port:** rebuild the scattering math from Hillaire; reference WG16 only for `CallOnRenderThread`/`Texture2Drd`/std430 patterns.
- **Sun source:** subscribe to Slice A's `ILuminaryFeed` (`PushSun(dirToSun, color, energy)`). If Slice A isn't merged in WG17 yet, use a temporary fixed-sun stub and a TODO to reconcile when A lands — verify the real `ILuminaryFeed` signature against `src/lighting/ILightingTarget.cs` at that time.
- **Sky toggle:** branch in-shader on `atmosphere_on`; NEVER swap the `Sky` resource (radiance re-bake thrash).

---

## File Structure (this plan)

```
src/atmosphere/AtmosphereCompute.cs   # Tasks 2-4 — RD owner, LUT dispatch, dirty-flag recompute, Texture2Drd-once
src/atmosphere/AtmosphereParams.cs    # Task 1 — atmosphere constants (Rayleigh/Mie/ozone, radii, exposure)
src/atmosphere/IAtmosphereFeed.cs     # Task 5 — the 3 cloud-light colors for Slice C
src/atmosphere/shaders/atmosphere_transmittance.glsl   # Task 2
src/atmosphere/shaders/atmosphere_multiscatter.glsl    # Task 3
src/atmosphere/shaders/atmosphere_skyview.glsl         # Task 4
src/atmosphere/shaders/sky_atmosphere.gdshader         # Task 4 — samples sky-view LUT by EYEDIR
src/atmosphere/checks/AtmosphereCheck.cs               # Task 6
```

---

### Task 1: AtmosphereParams + project scaffolding

**Files:** Create `src/atmosphere/AtmosphereParams.cs`

**Interfaces:**
- Produces: `record AtmosphereParams` with the standard-Earth scattering constants — `PlanetRadiusKm=6360`, `AtmosphereTopKm=6460`, Rayleigh scattering coeff (rgb), Rayleigh scale-height, Mie scattering/absorption + scale-height + phase-g, ozone absorption + layer params, `SunIlluminance`, `Exposure`. `static AtmosphereParams Earth()` returning the canonical Hillaire/Bruneton values.

- [ ] **Step 1: Write AtmosphereParams.cs with documented Earth constants**

Create the record with the standard values (from Hillaire/Bruneton): Rayleigh scattering `(5.802, 13.558, 33.1) e-6 /m`, Rayleigh height `8 km`; Mie scattering `3.996e-6`, Mie absorption `4.4e-6`, Mie height `1.2 km`, Mie phase g `0.8`; ozone absorption `(0.650, 1.881, 0.085) e-6` in a `25–40 km` tent layer; radii as above. Add `Earth()`. Comment each constant with its source.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add src/atmosphere/AtmosphereParams.cs && git commit -m "feat(atmosphere): Earth scattering constants (Hillaire/Bruneton)"
```

---

### Task 2: Transmittance LUT (computed once)

**Files:** Create `src/atmosphere/shaders/atmosphere_transmittance.glsl`; Create `src/atmosphere/AtmosphereCompute.cs` (initial — RD setup + this one LUT)

**Interfaces:**
- Produces: `class AtmosphereCompute : Node` with `void Attach(...)`, an RD-owned transmittance `Texture2Drd` (e.g. 256×64, parameterized by altitude × sun-zenith), dispatched once via `CallOnRenderThread`. `Texture2Drd TransmittanceTex { get; }` (RID assigned once).

- [ ] **Step 1: Write the transmittance compute shader**

`atmosphere_transmittance.glsl`: for each (altitude, cos-sun-zenith) texel, ray-march from the sample point to the atmosphere top, accumulating optical depth (Rayleigh + Mie + ozone), output `exp(-opticalDepth)` as colored transmittance. Standard Hillaire/Bruneton transmittance — ~40 march steps.

- [ ] **Step 2: Write AtmosphereCompute RD setup + dispatch transmittance once**

In `AtmosphereCompute.cs`: acquire the main RD via `RenderingServer`, compile the shader, create the 256×64 RGBA16F storage texture, wrap it in a `Texture2Drd` (assign the RID ONCE here), and dispatch via `CallOnRenderThread` at init. Document the assign-once rule inline.

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(atmosphere): transmittance LUT (computed once, Texture2Drd assign-once)"
```

---

### Task 3: Multiple-scattering LUT (computed once)

**Files:** Create `src/atmosphere/shaders/atmosphere_multiscatter.glsl`; Modify `AtmosphereCompute.cs`

**Interfaces:**
- Consumes: transmittance LUT (Task 2).
- Produces: a multiple-scattering `Texture2Drd` (e.g. 32×32), dispatched once; `Texture2Drd MultiScatterTex { get; }`.

- [ ] **Step 1: Write the multiscatter compute shader**

`atmosphere_multiscatter.glsl`: per (altitude, sun-zenith) texel, evaluate the isotropic multiple-scattering term per Hillaire §5 (the 2nd-order-and-beyond approximation via the uniform-sphere integral), sampling the transmittance LUT. ~64 directions × short march.

- [ ] **Step 2: Dispatch it once in AtmosphereCompute (after transmittance)**

Create the 32×32 texture, `Texture2Drd` RID once, dispatch once after transmittance on the render thread.

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(atmosphere): multiple-scattering LUT (computed once)"
```

---

### Task 4: Sky-View LUT + sky material (the visible sky)

**Files:** Create `src/atmosphere/shaders/atmosphere_skyview.glsl`, `src/atmosphere/shaders/sky_atmosphere.gdshader`; Modify `AtmosphereCompute.cs`

**Interfaces:**
- Consumes: transmittance + multiscatter LUTs; sun direction (stub or `ILuminaryFeed`).
- Produces: a sky-view `Texture2Drd` (e.g. 192×108, lat/long parameterization), recomputed on sun change (dirty flag, NOT per frame). `sky_atmosphere.gdshader` samples it by `EYEDIR` and branches on `atmosphere_on`. `void SetSun(Vector3 dirToSun, Color color, float energy)` sets the dirty flag.

- [ ] **Step 1: Write the sky-view compute shader**

`atmosphere_skyview.glsl`: per (view-azimuth, view-zenith) texel, march along the view ray accumulating in-scattering (Rayleigh+Mie phase × transmittance × sun illuminance) + the multiscatter term. Lat/long mapping with the horizon-zenith squeeze Hillaire uses to keep horizon detail. Output rgb radiance.

- [ ] **Step 2: Write the sky material shader**

`sky_atmosphere.gdshader` (shader_type sky): sample the sky-view LUT by `EYEDIR`; if `atmosphere_on == false`, fall back to a simple gradient (so the toggle never swaps the Sky resource). Bind the sky-view `Texture2Drd` once.

- [ ] **Step 2.5: Dispatch sky-view on sun-change**

In `AtmosphereCompute`: `SetSun(...)` stores the sun + sets `_skyDirty`. In a render-thread pump, if `_skyDirty`, re-dispatch the sky-view LUT (transmittance/multiscatter stay cached). RID assigned once; contents update.

- [ ] **Step 3: Wire to a scene + EYE-GATE the sky**

Install `sky_atmosphere.gdshader` as the scene Environment's Sky material; set `atmosphere_on=true`; feed a fixed noon sun. Build + launch:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k"
```
Expected: a believable physical sky — blue zenith, brighter/warmer horizon, no banding. Change the sun hour → sky recolors. **No full-screen tint** (this is sky-view only; aerial is Plan 2).

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(atmosphere): sky-view LUT + sky material — physical sky eye-gate (sun-change recompute, RID once)"
```

---

### Task 5: AT-3 cloud-light colors + IAtmosphereFeed

**Files:** Create `src/atmosphere/IAtmosphereFeed.cs`; Modify `AtmosphereCompute.cs`

**Interfaces:**
- Produces: `interface IAtmosphereFeed { Color CloudZenith { get; } Color CloudHorizonSun { get; } Color CloudSunTrans { get; } bool Ready { get; } }`; `AtmosphereCompute : IAtmosphereFeed` deriving the three colors by sampling sky-view (zenith dir, horizon-toward-sun dir) + transmittance (sun dir) after each recompute.

- [ ] **Step 1: Write IAtmosphereFeed.cs (exact interface from spec §4)**

```csharp
using Godot;
namespace Te10k.Atmosphere;
public interface IAtmosphereFeed
{
    Color CloudZenith { get; }
    Color CloudHorizonSun { get; }
    Color CloudSunTrans { get; }
    bool Ready { get; }
}
```

- [ ] **Step 2: Implement the three colors in AtmosphereCompute**

After a sky-view recompute, read back (or sample) the LUT at zenith and at the horizon-toward-sun azimuth for `CloudZenith`/`CloudHorizonSun`; sample the transmittance LUT along the sun direction for `CloudSunTrans`. Set `Ready=true` after the first compute.

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(atmosphere): AT-3 cloud-light colors via IAtmosphereFeed"
```

---

### Task 6: AtmosphereCheck (numeric + assign-once gate)

**Files:** Create `src/atmosphere/checks/AtmosphereCheck.cs`

**Interfaces:**
- Consumes: `AtmosphereCompute`.
- Produces: `static bool Run(AtmosphereCompute atmo)` — asserts transmittance at zenith ≈ 1 and at the horizon < zenith; sky-view LUT non-degenerate (variance > 0); and the LUT `Texture2Drd` RIDs are identical before/after a `SetSun` recompute (assign-once). Prints `ATMOSPHERE PASS ... rid-stable=YES`.

- [ ] **Step 1: Write AtmosphereCheck.cs**

Read back the transmittance LUT: zenith-up sample ≈ near 1.0 (little atmosphere overhead), horizon sample notably < 1 (long path). Capture each LUT's `texture_rd_rid` (or the `Texture2Drd` RID), call `SetSun` with a different angle + force a recompute, capture again, assert identical. Print PASS/FAIL.

- [ ] **Step 2: Wire `--atmoscheck` (temporary _Ready hook or the driver) + run**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --atmoscheck
```
Expected: `ATMOSPHERE PASS ... rid-stable=YES`, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(atmosphere): AtmosphereCheck — transmittance sanity + assign-once RID gate (PASS)"
```

---

## Self-Review

**Spec coverage (Plan 1 = AT-1 + AT-3):** §3 AtmosphereCompute + the 3 sky LUTs → Tasks 1–4; §4 IAtmosphereFeed/AT-3 → Task 5; §5 rewrite-from-model + assign-once + ILuminaryFeed (stubbed, with reconcile TODO) → Tasks 2–5; §6 AtmosphereCheck + rid-stable → Task 6. AT-2 (aerial) is Plan 2. ✓

**Placeholder scan:** the `ILuminaryFeed` stub is explicitly flagged to reconcile when Slice A merges (not a vague TODO — a named file to check). The `.../` in some launch lines abbreviates the full Godot path given in Global Constraints. No unspecified work. ✓

**Type consistency:** `IAtmosphereFeed` matches spec §4 exactly; `SetSun(Vector3, Color, float)` consistent Tasks 4–6; `Texture2Drd` properties (`TransmittanceTex`/`MultiScatterTex`/sky-view) consistent. ✓

**Note:** rewrite-from-model means no red/green unit loop on the math; the gates are the numeric `AtmosphereCheck` (transmittance physics) + the sky eye-gate (Task 4 Step 3). The blue-band AT-2 bug is guarded in Plan 2.
