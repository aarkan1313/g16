# docs/archive — frozen history

Archived 2026-06-20 in the doc-set reset (see `DECISIONS.md`). WG16 had accumulated
20 specs + 23 plans + 4 handoffs in five days — the exact "process outran the results"
sprawl the project was founded to avoid. This folder freezes the bulk of it so the active
doc set stays thin.

**Nothing here is deleted — it's moved, with git history intact.** If an arc resumes, pull its
design back out, or (more in the WG16 spirit) re-spec it fresh and judged-early rather than
porting an old plan.

## What's here
- `plans/` — all 23 implementation plans (throwaway execution detail by design).
- `specs/` — superseded or not-yet-scheduled designs: the cloud refactor/shadow/volumetric/
  multi-layer/per-deck arc, the ground richness/baked-compute/presentation-arc/foundation/
  material-system designs, library-integration, godray-occlusion, the base-field design.
  (The **erosion** and **terrain-LOD/CDLOD** specs were pulled BACK to the active set on
  2026-06-20 — the full-scope re-roadmap made them live Phase-A/Phase-B designs.)
- `handoffs/` — the four transient session handoffs.
- `cloud-look-audit-prompt.md`, `cloud-polish-handoff-prompt.md` — transient prompt docs.

## What stayed active (NOT here)
- `ROADMAP.md` (rewritten fresh), `NEEDS_REVIEW.md` (the eye-gate queue), `DECISIONS.md`,
  `HANDOFF.md`, `TECH_STACK.md`, `README.md`, `performance.md`.
- Reference docs for shipped systems: `cloud-system-overview.md`, `godray-system-overview.md`,
  `cloud-next-steps.md` (the pending cloud eye-gate checklist).
- The two active **lane roadmaps**: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`,
  `specs/2026-06-20-sun-light-system-architecture.md`.
- The four **built-but-unapproved** specs the imminent eye-gates act on:
  `specs/2026-06-19-sun-disc-polish-design.md`,
  `specs/2026-06-20-lighting-decouple-and-time-axis-design.md`,
  `specs/2026-06-20-ground-gm2-real-height-maps-design.md`,
  `specs/2026-06-20-ground-gm3-within-area-variation-design.md`.

## Note on cross-references
Docs written before the reset (including the two kept lane roadmaps) cite specs/plans at their
**pre-archive paths** (`specs/…`, `plans/…`). Those files now live under `docs/archive/`. The
new `ROADMAP.md` carries the correct active paths; treat it as the source of truth.
