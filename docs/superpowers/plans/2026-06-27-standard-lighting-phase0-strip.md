# Standard Lighting Stack — Phase 0 (Strip) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the custom heightfield-march terrain shadow + its scaffolding, rework review key-4 to toggle engine Sun shadows, and bank a clean `standard-stack-baseline` tag before any engine shadow/GI is enabled.

**Architecture:** Pure subtraction. The terrain already uses standard engine lighting (no `light()` in `ground.gdshader`), so removing the `AO`/`AO_LIGHT_AFFECT` hack and the `hz_*` march leaves a plain standard-PBR terrain that the engine lights. Delete the C# shadow-owner/registry abstraction whose only owner was the march. No new features in this phase.

**Tech Stack:** Godot 4.6.2 mono (C#), GLSL (`ground.gdshader`), Vulkan Forward+.

## Global Constraints

- No TDD (GPU/visual project). Verification = `dotnet build WG16.csproj` (0 errors) + windowed `--review=4` capture + eye-gate. Headless only compile-checks shaders (`--headless --import`); compute bakes NullRef headless, so run windowed to actually render.
- Launch: `"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- <user args>` — the bare `--` separator is REQUIRED or user flags no-op.
- C# edits need `dotnet build` to take effect (the player binary does NOT rebuild C#). Shaders hot-compile.
- `cam_world` uniform in `ground.gdshader` MUST stay (CDLOD geomorph uses it at lines 29/237/448). Only `sun_dir_to` + the `hz_*` set is march-only.
- Keep the sky/cloud/day-night arc and the Phase-fix that made the Sun disc visible (`dbg_sun=true`) — do NOT reintroduce `dbg_sun=false`.

---

### Task 1: Strip the march from `ground.gdshader`

**Files:**
- Modify: `shaders/ground.gdshader` (uniforms ~103–115; `horizon_shadow()` ~361–403; fragment hooks ~516–522 and the `dbg_hz_mask` block ~537–548)

**Steps:**

- [ ] **Step 1: Delete the `hz_*` + `sun_dir_to` uniform block.** Remove lines defining `hz_on`, `sun_dir_to`, `hz_steps`, `hz_maxdist`, `hz_stride0`, `hz_growth`, `hz_softness`, `hz_strength`, `hz_sun_gate`, `hz_full_dist`, `hz_fade_dist` and the comment header above them (the block starting `// Terrain heightfield horizon shadow.`). Leave `ground_rough` and the `detail_fade_*` block intact.

- [ ] **Step 2: Delete the `horizon_shadow()` function** entirely (`float horizon_shadow(vec2 surf_xz, float surf_h, vec3 sdir) { ... }`).

- [ ] **Step 3: Remove the fragment shadow hook.** Delete the block:
```glsl
    if (hz_on) {
        if (dot(v_normal, normalize(sun_dir_to)) > 0.0) {
            AO = horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to));
            AO_LIGHT_AFFECT = 1.0;
        }
    }
```

- [ ] **Step 4: Remove the `dbg_hz_mask` block** from `fragment()`:
```glsl
    if (dbg_hz_mask) {
        float hz_dbg = horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to));
        ALBEDO = vec3(0.0);
        EMISSION = vec3(1.0 - hz_dbg);
        AO = 1.0;
        AO_LIGHT_AFFECT = 0.0;
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }
```
Also remove the `uniform bool dbg_hz_mask` declaration (grep `dbg_hz_mask` in the file to find it).

- [ ] **Step 5: Verify the shader compiles.** Run:
```
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path . --headless --import 2>&1 | grep -iE "ground.gdshader|error" | head
```
Expected: no shader compile errors referencing `ground.gdshader` (an unrelated headless compute NullRef is fine). If it complains about an undefined identifier (`hz_*`, `sun_dir_to`, `horizon_shadow`, `dbg_hz_mask`), a reference was missed — grep and remove it.

---

### Task 2: Delete the C# shadow-owner/registry scaffolding

**Files:**
- Delete: `scripts/lab/HorizonMarchOwner.cs`, `scripts/lab/IShadowOwner.cs`, `scripts/lab/ShadowRegistry.cs`, `scripts/lab/ShadowCheck.cs`, `scripts/lab/TerrainLabUI.Shadows.cs` (+ their `.uid` siblings)
- Evaluate: `scripts/lab/ShadowDiagnostics.cs` — keep ONLY if it is a generic shadow-draw profiler reusable for Phase-1 CSM; if it exists solely to be audited against `ShadowRegistry`, delete it too.
- Modify: `scripts/lab/TerrainLabUI.cs`, `scripts/lab/TerrainLabUI.Process.cs` (remove `InitShadows()`, `TickShadows()`, `RunShadowCheckIfRequested()` calls + `_shadowRegistry` field refs)

**Steps:**

- [ ] **Step 1: Inspect `ShadowDiagnostics.cs`** to decide keep/delete:
```
grep -nE "class ShadowDiagnostics|ShadowRegistry|public" scripts/lab/ShadowDiagnostics.cs | head
```
If every public method takes/returns `ShadowRegistry`/`IShadowOwner`, delete it. If it independently counts engine shadow draws (e.g., from `RenderingServer`), keep it.

- [ ] **Step 2: Delete the scaffolding files** (decision from Step 1 applied):
```
git rm scripts/lab/HorizonMarchOwner.cs scripts/lab/IShadowOwner.cs scripts/lab/ShadowRegistry.cs scripts/lab/ShadowCheck.cs scripts/lab/TerrainLabUI.Shadows.cs
```
(`git rm` removes the `.uid` siblings too if tracked; otherwise `rm` them.)

- [ ] **Step 3: Remove call sites.** In `scripts/lab/TerrainLabUI.cs` and `scripts/lab/TerrainLabUI.Process.cs`, delete every line calling `InitShadows()`, `TickShadows(`, `RunShadowCheckIfRequested()`, and any `_shadowRegistry`/`ShadowRegistry`/`HorizonMarchOwner`/`ShadowCheck` reference. Use:
```
grep -rnE "InitShadows|TickShadows|RunShadowCheckIfRequested|_shadowRegistry|ShadowRegistry|HorizonMarchOwner|ShadowCheck\b|IShadowOwner|ShadowSlot" scripts/
```
and remove each remaining hit (skip `ShadowDiagnostics` if kept).

- [ ] **Step 4: Build.** Run `dotnet build WG16.csproj 2>&1 | grep -cE "error CS"` → expect `0`. Fix any `CS0103`/`CS0246` (missing name/type) by removing the orphaned reference.

---

### Task 3: Strip the march CLI flags + lab controls

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Cli.cs` (fields ~70–71, ~292; parse ~149–150, ~161; apply ~233–234)
- Modify: `scripts/lab/TerrainLabUI.Apply.cs` (the `case "sun"` stays; remove any `hz_*` / `dbg_hz_mask` cases if present)
- Modify: `scripts/lab/LabCliSequences.cs` (remove any `--horizon`/`--hzmask`/`--shadowcheck` sequence refs)
- Modify: `data/lab_controls.json` (remove every `hz_*` control entry)

**Steps:**

- [ ] **Step 1: Remove CLI fields + parsing + apply in `TerrainLabUI.Cli.cs`:** delete `_horizonShadowCli`, `_hzMaskCli`, `_shadowCheckCli` fields; the `--horizon=`, `--hzmask=`, `--shadowcheck` parse branches; and the apply lines:
```csharp
        if (_horizonShadowCli >= 0) { OverrideToggle("hz_on", _horizonShadowCli == 1); }
        if (_hzMaskCli >= 0) { _terrain.SetBool("dbg_hz_mask", _hzMaskCli == 1); }
```

- [ ] **Step 2: Remove `hz_*` controls from `data/lab_controls.json`.** Delete every object whose `id` starts with `hz_` (e.g. `hz_on`, `hz_strength`, `hz_steps`, `hz_maxdist`, `hz_stride0`, `hz_growth`, `hz_softness`, `hz_sun_gate`, `hz_full_dist`, `hz_fade_dist`). Verify valid JSON:
```
python -c "import json;json.load(open('data/lab_controls.json'));print('ok')"
```
Expected: `ok`.

- [ ] **Step 3: Clean stragglers in `LabCliSequences.cs` / `TerrainLabUI.Apply.cs`:**
```
grep -rnE "hz_|horizon|hzmask|shadowcheck|dbg_hz_mask" scripts/lab/LabCliSequences.cs scripts/lab/TerrainLabUI.Apply.cs
```
Remove each hit (leave the legitimate `case "sun"` visibility toggle).

- [ ] **Step 4: Build.** `dotnet build WG16.csproj 2>&1 | grep -cE "error CS"` → `0`.

---

### Task 4: Rework review key-4 into the engine-shadow A/B

**Files:**
- Modify: `scripts/lab/LabReviewController.cs` (`case 4:` ~185–238; `FrameSunForShadowReview()` ~392–405)

**Steps:**

- [ ] **Step 1: Replace the hz_* body of `case 4`.** Remove every `Set("hz_*", ...)` and the `_shadowReviewOn = ... ControlBool("hz_on")` / `Set("hz_on", ...)` / `_terrain.SetBool("hz_on", ...)` lines. Drive the toggle off the Sun's engine shadow instead. New toggle + apply (keep the mood/sun-disc/freeze-time setup, `dbg_sun=true`, `atmosphere_on=true`):
```csharp
                var sunNode = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
                bool wantShadow = _lastPreset == 4 ? !(sunNode?.ShadowEnabled ?? false) : true;
                if (sunNode != null) { sunNode.ShadowEnabled = wantShadow; }
```
Update `title`/`judge` strings to describe the engine cast shadow (drop "horizon shadow *" references). Keep `if (_lastPreset != 4) { FrameSunForShadowReview(); }`.

- [ ] **Step 2: Restore a sensible review vantage.** In `FrameSunForShadowReview()`, set a mid vantage good for near-bubble shadows (revert the 900/320 experiment to a steadier framing):
```csharp
        cam.Position = backHoriz * 1200f + new Vector3(0f, 600f, 0f);
        cam.LookAt(cam.GlobalPosition + toSun, Vector3.Up);
```

- [ ] **Step 3: Build + capture.**
```
dotnet build WG16.csproj 2>&1 | grep -cE "error CS"   # 0
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- --review=4 --auto-shot="C:/Users/josep/AppData/Local/Temp/claude/c--Wg16/4401ae95-8873-4126-9bef-2e3d26e43550/scratchpad/phase0.png" 2>&1 | grep -iE "review-shadow|error CS" | head
```

- [ ] **Step 4: Eye-gate `phase0.png`.** Confirm: terrain renders correctly lit (standard PBR, no acne, no `hz` artifacts), sun disc visible, and NO terrain cast shadow yet (Sun `ShadowEnabled` defaults false in scene; key-4 first press sets it true but the Sun scene node + CDLOD casters are not configured until Phase 1, so expect little/no shadow — that is correct for Phase 0).

---

### Task 5: Bank the clean baseline

**Steps:**

- [ ] **Step 1: Final grep — no march remnants:**
```
grep -rnE "hz_on|horizon_shadow|dbg_hz_mask|HorizonMarchOwner|ShadowRegistry|_horizonShadowCli" scripts/ shaders/ data/ | grep -v "docs/"
```
Expected: empty.

- [ ] **Step 2: Commit + tag.**
```
git add -A
git commit -m "Phase 0: strip custom heightfield-march terrain shadow + scaffolding

Remove horizon_shadow march + hz_* uniforms from ground.gdshader, the
ShadowRegistry/owner/check scaffolding, the --horizon/--hzmask/--shadowcheck
CLI, and hz_* lab controls. Rework review-4 to A/B the Sun's engine
ShadowEnabled. Terrain back to plain engine-lit standard PBR; no terrain
shadow. Clean slate before the standard Forward+ stack rebuild.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
git tag standard-stack-baseline
```

- [ ] **Step 3: Confirm the tag.** `git tag -l standard-stack-baseline` → prints the tag.
```

## Self-Review

**Spec coverage (Phase 0 of the spec):** strip shader march ✓ (T1), retire scaffolding + ShadowRegistry ✓ (T2), strip CLI + controls ✓ (T3), rework review-4 to Sun.ShadowEnabled ✓ (T4), revert hz experiments ✓ (T2/T4), commit + tag `standard-stack-baseline` ✓ (T5). `dbg_sun=true` preserved (Global Constraints + T4). `cam_world` preserved (Global Constraints + T1).

**Placeholder scan:** none — all deletions name exact symbols/lines; the one judgment call (`ShadowDiagnostics` keep/delete) has an explicit decision rule in T2 Step 1.

**Type consistency:** `sunNode.ShadowEnabled` (Godot `DirectionalLight3D.ShadowEnabled`) and node path `/root/TerrainLabRoot/Sun` match existing usage in `LabReviewController.cs`/`LightingComposer.cs`. `OverrideToggle`/`_terrain.SetBool` references are being removed, not added.
