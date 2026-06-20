# Ground Foundation G1 — Rule-Based Placement Engine (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) tracking.

**Goal:** Replace the splat bake's altitude/slope *band* logic with a signal-driven **role placement rule engine** (altitude + slope + signed curvature → a weight per surface ROLE → top-2), and route the *meaningful* baked secondary into the fragment — so the ground's material placement reads as coherent (right material in the right place) instead of "random." Behind a `rule_based` toggle defaulting to the current look.

**Architecture:** All new logic lives in the **bake** (`splat_weights.glsl` + `SplatCompute.cs` + `TerrainLab.RebakeSplat`). The compute shader gains a `role_weights()` function selected by a `rule_based` param (the legacy `zone_weights()` stays for A/B). The runner-up role (already computed as `d1`) becomes the baked secondary (`splat.g`). The fragment gains one `splat_rule_based` bool uniform so it reads the baked secondary (`splat.g`) instead of the fixed `sec_zone[dom]` lookup. Reuses the current 7 materials — palette (G2) and aspect/moisture signals (G3) come later. The placement breakpoints (currently hardcoded in `RebakeSplat`) are **promoted to live re-bake UI sliders** so placement is tunable without code. Baked once → fragment cost unchanged.

**Separation of concerns & infinite-world notes (design intent — keep these true):**
- The bake is a **pure deterministic function** of `(heightfield, rule params)` — same seed → same heights → identical bake, recomputable anywhere. `role_weights()` uses only **local** signals (altitude/slope/curvature) + world-space-continuous noise → **position-independent, no global state** → tiles seamlessly across future infinite chunks (per-chunk bake on demand). Do NOT introduce dependence on absolute chunk identity or a global bake.
- **Separation:** the rule ENGINE (`role_weights` in `splat_weights.glsl`) is isolated from PALETTE (which material fills a role — G2, `ground_palette.json`) and from the COMPOSITING (fragment blend). G1 touches only the engine + its tunable params + the secondary routing. Rule params live as a dedicated group on `TerrainLab` (not mixed into the legacy fragment uniforms).
- **Tunability:** every placement breakpoint is a live re-bake slider on the Splat tab → the user dials placement in by eye, no recompile.

**Tech Stack:** Godot 4.6 mono; GLSL compute (`shaders/splat_weights.glsl`) baked via local RenderingDevice + readback (`SplatCompute.cs`); GLSL spatial shader (`shaders/terrain_lab.gdshader`); C# (`scripts/lab/TerrainLab.cs`, `scripts/lab/TerrainLabUI.Apply.cs`, `scripts/lab/TerrainLabUI.Cli.cs`); data-driven controls (`data/lab_controls.json`).

## Global Constraints (verbatim from the spec + project rules)

