# WG17 Slice A (Lighting) — Plan 2 of 3: Seams + Composer + Registry

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the new behavior-bearing core — the one-way `ILightingTarget`/`ILuminaryFeed` seams, the `ShadowRegistry` single-owner invariant, the rewritten `LightingComposer` (sole scene-lighting writer), and the `ShadowOwnerCheck` gate.

**Architecture:** This is the slice's "why." The composer composes Time×Weather×Grade + the luminary budget into one immutable push through `ILightingTarget` — never reading scene state back, never knowing a scene path, never taking camera input (so lighting is view-independent by construction). The `ShadowRegistry` asserts ≤1 ground-shadow owner (0 this slice).

**Tech Stack:** Godot 4.6 / C# (.NET 8, nullable). Pure logic + a fake target for tests.

**Plan set (this is 2 of 3):** Plan 1 = data core (DONE prerequisite). **Plan 2 (this) = seams + composer + registry.** Plan 3 = driver + scene + eye-gate.

**Reference:**
- Spec: `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceA-lighting-design.md` (§4, §5, §6)
- WG16 composer (for the composition MATH only, not the host plumbing): `scripts/lab/LightingComposer.cs`

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k`.
- **Namespace:** `Te10k.Lighting` (+ `Te10k.Lighting.Checks` for checks).
- **C# rebuild gotcha:** `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit.
- **Launch gotchas:** absolute `--path`; CLI flags need the bare `--` separator.
- **THE invariant (verbatim from spec):** ONE writer of scene lighting (`LightingComposer` → `ILightingTarget`); one direction only; the composer never calls `GetNode`, never references a `/root/...` path, never reads scene state back, never calls UI; `Compose()` takes NO camera/orientation input. `ShadowRegistry` asserts ≤1 owner per band; `ActiveOwnerCount == 0` this slice; no CLI/hotkey/UI registers an owner.
- **Depends on:** Plan 1 (`LightingState`, `Luminary`, `SkyPresets` exist and compile).

---

## File Structure (this plan)

```
src/lighting/ILightingTarget.cs      # Task 1 — the one-way scene-write seam + ILuminaryFeed
src/lighting/ShadowRegistry.cs       # Task 2 — single-owner invariant + view-locked diagnostic
src/lighting/LightingComposer.cs     # Task 4 — sole writer; composes axes+luminaries → ILightingTarget
src/lighting/checks/ShadowOwnerCheck.cs   # Task 3 — asserts owners==0 & view-locked
src/lighting/checks/FakeLightingTarget.cs # Task 1 — records calls, for composer tests
```

---

### Task 1: ILightingTarget + ILuminaryFeed + a fake target

**Files:**
- Create: `src/lighting/ILightingTarget.cs`
- Create: `src/lighting/checks/FakeLightingTarget.cs`

**Interfaces:**
- Produces:
  - `interface ILightingTarget` with `ApplySun(Vector3 dirToSun, Color color, float energy, bool castsShadow)`, `ApplyMoon(Vector3 dirToMoon, Color color, float energy)`, `ApplyAmbient(Color color, float energy, float skyContribution)`, `ApplyFog(Color color, float density, float aerial, float height, float heightDensity, float sunScatter, float skyAffect)`, `ApplyGrade(float exposure, float white, float glow, float contrast, float saturation, float brightness, Color tint)`, `ApplySky(Color top, Color horizon, Color ground)`.
  - `interface ILuminaryFeed` with `PushSun(Vector3 dirToSun, Color color, float energy)`, `PushMoon(Vector3 dirToMoon, Color color, float energy, float phase)`, `PushExtraSuns(int count, Vector3[] dirs, Color[] colors, float[] energies)`.
  - `class FakeLightingTarget : ILightingTarget, ILuminaryFeed` recording the last values of each call (public fields) for assertions.

- [ ] **Step 1: Write ILightingTarget.cs**

