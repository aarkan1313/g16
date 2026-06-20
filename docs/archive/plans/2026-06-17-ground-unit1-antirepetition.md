# Ground Unit 1 — Anti-Repetition (texture bombing) + shared spine

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) tracking.

**Goal:** Kill the "wallpaper / tiled" look of the ground at all ranges by replacing the splat path's plain `textureGrad` sampling with histogram-preserving stochastic texture bombing, and establish the shared `sampleMaterial` / `distanceWeight` seams the rest of the ground-presentation arc plugs into.

**Architecture:** The default rendered path is the splat path (`splat_on=true`), which samples via `tp_alb`/`tp_nrm`/`tp_rgh` — plain triplanar `textureGrad`, **no anti-tiling at all** (the IQ/hex `tiled()` only runs on the non-splat path, off by default). That's why it tiles. We introduce ONE anti-repetition sampler `ar_sample()` (Heitz–Neyret-style histogram-preserving stochastic bombing) and route the triplanar `tp_*` functions through it. We also add `distanceWeight()` (camera-distance factor) so anti-repetition tap count LODs down with distance and later units (detail/parallax) share the seam. All behind live toggles in `data/lab_controls.json`.

**Tech Stack:** Godot 4.6 mono, GLSL spatial shader (`shaders/terrain_lab.gdshader`), C# registry (`TerrainLabUI`).

**Verification model:** GPU/visual, no unit harness. Per task: `dotnet build WG16.csproj` → headless `--import` (shader compiles) → `--auto-shot` A/B (unit on/off) at three ranges → `--profile` (ms). THE gate is the user flying it live at close/mid/far (the tiling is range-spanning). Commit per task.

**Environment:** Project `C:\Wg16\wg-16-project`. Console exe `…Godot_v4.6.2-stable_mono_win64_console.exe` (for `--import`); windowed exe (no `_console`) for runs. ALWAYS `--rendering-driver vulkan`. ONE Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Lab CLIs: `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<abs.png>`.

**Key shader facts (verified against current code):**
- Default path: `fragment()` with `splat_on=true` → `trip_alb_by/nrm_by/rgh_by` → `tp_alb/tp_nrm/tp_rgh` (lines ~376-389). These do plain `textureGrad` per triplanar plane. THIS is the path that tiles and must be routed through anti-repetition.
- `tex_scale_m` is the world→uv scale (uniform). `tri_w(nr)` gives triplanar blend weights.
- `tile_mode`/`iq_sample`/`hex_sample`/`tiled()` exist but only feed the NON-splat `s_alb` path — leave them; they're legacy A/B.

---

## Task 1: Add `distanceWeight()` + `ar_strength`/`ar_on` uniforms (the spine)

**Files:** Modify `shaders/terrain_lab.gdshader`.

- [ ] **Step 1: Add uniforms** after the `tex_scale_m` uniform (~line 36):

```glsl
// --- Ground anti-repetition (Unit 1) + shared distance seam -----------------
uniform bool ar_on = true;             // master toggle for stochastic bombing
uniform float ar_strength : hint_range(0.0, 1.0) = 1.0;  // blend toward bombed result
uniform float ar_far_m : hint_range(200.0, 6000.0) = 2500.0; // distance where AR fades to cheap
uniform vec3 cam_world = vec3(0.0);    // camera world pos (pushed from C#) for distanceWeight
```

- [ ] **Step 2: Add `distanceWeight()`** just before `vec3 tri_w(...)` (~line 218). 0 = near (full quality), 1 = far (cheap):

```glsl
// 0 near .. 1 far — shared LOD factor for the ground-presentation units.
float distanceWeight(vec3 wp){
    float d = length(wp - cam_world);
    return clamp(d / ar_far_m, 0.0, 1.0);
}
```

- [ ] **Step 3: Build + import.**
Run: `dotnet build WG16.csproj` then `"<console exe>" --headless --path . --import`
Expected: 0 errors; `terrain_lab.gdshader` not reported as a compile error. (Sky/material shaders compile at material-build, but `--import` catches GLSL-level parse errors here.)

- [ ] **Step 4: Push `cam_world` from C#.** In `scripts/lab/TerrainLab.cs`, add a setter:
```csharp
public void SetCameraWorld(Vector3 p) => _mat.SetShaderParameter("cam_world", p);
```
In `scripts/lab/TerrainLabUI.cs` `_Process`, push it each frame (camera moves):
```csharp
if (_terrain != null) { _terrain.SetCameraWorld(GetNode<Camera3D>("/root/TerrainLabRoot/Camera").GlobalPosition); }
```
Build: `dotnet build WG16.csproj` → 0 errors.

- [ ] **Step 5: Commit.**
```bash
git add shaders/terrain_lab.gdshader scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.cs
git commit -m "Ground unit 1: distanceWeight seam + anti-repetition uniforms + cam_world push"
```