- **Verification is GPU/visual — NO test harness, NO TDD / unit tests.** Per task: `dotnet build WG16.csproj` → headless `--import` (compiles GLSL) → `--auto-shot` A/B (`--groundrules=0` vs `1`) at three ranges → `--profile`. **THE gate is the user flying it live at close / mid / far.** Never judge a motion artifact from a still.
- **Perf target: 160+ fps in the final generator.** G1 is baked-once → adds ~0 fragment cost; keep it that way.
- **Build behind a toggle defaulting to the current approved look** (`rule_based` default **false** = legacy bands).
- **ONE Godot process at a time.** Before any relaunch: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found"). **Always** pass `--rendering-driver vulkan`.
- **Build/import order after ANY `.cs`/`.gdshader`/`.glsl` change:** `dotnet build WG16.csproj` → headless `--import` → only then launch windowed.
- **local-RD compute CANNOT run under `--headless`** — the splat bake runs **windowed**; `--headless --import` only compile-checks the GLSL. Verify the bake windowed.
- **std430 param buffer is all-scalar (4-byte) — safe to hand-pack** (the `Std430Writer` warning is for vec2/vec4 alignment, not present here). Keep `SplatCompute.BuildParams`'s manual style; just append fields.
- **Registry gotcha:** a `lab_controls.json` row that needs a *re-bake* (C#-side state, not a live shader uniform) must use `"field"` + `"rebake": true` and a C# case in `SetTerrainField`/the toggle handler — a `"param"` row would route to `SetShaderParameter` and silently no-op (there is no such uniform on the bake side).

## Environment

- **Project root:** `C:\Wg16\wg-16-project`. Scene: `scenes/terrain_lab.tscn`. Camera node: `/root/TerrainLabRoot/Camera`.
- **Console exe (import/headless):** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
- **Windowed exe (live / auto-shot / profile):** same folder, the binary **without** `_console`.
- **CLI already present:** `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<abs.png>`, `--ar=0/1`, `--detail=0/1`, `--splat=0/1`. This unit adds `--groundrules=0/1` (mirrors `--ar=`).

## Key facts (verified against current code)

- **`splat_weights.glsl`** (compute, baked by `SplatCompute`): `main()` already computes per-texel `hh` (height), `slope = 1.0 - n.y`, and `curv = (hl+hr+hd+hu)*0.25 - hh` (**signed: `curv>0` = concave hollow, `curv<0` = convex ridge**). It calls `zone_weights(w, hh, slope, curv, wxz)` (band logic), finds top-2 (`d0` dominant, `d1` runner-up), computes `boundary`, an intra-zone `mix_amt`, and a `companion = clamp(d0-1,0,6)` (index-adjacency), then writes `splat = vec4(d0, secondary, mix_amt, boundary)` where `secondary = (boundary>mix_amt)?d1:companion`.
- **`ParamsBuf`** in `splat_weights.glsl` (std430) currently has 17 scalar fields ending `...edge_noise_m, edge_noise_amp, macro_m`. `SplatCompute.Params` + `BuildParams` mirror it field-for-field into an 80-byte buffer (68 used).
- **`TerrainLab.RebakeSplat()`** builds `Params` from public fields (`MixScaleM`, `MixBias`, `EdgeNoiseM`, `EdgeNoiseAmp`, `MacroM`, `SplatMaskMode`) + **hardcoded** breakpoints (`HValley=100,HSlope=350,HHigh=700,HPeak=950,SlopeCliffLo=0.30,SlopeCliffHi=0.55,BandSoftnessM=120`), bakes, and binds `splat_tex`.
- **`terrain_lab.gdshader` `fragment()`** splat path (~line 507): `dom = int(sp.r+0.5)`; **`sec = clamp(sec_zone[dom],0,6)`** (fixed per-dom lookup — IGNORES `sp.g`); `t = clamp(max(sp.b,sp.a)*mix_strength,0,1)`. `uniform bool splat_on` (~114), `uniform int sec_zone[7]` (~131).
- **Control routing** (`TerrainLabUI.Apply.cs`): `slider` with `"field"` → `SetTerrainField(field, v)` (cases `MixScaleM`/`MixBias`); `toggle` → `_terrain.SetBool(c.Param, ...)` (shader uniform only, no field path yet); any control with `Rebake` true → `RebakeSplat()` after apply. `LabControl` already parses `"field"`.

---

## Task 1: Rule engine in the splat bake (`splat_weights.glsl`)

**Files:** Modify `shaders/splat_weights.glsl`.

**Interfaces:**
- Produces (compute-internal): `void role_weights(out float w[7], float hh, float slope, float curv)` — same `out`-array shape as `zone_weights`, so `main()` can swap between them.
- Consumes: new `ParamsBuf` fields `uint rule_based`, `float curv_k` (added Task 2's C# side must match).

- [ ] **Step 1: Add two fields to the `ParamsBuf`** block (append after `macro_m;`, keeping the existing order):

```glsl
    float edge_noise_m;
    float edge_noise_amp;
    float macro_m;
    uint  rule_based;      // 0 = legacy bands (zone_weights) | 1 = rule engine (role_weights)
    float curv_k;          // curvature scale (m) separating convex ridges from concave hollows
};
```

- [ ] **Step 2: Add `role_weights()`** immediately AFTER the existing `zone_weights()` function (so it can reuse the same `vnoise`/params and is defined before `main()`):

```glsl
// G1 rule engine: assign a weight per surface ROLE from terrain signals (altitude,
// slope, SIGNED curvature). Reuses the 7 slots as roles with the CURRENT materials:
//   0 valley/meadow · 1 valley->slope · 2 slope · 3 slope->cliff · 4 cliff/rock ·
//   5 high alpine · 6 snow. Curvature splits convex breaks (scree/exposed rock) from
//   concave hollows (where soil/grass collects) — the meaning bands alone can't give.
// Aspect + moisture rules come in G3; palette in G2. Same out-array shape as zone_weights.
void role_weights(out float w[7], float hh, float slope, float curv){
    for(int i=0;i<7;i++) w[i]=0.0;
    float soft = band_softness_m;
    float aLow  = 1.0 - smoothstep(h_valley-soft, h_valley+soft, hh);  // low ground
    float aHigh = smoothstep(h_high-soft,  h_high+soft,  hh);          // high ground
    float aMid  = clamp(1.0 - aLow - aHigh, 0.0, 1.0);                 // slopes between
    float aSnow = smoothstep(h_peak-soft,  h_peak+soft,  hh);          // snow band
    float rock  = smoothstep(slope_cliff_lo, slope_cliff_hi, slope);   // steep -> rock/scree
    float ground= 1.0 - rock;
    float k = max(curv_k, 1e-3);
    float convex  = smoothstep(0.0, k, -curv);                         // ridges / breaks
    float concave = smoothstep(0.0, k,  curv);                         // hollows

    w[0] = aLow  * ground * (0.5 + 0.5*concave);                       // meadow/valley (collects in hollows)
    w[1] = aMid  * ground * (1.0 - aHigh) * 0.6;                       // valley->slope transition
    w[2] = aMid  * (0.5*ground + 0.5*rock) * (0.4 + 0.6*convex);       // slope scree on breaks
    w[3] = mix(aMid, aHigh, 0.5) * rock * (0.3 + 0.7*convex) * (1.0 - aSnow); // loose scree below cliffs
    w[4] = rock  * (0.6 + 0.4*convex);                                 // cliff/rock (steep, any altitude)
    w[5] = aHigh * ground * (1.0 - aSnow);                             // high alpine
    w[6] = aSnow * ground;                                             // snow (sheds off steep faces)

    float s=0.0; for(int i=0;i<7;i++) s+=w[i];
    if(s>1e-4) for(int i=0;i<7;i++) w[i]/=s;
}
```

- [ ] **Step 3: Branch `main()` onto the rule engine** and make the secondary the runner-up role. Replace the existing `zone_weights(...)` call and the `companion`/`secondary` block. Find in `main()`:

```glsl
    float w[7];
    zone_weights(w, hh, slope, curv, wxz);
```
Replace with:
```glsl
    float w[7];
    if (rule_based == 1u) { role_weights(w, hh, slope, curv); }
    else                  { zone_weights(w, hh, slope, curv, wxz); }
```

Then find the secondary-selection block:
```glsl
    int companion = clamp(d0 - 1, 0, 6);
    if (d0 == 0) companion = 1; // valley mixes up toward its transition

    // Pack: prefer the boundary's runner-up when near an edge, else the companion.
    // R dominant, G secondary, B intra-zone mix amount, A boundary blend.
    float secondary = (boundary > mix_amt) ? float(d1) : float(companion);
```
Replace with:
```glsl
    // Secondary material. Rule engine: ALWAYS the runner-up ROLE (d1) — spatially
    // meaningful, the whole point of G1. Legacy: the old index-adjacency companion.
    int companion = clamp(d0 - 1, 0, 6);
    if (d0 == 0) companion = 1; // valley mixes up toward its transition
    float secondary = (rule_based == 1u)
        ? float(d1)
        : ((boundary > mix_amt) ? float(d1) : float(companion));
```

- [ ] **Step 4: Build + import (compile-check the GLSL).**
  Run: `dotnet build WG16.csproj` then `"<console exe>" --headless --path . --import`
  Expected: 0 build errors; no `splat_weights.glsl` compile error printed by `--import`. (The bake itself does not run under `--headless`, but the SPIR-V compile is checked at first windowed bake — Task 7 confirms it runs.)

- [ ] **Step 5: Commit.**
```bash
git add shaders/splat_weights.glsl
git commit -m "Ground G1: role_weights rule engine in splat bake (rule_based/curv_k; runner-up role = secondary)"
```

---

## Task 2: Mirror the new params in `SplatCompute.cs`

**Files:** Modify `scripts/lab/SplatCompute.cs`.

**Interfaces:**
- Consumes: `Params` struct (caller `TerrainLab.RebakeSplat` sets it — Task 3).
- Produces: `Params.RuleBased` (uint) + `Params.CurvK` (float) packed into the std430 buffer, matching `ParamsBuf` order from Task 1.

- [ ] **Step 1: Add the two fields to the `Params` struct** (append after `MacroM`):

```csharp
    public struct Params
    {
        public uint Res;
        public float TexelWorld, RegionSize;
        public float HValley, HSlope, HHigh, HPeak, SlopeCliffLo, SlopeCliffHi, BandSoftnessM;
        public float MixScaleM, MixBias;
        public uint MaskMode;
        public float EdgeNoiseM, EdgeNoiseAmp, MacroM;
        public uint RuleBased;   // 0 legacy bands | 1 rule engine
        public float CurvK;      // curvature scale (m)
    }
```

- [ ] **Step 2: Append the two writes in `BuildParams`** (after the `MacroM` write), and bump the comment count. Replace:

```csharp
        // 17 fields, std430 scalar layout (all 4-byte) → pad to 16-byte multiple (80B).
        var b = new byte[80];
```
with:
```csharp
        // 19 fields, std430 scalar layout (all 4-byte) → pad to 16-byte multiple (80B).
        var b = new byte[80];
```
and replace:
```csharp
        U(p.MaskMode); F(p.EdgeNoiseM); F(p.EdgeNoiseAmp); F(p.MacroM);
        return b;
```
with:
```csharp
        U(p.MaskMode); F(p.EdgeNoiseM); F(p.EdgeNoiseAmp); F(p.MacroM);
        U(p.RuleBased); F(p.CurvK);
        return b;
```
(19 × 4 = 76 bytes ≤ 80; layout stays valid.)

- [ ] **Step 3: Build.** Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 4: Commit.**
```bash
git add scripts/lab/SplatCompute.cs
git commit -m "Ground G1: SplatCompute.Params + BuildParams add RuleBased + CurvK (std430)"
```

---

## Task 3: Wire `RuleBased` + `CurvK` through `TerrainLab` + drive the fragment flag

**Files:** Modify `scripts/lab/TerrainLab.cs`.

**Interfaces:**
- Produces: public fields on `TerrainLab` — `bool RuleBased` (false), `float CurvK` (3f), and the placement breakpoints `HValley/HSlope/HHigh/HPeak/SlopeCliffLo/SlopeCliffHi/BandSoftnessM` (defaults = the old hardcoded values). `RebakeSplat()` reads them all into `Params` AND sets the `splat_rule_based` shader uniform so the bake + fragment stay consistent.
- Consumes: `Params.RuleBased`/`CurvK` (Task 2); fragment `uniform bool splat_rule_based` (Task 4).

- [ ] **Step 1: Add the public fields** beside the existing splat-bake fields (after the `SplatMaskMode` line ~23):

```csharp
    public int SplatMaskMode = 2;
    // G1 rule engine: meaningful, signal-driven material placement (vs legacy bands).
    public bool RuleBased = false;   // default off = current approved look
    public float CurvK = 3.0f;       // curvature scale (m): convex ridge vs concave hollow split
    // Placement breakpoints — promoted from hardcoded so they're live re-bake UI knobs.
    // Defaults preserve the previous hardcoded bake values exactly.
    public float HValley = 100f, HSlope = 350f, HHigh = 700f, HPeak = 950f;
    public float SlopeCliffLo = 0.30f, SlopeCliffHi = 0.55f, BandSoftnessM = 120f;
```

- [ ] **Step 2: Read the fields into `Params` + set the fragment flag** in `RebakeSplat()`. Replace:

```csharp
            HValley = 100f, HSlope = 350f, HHigh = 700f, HPeak = 950f,
            SlopeCliffLo = 0.30f, SlopeCliffHi = 0.55f, BandSoftnessM = 120f,
            MixScaleM = MixScaleM, MixBias = MixBias,
            MaskMode = (uint)SplatMaskMode,
            EdgeNoiseM = EdgeNoiseM, EdgeNoiseAmp = EdgeNoiseAmp, MacroM = MacroM,
        };
        var tex = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", tex);
        GD.Print($"TerrainLab: splat baked (mixScale {MixScaleM:F0}, bias {MixBias:F2}, mask {SplatMaskMode})");
```
with:
```csharp
            HValley = HValley, HSlope = HSlope, HHigh = HHigh, HPeak = HPeak,
            SlopeCliffLo = SlopeCliffLo, SlopeCliffHi = SlopeCliffHi, BandSoftnessM = BandSoftnessM,
            MixScaleM = MixScaleM, MixBias = MixBias,
            MaskMode = (uint)SplatMaskMode,
            EdgeNoiseM = EdgeNoiseM, EdgeNoiseAmp = EdgeNoiseAmp, MacroM = MacroM,
            RuleBased = RuleBased ? 1u : 0u, CurvK = CurvK,
        };
        var tex = _splat.Bake(_heights, _res, sp);
        _mat.SetShaderParameter("splat_tex", tex);
        // Fragment must read the baked secondary (splat.g) when the rule engine is on;
        // keep it in lockstep with the bake so the two never disagree.
        _mat.SetShaderParameter("splat_rule_based", RuleBased);
        GD.Print($"TerrainLab: splat baked (rule {(RuleBased ? 1 : 0)}, curvK {CurvK:F1}, snow {HPeak:F0}, mask {SplatMaskMode})");
```

- [ ] **Step 3: Build.** Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 4: Commit.**
```bash
git add scripts/lab/TerrainLab.cs
git commit -m "Ground G1: TerrainLab RuleBased + CurvK fields, RebakeSplat passes them + sets splat_rule_based"
```

---

## Task 4: Fragment reads the baked secondary under the rule engine

**Files:** Modify `shaders/terrain_lab.gdshader`.

**Interfaces:**
- Consumes: `splat_rule_based` set by `TerrainLab.RebakeSplat` (Task 3); `splat_tex.g` (baked runner-up role, Task 1).
- Produces: no new outward interface; changes only which `sec` the splat path blends.

- [ ] **Step 1: Add the uniform** right after the `splat_on` uniform (~line 114):

```glsl
uniform bool splat_on = false;
uniform bool splat_rule_based = false;   // G1: read baked secondary (splat.g) vs fixed sec_zone[dom]
```

- [ ] **Step 2: Use the baked secondary when rule-based.** Find (~line 509):

```glsl
        int dom = int(sp.r + 0.5);
        int sec = clamp(sec_zone[dom], 0, 6);   // per-zone companion (UI-controlled, live)
```
Replace with:
```glsl
        int dom = int(sp.r + 0.5);
        // G1: the rule engine bakes a spatially-meaningful runner-up role into splat.g;
        // use it. Legacy path keeps the fixed per-dom companion lookup.
        int sec = splat_rule_based ? clamp(int(sp.g + 0.5), 0, 6) : clamp(sec_zone[dom], 0, 6);
```

- [ ] **Step 3: Build + import.** `dotnet build WG16.csproj` → `"<console exe>" --headless --path . --import`. Expected: 0 errors; no `terrain_lab.gdshader` parse error.

- [ ] **Step 4: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground G1: fragment reads baked secondary (splat.g) when splat_rule_based"
```

---

## Task 5: Lab controls — toggle + curvature knob (re-bake, on the Splat tab)

**Files:** Modify `scripts/lab/TerrainLabUI.Apply.cs`, `data/lab_controls.json`.

**Interfaces:**
- Consumes: `TerrainLab.RuleBased`/`CurvK` (Task 3).
- Produces: a `toggle` with `"field":"RuleBased"` and a `slider` with `"field":"CurvK"`, both `"rebake": true`; a new `SetTerrainBoolField` handler + a `CurvK` case in `SetTerrainField`.

- [ ] **Step 1: Add a bool-field path to the `toggle` case** in `ApplyControl` (`TerrainLabUI.Apply.cs`). Replace:

```csharp
            case "toggle":
                if (c.Param != null) { _terrain.SetBool(c.Param, c.Value.AsBool()); }
                break;
```
with:
```csharp
            case "toggle":
                if (c.Field != null) { SetTerrainBoolField(c.Field, c.Value.AsBool()); }
                else if (c.Param != null) { _terrain.SetBool(c.Param, c.Value.AsBool()); }
                break;
```

- [ ] **Step 2: Add the new field cases + the bool-field setter.** Replace:

```csharp
    private void SetTerrainField(string field, float v)
    {
        if (field == "MixScaleM") { _terrain.MixScaleM = v; }
        else if (field == "MixBias") { _terrain.MixBias = v; }
    }
```
with:
```csharp
    private void SetTerrainField(string field, float v)
    {
        if (field == "MixScaleM") { _terrain.MixScaleM = v; }
        else if (field == "MixBias") { _terrain.MixBias = v; }
        else if (field == "CurvK") { _terrain.CurvK = v; }
        else if (field == "HValley") { _terrain.HValley = v; }
        else if (field == "HHigh") { _terrain.HHigh = v; }
        else if (field == "HPeak") { _terrain.HPeak = v; }
        else if (field == "SlopeCliffLo") { _terrain.SlopeCliffLo = v; }
        else if (field == "SlopeCliffHi") { _terrain.SlopeCliffHi = v; }
        else if (field == "BandSoftnessM") { _terrain.BandSoftnessM = v; }
    }

    private void SetTerrainBoolField(string field, bool v)
    {
        if (field == "RuleBased") { _terrain.RuleBased = v; }
    }
```

- [ ] **Step 3: Add the rule-engine control group to `data/lab_controls.json`** on the **Splat** tab. Insert after the `band_soft_mult` row (find `"id": "band_soft_mult"` and add after its closing `},`):

```json
    { "id": "rule_based", "label": "rule placement", "tab": "Splat", "type": "toggle",
      "field": "RuleBased", "default": false, "rand": false, "rebake": true },
    { "id": "rule_valley", "label": "valley line (m)", "tab": "Splat", "type": "slider",
      "field": "HValley", "min": 0, "max": 600, "default": 100, "rand": false, "rebake": true },
    { "id": "rule_high", "label": "alpine line (m)", "tab": "Splat", "type": "slider",
      "field": "HHigh", "min": 300, "max": 1200, "default": 700, "rand": false, "rebake": true },
    { "id": "rule_peak", "label": "snow line (m)", "tab": "Splat", "type": "slider",
      "field": "HPeak", "min": 500, "max": 1500, "default": 950, "rand": false, "rebake": true },
    { "id": "rule_slopelo", "label": "rock slope lo", "tab": "Splat", "type": "slider",
      "field": "SlopeCliffLo", "min": 0.05, "max": 0.6, "default": 0.30, "rand": false, "rebake": true },
    { "id": "rule_slopehi", "label": "rock slope hi", "tab": "Splat", "type": "slider",
      "field": "SlopeCliffHi", "min": 0.2, "max": 0.9, "default": 0.55, "rand": false, "rebake": true },
    { "id": "rule_bandsoft", "label": "band soft (m)", "tab": "Splat", "type": "slider",
      "field": "BandSoftnessM", "min": 20, "max": 300, "default": 120, "rand": false, "rebake": true },
    { "id": "curv_k", "label": "curve split (m)", "tab": "Splat", "type": "slider",
      "field": "CurvK", "min": 0.5, "max": 12, "default": 3, "rand": false, "rebake": true },
```

- [ ] **Step 4: Validate JSON.**
  Run: `python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"`
  Expected: `ok`.

- [ ] **Step 5: Build + launch windowed; confirm the two Splat-tab widgets appear and a re-bake fires on toggle.** Kill strays first.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--clouds=0"
```
  Expected: Splat tab shows `rule placement` + `curve split (m)`; toggling `rule placement` prints a new `TerrainLab: splat baked (rule 1, ...)` line (console) and visibly changes ground material placement; `curve split` re-bakes on release.

- [ ] **Step 6: Commit.**
```bash
git add scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git commit -m "Ground G1: Splat-tab toggle (rule placement) + curve-split knob (re-bake; field-routed)"
```

---

## Task 6: `--groundrules=0/1` CLI override (A/B harness)

**Files:** Modify `scripts/lab/TerrainLabUI.Cli.cs`.

**Interfaces:**
- Consumes: `TerrainLab.RuleBased` + `RebakeSplat()` (Task 3).
- Produces: `--groundrules=0/1` flag, applied in `ApplyCliOverrides()` after the existing `--ar` apply (the CLI applies after `ApplyAll()`'s initial bake, so it re-bakes with the flag).

- [ ] **Step 1: Add the field** beside `_terrainDetailCli` (~line 32):
```csharp
    private int _terrainDetailCli = -1;
    private int _groundRulesCli = -1;
```

- [ ] **Step 2: Parse the flag** right after the `--detail=` line (~line 62):
```csharp
            else if (a.StartsWith("--groundrules=")) { _groundRulesCli = a.Substring("--groundrules=".Length) == "1" ? 1 : 0; }
```

- [ ] **Step 3: Apply it** in `ApplyCliOverrides()`, right after the `_terrainDetailCli` apply (~line 114):
```csharp
            if (_groundRulesCli >= 0) { _terrain.RuleBased = _groundRulesCli == 1; _terrain.RebakeSplat(); }
```

- [ ] **Step 4: Build + import + A/B + profile at three ranges (clouds off, isolate ground).**
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
# A/B placement at three ranges:
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,150,500,-25,0"  "--clouds=0" "--groundrules=0" "--auto-shot=C:/tmp/g1_mid_legacy.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,150,500,-25,0"  "--clouds=0" "--groundrules=1" "--auto-shot=C:/tmp/g1_mid_rule.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,400,1800,-22,0" "--clouds=0" "--groundrules=1" "--auto-shot=C:/tmp/g1_far_rule.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0"    "--clouds=0" "--groundrules=1" "--auto-shot=C:/tmp/g1_near_rule.png"
# Profile: confirm baked-once = no fragment cost delta.
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,150,500,-25,0" "--clouds=0" "--groundrules=0" "--profile=3"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,150,500,-25,0" "--clouds=0" "--groundrules=1" "--profile=3"
```
  Expected: `g1_*_rule.png` show DIFFERENT, more spatially-varied material placement than `g1_mid_legacy.png` (rock follows steep, scree on convex breaks, secondary varies across the surface instead of one fixed companion per band). `--profile` legacy vs rule deltas within noise (baked once → no per-frame cost). Read the PNGs to confirm placement changed and nothing is solid-color/NaN.

- [ ] **Step 5: Commit.**
```bash
git add scripts/lab/TerrainLabUI.Cli.cs
git commit -m "Ground G1: --groundrules=0/1 CLI override (A/B harness)"
```

---

## Task 7: Live judging handoff (THE gate)

- [ ] **Step 1:** Kill strays, build, launch windowed (clouds off to isolate ground):
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--clouds=0"
```
- [ ] **Step 2: User judges placement COHERENCE in motion at three ranges** (close / mid / far), toggling `rule placement` (Splat tab) on/off to A/B. Questions: (a) Does material now sit where it makes sense — rock/cliff on steep faces, scree on convex breaks + below cliffs, grass/soil collecting in low concave ground, alpine then snow up high (snow shedding off steep)? (b) Does the *secondary* material now vary believably across the surface (vs the old single fixed companion per band)? (c) Is it COHERENT (reads like a designed landscape) even though the palette is still the drab greys (palette is G2)? Tune `curve split (m)` to taste. The win is *placement logic*, NOT color yet.
- [ ] **Step 3:** On approval → write the **G2 plan** (curated palette: `data/ground_palette.json` + per-role material dropdowns). If placement is wrong (e.g. snow too low, rock too aggressive, hollows not reading): the rules are isolated in `role_weights()` — tune the breakpoints (currently hardcoded in `RebakeSplat`; promote to fields + re-bake sliders if live tuning needs them) and `curv_k`, re-bake, re-judge. Do NOT proceed to G2 until placement reads coherent.

---

## Self-Review notes

- **Spec coverage:** Implements the G1 row of the foundation spec build order — "signals + rule engine in the bake; reuse the current 7 materials; gate = placement COHERENT." Signals used: altitude (`hh`), slope, signed curvature (`curv`) — all already computed in `splat_weights.glsl main()`; aspect + moisture/flow are explicitly deferred to G3 (spec), palette to G2. The arbitrary `companion = dominant-1` index-adjacency is replaced by the runner-up ROLE (`d1`) as the baked secondary, and the fragment now READS that baked secondary (it previously ignored `splat.g` in favor of the fixed `sec_zone[dom]`) — directly fixing the two "random" root causes named in the spec/DECISIONS.
- **Placeholder scan:** No TBD/TODO/stubs. Every shader + C# step is concrete copy-paste. No new GPU compute pass (reuses `SplatCompute`); no new assets.
- **Type/interface consistency:** `ParamsBuf` (GLSL) order `...macro_m, rule_based(uint), curv_k(float)` matches `Params` struct field order and `BuildParams` write order `...F(MacroM); U(RuleBased); F(CurvK)` — std430 all-scalar 4-byte, 76/80 bytes. `role_weights(out float w[7], float, float, float)` matches `zone_weights`'s out-array shape so `main()` swaps cleanly. `TerrainLab.RuleBased`(bool)/`CurvK`(float) ↔ registry `"field":"RuleBased"`/`"field":"CurvK"` ↔ `SetTerrainBoolField`/`SetTerrainField` cases. `splat_rule_based` uniform name identical in shader (`terrain_lab.gdshader`) and the `SetShaderParameter("splat_rule_based", ...)` call. `--groundrules=` mirrors the verified `--ar=`/`--detail=` field+parse+apply pattern.
- **Toggle default = current look:** `RuleBased` defaults false everywhere (C# field, registry row, shader uniform) → startup is the legacy bands look; `rule placement` / `--groundrules=1` opt in. Baked-once → fragment cost unchanged (profile step confirms).
- **Forward-reference check:** `role_weights` defined after `zone_weights`, before `main()` — no forward ref. `ParamsBuf` fields are declared before use in both functions.
