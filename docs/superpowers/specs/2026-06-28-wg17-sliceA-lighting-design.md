# WG17 Sky Stack — Slice A: Lighting Foundation (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `C:\Wg16\wg-16-project` + audit `docs/MIGRATION-AUDIT-2026-06-28.md`
**Part of:** the WG17 "sky stack" migration — four sub-slices, each its own spec/plan/eye-gate:
**A. Lighting (this doc)** → B. Atmosphere → C. Clouds → D. Godrays/Aerial.

**Scope:** Lighting + Sky-state + Luminary + a `ShadowRegistry`. This is the **clean single-owner
foundation everything else reads from**. Built first, deliberately, because the whole reason for the
restart is lighting correctness ("nothing weird when I mouse-look"). Atmosphere/clouds/godrays are
**out of scope** for Slice A.

---

## 1. Why This Slice Exists (the correctness mandate)

WG16's lighting "weirdness" had two distinct root causes, conflated for months across repeated resets
(see WG16 `docs/SHADOWS_AUDIT_2026_06_27.md` and the project memories):

1. **Too many shadow owners.** Five independent systems could each shadow the terrain — Godot CSM (which
   kept re-enabling itself default-on), CDLOD caster rings, a horizon-march shader path, cloud ground
   receive, and SSAO/SSIL/SDFGI. No single owner; hidden re-enable paths; they fought each other.
2. **View-dependent SURFACING mistaken for shadows.** "Shading changes when I mouse-look up/down /
   left/right" was almost never a shadow bug — it was the ground shader's view-dependent terms
   (normal-map distance/footprint fade `nfade`, grazing specular, Toksvig roughness). Because it *looked*
   like shadowing, it got chased by toggling shadow systems, which re-introduced owner chaos.

**This slice fixes #1 architecturally and walls off #2 so it can never be misdiagnosed again.**

Design principle (the spine of the whole sky stack):
> **One writer of scene lighting. One owner per shadow band, enforced in code. Lighting is
> view-independent by construction. View-dependent surfacing is explicitly owned elsewhere and
> labelled as such in diagnostics.**

---

## 2. Success Criteria

1. **Day/night cycle drives the terrain correctly** — sun arc (elevation/azimuth from time-of-day), the
   day color script (dawn→noon→dusk sun/sky/ambient), a physical night half (moon, dark ambient floor).
2. **World-locked.** Yawing and pitching the camera changes *only which faces you see* — the lit world
   (sun direction, ambient, fog, sky colors) does NOT change with camera orientation. This is the headline
   eye-gate and the thing WG16 kept getting wrong.
3. **Single-owner invariant holds.** Exactly one writer of `DirectionalLight3D`/`WorldEnvironment`
   (`LightingComposer`); the `ShadowRegistry` asserts `ActiveOwnerCount == 0` this slice (no terrain
   shadows yet) and makes a second ground-shadow owner impossible to add silently.
4. **Known bugs fixed-on-port** — no EMISSION ambient fill, no hardcoded `0.12f` ambient, no legacy
   mood-dict→axes shim (axes-native from the start).
5. **Cleaner.** `LightingComposer` reads nothing back from the scene and calls no UI; one-way push through
   `ILightingTarget`. No hardcoded `/root/...` scene paths.

**Non-goals this slice:** terrain shadows (registry framework only, 0 owners), atmosphere/clouds/godrays,
the lab harness/param-registry UI, view-dependent surfacing fixes (owned by a future material slice).

---

## 3. Architecture & File Layout

