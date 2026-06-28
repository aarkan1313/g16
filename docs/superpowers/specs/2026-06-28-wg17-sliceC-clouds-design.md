# WG17 Sky Stack — Slice C: Clouds (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `C:\Wg16\wg-16-project` + audit `docs/MIGRATION-AUDIT-2026-06-28.md`
**Part of:** WG17 sky stack — A. Lighting → B. Atmosphere → **C. Clouds (this)** → D. Godrays.
**Reads from:** Slice A Lighting (`ILuminaryFeed` — sun/moon) + Slice B Atmosphere (`IAtmosphereFeed` —
sky-scatter colors for cloud ambient).
**Scope:** CloudVolume (volumetric raymarch) + CloudNoiseCompute + cloud data classes/presets. Godrays out
of scope (Slice D).

---

## 1. What It Should Do

Render **volumetric clouds** by raymarching a Perlin-Worley density field (3D shape + detail noise, 2D weather
field for coverage/type) into a lat-long hemisphere `Texture2Drd`, amortized across frames, then composite that
dome over the physical sky. Clouds are lit by the sun/moon (from Lighting) and ambient-lit by the atmosphere's
sky-scatter colors (from Atmosphere), so they sit in the same light as the sky. Cloud coverage feeds back a
single `Overcast` scalar to the Lighting composer so the sun dims under cloud.

---

## 2. Success Criteria

1. **Volumetric clouds over the lit+scattered sky** — recognizable cumulus/stratus/cirrus, not flat billboards.
2. **Coverage + type variety** reads natural across the weather field (no obvious tiling/repetition).
3. **Overcast dims the lighting-owned sun** — heavier coverage → dimmer direct sun on terrain, via a one-way
   `Overcast` push to the composer (Lighting stays the sole light writer).
4. **The three flagged look-bugs are FIXED** (fix-on-port, §5): no double-alpha, sun-extinction no longer
   crushes direct light, the dead noise octave restored.
5. **View-locked** — cloud *lighting* derives from sun + world, not camera. (Clouds themselves parallax
   correctly with camera *position*, which is physical — but yaw/pitch at a fixed pose doesn't change their
   lighting.) Carries Slice A's invariant.
