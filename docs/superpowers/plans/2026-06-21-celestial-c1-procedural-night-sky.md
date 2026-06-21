# Celestial C1 — Procedural Fantasy Night-Sky Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended — coupled GPU bake↔shader work, user gates the look live) or superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`). Spec: `specs/2026-06-21-celestial-c1-procedural-night-sky-design.md`.

**Goal:** Replace the uniform Milky Way fog band with a procedural, tunable, fantasy-capable night sky (galaxy core/dust/star-clouds/color + nebula clouds + reworked starfield + presets), with the static structure **baked** to one runtime texture sample so it's cheaper than today.

**Architecture:** Evolve the existing `milkyway_bake.glsl` + AtmosphereCompute bake seam (built 2026-06-21) into a full night-sky bake: a compute shader composites galaxy + nebulae into an `rgba16f` lat-long texture (re-baked only on a tunable/preset change, on the render-thread RD); `cloud_sky.gdshader`'s `stars_layer()` samples it in one tap and adds the live point-stars. The procedural path stays as the `night_sky_baked`-off reference; each stage is pixel-diff-verified baked==procedural.

**Tech Stack:** Godot 4.6 mono (C# + GLSL compute via `RenderingDevice`), `Texture2Drd` seam, `Std430Writer`, the `cloud_sky.gdshader` sky shader.

## Global Constraints

- **Perf is first-class:** runtime night-sky cost ≤ today (target lower). Static structure baked; re-bake only on change, never per-frame. Runtime = 1 sample + live point-stars.
- **Baked default, pixel-diff-verified == procedural:** the bake compute copies `cloud_sky.gdshader`'s `hash13`/`vnoise3`/`fbm3` VERBATIM. `night_sky_baked` toggle restores the procedural reference exactly. Verify each stage: night, baked vs `--nsbaked=0`, sky-region mean diff within the star-twinkle floor (~0.1).
- **Look gates on the user's live eye** (fantasy aesthetic) — the final tuning is owed; build tunable + presets so the user gates/tunes live. Per-task verification is build + pixel-diff + `--profmove --profile=3`, NOT a look judgment.
- **Stay in sky/light files:** `cloud_sky.gdshader`, `night_sky_bake.glsl`, `AtmosphereCompute.cs`, `CloudVolume.cs`, `TerrainLabUI.*` sky bits, `data/*`. No terrain/ground/godray edits. Keep the bake's copied noise block in sync with `cloud_sky.gdshader`.
- **Run (one Godot; kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`):** `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --time=0 ...`. Build `dotnet build WG16.csproj` from the project dir. Night view: `--time=0` (or 23); `--celestial=1` boosts MW for review.
- **Coordination:** `git add` your paths explicitly, never `-A`. Commit-by-default; push only when asked.

## File Structure

- `shaders/night_sky_bake.glsl` — NEW (renames+grows `milkyway_bake.glsl`): bake galaxy + nebulae → `rgba16f` color. Sole owner of the procedural night-sky structure math (mirrored in cloud_sky's reference branch).
- `shaders/cloud_sky.gdshader` — `stars_layer()`: sample the baked color OR the procedural reference; reworked point-stars. Owns the LIVE (twinkle) star layer + the rotation/fade.
- `scripts/lab/AtmosphereCompute.cs` — the bake node: params buffer, `BakeNightSky`, `NightSkyTexture`, `SetNightSky(...)`, re-bake-on-change.
- `scripts/lab/CloudVolume.cs` — `_skyMat` setters: `SetNightSkyTex`, `SetNightSkyBaked`, per-layer param setters.
- `scripts/lab/TerrainLabUI.{Lighting,Clouds,Process,Cli}.cs` + `TerrainLabUI.cs` — push params, toggle, readiness gate, CLI.
- `scripts/lab/NightSkyState.cs` — NEW small state struct (galaxy + nebula + star params) — or extend the existing `_stars` state.
- `scripts/lab/TerrainLabUI.NightSkyPresets.cs` — NEW: load/apply `data/night_sky_presets.json`.
- `data/lab_controls.json` — Night-tab knobs. `data/night_sky_presets.json` — NEW presets.

## Reference points (read before building)

- `shaders/milkyway_bake.glsl` + `AtmosphereCompute.cs` (`_mwTex`/`_mwRd`/`BakeMilkyWay`/`MwParams`/`SetMilkyWay`, the EnsureSets `_mwSet` block, `_ExitTree` frees) — the seam to generalize.
- `shaders/cloud_sky.gdshader:296-328` `stars_layer()` (current baked/procedural branch + point-stars) + `hash13`/`vnoise3`/`fbm3` (lines ~129-149) + `dir_to_uv` / the bake uv inverse.
- `scripts/lab/TerrainLabUI.Lighting.cs:134-137` (SetStars/SetMilkyWay push), `.Apply.cs` star/mw cases, `.Clouds.cs` `mw_baked` case + `_mwBakedOn`/`_mwActivated`, `.Process.cs` mw readiness gate, `data/lab_controls.json` Night `star_*`/`mw_*` rows.

---

## Task 1: Generalize the bake → galaxy (core + dust + star-clouds + fantasy color), baked

Turns the bake from a 1-channel band-structure into a full **galaxy color** bake with a concentrated **core** (kills the uniform arc), **dust lanes**, **star-cloud knots**, and a **2-color fantasy gradient**. The procedural reference in `cloud_sky.gdshader` is rewritten to MATCH (same math) so pixel-diff stays the gate. Nebulae/stars are later tasks.

**Files:** Create `shaders/night_sky_bake.glsl` (from `milkyway_bake.glsl`); delete `milkyway_bake.glsl`; modify `AtmosphereCompute.cs`, `cloud_sky.gdshader`, `CloudVolume.cs`, `TerrainLabUI.{cs,Lighting.cs,Clouds.cs,Process.cs}`, `data/lab_controls.json`.

**Interfaces:**
- Produces: `AtmosphereCompute.NightSkyTexture` (`Texture2Drd`), `AtmosphereCompute.SetNightSky(GalaxyParams g)` where `GalaxyParams` = (Vector3 coreDir, float coreSize, float tilt, float width, float curve, float dust, Vector3 coreColor, Vector3 armColor, float brightness); `CloudVolume.SetNightSkyTex(Texture2Drd)`, `CloudVolume.SetNightSkyBaked(bool)`.

- [ ] **Step 1: Write `shaders/night_sky_bake.glsl`** (galaxy color → rgb).

Copy `milkyway_bake.glsl`'s header + the VERBATIM `hash13`/`vnoise3`/`fbm3` block. Replace the Params + `main()` with the galaxy model below. Param buffer (std430): `vec4 coreDir_size` (xyz coreDir, w coreSize), `vec4 tilt_width_curve_dust`, `vec4 coreColor_bright` (rgb core color, w brightness), `vec4 armColor` (rgb).

```glsl
// galaxy color at a (un-rotated) direction d. Core = a bright bulge concentrated around coreDir (not a
// uniform band); band = a great circle through the core plane, carved by dust-lane fbm; star clouds = bright
// fbm knots; color = mix(armColor, coreColor) by core proximity. All look constants are gate-tunable.
vec3 galaxy_color(vec3 d){
    vec3 coreDir = normalize(P.coreDir_size.xyz);
    float coreSize = P.coreDir_size.w;
    float tilt = P.tilt_width_curve_dust.x, width = P.tilt_width_curve_dust.y;
    float curve = P.tilt_width_curve_dust.z, dust = P.tilt_width_curve_dust.w;
    // band plane (tilted), thickness wobbling along it; warp the band toward a slight S-curve by `curve`.
    vec3 planeN = normalize(vec3(sin(tilt), 0.35, cos(tilt)));
    float along = dot(normalize(cross(planeN, vec3(0,1,0))), d);          // position along the band
    float w = width * (0.6 + 0.8 * fbm3(d * 2.0 + vec3(11.0)));
    float bandDist = abs(dot(d, planeN) + curve * along * along * sign(dot(d,planeN)));
    float band = smoothstep(w, 0.0, bandDist);
    // core bulge: a bright gaussian-ish glow around coreDir, falling off; concentrates brightness on one side.
    float cd = max(dot(d, coreDir), 0.0);
    float core = pow(cd, max(1.0, 60.0 * coreSize));                       // tighter as coreSize→0
    // star clouds + dust within the band.
    float clouds = fbm3(d * 6.0);
    float lanes = fbm3(d * 16.0 + vec3(5.0));
    float structure = smoothstep(0.30, 0.72, clouds) * mix(1.0 - dust, 1.0, lanes);
    float lum = band * structure + core * 0.8;                            // core also lifts off-band
    vec3 col = mix(P.armColor.rgb, P.coreColor_bright.rgb, clamp(core * 1.5 + band * 0.3, 0.0, 1.0));
    return col * lum * P.coreColor_bright.w;                               // w = brightness
}
void main(){ /* texel→dir (full-sphere lat-long, as milkyway_bake), imageStore vec4(galaxy_color(dir), 1.0) */ }
```
> The texel→direction + `imageStore` boilerplate is identical to `milkyway_bake.glsl`'s `main()` (full-sphere lat-long). Keep it.

- [ ] **Step 2: AtmosphereCompute — generalize the bake.** Rename `_mwTex/_mwRd/_mwShader/_mwPipe/_mwSet/_mwParamBuf` usages to the night-sky bake (keep the field names or rename to `_ns*`); `Compile("res://shaders/night_sky_bake.glsl", "night_sky_bake")`; widen `MwParams()` → `NsParams()` writing the 4 galaxy vec4s; add `public Texture2Drd? NightSkyTexture => _mwRd;` and replace `SetMilkyWay(tilt,width)` with:
```csharp
    public struct GalaxyParams { public Vector3 CoreDir; public float CoreSize, Tilt, Width, Curve, Dust; public Vector3 CoreColor, ArmColor; public float Brightness; }
    private GalaxyParams _gx = DefaultGalaxy();
    public void SetNightSky(GalaxyParams g) { if (!g.Equals(_gx)) { _gx = g; _mwDirty = true; } }
    private static GalaxyParams DefaultGalaxy() => new GalaxyParams {
        CoreDir = new Vector3(0.3f, 0.2f, 0.93f).Normalized(), CoreSize = 0.5f, Tilt = 0.6f, Width = 0.12f,
        Curve = 0.0f, Dust = 0.5f, CoreColor = new Vector3(0.95f, 0.75f, 0.55f), ArmColor = new Vector3(0.45f, 0.55f, 0.85f), Brightness = 0.6f };
```
`NsParams()` writes `Vec4(_gx.CoreDir, _gx.CoreSize).Vec4(_gx.Tilt,_gx.Width,_gx.Curve,_gx.Dust).Vec4(_gx.CoreColor, _gx.Brightness).Vec4(_gx.ArmColor.X,_gx.ArmColor.Y,_gx.ArmColor.Z,0f)`. Keep `_mwTex` as `rgba16f` (already), `BakeMilkyWay`→`BakeNightSky`, the `_ExitTree`/`EnsureSets`/InitCompute wiring unchanged (just the renamed shader/params).

- [ ] **Step 3: cloud_sky.gdshader — sample the baked galaxy color + matching procedural reference.** Rename `mw_baked_tex`→`night_sky_tex`, `mw_baked`→`night_sky_baked`. In `stars_layer()`, replace the milky-way block: when `night_sky_baked`, `vec3 gx = texture(night_sky_tex, ns_uv(sr)).rgb;` (same uv inverse as before); else compute `galaxy_color(sr)` inline (PASTE the same `galaxy_color` from the bake, using `night_sky_*` uniforms for the params). Then `return (gx + star) * fade;` (stars added in Task 3; keep the existing point-stars for now). Add the galaxy uniforms (coreDir/size/tilt/width/curve/dust/coreColor/armColor/brightness) for the procedural branch.

- [ ] **Step 4: CloudVolume + TerrainLabUI rename + push.** `SetMilkyWayTex`→`SetNightSkyTex`, `SetMilkyWayBaked`→`SetNightSkyBaked` (+ the `night_sky_*` uniform setters). `TerrainLabUI.cs` AttachClouds: `_cloud.SetNightSkyTex(_atmosphere.NightSkyTexture)`. `.Lighting.cs`: replace `_atmosphere?.SetMilkyWay(...)` with `_atmosphere?.SetNightSky(BuildGalaxyParams())` (from the night-sky state — Task 4 adds the state; for now build from the current `_stars.MwTilt/MwWidth` + the galaxy defaults). `.Clouds.cs` `mw_baked`→`night_sky_baked` toggle + `_nsBakedOn`/`_nsActivated` (rename `_mwBakedOn`). `.Process.cs` readiness gate renamed. `.Cli.cs` `--mwbaked`→`--nsbaked`. `data/lab_controls.json` `mw_baked`→`night_sky_baked`.

- [ ] **Step 5: Build + pixel-diff + perf.** `dotnet build` → 0 errors. Then night: capture `--time=0 --nsbaked=1` vs `--time=0 --nsbaked=0`; sky-region mean diff must be within ~0.1 (the star-twinkle floor) — proves baked==procedural galaxy. `--profmove --profile=3 --time=0` ≤ the pre-C1 night number. (Look is NOT judged here — just that the band now has a bright core, not a uniform arc; the user gates the look at Task 6.)

- [ ] **Step 6: Commit.**
```bash
git add shaders/night_sky_bake.glsl shaders/cloud_sky.gdshader scripts/lab/AtmosphereCompute.cs scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.Clouds.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Cli.cs data/lab_controls.json
git rm shaders/milkyway_bake.glsl
git commit -m "celestial(C1-1): generalize MW bake → galaxy (core+dust+star-clouds+fantasy color), baked"
```

---

## Task 2: Nebula layer (≤4 procedural colored gas clouds) in the bake + reference

Adds colored nebula blobs composited into the bake (and the procedural reference). Separate from the galaxy so nebulae-only / band-only both work.

**Files:** `shaders/night_sky_bake.glsl`, `shaders/cloud_sky.gdshader`, `scripts/lab/AtmosphereCompute.cs`, `CloudVolume.cs`, `TerrainLabUI.*`, `data/lab_controls.json`.

**Interfaces:** Produces `AtmosphereCompute.SetNebulae(NebulaParams[] n)`, `NebulaParams` = (Vector3 dir, Vector3 color, float scale, float density). Up to 4.

- [ ] **Step 1: `night_sky_bake.glsl` — add nebulae.** Add `vec4 neb_dir_scale[4]` (xyz dir, w scale), `vec4 neb_color_dens[4]` (rgb color, w density), and a count, to the param buffer. Add:
```glsl
vec3 nebula_color(vec3 d){
    vec3 acc = vec3(0.0);
    for (int i = 0; i < int(P.neb_count); i++){
        vec3 nd = normalize(P.neb_dir_scale[i].xyz); float sc = P.neb_dir_scale[i].w;
        float prox = max(dot(d, nd), 0.0);
        float falloff = pow(prox, mix(40.0, 6.0, sc));                    // bigger sc = broader cloud
        float n = fbm3(d * mix(8.0, 3.0, sc) + vec3(float(i) * 13.7));    // cloud shape
        float a = falloff * smoothstep(0.35, 0.8, n) * P.neb_color_dens[i].w;
        acc += P.neb_color_dens[i].rgb * a;
    }
    return acc;
}
// in main(): vec3 c = galaxy_color(dir) + nebula_color(dir);
```

- [ ] **Step 2: cloud_sky.gdshader reference.** Mirror `nebula_color` in the procedural branch (uniforms `neb_dir_scale[4]`, `neb_color_dens[4]`, `neb_count`).

- [ ] **Step 3: AtmosphereCompute + CloudVolume + TerrainLabUI.** Add `_nebs` to the state + `SetNebulae`; widen `NsParams()` to write the nebula arrays (`Vec4Array` or per-element `Vec4`); push from `.Lighting.cs`; uniforms in cloud_sky.

- [ ] **Step 4: Build + pixel-diff + perf.** Baked vs procedural with 1-2 nebulae enabled (set via the lab controls from Task 4 or a temporary default) — mean diff within the twinkle floor; perf unchanged (baked).

- [ ] **Step 5: Commit.** `git commit -m "celestial(C1-2): nebula layer (≤4 procedural colored clouds) in the night-sky bake"`

---

## Task 3: Starfield rework (live point-stars: magnitude/size/color variety)

Reworks the live point-stars (kept procedural for twinkle): few bright / many faint, size variety, warm/cool color tints, tunable density/twinkle.

**Files:** `shaders/cloud_sky.gdshader` (point-star block in `stars_layer`), `data/lab_controls.json`, `TerrainLabUI.Apply.cs`.

- [ ] **Step 1: cloud_sky.gdshader — rework the point stars.** Replace the star block with a magnitude-stratified version:
```glsl
// few-bright/many-faint: a second sparser, brighter grid layered over a dense faint one; color by temp.
vec3 star_field(vec3 sr){
    vec3 acc = vec3(0.0);
    for (int L = 0; L < 2; L++){
        float scale = (L == 0) ? 250.0 : 90.0;                            // dense faint / sparse bright
        float bright = (L == 0) ? 0.5 : 1.4;
        vec3 cell = floor(sr * scale); vec3 f = fract(sr * scale) - 0.5;
        float h = hash13(cell + float(L) * 7.0);
        float present = step(1.0 - star_density * (L == 0 ? 0.09 : 0.03), h);
        vec3 jit = (vec3(hash13(cell+1.7), hash13(cell+4.3), hash13(cell+8.1)) - 0.5) * 0.7;
        float d = length(f - jit);
        float sz = mix(0.10, 0.20, hash13(cell+20.0));                    // size variety
        float pt = present * smoothstep(sz, 0.0, d);
        float tw = mix(1.0, 0.5 + 0.5 * sin(TIME * 3.0 + h * 62.8), star_twinkle);
        float temp = hash13(cell + 30.0);                                  // 0 warm .. 1 cool
        vec3 tint = mix(vec3(1.0, 0.82, 0.7), vec3(0.75, 0.85, 1.0), temp);
        acc += tint * (pt * tw * mix(0.35, 1.0, hash13(cell+12.0)) * bright);
    }
    return acc * star_brightness;
}
```
Call `star_field(sr)` in `stars_layer` (replaces the inline star block); `return (gx + neb + star_field(sr)) * fade;`.

- [ ] **Step 2: Build + perf + (light) pixel sanity.** Build; night perf unchanged-ish (stars are live, cheap). No bake involved → no bake/proc diff needed; sanity-shoot `--time=0` for no errors.

- [ ] **Step 3: Commit.** `git commit -m "celestial(C1-3): starfield rework — magnitude/size/color-temp variety (live)"`

---

## Task 4: Tunables wired to the Night tab (state struct + controls)

Exposes every galaxy + nebula + star param as a Night-tab knob, routed through a `NightSkyState` to `ComposeLighting` → `AtmosphereCompute.SetNightSky/SetNebulae` (re-bake) + the live star uniforms.

**Files:** `scripts/lab/NightSkyState.cs` (new), `TerrainLabUI.{Apply,Lighting}.cs`, `data/lab_controls.json`.

- [ ] **Step 1: `NightSkyState.cs`** — a struct/class holding galaxy (coreDir as az/elev or vec3, coreSize, tilt, width, curve, dust, coreColor, armColor, brightness), nebulae (4× dir/color/scale/density), stars (brightness, density, twinkle). Defaults = `DefaultGalaxy()` + a couple of demo nebulae.

- [ ] **Step 2: lab controls** — Night-tab rows for each: `gx_core_az`, `gx_core_elev`, `gx_core_size`, `gx_tilt`, `gx_width`, `gx_curve`, `gx_dust`, `gx_core_color` (scenecolor), `gx_arm_color`, `gx_brightness`; `neb1_*`..`neb4_*` (or a count + per-nebula az/elev/color/scale/density); `star_brightness`/`star_density`/`star_twinkle` (exist). Route via `.Apply.cs` cases → set the `NightSkyState` field → `ComposeLighting()`.

- [ ] **Step 3: `ComposeLighting`** pushes `SetNightSky` + `SetNebulae` (re-bake on change) + the live star uniforms. **Debounce:** re-bake only when a galaxy/nebula value actually changed (`SetNightSky`/`SetNebulae` already guard with `!Equals`), so dragging a star (live) knob doesn't re-bake.

- [ ] **Step 4: Build + verify** the knobs drive the bake (auto-shot at two `gx_brightness` values differs) + perf. Commit. `git commit -m "celestial(C1-4): Night-tab tunables for galaxy + nebulae + stars (re-bake on change)"`

---

## Task 5: Presets (`night_sky_presets.json` + loader + picker)

Named "cool" night skies bundling the full param set; picking one applies + re-bakes.

**Files:** `data/night_sky_presets.json` (new), `scripts/lab/TerrainLabUI.NightSkyPresets.cs` (new), `data/lab_controls.json` (picker), `.Cli.cs` (`--nspreset=N`).

- [ ] **Step 1: `night_sky_presets.json`** — ≥4 presets: a dialled-back **"Subtle"** (low brightness, no nebulae, neutral color), **"Crimson Rift"** (red/orange core, magenta nebula, strong dust), **"Aurora Veil"** (teal/green nebulae, cool arms), **"Deep Field"** (many faint stars, dim galaxy, scattered nebulae). Each = the full `NightSkyState` JSON.
- [ ] **Step 2: `TerrainLabUI.NightSkyPresets.cs`** — load the json, `ApplyNightSkyPreset(int)` → set `NightSkyState` + `ComposeLighting()` (re-bakes). Mirror `FantasyPresets.cs`.
- [ ] **Step 3: Picker (lab control) + `--nspreset=N`** apply at startup.
- [ ] **Step 4: Build + verify** each preset bakes a distinct sky (auto-shot each, confirm they differ) + perf. Commit. `git commit -m "celestial(C1-5): night-sky presets (Subtle/Crimson Rift/Aurora Veil/Deep Field) + picker + --nspreset"`

---

## Task 6: USER LIVE LOOK EYE-GATE

The fantasy look is the user's call. Drive `review.tscn --time=0 --celestial=1`; cycle presets (`--nspreset`/picker) + tune Night-tab knobs live. **Judge:** no longer a uniform fog band (the core reads as a focal point); nebulae look "cool"; stars have variety; presets give distinct cool skies; ties with the night mood. Confirm `night_sky_baked` on/off is visually identical (the bake is faithful). Tune to taste; pick the default preset. Record in DECISIONS + NEEDS_REVIEW + ROADMAP. PASS → default-on. (Perf already verified per-task: baked = 1 sample, ≤ today.)

---

## Self-Review

**Spec coverage:** galaxy core/dust/star-clouds/color → T1 ✓; nebulae → T2 ✓; starfield rework → T3 ✓; tunables → T4 ✓; presets → T5 ✓; bake pipeline (evolve milkyway_bake + AtmosphereCompute, 1-sample runtime) → T1-2 ✓; baked==procedural pixel-diff + toggle → T1/T2 step 5 ✓; perf ≤ today → every task's profile step ✓; look gate → T6 ✓; out-of-scope (C2/C3, animated nebulae) → not in any task ✓.

**Placeholder scan:** Procedural look constants (noise scales, falloff powers, default colors) are concrete starting values explicitly flagged **gate-tunable** — they're a working v1 the user tunes at T6, not "TBD". Integration code (renames, param order, setters, seam) is exact. The texel→dir `main()` boilerplate references `milkyway_bake.glsl` (an existing file copied verbatim), not a placeholder.

**Type consistency:** `NightSkyTexture`/`SetNightSky(GalaxyParams)`/`SetNebulae(NebulaParams[])` (T1/T2) match the consumers (CloudVolume `SetNightSkyTex`/`SetNightSkyBaked`, TerrainLabUI push). `night_sky_tex`/`night_sky_baked` + galaxy/nebula uniforms match between the bake (T1/T2) and cloud_sky's reference branch. `NightSkyState` (T4) feeds `GalaxyParams`/`NebulaParams[]`. Renames (`mw_*`→`night_sky_*`/`ns*`) are applied consistently across shader uniform, C# setter, control id, and CLI flag.
