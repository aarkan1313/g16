# WG17 Sky Stack — Slice B: Atmosphere (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `C:\Wg16\wg-16-project` + audit `docs/MIGRATION-AUDIT-2026-06-28.md`
**Part of:** WG17 sky stack — A. Lighting (done/staged) → **B. Atmosphere (this)** → C. Clouds → D. Godrays.
**Reads from:** Slice A Lighting (`ILuminaryFeed` — sun dir/color/energy).
**Scope:** AtmosphereCompute (Hillaire physical-sky LUTs) + AerialPerspective (screen-space depth haze).
Clouds/godrays are out of scope.

---

## 1. What It Should Do

Render a **physically-based sky** using the Hillaire scattering model: precompute transmittance,
multiple-scattering, and sky-view LUTs on the GPU, plus a 64-slice log-Z aerial-perspective froxel volume.
The sky-view LUT colors the sky dome; the aerial volume tints distant terrain with extinction + inscatter
(distance haze). LUTs recompute only when the sun changes — not per frame. The atmosphere also produces the
three sun-dependent "cloud-lighting" colors (zenith / horizon-sun / sun-transmittance) that Slice C reads to
light clouds plausibly.

This replaces the procedural-sky fallback that Slice A's `ApplySky` drives: once atmosphere is on, the
physical LUT owns the daytime sky color.

---

## 2. Success Criteria

1. **Physical sky gradient** across a day cycle — believable dawn/noon/dusk sky colors driven by sun elevation.
2. **Aerial perspective** on distant terrain — far geometry fades into atmospheric haze (extinction +
   inscatter), not a flat fog color.
