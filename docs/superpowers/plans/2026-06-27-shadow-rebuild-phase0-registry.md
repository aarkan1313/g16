# Shadow Rebuild — Phase 0: Registry + Invariant Harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `ShadowRegistry` (the governance spine) plus a `--shadowcheck` invariant harness, and bring the existing `hz_on` horizon march under it as the first owner — with zero visual change.

**Architecture:** A single `ShadowRegistry` owns pluggable `IShadowOwner`s, one per `ShadowSlot` (NearCast / FarCast / ContactAo). `ShadowDiagnostics` remains the independent scene auditor; `--shadowcheck` asserts the two agree (no rogue/undeclared casters) and that the one-owner-per-slot invariant holds, exiting 0/1 for CI. The current horizon march is wrapped as the `FarCast` owner so later phases can swap its internals behind a stable interface.

**Tech Stack:** Godot 4.6 (mono/C#), GLSL shaders, Vulkan. No unit-test framework — verification is `dotnet build` + the windowed `--shadowcheck` self-check (process exit code) + `PROFILE-SHADOWS` + eye-gates.

## Global Constraints

- Build: `dotnet build C:\Wg16\wg-16-project\WG16.csproj` (the player binary does NOT rebuild C# on its own).
- Run windowed + Vulkan for anything touching the RenderingDevice/compute; `--headless` has no local RD. Launch per `tools/godot_vulkan_env.ps1` + the console exe; user `--flags` need a bare `--` separator.
- New C# files are flat in `scripts/lab/` (match existing convention); `TerrainLabUI` is a `partial class` — add behavior via a new `TerrainLabUI.Shadows.cs` partial.
- **No lighting-look changes** and **no visual change** in Phase 0 — `PROFILE-SHADOWS` and the rendered frame must be identical to pre-Phase-0 with `hz_on` in either state.
- Hard invariants the registry exists to protect: (1) world-anchored — owner output never depends on camera orientation; (2) one enabled owner per slot.
- Commit messages: plain imperative (match `git log`), ending with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.

---

## File Structure

- Create `scripts/lab/IShadowOwner.cs` — the `ShadowSlot` enum + `IShadowOwner` interface (the owner abstraction).
- Create `scripts/lab/ShadowRegistry.cs` — owner list, invariant check, tick, declared-owner reporting.
- Create `scripts/lab/HorizonMarchOwner.cs` — wraps the existing `hz_on` march as the `FarCast` owner.
- Create `scripts/lab/ShadowCheck.cs` — the `--shadowcheck` assertion (registry vs `ShadowDiagnostics`).
- Create `scripts/lab/TerrainLabUI.Shadows.cs` — partial: `_shadowRegistry` field, `InitShadows()`, `TickShadows()`, `RunShadowCheckIfRequested()`.
- Modify `scripts/lab/TerrainLabUI.cs` — call `InitShadows()` in `_Ready` after `InitReview()` (line 85).
- Modify `scripts/lab/TerrainLabUI.Process.cs` — call `TickShadows(delta)` and `RunShadowCheckIfRequested()` in `_Process`.
- Modify `scripts/lab/TerrainLabUI.Cli.cs` — parse `--shadowcheck` into `_shadowCheckCli`.

---

## Task 1: Owner abstraction (`ShadowSlot` + `IShadowOwner`)

**Files:**
- Create: `scripts/lab/IShadowOwner.cs`

**Interfaces:**
- Produces: `enum ShadowSlot { NearCast, FarCast, ContactAo }`; `interface IShadowOwner { string Name {get;} ShadowSlot Slot {get;} bool Enabled {get;set;} bool IsActive {get;} string DiagId {get;} void Tick(double delta); }`

- [ ] **Step 1: Create the file with the enum + interface**

```csharp
using Godot;

namespace WG16.Lab;

/// The distinct shadow responsibilities. INVARIANT: at most one ENABLED owner per slot (the registry
/// enforces/reports it). Blending across slots is the registry's job, not an owner's. Future decorations
/// add an owner (likely into NearCast via an object CSM), not a new slot.
public enum ShadowSlot
{
    NearCast,   // crisp near-band cast shadows (CSM) — terrain + future objects
    FarCast,    // world-anchored far-band terrain cast shadow (horizon-map / today's march)
    ContactAo,  // small-radius ambient crevice/valley darkening
}

/// One pluggable shadow contributor. Phase 0 ships the abstraction + one real owner (the existing
/// horizon march). Later phases add owners; the registry governs them uniformly.
public interface IShadowOwner
{
    string Name { get; }          // stable short id, e.g. "horizon-march"
    ShadowSlot Slot { get; }      // the single slot this owner occupies
    bool Enabled { get; set; }    // user/registry intent
    bool IsActive { get; }        // Enabled AND actually contributing this frame
    string DiagId { get; }        // prefix of the ShadowDiagnostics owner tag, e.g. "terrain:horizon"
    void Tick(double delta);      // per-frame hook (Phase 0 owners may no-op)
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)` (the existing 28-warning set is fine).

- [ ] **Step 3: Commit**

```bash
git add scripts/lab/IShadowOwner.cs
git commit -m "Add shadow owner abstraction (ShadowSlot + IShadowOwner)

First piece of the shadow rebuild registry (Phase 0). No behavior yet.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: `ShadowRegistry`

**Files:**
- Create: `scripts/lab/ShadowRegistry.cs`

**Interfaces:**
- Consumes: `IShadowOwner`, `ShadowSlot` (Task 1).
- Produces: `class ShadowRegistry { ShadowRegistry(Node host); void Register(IShadowOwner); void Tick(double); bool CheckInvariants(out string report); HashSet<string> ActiveDiagIds(); IReadOnlyList<IShadowOwner> Owners {get;} }`

- [ ] **Step 1: Create the registry**

```csharp
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WG16.Lab;

/// THE single authority for terrain shadows. Owns the pluggable owners, enforces/reports the hard
/// invariants (one ENABLED owner per slot; nothing casts except a registered owner), ticks them, and
/// exposes its declared owners so the independent ShadowDiagnostics auditor can be cross-checked against
/// it (--shadowcheck). Phase 0: framework + the existing horizon march as the first owner (no visual change).
public sealed class ShadowRegistry
{
    private readonly Node _host;
    private readonly List<IShadowOwner> _owners = new();

    public ShadowRegistry(Node host) { _host = host; }

    public IReadOnlyList<IShadowOwner> Owners => _owners;

    /// Register an owner once. Warns (does not throw) on a slot collision — the invariant is REPORTED by
    /// CheckInvariants and GATED by --shadowcheck, not crashed at boot.
    public void Register(IShadowOwner owner)
    {
        if (!_owners.Contains(owner)) { _owners.Add(owner); }
    }

    public void Tick(double delta)
    {
        foreach (IShadowOwner o in _owners) { if (o.Enabled) { o.Tick(delta); } }
    }

    /// Invariant 2: at most one ENABLED owner per slot. False + a report naming any conflict.
    public bool CheckInvariants(out string report)
    {
        var conflicts = _owners.Where(o => o.Enabled)
                               .GroupBy(o => o.Slot)
                               .Where(g => g.Count() > 1)
                               .ToList();
        if (conflicts.Count == 0) { report = "one-owner-per-slot OK"; return true; }
        var sb = new StringBuilder("slot conflicts: ");
        foreach (var g in conflicts) { sb.Append($"{g.Key}=[{string.Join(",", g.Select(o => o.Name))}] "); }
        report = sb.ToString().TrimEnd();
        return false;
    }

    /// DiagId prefixes of currently-active owners — what ShadowDiagnostics SHOULD attribute to the registry.
    public HashSet<string> ActiveDiagIds()
        => _owners.Where(o => o.IsActive).Select(o => o.DiagId).ToHashSet();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add scripts/lab/ShadowRegistry.cs
git commit -m "Add ShadowRegistry (owner list + one-per-slot invariant)

The governance spine: owners, tick, invariant check, declared-owner reporting
for the --shadowcheck auditor cross-check. No owners registered yet.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `HorizonMarchOwner` (wrap the existing `hz_on`)

**Files:**
- Create: `scripts/lab/HorizonMarchOwner.cs`

**Interfaces:**
- Consumes: `IShadowOwner`, `ShadowSlot.FarCast`, `TerrainLab.SetBool(string,bool)`, `TerrainLab.MaterialOverride` (a `ShaderMaterial` with a `hz_on` bool uniform).
- Produces: `class HorizonMarchOwner : IShadowOwner` with `DiagId == "terrain:horizon"`.

- [ ] **Step 1: Create the owner**

```csharp
using Godot;

namespace WG16.Lab;

/// Wraps the EXISTING ground.gdshader horizon march (the hz_on uniform) as a registry owner in the FarCast
/// slot. No behavior change — Enabled mirrors the terrain material's hz_on. This brings the current shadow
/// under the registry so Phase 1 can replace its internals (baked horizon map) behind the same interface.
/// DiagId matches what ShadowDiagnostics tags the horizon owner with ("terrain:horizon...").
public sealed class HorizonMarchOwner : IShadowOwner
{
    private readonly TerrainLab _terrain;

    public HorizonMarchOwner(TerrainLab terrain) { _terrain = terrain; }

    public string Name => "horizon-march";
    public ShadowSlot Slot => ShadowSlot.FarCast;
    public string DiagId => "terrain:horizon";

    public bool Enabled
    {
        get => _terrain.MaterialOverride is ShaderMaterial m && m.GetShaderParameter("hz_on").AsBool();
        set => _terrain.SetBool("hz_on", value);
    }

    // The march self-gates by sun elevation + camera distance in-shader; for registry purposes "active"
    // simply means the uniform is on. (Finer "is it contributing this pixel" is not knowable CPU-side.)
    public bool IsActive => Enabled;

    public void Tick(double delta) { }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add scripts/lab/HorizonMarchOwner.cs
git commit -m "Wrap the hz_on horizon march as a FarCast registry owner

Brings the existing terrain shadow under the registry with zero behavior
change; Enabled mirrors the hz_on shader uniform.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Wire the registry into `TerrainLabUI` lifecycle

**Files:**
- Create: `scripts/lab/TerrainLabUI.Shadows.cs`
- Modify: `scripts/lab/TerrainLabUI.cs:85` (in `_Ready`, after `InitReview();`)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (in `_Process`, among the `_ready`-gated ticks)

**Interfaces:**
- Consumes: `ShadowRegistry`, `HorizonMarchOwner`, the existing `_terrain` field (`TerrainLab`, `TerrainLabUI.cs:21/68`).
- Produces: `TerrainLabUI._shadowRegistry` (used by Task 5's check); `InitShadows()`, `TickShadows(double)`.

- [ ] **Step 1: Create the partial**

```csharp
using Godot;

namespace WG16.Lab;

public partial class TerrainLabUI
{
    private ShadowRegistry _shadowRegistry = null!;

    /// Phase 0: construct the registry + register the existing horizon march (FarCast). Later phases
    /// register NearCast (CSM) and ContactAo owners here. Called from _Ready after _terrain is resolved.
    private void InitShadows()
    {
        _shadowRegistry = new ShadowRegistry(this);
        _shadowRegistry.Register(new HorizonMarchOwner(_terrain));
    }

    private void TickShadows(double delta) => _shadowRegistry?.Tick(delta);
}
```

- [ ] **Step 2: Call `InitShadows()` from `_Ready`**

In `scripts/lab/TerrainLabUI.cs`, immediately after the existing `InitReview();` line (line 85):

```csharp
        InitReview();          // Phase 3d: eye-gate review controller (needs _sky/_lighting/_terrain + _nightGate)
        InitShadows();         // Phase 0: shadow registry + horizon-march owner (needs _terrain)
```

- [ ] **Step 3: Call `TickShadows(delta)` from `_Process`**

In `scripts/lab/TerrainLabUI.Process.cs`, inside `_Process(double delta)`, within the existing `if (_ready) { ... }` block that pushes the camera world position (the block beginning near line 277), add at its end:

```csharp
            TickShadows(delta);   // Phase 0: registry tick (owners may no-op)
```

- [ ] **Step 4: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 5: Smoke-test no visual/diagnostic regression**

Run (captures a frame + the PROFILE-SHADOWS line, then quits):

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --horizon=1 --time=17 --profile=1 *> "$env:TEMP\wg16_p0_smoke.log"
Select-String -Path "$env:TEMP\wg16_p0_smoke.log" -Pattern 'PROFILE-SHADOWS'
```

Expected: a `PROFILE-SHADOWS ... terrainShaderOwners=1 owners=terrain:horizon:/...` line, identical to pre-Phase-0 (the registry is passive; it must not change diagnostics). No new errors beyond the known shutdown leaks.

- [ ] **Step 6: Commit**

```bash
git add scripts/lab/TerrainLabUI.Shadows.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Process.cs
git commit -m "Wire ShadowRegistry into TerrainLabUI lifecycle

Construct the registry + register the horizon-march owner in _Ready; tick it in
_Process. Passive in Phase 0 (no visual or diagnostic change).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: `--shadowcheck` invariant harness

**Files:**
- Create: `scripts/lab/ShadowCheck.cs`
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (parse `--shadowcheck` near `--fieldcheck`, line 159; add the `_shadowCheckCli` field near line 289)
- Modify: `scripts/lab/TerrainLabUI.Shadows.cs` (add `RunShadowCheckIfRequested()`)
- Modify: `scripts/lab/TerrainLabUI.Process.cs` (call `RunShadowCheckIfRequested()` in `_Process`)

**Interfaces:**
- Consumes: `ShadowRegistry.CheckInvariants`, `ShadowRegistry.ActiveDiagIds`, `ShadowDiagnostics.Capture(Node)` → `Snapshot` (`.Owners` string `"a,b"` or `"none"`, `.ToProfileLine(prefix)`).
- Produces: `static bool ShadowCheck.Run(ShadowRegistry, Node host)`; CLI flag `--shadowcheck`.

- [ ] **Step 1: Create the check**

```csharp
using Godot;
using System.Linq;

namespace WG16.Lab;

/// --shadowcheck: assert the registry (authority) and ShadowDiagnostics (independent auditor) AGREE.
/// PASS iff: (1) the registry's one-owner-per-slot invariant holds, and (2) every shadow owner the auditor
/// finds in the live scene is one the registry declares active (a prefix match) — i.e. NO rogue/undeclared
/// caster. Prints a SHADOWCHECK line; returns the pass bool (caller maps to exit 0/1 for CI).
public static class ShadowCheck
{
    public static bool Run(ShadowRegistry registry, Node host)
    {
        bool inv = registry.CheckInvariants(out string invReport);

        ShadowDiagnostics.Snapshot snap = ShadowDiagnostics.Capture(host);
        var declared = registry.ActiveDiagIds();

        // Auditor owner tags look like "kind:/path" or "terrain:horizon:/path". A tag is legitimate if some
        // declared DiagId is a prefix of it; otherwise it is a rogue/undeclared caster.
        string[] auditor = snap.Owners == "none" ? System.Array.Empty<string>() : snap.Owners.Split(',');
        string[] rogue = auditor.Where(a => !declared.Any(d => a.StartsWith(d))).ToArray();

        bool pass = inv && rogue.Length == 0;
        GD.Print($"SHADOWCHECK: pass={(pass ? "YES" : "NO")} invariants={(inv ? "OK" : invReport)} " +
                 $"declared=[{string.Join(",", declared)}] rogue=[{string.Join(",", rogue)}] " +
                 $"diag=({snap.ToProfileLine("PROFILE-SHADOWS")})");
        return pass;
    }
}
```

- [ ] **Step 2: Add the CLI flag**

In `scripts/lab/TerrainLabUI.Cli.cs`, after the `--fieldcheck` parse line (159):

```csharp
            else if (a == "--fieldcheck") { _fieldCheckCli = true; }
            else if (a == "--shadowcheck") { _shadowCheckCli = true; }   // Phase 0: registry/auditor agreement self-check
```

And next to the `_fieldCheckCli` field declaration (line 289):

```csharp
    private bool _fieldCheckCli;      // --fieldcheck → one-shot field determinism/parity self-check (S1)
    private bool _shadowCheckCli;     // --shadowcheck → one-shot shadow registry/auditor agreement self-check
```

- [ ] **Step 3: Add the runner + first-frame dispatch**

In `scripts/lab/TerrainLabUI.Shadows.cs`, add the field + method:

```csharp
    private bool _shadowCheckRan;

    /// Runs once, after the scene is up and the registry exists, when --shadowcheck was passed. Exits the
    /// process 0 (pass) / 1 (fail) so CI can gate. Runs in _Process (not _Ready) so the first frame's
    /// owners/diagnostics are real.
    private void RunShadowCheckIfRequested()
    {
        if (!_shadowCheckCli || _shadowCheckRan || !_ready || _shadowRegistry == null) { return; }
        _shadowCheckRan = true;
        GetTree().Quit(ShadowCheck.Run(_shadowRegistry, this) ? 0 : 1);
    }
```

In `scripts/lab/TerrainLabUI.Process.cs`, in `_Process`, right after the `TickShadows(delta);` call added in Task 4:

```csharp
            TickShadows(delta);                 // Phase 0: registry tick (owners may no-op)
            RunShadowCheckIfRequested();        // Phase 0: --shadowcheck one-shot, exits 0/1
```

- [ ] **Step 4: Build**

Run: `dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`.

- [ ] **Step 5: GREEN — the check passes on a clean scene**

Run:

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --horizon=1 --time=17 --shadowcheck *> "$env:TEMP\wg16_shadowcheck.log"
"exit=$LASTEXITCODE"; Select-String -Path "$env:TEMP\wg16_shadowcheck.log" -Pattern 'SHADOWCHECK'
```

Expected: `exit=0` and `SHADOWCHECK: pass=YES invariants=OK declared=[terrain:horizon] rogue=[] diag=(...)`.

- [ ] **Step 6: RED — prove the check actually bites (negative test)**

Temporarily comment out the owner registration in `scripts/lab/TerrainLabUI.Shadows.cs` `InitShadows()`:

```csharp
        _shadowRegistry = new ShadowRegistry(this);
        // _shadowRegistry.Register(new HorizonMarchOwner(_terrain));   // TEMP: removed to prove --shadowcheck detects the rogue
```

Build, then run the same command as Step 5.
Expected: `exit=1` and `SHADOWCHECK: pass=NO ... declared=[] rogue=[terrain:horizon:/...]` — the auditor sees the horizon shadow but the registry no longer declares it, so it's flagged rogue.

Then **restore** the registration line, rebuild, and re-run Step 5 to confirm `exit=0` again.

- [ ] **Step 7: Commit**

```bash
git add scripts/lab/ShadowCheck.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Shadows.cs scripts/lab/TerrainLabUI.Process.cs
git commit -m "Add --shadowcheck registry/auditor agreement self-check

Asserts the one-owner-per-slot invariant + that every caster ShadowDiagnostics
finds is registry-declared (no rogue). Exits 0/1 for CI. Negative-tested: an
unregistered owner is correctly flagged rogue.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage (against `2026-06-27-terrain-shadow-hybrid-rebuild-design.md`):**
- ShadowRegistry + invariant 2 (one-per-slot) → Tasks 2, 5. ✓
- ShadowDiagnostics as auditor + "nothing casts except through the registry" (verified by `--shadowcheck` rogue detection) → Task 5. ✓
- Pluggable owners / one-per-slot model → Task 1 (`ShadowSlot`, `IShadowOwner`). ✓
- "Registry before effects" (audit), no visual change → Tasks 3–4 (existing march wrapped, passive). ✓
- Invariant 1 (world-anchored) — Phase 0 ships the harness scaffold + the existing dbg_hz_mask manual yaw eye-gate; **automated reprojection check is deferred** to the first owner whose math isn't already proven yaw-independent (Phase 1+). Noted as a follow-up, not a silent gap.
- Phases 1–5 (horizon-map far owner, AO, CSM, blend, multi-source) → out of scope for this plan; each gets its own plan. The far-owner bake's distant-occluder design question is flagged in the spec's Open Decisions and resolved in Phase 1's plan.

**Placeholder scan:** No TBD/TODO/"handle errors"/"similar to" — every step has complete code or an exact command. ✓

**Type consistency:** `IShadowOwner` members (`Name`/`Slot`/`Enabled`/`IsActive`/`DiagId`/`Tick`) are used identically in `HorizonMarchOwner` (Task 3) and `ShadowRegistry` (Task 2). `ShadowRegistry.ActiveDiagIds()`/`CheckInvariants(out string)` signatures match their call in `ShadowCheck.Run` (Task 5). `ShadowDiagnostics.Capture`/`.Snapshot.Owners`/`.ToProfileLine` match `scripts/lab/ShadowDiagnostics.cs`. ✓

**Note for the implementer:** if the `_Process` camera-push block or `TerrainLabUI.cs` line numbers have shifted, anchor on the symbols (`InitReview();`, the camera-world-position push, the `--fieldcheck` parse line) rather than the line numbers.
