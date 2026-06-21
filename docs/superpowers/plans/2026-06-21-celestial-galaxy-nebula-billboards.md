# Celestial Galaxy/Nebula v2 — "Billboards" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the rejected C1 procedural-noise galaxy/nebula with discrete, structure-based "billboard" objects rendered live in `cloud_sky.gdshader`, placed via a hybrid seeded-scatter + hero-slot model.

**Architecture:** Two units behind one interface (a packed slot-array uniform). C# (`TerrainLabUI` + `CloudVolume`) owns billboard configuration and writes the slot uniforms on change only; GLSL loops the active slots and renders each in a local 2D tangent-plane disc using *structure* (log-spiral arms, elliptical cores, hard dust lanes, embedded stars), never 3D fbm. No baked texture — the bake path is deleted.

**Tech Stack:** Godot 4.6.2 mono (C# + GDShader compute/sky shaders), `Std430Writer` for packed GPU buffers.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-06-21-celestial-galaxy-nebula-billboards-design.md` (decisions D1–D4).
- Shared branch `experiment/presentation` with the ground chat — **stay in sky/light + cloud files; `git add` explicit paths, NEVER `-A`.** Commit-by-default; push only when asked.
- One Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` before launching.
- Always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`.
- Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`.
- Build: `dotnet build WG16.csproj` (run from `/c/Wg16/wg-16-project`).
- **No unit-test harness for shaders.** Per-task verification = (a) `dotnet build` succeeds, AND (b) for visual tasks, a windowed run + **key `2`** night review (or `--review=2` headless capture cropped to the sky region), eye-gated by the user.
- `night_sky_brightness` stays the LIVE global multiplier; it is the lane's master on/off (0 = truly free).
- GPU param buffers: pack vec4s only, via `Std430Writer` (memory `std430-packing-helper`).
- Headless cannot execute compute (memory `headless-no-local-rendering-device`) — but this lane no longer bakes, so headless `--review=2` *can* render billboards (they're live in the sky shader). Headless `--headless --import` still only compile-checks.
- Lab controls: a non-shader control needs field/setter/scene/id + a C# registry case (memory `lab-registry-param-gotcha`).

---

## Phase 0 — Strip C1 (rejected generator + bake path)

Deliverable: night sky renders **stars + moon + meteors only**; all galaxy/nebula + bake code is gone; build is clean. This is independently verifiable (key 2 shows the night sky unchanged minus the already-off galaxy/nebula).

### Task 0.1: Delete the C1 generator + bake from the sky shader

**Files:**
- Modify: `shaders/cloud_sky.gdshader`
- Delete: `shaders/night_sky_bake.glsl`

**Interfaces:**
- Produces: a `stars_layer()` that returns `(star + meteors(rd)) * fade` (no `mw` term yet — Phase 1 adds `billboards`).

- [ ] **Step 1: Delete the galaxy/nebula uniforms.** In `cloud_sky.gdshader`, remove the block at lines ~77–96: the comment header and `night_sky_brightness` may STAY, but remove `night_sky_tex`, `night_sky_baked`, `gx_core_dir`, `gx_core_size`, `gx_tilt`, `gx_width`, `gx_curve`, `gx_dust`, `gx_core_color`, `gx_arm_color`, `neb_count`, `neb_dir_scale[4]`, `neb_color_dens[4]`. KEEP `uniform float night_sky_brightness ... = 0.25;`.

- [ ] **Step 2: Delete the generator functions.** Remove `ridge()` (lines ~320–326), `galaxy_color()` (~328–359), `nebula_color()` (~360–374), and `fbm3()` (~171–175). Leave `vnoise3()` (cirrus uses it). Verify no other reference: search the file for `fbm3`, `ridge`, `galaxy_color`, `nebula_color` — expect zero hits after removal.

- [ ] **Step 3: Simplify `stars_layer()`.** Replace the `mw` block (lines ~470–482) and the return. New body after the star/rotation setup:

```glsl
    // STARS: magnitude-stratified live point-stars.
    vec3 star = star_field(sr);
    float fade = night_factor * smoothstep(-0.02, 0.12, rd.y);   // night + above horizon
    return (star + meteors(rd)) * fade;
```

(Remove the `vec3 mw = ...; if (night_sky_brightness > 0.0001){...}` block entirely. `night_sky_brightness` returns in Phase 1.)

- [ ] **Step 4: Delete the bake shader.** `rm shaders/night_sky_bake.glsl`.

- [ ] **Step 5: Compile-check.** Run: `"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project --headless --import` (set `GODOT` to the exe path). Expected: imports/compiles with no shader error referencing `cloud_sky.gdshader`.

- [ ] **Step 6: Commit.**

```bash
git add shaders/cloud_sky.gdshader shaders/night_sky_bake.glsl
git commit -m "refactor(sky): strip C1 galaxy/nebula + bake from cloud_sky shader"
```

### Task 0.2: Delete the bake plumbing from AtmosphereCompute

**Files:**
- Modify: `scripts/lab/AtmosphereCompute.cs`

**Interfaces:**
- Produces: `AtmosphereCompute` with NO `GalaxyParams`/`NebulaParams`/`_mw*`/`NightSkyTexture`/`SetNightSky`/`SetNebulae`/`NsParams`/`BakeNightSky`.

- [ ] **Step 1: Remove the structs + state.** Delete `GalaxyParams`/`NebulaParams` structs (lines ~77–78), `_gx`/`_nebs` fields (~79–80), `DefaultGalaxy()` (~81–83), `SetNightSky()` (~85–92), `SetNebulae()` (~93–103).

- [ ] **Step 2: Remove the `_mw*` fields + property.** Delete the comment block + `_mwShader,_mwPipe,_mwTex,_mwSet,_mwParamBuf`, `_mwRd`, `_mwDirty`, `NightSkyTexture` (lines ~66–75). Also delete the `MwW`/`MwH` consts (search for `MwW`).

- [ ] **Step 3: Remove bake wiring.** In `Attach()` delete the `_mwRd = new Texture2Drd();` line (~122). In `_Process()` delete the `if (_mwDirty) {...}` block (~144). In `InitCompute()` delete the `_mwShader = Compile(...night_sky_bake...)` + pipe (~164–165), `_mwTex = CreateTex(MwW,MwH)` (~170), `_mwParamBuf = ...` (~171), `if (_mwRd != null) {...}` (~181), and `BakeNightSky();` (~183). In `EnsureSets()` delete the `_mwSet` build block (~302–306). In `Dispose`/cleanup delete every `_mw*` `FreeRid` (~459–463).

- [ ] **Step 4: Remove `NsParams()` + `BakeNightSky()`.** Delete both methods (~234–256, ~258–270).

- [ ] **Step 5: Build.** Run: `dotnet build WG16.csproj`. Expected: build succeeds, 0 errors. (Compiler will flag any missed `_mw*` reference — fix until clean.)

- [ ] **Step 6: Commit.**

```bash
git add scripts/lab/AtmosphereCompute.cs
git commit -m "refactor(sky): remove night-sky bake plumbing from AtmosphereCompute"
```

### Task 0.3: Remove the baked-texture gate from CloudVolume + TerrainLabUI

**Files:**
- Modify: `scripts/lab/CloudVolume.cs`, `scripts/lab/TerrainLabUI.cs`, `scripts/lab/TerrainLabUI.Lighting.cs`, `scripts/lab/TerrainLabUI.Clouds.cs`

**Interfaces:**
- Produces: `CloudVolume.SetNightSky(...)` REPLACED in Phase 1 (its old galaxy form is removed here); `SetNebulae`, `SetNightSkyTex`, `SetNightSkyBaked` deleted.

- [ ] **Step 1: CloudVolume — delete old pushers.** In `CloudVolume.cs` delete `SetNightSky(GalaxyParams g)` (the gx_* push, ~551–564), `SetNebulae(NebulaParams[])` (~566–577), `SetNightSkyTex` (~603), `SetNightSkyBaked` (~604). Keep nothing referencing `gx_*`/`neb_*`/`night_sky_tex`/`night_sky_baked`.

- [ ] **Step 2: TerrainLabUI — unbind the tex.** In `TerrainLabUI.cs` delete line ~120 `_cloud.SetNightSkyTex(_atmosphere.NightSkyTexture);`.

- [ ] **Step 3: Lighting — drop the bake/push calls.** In `TerrainLabUI.Lighting.cs` ComposeLighting (~170–175) remove `BuildGalaxyParams()`/`BuildNebulae()` calls and the four `_cloud.SetNightSky/_cloud.SetNebulae/_atmosphere.SetNightSky/_atmosphere.SetNebulae` lines. Delete the now-unused `BuildGalaxyParams()` (~35–45), `BuildNebulae()` (~47–57), `_nebDirs`/`_nebScales` (~27–31). (StarsState galaxy/neb fields are repurposed in Phase 1 — leave them for now or they'll error nothing; safe to leave.)

- [ ] **Step 4: Clouds — remove the bake gate.** In `TerrainLabUI.Clouds.cs` delete the `night_sky_baked` knob branch (~56) and search the file for `_nsBakedOn`/`_nsActivated`; delete those fields and any block that flips `SetNightSkyBaked(true)` once the RID is live.

- [ ] **Step 5: Build.** Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 6: Verify night still renders.** Launch windowed:
  `"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project` → press `2`. Expected: night sky with stars + moon + meteors, no errors in the console. (Galaxy/nebula were already off, so the look is unchanged.)

- [ ] **Step 7: Commit.**

```bash
git add scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.Clouds.cs
git commit -m "refactor(sky): remove baked night-sky gate; night = stars+moon+meteors"
```

---

## Phase 1 — Billboard infra + galaxy generator (FIRST EYE-GATE)

Deliverable: a single hand-placed spiral galaxy billboard rendered live, default small, tunable via global brightness. This is the first user eye-gate (per D2, galaxies before nebulae).

### Task 1.1: Slot-array uniforms + billboard loop in the shader

**Files:**
- Modify: `shaders/cloud_sky.gdshader`

**Interfaces:**
- Produces (GLSL): `uniform int bb_count;`, `uniform vec4 bb_dir_size[8];`, `uniform vec4 bb_color_type[8];`, `uniform vec4 bb_params[8];`, `uniform vec4 bb_color2[8];`; `vec3 billboards(vec3 sr)`. Type encoding in `bb_color_type[i].w`: `0.0` = spiral galaxy, `1.0` = elliptical galaxy, `2.0` = nebula.
- Consumes: `vnoise3` (kept), `night_sky_brightness` (kept), `night_factor`, `TIME`.

- [ ] **Step 1: Add the uniforms** (near where `night_sky_brightness` lives):

```glsl
// CELESTIAL BILLBOARDS (Galaxy/Nebula v2): up to 8 discrete objects, each rendered in a local 2D
// tangent-plane disc (structure, not 3D fbm). Pushed from C# on change only. type in color_type.w:
// 0 = spiral galaxy, 1 = elliptical galaxy, 2 = nebula.
const int MAX_BILLBOARDS = 8;
uniform int  bb_count = 0;
uniform vec4 bb_dir_size[MAX_BILLBOARDS];    // xyz = sky dir (unit), w = angular radius (radians)
uniform vec4 bb_color_type[MAX_BILLBOARDS];  // rgb = primary/core color, w = type
uniform vec4 bb_color2[MAX_BILLBOARDS];      // rgb = secondary/arm color, w = brightness
uniform vec4 bb_params[MAX_BILLBOARDS];      // x = rotation(rad), y = tilt(0..1 squash), z = arms, w = seed
```

- [ ] **Step 2: Add the disc-projection helper** (before `billboards`):

```glsl
// Project a sky dir `d` into a billboard's local disc centered on `dir`. Returns uv where |uv|~0 at
// center and ~1 at the rim (reach). `rot` spins the disc; `tilt` squashes one axis (disc inclination).
vec2 bb_disc_uv(vec3 d, vec3 dir, float reach, float rot, float tilt){
    vec3 up = abs(dir.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 ax = normalize(cross(up, dir));
    vec3 ay = cross(dir, ax);
    vec3 rel = d - dir * dot(d, dir);            // tangent-plane component
    vec2 uv = vec2(dot(rel, ax), dot(rel, ay)) / max(sin(reach), 1e-4);
    float cs = cos(rot), sn = sin(rot);
    uv = vec2(cs * uv.x - sn * uv.y, sn * uv.x + cs * uv.y);
    uv.y /= max(1.0 - 0.85 * tilt, 0.15);        // squash → inclined disc
    return uv;
}
```

- [ ] **Step 3: Add `galaxy_billboard` + the `billboards` loop** (nebula stub returns 0 until Phase 3):

```glsl
// Spiral/elliptical galaxy in disc-uv space: bright Gaussian core + log-spiral arms + radial falloff.
vec3 galaxy_billboard(vec2 uv, vec3 coreCol, vec3 armCol, float arms, float isSpiral, float seed){
    float r = length(uv);
    if (r > 1.0) return vec3(0.0);
    float rim = smoothstep(1.0, 0.75, r);                       // soft edge
    float core = exp(-r * r * 9.0);                             // bright bulge
    float theta = atan(uv.y, uv.x);
    // log-spiral arm intensity (no arms in the core; fade at the rim)
    float spiral = cos(arms * theta - 6.0 * log(r + 0.12) + seed * 6.2831);
    float armBand = smoothstep(0.2, 0.95, spiral) * smoothstep(0.05, 0.4, r) * (1.0 - core);
    armBand *= isSpiral;                                        // elliptical → no arms
    float disc = exp(-r * r * 2.2) * (0.35 + 0.65 * armBand);   // smooth disc * arm modulation
    float lum = (core * 1.6 + disc) * rim;
    vec3 col = mix(armCol, coreCol, clamp(core * 1.2, 0.0, 1.0));
    return col * lum;
}

vec3 billboards(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int i = 0; i < bb_count; i++){
        vec3 dir = bb_dir_size[i].xyz; float reach = bb_dir_size[i].w;
        if (dot(sr, dir) < cos(reach)) continue;               // EARLY-OUT: one dot product
        float type = bb_color_type[i].w;
        float rot = bb_params[i].x, tilt = bb_params[i].y, arms = bb_params[i].z, seed = bb_params[i].w;
        vec2 uv = bb_disc_uv(sr, dir, reach, rot, tilt);
        vec3 c;
        if (type < 1.5)   c = galaxy_billboard(uv, bb_color_type[i].rgb, bb_color2[i].rgb, arms, type < 0.5 ? 1.0 : 0.0, seed);
        else              c = vec3(0.0);                        // nebula: Phase 3
        acc += c * bb_color2[i].w;                              // per-slot brightness
    }
    return acc;
}
```

- [ ] **Step 4: Wire `billboards` into `stars_layer`.** Change the return to apply the global multiplier + fade once at the call site (keep the brightness-0 early-out so the lane is free):

```glsl
    vec3 bb = vec3(0.0);
    if (night_sky_brightness > 0.0001) { bb = billboards(sr) * night_sky_brightness; }
    float fade = night_factor * smoothstep(-0.02, 0.12, rd.y);
    return (star + meteors(rd) + bb) * fade;
```

- [ ] **Step 5: Compile-check.** Run: `"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project --headless --import`. Expected: no `cloud_sky.gdshader` compile error.

- [ ] **Step 6: Commit.**

```bash
git add shaders/cloud_sky.gdshader
git commit -m "feat(sky): billboard slot uniforms + galaxy generator in cloud_sky"
```

### Task 1.2: C# billboard model + uniform push

**Files:**
- Modify: `scripts/lab/CloudVolume.cs`, `scripts/lab/TerrainLabUI.Lighting.cs`
- Create: `scripts/lab/NightBillboards.cs`

**Interfaces:**
- Produces (C#): `struct Billboard { Vector3 Dir; float Size; int Type; Vector3 Color; Vector3 Color2; float Brightness, Rotation, Tilt, Arms, Seed; }` (in `NightBillboards.cs`, namespace `WG16.Lab`); `CloudVolume.SetBillboards(System.Collections.Generic.IReadOnlyList<Billboard> slots, float globalBrightness)`.
- Consumes: shader uniforms `bb_count`/`bb_dir_size`/`bb_color_type`/`bb_color2`/`bb_params`/`night_sky_brightness`.

- [ ] **Step 1: Create the `Billboard` struct.** `scripts/lab/NightBillboards.cs`:

```csharp
using Godot;

namespace WG16.Lab;

/// One celestial billboard (galaxy/nebula v2). Direction-anchored 2D disc rendered in cloud_sky.gdshader.
/// Type: 0 = spiral galaxy, 1 = elliptical galaxy, 2 = nebula.
public struct Billboard
{
    public Vector3 Dir;       // unit sky direction
    public float Size;        // angular radius (radians)
    public int Type;          // 0 spiral / 1 elliptical / 2 nebula
    public Vector3 Color;     // primary/core color
    public Vector3 Color2;    // secondary/arm color
    public float Brightness;  // per-slot
    public float Rotation;    // disc spin (rad)
    public float Tilt;        // 0..1 inclination squash
    public float Arms;        // spiral arm count (galaxy)
    public float Seed;        // per-object hash seed
}
```

- [ ] **Step 2: Add `CloudVolume.SetBillboards`.** In `CloudVolume.cs` (where the old `SetNightSky` was), add:

```csharp
// Celestial billboards (galaxy/nebula v2): push the packed slot arrays + the live global brightness.
// CloudVolume stays the SOLE _skyMat writer. Called on config change only (not per frame).
public void SetBillboards(System.Collections.Generic.IReadOnlyList<Billboard> slots, float globalBrightness)
{
    const int MAX = 8;
    int n = Mathf.Min(slots.Count, MAX);
    var dirSize = new Godot.Collections.Array();
    var colType = new Godot.Collections.Array();
    var col2    = new Godot.Collections.Array();
    var prm     = new Godot.Collections.Array();
    for (int i = 0; i < MAX; i++)
    {
        if (i < n)
        {
            var b = slots[i]; var d = b.Dir.Normalized();
            dirSize.Add(new Vector4(d.X, d.Y, d.Z, Mathf.Max(b.Size, 1e-3f)));
            colType.Add(new Vector4(b.Color.X, b.Color.Y, b.Color.Z, b.Type));
            col2.Add(new Vector4(b.Color2.X, b.Color2.Y, b.Color2.Z, Mathf.Max(b.Brightness, 0f)));
            prm.Add(new Vector4(b.Rotation, Mathf.Clamp(b.Tilt, 0f, 1f), Mathf.Max(b.Arms, 0f), b.Seed));
        }
        else { dirSize.Add(new Vector4(0,1,0,0.001f)); colType.Add(Vector4.Zero); col2.Add(Vector4.Zero); prm.Add(Vector4.Zero); }
    }
    _skyMat?.SetShaderParameter("bb_count", n);
    _skyMat?.SetShaderParameter("bb_dir_size", dirSize);
    _skyMat?.SetShaderParameter("bb_color_type", colType);
    _skyMat?.SetShaderParameter("bb_color2", col2);
    _skyMat?.SetShaderParameter("bb_params", prm);
    _skyMat?.SetShaderParameter("night_sky_brightness", Mathf.Max(globalBrightness, 0f));
}
```

- [ ] **Step 3: Build a default arrangement + push it.** In `TerrainLabUI.Lighting.cs` add a `BuildBillboards()` returning a `List<Billboard>` and call `_cloud.SetBillboards(BuildBillboards(), _stars.MwBrightness)` from ComposeLighting (where the old SetNightSky calls were). Initial body — one hand-placed spiral galaxy, small:

```csharp
private System.Collections.Generic.List<Billboard> BuildBillboards()
{
    var list = new System.Collections.Generic.List<Billboard>();
    // One hand-placed hero spiral (small by default; size in radians ≈ 6°).
    list.Add(new Billboard {
        Dir = new Vector3(0.3f, 0.45f, 0.84f).Normalized(), Size = Mathf.DegToRad(6f), Type = 0,
        Color = new Vector3(0.95f, 0.82f, 0.6f), Color2 = new Vector3(0.5f, 0.6f, 0.95f),
        Brightness = 1f, Rotation = 0.6f, Tilt = 0.55f, Arms = 2f, Seed = 0.21f });
    return list;
}
```

  Set `_stars.MwBrightness` default to a visible value for the gate (e.g. `0.6f` in `LightingState.cs`); it remains the live master knob.

- [ ] **Step 4: Build.** Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 5: Eye-gate (USER).** Launch windowed, press `2`. Capture cropped to the sky region. Expected: a small spiral galaxy visible in the upper sky — reads as a galaxy, NOT a cloud. Iterate `galaxy_billboard` constants live with the user until it passes. **Do not proceed until the user approves the galaxy look.**

- [ ] **Step 6: Commit.**

```bash
git add scripts/lab/NightBillboards.cs scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/LightingState.cs
git commit -m "feat(sky): C# billboard model + uniform push; default spiral galaxy"
```

---

## Phase 2 — Hybrid placement (seeded scatter + hero slots)

Deliverable: small accents auto-scattered from a seed + a couple of explicit hero slots, default small. Eye-gate "small by default."

### Task 2.1: Seeded auto-scatter + hero merge in C#

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Lighting.cs`, `scripts/lab/LightingState.cs`

**Interfaces:**
- Consumes: `Billboard` (Task 1.1), `CloudVolume.SetBillboards`.
- Produces: `BuildBillboards()` returns scattered accents (first N) + active heroes (appended), capped at 8.

- [ ] **Step 1: Add scatter/hero state to `StarsState`** (`LightingState.cs`) — replace the now-dead galaxy/neb fields with:

```csharp
    // Billboards v2 (galaxy/nebula). MwBrightness stays the live global master.
    public int ScatterSeed = 7, ScatterCount = 4;     // small background accents
    public float ScatterSizeDeg = 3.5f;               // accent angular radius (small)
    // Up to 2 explicit hero slots (size 0 = disabled).
    public float Hero1SizeDeg = 0f, Hero2SizeDeg = 0f;
    public Vector3 Hero1Dir = new Vector3(0.3f, 0.45f, 0.84f).Normalized();
    public Vector3 Hero2Dir = new Vector3(-0.5f, 0.4f, 0.6f).Normalized();
    public int Hero1Type = 0, Hero2Type = 2;          // hero1 spiral, hero2 nebula
```

  (Keep `MwBrightness`, `CoreColor`/`ArmColor`, `Neb1Color`/`Neb2Color` as the palette; remove `CoreAz/CoreElev/CoreSize/MwTilt/MwWidth/Curve/Dust/NebCount/NebDensity` if unused after this task.)

- [ ] **Step 2: Implement scatter in `BuildBillboards()`.** Deterministic hash (no `Math.random`), upper-hemisphere bias:

```csharp
private static float Hash(int i, int salt){ var x = Mathf.Sin((i * 127.1f + salt * 311.7f)) * 43758.5453f; return x - Mathf.Floor(x); }

private System.Collections.Generic.List<Billboard> BuildBillboards()
{
    var list = new System.Collections.Generic.List<Billboard>();
    int n = Mathf.Clamp(_stars.ScatterCount, 0, 6);
    for (int i = 0; i < n; i++)
    {
        int s = _stars.ScatterSeed * 13 + i;
        float az = Hash(s, 1) * Mathf.Tau;
        float el = Mathf.Lerp(0.2f, 1.2f, Hash(s, 2));              // upper sky
        float ce = Mathf.Cos(el);
        var dir = new Vector3(ce * Mathf.Cos(az), Mathf.Sin(el), ce * Mathf.Sin(az)).Normalized();
        int type = Hash(s, 3) < 0.6f ? 0 : 2;                       // mostly galaxies, some nebulae
        var pal = Hash(s, 4) < 0.5f ? _stars.Neb1Color : _stars.Neb2Color;
        list.Add(new Billboard {
            Dir = dir, Size = Mathf.DegToRad(_stars.ScatterSizeDeg * (0.7f + 0.6f * Hash(s, 5))), Type = type,
            Color = new Vector3(_stars.CoreColor.R, _stars.CoreColor.G, _stars.CoreColor.B),
            Color2 = new Vector3(pal.R, pal.G, pal.B),
            Brightness = 0.8f + 0.5f * Hash(s, 6), Rotation = Hash(s, 7) * Mathf.Tau,
            Tilt = 0.3f + 0.5f * Hash(s, 8), Arms = Hash(s, 9) < 0.5f ? 2f : 3f, Seed = Hash(s, 10) });
    }
    AddHero(list, _stars.Hero1Dir, _stars.Hero1SizeDeg, _stars.Hero1Type, 0.31f);
    AddHero(list, _stars.Hero2Dir, _stars.Hero2SizeDeg, _stars.Hero2Type, 0.62f);
    if (list.Count > 8) list.RemoveRange(8, list.Count - 8);
    return list;
}

private void AddHero(System.Collections.Generic.List<Billboard> list, Vector3 dir, float sizeDeg, int type, float seed)
{
    if (sizeDeg <= 0.001f) return;
    list.Add(new Billboard {
        Dir = dir.Normalized(), Size = Mathf.DegToRad(sizeDeg), Type = type,
        Color = new Vector3(_stars.CoreColor.R, _stars.CoreColor.G, _stars.CoreColor.B),
        Color2 = new Vector3(_stars.ArmColor.R, _stars.ArmColor.G, _stars.ArmColor.B),
        Brightness = 1f, Rotation = 0.6f, Tilt = 0.5f, Arms = 2f, Seed = seed });
}
```

- [ ] **Step 2b: Defaults small.** Set `Hero1SizeDeg`/`Hero2SizeDeg` defaults to `0f` (heroes off) and `MwBrightness` to a sane default (~`0.5f`). Scatter on by default (`ScatterCount = 4`, small).

- [ ] **Step 3: Build.** Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 4: Eye-gate (USER).** Windowed, key `2`. Expected: a few small galaxy accents scattered across the upper sky, easy to miss, not dominating. Re-roll by changing `ScatterSeed`; promote a hero by setting `Hero1SizeDeg`. Iterate with the user. **Approval gate.**

- [ ] **Step 5: Commit.**

```bash
git add scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/LightingState.cs
git commit -m "feat(sky): hybrid billboard placement (seeded scatter + hero slots)"
```

---

## Phase 3 — Nebula generator (SEPARATE EYE-GATE per R1)

Deliverable: nebula type renders as glowing structured gas (lobes + hard dust lanes + embedded stars), not cloud-feel. Gated separately.

### Task 3.1: `nebula_billboard` in the shader

**Files:**
- Modify: `shaders/cloud_sky.gdshader`

**Interfaces:**
- Consumes: `vnoise3`, the `billboards` dispatch (`type >= 1.5`).
- Produces: `vec3 nebula_billboard(vec2 uv, vec3 col1, vec3 col2, float seed)`.

- [ ] **Step 1: Add `nebula_billboard`** (before `billboards`):

```glsl
// Emission nebula in disc-uv space: 2–3 offset Gaussian lobes + hard dust lanes + embedded bright stars.
// Structure-dominated so it can't collapse into cloud-feel; vnoise3 is low-weight grain only.
vec3 nebula_billboard(vec2 uv, vec3 col1, vec3 col2, float seed){
    float r = length(uv);
    if (r > 1.0) return vec3(0.0);
    float rim = smoothstep(1.0, 0.7, r);
    // overlapping lobes (offset gaussians) → irregular body
    vec2 o1 = vec2(0.25, -0.1), o2 = vec2(-0.2, 0.22), o3 = vec2(0.05, 0.3);
    float body = exp(-dot(uv-o1,uv-o1)*3.0) + 0.8*exp(-dot(uv-o2,uv-o2)*4.5) + 0.6*exp(-dot(uv-o3,uv-o3)*6.0);
    // hard dust lanes: sharp dark cuts (signed lines)
    float lane1 = smoothstep(0.02, 0.0, abs(dot(uv, vec2(0.7, 0.71)) - 0.05));
    float lane2 = smoothstep(0.03, 0.0, abs(dot(uv, vec2(-0.5, 0.86)) + 0.18));
    float dust = 1.0 - 0.85 * max(lane1, lane2);
    // low-weight grain (NOT the dominant term)
    float grain = 0.6 + 0.4 * vnoise3(vec3(uv * 5.0 + seed * 7.0, seed));
    float lum = body * dust * grain * rim;
    // color varies across the body
    vec3 col = mix(col1, col2, clamp(uv.x * 0.5 + 0.5, 0.0, 1.0));
    // embedded bright stars (hashed within the disc)
    vec2 cell = floor(uv * 9.0); vec2 f = fract(uv * 9.0) - 0.5;
    float h = hash13(vec3(cell + seed * 11.0, seed));
    float star = step(0.93, h) * smoothstep(0.12, 0.0, length(f)) * rim;
    return col * lum * 1.4 + vec3(0.9, 0.95, 1.0) * star;
}
```

- [ ] **Step 2: Dispatch it.** In `billboards`, replace the `else c = vec3(0.0);` line with:

```glsl
        else c = nebula_billboard(uv, bb_color_type[i].rgb, bb_color2[i].rgb, seed);
```

- [ ] **Step 3: Compile-check.** Run: `"$GODOT" ... --headless --import`. Expected: no shader error.

- [ ] **Step 4: Eye-gate (USER).** Windowed, key `2`. Enable a nebula hero (`Hero2SizeDeg` up, `Hero2Type = 2`) and/or scatter nebulae. Expected: a structured glowing gas patch with visible dust lanes + embedded stars — reads as space, NOT a cloud. Iterate offsets/lanes/grain weight with the user. **Approval gate (R1).**

- [ ] **Step 5: Commit.**

```bash
git add shaders/cloud_sky.gdshader
git commit -m "feat(sky): nebula billboard generator (lobes + dust lanes + stars)"
```

---

## Phase 4 — Lab knobs, presets, CLI, docs

Deliverable: Night-tab controls for the billboard system, repurposed preset picker, updated review label + docs.

### Task 4.1: Night-tab controls

**Files:**
- Modify: `scripts/lab/TerrainLabUI.Registry.cs` (+ wherever the Night tab builds sliders), `data/lab_controls.json` (if night controls are data-driven there)

**Interfaces:**
- Consumes: `_stars.MwBrightness`, `ScatterSeed`, `ScatterCount`, `ScatterSizeDeg`, `Hero1SizeDeg`, `Hero2SizeDeg` (Task 2.1).

- [ ] **Step 1: Register the knobs.** Following the existing Night-tab slider + registry pattern (the same `_byId` + `SetWidgetValue` path used by `BuildGalaxyParams`'s old sliders; see `TerrainLabUI.Registry.cs` and the Night-tab build near `TerrainLabUI.Registry.cs:216`), add controls with ids: `night_sky_brightness` (0..3), `bb_scatter_seed` (0..64, integer step), `bb_scatter_count` (0..6), `bb_scatter_size` (1..15 deg), `bb_hero1_size` (0..30 deg), `bb_hero2_size` (0..30 deg). Each id needs: a field/setter on `_stars`, a scene slider, and a C# registry case that sets the field then calls `ComposeLighting()` (which calls `BuildBillboards` + `SetBillboards`). Per memory `lab-registry-param-gotcha`, a missing piece silently no-ops — verify each slider moves the sky.

- [ ] **Step 2: Build + verify each knob.** `dotnet build WG16.csproj`; windowed, key `2`, drag each slider, confirm the sky responds (brightness fades the lane, count adds/removes accents, hero size grows the hero). **Approval gate.**

- [ ] **Step 3: Commit.**

```bash
git add scripts/lab/TerrainLabUI.Registry.cs data/lab_controls.json
git commit -m "feat(sky): Night-tab billboard controls"
```

### Task 4.2: Repurpose presets + CLI + docs

**Files:**
- Modify: `data/night_sky_presets.json`, `scripts/lab/TerrainLabUI.NightSkyPresets.cs`, `scripts/lab/TerrainLabUI.Review.cs`, `docs/ROADMAP.md`, `docs/DECISIONS.md`, `docs/NEEDS_REVIEW.md`

- [ ] **Step 1: Rewrite presets.** Replace the galaxy/nebula entries in `data/night_sky_presets.json` with billboard arrangements keyed by the new control ids (`night_sky_brightness`, `bb_scatter_*`, `bb_hero1_size`, etc.) + palette colors: e.g. `Calm Night` (count 3, heroes off), `Galaxy-rich` (count 5, hero1 spiral 18°), `Nebula-rich` (count 4 nebula-biased, hero2 nebula 16°), `Hero` (one big spiral). In `TerrainLabUI.NightSkyPresets.cs` update the color-case ids (`gx_core_color`→ keep mapping to `_stars.CoreColor`, drop `neb*` if palette consolidates) so unknown-id warnings don't fire.

- [ ] **Step 2: Update the review label.** `TerrainLabUI.Review.cs:61` — change `case 2` label from "galaxy + nebulae killed" to "stars + moon + meteors + galaxy/nebula billboards".

- [ ] **Step 3: Build + verify presets.** `dotnet build WG16.csproj`; windowed, key `2`, cycle each preset via the Night-tab dropdown (and `--nspreset=N`). Confirm each loads a distinct look with no warnings. **Approval gate.**

- [ ] **Step 4: Update docs.** ROADMAP #6 Celestial: mark galaxy/nebula v2 (billboards) built + gated. DECISIONS: log "C1 procedural-noise rejected → billboards (live, structure-based, bake dropped)". NEEDS_REVIEW: close the old galaxy/nebula items, add the billboard gate entries.

- [ ] **Step 5: Commit.**

```bash
git add data/night_sky_presets.json scripts/lab/TerrainLabUI.NightSkyPresets.cs scripts/lab/TerrainLabUI.Review.cs docs/ROADMAP.md docs/DECISIONS.md docs/NEEDS_REVIEW.md
git commit -m "feat(sky): billboard presets + CLI + docs; close galaxy/nebula v2"
```

---

## Self-Review notes

- **Spec coverage:** D1 live-not-baked → Phase 0 (bake deleted) + Phase 1 (live render). D2 both generators phased → Phase 1 (galaxy) + Phase 3 (nebula). D3 hybrid placement → Phase 2. D4 C#-side, on-change → `BuildBillboards`/`SetBillboards` called from ComposeLighting, not per frame. Delete/keep list → Phase 0 tasks. Lab knobs + key-2 gate → Phase 4 + every eye-gate step. Perf early-outs → shader `dot`/brightness-0 guards. Presets → Task 4.2. All spec sections mapped.
- **Type consistency:** `Billboard` fields used identically across CloudVolume.SetBillboards / BuildBillboards / AddHero. Shader uniform names (`bb_count`/`bb_dir_size`/`bb_color_type`/`bb_color2`/`bb_params`) match between Task 1.1 (declare) and Task 1.2 (`SetShaderParameter`). Type encoding (0/1/2) consistent shader↔C#.
- **Verification:** every task ends in `dotnet build` or `--import` compile-check; visual tasks add a key-2 eye-gate (the project's test substitute; no shader unit harness exists).