```csharp
using Godot;
namespace Te10k.Lighting;

public interface ILightingTarget
{
    void ApplySun(Vector3 dirToSun, Color color, float energy, bool castsShadow);
    void ApplyMoon(Vector3 dirToMoon, Color color, float energy);
    void ApplyAmbient(Color color, float energy, float skyContribution);
    void ApplyFog(Color color, float density, float aerial, float height,
                  float heightDensity, float sunScatter, float skyAffect);
    void ApplyGrade(float exposure, float white, float glow, float contrast,
                    float saturation, float brightness, Color tint);
    void ApplySky(Color top, Color horizon, Color ground);
}

public interface ILuminaryFeed
{
    void PushSun(Vector3 dirToSun, Color color, float energy);
    void PushMoon(Vector3 dirToMoon, Color color, float energy, float phase);
    void PushExtraSuns(int count, Vector3[] dirs, Color[] colors, float[] energies);
}
```

- [ ] **Step 2: Write FakeLightingTarget.cs (records calls)**

```csharp
using Godot;
namespace Te10k.Lighting.Checks;
using Te10k.Lighting;

public sealed class FakeLightingTarget : ILightingTarget, ILuminaryFeed
{
    public Vector3 SunDir; public Color SunColor; public float SunEnergy; public bool SunShadow;
    public float MoonEnergy = -1f; public Color AmbientColor; public float AmbientEnergy = -1f;
    public bool FogApplied, GradeApplied, SkyApplied;
    public int SunApplyCount;

    public void ApplySun(Vector3 d, Color c, float e, bool s) { SunDir = d; SunColor = c; SunEnergy = e; SunShadow = s; SunApplyCount++; }
    public void ApplyMoon(Vector3 d, Color c, float e) { MoonEnergy = e; }
    public void ApplyAmbient(Color c, float e, float sky) { AmbientColor = c; AmbientEnergy = e; }
    public void ApplyFog(Color c, float d, float a, float h, float hd, float ss, float sa) { FogApplied = true; }
    public void ApplyGrade(float ex, float w, float g, float ct, float sat, float b, Color t) { GradeApplied = true; }
    public void ApplySky(Color t, Color h, Color g) { SkyApplied = true; }
    public void PushSun(Vector3 d, Color c, float e) { }
    public void PushMoon(Vector3 d, Color c, float e, float p) { }
    public void PushExtraSuns(int n, Vector3[] d, Color[] c, float[] e) { }
}
```

- [ ] **Step 3: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add src/lighting/ILightingTarget.cs src/lighting/checks/FakeLightingTarget.cs
git commit -m "feat(lighting): one-way ILightingTarget/ILuminaryFeed seams + fake target"
```
Expected: `Build succeeded`.

---

### Task 2: ShadowRegistry (the single-owner invariant)

**Files:**
- Create: `src/lighting/ShadowRegistry.cs`

**Interfaces:**
- Produces: `enum ShadowBand { ContactNear, TerrainSelf, CloudGround }`; `class ShadowRegistry` with `void Register(ShadowBand band, string ownerName)` (throws `InvalidOperationException` if the band already has a different owner), `void Unregister(ShadowBand band, string ownerName)`, `int ActiveOwnerCount { get; }`, `bool LightingViewLocked => true`, `string StatusLine()`.

- [ ] **Step 1: Write ShadowRegistry.cs**

```csharp
using System.Collections.Generic;
namespace Te10k.Lighting;

public enum ShadowBand { ContactNear, TerrainSelf, CloudGround }

// Single-owner invariant for ground shadows. The thing WG16 never had: a second ground-shadow
// owner cannot slip in silently — Register throws. There is NO CLI/hotkey/UI path that registers.
public sealed class ShadowRegistry
{
    private readonly Dictionary<ShadowBand, string> _owners = new();

    public void Register(ShadowBand band, string ownerName)
    {
        if (_owners.TryGetValue(band, out var cur) && cur != ownerName)
            throw new System.InvalidOperationException(
                $"ShadowRegistry: band {band} already owned by '{cur}', refused '{ownerName}'. " +
                "One owner per band — see SHADOWS_AUDIT_2026_06_27.");
        _owners[band] = ownerName;
    }

