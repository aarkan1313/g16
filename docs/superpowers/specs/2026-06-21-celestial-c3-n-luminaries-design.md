# Celestial C3 — N Suns / N Moons (luminary abstraction) — DESIGN

> **Status: DESIGN-AHEAD ONLY (2026-06-21).** Spec written now so it's ready; **build nothing yet** (sky lane is
> look-complete and paused while ground/terrain settle; this is the last/heaviest sky feature). When built, each Unit
> is its own eye-gate, one phase past the last pass (discipline rule). Supersedes the single-sun + single-moon
> assumption baked into `ComposeLighting` + `cloud_sky.gdshader`.

**Goal:** Let the sky hold an arbitrary number of luminaries (suns + moons), each with its own arc, color, size,
phase, and lighting contribution — e.g. a 3-sun sky — **without killing the frame budget.** The efficiency design is
first-class, not an afterthought: N luminaries cost ≈ today + a little, by *sharing* the expensive work and
*allocating* the scarce hardware by priority.

**Architecture in one line:** a general `List<Luminary>` data model + a **priority-budgeting layer** in an extracted
`LightingComposer` that hands the few scarce resources (≤4 hardware directional lights, ≤2 shadow atlases, the shared
atmosphere raymarch) to the highest-priority luminaries; everyone else renders as a cheap disc + summed ambient tint.

