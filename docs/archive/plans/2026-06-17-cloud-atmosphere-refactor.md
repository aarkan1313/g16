# Cloud + Atmosphere Refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (the author executes inline). Steps use `- [ ]`. NOTE: there is **no automated assert for "looks good"** — GPU/visual work. Mechanical steps (builds, runs windowed, shaders compile, coverage=0 gives clear sky, no tiling in an overhead shot, FPS in budget) are the checkable part; the real gate is the USER's eye in motion. FLAG each visual gate for the user; never claim a look is good from a still.

**Goal:** Replace the cloud render path with a from-scratch tunable system (photoreal↔stylized↔sparse), repetition killed at all ranges, true clear→overcast coverage, in-march god rays (no FogVolume), all presence features rebuilt — on a mid-range GPU budget via half-res temporal reconstruction.

**Architecture:** Blend of the proven HZD/clayjohn recipe (noise authoring, density, lighting, coverage remap) built into WG16's compute→render-thread→Texture2Drd plumbing (the part that works). Units: CloudNoise (bake) · CloudWeather (large 2D field) · CloudRaymarch (compute, half-res, amortized) · temporal reconstruction · sky composite · presence (shadow/dim/aerial/GI/mood) · god rays (in-scatter + optional screen-space). Reuses CloudVolume.cs's render-thread dispatch + CloudNoiseCompute's local-RD bake.

**Tech Stack:** Godot 4.6 mono; GLSL compute on the main RD via `RenderingServer.CallOnRenderThread`; `Texture2Drd` sampled by `cloud_sky.gdshader` (view dir) + terrain `light()` (world XZ). Local-RD bake for noise (windowed only — not headless).

**Spec:** `docs/superpowers/specs/2026-06-17-cloud-atmosphere-refactor-design.md`.

**Reuse map (verified against code):**
- `scripts/lab/CloudNoiseCompute.cs` — local-RD bake + `BakeRaw()` → main-RD textures. Keep; retune noise scales.
- `scripts/lab/CloudVolume.cs` — render-thread dispatch, `Texture2Drd` RID-once, param buffers, knob setters, `SetCameraWorld`/`SetSun`/`SetSkyColors`/`Overcast()`. Keep the plumbing; rework the dispatch contents + add a history buffer.
- `scripts/lab/CloudWeather.cs` — 2D weather field. Rework for the large-scale low-freq field.
- `scripts/lab/CloudParams.cs` + `data/cloud_params.json` — knobs. Extend.
- `shaders/cloud_raymarch.glsl` / `cloud_shadow.glsl` / `cloud_sky.gdshader` — rewritten.
- `shaders/cloud_godray_fog.gdshader` + its FogVolume wiring — REMOVED (Task 1).
- `data/cloud_presets.json` — re-authored (Task 8).
- `data/lab_controls.json` — 23 cloud controls exist; reuse, add a few (Task 8).

---

### Task 1: Back up the current system, then rip out the god-ray FogVolume

**Files:**
- Modify: `scripts/lab/CloudVolume.cs` (remove `BuildGodrayVolume`, `_godrayVol`, `_godrayMat`, `SetGodraysEnabled` FogVolume bits)
- Modify: `scripts/lab/TerrainLabUI.cs` (remove the `BuildGodrayVolume()` call + add-child wiring ~line 113)
- Delete: `shaders/cloud_godray_fog.gdshader`

- [ ] **Step 1: Tag the current working tree as the cloud backup**

Run:
```bash
git -C /c/Wg16/wg-16-project tag backup-clouds-pre-refactor-2026-06-17
```
Expected: tag created (no output). This is the revert point if the refactor stalls.

- [ ] **Step 2: Remove the god-ray FogVolume wiring from TerrainLabUI**

Find the block (~line 108-114) that calls `_cloud.BuildGodrayVolume()` and adds the FogVolume to the tree. Delete that block and the `--godrays` CLI apply that targets the FogVolume. Leave a `// god rays rebuilt in-march (Task 7)` marker.

- [ ] **Step 3: Remove the FogVolume members + builder from CloudVolume**

