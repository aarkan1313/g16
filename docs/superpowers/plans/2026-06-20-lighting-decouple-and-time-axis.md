# Lighting Decouple + Time-of-Day Driver (Stage 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax. Each task re-derives exact code from the spec — code blocks below are the canonical shapes, not the only valid wording.

**Goal:** Split the bundled lighting "mood" into three independent axes (Time, Weather, Grade) behind one `LightingComposer`, and drive the **Time** axis from a single `time_of_day` knob (analytic sun arc + a keyframed daytime color script).

**Architecture:** Three pure-data state structs (`LightingState.cs`) + one writer (`LightingComposer.cs`) that replaces `ApplyMood`'s body. Time = analytic sun position + interpolated color-script anchors. Weather/Grade re-home the existing cloud/fog + tonemap/grade with no internal change. Spec: `specs/2026-06-20-lighting-decouple-and-time-axis-design.md`. Parent arch: `specs/2026-06-20-sun-light-system-architecture.md`.

**Tech Stack:** Godot 4.6 C# (Environment/DirectionalLight/Sky), JSON registry, `cloud_sky.gdshader`.

## Global Constraints

- **One writer:** after this stage, `LightingComposer.Apply()` is the ONLY code that writes scene lighting (sun transform/color/energy, env tonemap/adjust/glow/fog/ambient, sky colors, cloud knobs). No path may write these directly outside the composer.
- **Behavior-preserving first, then extend:** Tasks 1–3 must reproduce today's look exactly (refactor only); the Time driver (Tasks 4–5) is the only intended visual change.
- **Keep conventions:** sun orientation must keep the existing `+Basis.Z` "to sun" convention (`OrientSun`/`PushSunToCloud`); verify `--shadowcheck` PASS after any sun-position change.
- **Re-home, don't rebuild:** clouds, fog, AgX tonemap, adjustments, glow already exist — wire them behind Weather/Grade unchanged.
- **No shader unit tests** — verify windowed: `"<Godot>" --path C:/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn -- <args>`. Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`. Build: `dotnet build WG16.csproj -c Debug -v quiet 2>&1 | grep -E "Build succeeded|error CS"`. Commit after each task (`experiment/presentation`).

---

### Task 1: `LightingState.cs` — the three pure-data structs + JSON load

**Files:** Create `scripts/lab/LightingState.cs`. Create `data/time_presets.json`, `data/weather_presets.json`, `data/grade_presets.json` (seed each with ONE default entry for now; Task 3 fills from the moods).

**Interfaces — Produces:**
- `TimeState { float TimeOfDay; float SunriseH, SunsetH, PeakElev, AzStart, AzEnd; List<TimeKey> Script; }`
  with `TimeKey { float Hour; float SunEnergy; Color SunColor, SkyTop, SkyHorizon, SkyGround; float Ambient, AmbientSky; }`
- `WeatherState { string CloudPreset; Color FogColor; float FogDensity, FogAerial, FogHeight, FogHeightD, FogSunScatter; }`
- `GradeState { float Exposure, White, Glow, Contrast, Saturation, Brightness; Color Tint; }`
- `static class LightingPresets { List<TimeState> Time; List<WeatherState> Weather; List<GradeState> Grade; Load(); }`

- [ ] **Step 1:** Write the structs + a `Load()` that parses the three JSON files (mirror `CloudLayers.Load()` / `FieldParams.Load()` JSON style in the repo). Default-seed each file with one entry capturing today's spawn look (sun ~35°, neutral sky, light fog, exposure 1.0).
- [ ] **Step 2:** Build → `Build succeeded.`
- [ ] **Step 3:** Add a temporary `GD.Print` in `_Ready` after `LightingPresets.Load()` printing counts; run `--auto-shot` once; expected stdout shows `time=1 weather=1 grade=1` (loaded). Remove the print.
- [ ] **Step 4:** Commit `feat(light): LightingState structs + preset JSON loaders (data only)`.

---

### Task 2: `LightingComposer.cs` — behavior-preserving composition, `ApplyMood` delegates

**Files:** Create `scripts/lab/LightingComposer.cs`. Modify `scripts/lab/TerrainLabUI.Moods.cs` (`ApplyMood` body → build the 3 states from the current mood, call composer).

**Interfaces — Produces:** `LightingComposer.Apply(Environment env, DirectionalLight3D sun, CloudVolume cloud, GodRaysScreen godrays, TimeState time, WeatherState weather, GradeState grade)` — writes the full scene lighting; `static (TimeState,WeatherState,GradeState) FromMood(Godot.Collections.Dictionary mood)` — splits a legacy mood dict into the 3 states.

- [ ] **Step 1:** Implement `Apply()` by MOVING the existing writes out of `ApplyMood` verbatim, grouped: TIME block (sun orient via `_sunAngle`/`_sunAzimuth` + `OrientSun`, `sun.LightEnergy/LightColor`, sky colors via `CloudVolume.SetSkyColors`, `env.AmbientLight*`), WEATHER block (`env.Fog*`, cloud knobs), GRADE block (`env.Tonemap*`, `env.Adjustment*`, `env.Glow*`). Implement `FromMood()` reading the exact keys (`sun_angle,sun_az,sun_energy,sun_color,sky_*,ambient,ambient_sky,shadow_soft` → Time; `fog_*` → Weather; `exposure,white,glow,contrast,saturation` → Grade) — same defaults `ApplyMood` used.
- [ ] **Step 2:** Rewrite `ApplyMood(idx)` to: parse mood dict → `FromMood()` → store the 3 states on the UI (`_time/_weather/_grade` fields) → `LightingComposer.Apply(...)`. Keep `_sunAngle/_sunAzimuth/_baseSunEnergy/_baseAmbient/_baseFogColor` updated from the states (OvercastDirty depends on them).
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Verify NO visual change: capture each mood before/after is identical. Run `--mood=0 --auto-shot=C:/tmp/light/m0.png` (and 2,4); the refactor must look pixel-equivalent to the pre-refactor commit (allow only SDFGI/cloud-temporal noise). A/B against `git stash` if unsure.
- [ ] **Step 5:** Commit `refactor(light): LightingComposer is the one writer; ApplyMood delegates (no visual change)`.

---

### Task 3: Split the 6 moods into Time/Weather/Grade presets (+ combo shim)

**Files:** Modify `data/time_presets.json`, `data/weather_presets.json`, `data/grade_presets.json` (fill from the 6 moods). Modify `data/lighting_moods.json` (keep as combo presets: `{name, time_id, weather_id, grade_id}`). Modify `TerrainLabUI.Moods.cs` (a combo "mood" now = pick a Time + Weather + Grade by id).

- [ ] **Step 1:** For each of the 6 moods, emit a Time preset (its sun/sky/ambient as a single color-script anchor at its `sun_angle`-derived hour), a Weather preset (its fog), a Grade preset (its tonemap/grade). De-dupe identical Grades. Add a `time_id/weather_id/grade_id` combo to `lighting_moods.json` per mood.
- [ ] **Step 2:** `ApplyMood(idx)` now loads the combo → sets `_time/_weather/_grade` from the three preset lists → `LightingComposer.Apply`.
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Verify each of the 6 moods still looks the same as Task 2 (now sourced from the split presets, not the bundled dict). `--mood=0..5 --auto-shot`.
- [ ] **Step 5:** Commit `feat(light): split 6 moods into Time/Weather/Grade presets + combo shim`.

---

### Task 4: Time axis — analytic sun arc driven by `time_of_day`

**Files:** Modify `LightingComposer.cs` (TIME block: compute sun elev/az from the arc), `LightingState.cs` (arc params already present), `TerrainLabUI.Cli.cs` (`--time=<h>`), `TerrainLabUI.Apply.cs` (`time_of_day` case), `data/lab_controls.json` (Light-tab `time_of_day` + arc knobs).

**Interfaces — Produces:** `LightingComposer.SunArc(TimeState t) → (float elevDeg, float azDeg)`.

- [ ] **Step 1:** Implement `SunArc`: `float f = clamp((t.TimeOfDay - t.SunriseH)/(t.SunsetH - t.SunriseH), 0, 1); elev = t.PeakElev * sin(PI*f); az = lerp(t.AzStart, t.AzEnd, f);`. In the TIME block, set `_sunAngle=elev; _sunAzimuth=az; OrientSun(sun)` from the arc instead of the stored mood angle (when a `time_of_day` is active).
- [ ] **Step 2:** Add `--time=<h>` (Cli) → sets `_time.TimeOfDay` + recomposes; add `time_of_day` Light-tab control (`scenef`, 5.0–19.0) routed in `ApplySceneFloat` → `_time.TimeOfDay = v; LightingComposer.Apply(...)`. Add `day_sunrise/day_sunset/day_peak_elev/day_az_start/day_az_end` knobs likewise.
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Verify: `--time=7 --auto-shot=dawn.png`, `--time=12 --auto-shot=noon.png`, `--time=17 --auto-shot=dusk.png` — the sun is low-east at 7, high at 12, low-west at 17 (disc position moves across the sky). `--shadowcheck` PASS (sun convention intact). The `time_of_day` slider sweeps the sun live.
- [ ] **Step 5:** Commit `feat(light): analytic sun arc driven by time_of_day + arc knobs + --time`.

---

### Task 5: Time axis — keyframed daytime color script (sun color/energy, sky, ambient)

**Files:** Modify `LightingComposer.cs` (TIME block: interpolate the color script), `data/time_presets.json` (a multi-anchor default day script: Dawn/Golden/Noon/Golden/Dusk).

**Interfaces — Produces:** `LightingComposer.SampleScript(TimeState t) → TimeKey` (lerp the two anchors bracketing `t.TimeOfDay`).

- [ ] **Step 1:** Implement `SampleScript`: find the two `Script` anchors bracketing `TimeOfDay`, lerp all fields (sun_energy, sun_color, sky_top/horizon/ground, ambient, ambient_sky) by hour. In the TIME block, drive `sun.LightEnergy/LightColor`, sky colors, and ambient from the sampled key (replacing the single stored values when `time_of_day` is active).
- [ ] **Step 2:** Author a default day color script in `time_presets.json` (Dawn 6h warm-dim, Golden 8h warm, Noon 12h white-bright blue sky, Golden 16h warm, Dusk 18h warm-dim) seeded from the moods' sun/sky palettes.
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Verify cohesion: scrub `time_of_day` 6→12→18 (or `--time`): sun color goes warm→white→warm, sky goes dawn→blue→dusk, ambient dim→bright→dim, ALL together with the moving sun. Stage-1 sun disc reddens at the low ends automatically. No banding/pops mid-interpolation.
- [ ] **Step 5:** Commit `feat(light): keyframed daytime color script (sun/sky/ambient over time)`.

---

### Task 6: Weather + Grade as independent axes (pickers + CLI) and slider sync

**Files:** Modify `TerrainLabUI.Apply.cs` (`SyncLightControlsToScene` → reflect from the 3 states; weather/grade preset routing), `TerrainLabUI.Cli.cs` (`--weather`/`--grade`), `data/lab_controls.json` (Weather/Grade preset pickers; re-home the existing fog + tonemap/grade knobs to write `_weather`/`_grade` then recompose).

- [ ] **Step 1:** Add `--weather=<id>`/`--grade=<id>` (Cli) → set `_weather`/`_grade` from the preset lists → recompose. Add Light-tab pickers (enum/scene) for Weather + Grade presets.
- [ ] **Step 2:** Re-home the existing fog + exposure/white/contrast/saturation/glow Light-tab controls so each writes its field on `_weather`/`_grade` then calls `LightingComposer.Apply` (instead of `env.*` directly). Point `SyncLightControlsToScene` at `_time/_weather/_grade` so sliders show composed values.
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Verify the combinatorial range: `--time=8 --weather=<overcast> --grade=<desaturated> --auto-shot` = golden-hour sun geometry + overcast clouds + moody grade, all at once. Change one axis, the others hold. The 6 combo moods still reproduce their original looks.
- [ ] **Step 5:** Commit `feat(light): Weather + Grade independent axes + pickers + slider sync`.

---

### Task 7: Migrate the load-bearing callers; retire the bundled path

**Files:** Modify `TerrainLabUI.cs` (`ApplyDefaultMood` / spawn), `TerrainLabUI.Clouds.cs` (preset apply, `OvercastDirty` base), `TerrainLabUI.Apply.cs`. Remove any remaining direct `env.*`/`sun.*` lighting writes outside `LightingComposer`.

- [ ] **Step 1:** Route spawn default (`ApplyDefaultMood`), cloud-preset apply, and the mood re-apply after cloud attach through the composer (set the 3 states, call `Apply`). Re-home `OvercastDirty`'s base sun/ambient capture to read from `_time` (composed), so overcast still scales correctly.
- [ ] **Step 2:** Grep for direct lighting writes (`\.LightEnergy|\.LightColor|env\.Tonemap|env\.Fog|env\.Glow|env\.Adjustment|SetSkyColors`) outside `LightingComposer.cs`; move any stragglers into the composer.
- [ ] **Step 3:** Build → `Build succeeded.`
- [ ] **Step 4:** Full verify: spawn look sane; each combo mood correct; `time_of_day` sweep cohesive; overcast still dims; `--shadowcheck` PASS; no double-writes (toggle weather/grade/time in any order, result is consistent).
- [ ] **Step 5:** Commit `refactor(light): all callers go through LightingComposer; bundled mood path retired`.

---

## Self-review notes (done)

- **Spec coverage:** composer+3 states (T1–T2), mood split (T3), analytic arc (T4), color script (T5),
  Weather/Grade axes + CLI + sync (T6), load-bearing migration + one-writer guarantee (T7). All spec
  sections map to a task. Night/celestial + auto-cycle correctly OUT (Stages 3–4).
- **Risk coverage:** ApplyMood load-bearing (T2+T7), sun convention/`--shadowcheck` (T4), OvercastDirty
  base (T2+T7), slider sync (T6), night scope kept out (whole plan).
- **Type consistency:** `TimeState/WeatherState/GradeState`, `LightingComposer.Apply/FromMood/SunArc/
  SampleScript`, `TimeKey` used consistently across tasks.
- **Behavior-preserving gate:** T2–T3 must look identical to pre-refactor; only T4–T6 change the look.
```