**Tech stack:** Godot 4.6 mono (C# + GDShader); the sky `cloud_sky.gdshader`; the compute atmosphere LUTs
(`atmosphere_*.glsl`) on the `CallOnRenderThread` / `Texture2Drd`/`Texture3Drd` seam; `Std430Writer` param buffers.

---

## Global Constraints (copied from the engine + the lane's laws)

- **Godot sky shaders read at most 4 directional lights** (`LIGHT0..LIGHT3`). Luminaries beyond 4 cannot be real
  lights — they are custom-uniform discs (the way the moon is today).
- **Each shadow-casting `DirectionalLight3D` is a full CSM atlas pass** — the single most expensive thing in the
  frame. Default cap: **1 full-shadow + 1 reduced-shadow caster**; the rest are shadowless.
- **Atmosphere LUTs (`atmosphere_skyview.glsl`, `atmosphere_aerial.glsl`) currently scatter one `sunDir`.** Extra
  suns are summed *inside the existing 32-step raymarch* (shared steps) — cheap; NOT a per-sun re-dispatch.
- **Ownership law (unchanged):** `ComposeLighting` (→ becoming `LightingComposer`) is THE ONE writer of scene
  lighting. When the atmosphere arc is on (default), the physical LUT owns daytime sky/cloud-ambient color; the
  keyframed gradient is the night sky + atmosphere-off fallback (see `TerrainLabUI.Lighting.cs` ownership comment).
- **Recompute-on-change only** (the #7 perf-pass discipline): LUTs/shadows recompute when a luminary actually moves,
  not every frame.
- Launch / verify gotchas unchanged (one Godot at a time, `--rendering-driver vulkan`, `--` separator before user
  flags, local-RD compute can't run `--headless`).

---

## Current single-luminary architecture (what this generalizes)

- **Sun** = the scene `DirectionalLight3D` "Sun". In the sky shader it is `LIGHT0_*`. `sun_layers(rd)` in
  `cloud_sky.gdshader:174` renders its disc/corona/halo/redden from `LIGHT0_DIRECTION/COLOR` + the `sun_*` uniforms.
  Orientation by `OrientSun` from `_sunAngle/_sunAzimuth`; energy/color by the day script + overcast scaling.
- **Moon** = a *second* `DirectionalLight3D` "MoonLight" (created lazily, `EnsureMoonLight`, shadow-casting, gated to
  night×moon-up×phase) **plus** custom uniforms `moon_dir/moon_color/moon_size/moon_phase/...` pushed via
  `CloudVolume.SetMoon*`. Its disc is a separate code path in the sky shader (not `LIGHTn`). Its own arc is computed
  in `ComposeLighting` (phase-lagged hour → elev/az).
- **Atmosphere** = `AtmosphereCompute` builds transmittance→multiscatter→skyview→aerial LUTs for the single sun
  direction; `cloud_sky background()` samples skyview; AT-3 feeds cloud lighting via CPU readback.
- **All of this lives inside the `TerrainLabUI` partial class** (`ComposeLighting` in `TerrainLabUI.Lighting.cs`),
  which is the god-class this design also begins to break up (Unit 1).

The single-luminary assumptions to remove: exactly one `LIGHT0` sun disc; exactly one moon disc + one moon arc; one
`sunDir` into the LUTs; sun-specific fields (`_sunAngle`, `_baseSunEnergy`) and moon-specific fields (`_moon`) as
scalars rather than a list.

---

## Data model — `Luminary`

A pure-data record (new file `scripts/lab/Luminary.cs`); scene state becomes `List<Luminary> _luminaries` (replacing
the scalar sun fields + `_moon`). One sun-class and one moon-class entry reproduce **today's look byte-for-byte** as
the migration baseline.

```
enum LuminaryKind { Sun, Moon }       // Sun = self-luminous star; Moon = phased reflector

class Luminary {
    string Id;                         // stable key (e.g. "sun_primary", "moon_a") for presets/UI
    LuminaryKind Kind;

    // ── Arc (own celestial path; today's sun/moon math, parameterized) ──
    float TimeOffsetH;                 // hours added to time-of-day before evaluating the arc (moon phase-lag = this)
    float PeakElev;                    // peak elevation degrees (own declination → own peak height)
    float AzStart, AzEnd;              // azimuth sweep endpoints
    float DeclScale;                   // declination scale vs the base arc
    float ElevOffset, AzOffset;        // rigid offsets to push the whole path off another body's

    // ── Appearance (disc; superset of today's sun_* / moon_* uniforms) ──
    Color Color;                       // light + disc tint
    float Size, Limb;                  // angular radius (deg), limb darkening
    float CoronaSize, CoronaEnergy;    // sun-class inner bloom
    float HaloSize, HaloEnergy;        // wide glow (both classes)
    float DiscEnergy;                  // disc brightness
    float Phase;                       // moon-class only: 0 new .. 1 full (drives terminator + lag + illum)
    // surface params (cells/contrast/spots/churn/warm) — sun-class granules / moon-class maria
    SurfaceParams Surface;
    float Redden, ReddenOnset, HorizonGrow, CloudRedden;   // sun-class low-elevation warm shift

    // ── Capability + priority (the budgeting inputs) ──
    bool  IsPhysicalLight;             // wants a real LIGHTn (terrain direct light)? else disc + ambient tint only
    bool  CastsShadow;                 // wants a shadow atlas? (only granted to the top Cap.ShadowCasters)
    bool  ContributesToAtmosphere;     // summed into the skyview/aerial LUT raymarch?
    float Priority;                    // higher = gets scarce resources first (brightness × narrative importance)
    float LightEnergy;                 // terrain direct-light energy (gated by night/up/phase for moon-class)
    float CloudLight;                  // strength of this luminary's light on the cloud raymarch
}
```

`TimeState`/`SunDiscState`/`MoonState` collapse into per-luminary fields; the global `TimeState` keeps only the
shared clock + sunrise/sunset + the base arc endpoints the luminaries offset from.

---

## The budgeting layer (this is the "3 suns without killing the computer" answer)

A pure function `BudgetLuminaries(List<Luminary>, Caps) -> Allocation`, called by the composer each recompose. It
ranks luminaries by `Priority × current-visibility` and assigns the scarce resources:

| Resource | Cap (default) | Why scarce | Overflow behavior |
|---|---|---|---|
| Hardware lights `LIGHT0..3` | **4** | Godot sky-shader hard limit | Demote to disc-only + summed ambient tint |
| Shadow atlases | **2** (1 full @8192, 1 reduced @2048) | each = a full CSM pass, the frame's costliest item | Caster becomes a shadowless physical light |
| Atmosphere suns | **3** (summed in one raymarch) | +10-20% raymarch cost per extra sun | Contributes ambient/horizon tint only, no scatter |
| Discs | **~16** (`MAX_DISCS`) | shader uniform-array size | Beyond 16 → not drawn (log it) |

**Worked example — 3 suns + 1 moon:**
- Shadows: **1 full-shadow pass** (the dominant sun) + optionally 1 reduced (a 2nd sun or the moon at night). The
  other suns are **shadowless fill lights** — perceptually fine (eyes read one dominant shadow; extra crisp shadow
  sets just look noisy anyway). **This is the dominant cost lever: shadow passes ≈ today, regardless of sun count.**
- Direct terrain light: up to 4 real `LIGHTn` (3 suns + 1 moon fits exactly). Forward lights without shadows are
  cheap.
- Atmosphere: all 3 suns summed in the **one** 32-step skyview raymarch (+~30-40% on that small LUT, computed only
  on change) — not 3 dispatches.
- Discs: 4 disc iterations in the sky fragment shader — negligible.
- Net: **≈ today's frame + one optional reduced shadow pass + a slightly heavier (but rarely-recomputed) skyview
  LUT.** A 3-sun sky is affordable; a 3-*shadow-casting*-sun sky is the thing we deliberately don't do by default.

`Caps` is data (a `LuminaryCaps` with the four numbers) so a low-end profile can drop shadow casters to 1 and atmo
suns to 1 without touching code. Every demotion is `GD.Print`-logged (no silent caps — the audit's rule).

---

## `LightingComposer` — extracted from the god-class (Unit 1)

The single-sun/single-moon body of `ComposeLighting` (`TerrainLabUI.Lighting.cs`) becomes a real class
`scripts/lab/LightingComposer.cs` with an explicit input struct, so it is no longer coupled to ~245 `TerrainLabUI`
fields:

```
class LightingComposer {
    // dependencies passed in, not reached via GetNode string-walks:
    LightingComposer(DirectionalLight3D[] lightPool, WorldEnvironment env, CloudVolume cloud, ...);
    void Compose(ComposeInput in);   // in = { List<Luminary>, TimeState, WeatherState, GradeState, caps, overcast }
}
```

- `Compose` runs `BudgetLuminaries`, orients each granted hardware light by its luminary's arc, sets shadow props on
  the granted shadow casters, pushes the disc-uniform arrays to the sky material, sums the disc-only luminaries into
  an ambient tint, and pushes the atmosphere sun-set to `AtmosphereCompute`.
- Migration is behavior-preserving: with one Sun + one Moon luminary, `Compose` produces the same writes
  `ComposeLighting` does today (verified by A/B auto-shots at several `--time` values + `--celestial`/`--fantasy`
  presets before any visual change). **This is the de-god-objecting win**: lighting composition leaves the 2,600-line
  class and gains a testable interface, done at the moment C3 forces the refactor (not twice).
- `TerrainLabUI.Lighting.cs` shrinks to a thin adapter (build the `ComposeInput` from UI state, call the composer,
  run `SyncLightControlsToScene`). `CloudVolume`'s ~40% sky pass-through setters move behind a `SkyMaterial` facade
  in the same spirit (Unit 1b, optional but cheap once the disc arrays exist).

---

## Shader generalization — `cloud_sky.gdshader`

- Replace `sun_layers(rd)` (single `LIGHT0`) + the separate moon disc path with **`disc_layers(rd)`**: a loop over
  uniform arrays `disc_dir[N]`, `disc_color[N]`, `disc_size[N]`, `disc_limb[N]`, `disc_corona*[N]`, `disc_halo*[N]`,
  `disc_kind[N]`, `disc_phase[N]`, `disc_energy[N]` (length `disc_count`, ≤ `MAX_DISCS`). Sun-class branch keeps the
  corona/redden/surface granules; moon-class branch keeps phase terminator + maria. Sum contributions.
- The ≤4 physical luminaries still drive terrain via real `LIGHTn`; the disc array is purely the *visual* discs (a
  disc and its light are separate concerns — a disc-only luminary has an array entry but no `LIGHTn`).
- `background()` unchanged in structure; atmosphere sampling extended (below).
- Keep arrays small + branch-light; `MAX_DISCS` chosen so the unrolled loop stays cheap (start 16).

---

## Atmosphere extension (AT-1 LUTs → N suns)

- `atmosphere_skyview.glsl` `raymarchSky(pos, rayDir, sunDir)` → `raymarchSky(pos, rayDir, sunDir[], sunCol[], n)`:
  inside the existing 32-step loop, accumulate single-scatter for each of the `n` (≤3) atmosphere suns (phase
  function + transmittance-to-that-sun per step). **The 32 steps are shared** — cost grows ~linearly in `n` only on
  the per-step sun term, not the march. Same change to `atmosphere_aerial.glsl`.
- `transmittance`/`multiscatter` LUTs are sun-direction-independent — **unchanged** (computed once, reused for all
  suns). This is why N-sun atmosphere is cheap.
- `AtmosphereCompute` pushes a small sun-set buffer (dir+color × ≤3) via `Std430Writer`; recompute the skyview/aerial
  LUTs only when any atmosphere sun moves past a threshold (recompute-on-change).
- AT-3 cloud lighting: the CPU readback handoff sums the atmosphere suns' transmittance for the cloud direct term.

---

## Build sequence (each Unit = its own eye-gate, build one phase past the last pass)

1. **Unit 1 — `LightingComposer` extraction (refactor, no visual change).** Move `ComposeLighting`'s body into the
   new class behind a `ComposeInput`; `_luminaries` = [one Sun, one Moon] reproducing today. **Gate:** A/B auto-shots
   + live eye across `--time` sweep, `--celestial`, `--fantasy` show *zero* visual change. (Also: `SkyMaterial`
   facade out of `CloudVolume`, 1b.)
2. **Unit 2 — disc-array shader path.** `disc_layers()` + uniform arrays; composer pushes the 2-entry array. Still
   one sun + one moon. **Gate:** identical look; confirms the array path matches the old single-disc path.
3. **Unit 3 — budgeting layer + caps.** `BudgetLuminaries` + `LuminaryCaps` + demotion logging; still 2 luminaries
   so allocation is trivial. **Gate:** logs correct grants; no visual change.
4. **Unit 4 — add a 2nd/3rd sun (the feature).** Author a multi-sun preset; real `LIGHTn` for ≤4, shadows for ≤2,
   discs for the rest. **Gate (the real one):** a 3-sun sky looks good in motion AND `--profmove` shows the frame
   stayed in budget (the efficiency claim, measured).
5. **Unit 5 — atmosphere N-sun summation.** Extend the skyview/aerial raymarch + sun-set buffer. **Gate:** sky color
   responds to all suns; `--profmove` confirms the +per-sun cost is small; recompute-on-change holds.
6. **Unit 6 — N moons + presets + UI.** Generalize the moon path the same way; add a luminary-list editor to a lab
   tab + cross-system presets (binary sun, triple moon, etc.). **Gate:** flip-through eye-gate.

---

## Risks & mitigations

- **Behavior drift in the Unit-1 refactor** (untested visual tool, no TDD). → Mitigate with A/B auto-shots at fixed
  `--time`/preset states as the regression harness; refactor is "same image, new structure" and is gated on that.
- **Shadow cost if users expect every sun to cast crisp shadows.** → Default cap = 1 full + 1 reduced; documented +
  logged; the look reads fine (one dominant shadow). The cap is data, raisable on strong hardware.
- **Shader uniform-array limits / unrolled-loop cost** at high `MAX_DISCS`. → Start at 16, profile, keep the loop
  branch-light; disc-only luminaries are visual-cheap.
- **`MAX_DISCS`/atmo-sun mismatch between C# and GLSL** (the dead-control class of bug). → single-source the caps as
  constants shared by name; add the registry/uniform lint from the debt backlog.
- **Scope creep into Phase-B world systems.** → C3 is sky-only; it does not touch terrain/biomes.

## Out of scope

- N-sun *terrain* effects beyond direct light (multi-sun ambient occlusion subtleties, colored double-shadows as a
  feature) — only the budgeted direct + ambient tint.
- Authored galaxy/nebula (KILLED) — unrelated.
- Gameplay/day-night-cycle implications of multiple suns (when is it "night" with 3 suns?) — the composer exposes a
  combined `nightFactor` from the brightest-up luminary; richer rules defer to the Weather/world lane.

## Testing / eye-gate

No TDD (GPU/visual). Per Unit: build → headless `--import` compile-check → `--auto-shot` A/B + `--profmove` →
user's live eye. The two load-bearing gates: Unit 1 (zero visual drift on the refactor) and Unit 4 (a 3-sun sky
looks good *and* profiles within budget — the whole reason for the budgeting design).
