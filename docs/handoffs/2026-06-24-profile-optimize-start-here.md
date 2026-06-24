# Profile + Optimize SEVERELY — start here (2026-06-24)

**User directive:** "before we go back to water we need to profile and optimize severely."
**Read first:** `docs/performance.md` (top section, 2026-06-24) + `docs/HANDOFF.md` §6.

---

## The headline (this reframes the whole perf story)

The long-standing **"frame is terrain-mesh-bound at ~27.5 ms"** conclusion — which drove the entire perf
narrative in `performance.md` — was measured on the **single 4.19M-vert mesh (CDLOD OFF)**, which is **NOT the
shipping streaming path.** With CDLOD on (the real game), the in-motion frame is **~6.5 ms avg.** The problem
is no longer a mesh *floor*; it's the **worst-case SPIKES in motion**, and they are **shadow-map-dominated.**

CDLOD-on, in-motion decomposition (RTX 5090 laptop, uncapped, `--cdlod=1 --profmove --profile=4`; mid-range ≈ ×2.5–4):

| Config (CDLOD on, moving) | avg ms (fps) | worst ms (fps) | reading |
|---|---|---|---|
| **default (all on)** | **6.5 (153)** | **20.0 (50)** | the real shipped game |
| clouds off | 6.1 (165) | 18.1 (55) | clouds ≈ 0.4 avg / ~2 ms of the spike |
| shadows off | 5.2 (194) | **10.2 (98)** | **shadows ≈ 1.3 avg / ~10 ms of the WORST-CASE SPIKE** |
| clouds + shadows off | 4.5 (221) | 8.5 (117) | base = mesh raster + fragment + CDLOD/async-AABB |
| CDLOD **OFF** (single mesh = launch default) | 25.1 (40) | 28.8 (35) | the old "27.5 ms floor" — not the ship path |

## The arc (ranked)

0. **⚠ FIRST: make CDLOD default-on.** `terrain_lab.tscn` ships with `CdlodTerrain._enabled=false` and nothing
   calls `SetCdlod` at launch → a bare launch is the slow 25 ms single mesh (this is why the user said "it
   wasn't infinite"). Enable CDLOD at the end of `TerrainLabUI._Ready` unless `--cdlod=0` overrides. ~3 lines.
   This alone flips the default 25 → 6.5 ms. (`SetCdlod` is in `TerrainLab.cs:163`; the CLI hook is
   `TerrainLabUI.Cli.cs:245`, currently gated on `_cdlodCli >= 0` with default `-1`.)

1. **Kill the 20 ms shadow spike (biggest lever, ~10 ms of it).** The 8192² directional atlas re-rasters the
   CDLOD chunks as they're born/morph in motion. Levers, cheapest-first:
   - **Atlas dial-down 8192 → 6144 / 4096** (owed since 2026-06-22; measure the edge-quality cost — eye-gate).
   - **Amortize the cascade re-raster** across frames (stride the far cascades; risks shadow lag in motion).
   - **Drop the finest CDLOD level from the far shadow cascades** (far shadows are low-frequency).
   - Re-check `LightingState.SunDisc.ShadowMaxDist` (currently 6000) — the horizon-shadow memory says cascade
     RANGE was a no-op for *look*, but it may still drive the *cost*.

2. **Clouds spike (~2 ms of it):** temporal-stride lever (`--temporal=N`); the sky lane is already efficient
   (~2 ms total) so this is the only cloud lever worth pulling here.

3. **Base spike (~8.5 ms):** chunk-birth / async-AABB tighten (`ChunkAabbProvider`) / floating-origin snap. The
   async AABB path is already correct (see audit #8 — it's fenced + render-thread + throttled). The snap is
   seamless (`--snapdiff` PASS). Profile chunk-birth churn during fast translation; the identity-keyed pool was
   the prior fix (memory `cdlod-rebuild-spike-rootcause`) — re-measure before trusting the recorded diagnosis.

## How to reproduce / probe

`<godot_console.exe> --path C:/Wg16/wg-16-project -- --cdlod=1 --profmove --profile=4 [probe]`
Probes: `--clouds=0/1 --shadow=0/1 --sdfgi=0/1 --ssao=0/1 --ar=0/1 --temporal=N --cloudtex=H`.
The `--profmove` orbits the camera (motion cost); `--profile=N` uncaps fps + vsync. Single wild outliers =
thermal hitches; reproduce before trusting.

## Constraints (don't trip these)

- **Drift-free A/B only** (the SSIL-misdiagnosis lesson): freeze time / single launch when comparing looks.
  Two launches drift (day cycle + cloud temporal). Use `--fillab`/`--godrayab` style frozen harnesses.
- **Don't reduce the look without an eye-gate** — the user's eye is the only gate for look; mechanical checks
  gate cost. Every shadow dial-down needs a motion eye-check (acne/penumbra). Default to the approved look.
- **Don't touch the water chat's UNCOMMITTED files** (`scripts/hydrology/{RiverRibbonMesh,LakeMesh,WaterRenderer,
  RegionHeightGrid}.cs`, modified `WorldWaterRegion.cs`, `shaders/water_surface.gdshader`). Note: the committed
  `Cli.cs` already references `WaterRenderer`/`WorldWaterRegion`, so origin doesn't build standalone without
  them — coordinate with that chat; do NOT `git stash`/`checkout` broadly (it reverts their work).
- **Verify behaviour-neutral wins with the gates** (`--luminarycheck`/`--shadowcheck`/`--cdlodcheck` etc.).
  Note `--luminarycheck` reports FAIL with clouds on (floor=214) — that's cloud-temporal noise unfrozen by
  TimeScale=0, NOT a regression (fails on baseline; passes clouds-off). Don't chase it.

## Then (after the perf arc)

Water (resolve the uncommitted ribbon-river WIP, brainstorm ONE approach before grinding — STOP criterion) /
**biomes** (terrain is biome-ready: a manifest = a biome) / relight #2 long-range cast shadows.