```
src/lighting/
├── LightingState.cs       # PORT AS-IS — pure-data axes: TimeState, TimeKey, SunDiscState,
│                          #   MoonState, StarsState, WeatherState, GradeState + LightingPresets loader.
│                          #   Zero scene writes, JSON-loaded. The gold-standard file; namespace → Te10k.Lighting.
├── Luminary.cs            # PORT AS-IS — Luminary (sun|moon body), LuminaryCaps, LuminaryAllocation,
│                          #   LuminaryBudget.Allocate(). Pure logic, no Godot scene types.
├── ILightingTarget.cs     # NEW — the one-way seam (replaces WG16's ILightingHost). §5.
├── ILuminaryFeed.cs       # NEW — where the composer pushes sun/moon for sky-visual consumers (no-op until Slice C).
├── ShadowRegistry.cs      # NEW — single-owner invariant + view-locked diagnostic. §6.
├── LightingComposer.cs    # REWRITE the host seam — sole writer of scene lighting; one-way; no paths. §4.
└── SkyPresets.cs          # PORT — sun/celestial/fantasy/mood preset libraries; drop the mood-dict shim.

src/lighting/checks/
├── LuminaryPresetCheck.cs # PORT — round-trip data gate (headless-safe pure logic).
└── ShadowOwnerCheck.cs    # NEW — asserts ShadowRegistry.ActiveOwnerCount == 0 and LightingViewLocked.

src/app/
└── LightingDriver.cs      # NEW — the minimal host node: implements ILightingTarget by holding INJECTED
                           #   Sun (DirectionalLight3D) + Env (WorldEnvironment) refs; drives the time clock.

data/   (copied from WG16, paths fixed to res://)
├── time_presets.json      # time arcs + the day_script color anchors
├── weather_presets.json   # fog presets
├── grade_presets.json     # tonemap/glow/adjustment presets
├── sun_presets.json, celestial_presets.json, fantasy_presets.json, lighting_moods.json
└── luminaries.json        # optional N-body sets
```

**Layering rules (enforced in review):**
- `LightingState.cs` and `Luminary.cs` are pure data/logic — no `GetNode`, no scene writes.
- `LightingComposer` writes scene lighting ONLY through `ILightingTarget`; it never calls `GetNode`, never
  references a `/root/...` path, never reads scene state back, never calls UI.
- `LightingComposer.Compose()` takes NO camera/orientation input → view-independence is structural.
- `LightingDriver` is the only class that touches concrete `DirectionalLight3D`/`WorldEnvironment` nodes,
  and it gets them by injection (exported node paths in the scene), not hardcoded absolute paths.

---

## 4. LightingComposer — the sole writer

Keeps WG16's proven composition: three orthogonal axes **Time × Weather × Grade**, plus the celestial
overlay (sun disc, moon, stars) and the N-luminary priority budgeter. What changes is the *plumbing*:

- **Input:** the axis states (`Time`, `Weather`, `Grade`, `SunDisc`, `Moon`, `Stars`), the luminary list,
  and an overcast scalar (pushed in from clouds later; 0 until Slice C). NO camera, NO scene handles.
- **Output:** computed lighting pushed once through `ILightingTarget` (sun, moon, ambient, fog, grade, sky),
  and luminary disc/sun data pushed through `ILuminaryFeed` (no-op feed until Slice C).
