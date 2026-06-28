# WG17 Slice A (Lighting) — Plan 3 of 3: Driver + Scene + Eye-Gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the lighting core into a live scene — a `LightingDriver` node implementing `ILightingTarget` with injected Sun/Env, a day/night clock, CLI flags for the checks, an on-screen HUD, and the headline eye-gate: camera yaw/pitch must not change the lit world.

**Architecture:** `LightingDriver` is the minimal host (no harness). It holds INJECTED `DirectionalLight3D` + `WorldEnvironment` refs (exported NodePaths, not `GetNode("/root/...")`), owns the `LightingComposer` + `ShadowRegistry`, ticks the clock in `_Process`, and applies the composer's one-way pushes to the real nodes. CLI flags run the checks and quit.

**Tech Stack:** Godot 4.6 / C# (.NET 8). Scene + Environment/DirectionalLight3D API.

**Plan set (this is 3 of 3):** Plan 1 = data core, Plan 2 = composer+registry (both prerequisites). **Plan 3 (this) = driver + scene + eye-gate.**

**Reference:** Spec `C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-sliceA-lighting-design.md` (§4 ApplyOvercast removal, §8 placeholder-scene fallback, §9 DoD).

## Global Constraints

- **Target repo (verbatim):** `C:\Wg16\WG17\terrainengine-10k`.
- **Namespace:** `Te10k.App` for the driver; `Te10k.Lighting`/`.Checks` for the rest.
- **C# rebuild gotcha:** `dotnet build Terrainengine10k.csproj` after EVERY `.cs` edit.
- **Launch gotchas:** absolute `--path`; CLI flags need the bare `--` separator (e.g. `... -- --autotime`).
- **Injection rule:** `LightingDriver` gets Sun + Env via exported `NodePath` set in the scene — NEVER `GetNode("/root/...")` and NEVER a hardcoded absolute path.
- **No second writer:** ONLY `LightingDriver` (via the composer's `ILightingTarget` calls) writes the Sun/Env. Nothing else in WG17 touches them.
- **Depends on:** Plan 1 + Plan 2 complete (composer, registry, checks, fakes all compile).

---

## File Structure (this plan)

```
src/app/LightingDriver.cs        # Task 1 — host node: ILightingTarget impl (injected nodes) + clock + checks + HUD
scenes/terrain.tscn              # Task 2 (modify) OR Task 3 (create placeholder) — Sun, Env, LightingDriver wired
```

---

### Task 1: LightingDriver — ILightingTarget on injected nodes

**Files:**
- Create: `src/app/LightingDriver.cs`

**Interfaces:**
- Consumes: `LightingComposer`, `ShadowRegistry`, `ILightingTarget` (Plan 2); `LightingPresets`, `SkyPresets` (Plan 1); the check classes.
- Produces: `class LightingDriver : Node, ILightingTarget` with `[Export] NodePath SunPath`, `[Export] NodePath EnvPath`, `[Export] float AutoTimeSpeed = 0f` (hours/sec; 0 = paused), `[Export] float StartHour = 10f`. Implements the six `ILightingTarget` methods writing to the injected `DirectionalLight3D`/`WorldEnvironment`. Lazily creates a non-shadowing moon `DirectionalLight3D` as its own child.

- [ ] **Step 1: Write LightingDriver.cs**

```csharp
using Godot;
namespace Te10k.App;
using Te10k.Lighting;
using Te10k.Lighting.Checks;

public partial class LightingDriver : Node, ILightingTarget
{
    [Export] public NodePath SunPath = "";
    [Export] public NodePath EnvPath = "";
    [Export] public float AutoTimeSpeed = 0f;   // in-world hours per real second; 0 = paused
    [Export] public float StartHour = 10f;

    DirectionalLight3D _sun = null!;
    WorldEnvironment _env = null!;
    DirectionalLight3D? _moon;
    LightingComposer _composer = null!;
    ShadowRegistry _shadows = null!;
    Label? _hud;
    float _hour;

    public override void _Ready()
    {
        _sun = GetNode<DirectionalLight3D>(SunPath);   // INJECTED path, not /root/...
        _env = GetNode<WorldEnvironment>(EnvPath);
        _shadows = new ShadowRegistry();
        _composer = new LightingComposer(this, /* feed */ new NullLuminaryFeed(), _shadows);
        LightingPresets.Load();
        _hour = StartHour;

        if (HandleCli()) return;   // a check flag ran + quit

        _composer.DriveTime(_hour);
        _composer.Compose();
        BuildHud();
    }

    public override void _Process(double delta)
    {
        if (AutoTimeSpeed != 0f)
        {
            _hour = Mathf.PosMod(_hour + AutoTimeSpeed * (float)delta, 24f);
            _composer.DriveTime(_hour);
            _composer.Compose();
        }
        if (_hud != null) _hud.Text = $"t={_hour:0.0}h  {_shadows.StatusLine()}";
    }

    // ── ILightingTarget: the ONLY writer of Sun/Env ──
    public void ApplySun(Vector3 dirToSun, Color color, float energy, bool castsShadow)
    {
        // Orient so the light points FROM the sun toward the scene (basis.Z toward sun).
        _sun.LookAtFromPosition(Vector3.Zero, -dirToSun, Vector3.Up, true);
        _sun.LightColor = color; _sun.LightEnergy = energy; _sun.ShadowEnabled = castsShadow;
    }
    public void ApplyMoon(Vector3 dirToMoon, Color color, float energy)
    {
        if (energy <= 0.001f) { if (_moon != null) _moon.Visible = false; return; }
        _moon ??= NewMoon();
        _moon.Visible = true;
        _moon.LookAtFromPosition(Vector3.Zero, -dirToMoon, Vector3.Up, true);
        _moon.LightColor = color; _moon.LightEnergy = energy;
    }
    public void ApplyAmbient(Color color, float energy, float skyContribution)
    {
        var e = _env.Environment;
        e.AmbientLightColor = color; e.AmbientLightEnergy = energy; e.AmbientLightSkyContribution = skyContribution;
    }
    public void ApplyFog(Color c, float density, float aerial, float height, float heightDensity, float sunScatter, float skyAffect)
    {
        var e = _env.Environment;
        e.FogEnabled = true; e.FogMode = Godot.Environment.FogModeEnum.Depth;
        e.FogLightColor = c; e.FogDensity = density; e.FogAerialPerspective = aerial;
        e.FogHeight = height; e.FogHeightDensity = heightDensity; e.FogSunScatter = sunScatter; e.FogSkyAffect = skyAffect;
    }
    public void ApplyGrade(float exposure, float white, float glow, float contrast, float saturation, float brightness, Color tint)
    {
        var e = _env.Environment;
        e.TonemapExposure = exposure; e.TonemapWhite = white;
        e.GlowEnabled = glow > 0f; e.GlowIntensity = glow;
        e.AdjustmentEnabled = true; e.AdjustmentContrast = contrast; e.AdjustmentSaturation = saturation; e.AdjustmentBrightness = brightness;
    }
    public void ApplySky(Color top, Color horizon, Color ground)
    {
        if (_env.Environment.Sky?.SkyMaterial is ProceduralSkyMaterial m)
        { m.SkyTopColor = top; m.SkyHorizonColor = horizon; m.GroundBottomColor = ground; }
    }

    DirectionalLight3D NewMoon()
    {
        var m = new DirectionalLight3D { Name = "MoonLight", ShadowEnabled = false };
        AddChild(m); return m;
    }

    void BuildHud()
    {
        var layer = new CanvasLayer(); AddChild(layer);
        _hud = new Label { Position = new Vector2(12, 12) }; layer.AddChild(_hud);
    }

    bool HandleCli()
    {
        var args = OS.GetCmdlineUserArgs();
        bool has(string f) { foreach (var a in args) if (a == f) return true; return false; }
        if (has("--shadowcheck"))   { var ok = ShadowOwnerCheck.Run(_shadows);   GetTree().Quit(ok ? 0 : 1); return true; }
        if (has("--composercheck")) { var ok = ComposerCheck.Run();              GetTree().Quit(ok ? 0 : 1); return true; }
        if (has("--luminarycheck")) { var ok = LuminaryPresetCheck.Run();        GetTree().Quit(ok ? 0 : 1); return true; }
        if (has("--autotime"))      { AutoTimeSpeed = AutoTimeSpeed == 0f ? 1f : AutoTimeSpeed; }
        return false;
    }
}

// No-op feed until Slice C wires clouds/atmosphere.
file sealed class NullLuminaryFeed : ILuminaryFeed
{
    public void PushSun(Vector3 d, Color c, float e) { }
    public void PushMoon(Vector3 d, Color c, float e, float p) { }
    public void PushExtraSuns(int n, Vector3[] d, Color[] c, float[] e) { }
}
```
(If `LuminaryPresetCheck.Run()` has a different signature from Plan 1, match it here.)

- [ ] **Step 2: Build**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
```
Expected: `Build succeeded`.

- [ ] **Step 3: Commit**

```bash
git add src/app/LightingDriver.cs
git commit -m "feat(lighting): LightingDriver host — ILightingTarget on injected Sun/Env + clock + CLI checks + HUD"
```

---

### Task 2: Wire the driver into the scene (terrain or placeholder)

**Files:**
- Modify or Create: `scenes/terrain.tscn`

**Interfaces:**
- Consumes: `LightingDriver` (Task 1).
- Produces: a launchable scene with `Sun` (DirectionalLight3D), `Env` (WorldEnvironment + Environment with a ProceduralSky), a `LightingDriver` node whose `SunPath`/`EnvPath` exports point at those nodes, and SOMETHING to light (the terrain if merged, else a placeholder plane + boxes).

- [ ] **Step 1: Decide terrain-or-placeholder**

Run (Bash):
```bash
ls "C:/Wg16/WG17/terrainengine-10k/scenes/terrain.tscn" 2>/dev/null && echo HAS_SCENE || echo NO_SCENE
ls "C:/Wg16/WG17/terrainengine-10k/src/terrain/" 2>/dev/null && echo HAS_TERRAIN || echo NO_TERRAIN
```
If `HAS_TERRAIN` (the terrain slice merged), modify the existing scene (Step 2). If `NO_TERRAIN`, create a placeholder scene (Step 3 / Task 3) instead.

- [ ] **Step 2: (terrain merged) add Sun/Env/LightingDriver to terrain.tscn**

In `scenes/terrain.tscn`: ensure a `Sun` (DirectionalLight3D) and `Env` (WorldEnvironment whose `Environment` has a `Sky` = ProceduralSkyMaterial, tonemap Filmic). Add a `LightingDriver` node (script `src/app/LightingDriver.cs`); set its `SunPath` export to the Sun node and `EnvPath` to the Env node (relative NodePaths). Set `AutoTimeSpeed=0`, `StartHour=10`. Keep the existing Camera+FlyCamera and terrain.

- [ ] **Step 3: Build + commit (if terrain path)**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(lighting): wire LightingDriver into terrain scene (injected Sun/Env)"
```
Then go to Task 4.

---

### Task 3: Placeholder scene (only if terrain not yet merged)

**Files:**
- Create: `scenes/lighting_probe.tscn`
- Modify: `project.godot` (temporarily set `run/main_scene` to the probe; revert when terrain lands)

**Interfaces:**
- Produces: a standalone lit scene to eye-gate lighting without terrain.

- [ ] **Step 1: Author lighting_probe.tscn**

Create `scenes/lighting_probe.tscn`: `Root(Node3D)` → `Camera(Camera3D + FlyCamera if available, else a plain Camera3D)`, `Sun(DirectionalLight3D)`, `Env(WorldEnvironment, Environment with ProceduralSky + Filmic tonemap)`, `Ground(MeshInstance3D, large PlaneMesh, plain StandardMaterial3D mid-grey)`, three `Box` MeshInstance3D at varied positions/heights (so sun direction + ambient are readable on faces), and a `LightingDriver` node with `SunPath`→Sun, `EnvPath`→Env, `StartHour=10`, `AutoTimeSpeed=0`. Point `run/main_scene` at this probe.

- [ ] **Step 2: Build + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj
git add -A && git commit -m "feat(lighting): placeholder lighting_probe scene for standalone eye-gate"
```

---

### Task 4: Run the checks (gates)

**Files:** none (runs the Plan 1/2 checks via the driver's CLI flags)

- [ ] **Step 1: Find the Godot 4.6 mono binary**

Run (Bash): `where godot 2>/dev/null || ls /c/**/Godot_v4.6*mono*win64.exe 2>/dev/null`. Note the path as `$GODOT`.

- [ ] **Step 2: Run the three checks windowed**

```bash
cd "C:/Wg16/WG17/terrainengine-10k"
"$GODOT" --path "C:/Wg16/WG17/terrainengine-10k" -- --luminarycheck ; echo "exit=$?"
"$GODOT" --path "C:/Wg16/WG17/terrainengine-10k" -- --shadowcheck   ; echo "exit=$?"
"$GODOT" --path "C:/Wg16/WG17/terrainengine-10k" -- --composercheck ; echo "exit=$?"
```
Expected: each prints its PASS line (`... PASS ...`) and `exit=0`.

- [ ] **Step 3: Commit a note if any check needed a fix**

(If a check failed, fix the implicated Plan 1/2 file, rebuild, re-run. Commit the fix. If all passed first try, nothing to commit here.)

---

### Task 5: The day/night eye-gate (the headline gate)

**Files:** none (manual visual gate)

- [ ] **Step 1: Launch with the day/night clock running**

```bash
"$GODOT" --path "C:/Wg16/WG17/terrainengine-10k" -- --autotime
```
Watch a full cycle. Expected: dawn→noon→dusk→night reads cohesive; the sun arcs across the sky; at night the moon rises and ambient drops to a dark (not pure-black) floor. The HUD shows `owners=0 lighting-view-locked=YES`.

- [ ] **Step 2: THE view-lock gate — yaw and pitch at a fixed time**

Relaunch paused (no `--autotime`, `StartHour≈10`). Without changing time, **mouse-look: yaw left/right and pitch up/down across the whole scene.** Expected: the LIT WORLD does not change — the sun stays in the same world position, ambient/fog/sky colors are constant; only *which faces you see* changes (faces toward the sun bright, away dark — that's correct N·L, not a bug). HUD `lighting-view-locked=YES`.
- If the lighting itself shifts with camera angle → REAL BUG, stop and debug (something took camera input).
- If only surfacing detail shifts → that's the material slice's concern (and there's no material yet, so on the placeholder/plain terrain you should see nothing shift at all).

- [ ] **Step 3: Record the result + profile, commit the slice marker**

Note frame ms (lighting compose cost is trivial; record anyway). Commit a slice-complete marker:
```bash
cd "C:/Wg16/WG17/terrainengine-10k"
git commit --allow-empty -m "milestone(lighting): Slice A eye-gate PASS — day/night cohesive, view-locked YES, owners=0 (<X>ms)"
```

- [ ] **Step 4: Update the migration record (WG16 repo)**

Append a short "WG17 Slice A (Lighting) outcome" note (eye-gate result, check results, any deviations, frame ms) to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md` and commit it in the WG16 repo.

---

## Self-Review

**Spec coverage (Plan 3 portion):**
- §3 `LightingDriver` implements `ILightingTarget` via INJECTED Sun/Env (no `/root/...`) → Task 1 (exported NodePaths). ✓
- §4 no second writer; ApplyAmbient uses composed energy not hardcoded 0.12; ApplyGrade real → Task 1 methods. ✓
- §2 day/night cycle + world-locked eye-gate → Task 5. ✓
- §7 checks runnable (`--luminarycheck/--shadowcheck/--composercheck`) → Task 1 `HandleCli` + Task 4. ✓
- §8 placeholder-scene fallback when terrain not merged → Task 3. ✓
- §9 DoD (eye-gate PASS, owners==0, checks pass, profile recorded, migration note) → Tasks 4–5. ✓

**Placeholder scan:** `$GODOT` and `<X>ms` are run-time values (the binary path is located in Task 4 Step 1; the ms is measured), not unspecified work. No TBD/TODO. ✓

**Type consistency:** `LightingDriver` ctor-wires `new LightingComposer(this, new NullLuminaryFeed(), _shadows)` — matches the Plan 2 ctor `LightingComposer(ILightingTarget, ILuminaryFeed, ShadowRegistry)`. The six `ILightingTarget` methods match the Plan 2 interface exactly. CLI flags match the check class names (`ShadowOwnerCheck.Run`, `ComposerCheck.Run`, `LuminaryPresetCheck.Run`). `StatusLine()` matches Plan 2. ✓

**Note:** `LightingDriver` derives from `Node` (not `Node3D`) and creates its own moon child + HUD CanvasLayer; it's a controller, not spatial. The Sun/Env are separate scene nodes it drives — preserving the single-writer rule (the driver is the one writer).
