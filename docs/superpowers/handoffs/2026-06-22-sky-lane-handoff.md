# Sky / Light lane — HANDOFF (2026-06-22)

**Read this first when resuming the sky/light lane.** Authoritative logs: `docs/ROADMAP.md`, `docs/DECISIONS.md`,
`docs/NEEDS_REVIEW.md`. Branch: `experiment/presentation` (shared with a terrain/CDLOD chat). All work below is
committed + pushed.

## The directive (user, 2026-06-22)
**"Finish this [data-driven luminaries] and any other sky/light stuff. I know we have performance."**
→ The plan: (1) build the **data-driven object-lists** feature (specّd, below), (2) the **#7 end-of-arc perf pass**
(ROADMAP #7), (3) any remaining sky/light loose ends. Then the sky/light lane is DONE.

## Where the lane stands
Sky/light is **look-complete and mostly gated**:
- Stage 1-4 + GPU atmosphere (AT-1/2/3) — all default-on, gated.
- Night = moon + stars + meteors (galaxy/nebula KILLED, don't re-attempt procedurally).
- **Celestial C3 (N suns / N moons) — BUILT (Units 1-6) + ✅ PASSED eye-gate 2026-06-22** ("it looks good", review key 5).
  See NEEDS_REVIEW 12. The whole Celestial arc is feature-complete.

## ACTIVE TASK — data-driven object lists (the "formula") + luminaries
**Spec (approved core, awaiting nothing — user said "finish this"):**
`docs/superpowers/specs/2026-06-22-data-driven-object-lists-design.md`. **Next step: invoke `writing-plans` → build the
3 units.** (Brainstorming is DONE; the user approved the formula and said to finish it.)

**What it is:** a reusable **`objectlist`** registry type + **item schema** + **generic list-editor widget**
(add/remove/duplicate/reorder, per-item sub-panel reusing the existing widget builders) + **JSON-array save/load** —
so any future system (flora, biomes, weather cells) data-drives the same way. **Luminaries are the first consumer:**
their per-body settings (currently hardcoded constants in `LightingComposer`) become data; the user can add/edit/remove
sky bodies live and save named sets. It's also the first nested section of the eventual one-big-"world config" JSON
(direction **B**, out of scope here — this is its first module/proof).

**Build units (from the spec):**
1. **U1 — `objectlist` type + `ObjectListControl` widget** (the formula). Prove with a throwaway test schema → callback.
   Keep the FLAT registry (`_byId`, presets, randomizer) untouched — the list is a PARALLEL structure.
2. **U2 — luminary item schema + `data/luminaries.json` + `LightingComposer.LoadLuminaries` → loaded list is the
   composer's source of truth.** Load-bearing gate: **default look byte-identical** (auto-shot A/B noon/dusk/night vs
   the current C3 baseline) AND live edits change the sky. `--suns`/`--moons` + the `extra suns`/`extra moons` sliders
   become thin "append/trim default bodies" helpers.
3. **U3 — save/load named luminary presets** (additive `lists` section in the preset JSON) + round-trip gate.

**Open questions the user may answer (from the spec-review message):** list editor on the **Night tab** (my default) or a
dedicated tab? `max_items` = 7 (my default) ok? — proceed with the defaults if no answer.

## AFTER that — #7 end-of-arc code-efficiency pass (the lane's last item)
ROADMAP #7. **Non-tuning** perf sweep over the whole lighting/cloud/sun/atmosphere lane (user: "look at code
efficiency, not tuning"). Scope: profile real GPU/CPU costs (atmosphere LUTs + AT-2 32³ aerial recompute + AT-3
readback + cloud raymarch + god rays + shadow map), attack redundant per-frame work, recompute cadence, dead/dup shader
math, oversized dispatches, Std430 churn, CallOnRenderThread seams. Output: measured before/after ms per subsystem.
Perf state-of-record already consolidated in `performance.md` (2026-06-22 section) — start there. Specific owed items:
AT-3 readback "read only 3 texels via a tiny GPU output" (not two full TextureGetData); 8192 shadow atlas dial-down
A/B; a fresh whole-frame `--profmove` once terrain/CDLOD settles.

## Key technical state (C3 — what's built)
- **`scripts/lab/LightingComposer.cs`** — extracted from the `TerrainLabUI` god-class (Unit 1) behind `ILightingHost`.
  Owns lighting state + composition. Holds `List<Luminary>` + `LuminaryBudget` allocator. `ComposeExtraSuns` /
  `ComposeExtraMoons` build extras from **hardcoded constants** (`ExtraSunColors`/`ExtraSunSizeFac`/`ExtraMoonColors`/
  `ExtraMoonPhases`, az offsets 45°/40°·i) — **these become the U2 JSON data.** `ExtraSunCount`/`ExtraMoonCount` gate them.