    public void Unregister(ShadowBand band, string ownerName)
    {
        if (_owners.TryGetValue(band, out var cur) && cur == ownerName) _owners.Remove(band);
    }

    public int ActiveOwnerCount => _owners.Count;
    public bool LightingViewLocked => true;   // lighting never varies with camera — standing invariant

    public string StatusLine() =>
        $"shadows: owners={ActiveOwnerCount} | lighting-view-locked={(LightingViewLocked ? "YES" : "NO")} " +
        "| view-dep surfacing OWNED BY: material-slice (not built)";
}
```

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add src/lighting/ShadowRegistry.cs
git commit -m "feat(lighting): ShadowRegistry single-owner invariant + view-locked diagnostic"
```
Expected: `Build succeeded`.

---

### Task 3: ShadowOwnerCheck (assert the invariant)

**Files:**
- Create: `src/lighting/checks/ShadowOwnerCheck.cs`

**Interfaces:**
- Consumes: `ShadowRegistry` (Task 2).
- Produces: `static class ShadowOwnerCheck` with `static bool Run(ShadowRegistry reg)` — asserts `reg.ActiveOwnerCount == 0` AND `reg.LightingViewLocked`; prints `SHADOW-OWNER PASS owners=0 view-locked=YES` or a FAIL line; returns the boolean. Also a self-contained throw-test (registering two owners on one band throws).

- [ ] **Step 1: Write ShadowOwnerCheck.cs**

```csharp
using Godot;
namespace Te10k.Lighting.Checks;
using Te10k.Lighting;

public static class ShadowOwnerCheck
{
    public static bool Run(ShadowRegistry reg)
    {
        bool ok = reg.ActiveOwnerCount == 0 && reg.LightingViewLocked;

        // Invariant self-test: a second owner on a band MUST throw.
        bool threw = false;
        var probe = new ShadowRegistry();
        probe.Register(ShadowBand.TerrainSelf, "ownerA");
        try { probe.Register(ShadowBand.TerrainSelf, "ownerB"); }
        catch (System.InvalidOperationException) { threw = true; }
        ok = ok && threw;

        GD.Print(ok
            ? $"SHADOW-OWNER PASS owners={reg.ActiveOwnerCount} view-locked=YES invariant-throws=YES"
            : $"SHADOW-OWNER FAIL owners={reg.ActiveOwnerCount} view-locked={reg.LightingViewLocked} invariant-throws={threw}");
        return ok;
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add src/lighting/checks/ShadowOwnerCheck.cs
git commit -m "feat(lighting): ShadowOwnerCheck asserts 0 owners + invariant throws"
```
Expected: `Build succeeded`. (The `--shadowcheck` CLI wiring lands in Plan 3.)

---

### Task 4: LightingComposer (sole writer; the rewrite)

**Files:**
- Create: `src/lighting/LightingComposer.cs`

**Interfaces:**
- Consumes: `LightingState` types, `Luminary`/`LuminaryBudget`/`LuminaryCaps` (Plan 1); `ILightingTarget`, `ILuminaryFeed` (Task 1); `ShadowRegistry` (Task 2); `FakeLightingTarget` (Task 1, for tests).
- Produces: `class LightingComposer` with ctor `LightingComposer(ILightingTarget target, ILuminaryFeed feed, ShadowRegistry shadows)`; mutable axis-state properties `Time`, `Weather`, `Grade`, `SunDisc`, `Moon`, `Stars`; `List<Luminary> Luminaries`; `float Overcast` (default 0); `void DriveTime(float hour)`; `void Compose()`. `Compose()` computes sun/moon orientation from `Time` (NOT camera), runs the budgeter, and pushes once through `target` + `feed`. `ApplySun(... castsShadow:)` is `shadows.ActiveOwnerCount > 0 ? ... : false` → always false this slice.

- [ ] **Step 1: Write a failing composer test (view-independence + single-write)**

