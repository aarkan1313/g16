# Terrain Self-Shadow (Heightfield March) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild terrain self-shadows as a world-anchored heightfield ray-march (no engine shadow maps), starting by stripping the dead CSM baggage so the new system lands on clean ground.

**Architecture:** A single `FarCast` shadow owner drives a per-pixel march toward the sun inside `ground.gdshader`, sampling world-space height (no mesh casters) so the yaw-drift and LOD-pop bugs are structurally impossible. The registry/auditor scaffolding stays as the one-owner-per-slot guardrail; the failed near-CSM owner and its plumbing are deleted.

**Tech Stack:** Godot 4.6 (mono/C#), Vulkan, GLSL compute (RenderingDevice) for any bake. Verification = `dotnet build` + mechanical CLI self-checks (`--shadowcheck`, `--yawab`, `--profmove`) + the user's live windowed eye-gate. **NO TDD** (GPU/visual project convention).

## Global Constraints

- Build: `dotnet build C:\Wg16\wg-16-project\WG16.csproj`. **C# edits need a rebuild** (the player binary does NOT rebuild C#); shaders hot-compile.
- Run windowed + Vulkan only; **headless has no local RenderingDevice** (compute bakes NullRef). `--import` only compile-checks.
- User `--flags` need a bare `--` separator before them, else they silently no-op.
- New C# files flat in `scripts/lab/`; `TerrainLabUI` is `partial`.
- Baseline tag to diff against: `shadowless-clean-2026-06-27`. Commit by default on `experiment/presentation`; do not push unless asked.
- Spec: `docs/superpowers/specs/2026-06-27-terrain-self-shadow-heightfield-march-design.md`.
- **Discipline:** build at most ONE real (eye-gated) slice past the last passed gate. Slice 0 is mechanical (no eye-gate). Each later slice is default-OFF until the user flies it.

---

## SLICE 0 — Strip the baggage (mechanical, no visual change)

Delete the dead CSM debris; keep the registry guardrail; leave the `hz_*` march intact (Slice 1 evolves it).
Deliverable: a build that renders byte-identical to `shadowless-clean-2026-06-27`, with no CSM code left.

### Task 0.1: Remove the CSM owner + its registration

**Files:**
- Delete: `scripts/lab/CsmCastOwner.cs` (+ `scripts/lab/CsmCastOwner.cs.uid` if present)
- Modify: `scripts/lab/TerrainLabUI.Shadows.cs` (InitShadows; the `WantsSunShadow` property — done in 0.3)

- [ ] **Step 1: Delete the owner file**

```bash
cd /c/Wg16/wg-16-project
git rm scripts/lab/CsmCastOwner.cs
rm -f scripts/lab/CsmCastOwner.cs.uid
```

- [ ] **Step 2: Drop its registration + the destruction comment in `InitShadows()`**

In `scripts/lab/TerrainLabUI.Shadows.cs`, replace the body of `InitShadows()` so only the horizon march registers:

```csharp
    private void InitShadows()
    {
        _shadowRegistry = new ShadowRegistry(this);
        _shadowRegistry.Register(new HorizonMarchOwner(_terrain));
    }
```

(Removes the `// DESTROYED back to the clean shadowless baseline…` comment block and the
`_shadowRegistry.Register(new CsmCastOwner(this) { Enabled = false });` line.)

- [ ] **Step 3: Build (expect a known break until 0.3)**

`dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal` — will still reference `WantsSunShadow`; that is removed in Task 0.3. Proceed to 0.2/0.3 before re-checking. (Tasks 0.1–0.3 form one compiling commit; commit at 0.3 Step 4.)

### Task 0.2: Revert the CDLOD caster-gating

**Files:** Modify `scripts/lab/CdlodTerrain.cs`

- [ ] **Step 1: Remove the field + setter** — delete lines `81-84`:

```csharp
    private int _shadowCasterMinLevel = 99;   // leaves with Level >= this cast engine shadows; 99 = none (default)
    /// Gate engine-shadow casting to the finest near leaves (Level 0=coarse/far .. MaxDepth=fine/near). The
    /// near-CSM owner sets this so caster-LOD == visible-LOD (the WG1-15 graveyard mismatch can't occur).
    public void SetShadowCasterMinLevel(int min) { _shadowCasterMinLevel = min; _forceReapply = true; }
```

- [ ] **Step 2: Replace the per-chunk cast branch** — in `ApplyChunk`, replace lines `372-374`:

```csharp
            mi.CastShadow = c.Level >= _shadowCasterMinLevel
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
```

with the plain (pooled slots may carry a stale value, so set it explicitly):

```csharp
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
```

### Task 0.3: Strip the `WantsSunShadow` plumbing

**Files:** Modify `scripts/lab/LightingComposer.cs`, `scripts/lab/ShadowRegistry.cs`, `scripts/lab/TerrainLabUI.Shadows.cs`

- [ ] **Step 1: Hardcode the sun shadow off** — in `scripts/lab/LightingComposer.cs:216`:

```csharp
        sun.ShadowEnabled = false;
```

(was `sun.ShadowEnabled = _host.WantsSunShadow;`)

- [ ] **Step 2: Remove it from the host interface** — in `scripts/lab/LightingComposer.cs`, delete the
`ILightingHost` member (line 22):

```csharp
    bool WantsSunShadow { get; }              // shadow registry owns sun.ShadowEnabled (near-CSM phase)
```

- [ ] **Step 3: Remove the registry property** — in `scripts/lab/ShadowRegistry.cs`, delete the `WantsSunShadow`
property (the `=> _owners.Any(o => o.Slot == ShadowSlot.NearCast && o.Enabled);` block, lines ~51-53) and its
doc comment.

- [ ] **Step 4: Remove the host implementation** — in `scripts/lab/TerrainLabUI.Shadows.cs`, delete:

```csharp
    /// ILightingHost: the shadow registry owns whether the sun casts (read by LightingComposer.Compose).
    public bool WantsSunShadow => _shadowRegistry?.WantsSunShadow ?? false;
```

- [ ] **Step 5: Build — expect 0 errors now**

`dotnet build C:\Wg16\wg-16-project\WG16.csproj -v minimal`
Expected: `0 Error(s)`. If another `ILightingHost` implementer errors on the missing member, delete its
`WantsSunShadow` too (search: `grep -rn "WantsSunShadow" scripts/` should return nothing after this task).

- [ ] **Step 6: Commit Tasks 0.1–0.3**

```bash
git add -A
git commit -m "Slice 0: delete dead near-CSM code (owner, caster-gating, WantsSunShadow)" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

### Task 0.4: Archive the superseded shadow docs

**Files:** Move into `docs/archive/` (create if absent)

- [ ] **Step 1: Move the superseded specs/plans/handoffs**

```bash
cd /c/Wg16/wg-16-project
mkdir -p docs/archive
git mv docs/superpowers/specs/2026-06-27-terrain-shadow-hybrid-rebuild-design.md docs/archive/ 2>/dev/null || true
git mv docs/superpowers/specs/2026-06-24-terrain-horizon-shadows-design.md docs/archive/ 2>/dev/null || true
git mv docs/superpowers/plans/2026-06-27-shadow-rebuild-near-csm.md docs/archive/ 2>/dev/null || true
git mv docs/handoffs/2026-06-27-shadow-view-yaw-start-here.md docs/archive/ 2>/dev/null || true
git mv docs/handoffs/2026-06-24-terrain-horizon-shadows.md docs/archive/ 2>/dev/null || true
```

(Keep `docs/SHADOWS_AUDIT_2026_06_27.md`, the new `…-self-shadow-heightfield-march-design.md` spec, the
Phase-0 registry plan, and `…-lighting-shadow-strip-and-rebuild.md` as live history.)

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "Slice 0: archive superseded shadow specs/plans/handoffs" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

### Task 0.5: Verify clean baseline (mechanical gate — no eye-gate)

- [ ] **Step 1: No CSM symbols remain**

```bash
grep -rniE "CsmCast|WantsSunShadow|SetShadowCasterMinLevel|_shadowCasterMinLevel" scripts/
```
Expected: no matches.

- [ ] **Step 2: `--shadowcheck` green, zero engine shadow draws**

```powershell
. 'C:\Wg16\wg-16-project\tools\godot_vulkan_env.ps1'
& '<godot_console.exe>' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --shadowcheck *> "$env:TEMP\sc.log"; "exit=$LASTEXITCODE"; Select-String "$env:TEMP\sc.log" -Pattern 'SHADOWCHECK'
```
Expected: `exit=0`, `pass=YES`, shadow draws `0`.

- [ ] **Step 3: Render-identical to the tag (no visual change)**

Build, launch windowed, confirm terrain is shadowless and looks identical to `shadowless-clean-2026-06-27`
(no cast shadows, no acne). Slice 0 changes no rendering. If anything differs, a deletion went too far — revert
and re-check. **This is the only confirmation needed for Slice 0** (mechanical; no user eye-gate required).

---

## SLICES 1–4 — Outline (detail after Slice 0 lands)

> These are decomposed with interfaces and approach, but their bite-sized steps are finalized **after** Slice 0
> is committed and (for 2–4) after the prior slice passes its eye-gate — because Slice 1's first design step
> resolves a real choice against the now-clean base, and 2–4's tuning depends on how 1 looks. Designing them now
> is fine; building past one eye-gated slice is the trap the project keeps falling into.

### Slice 1 — Far ridge shadows (first real slice; ends in an eye-gate)

**Design step (do first):** decide the far-march data source on the clean base —
(a) strengthen the EXISTING analytic growing-stride macro march (`horizon_shadow` in `ground.gdshader:361`,
uniforms `hz_*` at `:105-115`; raise default steps, tune softness/fade) — *no new bake, lowest risk*; or
(b) add a camera-anchored **min-max height clipmap** bake (mirror `ChunkFieldCache.cs` + `shaders/field_bake.glsl`)
for accelerated, miss-free long marches. **Recommendation:** ship (a) first to get the look + eye-gate, add (b)
as the acceleration once the look is approved (it's a perf lever, not a look lever).

**Files:** Modify `shaders/ground.gdshader` (rename `hz_*`→`selfshadow_*`, evolve `horizon_shadow`),
`data/lab_controls.json` (rename the `hz_*` controls), `scripts/lab/HorizonMarchOwner.cs` →
`TerrainSelfShadowOwner.cs` (rename; `DiagIds = ["terrain:selfshadow"]`), `scripts/lab/TerrainLabUI.Cli.cs`
(`--horizon`→`--selfshadow`), `scripts/lab/ShadowDiagnostics.cs` (the `hz_on` probe → new uniform).

**Interfaces:** Produces `TerrainSelfShadowOwner : IShadowOwner` (`Slot=FarCast`, `Name="terrain-selfshadow"`).
Consumes the existing `field_macro_height()` and the `sun_dir_to` the shader already has.

**Gate:** long ridge shadows at low sun; `--yawab` world-locked; no pop flying+teleport; `--shadowcheck` green
0 draws; `--profmove` cost recorded; **user live eye-gate** (default-OFF until passed).

### Slice 2 — Near crisp detail

Add the fine per-chunk field-cache height (`chunk_cache`, sampled at `ground.gdshader:261`) for the first
`near_dist` meters of the march; blend to the macro source over a band. **Gate:** crisp near self-shadow, seamless
near→far handoff, world-lock holds, cost recorded, eye-gate.

### Slice 3 — Soft penumbra

Cone / closest-approach contact hardening: penumbra widens with occluder distance; accumulate soft `[0,1]`.
**Gate:** soft far / harder near, natural in motion at multiple times of day, no shimmer, cost, eye-gate.

### Slice 4 — Quality dial + integration + default tuning

Finalize the `Terrain Shadow` lab tab (steps / max_dist / near_dist / softness / strength), CLI mirrors, pick the
flying default at the eye-gate, retire any remaining `hz_`/`horizon` naming, update `performance.md` + memory.
**Gate:** final max-vs-default A/B, eye-gate, default-ON decision.

---

## Self-Review

**Spec coverage:** Slice 0 strip (spec "Slice 0") ✓; far march (Slice 1 = spec Slice 1) ✓; near detail (Slice 2) ✓;
soft penumbra (Slice 3) ✓; dial/tab/CLI/owner-rename (Slice 4 = spec Slice 4) ✓; min-max accel noted as Slice 1
design-step option ✓; verification flags (`--yawab`/`--shadowcheck`/`--profmove`) per slice ✓; objects/cloud/contact-AO
out of scope ✓. The min-max pyramid is presented as a recommendation-deferred perf lever, consistent with the spec's
"behaves like terrain LOD" intent (analytic macro already is world-anchored + LOD-free).

**Placeholder scan:** Slice 0 is fully concrete (exact files/lines/code/commands). Slices 1–4 are explicitly an
OUTLINE by design (discipline rule), not placeholders in an executable task — their detail is authored at slice start.

**Type consistency:** `WantsSunShadow` removed in all four sites it appears (interface, composer, registry, host) —
verified by the 0.5 Step-1 grep. `TerrainSelfShadowOwner` (Slice 1) replaces `HorizonMarchOwner`; `Slot=FarCast`
matches the enum. No symbol referenced that a task doesn't define or delete.
