# Celestial C2 — Planets + Named Stars Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add ~5 bright colored planet points (+ glow, no twinkle) and ~7 "landmark" bright stars to the night sky, riding the celestial sphere with the starfield.

**Architecture:** Two shader helpers (`planets`, `bright_stars`) driven by curated `const` tables, called inside `stars_layer` (reusing its rotation + night fade). Global on/off + brightness exposed as uniforms, wired through the standard StarsState → lab_controls → Apply → CloudVolume → `_skyMat` path.

**Tech Stack:** Godot 4.6.2 mono (GDShader sky shader + C#).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-06-21-celestial-c2-planets-design.md`.
- Shared branch `experiment/presentation` with the ground chat — stay in sky/light files; `git add` explicit paths, NEVER `-A`. Commit-by-default; push only when asked.
- **Launch hygiene (critical — stale windows caused bad reviews before):** `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` → confirm ZERO processes → launch exactly ONE. Always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`, scene `scenes/review.tscn`, `--review=2` for night.
- Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`. Build: `dotnet build WG16.csproj`.
- No shader unit harness: verify = `dotnet build` / `--import` compile + key-`2` night eye-gate.
- CloudVolume is the SOLE `_skyMat` writer.

---

### Task 1: Shader — planets + bright stars in `stars_layer`

**Files:**
- Modify: `shaders/cloud_sky.gdshader`

**Interfaces:**
- Produces (GLSL): uniforms `planets_on`, `planet_brightness`, `bright_stars_on`, `bright_star_brightness`; functions `vec3 planets(vec3 sr)`, `vec3 bright_stars(vec3 sr)`.
- Consumes: `TIME`, the rotated `sr` already computed in `stars_layer`, `hash13`/`vnoise3` not needed.

- [ ] **Step 1: Add the uniforms.** Place near the other star uniforms (search for `uniform float star_brightness`):

```glsl
// CELESTIAL C2 — planets (bright steady colored points + glow) + named/landmark bright stars. Curated
// const tables below; only global on/off + brightness are tunable. Rendered in stars_layer (ride the sky).
uniform bool  planets_on = true;
uniform float planet_brightness : hint_range(0.0, 3.0) = 1.0;
uniform bool  bright_stars_on = true;
uniform float bright_star_brightness : hint_range(0.0, 3.0) = 1.0;
```

- [ ] **Step 2: Add the const tables + helpers** immediately before `vec3 stars_layer(vec3 rd){`:

```glsl
// Curated planets: xyz = direction on the celestial sphere (normalized in-shader), w = core angular size (rad).
const vec4 PLANET_DIR_SIZE[5] = vec4[5](
    vec4( 0.5,  0.5,  0.7, 0.0060),   // Venus  — brightest, white
    vec4(-0.6,  0.4,  0.6, 0.0045),   // Mars   — red
    vec4( 0.2,  0.7, -0.5, 0.0052),   // Jupiter— gold, bright
    vec4(-0.3,  0.6, -0.6, 0.0042),   // Saturn — pale gold
    vec4( 0.7,  0.45,-0.2, 0.0038)    // blue ice giant
);
const vec4 PLANET_COL[5] = vec4[5](   // rgb = color, w = relative brightness
    vec4(1.00, 0.97, 0.90, 2.2),
    vec4(1.00, 0.50, 0.35, 1.3),
    vec4(1.00, 0.92, 0.72, 1.8),
    vec4(0.95, 0.85, 0.60, 1.1),
    vec4(0.70, 0.82, 1.00, 0.9)
);
// Curated landmark stars: xyz = dir, w = relative brightness; BSTAR_COL = color.
const vec4 BSTAR_DIR[7] = vec4[7](
    vec4( 0.10, 0.80, 0.50, 1.8), vec4(-0.50, 0.60, 0.40, 1.5),
    vec4( 0.60, 0.55, 0.30, 1.6), vec4(-0.20, 0.70,-0.60, 1.4),
    vec4( 0.40, 0.50,-0.70, 1.7), vec4(-0.70, 0.45,-0.30, 1.3),
    vec4( 0.00, 0.60, 0.80, 1.9)
);
const vec3 BSTAR_COL[7] = vec3[7](
    vec3(0.80,0.88,1.00), vec3(1.00,1.00,0.97), vec3(1.00,0.78,0.55),
    vec3(0.80,0.88,1.00), vec3(1.00,1.00,0.97), vec3(1.00,0.78,0.55),
    vec3(0.80,0.88,1.00)
);

// Planets: bright steady colored points (NO twinkle) with a tiny core disc + faint soft glow.
vec3 planets(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int i = 0; i < 5; i++){
        vec3 dir = normalize(PLANET_DIR_SIZE[i].xyz);
        float cd = dot(sr, dir);
        if (cd < 0.2) continue;                                  // early-out (well off the object)
        float ang = acos(clamp(cd, 0.0, 1.0));
        float core = smoothstep(PLANET_DIR_SIZE[i].w, 0.0, ang); // tiny bright disc
        float glow = exp(-ang * 55.0);                           // faint soft halo
        acc += PLANET_COL[i].rgb * (core * PLANET_COL[i].w + glow * 0.25);
    }
    return acc;
}

// Landmark stars: brighter than the field, slightly colored, faint glow + a slow gentle twinkle.
vec3 bright_stars(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int i = 0; i < 7; i++){
        vec3 dir = normalize(BSTAR_DIR[i].xyz);
        float cd = dot(sr, dir);
        if (cd < 0.2) continue;
        float ang = acos(clamp(cd, 0.0, 1.0));
        float point = smoothstep(0.0035, 0.0, ang);
        float glow  = exp(-ang * 110.0) * 0.4;
        float tw = mix(1.0, 0.6 + 0.4 * sin(TIME * 0.6 + float(i) * 9.7), 0.5);   // slow gentle twinkle
        acc += BSTAR_COL[i] * (point * BSTAR_DIR[i].w * tw + glow);
    }
    return acc;
}
```

- [ ] **Step 3: Integrate into `stars_layer`.** Replace the return:

```glsl
    vec3 pl = planets_on ? planets(sr) * planet_brightness : vec3(0.0);
    vec3 bs = bright_stars_on ? bright_stars(sr) * bright_star_brightness : vec3(0.0);

    float fade = night_factor * smoothstep(-0.02, 0.12, rd.y);   // night + above horizon
    return (star + pl + bs + meteors(rd)) * fade;
```

(Remove the old `float fade = ...; return (star + meteors(rd)) * fade;` lines.)

- [ ] **Step 4: Compile-check.** Run: `"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project --headless --import` (set GODOT to the exe). Expected: no `cloud_sky.gdshader` compile error.

- [ ] **Step 5: Commit.**

```bash
git add shaders/cloud_sky.gdshader
git commit -m "feat(sky): C2 planets + named stars in cloud_sky stars_layer"
```

---

### Task 2: C# wiring + Night-tab controls

**Files:**
- Modify: `scripts/lab/LightingState.cs`, `scripts/lab/CloudVolume.cs`, `scripts/lab/TerrainLabUI.Lighting.cs`, `scripts/lab/TerrainLabUI.Apply.cs`, `data/lab_controls.json`

**Interfaces:**
- Consumes (GLSL uniforms from Task 1): `planets_on`, `planet_brightness`, `bright_stars_on`, `bright_star_brightness`.
- Produces (C#): `CloudVolume.SetPlanets(bool, float)`, `CloudVolume.SetBrightStars(bool, float)`; `StarsState.PlanetsOn/PlanetBrightness/BrightStarsOn/BrightStarBrightness`.

- [ ] **Step 1: StarsState fields.** In `scripts/lab/LightingState.cs`, in `class StarsState` (after the meteor fields), add:

```csharp
    // Celestial C2 — planets + landmark bright stars (curated in shader; global tuning only).
    public bool PlanetsOn = true;        public float PlanetBrightness = 1.0f;
    public bool BrightStarsOn = true;    public float BrightStarBrightness = 1.0f;
```

- [ ] **Step 2: CloudVolume setters.** In `scripts/lab/CloudVolume.cs`, near `SetStars`/`SetMeteors`, add:

```csharp
    public void SetPlanets(bool on, float brightness)
    {
        _skyMat?.SetShaderParameter("planets_on", on);
        _skyMat?.SetShaderParameter("planet_brightness", Mathf.Max(brightness, 0f));
    }
    public void SetBrightStars(bool on, float brightness)
    {
        _skyMat?.SetShaderParameter("bright_stars_on", on);
        _skyMat?.SetShaderParameter("bright_star_brightness", Mathf.Max(brightness, 0f));
    }
```

- [ ] **Step 3: Push from ComposeLighting.** In `scripts/lab/TerrainLabUI.Lighting.cs`, in the night block right after the `_cloud.SetMeteors(...)` line, add:

```csharp
            _cloud.SetPlanets(_stars.PlanetsOn, _stars.PlanetBrightness);
            _cloud.SetBrightStars(_stars.BrightStarsOn, _stars.BrightStarBrightness);
```

- [ ] **Step 4: Apply cases.** In `scripts/lab/TerrainLabUI.Apply.cs`, add to the float-control switch (near the `meteor_*` cases):

```csharp
            case "planet_brightness":      _stars.PlanetBrightness = v; ComposeLighting(); break;
            case "bright_star_brightness": _stars.BrightStarBrightness = v; ComposeLighting(); break;
```

  and to the bool/scene switch (where `meteors_on` is handled — find `case "meteors_on"`):

```csharp
            case "planets_on":      _stars.PlanetsOn = on; ComposeLighting(); break;
            case "bright_stars_on": _stars.BrightStarsOn = on; ComposeLighting(); break;
```

  (If `meteors_on` is handled via a `bool on` switch with a different signature, mirror that exact shape.)

- [ ] **Step 5: Lab controls.** In `data/lab_controls.json`, after the `meteor_color_var` Night-tab entry, add:

```json
    { "id": "planets_on", "label": "planets", "tab": "Night", "type": "scene", "scene": "planets_on", "default": true, "rand": false },
    { "id": "planet_brightness", "label": "planet brightness", "tab": "Night", "type": "scenef", "scene": "planet_brightness", "min": 0.0, "max": 3.0, "default": 1.0, "rand": false },
    { "id": "bright_stars_on", "label": "bright stars", "tab": "Night", "type": "scene", "scene": "bright_stars_on", "default": true, "rand": false },
    { "id": "bright_star_brightness", "label": "bright star brightness", "tab": "Night", "type": "scenef", "scene": "bright_star_brightness", "min": 0.0, "max": 3.0, "default": 1.0, "rand": false },
```

  (Verify the `meteors_on` entry uses `"type": "scene"` for bools; match it. If it uses a different bool type id, use that.)

- [ ] **Step 6: Build.** Run: `dotnet build WG16.csproj`. Expected: `Build succeeded`, 0 errors.

- [ ] **Step 7: Eye-gate (USER).** Clean launch: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`; confirm zero; `"$GODOT" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/review.tscn --review=2`. Press `2`. Expected: a few bright steady colored points (planets, no twinkle) clearly brighter than field stars, plus standout landmark stars with a slow twinkle; sky not cluttered; daytime unaffected. Tune the const tables / `planet_brightness` with the user until it passes.

- [ ] **Step 8: Commit.**

```bash
git add scripts/lab/LightingState.cs scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.Apply.cs data/lab_controls.json
git commit -m "feat(sky): Night-tab controls + C# wiring for C2 planets + named stars"
```

---

## Self-Review notes

- **Spec coverage:** Planets (bright colored points + glow, no twinkle) → `planets()` Task 1. Named stars (brighter, colored, slow twinkle) → `bright_stars()` Task 1. Ride the sky + night fade → rendered in `stars_layer` using `sr` Task 1 Step 3. Global on/off + brightness knobs → uniforms (T1) + StarsState/CloudVolume/Apply/lab_controls (T2). Curated const tables (D2) → T1 Step 2. One `_skyMat` writer → CloudVolume setters. Eye-gate key 2 → T2 Step 7. All spec points covered.
- **Type consistency:** uniform names `planets_on`/`planet_brightness`/`bright_stars_on`/`bright_star_brightness` identical across shader (T1) and `SetShaderParameter` (T2). StarsState field names match Apply cases + ComposeLighting push. Control `scene` ids match Apply case strings.
- **Verification:** T1 compile-check (`--import`); T2 `dotnet build` + key-2 eye-gate (project's test substitute; no shader unit harness).
- **Open detail to confirm during impl:** the exact bool-control type/handler shape (`"type": "scene"` + `ApplyCloudBool`/scene-bool path) — match the existing `meteors_on` control + its Apply case verbatim so the toggles wire correctly (memory `lab-registry-param-gotcha`).
