# CDLOD Worst-Frame Reduction Roadmap - 2026-06-26

## Objective

Get the current high-speed terrain, shadow, and cloud stack into a repeatable sub-6 ms average posture while cutting the visible worst-frame spikes that show up at 5 km/s to 25 km/s testing speeds.

The average frame is already close enough to be useful. The next problem is not "make everything cheaper" in the abstract; it is identifying which frames spike, what CDLOD/shadow/cloud state changed on those frames, and then moving the expensive work out of one-frame bursts.

## Non-negotiables

- Preserve current visual quality by default. Do not reduce knobs or silently lower quality settings to win a profile.
- Keep the high-speed controls. The 25 km/s case is a stress tool, not the normal design speed, but the engine should expose tunable systems that can survive it better.
- Keep coverage graceful. Missing detail may be temporarily hidden by coarse terrain, but it should refine into the real chunk set instead of popping from void to detailed terrain.
- Treat single worst frame as a clue, not a pass/fail metric. Use p95, p99, max, and top spike context.
- Keep shadow LOD visually monotonic: shadows should already exist in a coarse form, then gain detail as the camera approaches.
- Leave unrelated water/hydrology work and untracked artifacts alone.

## Current State

- CDLOD is default-on and can now fly much farther before exposing empty coverage.
- A two-layer coverage underlay hides high-speed holes without casting stale shadows.
- Speed-scaled retire grace keeps old visual chunks alive briefly at very high speed.
- Average frame time at 5 km/s and 25 km/s is near the target on the current machine, but max worst frames are still noisy and sometimes large.
- Lowering bake request throughput blindly made backlog explode, so the next pass needs smarter scheduling, not lower throughput.
- Task 1 telemetry is live: `--profile` now reports p50/p95/p99/top spikes and optional CSV output.

## Baseline After Task 1 Telemetry

Measured on 2026-06-26 with CDLOD default-on:

| Command | avg | p50 | p95 | p99 | worst | Read |
|---|---:|---:|---:|---:|---:|---|
| `--profile=5 --profmove --profspeed=5000` | 5.9 ms | 5.8 ms | 7.1 ms | 7.5 ms | 13.5 ms | normal-speed p99 is controlled; top spike had no missing detail and no snap |
| `--profile=5 --profmove --profspeed=25000 --profilelog=artifacts/profile_25kms_telemetry.csv` | 5.7 ms | 5.6 ms | 7.1 ms | 8.3 ms | 29.3 ms | stress max was not a snap; it aligned with saturated births, 54 retires, 78 missing chunks, and bake backlog 4531 |

Early conclusion: snap fan-out still needs review, but the first measured stress spike points harder at scheduler/backlog/retire burst behavior than origin snaps.

## Milestones

### M1 - Profiler Truth

Add p50, p95, p99, max, and top spike reporting to `--profile --profmove`. Include per-spike CDLOD state: snap flag, selected leaves, active chunks, births, retires, bake backlog, cache-ready backlog, effective birth budget, retire grace, and cumulative snaps/births. Add optional `--profilelog=<csv>` for offline comparisons.

Acceptance:

- `PROFILE:` includes avg, p50, p95, p99, worst, and frame count.
- `PROFILE-SPIKES:` explains the top outlier frames.
- `--profilelog=<csv>` writes one row per measured frame when requested.
- Existing build and CDLOD/shadow gates stay green.

### M2 - Render-Origin Snap Fan-Out

Remove or reduce the one-frame "touch every active chunk" cost when render origin snaps. The likely long-term shape is a parent render-frame transform or equivalent shared offset so one transform changes instead of hundreds of chunk instances being re-positioned and re-AABB'd.

Acceptance:

- `--snapdiff` stays PASS.
- Spike telemetry shows snap frames no longer dominate top outliers.
- Visual motion across an origin snap remains seamless.

### M3 - Adaptive Streaming Scheduler

Replace blunt birth/cache tuning with a scheduler that prioritizes chunks likely to matter: near chunks first, then long-lived incoming chunks, with stale or low-value work deprioritized. The underlay should buy time; the scheduler should spend that time on the right detail.

Acceptance:

- Bake backlog does not grow unbounded during 25 km/s stress.
- Active detail converges after speed settles.
- No flat/stale chunk data appears from cache slot races.

### M4 - Shadow Caster LOD Policy

Make CSM caster selection ring-based and LOD-aware. Coarse terrain should cast at distance, finest detail should cast near the camera, and retained/underlay fallback should remain shadow-off unless explicitly enabled for diagnosis.

Acceptance:

- Shadows do not appear from nothing as the camera approaches.
- Far shadows read coarse first, then refine.
- `--shadowcheck` stays PASS.
- Shadow spike contribution drops or becomes bounded.

### M5 - Acceptance Sweep

Run a repeatable mechanical and visual sweep at normal and stress speeds.

Acceptance:

- 5 km/s: avg at or under 6 ms, p99 materially below current worst-frame spikes.
- 25 km/s: avg at or under 6 ms where possible on the current rig, p99 bounded, max outliers explained by spike telemetry.
- No gray voids, no large shadow pop, no quality knobs removed.
- Final live visual pass in `review.tscn` or `terrain_lab.tscn`.

## Working Gates

Use these after any change touching CDLOD, shadows, or profiler code:

```powershell
dotnet build WG16.csproj --nologo
```

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --morphcheck --popcheck --snapdiff --stitchcheck --streamcheck --cdlodcheck --fieldcheck
```

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --shadowcheck
```

```powershell
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=5000
Godot_v4.6.2-stable_mono_win64_console.exe --path C:/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --profile=5 --profmove --profspeed=25000 --profilelog=artifacts/profile_25kms.csv
```
