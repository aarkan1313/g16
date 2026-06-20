# Spec: Stage 4 — Auto Day/Night Cycle + Fantasy/Exotic (Sky lane #4)

Date: 2026-06-20. Status: DESIGN (brainstorm complete, user-approved). Owner: Sun/Light lane.
Parent: ROADMAP ▸ ☀️🌙 FINISH THE FULL SKY SYSTEM ▸ #4. Follows #2 (Clouds) + #3 (Atmosphere). Builds
entirely on the Stage-3 time/celestial machinery + the established preset systems.

## Why

`time_of_day` (0–24) already drives the whole sky (sun arc, day/night color, moon, stars) but is set
**manually**. Stage 4 (a) makes time **advance automatically** (a day/night clock — play/pause/speed/scrub)
so the world feels alive, and (b) adds **fantasy/exotic** skies by orchestrating the **existing** exotic
levers (sun fantasy color, celestial `exotic`, atmosphere alien-air, grade tint) into one-click alien looks
— no new celestial bodies (multiple suns/moons is deferred to its own spec).

## Scope

IN: an auto time clock (running flag, speed, wrap at 24 h, play/pause/scrub) driving the existing
`DriveTime`; UI (play toggle + speed slider) + `--autotime=<speed>` CLI; **fantasy sky presets** — a
cross-system preset layer composing sun + celestial (+ atmosphere if present) + grade into exotic skies
(blood-moon, twin-color dusk, green/purple alien sky). OUT (own later spec): **multiple suns/moons**
generalization (the celestial system stays single sun + single moon); new physical phenomena.

## Architecture — a small clock + a cross-system preset layer

```
TerrainLabUI (Process/Lighting) — auto cycle:
  fields TimeRunning (bool), TimeSpeed (hours per real-second). In _Process, when running:
    _time.TimeOfDay = wrap24(_time.TimeOfDay + dt * TimeSpeed); DriveTime(_time.TimeOfDay).
  Pause/scrub = the existing time_of_day slider (manual still works; play resumes from current). No new
  rendering — just advances the existing Time axis. (Sun moves each frame → realtime sky radiance rebakes
  each frame as intended; the known benign radiance-rebake log lines recur while playing — cosmetic.)
  Controls: a 'play day/night' toggle + 'cycle speed' slider (Night/Light tab, registry-routed);
  --autotime=<speed> starts the clock at launch.

scripts/lab/TerrainLabUI.FantasyPresets.cs (NEW) + data/fantasy_presets.json — a CROSS-SYSTEM preset:
  each fantasy preset names a sun preset + celestial preset (+ atmosphere preset if #3 in) + grade tint +
  any direct overrides, and fans out to the existing appliers (ApplySunPreset / ApplyCelestialPreset /
  [ApplyAtmospherePreset] / grade). Data only — no new rendering. Night-tab (or a Sky-tab) picker +
  --fantasy=N CLI. Mirrors the per-feature preset pattern (sun/celestial/cloud/atmosphere).
```

### Build order (sub-phases — each its own eye-gate; build one past the last pass)

1. **ST4-1 — Auto day/night cycle.** The clock (running/speed/wrap) + play toggle + speed slider +
   `--autotime`. Gate: pressing play smoothly cycles dawn→day→dusk→night→dawn at a tunable speed, no pops
   at the 24→0 wrap or the dawn/dusk seams; pause/scrub still work; cost acceptable in motion (`--profmove`).
   **Buildable now** (no dependency on #2/#3).
2. **ST4-2 — Fantasy/exotic presets.** `fantasy_presets.json` + cross-system applier + picker + `--fantasy`.
   Presets: e.g. `blood_moon`, `twin_dusk`, `alien_green`, `violet_night`, `harvest`. Gate: each one-click
   preset yields a cohesive believable exotic sky composing sun+moon+stars(+atmosphere)+grade; presets
   compose with the running cycle (look holds as time advances); super-tunable underneath.

## Acceptance

- Auto cycle: play/pause/speed/scrub a believable continuous day, smooth across the 24→0 wrap and
  dawn/dusk; manual time still works; `--autotime` starts it at launch; cost within budget.
- Fantasy: one-click cross-system exotic skies (blood-moon, twin dusk, alien-green, …) that read cohesive,
  compose with time/weather/grade and with the running cycle, and stay tunable.
- No new celestial bodies (single sun + single moon preserved); multiple-bodies cleanly deferred.

## Risks

1. **Wrap discontinuity** — `time_of_day` 24→0 and the dawn/dusk anchor seams must be pop-free while the
   clock runs; the night anchors already wrap (0 h ≈ 22 h deep night). Verify in motion at several speeds.
2. **Per-frame ComposeLighting cost** — the running clock re-composes each frame; keep ComposeLighting
   lean (it already is); profile `--profmove`. The benign radiance-rebake log lines recur while playing —
   cosmetic, documented (memory + CloudVolume comment), no crash.
3. **Fantasy preset coupling** — a cross-system preset depends on the sun/celestial/atmosphere appliers;
   reference them by the SAME ids the per-feature presets use (one schema) so they can't drift; degrade
   gracefully if the atmosphere (#3) isn't present (skip that block).
4. **Speed range** — too-fast cycles strobe the radiance/shadows; clamp speed to a sane range and judge.
