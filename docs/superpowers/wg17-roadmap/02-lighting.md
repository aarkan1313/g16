# Slice A — Lighting  🟢 SHIPPED

**What:** the day/night + celestial composer and the single-owner shadow framework. The clean foundation the
whole sky stack reads from. Built first because lighting correctness is the reason for the WG17 restart.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-sliceA-lighting-design.md](../specs/2026-06-28-wg17-sliceA-lighting-design.md)
- Plans: [datacore](../plans/2026-06-28-wg17-sliceA-lighting-plan1-datacore.md) ·
  [composer](../plans/2026-06-28-wg17-sliceA-lighting-plan2-composer.md) ·
  [driver](../plans/2026-06-28-wg17-sliceA-lighting-plan3-driver.md)
- Kickoff: [../plans/2026-06-28-wg17-sliceA-KICKOFF-PROMPT.md](../plans/2026-06-28-wg17-sliceA-KICKOFF-PROMPT.md)

**Key architecture:** `LightingComposer` is the SOLE writer of scene lighting, pushing one-way through
`ILightingTarget` (replaces WG16's bidirectional `ILightingHost` + hardcoded `/root/` paths). `Compose()`
takes NO camera input → lighting is view-locked by construction. `ShadowRegistry` asserts ≤1 ground-shadow
owner (0 this slice; `Register` throws on a 2nd). Pure-data axis states (Time×Weather×Grade) + Luminary
budgeter ported. Publishes `ILuminaryFeed` for the sky stack.

**Fix-on-port applied:** no EMISSION ambient fill, no hardcoded `0.12f` ambient, no legacy mood-dict shim.

**Outcome (SHIPPED):** day/night cohesive, **view-locked YES**, **0 shadow owners**, **4.18ms**. Bug found +
fixed during build: sun lit BACKWARDS via `LookAtFromPosition(useModelFront:true)` (→ dark scene / sun disc
below horizon); fix = drop `useModelFront`, tonemap AgX→Filmic. (See memory `directionallight-usemodelfront-backwards`.)

**Anti-misdiagnosis guard:** the diagnostic labels view-dependent SURFACING as the material slice's concern,
so "weird when I mouse-look" can never again be chased as a shadow bug.