3. **LUTs recompute on sun change only** — no per-frame recompute; cheap when the sun is static.
4. **View-locked** — the sky's *lighting/color* derives from sun + world, never camera orientation. Yaw/pitch
   at a fixed time does not change sky color (only what's framed). Carries Slice A's invariant.
5. **GPU assign-once** — the LUT `Texture2Drd`s are bound to the sky material exactly once; contents update
   via compute, the RID is never reassigned per frame (guards Godot bug #118292).
6. **Cleaner** — `AtmosphereCompute` subscribes to Lighting's `ILuminaryFeed` (no host callback); publishes
   `IAtmosphereFeed` for Clouds; RenderingDevice quarantined to the compute class.

**Non-goals:** clouds, godrays, the lab harness/UI, multi-sun atmosphere beyond the proven 3-sun cap (port the
cap as-is — `MaxAtmosphereSuns = 3`).

---

## 3. Architecture & File Layout

```
src/atmosphere/
├── AtmosphereCompute.cs    # PORT — Hillaire LUTs (transmittance/multiscatter/skyview) + 64-slice aerial
│                           #   froxel + the 3 cloud-light colors. RD quarantined. Texture2Drd assigned-once.
│                           #   Recompute gated on sun change.
├── AerialPerspective.cs    # PORT — screen-space fullscreen quad sampling the aerial froxel LUT;
│                           #   composites extinction+inscatter on geometry (sky skipped). RenderPriority 120.
├── IAtmosphereFeed.cs      # NEW — what Clouds (Slice C) reads: CloudZenith, CloudHorizonSun, CloudSunTrans.
├── AtmosphereParams.cs     # PORT — exposure, density knobs (from data/atmosphere_params.json if present, else defaults).
└── checks/AtmosphereCheck.cs   # PORT — LUT numeric readback gate (--atmoscheck) + assign-once RID assertion.

src/atmosphere/shaders/   (port verbatim, fix res:// paths)
├── atmosphere_transmittance.glsl
├── atmosphere_multiscatter.glsl
├── atmosphere_skyview.glsl
├── atmosphere_aerial_v2.glsl
├── atmosphere_cloudlight.glsl     # the 3 cloud-light colors
└── aerial_screen_v2.gdshader      # the screen-space aerial quad

data/atmosphere_params.json   (copy from WG16 if present; else a small defaults file)
```

**Layering rules (carry from Slice A):**
- `RenderingServer`/`RenderingDevice`/`CallOnRenderThread` appear ONLY in `AtmosphereCompute.cs` and
  `AerialPerspective.cs` (the compute/screen classes).
- `AtmosphereCompute` reads sun state ONLY via `ILuminaryFeed` (pushed from the Lighting composer); it never
  reads camera orientation into the LUT math.
- The sky material's LUT `Texture2Drd` RIDs are assigned ONCE at init; per-frame work updates contents only.

---

## 4. Data Flow & Seams

**Inbound — `ILuminaryFeed` (defined in Slice A):** Lighting's composer calls `PushSun(dirToSun, color, energy)`
(+ extra suns) each `Compose()`. `AtmosphereCompute` subscribes; on a *changed* sun it re-dispatches the
sky-view + aerial LUTs (a dirty flag, not per-frame). This is the one-way edge — atmosphere never calls back
into lighting.

**Outbound — `IAtmosphereFeed` (new this slice):**
```csharp
namespace Te10k.Atmosphere;
public interface IAtmosphereFeed
{
    Color CloudZenith { get; }       // sky-scatter color at zenith (for cloud top-light)
    Color CloudHorizonSun { get; }   // horizon color toward the sun (for cloud side-light)
    Color CloudSunTrans { get; }     // sun transmittance color (for cloud direct-light tint)
    bool Ready { get; }              // LUTs computed at least once
}
```
Slice C's `CloudVolume` reads these to light clouds with the same atmosphere as the sky (no separate ad-hoc
cloud sky color). Until C exists, nothing consumes it — fine.

**Sky ownership handoff:** Slice A's `LightingDriver.ApplySky` drives a `ProceduralSkyMaterial` fallback. When
atmosphere is enabled, `AtmosphereCompute` installs the physical sky material (sampling the sky-view LUT) and
the procedural fallback is bypassed via an `atmosphere_on` uniform branch — NOT by swapping the Sky resource
(swapping triggers a radiance re-bake thrash; branch in-shader instead, per WG16's proven approach).

---

## 5. Fix-on-Port Notes

The atmosphere code itself was sound in WG16 (the cloud bugs live in Slice C). Port faithfully. The only
discipline changes:
- **Assign-once `Texture2Drd`** made explicit + asserted (see §6) — WG16 already did this; we now guard it.
- **Subscribe to `ILuminaryFeed`** instead of WG16's `host.Atmosphere`-style push from the UI god-class.
- **No CompositorEffect** for the aerial pass — keep it a screen-space quad with `render_priority`, the proven
  non-racing path (bug #118292).

---

## 6. Testing & Eye-Gate

- **`AtmosphereCheck`** (ported, `--atmoscheck`) — numeric LUT readback: transmittance at known sun angles
  matches expected ranges; sky-view LUT non-degenerate. PLUS a new assertion: the sky material's LUT
  `Texture2Drd` RID is identical before/after a recompute (assign-once invariant — contents change, RID doesn't).
  Prints `ATMOSPHERE PASS ... rid-stable=YES`.
- **Eye-gate (manual, in motion):** enable atmosphere on the lit terrain, run `--autotime`:
  - Sky gradient reads physical across dawn→noon→dusk; distant terrain hazes into the sky (aerial), not a flat
    fog wall.
  - **Yaw/pitch at fixed time** — sky color/lighting unchanged (only framing changes). HUD shows the carried
    `lighting-view-locked=YES` plus an `atmosphere: on rid-stable=YES` line.
- Record frame ms with atmosphere on vs off (LUT cost is amortized; the per-frame cost is the aerial quad).

---

## 7. Dependencies, Risks, Constraints

- **Depends on Slice A** for `ILuminaryFeed` (the sun source). If A isn't merged, B can stand up against a
  stub feed that supplies a fixed sun — but the real eye-gate wants the day cycle, so prefer A merged first.
- **Depends on terrain** only for *something to apply aerial perspective to* (the depth buffer). Terrain Slice
  1 has shipped (flying 4.2ms), so this is satisfied.
- **Risk — radiance re-bake thrash:** never swap the Sky resource to toggle atmosphere; branch in-shader on
  `atmosphere_on` (WG16's lesson, carried).
- **Risk — assign-once violation:** the one way to reintroduce bug #118292 is reassigning a LUT RID per frame.
  `AtmosphereCheck` asserts RID stability; layering keeps RD in the compute class.
- **Build/launch gotchas:** `dotnet build` after every `.cs` edit; absolute `--path`; CLI flags need the bare
  `--` separator; compute runs windowed (local RD).

---

## 8. Definition of Done (Slice B)

- Physical sky on the lit terrain; believable day-cycle sky; aerial haze on distance.
- LUTs recompute on sun change only; sky color view-locked (eye-gate PASS; HUD `view-locked=YES`).
- `Texture2Drd` LUT RIDs assigned once; `AtmosphereCheck` PASS with `rid-stable=YES`.
- `AtmosphereCompute` reads sun via `ILuminaryFeed`; publishes `IAtmosphereFeed`; RD quarantined.
- Frame ms recorded (atmosphere on/off). Clean focused commits; spec/plan ~500-line files.
- Migration record (WG16 `docs/MIGRATION-AUDIT-2026-06-28.md`) updated with the Slice B outcome.
