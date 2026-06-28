# Slice D — Godrays  🟡 STAGED  (port)

**What:** screen-space crepuscular shafts — radial light-scatter (GPU Gems 3) from the sun's screen position,
occluded by cloud luminance. Smallest sky-stack slice; rides on Clouds.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-sliceD-godrays-design.md](../specs/2026-06-28-wg17-sliceD-godrays-design.md)
- Plan: [../plans/2026-06-28-wg17-sliceD-godrays-plan.md](../plans/2026-06-28-wg17-sliceD-godrays-plan.md)
- Kickoff: [../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md](../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md) (Slice D block)

**Approach:** faithful port of WG16's WORKING screen-space path. Carry intact:
- the **tangential high-pass** (the fix that killed the full-screen wash + the bright ring) — load-bearing.
- occlusion from **cloud luminance, not albedo/emission** (the WG16 root-cause confusion).
- screen-space quad at `render_priority` 127, **NOT a CompositorEffect** (bug #118292).
- do NOT bring back the deleted cloud-shadow-map mode; do NOT rebuild as volumetric (reads washy).

**Reads:** Clouds (occlusion) + Lighting (sun dir). No clouds → no-op (nothing occludes the sun).

**Note:** godrays ARE view-dependent BY DESIGN (a screen-space camera effect) — unlike Lighting/Atmosphere/
Clouds-lighting. The HUD must NOT claim view-lock for this layer. This is the one legitimately camera-dependent
sky layer, called out so it's never mistaken for a view-lock regression.