6. **GPU assign-once** — the cloud dome `Texture2Drd` is bound to the sky material once; contents update via
   compute; RID never reassigned per frame (bug #118292). RenderingDevice quarantined to the compute classes.

**Non-goals:** flat-slab → true vertical-deck cloud distribution (explicit FUTURE work, see §5); godrays;
the lab harness/UI.

---

## 3. Architecture & File Layout

```
src/clouds/
├── CloudVolume.cs          # PORT + fix-on-port — owns the sky material, dispatches the raymarch compute into
│                           #   a Texture2Drd dome (assigned-once), composites on sky. Subscribes ILuminaryFeed
│                           #   + IAtmosphereFeed. Publishes Overcast back to the composer (one scalar).
├── CloudNoiseCompute.cs    # PORT — one-time 3D Perlin-Worley (shape+detail) + 2D weather bake; local RD,
│                           #   readback to main RD once at init.
├── CloudParams.cs          # PORT — live knobs (coverage, density, altitude, thickness, raymarch_steps, …)
├── CloudLayers.cs          # PORT — multi-deck layer stack (pack to GPU param buffer)
├── CloudWeather.cs         # PORT — 2D weather field (coverage + type macro; CPU FBM, tileable)
├── CloudPresets.cs         # PORT — named cloud looks (data/cloud_presets.json); applies to params/layers
│                           #   (NO ILabControls — operates on plain state, like SkyPresets in Slice A)
└── checks/CloudLightCheck.cs   # PORT — per-deck lighting numeric gate (--lightcheck) + assign-once RID assertion.

src/clouds/shaders/   (port verbatim EXCEPT the 3 fixes in §5, fix res:// paths)
├── cloud_sky.gdshader          # sky material: cloud raymarch composite + (sun/moon discs handled by lighting/atmosphere)
├── cloud_raymarch.glsl         # the volumetric march (the 3 fixes land here + CloudVolume.cs)
├── cloud_noise_3d.glsl         # 3D noise bake
└── cloud_density.gdshaderinc   # shared density/weather sampling helpers

data/   (copy from WG16)
└── cloud_params.json, cloud_layers.json, cloud_presets.json
```

**Layering rules (carry from A/B):** RD only in `CloudVolume`/`CloudNoiseCompute`; cloud dome `Texture2Drd`
assigned once; sun/moon/atmosphere read via the feeds (no host callbacks); `Overcast` is the only back-edge
and it's a single scalar.

---

## 4. Data Flow & Seams

**Inbound:**
- `ILuminaryFeed` (Slice A) — sun + moon direction/color/energy for cloud direct lighting.
- `IAtmosphereFeed` (Slice B) — `CloudZenith`/`CloudHorizonSun`/`CloudSunTrans` for cloud ambient/scatter, so
  clouds are lit by the same atmosphere as the sky. If atmosphere is off, fall back to a flat ambient (graceful).

**Outbound — the one back-edge:** `CloudVolume` exposes `float Overcast { get; }` (coverage 0..1). The
`LightingDriver` reads it once per frame and sets `composer.Overcast` before `Compose()`. This is a pull of a
single scalar — NOT clouds writing lights. Lighting remains the sole writer; clouds only inform the dimming.

**Sky composite:** `CloudVolume` owns the sky `ShaderMaterial` (the one Atmosphere installed in Slice B, or a
fallback). It binds the cloud dome `Texture2Drd` once and composites in `cloud_sky.gdshader`. Toggling clouds
branches on a `cloud_enabled` uniform — never swaps the Sky resource (radiance re-bake thrash).

---

## 5. Fix-on-Port — the three flagged bugs (from `cloud-look-real-rootcauses`)

Port faithfully, but FIX these specific bugs as we go (each gets a check or eye-gate confirmation):

1. **Double-alpha composite.** WG16 applied cloud alpha twice (once in the raymarch accumulation, again in the
   sky composite), over-darkening edges. Fix: apply transmittance/alpha once; the composite uses pre-multiplied
   color + single alpha. Confirm: cloud edges read soft, not doubly-darkened.
2. **Sun-extinction crushing direct light.** The direct-light term ran through extinction so aggressively that
   lit cloud faces went near-black. Fix: clamp/rebalance the extinction applied to the direct sun contribution
   (keep Beer-Lambert for *transmittance through* the cloud, but don't crush the *surface* lit term). Confirm:
   sun-facing cloud sides are bright.
3. **Dead noise octave.** One detail-noise octave was effectively zero-weighted (a packing/index bug), so
   clouds lacked fine structure. Fix: restore the octave's contribution. Confirm: clouds show fine wispy detail,
   not just blobby low-frequency shapes.

**Explicit FUTURE (not this slice):** clouds are flat-altitude slabs ("decks"); real clouds have vertical
distribution. Per `cloud-deck-vertical-realism` this is its own future project — note it, don't build it.

---

## 6. Testing & Eye-Gate

- **`CloudLightCheck`** (ported, `--lightcheck`) — per-deck lighting math numeric probe (the CPU mirror of the
  shader phase/multi-scatter), PLUS the assign-once RID assertion on the cloud dome. Prints
  `CLOUD-LIGHT PASS ... rid-stable=YES`.
- **`CloudStats`** (ported, `--cloudstats`) — read back the dome, print coverage/brightness; used to confirm
  the fix-on-port bugs (e.g. mean brightness no longer crushed).
- **Eye-gate (manual):** clouds on over the lit+scattered sky:
  - Cumulus/stratus/cirrus read natural; coverage variety with no obvious tiling.
  - The three fixes visibly hold: soft (not double-dark) edges; bright sun-facing sides; fine detail present.
  - Increase coverage → the sun on the terrain visibly dims (Overcast feedback).
  - **Yaw/pitch at fixed pose** → cloud lighting unchanged; clouds parallax with camera *movement* only.
- Record frame ms (clouds on/off; the raymarch is the cost — amortized across frames).

---

## 7. Dependencies, Risks, Constraints

- **Depends on Slice A** (`ILuminaryFeed`) and **Slice B** (`IAtmosphereFeed`). If B isn't merged, clouds fall
  back to flat ambient (graceful, but the eye-gate wants atmosphere for the real look). Prefer A+B merged first.
- **Risk — reintroducing a look-bug:** the three fixes are specific; the eye-gate + `CloudStats` confirm each.
  Don't "tidy" the raymarch beyond the three fixes (scope creep toward the vertical-deck rework).
- **Risk — assign-once / radiance thrash:** dome RID assigned once; toggle via `cloud_enabled` uniform, never
  swap the Sky. Asserted by `CloudLightCheck`.
- **Build/launch gotchas:** `dotnet build` after every `.cs` edit; absolute `--path`; bare `--` for flags;
  compute runs windowed.

---

## 8. Definition of Done (Slice C)

- Volumetric clouds over the lit+scattered sky; natural coverage/type variety.
- The three flagged bugs fixed and confirmed (soft edges, bright sun sides, fine detail).
- Overcast dims the lighting-owned sun via the one-way scalar; Lighting still sole writer.
- Cloud dome `Texture2Drd` assigned once; `CloudLightCheck` PASS `rid-stable=YES`; view-locked eye-gate PASS.
- Frame ms recorded. Flat-deck→vertical realism noted as future. Clean focused commits; ~500-line docs.
- Migration record updated with the Slice C outcome.
