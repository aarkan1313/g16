# Shadow Rebuild — Near-CSM Crisp Cast Shadows — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add real, crisp, world-locked cast shadows near the camera — Godot cascaded shadow maps (CSM) on the sun, capped to the near band where CDLOD is finest, governed by the Phase-0 ShadowRegistry — without re-opening the terrain-CSM graveyard.

**Architecture:** A `CsmCastOwner` (registry `NearCast` slot) turns the sun's shadow on (via the registry, which now owns `sun.ShadowEnabled`) and configures `DirectionalShadowMaxDistance` to a near band. CDLOD casts shadows **only from its finest leaves** (`Level >= threshold`), so the shadow caster set equals the visible finest-LOD set — the exact mismatch that killed CSM-for-terrain in WG1–15 cannot occur. ShadowDiagnostics remains the auditor; `--shadowcheck` proves the new owner is declared and nothing else casts.

**Tech Stack:** Godot 4.6 (mono/C#), Vulkan. Verification = `dotnet build` + `--shadowcheck` (exit code) + `--yawab` (world-lock) + windowed eye-gate + a fly/teleport motion check for the graveyard.

## Global Constraints

- Build: `dotnet build C:\Wg16\wg-16-project\WG16.csproj`. Run windowed + Vulkan (no headless for RD/compute). User `--flags` need a bare `--` separator.
- New C# files flat in `scripts/lab/`; `TerrainLabUI` is `partial`.
- **Keep the lighting look** — only shadows change. **World-anchored invariant holds** (`--yawab`).
- **Graveyard rule (non-negotiable):** CSM near-band only; only finest CDLOD leaves cast; never let coarse far chunks into the shadow frustum. The fly/teleport motion eye-gate (Task 5) exists to prove no popping.
- `--shadowcheck` must stay green: the registry declares every caster ShadowDiagnostics can see.
- CDLOD facts: `Level` ∈ [0 = coarsest/far .. `MaxDepth`=6 = finest/near] (`CdlodTerrain.cs:79`). Chunks acquire `CastShadow.Off` (`:414-415`); per-chunk state applies in `ApplyChunk` (`:357`). The sun is `/root/TerrainLabRoot/Sun`, `SunNode` in `LightingComposer.cs:152`; `sun.ShadowEnabled=false` is forced in `Compose()` at `:215`. `Compose()` runs on every lighting change.

---

## File Structure

- Modify `scripts/lab/ShadowRegistry.cs` — add `WantsSunShadow`; `DiagId` (string) → `DiagIds` (list) in `ActiveDiagIds`.
- Modify `scripts/lab/IShadowOwner.cs` — `string DiagId` → `IReadOnlyList<string> DiagIds`.
- Modify `scripts/lab/HorizonMarchOwner.cs` — implement `DiagIds`.
- Modify `scripts/lab/ShadowCheck.cs` — match against each owner's `DiagIds`.
- Modify `scripts/lab/LightingComposer.cs:215` + the `ILightingHost` interface (`:9`) — sun shadow follows `_host.WantsSunShadow`.
- Modify `scripts/lab/TerrainLabUI.Shadows.cs` — implement `WantsSunShadow`; register `CsmCastOwner`.
- Modify `scripts/lab/CdlodTerrain.cs` — `ShadowCasterMinLevel` field + setter + `ApplyChunk` per-level `CastShadow`.
- Create `scripts/lab/CsmCastOwner.cs` — the NearCast owner.

---

## Task 1: Registry owns the sun's shadow flag (decouple from Compose)

**Files:** Modify `ShadowRegistry.cs`, `LightingComposer.cs` (interface `:9-22` + line `:215`), `TerrainLabUI.Shadows.cs`.

**Interfaces:**
- Produces: `ShadowRegistry.WantsSunShadow` (bool); `ILightingHost.WantsSunShadow` (bool); `TerrainLabUI.WantsSunShadow`.

- [ ] **Step 1: Add `WantsSunShadow` to the registry**

In `scripts/lab/ShadowRegistry.cs`, after the `ActiveDiagIds()` method, add:

```csharp
    /// True when an enabled owner occupies the NearCast slot — i.e. the sun must cast an engine shadow.
    /// The lighting composer reads this so it (not a hardcoded false) decides sun.ShadowEnabled.
    public bool WantsSunShadow => _owners.Any(o => o.Slot == ShadowSlot.NearCast && o.Enabled);
```

- [ ] **Step 2: Add it to the `ILightingHost` interface**

In `scripts/lab/LightingComposer.cs`, inside `public interface ILightingHost { ... }` (line 9), add a member before the closing brace (line 22):

```csharp
    bool WantsSunShadow { get; }       // shadow registry owns sun.ShadowEnabled (Phase: near-CSM)
```

- [ ] **Step 3: Route `sun.ShadowEnabled` through it**

In `scripts/lab/LightingComposer.cs`, change line 215:

```csharp
        sun.ShadowEnabled = _host.WantsSunShadow;
```

(was `sun.ShadowEnabled = false;`)

- [ ] **Step 4: Implement `WantsSunShadow` on the host**

In `scripts/lab/TerrainLabUI.Shadows.cs`, add inside the partial class:

```csharp
    /// ILightingHost: the shadow registry owns whether the sun casts (read by LightingComposer.Compose).
    public bool WantsSunShadow => _shadowRegistry?.WantsSunShadow ?? false;
```

- [ ] **Step 5: Build + verify no change yet**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`. No NearCast owner exists yet → `WantsSunShadow` is false → sun shadow stays off → identical render.

- [ ] **Step 6: Confirm `--shadowcheck` still green**

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --horizon=1 --time=17 --shadowcheck *> "$env:TEMP\sc.log"; "exit=$LASTEXITCODE"; Select-String "$env:TEMP\sc.log" -Pattern 'SHADOWCHECK'
```
Expected: `exit=0`, `pass=YES`.

- [ ] **Step 7: Commit**

```bash
git add scripts/lab/ShadowRegistry.cs scripts/lab/LightingComposer.cs scripts/lab/TerrainLabUI.Shadows.cs
git commit -m "Registry owns sun.ShadowEnabled (decouple from Compose)" -m "LightingComposer.Compose no longer hardcodes the sun shadow off; it reads ILightingHost.WantsSunShadow, which the ShadowRegistry drives (true iff an enabled NearCast owner exists). No NearCast owner yet -> no change." -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `DiagId` → `DiagIds` (owners can declare multiple auditor tags)

**Why:** A CSM owner produces TWO kinds of ShadowDiagnostics tags — the sun (`light:/root/TerrainLabRoot/Sun`) and many chunk casters (`caster:/root/TerrainLabRoot/CdlodTerrain/...`). A single `DiagId` can't prefix-match both, so `--shadowcheck` would flag them rogue. Make `DiagIds` a list.

**Files:** Modify `IShadowOwner.cs`, `HorizonMarchOwner.cs`, `ShadowRegistry.cs`, `ShadowCheck.cs`.

**Interfaces:**
- Produces: `IShadowOwner.DiagIds` (`IReadOnlyList<string>`), replacing `DiagId`.

- [ ] **Step 1: Change the interface**

In `scripts/lab/IShadowOwner.cs`, replace the `DiagId` member:

```csharp
    System.Collections.Generic.IReadOnlyList<string> DiagIds;   // prefixes of the ShadowDiagnostics owner tags this owner accounts for
```

(remove the line `string DiagId { get; }`)

- [ ] **Step 2: Update `HorizonMarchOwner`**

In `scripts/lab/HorizonMarchOwner.cs`, replace the `DiagId` property:

```csharp
    public System.Collections.Generic.IReadOnlyList<string> DiagIds => new[] { "terrain:horizon" };
```

- [ ] **Step 3: Update the registry's collector**

In `scripts/lab/ShadowRegistry.cs`, replace `ActiveDiagIds`:

```csharp
    /// DiagId prefixes of all currently-active owners — what ShadowDiagnostics SHOULD attribute to the registry.
    public HashSet<string> ActiveDiagIds()
        => _owners.Where(o => o.IsActive).SelectMany(o => o.DiagIds).ToHashSet();
```

- [ ] **Step 4: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`. (`ShadowCheck.Run` already consumes `ActiveDiagIds()` unchanged.)

- [ ] **Step 5: Confirm `--shadowcheck` still green** (same command as Task 1 Step 6) — Expected `exit=0 pass=YES`.

- [ ] **Step 6: Commit**

```bash
git add scripts/lab/IShadowOwner.cs scripts/lab/HorizonMarchOwner.cs scripts/lab/ShadowRegistry.cs
git commit -m "Shadow owners declare a LIST of diagnostic tags (DiagId -> DiagIds)" -m "A CSM owner accounts for both the sun light tag and the chunk caster tags, so one string is not enough. HorizonMarchOwner declares [terrain:horizon]." -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: CDLOD per-chunk caster gating (finest leaves only)

**Files:** Modify `scripts/lab/CdlodTerrain.cs`.

**Interfaces:**
- Produces: `CdlodTerrain.SetShadowCasterMinLevel(int)` — leaves with `Level >= min` cast; default = none.

- [ ] **Step 1: Add the field + setter**

In `scripts/lab/CdlodTerrain.cs`, near the other tunables (around line 79, `public int MaxDepth = 6;`), add:

```csharp
    private int _shadowCasterMinLevel = 99;   // leaves with Level >= this cast engine shadows; 99 = none (default)
    /// Gate engine-shadow casting to the finest near leaves (Level 0=coarse/far .. MaxDepth=6=fine/near). The
    /// near-CSM owner sets this so caster-LOD == visible-LOD (the WG1-15 graveyard mismatch can't occur).
    public void SetShadowCasterMinLevel(int min) { _shadowCasterMinLevel = min; _forceReapply = true; }
```

- [ ] **Step 2: Apply per-chunk in `ApplyChunk`**

In `scripts/lab/CdlodTerrain.cs`, inside `ApplyChunk`, within the `if (snapped || isNew)` block (after line 367 `slot.OriginXZ = ...; slot.Level = c.Level;`), add:

```csharp
            mi.CastShadow = c.Level >= _shadowCasterMinLevel
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
```

Note: `_forceReapply` (set by the setter) re-runs the full apply path for existing chunks, and any LOD change re-applies a chunk as a new leaf, so the gate self-updates as the camera moves.

- [ ] **Step 3: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`. Default `_shadowCasterMinLevel = 99` → no chunk casts → identical render.

- [ ] **Step 4: Commit**

```bash
git add scripts/lab/CdlodTerrain.cs
git commit -m "CDLOD: gate engine-shadow casting to the finest near leaves" -m "SetShadowCasterMinLevel(min): leaves with Level >= min cast (Level 0=coarse/far .. MaxDepth=6=fine/near). Default 99 = none. This is the graveyard fix -- only finest near leaves cast, so caster-LOD == visible-LOD." -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `CsmCastOwner` (NearCast) — enable + configure CSM

**Files:** Create `scripts/lab/CsmCastOwner.cs`; modify `scripts/lab/TerrainLabUI.Shadows.cs` (register it).

**Interfaces:**
- Consumes: `IShadowOwner`, `ShadowSlot.NearCast`, `DirectionalLight3D` (`/root/TerrainLabRoot/Sun`), `CdlodTerrain.SetShadowCasterMinLevel`, `CdlodTerrain.MaxDepth`.
- Produces: `class CsmCastOwner : IShadowOwner` (Name `"csm-near"`).

- [ ] **Step 1: Create the owner**

```csharp
using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// NearCast owner: Godot cascaded shadow maps on the sun, capped to a near band, with CDLOD casting gated to
/// its finest leaves. The registry drives sun.ShadowEnabled (via WantsSunShadow); this owner sets the sun's
/// CSM mode/distance/bias and the terrain's caster-min-level when enabled. DiagIds cover BOTH the sun light
/// tag and the chunk caster tag so --shadowcheck recognises them.
public sealed class CsmCastOwner : IShadowOwner
{
    private readonly Node _host;
    private readonly CdlodTerrain _terrain;
    private bool _enabled;
    private bool _applied;

    // Tunables (defaults are a conservative near band; refine at eye-gate).
    public float MaxDistance = 1500f;   // DirectionalShadowMaxDistance (near band)
    public int   CasterTopLevels = 2;   // finest N LOD levels cast

    public CsmCastOwner(Node host, CdlodTerrain terrain) { _host = host; _terrain = terrain; }

    public string Name => "csm-near";
    public ShadowSlot Slot => ShadowSlot.NearCast;
    public IReadOnlyList<string> DiagIds => new[]
    {
        "light:/root/TerrainLabRoot/Sun",
        "caster:/root/TerrainLabRoot/CdlodTerrain",
    };

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _applied = false; }   // re-apply config on next Tick
    }

    public bool IsActive => _enabled;

    public void Tick(double delta)
    {
        if (_applied) { return; }
        _applied = true;

        var sun = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (sun != null)
        {
            // sun.ShadowEnabled is owned by the registry (WantsSunShadow) — we only set the CSM PARAMS.
            sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
            sun.DirectionalShadowMaxDistance = _enabled ? MaxDistance : 100f;
            sun.ShadowBias = 0.04f;
            sun.ShadowNormalBias = 1.5f;
            sun.ShadowBlur = 1.0f;
        }
        // Caster gate: finest CasterTopLevels levels cast when enabled, none when off.
        _terrain.SetShadowCasterMinLevel(_enabled ? (_terrain.MaxDepth - (CasterTopLevels - 1)) : 99);
    }
}
```

- [ ] **Step 2: Register it (default ON for this phase's eye-gate)**

In `scripts/lab/TerrainLabUI.Shadows.cs`, in `InitShadows()`, after the existing horizon-march registration:

```csharp
        _shadowRegistry.Register(new HorizonMarchOwner(_terrain));
        _shadowRegistry.Register(new CsmCastOwner(this, GetNode<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain")) { Enabled = true });
```

- [ ] **Step 3: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add scripts/lab/CsmCastOwner.cs scripts/lab/TerrainLabUI.Shadows.cs
git commit -m "Add CsmCastOwner: near-band sun CSM on finest CDLOD leaves" -m "NearCast owner: sets sun CSM mode/distance/bias and gates CDLOD casting to the finest near leaves. Registry drives sun.ShadowEnabled. DiagIds cover the sun + chunk-caster tags. Registered default-on for this phase's eye-gate." -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Verify — diagnostics, world-lock, eye-gate, graveyard motion

- [ ] **Step 1: `--shadowcheck` recognises the CSM owner (no rogue)**

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --time=17 --shadowcheck *> "$env:TEMP\sc.log"; "exit=$LASTEXITCODE"; Select-String "$env:TEMP\sc.log" -Pattern 'SHADOWCHECK'
```
Expected: `exit=0 pass=YES`. The `SHADOWCHECK` line should show `declared=[...,light:/root/TerrainLabRoot/Sun,caster:/root/TerrainLabRoot/CdlodTerrain]`, `rogue=[]`. If `rogue` lists a `light:`/`caster:` tag, a `DiagIds` prefix is wrong — fix it.

- [ ] **Step 2: Eye-gate — crisp cast shadows exist**

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --time=17 --nofog --auto-shot="C:\Users\josep\AppData\Local\Temp\claude\c--Wg16\5a456ca1-e68f-47d8-8f2d-5db64875780e\scratchpad\csm.png"
```
Read `csm.png`. Expected: visible **crisp cast shadows** on near terrain (dune crests casting onto troughs at the low sun) — distinct from the soft N·L slope shading. If absent: raise `MaxDistance`, lower `CasterTopLevels`/`ShadowBias`, or the near band is past the visible terrain.

- [ ] **Step 3: World-lock — shadows do not yaw**

```powershell
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --time=17 --nofog --yawab="C:\Users\josep\AppData\Local\Temp\claude\c--Wg16\5a456ca1-e68f-47d8-8f2d-5db64875780e\scratchpad\csmyaw"
```
Read `csmyaw_a.png` / `csmyaw_b.png`. Expected: a cast shadow stays glued to its caster geometry across the +25° yaw (only framing scrolls). CSM shadows render world geometry, so they must be world-locked.

- [ ] **Step 4: GRAVEYARD motion test — no popping/acne while flying + teleporting**

```powershell
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --time=17 --profmove --profspeed=1500 --profile=6 *> "$env:TEMP\csmmove.log"; Select-String "$env:TEMP\csmmove.log" -Pattern 'PROFILE-SHADOWS|avg|worst'
```
Then live: fly with WASD + teleport far, watch shadow edges at LOD transitions. Expected: shadows do NOT pop/flicker/acne as chunks change LOD (only finest leaves cast, so caster==visible). If they pop: the caster band extends past the finest-LOD ring — lower `MaxDistance` or raise `CasterTopLevels` to 1 (finest only). Record the profile cost.

- [ ] **Step 5: Commit the tuned defaults**

```bash
git add -A
git commit -m "Tune near-CSM defaults after eye-gate + graveyard motion test" -m "<record the final MaxDistance / CasterTopLevels / bias, the eye-gate verdict, and the measured frame cost>" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:** Near-CSM owner ✓ (Task 4); cap to near band ✓ (MaxDistance); finest-leaf casting / graveyard fix ✓ (Task 3 + Task 5 Step 4); registry-owned sun shadow ✓ (Task 1); ShadowDiagnostics/`--shadowcheck` integration ✓ (Task 2 + Task 5 Step 1); world-anchored invariant ✓ (Task 5 Step 3). **Deferred to later phases (not this plan):** altitude-aware near-band auto-scaling, cascade-split tuning, multi-luminary CSM, far horizon-map, contact AO. Noted, not silent.

**Placeholder scan:** No TBD/TODO. Task 5 Step 5's commit body is a deliberate "record the measured result" instruction, not a code placeholder.

**Type consistency:** `DiagIds` (`IReadOnlyList<string>`) defined in Task 2 Step 1, implemented in `HorizonMarchOwner` (Task 2 Step 2) + `CsmCastOwner` (Task 4), consumed in `ShadowRegistry.ActiveDiagIds` (Task 2 Step 3) — consistent. `WantsSunShadow` defined on registry (Task 1 Step 1), interface (Step 2), host (Step 4), read in Compose (Step 3) — consistent. `SetShadowCasterMinLevel(int)` defined Task 3, called in `CsmCastOwner.Tick` Task 4 — consistent. `CdlodTerrain.MaxDepth` is `public` (`:79`) — readable by the owner. ✓

**Risk note for the implementer:** if `--shadowcheck` flags the sun/caster as rogue, the `DiagIds` prefixes must exactly prefix the real `ShadowDiagnostics` owner strings (run `--profile` and read the `owners=` field for the exact paths). If shadows pop while flying, that's the graveyard — tighten the caster band before anything else.
