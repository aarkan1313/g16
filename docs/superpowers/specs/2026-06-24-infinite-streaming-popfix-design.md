# Infinite-streaming pop-in fix — Design

**Date:** 2026-06-24
**Status:** APPROVED 2026-06-24 — decisions resolved: COUPLE fog↔load-radius; build it modular + tunable;
pillar-led on every choice (lead with the better option, expose the knobs).
**Goal:** Eliminate the visible "regions load/pop in" as you fly the infinite CDLOD terrain — by loading a
larger radius, adding boundary hysteresis, masking the far load boundary with fog that clears as you approach,
and biasing loading toward the player's movement direction.

## Background — the exact mechanism (verified in code)

`CdlodTerrain.Tick` → `CdlodQuadtree.SelectRoaming(camPos)` (`scripts/lab/CdlodQuadtree.cs:58`):
- The world is tiled into `_rootSize`-metre cells (`_rootSize` = `RegionSizeM`, currently **8192 m**).
- Each frame it selects leaves over a **3×3 block of root cells centered on the camera's cell**, cell-aligned
  via `floor(camX / _rootSize)`. So the loaded window is ~24 km wide, BUT because it's cell-aligned the
  *guaranteed load-ahead* drops to ~1 cell (8192 m) just before the camera crosses a cell boundary.
- **Crossing a cell boundary (every 8192 m of travel) shifts the 3×3 block by one cell** → a whole 8192 m strip
  of (coarse) terrain appears at the leading edge and unloads at the trailing edge. **This strip pop is the
  bug.** The renderOrigin also snaps on the same 8192 m grid (`_coarseSnap = _regionSize`); `--snapdiff` PASSES
  (the snap itself is seamless), so the visible pop is the *load-window shift*, not the snap.
- Far chunks are already coarse (CDLOD LOD), so loading more of them is comparatively cheap.

Two related defaults the user hit (documented, not bugs): **CDLOD is off by default** (a bare launch shows the
finite single mesh) and the **review 1-9 keys are `review.tscn`-only**. The CDLOD-default-on flip is step 0 of
the *perf* arc (`docs/handoffs/2026-06-24-profile-optimize-start-here.md`) — assume CDLOD is on for this work.

## Design — four mechanisms

### 1. Expand the load radius (ring radius R)

Replace the hard-coded 3×3 in `SelectRoaming` with a configurable **ring radius `R`** (loop `dz,dx ∈ [-R, R]`
→ a (2R+1)² block). `R=1` = today's 3×3; **default `R=2`** = 5×5 ≈ ±16–40 km loaded. This moves the pop
boundary out past the view/fog distance. `R` is a public field on `CdlodTerrain` (lab-tunable + CLI
`--loadring=N`). Cost scales with the ring area but the *added* cells are the farthest/coarsest → cheap-ish;
re-measured in the perf arc.

Interaction: `SelectRoaming`, `LeafSizeAt`, and `FillStitchMasks` must agree on the loaded set so the
neighbor/stitch invariant still holds at the new outer ring. `LeafSizeAt` already roots at the cell containing
any probe point (infinite), so it's unaffected; verify `--cdlodcheck`/`--stitchcheck`/`--streamcheck` still
PASS with `R=2`.

### 2. Hysteresis on the window center

Today the window center is `floor(cam / _rootSize)` — deterministic, but it re-centers the instant the camera
crosses a boundary, so oscillating across a boundary thrashes a strip load/unload. Add a **dead-band**: keep
the current center cell until the camera is more than `_rootSize * H` past the boundary (default `H = 0.15`,
~1.2 km). Store the last center cell on `CdlodTerrain`; only advance it when the camera exceeds the band.

CRITICAL: the renderOrigin snap and `LeafSizeAt` both use `floor(cam/_rootSize)` and MUST stay consistent with
the (now hysteretic) window center, or the floating-origin reconstruction won't round-trip (re-introducing the
snap-pop). Option: keep the snap grid as-is (snapdiff already passes) and apply hysteresis ONLY to which cells
are *selected*, not to the origin math — selection is a superset (the ring), so a slightly-stale center just
keeps an extra trailing ring loaded, which is harmless. Verify `--snapdiff` stays PASS.

