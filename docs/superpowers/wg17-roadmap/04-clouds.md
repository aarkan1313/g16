# Slice C — Clouds  🟡 STAGED  (port + fix 3 known bugs)

**What:** volumetric clouds — Perlin-Worley raymarch into a lat-long dome `Texture2Drd`, composited on the
physical sky, lit by sun/moon (Lighting) + atmosphere ambient (Atmosphere).

**Docs:**
- Spec: [../specs/2026-06-28-wg17-sliceC-clouds-design.md](../specs/2026-06-28-wg17-sliceC-clouds-design.md)
- Plan: [../plans/2026-06-28-wg17-sliceC-clouds-plan.md](../plans/2026-06-28-wg17-sliceC-clouds-plan.md)
- Kickoff: [../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md](../plans/2026-06-28-wg17-sliceBCD-KICKOFF-PROMPTS.md) (Slice C block)

**Approach:** faithful port of WG16's proven raymarch/noise architecture, BUT fix the three flagged look-bugs
as we go (documented in `docs/cloud-system-overview.md` — port the FIXED versions + verify they hold):
1. **double-alpha composite** (over-darkened edges) → apply alpha once.
2. **sun-extinction crushing direct light** (near-black lit faces) → keep transmittance-through, don't crush
   the surface lit term.
3. **dead noise octave** (no fine detail) → restore the octave's contribution.

**Reads:** `ILuminaryFeed` + `IAtmosphereFeed` (flat ambient fallback if atmosphere absent).
**Back-edge:** exposes a single `Overcast` scalar the driver pulls into the composer — the ONLY clouds→lighting
edge (Lighting stays sole writer). **GPU rule:** dome `Texture2Drd` assigned once.

**Explicit future (NOT this slice):** flat-slab decks → true vertical cloud distribution (its own project).