- **`scripts/lab/Luminary.cs`** — the per-body record (ALREADY the item schema) + `LuminaryCaps` + `LuminaryBudget.Allocate`
  (ranks by priority×visibility; ≤4 LIGHTn, ≤2 shadow casters, ≤3 atmosphere suns; logs every demotion, no silent caps).
- **`shaders/cloud_sky.gdshader`** — `extra_suns()` + `extra_moons()` loops over custom-uniform arrays (no-op at count 0
  → default sky byte-identical). Disc colors are plain `vec3` arrays (bind as `Vector3[]`, NOT `source_color`).
- **`shaders/atmosphere_skyview.glsl` + `atmosphere_aerial.glsl`** — N-sun scatter: `sunInScatter()` looped in the
  shared 32-step raymarch; extra suns appended at the param-buffer TAIL (trans/ms read only the head, untouched).
  GOTCHA: the aerial helper MUST sit BELOW the shared Hillaire block (`getMiePhase`) or it fails to parse.
- **`scripts/lab/AtmosphereCompute.cs`** — `SetExtraSuns` + the tail in `BuildParams` (Std430Writer).
- **CLI/UI:** `--suns=N`/`--moons=N`; Night-tab `extra suns`/`extra moons` sliders (`ApplySceneFloat` cases →
  `_lighting.ExtraSunCount/Count`); fantasy presets binary_suns/trinary_worlds/twin_moons/triple_moons (now set
  `time_of_day` so clicking jumps to the right time); **review.tscn key 5** cycles 2/3/4 suns → 2/3 moons.

## Gotchas / process (carry these)
- **Shaders are NOT verified by `dotnet build`** — Godot compiles them at LOAD. A shader edit is unverifiable without a
  windowed launch; a broken `cloud_sky.gdshader`/atmosphere shader breaks the shared branch's sky for everyone. Always
  launch + shot after a shader change.
- **C# stale DLL:** `dotnet build WG16.csproj` after ANY .cs edit (launching the player does NOT rebuild C#).
- **Launch:** kill-all + verify zero first; `--rendering-driver vulkan`; absolute `--path /c/Wg16/wg-16-project`; user
  flags need a bare `--` separator; `--auto-shot=<OS path>` shoots @1.5s then quits (one shot/launch). Godot exe:
  `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`.
  **Background-launch quirk:** the mono launcher reports "failed exit 1" while the Godot process actually stays ALIVE
  (check `tasklist | grep -i godot` — the multi-GB process is the live scene). Don't thrash relaunching on that signal.
- **Shared branch with the terrain/CDLOD chat** (they're on S3 streaming): **`git add` EXPLICIT paths, NEVER `-A`** (an
  `-A` once swept their `.uid` sidecars into my commit — harmless but avoid). They + a linter touch
  TerrainLabUI.*.cs / ROADMAP / MEMORY.md concurrently — re-read before editing.
- **Push:** the user has been saying to push this session ("it's fine do it"); confirm, but they want it off-machine.
- **Eye-gate verification loop that works:** launch windowed, `--auto-shot` at a few `--time`/`--suns`/`--moons` values,
  view the PNGs (Read tool shows images), iterate. For multi-luminary use `--lookatsun`/`--lookatmoon` + `--coverage=0.05`
  (clear sky) to actually SEE the bodies.

## Commits (C3 + spec, all pushed)
79497f3 (Luminary model) · 41e2fe9 (U1 LightingComposer extract) · 61686f0 (U2 wire) · aa62b38 + 2ad2b23 (U4 suns +
tune) · d995c88 (U5 atmosphere scatter) · bf9bfb6 (U6 moons + controls + presets) · 9c8801e (NEEDS_REVIEW/roadmap) ·
eaed0b7 (review key 5 + preset times) · 4c71f1d (the data-driven-object-lists SPEC). Backup tag
`backup-c3-unit1-2026-06-22`.

## Immediate next action on resume
Invoke **`writing-plans`** on `specs/2026-06-22-data-driven-object-lists-design.md` → build U1→U2→U3, gating each
(U2's byte-identical-default A/B is load-bearing). Then the **#7 perf pass**. Memory: `[[sun-light-arc]]`.
