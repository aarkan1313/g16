# CDLOD streaming + shadow — KNOWN ISSUES for the outside audit (2026-06-24)

Surfaced when CDLOD was made **default-on** this session (ARC A step 0) and the user actually FLEW the infinite
world (`terrain_lab.tscn`) — these were invisible before because the default scenes showed the finite single
mesh. The user reports three issues; this doc characterizes each, flags **what is a likely regression from this
session vs pre-existing**, gives repro + A/B knobs, and points the auditor at the right code/memory.

**All three are MOTION-ONLY** — frozen frames look fine (verified: far terrain is coherent in a still; the
shadow stipple is the only one visible statically). They need **live in-motion analysis** (slow-mo frame diffs,
a debug-color chunk-state viz) — which is the audit's job.

**NOT the field cache.** Ruled out: `--fieldcache=0` (CDLOD on, cache off) still shows all three; at rest there
is zero churn; the field-cache work is a clean perf win (10.7→7.2 ms, quality-identical, gates PASS).

---

## Issue 1 — Shadow stipple (the dotted pattern) — PRE-EXISTING (not this session)

**Looks like:** a grid of dots / stipple over shaded terrain (user: "we had this before, can't remember the
exact problem"). Visible statically.

**What it is (documented):** PCF penumbra grain from the **SoftHigh** soft-shadow filter dithering over the
**coarse, un-surfaced CDLOD mesh**. See memory `cdlod-shadow-acne-vs-penumbra-stipple` (two distinct stipples:
grid-aligned ACNE vs soft penumbra/PCF stipple) and `shadow-pass-findings`. The recorded conclusion:
**"the real fix is surfacing / denser mesh, NOT softer shadows"** — i.e. it's gated on the ground-surfacing arc
(still a placeholder) and is largely a look-consequence of judging bare geometry.

**Regression?** No — pre-existing, predates this session. `LightingComposer.cs:222`
`DirectionalSoftShadowFilterSetQuality(SoftHigh)`.

**Tractable mitigation to try (audit):** a less-stippling shadow filter quality (Soft/Hard vs SoftHigh) or a
lower `ShadowBlur`; A/B `--shadowdist`. But the true fix is surfacing (`REVIEW-2026-06-24` Area 1) + the lighting
analytic-AO (Area 2). Don't over-soften to mask it.

## Issue 2 — "Chunks not right far out" — POSSIBLE REGRESSION (ARC B), motion-only

**Looks like:** far terrain / the load boundary reads wrong or changes as you move (frozen stills look fine).

**Likely cause + regression suspicion:** this session's **ARC B** changed the streaming: load ring **R=1→2**
(5×5, boundary ~16 km), **fog coupled to the load radius**, **velocity-predictive** center bias. The larger ring
loads more far/coarse chunks; the fog coupling adds distance haze (stills show it's mild, not broken). **A/B the
ARC-B knobs to localize:** `--loadring=1` (pre-ARC-B 3×3) vs `=2`; `--fogviewscale=0` (coupling off) vs `=1`;
`--lookahead=0` (predictive off) vs `=3`. If `--loadring=1`/`--lookahead=0` looks better in motion, ARC B
regressed it and should be re-tuned or reverted. Spec/decisions: `2026-06-24-infinite-streaming-popfix*`,
memory `arcb-infinite-streaming-tuning-northstar` (the user's north-star: MIN fog / MAX view / no detail loss —
the OPPOSITE of over-fogging, so the fog coupling default may be wrong for this goal).

**Code:** `CdlodQuadtree.SelectRoaming` (ring), `CdlodTerrain.cs` (LoadRing/PredictLookahead/hysteresis),
`LightingComposer.cs` fog-coupling block (~line 308), `TerrainLabUI.Lighting.cs` `CdlodViewDistance`.

## Issue 3 — Churn / "blink out then back" when moving fast — PRE-EXISTING CDLOD, motion + speed-related

**Looks like:** patches blink out and back; transitions visible; **scales with speed** (user: "speed related").
`--notighten` (generous AABB) did NOT obviously fix it, so it's not only the AABB cull.

**Diagnostics gathered (this session):**
- **Not thrash:** rebirths = ~39 of ~4,900 births (<1%) on the border-crossing profile (`PROFILE-STREAM` now
  reports `rebirths`). Chunks aren't oscillating; it's mostly clean leading-edge streaming + LOD splits.
- **Loader keeps up:** active chunk count stays ~570 at all tested speeds (no holes).
- **Numeric gates PASS:** `--morphcheck`/`--popcheck`/`--snapdiff` → height/normal at LOD swaps and the
  renderOrigin snap are seamless *in the model*. So the visible churn is an **ungated transition artifact**:
  candidates = (a) AABB frustum/shadow-cull flicker (memory `cdlod-chunk-shadow-aabb` — the 7×7 height probe
  misses peaks → too-short box → wrong cull → "vanishing chunk"; `--notighten` tests this), (b) the renderOrigin
  **snap-pop** every 8192 m (memory `cdlod-renderorigin-snap-pop` — documented as "NOT yet fixed": wxz
  reconstruction doesn't round-trip across the snap), (c) LOD-split-band visibility at speed.
- The user saw BOTH "blink out" (→ AABB cull) AND "stuff at the horizon" (→ Issue 2), "everywhere".

**Regression?** Mostly pre-existing CDLOD (the graveyard — memory `terrain-clipmap-killed-wg1-15`: "terrain LOD
is the project graveyard"). But ARC B's larger ring + predictive bias may amplify what's visible. The AABB
vanish + snap-pop are both pre-existing, documented-unfixed.

**Code:** `CdlodTerrain.cs` (`ApplyChunk` AABB margin ~line 277-298, `MaxChunkOps=24` birth budget, `RetireGrace`,
the renderOrigin snap in `Tick` ~line 153), `ChunkAabbProvider.cs` (the coarse 7×7 probe). **The proper AABB fix
the session identified:** the field cache already bakes the full GridN height grid — derive the EXACT per-chunk
min/max from it (one amortized readback per frame, not per chunk) → boxes that are both correct (no vanish) and
tight (good shadows). Not built (needs a small GPU min/max reduction + a per-frame readback).

---

## Repro + A/B knobs (for the auditor)

Fly `terrain_lab.tscn` (CDLOD default-on). `review.tscn` is the STABLE single mesh now (CDLOD off in ReviewMode —
the 1-9 look presets judge on it; `--cdlod=1` to force streaming in review).

- **Profiler (border-crossing, the real test):** `--profmove --profile=8 --profspeed=800` → `PROFILE:` +
  `PROFILE-STREAM: snaps/births/rebirths/activeChunks`. `--profspeed=` sets flight speed.
- **Isolation:** `--fieldcache=0/1` (cache), `--notighten` (generous AABB), `--pinorigin` (no renderOrigin snap),
  `--loadring=1..4`, `--fogviewscale=0..3`, `--lookahead=0..8`, `--bakereq=N`, `--cdlod=0` (single mesh).
- **Gates (all PASS):** `--morphcheck --popcheck --snapdiff --stitchcheck --streamcheck --cdlodcheck --fieldcheck`.

## This session's changes (for regression triage)

- **ARC A step 0:** CDLOD default-on (review.tscn excepted). Exposed all three; didn't cause them.
- **ARC B:** load ring R=2, hysteresis, fog↔radius coupling, velocity-predictive (`--loadring/--fogviewscale/
  --lookahead`). **The prime regression suspect for Issue 2** (far-out / haze). Cursory-passed earlier but the
  user only glanced; re-evaluate against the MIN-fog/MAX-view north-star.
- **Field cache:** default-on perf win; ruled out as a cause of all three.
- Pre-existing, documented-unfixed: shadow stipple, AABB vanishing-chunk, renderOrigin snap-pop.

## Recommended audit priorities

1. **A/B ARC B in motion** (`--loadring=1`, `--fogviewscale=0`, `--lookahead=0`) — cheapest way to confirm/deny
   the Issue-2 regression. If better, re-tune/revert ARC B toward the north-star.
2. **AABB vanish** — build the exact-min/max-from-the-cache AABB (the identified proper fix) or a finer probe.
3. **renderOrigin snap-pop** — the documented-unfixed wxz round-trip issue.
4. **Shadow stipple** — gated on surfacing; a filter mitigation is a stopgap, not a fix.
