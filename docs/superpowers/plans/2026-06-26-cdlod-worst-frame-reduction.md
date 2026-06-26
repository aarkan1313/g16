# CDLOD Worst-Frame Reduction - Execution Plan

> Implement task-by-task. This is a visual/performance arc, so mechanical gates prove safety and the user's live eye remains the look gate. Do not reduce quality defaults or remove tuning knobs to win a number.

**Roadmap:** `docs/handoffs/2026-06-26-cdlod-worst-frame-roadmap.md`

**Goal:** Cut CDLOD/shadow/cloud worst-frame spikes while preserving the current look, high-speed test controls, and the "coarse now, detailed later" terrain refinement behavior.

## Global Constraints

- CDLOD stays default-on.
- `--profspeed=25000` is a stress case, but it must remain supported as a tunable test path.
- Underlay and retained chunks are coverage fallbacks, not a substitute for detail convergence.
- Retained chunks and underlay stay shadow-off by default.
- Do not judge motion artifacts from stills.
- Do not stage `artifacts/` unless explicitly requested.

## Task 1 - Profile Spike Telemetry

**Files:**

- Modify: `scripts/lab/CdlodTerrain.cs`
- Modify: `scripts/lab/LabCliSequences.cs`
- Optionally modify: `scripts/lab/TerrainLabUI.Cli.cs` if a CSV/log flag is added

**Steps:**

- [x] Add a per-frame CDLOD stats snapshot on `CdlodTerrain`.
- [x] Track profile samples after the warmup window.
- [x] Add optional `--profilelog=<csv>` for one-row-per-frame analysis.
- [x] Print avg, p50, p95, p99, worst, and frame count.
- [x] Print the top spike frames with CDLOD context: snap, leaves, active, births, retires, bake backlog, cache-ready backlog, effective near budget, retire grace.
- [x] Keep the old `PROFILE-STREAM:` summary for cumulative totals.

**Gates:**

```powershell
dotnet build WG16.csproj --nologo
```

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=25000 --profilelog=artifacts/profile_25kms.csv
```

**Acceptance:**

- The profile output names p95 and p99.
- The top spikes include enough CDLOD state to tell whether a spike aligns with a snap, births, retire burst, bake backlog, or shadow/render count.
- `--profilelog=<csv>` writes a machine-readable profile when requested.
- No visual behavior changes.

**Result 2026-06-26:** complete. At 25 km/s, avg 5.7 ms, p95 7.1 ms, p99 8.3 ms, worst 29.3 ms. The worst sample was `snap=0`, `births=24+4`, `retires=54`, `missing=78`, `bakePend=4531`, which points the next optimization pass toward adaptive scheduling/backlog/retire-burst handling before snap fan-out.

## Task 2 - Snap Fan-Out Reduction

**Files:**

- Modify: `scripts/lab/CdlodTerrain.cs`
- Possibly modify: terrain scene/root transform plumbing if the parent-frame approach is used

**Steps:**

- [ ] Use Task 1 output to confirm whether snap frames appear in top spikes.
- [ ] Replace per-slot snap re-positioning with a shared render-frame transform or equivalent shared offset.
- [ ] Keep world-coordinate reconstruction bit-identical across snaps.
- [ ] Re-run `--snapdiff` and a high-speed profile.

**Gates:**

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --snapdiff --streamcheck --popcheck
```

**Acceptance:**

- `--snapdiff` remains PASS.
- Snap frames stop showing as broad active-chunk update spikes.
- Live motion across cell boundaries remains seamless.

## Task 3 - Adaptive Detail Scheduler

**Files:**

- Modify: `scripts/lab/CdlodTerrain.cs`
- Modify: `scripts/lab/ChunkFieldCache.cs` only if cache queue cancellation/prioritization needs internal support

**Steps:**

- [x] Use spike/backlog telemetry to separate birth cap, bake cap, and retire churn failures.
- [~] Prioritize near and long-lived chunks. Nearest-first already exists; long-lived prediction remains open.
- [x] Avoid spending cache work on chunks that are likely to retire before the bake lands.
- [x] Keep `--bakereq`, `--chunkops`, `--farops`, speed gain, and priority knobs available; add `--cachepend` and `--cacheradius`.

**Gates:**

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=5000
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=25000
```

**Acceptance:**

- Bake backlog stays bounded or drains after stress movement.
- Detail fills closest first and does not expose void.
- No stale/flat cache-slot visual artifacts.

**Result 2026-06-26 Task 3a:** cache scheduler slice complete. Final 25 km/s run: avg `5.8 ms`, p95 `6.7 ms`, p99 `8.2 ms`, worst `15.2 ms`; backlog capped near `450` instead of `4531`. Normal-speed worst improved from `13.5 ms` to `7.8 ms`. Remaining 25 km/s max aligns with capped births, missing detail, and retire work, not snap or cache backlog. A stricter `--cachepend=256` improved p99 but worsened max; `--chunkopsgain=0.001` ballooned active chunks and should stay off by default.

## Task 4 - Shadow Caster LOD Rings

**Files:**

- Modify: `scripts/lab/CdlodTerrain.cs`
- Possibly modify: lighting/shadow CLI controls if a new diagnostic knob is needed

**Steps:**

- [ ] Split shadow caster policy by distance and CDLOD level.
- [ ] Ensure coarse casters exist before fine detail arrives.
- [ ] Keep far shell, underlay, and retained fallback shadow-off by default.
- [ ] Measure shadow-on and shadow-off profiles with the new p99 output.

**Gates:**

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --shadowcheck
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=25000
```

**Acceptance:**

- Shadows refine rather than appearing from nothing.
- Near shadow quality does not regress.
- p99 and top spike output show shadow work is bounded.

## Task 5 - Final Sweep

**Steps:**

- [ ] Run build.
- [ ] Run CDLOD mechanical gate bundle.
- [ ] Run `--shadowcheck`.
- [ ] Run 5 km/s and 25 km/s profiles.
- [ ] Run `--visualcheck=artifacts/visualcheck_default.png` for a mathematical rendered-frame sanity gate.
- [ ] Capture a live visual pass if the change touched rendering behavior.
- [ ] Commit scoped changes and push.

**Acceptance:**

- The repo contains current numbers and a clear read on remaining spikes.
- The high-speed path is tunable, not hard-coded around one machine.
- The visual pass is backed by a gross-regression metric and left ready for live review.
