# Slice B — Atmosphere  🟡 STAGED  (REWRITE, not port)

**What:** physically-based sky via the Hillaire scattering model — transmittance / multiple-scattering /
sky-view LUTs (sky color) + a 64×64×32 aerial-perspective froxel (distance haze) + the 3 cloud-light colors.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-sliceB-atmosphere-design.md](../specs/2026-06-28-wg17-sliceB-atmosphere-design.md)
- Plans: [LUT core (AT-1/AT-3)](../plans/2026-06-28-wg17-sliceB-atmosphere-plan1-luts.md) ·
  [aerial (AT-2) + gates](../plans/2026-06-28-wg17-sliceB-atmosphere-plan2-aerial.md)
- Kickoff: [../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md](../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md) (Slice B block)

**Why a rewrite:** WG16's atmosphere was never validated and **AT-2 (aerial perspective) was visibly broken —
a full-screen blue band**. So AT-1/2/3 are rebuilt fresh from the Hillaire model (the technique is sound;
the WG16 implementation wasn't), referencing WG16 only for GPU plumbing.

**THE critical fix:** aerial perspective applies to SCENE GEOMETRY ONLY, gated by depth; the **sky/background
is EXCLUDED** (early-out on background depth). Whole-frame apply = the blue band. `AerialDepthCheck` + the
eye-gate assert sky-untinted + depth-graded.

**Reads:** `ILuminaryFeed` (sun). **Publishes:** `IAtmosphereFeed` (cloud-light colors) → Clouds.
**GPU rule:** LUT `Texture2Drd`/`Texture3Drd` assigned once (bug #118292).
