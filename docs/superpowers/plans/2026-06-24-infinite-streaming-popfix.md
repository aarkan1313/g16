# Infinite-streaming pop-in fix — Implementation Plan

> **For agentic workers:** implement task-by-task. No unit tests (Godot lab) — verification is build-green +
> the mechanical `--*check` gates staying PASS + a drift-free in-motion EYE-GATE (the user judges the look).
> Steps use checkbox (`- [ ]`) tracking.

**Goal:** Kill the visible "regions load/pop in" on the infinite CDLOD terrain via a larger load ring, boundary
hysteresis, load-radius-matched fog masking, and velocity-predictive loading.

**Spec:** `docs/superpowers/specs/2026-06-24-infinite-streaming-popfix-design.md` (read it first).

**Architecture:** all four pieces are CDLOD-streaming changes in `CdlodQuadtree.cs` / `CdlodTerrain.cs` +
`TerrainLabUI.Process.cs` (camera velocity) + the aerial-fog coupling. Migration-shim era is over; these are
direct edits to the streaming core — go carefully, gate every step.

## Global Constraints

- **Assume CDLOD is ON** (`--cdlod=1`, or after the perf arc's default-on flip). Profile/test with `--cdlod=1`.
- **The mechanical gates MUST stay PASS** after each task: `--cdlodcheck --stitchcheck --streamcheck --popcheck
  --snapdiff`. Run the relevant ones per task.
- **Drift-free A/B only** for eye-gates (frozen time / single launch). Fly in-motion (`--profmove` or live) —
  never judge a streaming artifact from a still.
- **Do NOT touch the water chat's uncommitted files** (`scripts/hydrology/*`, `shaders/water_surface.gdshader`).
- **Re-profile after each task** (`--cdlod=1 --profmove --profile=4`) — this expands the chunk set, and the
  perf arc inherits the result. Log the delta.
- Commit per task on `experiment/presentation`.

---

### Task 1: Configurable load ring radius (R)

**Files:**
- Modify: `scripts/lab/CdlodQuadtree.cs` (`SelectRoaming`)
- Modify: `scripts/lab/CdlodTerrain.cs` (add `LoadRing` field; pass it to the quadtree or expose a setter)
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (`--loadring=N`) + `data/lab_controls.json` (a Debug-tab slider)

**Interfaces produced:** `CdlodQuadtree.SelectRoaming` honors a ring radius R; `CdlodTerrain.LoadRing` (int,
default 2) drives it.

- [ ] **Step 1:** In `CdlodQuadtree`, add a `Ring` field (default 1 to preserve current behavior until set) and
  change `SelectRoaming`'s loops `for (int dz=-1; dz<=1; ...)` → `for (int dz=-Ring; dz<=Ring; ...)` (and dx).
- [ ] **Step 2:** In `CdlodTerrain`, add `public int LoadRing = 2;` and push it to the quadtree's `Ring` in
  `Setup`/`Tick` (so a live change takes effect). Default 2 = 5×5.
- [ ] **Step 3:** CLI `--loadring=N` (`TerrainLabUI.Cli.cs`, use `MatchFlag`) → sets `_terrain` LoadRing; a
  Debug-tab slider in `data/lab_controls.json` (int 1..4) wired in the registry (`setter`/`field` case).
- [ ] **Step 4 (gate):** `dotnet build` (0 errors). Run `--cdlod=1 --cdlodcheck`, `--stitchcheck`,
  `--streamcheck`, `--snapdiff` → ALL PASS at R=2 (the neighbor/stitch invariant must hold at the new outer
  ring). If a gate fails, the ring outer edge breaks the invariant — fix `LeafSizeAt`/`FillStitchMasks` to cover
  the full ring before proceeding.
- [ ] **Step 5 (eye + perf):** `--cdlod=1 --profmove --profile=4` (log avg/worst delta vs R=1). Live-fly: the
  pop boundary should be much further out. Commit.

### Task 2: Window-center hysteresis (anti-thrash)

**Files:** Modify `scripts/lab/CdlodTerrain.cs` (or `CdlodQuadtree.cs` where the center cell is chosen).

- [ ] **Step 1:** Track `_lastCenterCell` (Vector2I) on the terrain. Compute the camera's cell; only advance
  `_lastCenterCell` to it when the camera is more than `_rootSize * H` (H=0.15) past the current center-cell
  boundary in that axis. Pass `_lastCenterCell` as the selection center (NOT the origin/snap math — leave that
  on the existing `floor` grid so reconstruction round-trips; selection is a superset).
- [ ] **Step 2 (gate):** `--snapdiff` MUST stay PASS (the origin math is untouched). `--streamcheck` PASS.
- [ ] **Step 3 (eye):** Hover/oscillate the camera across a cell boundary — no strip should flicker load/unload.
  Commit.

### Task 3: Load-radius-matched fog masking

**Files:** Modify the aerial/fog config — `scripts/lab/AerialPerspective.cs` / the env fog setters in
`LightingComposer.cs` or `TerrainLabUI.Apply.cs`; couple to `CdlodTerrain.LoadRing * _rootSize`.

- [ ] **Step 1:** Define a single "view distance" = `LoadRing * _rootSize` (≈ the load radius). Drive the env
  fog far-distance + AT-2 aerial strength from it so the far load boundary is fully occluded and fades up as the
  camera approaches.
- [ ] **Step 2:** (optional perf synergy, flag for the perf arc) allow the fully-fogged far ring to select a
  coarser LOD — note it, don't necessarily build it here.
- [ ] **Step 3 (eye-gate — THE key one):** fly hard toward the boundary. The far terrain must FADE UP out of
  haze, never snap. A/B with fog off to confirm the masking is doing the work. Commit.

### Task 4: Predictive (velocity-biased) loading

**Files:** Modify `scripts/lab/TerrainLabUI.Process.cs` (camera velocity → `CdlodTick`), `scripts/lab/TerrainLab.cs`
(`CdlodTick` signature), `scripts/lab/CdlodTerrain.cs`/`CdlodQuadtree.cs` (apply the lookahead to the center).

- [ ] **Step 1:** In `_Process`, compute camera XZ velocity (delta-position / delta, smoothed). Thread it into
  `CdlodTick(camPos, vel)` → `CdlodTerrain.Tick` → `SelectRoaming`.
- [ ] **Step 2:** Shift the selection center forward: `center = floor((cam + vel*lookahead) / _rootSize)`
  (lookahead ≈ one cell of travel time). Keep it a contiguous cell block (neighbor invariant holds).
- [ ] **Step 3 (gate + eye):** gates PASS; fly FAST in one direction — the loaded terrain stays ahead of the
  camera (no outrunning). Commit.

## Self-review (run after drafting; fix inline)

- Coverage: all four spec mechanisms have a task. ✓
- Type consistency: `LoadRing` (int) and the velocity (Vector2/3) signatures match across `Process → TerrainLab
  → CdlodTerrain → CdlodQuadtree`.
- Gate discipline: every task ends on the `--*check` gates + an in-motion eye-gate; no task judged from a still.

## After this feature → the perf arc

This intentionally grows the chunk set + shadow coverage. Hand the re-profiled numbers
(`docs/performance.md`) to `docs/handoffs/2026-06-24-profile-optimize-start-here.md` — the shadow-spike work
now also has to cover the larger radius (the fog-coarsening synergy in Task 3 is the lever).
