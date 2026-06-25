# View distance + streaming deep-tune — START HERE (2026-06-25)

> ## ⛔ REVIEW OUTCOME (later 2026-06-25): FAILED LIVE EYE-GATE — REGRESSED, REWORK PENDING
> The user flew the real CDLOD path (`review.tscn --cdlod=1`) and it was **bad**: **54 ms / 28 fps focused**, a
> **flat "brown rectangle" chunk artifact**, shadows **popping in / blocky at distance**, and "a lot of regression
> in general." **The decision to revert was DEFERRED to a NEW CHAT** ("we will review shadows and these things you
> did"). The code is left AS-IS (builds, `e00b380` view-distance changes committed) so the next chat can review the
> actual state. **Do NOT trust the "SHIPPED" tone of the rest of this doc — treat it as the changelog of what was
> changed, then read the diagnosis + decision below.**
>
> ### Symptom → cause (diagnosed, high confidence)
> | Symptom | Cause | Fix |
> |---|---|---|
> | Flat "brown rectangle" chunk | **Velocity-scaled birth budget** (births burst to 256/fr ≫ 16/fr bake rate → field-cache slot churn hits the stale-slot race → a chunk shows wrong/flat baked data) | Remove the velocity budget (back to flat `MaxChunkOps=24`) |
> | 54 ms / 28 fps | **Ring 8** (~+7 ms over R=4) ON TOP OF an already-high in-motion floor. **KEY:** the motion cost barely moves across R=2/4/8, clouds-off, atlas-4096, or **even `--fieldcache=0` (867 live-eval chunks ≈ 578 cached)** → it is **CPU streaming-churn-bound, NOT GPU/shadow/cloud**. The recorded 6 ms baseline was a **5090**; this machine is not. | Dial ring to ~4; the floor needs the ARC A optimize arc |
> | Extra churn / pop | **PredictLookahead 3→0** (killed your tap-thrash but made sustained fast-flight rebirths 7.7% vs <1%) | Restore predictive loading (ideally speed-gated so taps don't thrash) |
> | Shadows pop in / blocky far | **NOT changed this session** — pre-existing 6 km CSM cutoff, just made glaring by seeing far past it (ring 8 + 64 km far-clip) | The shadow-first ARC A work (or accept + lean on the horizon march) |
> | "Looks off" | Depth fog + far-clip changed the LOOK (a choice, not a bug) | Keep or drop per taste |
>
> ### ⚠️ Revert is ENTANGLED — do it SURGICALLY, not `git checkout 7fd7344`
> The pre-session baseline commit `7fd7344` **lacks uncommitted cross-chat methods** (`TerrainLab.SetChunkOps`, field-
> cache era) that the working-tree `Cli.cs` references → `git checkout 7fd7344 -- TerrainLab.cs` **breaks the build**
> (tried it; reverted). My commit `e00b380` folded that pre-session uncommitted work into itself. **So: reverse MY
> specific edits from the current `e00b380` files** (preserves cross-chat work), do not wholesale-checkout the parent.
>
> ### DECISION PENDING (for the new chat, with the user)
> Three options were on the table; **user has not chosen**:
> 1. **Targeted revert (recommended):** kill the velocity budget (fixes artifact), dial ring 8→4, restore predictive
>    loading (fixes churn). Keeps the see-far depth-fog look; perf still floor-limited; shadows still need work.
> 2. **Full revert to pre-today:** reverse ALL my edits → R=2, original coupled fog (16 km wall), original streaming.
>    Clean known-good; re-approach view distance in small eye-gated steps later.
> 3. Keep + push straight into the ARC A optimize arc (perf + shadows) on top of the current state.
>
> ### Two real work items for the new chat
> 1. **Decide + execute the revert** (surgical, per above).
> 2. **Shadows** (the user explicitly wants to review these): the 6 km CSM cutoff on wide vistas — redistribute
>    cascades / tune `--shadowdist` / lean on the shipped horizon self-shadow march, as part of the shadow-first
>    ARC A pass. Note `--shadowatlas=4096` did NOTHING to perf here (shadows are not the perf cost — the churn is).
>
> **Safety nets:** tag `viewdist-checkpoint-2026-06-25` + fs backup `C:\Wg16\backups\wg-16-project_viewdist_2026-06-25`.
> Also revert `FlyCamera.InitialSpeed 5000→120` (review aid in `e00b380`). Current ring default in code = **8**.

---

This session executed the **ARC B north-star deep pass** (memory `arcb-infinite-streaming-tuning-northstar`):
*minimum fog, maximum view distance, no perceptible detail loss* — done **AHEAD of the perf arc at the user's
explicit "see much much farther" direction** (overrides the earlier "keep R=2 until post-perf" sequencing; ARC A
now optimizes at the larger radius). It also **RESOLVES Issues 2 & 3** of
`2026-06-24-cdlod-streaming-shadow-known-issues.md` (far-out/fog regression + fast-move churn/blink).

**Checkpoint:** commit `e00b380` on `experiment/presentation`, tag `viewdist-checkpoint-2026-06-25` (NOT pushed).
**Full filesystem backup (incl. the water chat's untracked WIP):** `C:\Wg16\backups\wg-16-project_viewdist_2026-06-25`.

---

## What shipped

### A. See far + minimal NEAR fog (the look goal)
The old fog was exponential and **coupled to the load radius** (`density = 2.8/(LoadRing·8192)` → ~94% occluded
at the boundary), which deliberately hid the load seam with haze and **capped the clear view at ~16 km** — the
exact opposite of the north-star.

- **Fog DECOUPLED + switched to DEPTH mode** (`LightingComposer.cs` fog block ~313 + new fields
  `FogDepthBegin/End/Curve`). Zero fog until **`FogDepthBegin` 22 km**, curved ramp (**curve 3.0** → stays thin
  most of the way) to full at **`FogDepthEnd` 62 km**. Near/mid crystal clear; haze deepens only far out
  ("less up-close fog, deeper farther-away" — user's words). A flat exponential fog cannot do this.
- **Seam hidden by CLIPPING, not fog.** Camera far-clip **30 → 64 km** (`scenes/terrain_lab.tscn:70`), set just
  inside the worst-case loaded edge (`LoadRing·8192`) so the load boundary is always clipped, never seen.
- **AT-2 aerial range 32 → 64 km** in **all three sites that must match the far-clip**:
  `AerialPerspective.cs:57`, `TerrainLabUI.Process.cs:215`, `AtmosphereCompute.cs:50`. (If you change far-clip,
  change all three or distant terrain loses physical haze / mis-colors past the old 32 km.)
- **Load ring default 2 → 8** (`CdlodTerrain.cs:76`; ~65 km worst-case loaded edge; slider + `TerrainLab.SetLoadRing`
  clamp raised to 8). **Cheap:** all fine detail lives inside the ~20 km split disc (`SplitFactor·8192`), so every
  ring cell beyond that is a single coarse 8192 m chunk — leaves grow only ~570 → ~900. Cache slots 1024 → **1280**
  (~90 MB) for the larger active set.

### B. Streaming pop / thrash fixes (closes Issues 2 & 3)
- **Trailing-edge "load in/out/in/out" toggle (the original user report):** root cause = the **velocity-predictive
  bias**. A light tap spikes velocity then it decays to 0 → the biased window-center swings out and back → the
  trailing-edge chunks birth then retire each cycle. **Fix: `PredictLookahead` 3 → 0** (`CdlodTerrain.cs:81`) +
  **`CenterHysteresis` 0.15 → 0.35** (`:77`, wider seam dead-band). With Ring 8 you can't outrun the frontier at
  normal speeds, so prediction is no longer needed — it was only ever a small-ring crutch.
- **Merge despawn-pop (a regression I introduced then reverted):** bumping `RetireGrace` 2 → 10 caused a NEW pop —
  a chunk that merges into a coarser parent lingers `Visible` over its replacement for the grace window (LOD
  overlap → shimmer, then despawn). **Reverted to 2** (≈33 ms, imperceptible). **LESSON: RetireGrace must stay
  low** — it bridges budget-deferred BIRTH holes, it is NOT a thrash fix.
- **Holes at high speed:** `MaxChunkOps=24` births/frame can't fill the frontier above ~12–15 km/s. **Fix:
  velocity-scaled birth budget** (`CdlodTerrain.cs` Tick): `effOps = min(MaxChunkOpsCeil 256, MaxChunkOps 24 +
  speed·ChunkOpsPerSpeed 0.01)`. **Free when slow** (you only birth what you need, so a high ceiling costs nothing
  idle — weak-HW safe); ramps up only when flying fast.

---

## OPEN — needs more review (resume here)
1. **Far chunks still lag at sustained ~25 km/s** (user, mid-review). The velocity-scaled budget helped but isn't
   fully keeping up at boost. Levers, in order: raise `ChunkOpsPerSpeed` / `MaxChunkOpsCeil`; turn on `--streamdbg`
   and check whether far-ring births are deferred vs the near LOD-splits eating the budget; **scale the field-cache
   `MaxRequestsPerFrame` with speed** (at 25 km/s far chunks fall back to live field-eval — fps cost, not holes).
2. **EYE-GATES OWED** (user judges look): (a) the near-clear / far-heavy depth-fog feel (`fog start distance`
   slider + the `FogDepthEnd`/`FogDepthCurve` fields); (b) the 64 km far-clip edge — fully hidden in all directions
   by aerial+depth fog? (c) the renderOrigin snap-pop at the larger radius.

## NOT touched (deliberate — don't "fix" without an eye-gate)
- **renderOrigin snap-pop / float precision** — orthogonal to view distance (precision is bound by camera distance
  from world origin, NOT by view radius). If it reads at distance, the robust fix is a **double-precision domain
  split** (pass each chunk's world origin as hi/lo, sample noise chunk-relative) — highest-effort; gate on an
  eye-test first. Memory `cdlod-renderorigin-snap-pop`.
- **Shadows** — CSM max stays ~6 km; rely on the shipped horizon self-shadow march (`field_macro_height`) for long
  range. Do NOT extend CSM (dilutes near texel density → worse near shadows). Memory `terrain-horizon-shadows-built`.
- **`FlyCamera.InitialSpeed = 5000`** rode along in commit `e00b380` as a no-mouse review aid — **revert to 120**
  before any real finalize.

## Live knobs (Debug tab + CLI)
- `CDLOD load ring` (1–8) / `--loadring=N` — dial DOWN for weak HW.
- `fog start distance` (repurposed from the now-dead `fog vs load-radius` slider) → sets `FogDepthBegin`.
- `predictive load` default 0 / `--lookahead=` — **leave off** (any value re-enables the tap-thrash).
- `--chunkops=N` sets the budget BASE (velocity scaling rides on top).
- No-slider levers (composer/terrain fields): `FogDepthEnd`, `FogDepthCurve`, `MaxChunkOpsCeil`, `ChunkOpsPerSpeed`.

## Tension to carry into ARC A (perf)
This pass GREW the radius (R=8) the perf arc was told to optimize at R=2 — done on user direction. **ARC A now
inherits R=8 + a 64 km far-clip.** The Task-3 "fog-coarsen the far ring" synergy is moot now (depth fog, no
coupling), so the far ring is full-LOD-cheap-coarse already. Re-profile `--profmove` at R=8 before the shadow-atlas
dial-down. The 25 km/s live-eval fallback (open item 1) is squarely ARC A's territory.