Delete `_godrayVol`, `_godrayMat`, `BuildGodrayVolume()`, and the FogVolume body of `SetGodraysEnabled` (keep a stub `public void SetGodraysEnabled(bool on){ _godraysOn = on; }` + `GodraysOn` — the in-march god rays in Task 7 reuse the flag; it just won't touch a FogVolume). Remove the `_godrayVol` cleanup in `_ExitTree`.

- [ ] **Step 4: Delete the fog shader**

Run:
```bash
git -C /c/Wg16/wg-16-project rm shaders/cloud_godray_fog.gdshader
```

- [ ] **Step 5: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj`
Expected: 0 errors.

- [ ] **Step 6: Commit**
```bash
git -C /c/Wg16/wg-16-project add -A
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T1: backup tag + rip out god-ray FogVolume (rebuilt in-march later)"
```

---

### Task 2: CloudWeather — large low-frequency weather field + coverage that reaches clear

**Files:**
- Modify: `scripts/lab/CloudWeather.cs`

- [ ] **Step 1: Read the current CloudWeather to see its field + Mean**

Read `scripts/lab/CloudWeather.cs`. Note `BakeRaw()`, `Res`, `Mean()`.

- [ ] **Step 2: Rework the field to a large-scale, low-freq FBM with a controllable mean**

The field must vary cloud placement over a world scale FAR larger than the shape noise (so macro placement never repeats in view) AND let coverage reach true zero. In the FBM, ensure the R channel (coverage) spans a full 0..1 with low frequency (few octaves, large wavelength). Keep `Res` (e.g. 256) and `Mean()`. The KEY change is the sampling scale used in the raymarch (Task 4), but make the field itself smooth + full-range here.

```csharp
// CloudWeather: large-scale 2D field. R = coverage bias, G = cloud type, B = density bias.
// Low-frequency so the macro distribution reads as weather systems, not a tiled pattern.
public static byte[] BakeRaw()
{
    int res = Res;
    var data = new float[res * res * 4];
    for (int y = 0; y < res; y++)
    for (int x = 0; x < res; x++)
    {
        float u = x / (float)res, v = y / (float)res;
        // 3-octave value-noise FBM, large wavelength (few cycles across the whole field)
        float cov = Fbm(u * 2.3f, v * 2.3f, 3) ;          // 0..1 coverage bias
        float typ = Fbm(u * 1.7f + 11.0f, v * 1.7f + 7.0f, 2);
        float den = Fbm(u * 3.1f + 23.0f, v * 3.1f + 19.0f, 3);
        int i = (y * res + x) * 4;
        data[i] = cov; data[i+1] = typ; data[i+2] = den; data[i+3] = 1f;
    }
    var bytes = new byte[data.Length * sizeof(float)];
    System.Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
    return bytes;
}
```
If `Fbm`/value-noise helpers don't exist, add small private static ones (hash + bilinear value noise + octave sum, normalized to 0..1). Keep `Mean()` computing the average of channel R.

- [ ] **Step 3: Build**

Run: `dotnet build WG16.csproj`. Expected: 0 errors.

- [ ] **Step 4: Commit**
```bash
git -C /c/Wg16/wg-16-project add scripts/lab/CloudWeather.cs
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T2: large low-freq weather field, full-range coverage"
```

---

### Task 3: CloudNoise — author shape/detail at mismatched tiling scales

**Files:**
- Modify: `shaders/cloud_noise_3d.glsl` (noise frequencies), `scripts/lab/CloudNoiseCompute.cs` (only if res changes)

- [ ] **Step 1: Read the noise shader**

Read `shaders/cloud_noise_3d.glsl`. Note the Perlin-Worley (mode 0, RGBA) and Worley detail (mode 1) generation and the frequencies used.

- [ ] **Step 2: Ensure the volumes are independently tileable + cover several octaves**

The shape volume (96³) must be seamlessly tileable (Worley/Perlin on a periodic lattice) AND contain a low + mid + high octave in RGBA (R = Perlin-Worley base, G/B/A = increasing-freq Worley). The detail volume (32³) tileable high-freq Worley. The ACTUAL anti-repeat comes from the world-space SAMPLE SCALES in Task 4 (mismatched periods) — here just guarantee each volume tiles cleanly and packs distinct octaves so Task 4 has frequencies to combine. If the existing shader already does periodic Worley + octaves, no change needed; confirm and note it.

- [ ] **Step 3: Build + run windowed; confirm the bake still runs**

Run: `dotnet build WG16.csproj`, launch `scenes/terrain_lab.tscn --rendering-driver vulkan`.
Expected (MECHANICAL): "CloudNoiseCompute: baked 96³…" + "compute initialized" print, no errors. (Visual judged after Task 4.)

- [ ] **Step 4: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_noise_3d.glsl scripts/lab/CloudNoiseCompute.cs
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T3: tileable multi-octave shape+detail volumes for anti-repeat"
```

---

### Task 4: CloudRaymarch core — HZD-recipe march, full-res first (correctness)

**Files:**
- Rewrite: `shaders/cloud_raymarch.glsl`
- Modify: `scripts/lab/CloudVolume.cs` (param buffer fields if the recipe needs new uniforms)

Build the march at FULL res first (temporal/half-res added in Task 6) so correctness is judged before optimizing.

- [ ] **Step 1: Rewrite the density model to the HZD recipe**

`sample_density(p, baseR, topR, windOff)` — curved shell (radial height), world-XZ lp (shadow coupling). Recipe:
```glsl
// 1) weather: large sample scale so macro placement doesn't repeat in view
vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;   // WEATHER_SCALE ~ 1/80000
vec4 w = texture(weather_tex, wuv);
float coverage = clamp(P.coverage + (w.r - 0.5) * 0.7, 0.0, 1.0);
float type     = clamp(P.cloud_type + (w.g - 0.5) * 0.4, 0.0, 1.0);
// 2) base shape, mismatched scale from detail (anti-repeat defense #1)
float sScale = SHAPE_SCALE / max(P.size, 0.01);               // SHAPE_SCALE ~ 1/9000
vec3 suv = lp * sScale + vec3(windOff.x, h, windOff.y) * sScale;
vec4 sh = texture(shape_tex, suv);
float fbm = sh.g*0.625 + sh.b*0.25 + sh.a*0.125;
float base = remap(sh.r, fbm*0.3, 1.0, 0.0, 1.0);
// 3) coverage → threshold so coverage=0 → no cloud (true clear), 1 → overcast
float thresh = mix(0.92, 0.02, coverage);          // high thresh at cov0 = nothing passes
float band = mix(0.30, 0.10, P.edge);
float shape = clamp(remap(base, thresh, min(thresh+band,1.0), 0.0, 1.0), 0.0, 1.0);
shape *= type_gradient(h, type);
if (shape <= 0.0) return 0.0;
// 4) detail erosion, mismatched scale + height-varied (anti-repeat #4)
float dScale = DETAIL_SCALE / max(P.detail_size, 0.01);       // DETAIL_SCALE ~ 1/1300
vec3 duv = lp * dScale + vec3(windOff.x*1.7, h, windOff.y*1.7) * dScale;
float det = texture(detail_tex, duv).r;
float erodeAmt = mix(0.25, 0.6, h) * P.detail;     // erode tops more than bases
shape = clamp(remap(shape, det*erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
return shape * P.density;
```
Define the `*_SCALE` consts at top. The mismatched `SHAPE_SCALE` (~1/9000 m) vs `DETAIL_SCALE` (~1/1300 m) vs `WEATHER_SCALE` (~1/80000 m) are anti-repeat defenses #1 and #3.

- [ ] **Step 2: Add domain warp (anti-repeat #2)**

Before sampling the shape, warp lp by a low-freq detail tap:
```glsl
vec3 warp = (texture(detail_tex, lp * (DETAIL_SCALE*0.25)).rrr - 0.5) * WARP_AMOUNT; // WARP_AMOUNT ~ 600m
vec3 lpw = lp + warp;   // use lpw for the shape suv sample
```
Apply to the shape sample (`suv = lpw * sScale + ...`). This bends tiling seams organic — the biggest "kills procedural look" lever.

- [ ] **Step 3: Curved camera-anchored shell + above-clamp in main()**

(As proven to compile in the prior pass — keep it.)
```glsl
vec3 rd = dir_from_texel(px);
float belowBase = min(P.cam_world.y, P.altitude - 1.0);
vec3 ro = vec3(P.cam_world.x, PLANET_R + belowBase, P.cam_world.z);
float baseR = PLANET_R + P.altitude;  float topR = baseR + P.thickness;
vec2 hitB = ray_sphere(ro, rd, baseR);  vec2 hitT = ray_sphere(ro, rd, topR);
float tStart = max(hitB.y, 0.0);  float tEnd = max(hitT.y, 0.0);
```

- [ ] **Step 4: HZD lighting in the march loop (Beer + HG + powder + ramped steps)**

In the integration loop: ramped step count (more steps where density found), `light_march` (cone toward sun), `phase = HG(cosA, hg_aniso)`, powder, Beer transmittance, ambient from mood sky colors. (The existing loop is close — keep its Beer/HG/powder; ensure steps clamp `clamp(int(P.steps),16,160)` and `light_march` uses baseR/topR.)

- [ ] **Step 5: Build + run windowed; judge correctness + true clear**

Run: build, launch. Set coverage slider to 0.
Expected (MECHANICAL): coverage=0 → CLEAR sky (no cloud). Raise to 1 → overcast. No shader errors.
Expected (VISUAL GATE — user): clouds look natural near + overhead; coverage knob spans clear→overcast. FLAG for user. (Anti-repeat judged next step in motion.)

- [ ] **Step 6: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_raymarch.glsl scripts/lab/CloudVolume.cs
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T4: HZD-recipe density+lighting, mismatched scales + domain warp, true-clear coverage"
```

---

### Task 5: Mirror the recipe in the shadow shader (keep coupling)

**Files:** Rewrite: `shaders/cloud_shadow.glsl`

- [ ] **Step 1: Copy the EXACT sample_density from cloud_raymarch.glsl**

Replace `cloud_shadow.glsl`'s `sample_density` with a byte-identical copy of the Task-4 version (same consts, same domain warp, same coverage/erosion). They MUST match or shadows desync from the visible cloud. Keep the shadow's curved-shell sun-march (`ro` at `PLANET_R + ground_height`, march along `L`).

- [ ] **Step 2: Build + run; check a cloud's shadow sits under it**

Run: build, launch, fly under a cloud.
Expected (VISUAL GATE — user): shadow roughly beneath its cloud (offset by sun angle). FLAG for user.

- [ ] **Step 3: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_shadow.glsl
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T5: shadow mirrors the new recipe (stays coupled)"
```

---

### Task 6: Half-res + temporal reconstruction (hit the mid-range budget + kill jitter)

**Files:**
- Modify: `scripts/lab/CloudVolume.cs` (history buffer, half-res out texture, reproject params)
- Modify: `shaders/cloud_raymarch.glsl` (write strided subset; reconstruction blend)
- Possibly add: `shaders/cloud_reconstruct.glsl` (a small reproject+blend compute) OR fold into the raymarch.

- [ ] **Step 1: Halve the raymarch output resolution**

In `CloudVolume.InitCompute`, the out texture is `TexW×TexH` (512×128). Add a half-res march target (e.g. keep the lat-long texture but march `stride` texels/frame). Simplest correct path: keep the full lat-long texture but each frame march only `1/N` of texels (the existing `update`/`stride` mechanism) AND **blend with the previous value** instead of overwriting, so the texture is always fully populated (no stale stripes):
```glsl
// in main(), after computing `result` for the marched subset:
vec4 prev = imageLoad(out_tex, px);
float blend = (stride > 1) ? 0.5 : 1.0;   // ease new samples in (temporal smoothing)
imageStore(out_tex, px, mix(prev, result, blend));
```
And REMOVE the early-out `if ((idx % stride) != offset) return;` so every texel updates every frame but only the strided subset does the FULL march; the rest re-blend their previous value cheaply. (Pragmatic reconstruction for a lat-long sky texture — no camera reprojection needed because the texture is direction-parameterized, not screen-space; camera motion is handled by the camera-anchored march origin.)

- [ ] **Step 2: Raise the default temporal stride**

In `CloudParams.Defaults()`, set `TemporalFrames: 8` (was 1). In the raymarch, allow `int stride = clamp(int(P.update.y), 1, 16);` (remove the forced `,1,1` clamp). Now stride 8 = each texel does a full march every 8 frames, blends between → ~8× cheaper march, smooth.

- [ ] **Step 3: Build + run; check FPS + smooth motion (no jitter, no stripes)**

Run: build, launch. Watch the FPS HUD; fly.
Expected (MECHANICAL): cloud cost drops (higher FPS / lower ms vs full-march); no stale stripes; coverage still works.
Expected (VISUAL GATE — user): clouds drift SMOOTHLY (no jitter), no shimmer/stripes. FLAG for user. If ghosting on fast turns, reduce blend toward 0.35 or stride toward 4.

- [ ] **Step 4: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_raymarch.glsl scripts/lab/CloudVolume.cs scripts/lab/CloudParams.cs data/cloud_params.json
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T6: temporal amortization (stride 8 + blend) for mid-range budget, smooth motion"
```

---

### Task 7: God rays — in-scatter in the march + optional screen-space boost

**Files:**
- Modify: `shaders/cloud_raymarch.glsl` (accumulate view-ray in-scatter; expose as a god-ray strength)
- Modify: `shaders/cloud_sky.gdshader` (apply the in-scatter when compositing)
- Optional: a screen-space radial-shaft post pass (only if the user wants the boost after seeing in-scatter)

- [ ] **Step 1: Accumulate sun in-scatter toward the eye in the march**

In the raymarch loop, the `scattered` term already integrates `sunCol * sun * phase`. Strengthen the forward-scatter (sun behind the cloud edge) so crepuscular rays emerge in gaps. Add a `godray_strength` (reuse the `_godraysOn` flag as a 0/1 multiply for now, or a float knob):
```glsl
// forward-scatter boost when looking toward the sun through thin/gap cloud
float fwd = pow(max(dot(rd, L), 0.0), 8.0);   // tight forward lobe
scattered += T * sunCol * fwd * (1.0 - dens) * godray_strength * dt * 0.02;
```
This makes sun streaming through gaps brighten along the view ray — shafts that match the real clouds, no FogVolume.

- [ ] **Step 2: Wire the godray flag/knob through CloudVolume → param buffer**

In `CloudVolume.BuildParams`, pass `_godraysOn ? godrayStrength : 0.0`. Keep `SetGodraysEnabled` (now just sets the flag — no FogVolume). Default OFF.

- [ ] **Step 3: Build + run; toggle god rays, look toward the sun**

Run: build, launch, enable "god rays (live-tune)", aim near the sun through broken cloud.
Expected (VISUAL GATE — user): crepuscular shafts through gaps, no black wedges, scene not darkened. FLAG for user. If the user wants stronger ground-beams, ADD the screen-space radial pass (note: only build on request — STOP clause, don't pre-build).

- [ ] **Step 4: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_raymarch.glsl shaders/cloud_sky.gdshader scripts/lab/CloudVolume.cs
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T7: in-march in-scatter god rays (no FogVolume)"
```

---

### Task 8: Presence rebuild + presets + knobs + judge

**Files:**
- Verify/modify: `scripts/lab/TerrainLabUI.cs` (`UpdateOvercast`, mood→`SetSkyColors`), terrain `light()` shadow sampling (unchanged), reflections (radiance cubemap)
- Modify: `data/cloud_presets.json`, `data/lab_controls.json` (add any new knob like `godray_strength`)

- [ ] **Step 1: Confirm presence still wired to the new field**

`Overcast()` (CPU coverage proxy) → `UpdateOvercast` dims sun/ambient + aerial tint; mood → `SetSkyColors` tints cloud ambient + sky; terrain `light()` samples the shadow map world-XZ (`terrain_lab.gdshader:541`); sky radiance cubemap feeds reflections/GI. Verify each still fires (grep the call sites); fix any that broke when the field changed. Expected: overcast preset dims the scene; mood tints clouds.

- [ ] **Step 2: Re-author the presets across the tunable range**

Rewrite `data/cloud_presets.json` so each is a clear point in the range, leveraging the now-working coverage:
- **Clear:** coverage 0.08 (near-empty blue sky, a few wisps).
- **Scattered:** coverage 0.3, distinct puffs, big gaps.
- **Broken:** coverage 0.55, cover with real holes.
- **Overcast:** coverage 0.85, flat (type 0.15), even grey, thin.
- **Stormy:** coverage 0.65, dark (brightness 0.45), towering (type 0.9), thick, low base, drift ~16.
Include `cloud_drift_speed` (≤18) in each.

- [ ] **Step 3: Add any new knob to the registry**

If `godray_strength` is a new float, add a `{ "id":"cloud_godray_strength", "tab":"Clouds", "type":"cloudf", "cloud":"godray_strength", "min":0, "max":2, "default":1, "rand":false }` row to `data/lab_controls.json` and a `case "godray_strength":` in `CloudVolume.SetKnob`. (Registry gotcha: a `param` to a missing uniform no-ops; cloud knobs use `"type":"cloudf"` + `"cloud":` → `ApplyCloudFloat` → `SetKnob`.)

- [ ] **Step 4: Build + run; full judge across all three looks**

Run: build, launch. Cycle presets; manually push toward photoreal, stylized, sparse.
Expected (VISUAL GATE — user, THE gate): all three looks reachable; repetition gone near→horizon (fly low + look to horizon, AND straight up); true clear works; presets read right; shadow matched; god rays good; presence (dim/aerial/GI/mood) coherent. Judge in MOTION. FLAG for user.

- [ ] **Step 5: Commit**
```bash
git -C /c/Wg16/wg-16-project add data/cloud_presets.json data/lab_controls.json scripts/lab/CloudVolume.cs scripts/lab/TerrainLabUI.cs
git -C /c/Wg16/wg-16-project commit -m "Clouds refactor T8: presence rebuild + tunable-range presets + knobs"
```

---

### Task 9: Docs + memory + handoff

- [ ] **Step 1: DECISIONS.md (newest-first)** — cloud system refactored from scratch (3 patch-passes rejected → look-first rebuild); blend-A+B; anti-repeat 5 defenses; true-clear coverage; in-march god rays (FogVolume removed); temporal amortization for mid-range. Reference the spec + this plan.
- [ ] **Step 2: HANDOFF §6 + ROADMAP** — cloud review-backlog item → "refactored v3, pending review"; note backup tag `backup-clouds-pre-refactor-2026-06-17`.
- [ ] **Step 3: Memory** — update `cloud-shadow-dome-mismatch` + `volumetric-clouds-research`: the slab/shell patches failed on look; the refactor (mismatched-scale noise + domain warp + true-clear remap + temporal stride + in-march god rays) is the resolution.
- [ ] **Step 4: Commit docs.**

---

## Self-Review

- **Spec coverage:** tunable range (T4 recipe + T8 presets) · kill repetition all ranges (T3 tileable octaves + T4 mismatched scales + domain warp + T4 height erosion + wind) · true clear→overcast (T2 field + T4 threshold remap) · mid-range budget (T6 temporal) · keep presence (T8) · god rays no-FogVolume (T1 removal + T7 in-scatter). All spec requirements have a task.
- **Placeholder scan:** all code steps show concrete GLSL/C#; the only deferred item is the OPTIONAL screen-space god-ray boost (explicitly build-on-request per the STOP clause, not a hidden TODO).
- **Type consistency:** `sample_density(p, baseR, topR, windOff)` identical in raymarch (T4) + shadow (T5) — called out as a hard requirement. `SetGodraysEnabled`/`GodraysOn` kept as flag-only after T1, reused in T7. `cloud_godray_strength` knob id + `SetKnob` case added together (T8). `TemporalFrames` raised in T6 matches the stride clamp change.
- **Risk:** full path replacement but behind the clouds toggle + backup tag (T1) → `git checkout` / `git reset --hard backup-clouds-pre-refactor-2026-06-17` reverts. Biggest footgun = the identical-`sample_density` requirement (copy, don't paraphrase) and temporal ghosting (tunable blend/stride in T6). STOP clause honored: 4th attempt, so each visual gate is a real go/no-go, not a twiddle loop.
- **Gate:** no automated "looks good"; mechanical checks + the user's eye in motion per task.
