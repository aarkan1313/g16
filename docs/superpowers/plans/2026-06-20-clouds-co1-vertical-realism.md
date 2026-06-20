# Clouds CO-1 — Vertical Realism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each cloud deck a tunable vertical density profile (flat-ish rounded base → faded/anvil top) so decks read as 3D volumes instead of flat slabs — behind a default-off toggle that reproduces the approved cumulus look exactly.

**Architecture:** Add three per-layer profile fields (`ProfileBottom`, `ProfileTop`, `Anvil`) to the packed GPU layer buffer; grow the buffer stride from 5 to 6 vec4/layer in lockstep across the C# packer and BOTH compute shaders (raymarch + shadow — fields must stay byte-identical or ground shadows desync from the visible cloud). A new `height_profile(h, …)` function multiplies `layer_density`'s shape; at neutral field values (0, 1, 0) it returns ~1.0 (no-op), so the default look is unchanged. A Clouds-tab toggle + 3 knobs drive layer 0 (the knob-driven cumulus deck); the user tunes live and we bake the approved values as defaults after the eye-gate.

**Tech Stack:** Godot 4.6 mono (C#), GLSL compute (cloud raymarch + cloud shadow), `Std430Writer` GPU buffer packing, data-driven lab UI (`lab_controls.json` → `cloudf` routing).

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = long-term-best — lead with the better option, no shortcut traded for speed. (HANDOFF §2)
- **Discipline rule:** CO-1 is ONE sub-phase. Build it behind a toggle defaulting to the approved look; STOP at the eye-gate. Do NOT start CO-2 (types/cirrus) until the user passes CO-1 live. (ROADMAP)
- **Shadow lockstep (the #1 CO-1 risk):** the per-layer DENSITY layout and the `height_profile` math MUST be byte-identical between `shaders/cloud_raymarch.glsl` and `shaders/cloud_shadow.glsl`. Fields 0-11 stay untouched; the new fields (19/20/21) and the profile function must mirror exactly. Verify with `--shadowcheck`. (CloudLayers.cs header; spec Risk 1)
- **No TDD — GPU/visual project.** Per-task verification = `dotnet build WG16.csproj` → headless `--import` (compiles both compute shaders) → `--auto-shot` A/B + `--profmove`, and for shader changes `--shadowcheck`. The ONLY look-gate is the user flying `scenes/review.tscn`. Never judge a motion artifact from a still. (HANDOFF §4, ROADMAP)
- **Coordination:** stay in sky/light files. The files this plan touches (`CloudLayers.cs`, `CloudVolume.cs`, `cloud_raymarch.glsl`, `cloud_shadow.glsl`, `lab_controls.json`) are this lane's; `lab_controls.json` is shared → additive edits only, `git add` your paths explicitly (never `git add -A`). Commit by default; push only when the user asks. (HANDOFF Coordination)
- **Run (one Godot at a time, kill strays first):** `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` then `"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`. Headless `--import` uses the `_console.exe` variant. Local-RD compute can't run `--headless` (run windowed to execute; `--import` only compile-checks).
- **Benign log lines:** `not a valid texture` / `us is null` radiance-rebake spam on any lighting change is cosmetic — documented in CloudVolume + DECISIONS. NOT a bug; don't chase.

**Paths (absolute):**
- Godot windowed: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`
- Godot console (headless `--import`/auto-shot): the `_console.exe` sibling of the above.
- Project: `C:\Wg16\wg-16-project`

---

## File Structure

| File | Responsibility | Change |
|------|---------------|--------|
| `scripts/lab/CloudLayers.cs` | Layer record + schema + GPU packing (one source of truth) | Add 3 profile fields; `Stride` 20→24; `Build` defaults; `Pack` writes 19/20/21 + 22/23=0 |
| `shaders/cloud_raymarch.glsl` | Sky cloud raymarch (visible clouds) | `layers[40]`→`[48]`; `LF` stride `*5`→`*6`; add `height_profile()`; multiply in `layer_density` |
| `shaders/cloud_shadow.glsl` | Ground cloud-shadow map (must match raymarch density) | Byte-identical mirror of the raymarch changes |
| `scripts/lab/CloudVolume.cs` | Cloud subsystem driver; layer-0 knob override; param push | `_profileOn` + 3 profile fields + setters + `SetKnob` cases; apply to layer 0 in `PackLayers` |
| `data/lab_controls.json` | Data-driven lab controls (Clouds tab) | Add `profile_on` toggle + 3 `cloudf` knobs (additive) |

---

## Background — exact current layout (so the lockstep edit can't drift)

**Per-layer packed buffer** (`CloudLayers.Pack`, `Stride = 20` floats = 5 vec4/layer):
- Density (fields 0-11, byte-identical both shaders): `0 alt · 1 thick · 2 size · 3 cell · 4 covW · 5 dens · 6 opac · 7 type · 8 edge · 9 detail · 10 detailSize · 11 noiseId`
- Lighting (12-18, raymarch-only): `12 phaseG · 13 phaseIso · 14 albedo · 15 sunAbsorb · 16 tintR · 17 tintG · 18 tintB`
- `19` = reserved (currently `0f`).

**Shader accessor (both shaders):** `#define LF(i, f) P.layers[(i)*5 + ((f)>>2)][(f)&3]` and `vec4 layers[40];`.

**CO-1 adds** (grows to `Stride = 24` = 6 vec4/layer, `layers[48]`, `LF` stride `*6`):
- `19` = `ProfileBottom` (was reserved; default `0`)
- `20` = `ProfileTop` (default `1`)
- `21` = `Anvil` (default `0`)
- `22`, `23` = reserved (`0f`) — spare for CO-2 `ShapeMode` later (no extra stride growth then).

At neutral `(bottom=0, top=1, anvil=0)`, `height_profile` returns ~1.0 → no visual change (proves the no-regression gate).

The vertical density multiply currently in `layer_density` is `shape *= type_gradient(h, type);`. CO-1 multiplies an ADDITIONAL `height_profile(h, …)` alongside it (keeps the proven `type_gradient` intact).

---

## Task 1: Grow the per-layer buffer to carry profile fields (atomic — 3 files, no behavior change yet)

**Files:**
- Modify: `scripts/lab/CloudLayers.cs` (record, `Stride`, `Build`, `Pack`)
- Modify: `shaders/cloud_raymarch.glsl:45,53` (`layers[40]`→`[48]`, `LF` stride)
- Modify: `shaders/cloud_shadow.glsl:32,37` (`layers[40]`→`[48]`, `LF` stride)

**Interfaces:**
- Produces: `CloudLayer` record gains `float ProfileBottom, float ProfileTop, float Anvil` (inserted after `TintB`, before `NoiseId`). `CloudLayers.Stride == 24`. Packed buffer = 8×24 = 192 floats = 48 vec4. Shaders read fields 19/20/21 via `LF(i,19/20/21)`.
- Rationale for atomicity: the C# `Stride` and the shaders' `layers[]`/`LF` stride MUST change together. If only C# grows, the buffer becomes 192 floats but a stride-5 shader reads 40 vec4 → scrambled layout → NO CLOUDS (the std430 drift class documented in `cloud_raymarch.glsl`).

- [ ] **Step 1: Add the 3 fields to the `CloudLayer` record**

In `scripts/lab/CloudLayers.cs`, change the record (lines 11-17). Insert the profile fields after `TintR, TintG, TintB` and before `NoiseId`, and update the header comment:

```csharp
/// One cloud deck. Data only — no marching/scene knowledge (separation of concerns).
/// Fields 0-11 are DENSITY (must stay byte-identical between raymarch & shadow shaders);
/// fields 19-21 are the VERTICAL PROFILE (CO-1) — ALSO density-affecting, so they too are
/// read + applied byte-identically by BOTH shaders (height_profile). PhaseG..TintB are
/// per-deck LIGHTING (raymarch only — shadow ignores them).
public readonly record struct CloudLayer(
    float Altitude, float Thickness, float Size, float CellScale,
    float CoverageWeight, float Density, float Opacity, float Type,
    float Edge, float Detail, float DetailSize,
    float PhaseG, float PhaseIso, float Albedo, float SunAbsorb,
    float TintR, float TintG, float TintB,
    float ProfileBottom, float ProfileTop, float Anvil,
    int NoiseId, bool Enabled);
```

- [ ] **Step 2: Bump `Stride` to 24 and document the new layout**

Change line 25:

```csharp
    public const int Stride = 24;   // floats per layer: 12 density (0-11) + 7 lighting (12-18) + reserved (19→profile) + 3 profile (19-21) + 2 reserved (22-23) = 6 vec4. See Pack.
```

- [ ] **Step 3: Add neutral defaults in `Build`**

In `CloudLayers.Build` (lines 33-39), add the profile keys to the constructor call after the tint line, before the `noise_id` line. Neutral defaults `(0, 1, 0)` = no-op profile:

```csharp
            F("tint_r", 1f), F("tint_g", 1f), F("tint_b", 1f),
            F("profile_bottom", 0f), F("profile_top", 1f), F("anvil", 0f),
            I("noise_id", 0), B("enabled", true));
```

- [ ] **Step 4: Write the profile fields in `Pack` (indices 19/20/21; 22/23 reserved)**

In `CloudLayers.Pack` (lines 103-107), replace the reserved-slot line so the per-deck lighting block ends at 18 and the profile fills 19-21:

```csharp
            // 12-18: per-deck LIGHTING (raymarch only)
            packed[o + 12] = L.PhaseG;        packed[o + 13] = L.PhaseIso;
            packed[o + 14] = L.Albedo;        packed[o + 15] = L.SunAbsorb;
            packed[o + 16] = L.TintR;         packed[o + 17] = L.TintG;
            packed[o + 18] = L.TintB;
            // 19-21: VERTICAL PROFILE (CO-1) — density-affecting, read by BOTH shaders
            packed[o + 19] = L.ProfileBottom; packed[o + 20] = L.ProfileTop;
            packed[o + 21] = L.Anvil;         packed[o + 22] = 0f;   // reserved (CO-2 ShapeMode)
            packed[o + 23] = 0f;              // reserved
            count++;
```

- [ ] **Step 5: Grow `layers[]` and `LF` stride in the RAYMARCH shader**

In `shaders/cloud_raymarch.glsl`: line 45 `vec4 layers[40];` → `vec4 layers[48];` and update its comment to "8 layers × 24 floats = 6 vec4 PER LAYER (48 vec4)". Line 53 `#define LF(i, f) P.layers[(i)*5 + ((f)>>2)][(f)&3]` → `#define LF(i, f) P.layers[(i)*6 + ((f)>>2)][(f)&3]`. Update the field-map comment (lines 50-52) to add `19 profileBottom,20 profileTop,21 anvil, 22-23 reserved`.

- [ ] **Step 6: Grow `layers[]` and `LF` stride in the SHADOW shader (byte-identical)**

In `shaders/cloud_shadow.glsl`: line 32 `vec4 layers[40];` → `vec4 layers[48];` and its comment to "8 layers × 6 vec4 (CloudLayers.Pack order); 19-21 = vertical profile (applied), 12-18 lighting (ignored)". Line 37 `#define LF(i, f) P.layers[(i)*5 + ((f)>>2)][(f)&3]` → `#define LF(i, f) P.layers[(i)*6 + ((f)>>2)][(f)&3]`.

- [ ] **Step 7: Build + compile-check both shaders**

Run:
```
dotnet build WG16.csproj
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import
```
Expected: build succeeds; import prints no `cloud_raymarch.glsl:`/`cloud_shadow.glsl:` SPIR-V compile errors (those would surface from `InitCompute`'s `PrintErr`). The benign radiance-rebake lines are OK.

- [ ] **Step 8: Verify NO visual regression (the buffer grew but nothing reads 19-21 yet)**

Run windowed with a fixed cloud scene and capture a shot:
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5 --auto-shot=/c/tmp/co1_t1_buffergrow.png
```
Expected: clouds render normally (NOT scrambled/absent) — confirms the stride grew correctly in lockstep. If clouds vanish or scramble, the C#/shader stride is mismatched; re-check Steps 4-6 before proceeding.

- [ ] **Step 9: Commit**

```bash
git add scripts/lab/CloudLayers.cs shaders/cloud_raymarch.glsl shaders/cloud_shadow.glsl
git commit -m "clouds(CO-1): grow per-layer buffer to 6 vec4 for vertical-profile fields

Add ProfileBottom/ProfileTop/Anvil (fields 19-21) to the packed cloud-layer
buffer; Stride 20->24, layers[40]->[48], LF stride *5->*6 in lockstep across
CloudLayers.Pack + both compute shaders. Fields carried but unused yet (neutral
defaults) -> no visual change. Shadow lockstep preserved (fields 0-11 untouched).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Apply the vertical density profile (atomic — both shaders, byte-identical)

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` (add `height_profile`; extend `layer_density` signature + call)
- Modify: `shaders/cloud_shadow.glsl` (identical mirror)

**Interfaces:**
- Consumes: per-layer fields 19/20/21 from Task 1 (`LF(i,19/20/21)`).
- Produces: `float height_profile(float h, float pBottom, float pTop, float anvil)` — identical in both shaders. `layer_density(...)` gains 3 trailing params `(float pBottom, float pTop, float anvil)` and applies `shape *= height_profile(h, pBottom, pTop, anvil);` immediately after the existing `shape *= type_gradient(h, type);`. `density_all` passes `LF(i,19), LF(i,20), LF(i,21)`.
- Neutral `(0,1,0)` → `height_profile` ≈ 1.0 for `h ∈ (0,1)` → no-op (regression-safe).

- [ ] **Step 1: Add `height_profile()` to the RAYMARCH shader**

In `shaders/cloud_raymarch.glsl`, add this function directly after `type_gradient` (after line 64):

```glsl
// Vertical density profile WITHIN a deck (CO-1). Turns a flat slab into a 3D body:
//   pBottom = height fraction over which density rounds up from the base (flat-ish bottom),
//   pTop    = height fraction at which density begins fading to the top,
//   anvil   = 0 cumulus (taper) .. 1 cumulonimbus (a spreading top lobe near the crown).
// NEUTRAL (0,1,0) returns ~1.0 across the body so the approved look reproduces exactly.
// MUST be byte-identical to cloud_shadow.glsl's height_profile (density-affecting → shadows).
float height_profile(float h, float pBottom, float pTop, float anvil){
    float bottom = smoothstep(0.0, max(pBottom, 1e-4), h);   // rounded base
    float top    = 1.0 - smoothstep(pTop, 1.0, h);           // faded top
    // anvil: a secondary density lobe just below the crown so tops spread instead of tapering.
    float bump = anvil * smoothstep(pTop, mix(pTop, 1.0, 0.5), h) * (1.0 - smoothstep(0.85, 1.0, h));
    return bottom * max(top, bump);
}
```

- [ ] **Step 2: Extend `layer_density` signature + apply it (RAYMARCH)**

In `shaders/cloud_raymarch.glsl`, change the `layer_density` signature (lines 88-90) to add the 3 trailing params:

```glsl
float layer_density(vec3 p, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW,
                    float pBottom, float pTop, float anvil){
```

Then change the `type_gradient` line (line 129) to also multiply the profile:

```glsl
    shape *= type_gradient(h, type);
    shape *= height_profile(h, pBottom, pTop, anvil);
    if (shape <= 0.0) return 0.0;
```

- [ ] **Step 3: Pass the profile fields from `density_all` (RAYMARCH, both overloads' shared call)**

In `shaders/cloud_raymarch.glsl`, update the `layer_density` call inside `density_all` (lines 159-160) to pass `LF(i,19), LF(i,20), LF(i,21)`:

```glsl
        float d = layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4),
            LF(i,19), LF(i,20), LF(i,21));
```

- [ ] **Step 4: Mirror ALL THREE changes byte-identically into the SHADOW shader**

In `shaders/cloud_shadow.glsl`:
1. Add the SAME `height_profile()` function after `type_gradient` (after line 47) — copy verbatim from Step 1.
2. Extend the `layer_density` signature (lines 65-67) with the same 3 trailing params (Step 2).
3. After its `shape *= type_gradient(h, type);` (line 100), add `shape *= height_profile(h, pBottom, pTop, anvil);`.
4. Update the `layer_density` call in its `density_all` (lines 120-121) to pass `LF(i,19), LF(i,20), LF(i,21)` (Step 3).

(The shadow shader's detail-erosion constants differ from the raymarch by design — do NOT touch those; only the density-shape profile must match, which it now does.)

- [ ] **Step 5: Build + compile-check both shaders**

Run:
```
dotnet build WG16.csproj
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import
```
Expected: no SPIR-V compile errors for either shader.

- [ ] **Step 6: Verify no-op at neutral defaults (regression gate)**

```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5 --auto-shot=/c/tmp/co1_t2_neutral.png
```
Expected: the shot matches `/c/tmp/co1_t1_buffergrow.png` (clouds unchanged) — because all layers still carry the neutral `(0,1,0)` profile. This proves the profile is wired but dormant until the knobs (Task 3) drive non-neutral values.

- [ ] **Step 7: Verify shadow lockstep**

```
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5 --shadowcheck
```
Expected: `--shadowcheck` reports the cloud-shadow map without errors and consistent with prior baselines (placement/coverage numbers in the same range as before the change — neutral profile doesn't move shadows). Record the numbers in the commit message.

- [ ] **Step 8: Commit**

```bash
git add shaders/cloud_raymarch.glsl shaders/cloud_shadow.glsl
git commit -m "clouds(CO-1): apply per-deck vertical density profile (height_profile)

height_profile(h, bottom, top, anvil) multiplies layer_density's shape in BOTH
the raymarch and the shadow shader (byte-identical) so decks read as 3D volumes
(rounded base, faded/anvil top) not slabs. Neutral (0,1,0) = ~1.0 no-op ->
approved look + shadow placement unchanged (--shadowcheck verified, --auto-shot
matches baseline). Knobs in next task.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Profile toggle + knobs (drive layer 0 from the Clouds tab)

**Files:**
- Modify: `scripts/lab/CloudVolume.cs` (`_profileOn` + 3 fields + setters + `SetKnob`/`SetKnobBool` cases + `PackLayers` override)
- Modify: `data/lab_controls.json` (Clouds-tab toggle + 3 `cloudf` knobs — additive)

**Interfaces:**
- Consumes: the per-layer profile fields (Tasks 1-2).
- Produces: `CloudVolume` knob ids — `SetKnobBool("profile_on", bool)`, `SetKnob("profile_bottom"|"profile_top"|"anvil", float)`. `PackLayers` layer-0 override sets `ProfileBottom/ProfileTop/Anvil` from `_profileBottom/_profileTop/_anvil` when `_profileOn`, else neutral `(0,1,0)`. Default `_profileOn = false` (approved look).

- [ ] **Step 1: Add the profile state + setters to `CloudVolume`**

In `scripts/lab/CloudVolume.cs`, add fields near `_cellScale` (after line 79):

```csharp
    // CO-1 vertical profile (drives layer 0, the knob cumulus deck). Default OFF = approved
    // slab look; toggle on + tune to make decks read as 3D volumes (eye-gate).
    private bool _profileOn = false;
    private float _profileBottom = 0.15f;   // believable cumulus starting point (applied only when _profileOn)
    private float _profileTop = 0.6f;
    private float _anvil = 0f;
```

- [ ] **Step 2: Route the toggle in `SetKnobBool`**

In `SetKnobBool` (after the `if (knob == "enabled")` block, before the closing brace ~line 571), add:

```csharp
        if (knob == "profile_on") { _profileOn = on; }
```

- [ ] **Step 3: Route the 3 knobs in `SetKnob`**

In `SetKnob`'s switch (after the `overcast_strength` case, line 607), add:

```csharp
            case "profile_bottom":  _profileBottom = Mathf.Clamp(v, 0f, 0.6f); break;   // CO-1 base round-up height
            case "profile_top":     _profileTop = Mathf.Clamp(v, 0.2f, 1f); break;      // CO-1 top fade onset
            case "anvil":           _anvil = Mathf.Clamp(v, 0f, 1f); break;             // CO-1 cumulonimbus top spread
```

- [ ] **Step 4: Apply the profile to layer 0 in `PackLayers`**

In `PackLayers` (lines 403-412), add the profile fields to the layer-0 `with` override so the knobs drive the cumulus deck (neutral when off):

```csharp
    private float[] PackLayers(out int count)
    {
        var eff = new System.Collections.Generic.List<CloudLayer>(_layers);
        var l0 = eff[0];
        float pb = _profileOn ? _profileBottom : 0f;
        float pt = _profileOn ? _profileTop : 1f;
        float av = _profileOn ? _anvil : 0f;
        eff[0] = CloudLayers.WithCumulusLighting(l0 with {
            Altitude = _p.AltitudeM, Thickness = _p.ThicknessM, Size = _p.Size, CellScale = _cellScale,
            CoverageWeight = 1f, Density = _p.Density, Opacity = _p.Opacity, Type = _p.CloudType,
            Edge = _p.Edge, Detail = _p.Detail, DetailSize = _p.DetailSize,
            ProfileBottom = pb, ProfileTop = pt, Anvil = av, NoiseId = 0, Enabled = true });
        return CloudLayers.Pack(eff, out count);
    }
```

- [ ] **Step 5: Add the Clouds-tab controls (additive JSON)**

In `data/lab_controls.json`, add four entries near the other cloud controls (e.g. after `cloud_cell_scale`, line 217). The `cloud`/`cloudf` types route to `_cloud.SetKnobBool`/`SetKnob` — no extra C# routing, no param-no-op risk:

```json
    { "id": "cloud_profile_on", "label": "vertical profile (CO-1)", "tab": "Clouds", "type": "cloud", "cloud": "profile_on", "default": false, "rand": false },
    { "id": "cloud_profile_bottom", "label": "  profile: base round", "tab": "Clouds", "type": "cloudf", "cloud": "profile_bottom", "min": 0.0, "max": 0.6, "default": 0.15, "rand": false },
    { "id": "cloud_profile_top", "label": "  profile: top fade", "tab": "Clouds", "type": "cloudf", "cloud": "profile_top", "min": 0.2, "max": 1.0, "default": 0.6, "rand": false },
    { "id": "cloud_anvil", "label": "  profile: anvil (Cb)", "tab": "Clouds", "type": "cloudf", "cloud": "anvil", "min": 0.0, "max": 1.0, "default": 0.0, "rand": false },
```

- [ ] **Step 6: Build + import**

```
dotnet build WG16.csproj
"<godot_console.exe>" --headless --path /c/Wg16/wg-16-project --import
```
Expected: build + import clean.

- [ ] **Step 7: Self-check that the profile now visibly changes the clouds**

```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn
```
In the lab: Clouds tab → ensure clouds on, coverage ~0.5. Toggle `vertical profile (CO-1)` ON; raise/lower `profile: base round`, `profile: top fade`, and `anvil`. Fly under the deck. Expected: with the toggle ON the deck gains a visible flat-ish base and a rounded/spreading top (3D volume); toggle OFF returns to the current slab. If toggling does nothing, confirm `profile_on` reached `PackLayers` (the layer-0 override) and that `_profileOn` gates the values.

- [ ] **Step 8: Profile cost (must stay within the cloud budget)**

```
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5 --profmove
```
Expected: in-motion ms with profile on ≈ profile off (a couple of `smoothstep`s per density tap). Record the number; if it creeps, note it for the gate.

- [ ] **Step 9: Commit**

```bash
git add scripts/lab/CloudVolume.cs data/lab_controls.json
git commit -m "clouds(CO-1): Clouds-tab vertical-profile toggle + base/top/anvil knobs

profile_on (default OFF = approved slab look) gates 3 knobs that drive layer 0's
ProfileBottom/ProfileTop/Anvil through PackLayers. cloudf routing -> SetKnob; no
new C# routing. Lets the user A/B + tune the 3D vertical profile live at the gate.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Drive the CO-1 eye-gate + bank the result

**Files:**
- Modify (after the gate): `docs/DECISIONS.md` (1 entry), `docs/ROADMAP.md` (CO-1 status line), `docs/NEEDS_REVIEW.md` (CO-1 gate result). `scripts/lab/CloudVolume.cs` + `data/lab_controls.json` defaults if the user wants the approved profile baked as default-on.

**Interfaces:** none — this is the look-gate. The user flies; I drive the changes (set toggles/knobs for them) and never make them tune blind.

- [ ] **Step 1: Set up the review for the user**

Launch windowed `scenes/review.tscn`, clouds on at a few coverages (e.g. 0.35 broken, 0.6 fuller). Be ready to toggle `vertical profile (CO-1)` for an instant A/B and to nudge `base round` / `top fade` / `anvil` on request. Brief the user on what to judge (spec CO-1 gate): **do cumulus decks read as 3D volumes (flat-ish base, rounded/anvil tops), not flat slabs? Does the approved cumulus look reproduce with the toggle OFF? Any motion shimmer or grazing-angle/horizon artifact? Cost OK (`--profmove`)?**

- [ ] **Step 2: Tune to a believable cumulus profile live with the user, settle approved values.**

- [ ] **Step 3: If PASS — bake the approved values as defaults**

If the user wants CO-1 on by default, set `_profileOn = true` and the approved `_profileBottom/_profileTop/_anvil` in `CloudVolume.cs`, and the matching `default`s in `lab_controls.json` (`cloud_profile_on` → `true`, the 3 knob defaults → approved). Keep the toggle so OFF still reproduces the prior slab look. Rebuild + `--import` + a confirming `--auto-shot`.

- [ ] **Step 4: Thin docs (roadmap line + decision entry, not plan-sprawl)**

- `docs/DECISIONS.md`: prepend a dated CO-1 entry (built height_profile on the 6-vec4 lockstep buffer; gate verdict; approved profile values; cost; `--shadowcheck` clean).
- `docs/ROADMAP.md`: update the `#2 Clouds overhaul` line — CO-1 ✅ DONE + GATED, CO-2 (types/cirrus) NEXT.
- `docs/NEEDS_REVIEW.md`: record the CO-1 gate result.

- [ ] **Step 5: Commit the docs + any baked defaults**

```bash
git add docs/DECISIONS.md docs/ROADMAP.md docs/NEEDS_REVIEW.md scripts/lab/CloudVolume.cs data/lab_controls.json
git commit -m "clouds(CO-1): eye-gate result + bank (vertical realism)

<verdict>. Approved profile baked as default (toggle preserved). DECISIONS/ROADMAP/
NEEDS_REVIEW updated. CO-2 (types: cirrus 2D layer + stratus) is next, gated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 6: STOP.** CO-1 gated. Do not start CO-2 until the user picks it (discipline rule: one sub-phase past the last pass).

---

## Self-Review (checked against the spec)

**Spec coverage (CO-1 = "Vertical realism. `height_profile()` in the march + new `CloudLayer` profile fields + Pack/shadow lockstep"):**
- `height_profile()` in the march → Task 2 Steps 1-3. ✅
- New `CloudLayer` profile fields → Task 1 Steps 1-4. ✅
- Pack/shadow lockstep → Task 1 (stride in 3 files) + Task 2 Step 4 (byte-identical shadow mirror) + `--shadowcheck` (Task 2 Step 7). ✅
- Gate: "cumulus decks read as 3D volumes … approved cumulus look reproduces at neutral profile values; cost unchanged" → neutral no-op verified (Task 2 Step 6), knobs to dial in (Task 3), cost check (Task 3 Step 8), live gate (Task 4). ✅
- Spec Risk 1 (shadow/march field drift) → fields 0-11 untouched; new fields mirrored; `--shadowcheck`. ✅
- Spec Risk 3 (profile vs lat-long dome at grazing angles) → called out in Task 4 Step 1 judge criteria (verify at the horizon). ✅
- Spec Risk 4 (perf creep) → `--profmove` in Task 3 Step 8. ✅
- ShapeMode (stratus) is CO-2, NOT CO-1 — correctly excluded; reserved slot 22 left for it. ✅

**Placeholder scan:** all code steps show full code; commands have expected output; no TBD/TODO. ✅

**Type consistency:** `CloudLayer` field names `ProfileBottom/ProfileTop/Anvil` used identically in the record (T1.1), `Build` keys `profile_bottom/profile_top/anvil` (T1.3), `Pack` indices 19/20/21 (T1.4), shader `LF(i,19/20/21)` (T2.3/T2.4), `CloudVolume` fields `_profileBottom/_profileTop/_anvil` + knob ids `profile_bottom/profile_top/anvil` + `profile_on` (T3) + JSON `cloud` routing keys (T3.5). `Stride == 24`, `layers[48]`, `LF` stride `*6` consistent across T1.2/T1.5/T1.6. ✅