Add `src/lighting/checks/ComposerCheck.cs`:
```csharp
using Godot;
namespace Te10k.Lighting.Checks;
using Te10k.Lighting;

public static class ComposerCheck
{
    public static bool Run()
    {
        var fake = new FakeLightingTarget();
        var reg = new ShadowRegistry();
        var c = new LightingComposer(fake, fake, reg);
        c.DriveTime(12f);
        c.Compose();
        Vector3 noonSun = fake.SunDir;
        bool sunWritten = fake.SunApplyCount >= 1;
        bool noonShadowOff = fake.SunShadow == false;          // 0 owners → no shadow
        bool ambientWritten = fake.AmbientEnergy >= 0f;

        // View-independence: Compose() takes no camera; re-composing at the same time is identical.
        c.Compose();
        bool stable = fake.SunDir.IsEqualApprox(noonSun);

        // Day/night: sun should be higher at noon than at midnight (arc is time-driven).
        c.DriveTime(12f); c.Compose(); float noonY = fake.SunDir.Y;
        c.DriveTime(0f);  c.Compose(); float midnightY = fake.SunDir.Y;
        bool arcSane = noonY > midnightY;

        bool ok = sunWritten && noonShadowOff && ambientWritten && stable && arcSane;
        GD.Print(ok ? "COMPOSER PASS (written, shadow-off, view-stable, arc-sane)"
                    : $"COMPOSER FAIL written={sunWritten} shadowOff={noonShadowOff} stable={stable} arc={arcSane}");
        return ok;
    }
}
```

- [ ] **Step 2: Build to confirm it FAILS (LightingComposer undefined)**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: FAIL — `LightingComposer` does not exist yet.

- [ ] **Step 3: Implement LightingComposer**

Create `src/lighting/LightingComposer.cs`. Port the COMPOSITION MATH from `scripts/lab/LightingComposer.cs` (sun arc elev/az from time-of-day + arc params; the day_script color interpolation; night factor + ambient floor; fog from `Weather`; grade from `Grade`; the `LuminaryBudget.Allocate` call ranking by priority×visibility). DISCARD all host plumbing: no `ILightingHost`, no `_host.SceneOwner.GetNode`, no `/root/...`, no `OrientSun`/`SyncLightControlsToScene`, no EMISSION, no hardcoded `0.12f` ambient. Structure:
```csharp
using Godot; using System.Collections.Generic;
namespace Te10k.Lighting;

public sealed class LightingComposer
{
    readonly ILightingTarget _t; readonly ILuminaryFeed _feed; readonly ShadowRegistry _shadows;
    public TimeState Time = new(); public WeatherState Weather = new(); public GradeState Grade = new();
    public SunDiscState SunDisc = new(); public MoonState Moon = new(); public StarsState Stars = new();
    public List<Luminary> Luminaries = new();
    public LuminaryCaps Caps = new();
    public float Overcast = 0f;

    public LightingComposer(ILightingTarget target, ILuminaryFeed feed, ShadowRegistry shadows)
    { _t = target; _feed = feed; _shadows = shadows; }

    public void DriveTime(float hour) { Time.TimeOfDay = Mathf.PosMod(hour, 24f); }

    public void Compose()
    {
        // 1) Sun orientation — analytic from Time ONLY (never camera). Returns dirToSun (unit).
        Vector3 dirToSun = SunArc.DirToSun(Time);           // port the arc math into a SunArc helper or inline
        float nightFactor = SunArc.NightFactor(dirToSun);   // 0 day .. 1 deep night, from sun elevation

        // 2) Day color script → sun color/energy, sky colors, ambient (no EMISSION, no magic 0.12).
        var look = DayScriptSample.At(Time.TimeOfDay);       // interpolate LightingPresets.DayScript
        float ambEnergy = Mathf.Lerp(look.Ambient, Time.NightAmbientFloor, nightFactor);

        // 3) Budget the luminaries (sun + moon + extras). castsShadow strictly from the registry.
        bool sunShadow = _shadows.ActiveOwnerCount > 0;      // == false this slice

        // 4) ONE push, one direction.
        _t.ApplySun(dirToSun, look.SunColor, look.SunEnergy * Mathf.Max(0f, 1f - 0.6f * Overcast), sunShadow);
        // moon: gated by night × up × phase (energy 0 = off)
        Vector3 dirToMoon = SunArc.DirToMoon(Time, Moon);
        float moonE = Moon.LightEnergy * nightFactor * Mathf.Max(0f, dirToMoon.Y) * Moon.Phase;
        _t.ApplyMoon(dirToMoon, Moon.LightColor, moonE);
        _t.ApplyAmbient(Colors.White, ambEnergy, look.AmbientSky);
        _t.ApplyFog(Weather.FogColor, Weather.FogDensity, Weather.FogAerial, Weather.FogHeight,
                    Weather.FogHeightD, Weather.FogSunScatter, Mathf.Lerp(1f, 0.05f, nightFactor));
        _t.ApplyGrade(Grade.Exposure, Grade.White, Grade.Glow, Grade.Contrast, Grade.Saturation, Grade.Brightness, Grade.Tint);
        _t.ApplySky(look.SkyTop, look.SkyHorizon, look.SkyGround);

        // 5) Feed sky-visual consumers (no-op until Slice C).
        _feed.PushSun(dirToSun, look.SunColor, look.SunEnergy);
        _feed.PushMoon(dirToMoon, Moon.LightColor, moonE, Moon.Phase);
    }
}
```
Implement the small helpers `SunArc.DirToSun/DirToMoon/NightFactor` and `DayScriptSample.At` by porting the corresponding WG16 arc + day-script-interpolation math into `src/lighting/SunArc.cs` and `src/lighting/DayScriptSample.cs` (pure functions; create them in this step). Keep them camera-free.