### 3. Fog/haze masking that clears as you approach

Tune the existing **AT-2 aerial perspective** (`AerialPerspective` / `atmosphere_aerial.glsl`) + the env fog so
the far load boundary is fully occluded by haze and **resolves smoothly as the camera closes the distance** (no
hard edge). The fog "full-occlusion" distance should be **derived from the load radius** (≈ `R * _rootSize`)
so the two stay matched when `R` changes — expose a single "view distance" that drives both the load ring and
the fog far-plane. Synergy: anything fully fogged can be loaded at a coarser LOD (barely visible) → a perf win,
not just a mask. No new render effect; this is tuning + a coupling between the load radius and the fog distance.

Eye-gate: fly toward the boundary at speed — the far terrain must fade UP out of haze, never snap in.

### 4. Predictive directional loading

Bias the loaded ring toward the camera's movement so the player can't outrun the loader. `TerrainLabUI.Process`
already has the camera; compute its **XZ velocity** (or use the frame delta-position) and pass it to `Tick`.
Shift the window-center cell **forward by a velocity-lookahead** (e.g. `center = floor((cam + vel*lookahead) /
_rootSize)`, default lookahead ~1 cell of travel), OR load an **asymmetric ring** (extra cells in the +velocity
direction, fewer behind). Recommended: the lookahead shift (simplest, reuses the ring). Must still satisfy the
neighbor invariant (the shifted window is still a contiguous cell block).

## Non-goals / out of scope

- The CDLOD-default-on flip (it's the perf arc's step 0; assume CDLOD on here).
- The shadow-spike optimization (perf arc).
- Any change to the per-vertex geomorph or edge-stitch (those are pop-free already; this is the *outer load
  boundary*, a different seam).
- A brand-new fog/volumetric effect — reuse AT-2 aerial + env fog.

## Verification

No unit tests (Godot lab). Per piece: build green; the mechanical gates stay PASS at `R=2`
(`--cdlodcheck`/`--stitchcheck`/`--streamcheck`/`--popcheck`/`--snapdiff`); and the load-side behavior is a
**drift-free in-motion eye-gate** — fly hard in one direction and confirm NO strip pops in at the boundary (the
far terrain fades up out of haze). Capture a before/after `--profmove` shot/clip. Re-profile (`--profile=4
--profmove`) so the perf arc inherits the true expanded-radius cost.

## Decisions (RESOLVED 2026-06-24 — "couple, modular, tunable, pillars")

**Build ethos (applies to all four mechanisms):** each piece is its own **modular** unit (the load ring lives
in the quadtree, fog coupling in the lighting/aerial path, prediction in the tick) — swap or tune one without
disturbing the others. Everything user-facing is **tunable** (a lab slider + a CLI flag). **Pillar-led:** lead
with the more-correct option, not the cheap one; expose the knob rather than hard-code a guess.

1. **Load ring `R`** — **tunable**, default **2** (5×5). Field on `CdlodTerrain` + `--loadring=N` + Debug-tab
   slider. The perf arc dials it against the shadow cost; the default is just a starting point.
2. **Fog ↔ radius — COUPLED.** One **"view distance" = `LoadRing * _rootSize`** drives BOTH the load ring and
   the fog far-plane, so they can never desync when `R` changes. **Still tunable:** a `fog_view_scale` /
   onset-bias knob sits on top (coupled baseline × user scale) so you can dial fog density/onset relative to
   the radius without breaking the coupling. This is the pillar-correct choice — the masking is *defined by*
   what's actually loaded, not a second guess that drifts.
3. **Predictive — velocity-lookahead shift**, with a **tunable lookahead** knob. It achieves the asymmetry
   (loads more ahead / less behind — the perf win the explicit asymmetric ring offered) by simply moving the
   symmetric ring forward by `vel * lookahead`, with far less code/coupling. The pillar + YAGNI call: same
   outcome, more modular. If a future need wants independent ahead/behind radii, the ring is already
   parameterized (Task 1) so it's a small extension.
