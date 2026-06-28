# WG17 Sky Stack — Slice D: Godrays (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `C:\Wg16\wg-16-project` + audit `docs/MIGRATION-AUDIT-2026-06-28.md`
**Part of:** WG17 sky stack — A. Lighting → B. Atmosphere → C. Clouds → **D. Godrays (this)**.
**Reads from:** Slice C Clouds (occlusion silhouette) + Slice A Lighting (sun screen position).
**Scope:** GodRaysScreen (screen-space radial light scatter). The smallest slice; rides on Clouds.

---

## 1. What It Should Do

Render **god rays / crepuscular shafts** as a screen-space post effect: GPU Gems 3 radial light-scattering
from the sun's on-screen position, using a hybrid occlusion buffer (cloud luminance silhouette) so shafts
appear where the sun is partially occluded by clouds. A clip-space fullscreen quad with high render priority;
the result is additively blended over the frame.

This is the visual payoff that makes the cloud+sun interaction read dramatically — but only when the sun is
actually occluded. Clear sky = no shafts (correct).

---

## 2. Success Criteria

1. **Crisp shafts when the sun is occluded by clouds** — point the camera near an occluded sun → visible
   radial light beams.
2. **No shafts when the sun is clear/unoccluded** — fully clear sky produces no spurious rays.
3. **No wash / no ring artifacts** — the failure modes WG16 fixed (full-screen wash; a bright ring around the
   sun) stay fixed; the tangential high-pass that fixed them is carried over.
4. **Sun off-screen → graceful** — when the sun is behind the camera or off-screen, rays fade out, no popping.
5. **GPU pattern carried** — screen-space quad with `render_priority`, NOT a CompositorEffect (bug #118292 /
   the proven non-racing path). RenderingDevice (if any) quarantined.

**Non-goals:** volumetric (froxel) god rays — WG16 proved the screen-space radial-scatter path is the one that
reads well; the froxel attempts read washy (see `godray-emission-vs-albedo-rootcause`). Do NOT rebuild as
volumetric. Also out: the lab harness/UI.

---

## 3. Architecture & File Layout

```
src/godrays/
└── GodRaysScreen.cs        # PORT — clip-space fullscreen quad; reads sun screen UV (from Lighting/camera
                            #   unproject) + cloud luminance as the occlusion source; radial blur scatter;
                            #   tangential high-pass (the wash/ring fix); additive blend. RenderPriority 127.

src/godrays/shaders/   (port verbatim, fix res:// paths)
└── godray_screen.gdshader  # the radial-scatter + occlusion + high-pass shader
```

**Layering rules (carry):** screen-space quad with `render_priority` (not a CompositorEffect); any RD use
quarantined to `GodRaysScreen`; reads sun + cloud-occlusion via the established feeds/textures, no host callbacks.

---

## 4. Data Flow & Seams

- **Sun screen position:** from the sun world direction (Lighting `ILuminaryFeed`) unprojected to screen UV via
  the active camera. When the unprojected sun is off-screen / behind, strength fades to zero.
- **Occlusion source:** the cloud luminance silhouette (Slice C's cloud dome / a luminance read). Shafts scale
  with how occluded the sun is — clear sun = low contrast in the occlusion buffer = no shafts. If clouds are
  off, godrays effectively do nothing (graceful — there's nothing to occlude the sun).
- **Composite:** additive blend over the frame at `render_priority` 127 (drawn after the aerial pass at 120).

No back-edges. Godrays is a pure consumer/screen effect.

---

## 5. Fix-on-Port Notes

The screen-space path was the *working* one in WG16; port it faithfully. Carry these existing fixes (do not
regress them):
- **Tangential high-pass** on the occlusion buffer — this is what killed the full-screen wash and the bright
  ring around the sun. It is load-bearing; port it intact.
- **Occlusion = clouds, not albedo** — the sun must be OCCLUDED (by clouds) for shafts; don't drive scatter off
  scene albedo/emission (the root-cause confusion in `godray-emission-vs-albedo-rootcause`). Port the hybrid
  occlusion buffer as-is.
- **Deleted shadow-map input** — WG16's dormant cloud-shadow-map godray mode was removed in the shadows audit;
  do NOT bring it back. Screen-luminance occlusion only.

No new bugs to fix here — just don't lose the fixes.

---

## 6. Testing & Eye-Gate

- **No numeric check** — godrays is inherently a visual effect; the gate is the eye-gate. (A `--godrayab`
  drift-free A/B capture path can be ported from WG16 if a repeatable before/after is wanted, but it's optional.)
- **Eye-gate (manual):** clouds on, sun partway behind a cloud:
  - Point camera near the occluded sun → crisp shafts radiating from the sun's screen position.
  - Clear the clouds (or move so the sun is unoccluded) → shafts vanish. No wash, no ring.
  - Turn so the sun goes off-screen / behind → shafts fade smoothly to nothing, no pop.
  - Confirm the shafts track the sun's *screen* position as the camera moves (this is screen-space and view
    -dependent BY DESIGN — godrays are a camera effect, unlike the world-locked lighting; the HUD should NOT
    claim view-lock for this layer).
- Record frame ms (godrays on/off) — the radial blur is the cost.

---

## 7. Dependencies, Risks, Constraints

- **Depends on Slice C** (clouds, for the occlusion silhouette) and **Slice A** (sun direction). Without clouds
  there's nothing to occlude the sun, so godrays is a no-op — build it last, as planned.
- **Risk — reintroducing wash/ring:** the tangential high-pass is the fix; port it intact, confirm by eye.
- **Risk — wrong occlusion source:** must be cloud luminance, not albedo/emission. Carry the hybrid buffer.
- **Note on view-dependence:** unlike Lighting/Atmosphere/Clouds-lighting, godrays ARE a screen-space,
  camera-dependent effect — that's correct. This is the one sky-stack layer that legitimately changes with
  camera, and the design says so explicitly so it's not mistaken for a regression of the view-lock invariant.
- **Build/launch gotchas:** `dotnet build` after every `.cs` edit; absolute `--path`; bare `--` for flags.

---

## 8. Definition of Done (Slice D)

- Crisp shafts when the sun is occluded by clouds; none when clear; graceful when sun off-screen.
- No wash, no ring (tangential high-pass carried); occlusion from cloud luminance, not albedo.
- Screen-space quad at `render_priority` 127, not a CompositorEffect.
- Eye-gate PASS; frame ms recorded (on/off). Clean focused commits; ~500-line doc.
- Migration record updated with the Slice D outcome — and with this, the WG17 sky stack (A–D) is complete.