- [ ] **Step 4: Build + run ComposerCheck to confirm PASS**

Temporarily invoke `ComposerCheck.Run()` from a throwaway `_Ready` (or wait for the Plan 3 `--composercheck` flag). Build:
```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "/c/.../Godot_v4.6...mono.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --composercheck
```
Expected: `COMPOSER PASS (written, shadow-off, view-stable, arc-sane)`.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(lighting): LightingComposer sole writer — one-way push, view-locked, 0 shadow owners (COMPOSER PASS)"
```

---

## Self-Review

**Spec coverage (Plan 2 portion):**
- §5 `ILightingTarget` one-way seam + `ILuminaryFeed` → Task 1. ✓
- §6 `ShadowRegistry` (Register throws on 2nd owner, `ActiveOwnerCount`, `LightingViewLocked`, `StatusLine`) → Task 2; asserted by Task 3. ✓
- §4 composer sole writer, no host/paths/readback/UI, no camera input, fixed-on-port (no EMISSION/0.12/mood-dict) → Task 4. ✓
- §2 view-locked + day/night arc → ComposerCheck (Task 4 Step 1). ✓
- §7 `ShadowOwnerCheck` + `LuminaryPresetCheck`(Plan 1) → Task 3. ✓

**Placeholder scan:** the `/c/.../Godot...mono.exe` is a machine-specific path to fill at run time (Plan 3 locates it), not unspecified work. `SunArc`/`DayScriptSample` are explicitly specified as "port the WG16 arc math, camera-free" with file paths — concrete, not a TODO. ✓

**Type consistency:** `ILightingTarget`/`ILuminaryFeed` signatures identical in Task 1 definition, FakeLightingTarget impl, and composer calls. `ShadowRegistry.Register/Unregister/ActiveOwnerCount/LightingViewLocked/StatusLine` consistent across Tasks 2/3. `LightingComposer(ILightingTarget, ILuminaryFeed, ShadowRegistry)` ctor matches ComposerCheck usage. ✓

**TDD note:** the composer (new behavior) gets a real failing-test-first loop (Task 4 Steps 1–4) covering the two things that matter most — view-independence and zero shadow owners. The ported data (Plan 1) didn't need it; this does.