- **Budgeter:** `LuminaryBudget.Allocate()` ranks bodies by priority×visibility and fills the hard caps
  (`MaxPhysicalLights = 4` — Godot's `LIGHT0..3` limit; `MaxAtmosphereSuns = 3`; `MaxDiscs = 16`). Ported
  unchanged — this is the existing, correct light-count discipline.

**Sun/moon orientation:** computed analytically from time-of-day + arc params (elev/az), exactly as WG16.
The composer sets the directional light rotation; because orientation derives only from time (never camera),
turning the camera cannot move the sun. `ApplySun(dirToSun, color, energy, castsShadow)` — `castsShadow`
is **always false this slice** (no shadow owner registered).

**Fixed-on-port (do NOT carry these over):**
- WG16 `ApplyOvercastScaling` baked a magic `0.12f` ambient floor → replaced by the axis-driven
  `NightAmbientFloor` / composed ambient; no magic constant.
- WG16 ground shader faked ambient via EMISSION → WG17 ground shader carries no EMISSION fill; ambient is
  real `ApplyAmbient` energy/color. (Ground shader is the terrain slice's; this slice just stops feeding the hack.)
- WG16 `MoodToStates()` split a legacy `Dictionary` mood into axes for back-compat → deleted. WG17 moods are
  authored directly as `{time, sundisc, weather, grade}` bundles in `lighting_moods.json`. No shim, no
  "which value wins."

---

## 5. ILightingTarget — the one-way seam

Replaces WG16's bidirectional `ILightingHost` (which let the composer reach back into the UI god-class via
`_host.SceneOwner.GetNode("/root/TerrainLabRoot/Env")`, `_host.Cloud`, `_host.OrientSun()`, etc.). WG17 is
strictly **composer → target**, one direction.

```csharp
namespace Te10k.Lighting;

public interface ILightingTarget
{
    void ApplySun(Vector3 dirToSun, Color color, float energy, bool castsShadow);
    void ApplyMoon(Vector3 dirToMoon, Color color, float energy);   // energy 0 = moon light off
    void ApplyAmbient(Color color, float energy, float skyContribution);
    void ApplyFog(Color color, float density, float aerial, float height, float heightDensity,
                  float sunScatter, float skyAffect);
    void ApplyGrade(float exposure, float white, float glow, float contrast, float saturation,
                    float brightness, Color tint);
    void ApplySky(Color top, Color horizon, Color ground);          // procedural-sky fallback (no cloud sky yet)
}

public interface ILuminaryFeed   // sky-visual consumers (clouds/atmosphere) subscribe here in B/C; no-op until then
{
    void PushSun(Vector3 dirToSun, Color color, float energy);
    void PushMoon(Vector3 dirToMoon, Color color, float energy, float phase);
    void PushExtraSuns(int count, Vector3[] dirs, Color[] colors, float[] energies);
}
```

- `LightingDriver` implements `ILightingTarget` with **injected** `DirectionalLight3D` (sun) +
  `WorldEnvironment` (env) refs (exported `NodePath`s set in `terrain.tscn`), and a lazily-created
  non-shadowing moon light. No `GetNode("/root/...")`.
- WG16's `OrientSun` / `SyncLightControlsToScene` callbacks are **deleted** (no UI to sync; no harness).
- To know what the scene will look like, read `Compose()` top to bottom: one writer, one direction, no path
  strings, no read-back.

---

## 6. ShadowRegistry — single-owner invariant + view-lock diagnostic

The device that makes "nothing weird" enforceable and prevents the surfacing-vs-shadow misdiagnosis.

```csharp
namespace Te10k.Lighting;

public enum ShadowBand { ContactNear, TerrainSelf, CloudGround }   // at most ONE owner each

public sealed class ShadowRegistry
{
    // Throws if `band` already has a DIFFERENT owner. The hard invariant — a second ground-shadow
    // owner cannot slip in silently. There is NO CLI/hotkey/UI path that registers an owner.
    public void Register(ShadowBand band, string ownerName);
    public void Unregister(ShadowBand band, string ownerName);

    public int ActiveOwnerCount { get; }              // Slice A: asserted == 0
    public bool LightingViewLocked => true;           // lighting never varies with camera — standing invariant

    // One-line status for the eye-gate HUD + --shadowstatus CLI, e.g.:
    //   "shadows: owners=0 | lighting-view-locked=YES | view-dep surfacing OWNED BY: material-slice (not built)"
    public string StatusLine();
}
```

**Encoded rules:**
1. **≤1 shadow owner per band; 0 this slice.** Registry starts empty; `ShadowOwnerCheck` asserts
   `ActiveOwnerCount == 0`. The look is the clean shadowless day/night cycle. Future shadows register one
   owner (the audit's recommended world-anchored heightfield march) through this registry.
2. **No hidden re-enable.** No CLI/hotkey/UI registers an owner — there's no harness, and `Register` is only
   called from a future shadow module's `_Ready`, asserted by the check.
3. **View-dependent surfacing is explicitly NOT lighting's.** `StatusLine()` names where it lives. So when
   you mouse-look and *anything* shifts: lighting is view-locked → it's surfacing → material slice, do NOT
   touch shadows. This is the anti-misdiagnosis guardrail WG16 never had.

---

## 7. Testing & Eye-Gate

WG16-style: pure-logic checks as the test suite, plus the in-motion eye-gate.

- **`LuminaryPresetCheck`** (ported, headless-safe) — luminary ↔ dict ↔ JSON round-trips byte-identical;
  budgeter fills caps deterministically. Run via `-- --luminarycheck`.
- **`ShadowOwnerCheck`** (new) — at startup asserts `ShadowRegistry.ActiveOwnerCount == 0` and
  `LightingViewLocked == true`; prints `SHADOW-OWNER PASS owners=0 view-locked=YES`. Run via `-- --shadowcheck`.
- **Eye-gate (manual, in motion):** launch on the terrain (or a placeholder plane if the terrain slice
  isn't merged yet — see §8), run the day/night clock (`-- --autotime`), and:
  - Confirm dawn→noon→dusk→night looks cohesive; moon rises at night; ambient floor is dark not black.
  - **Yaw and pitch the camera at a fixed time** — the lit world must not change (only visible faces do).
    HUD shows `owners=0 lighting-view-locked=YES`.
  - If lighting changes with camera → real bug, STOP. If only surfacing shifts → expected, tagged for the
    material slice (and the HUD says so).
- **View-lock spot-check (optional, cheap):** a `--timefreeze` flag that holds time-of-day constant so an
  A/B yaw capture is trivially comparable by eye. (Full automated yaw/pitch pixel-diff is deferred — the
  HUD invariant + manual gate is enough for Slice A; the diagnostic is the guardrail.)

---

## 8. Dependencies, Risks, Constraints

- **Lighting needs something to light.** Ideal: the terrain slice has reached at least Slice 1 (a visible
  field mesh) in WG17. If not yet merged, Slice A stands up against a **placeholder lit plane + a few test
  boxes** (enough to read sun direction, ambient, and shadow-free correctness). The composer doesn't depend
  on terrain code — only on *a* `DirectionalLight3D` + `WorldEnvironment` in the scene.
- **No coupling to clouds/atmosphere.** The composer pushes luminary data to a no-op `ILuminaryFeed` until
  Slice C provides a real one. Lighting builds and eye-gates fully standalone.
- **Build/launch gotchas (carry from terrain slice):** `dotnet build` after every `.cs` edit (Godot won't
  rebuild C# on launch); launch with absolute `--path`; CLI flags need the bare `--` separator;
  pure-logic checks run windowed-or-headless, but anything touching the scene runs windowed.
- **Risk — re-inheriting view-dependence.** The one way Slice A could fail its mandate is if a ported
  value secretly depends on camera. Mitigation: `Compose()` takes no camera input (structural), and the
  eye-gate explicitly yaws/pitches at fixed time.
- **Risk — preset JSON drift.** Six preset files port over; `LuminaryPresetCheck` + graceful-default
  loaders (already in `LightingState`) keep a missing/renamed key from killing boot.

---

## 9. Definition of Done (Slice A)

- WG17 launches a day/night cycle on the terrain (or placeholder); dawn→night reads cohesive; moon at night.
- **Camera yaw/pitch at fixed time does not change the lit world** (eye-gate PASS; HUD `view-locked=YES`).
- `LightingComposer` is the sole scene-lighting writer; one-way `ILightingTarget`; no `/root/...` paths;
  no read-back; no UI calls.
- `ShadowRegistry.ActiveOwnerCount == 0`; `ShadowOwnerCheck` PASS; no CLI/hotkey/UI can register an owner.
- Fixed-on-port confirmed: no EMISSION fill fed, no hardcoded `0.12f` ambient, no mood-dict shim.
- `LuminaryPresetCheck` PASS. Profile number recorded (lighting compose cost is trivial; record frame ms anyway).
- Clean commits; spec/plan kept to focused ~500-line files.