---

## Task 2: Implement `ar_sample()` — histogram-preserving stochastic bombing

**Files:** Modify `shaders/terrain_lab.gdshader`.

**Approach:** Heitz–Neyret stochastic tiling without the full LUT (which needs a precomputed inverse-histogram texture we don't have): use 3 hash-jittered, per-tile-rotated taps on a triangle grid (like `hex_sample`) BUT with variance-preserving reconstruction so contrast is kept — the contrast loss is what made the old 50/50 blend muddy. This is the proven "no-LUT bombing" form. Tap count drops to 1 at distance via `distanceWeight`.

- [ ] **Step 1: Add `ar_sample()`** right after `hex_sample` (~line 213). It mirrors hex's triangle-grid + per-tile rotate/jitter, variance-corrected, and is a drop-in `(sampler2D, vec2 uv) → vec4`:

```glsl
// Unit 1: histogram-preserving stochastic bombing. 3 jittered+rotated taps on a
// triangle grid, variance-preserving blend (keeps contrast — the 50/50-blend mud
// was variance loss). LODs to 1 plain tap at distance. No LUT needed.
vec4 ar_sample(sampler2D tex, vec2 uv){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    float far = distanceWeight(vec3(0.0));   // placeholder; real wp passed by caller wrapper
    // (the wrapper below passes the true world pos; see ar_sample_wp)
    return textureGrad(tex, uv, dx, dy);     // replaced by ar_sample_wp; kept for safety
}

// World-aware entry: lets distanceWeight use the real fragment world pos.
vec4 ar_sample_wp(sampler2D tex, vec2 uv, vec3 wp){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    if (!ar_on) return textureGrad(tex, uv, dx, dy);
    float far = distanceWeight(wp);
    // far away: single cheap tap (no visible tiling at distance anyway, saves cost)
    if (far > 0.95) return textureGrad(tex, uv, dx, dy);

    float w1,w2,w3; vec2 v1,v2,v3;
    tri_grid(uv, w1,w2,w3, v1,v2,v3);
    // per-tile random rotation + origin jitter (decorrelates repeats)
    mat2 r1 = load_rot(v1), r2 = load_rot(v2), r3 = load_rot(v3);
    vec2 c1 = r1*(uv - v1) + v1 + hash2v(v1);
    vec2 c2 = r2*(uv - v2) + v2 + hash2v(v2);
    vec2 c3 = r3*(uv - v3) + v3 + hash2v(v3);
    vec4 t1 = textureGrad(tex, c1, r1*dx, r1*dy);
    vec4 t2 = textureGrad(tex, c2, r2*dx, r2*dy);
    vec4 t3 = textureGrad(tex, c3, r3*dx, r3*dy);
    vec3 w = vec3(w1,w2,w3); w *= w;                 // sharpen weights
    w /= max(w.x+w.y+w.z, 1e-5);
    vec4 mean = (t1+t2+t3)/3.0;
    vec4 blend = t1*w.x + t2*w.y + t3*w.z;
    float vc = inversesqrt(max(dot(w,w), 1e-5));     // variance restore
    vec4 bombed = mean + (blend - mean)*vc;
    // fade strength toward plain near the far cutoff + by ar_strength knob
    vec4 plain = textureGrad(tex, uv, dx, dy);
    float k = ar_strength * (1.0 - smoothstep(0.7, 0.95, far));
    return mix(plain, bombed, k);
}
```

- [ ] **Step 2: Build + import.** `dotnet build` + headless `--import`. Expected: compiles clean (tri_grid/load_rot/hash2v already exist above this point).

- [ ] **Step 3: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 1: ar_sample_wp stochastic bombing sampler (no-LUT, variance-preserving)"
```

---

## Task 3: Route the splat triplanar (`tp_*`) through `ar_sample_wp`

**Files:** Modify `shaders/terrain_lab.gdshader`.

This is the change that actually fixes the visible tiling — `tp_alb`/`tp_nrm`/`tp_rgh` are the default path.

- [ ] **Step 1: Replace `tp_alb`** (~line 376) — swap the 3 `textureGrad` for `ar_sample_wp` (note `tp_*` already receive `wp`):

```glsl
vec3 tp_alb(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec2 ux=wp.zy/tex_scale_m, uy=wp.xz/tex_scale_m, uz=wp.xy/tex_scale_m;
    return ar_sample_wp(t,ux,wp).rgb*bw.x
         + ar_sample_wp(t,uy,wp).rgb*bw.y
         + ar_sample_wp(t,uz,wp).rgb*bw.z;
}
```

- [ ] **Step 2: Replace `tp_rgh`** (~line 383):

```glsl
float tp_rgh(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=tri_w(nr);
    vec2 ux=wp.zy/tex_scale_m, uy=wp.xz/tex_scale_m, uz=wp.xy/tex_scale_m;
    return ar_sample_wp(t,ux,wp).r*bw.x
         + ar_sample_wp(t,uy,wp).r*bw.y
         + ar_sample_wp(t,uz,wp).r*bw.z;
}
```

- [ ] **Step 3:** `tp_alb` is also reused for normals via `trip_nrm_by` (it calls `tp_alb` on the `_nrm` sampler — see line ~397). That's fine: normals get the same anti-repetition. No separate change.

- [ ] **Step 4: Build + import + A/B captures.**
```
dotnet build WG16.csproj
"<console>" --headless --path . --import
"<windowed>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,120,300,-25,0" "--clouds=0" --auto-shot=C:/tmp/ar_off.png   # then with ar_on flipped off via a temp default, OR compare to pre-commit
"<windowed>" … --auto-shot=C:/tmp/ar_on.png
```
Expected: clouds-off close shot shows visibly LESS repetition with `ar_on`. (Read both PNGs; confirm no obvious new seams/artifacts and contrast preserved.)

- [ ] **Step 5: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 1: route splat triplanar tp_* through ar_sample_wp (fixes default-path tiling)"
```

---

## Task 4: Expose toggle + knobs in the registry; profile

**Files:** Modify `data/lab_controls.json`.

- [ ] **Step 1: Add a Surface-tab group** (anti-repetition lives with surface controls). Add rows:
```json
    { "id": "ar_on", "label": "anti-repeat", "tab": "Surface", "type": "toggle", "param": "ar_on", "default": true, "rand": false },
    { "id": "ar_strength", "label": "anti-repeat amt", "tab": "Surface", "type": "slider", "param": "ar_strength", "min": 0, "max": 1, "default": 1.0, "rand": false },
    { "id": "ar_far_m", "label": "anti-repeat far m", "tab": "Surface", "type": "slider", "param": "ar_far_m", "min": 200, "max": 6000, "default": 2500, "rand": false },
```

- [ ] **Step 2: Validate JSON.** `python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"` → `ok`.

- [ ] **Step 3: Build + profile A/B.**
```
dotnet build WG16.csproj
"<windowed>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,120,300,-25,0" "--clouds=0" --profile=3   # baseline
```
Then toggle `ar_on` off via the panel live, or temporarily default false, and re-profile. Expected: AR adds a measurable but modest cost (3 taps vs 1 on the near band); record the ms delta. If too heavy, `ar_far_m` down / `ar_strength` are the levers.

- [ ] **Step 4: Commit.**
```bash
git add data/lab_controls.json
git commit -m "Ground unit 1: anti-repeat toggle + strength + far-distance knobs (Surface tab)"
```

---

## Task 5: Live judging handoff (THE gate)

- [ ] **Step 1:** Kill strays, launch windowed:
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```
- [ ] **Step 2: Tell the user to judge in MOTION at three ranges** (close / mid / far): does the ground stop reading as tiled/wallpaper? Toggle `anti-repeat` (Surface tab) on/off to A/B. Dial `anti-repeat amt` / `far m`. Confirm contrast isn't muddied (the old failure) and no new seams. Check perf in the FPS HUD.
- [ ] **Step 3:** On approval → proceed to Unit 2 (distance detail layering) plan. If the bombing reads wrong (seams, mush, swimming), it's isolated to `ar_sample_wp` — tune taps/variance or reconsider before stacking unit 2.

---

## Self-Review notes
- **Spec coverage:** implements arc-spec Unit 1 (anti-repetition) + establishes the shared `distanceWeight` seam and the `sampleMaterial`-style single-entry sampling (`ar_sample_wp` is now the one fetch all triplanar goes through). `groundData` seam is untouched (Unit 4's concern). Matches build order (Unit 1 first, owns the sampler).
- **Placeholder scan:** `ar_sample()` stub is explicitly marked superseded by `ar_sample_wp` and kept only so an accidental old call still compiles; real path is `ar_sample_wp`. (Could delete `ar_sample` entirely in Task 3 if no caller — verify with grep; if unused, remove it.) No TBDs.
- **Type consistency:** `ar_sample_wp(sampler2D, vec2, vec3)` used identically in Task 2 (def) and Task 3 (calls). `distanceWeight(vec3)` def Task 1, used Task 2. `cam_world` uniform set via `SetCameraWorld` (Task 1 C#). Registry `param` ids (`ar_on`/`ar_strength`/`ar_far_m`) match the uniforms exactly.
- **Watch-point:** confirm `tri_grid`/`load_rot`/`hash2v` are defined ABOVE `ar_sample_wp` in the file (they are, ~lines 159-195, and `ar_sample_wp` goes in at ~213) so no forward-reference. The legacy `ar_sample` stub references `distanceWeight(vec3(0.0))` harmlessly; delete it in Task 3 if grep shows no caller.
