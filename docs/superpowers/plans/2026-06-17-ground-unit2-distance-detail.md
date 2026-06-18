# Ground Unit 2 — Distance Detail Layering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) tracking.

**Goal:** Give the ground crisp, high-frequency detail up close AND large-scale variation far away by layering a near *detail* albedo+normal (the same material textures resampled at a finer scale, plus a derived high-freq detail-normal) over the existing macro sample, cross-faded by camera distance via the shared `distanceWeight`. This is the single biggest AAA lever for the arc — it fixes "flat up close" and "samey at distance" at the same time, with **zero new assets**.

**Architecture:** Distance detail lives *inside/around* the one material-fetch seam established by Unit 1 — `ar_sample_wp` — never bypassing anti-repetition. The splat path's triplanar helpers (`tp_alb`/`tp_rgh` and the `_nrm` reuse) gain a *near-detail tap*: the same sampler read a second time at a finer world→uv scale (`tex_scale_m / detail_scale`), still through `ar_sample_wp` so it inherits stochastic bombing, blended into the macro result by `(1 - distanceWeight)` shaped by `detail_dist`/`detail_strength`. A high-frequency detail-normal is derived analytically from the detail albedo's luminance gradient (no extra texture fetch, no new asset) and composited onto the macro normal. Everything is additive, behind a `detail_on` toggle, and composes cleanly with Unit 1 (off ⇒ pixel-identical to current).

**Tech Stack:** Godot 4.6 mono, GLSL spatial shader (`shaders/terrain_lab.gdshader`), C# registry + CLI (`scripts/lab/TerrainLab.cs`, `scripts/lab/TerrainLabUI.cs`), control registry (`data/lab_controls.json`).

> **Cross-cutting (all ground units):** (1) **Find the seam functions by NAME/content, not line number** — several units edit the same `fragment()`/`trip_*`/`tp_*` region, so a prior-built unit may have shifted the lines; the anchors here are approximate. (2) **Registry-wiring gotcha:** a `lab_controls.json` row with `"type":"slider"`/`"toggle"` + `"param":"X"` auto-routes to `SetFloat/SetBool("X")` → `SetShaderParameter`, which **silently no-ops if uniform `X` doesn't exist**. Every `param` here must name a uniform actually added in the shader edits (verified below). (Units that need C#-side state, not a shader uniform, must use the `field`/`setter`/`cloud` mechanisms + a C# case — NOT `param`.)

**Verification model:** GPU/visual, no unit harness (there is no test harness; this is a GPU/visual subsystem — do NOT write unit tests / TDD). Per task gate: `dotnet build WG16.csproj` → headless `--import` (GLSL compiles) → `--auto-shot` A/B (`--detail=0` vs `--detail=1`) at three ranges → `--profile` (ms cost). **THE gate is the user flying it live at close / mid / far** — the badness ("flat up close", "samey far") is range-spanning, so all three must improve. Commit per task. Never judge a motion artifact from a still.

---

## Environment (Godot 4.6 gotchas — respect all of these)

- **Project root:** `C:\Wg16\wg-16-project`. Scene: `scenes/terrain_lab.tscn`. Camera node path: `/root/TerrainLabRoot/Camera`.
- **ONE Godot process at a time.** Before any relaunch: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found").
- **ALWAYS pass `--rendering-driver vulkan`** on every launch.
- **Console exe (for `--import` / headless):**
  `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe`
  **Windowed exe (for live runs / auto-shot / profile):** same folder, the binary **without** `_console`.
- **Build/import order after ANY `.cs` or `.gdshader` change:** `dotnet build WG16.csproj` → headless `--import` (catches GLSL parse errors) → only then launch windowed.
- **local-RD compute CANNOT run under `--headless`.** This unit adds NO new compute (see "Why no compute" below); if a future detail-variation bake is added, it must run **windowed**.
- **Per-frame compute→material binding** (not used here, stated for arc consistency): use `RenderingServer.CallOnRenderThread` + assign the `Texture2Drd` RID **once**, NOT a `CompositorEffect`.
- **Never debug a motion artifact (shimmer/swimming/crawling) from a still** — fly it. Stills only confirm gross presence/absence, not temporal stability.
- **Lab CLIs:** `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--profile[=secs]`, `--auto-shot=<abs.png>`, plus the existing toggles `--ar=0/1`, `--splat=0/1`, `--blend=`, `--tile=`. This unit adds `--detail=0/1` (mirrors `--ar=`).
- **Controls are data-driven** in `data/lab_controls.json` (this unit's knobs go on the existing **Detail** tab). A `slider`/`toggle` row with a `"param"` maps straight to a shader uniform with no C# change.

**Why no GPU compute in Unit 2 (justified per the tech focus):** distance detail is a pure per-fragment operation — it needs the live camera distance and the existing material samplers, both already in the fragment shader. Baking a detail map would (a) add assets/VRAM we explicitly want to avoid, (b) still need per-fragment distance crossfade anyway, and (c) can't beat re-tapping an already-resident, mip-correct texture at a finer scale. Compute earns its place in **Unit 4** (breakup masks baked once), not here. Keep Unit 2 per-frame in the shader.

**Key shader facts (verified against current `terrain_lab.gdshader`):**
- Default render path is the **splat path** (`splat_on=true`): `fragment()` (~459) → `trip_alb_by`/`trip_nrm_by`/`trip_rgh_by` (~433–449) → `tp_alb`/`tp_rgh` (~419–431), which already call `ar_sample_wp`. `trip_nrm_by` reuses `tp_alb` on the `_nrm` sampler.
- **`ar_sample_wp(sampler2D tex, vec2 uv, vec3 wp) -> vec4`** (~243) is the ONE material-fetch entry; anti-repetition lives inside it. Distance detail MUST go through it, never around.
- **`distanceWeight(vec3 wp) -> float`** (~231): `0` near .. `1` far, `clamp(d/ar_far_m,0,1)`. Shared LOD factor; the detail crossfade is driven by `1 - distanceWeight` shaped by knobs.
- **`cam_world`** uniform (~42), pushed every frame from `TerrainLabUI._Process` via `_terrain.SetCameraWorld(...)` (already live for Unit 1).
- **`tex_scale_m`** (~36) is the world→uv scale (`wp.* / tex_scale_m`). The detail tap divides this further by `detail_scale` (finer ⇒ higher freq).
- `tri_w(nr)` gives triplanar blend weights. `s_alb/s_nrm/s_rgh` (the `tiled()` non-splat path, ~269–288) are **legacy A/B** — leave them untouched; this unit only touches the `tp_*` splat path.

---

## Task 1: Add distance-detail uniforms (the knob set)

**Files:** Modify `shaders/terrain_lab.gdshader`.

- [ ] **Step 1: Add the uniform block** immediately after the Unit-1 anti-repetition block (after the `cam_world` uniform, ~line 42):

```glsl
// --- Distance detail layering (Unit 2) --------------------------------------
// Near detail = the SAME material textures resampled at a finer world->uv scale
// (tex_scale_m / detail_scale), still through ar_sample_wp (inherits Unit-1
// bombing), cross-faded into the macro sample by (1 - distanceWeight) shaped by
// detail_dist. A high-freq detail-normal is DERIVED from the detail albedo
// luminance gradient (no new asset/fetch). Off => pixel-identical to current.
uniform bool detail_on = true;            // master toggle for distance detail
uniform float detail_strength : hint_range(0.0, 1.0) = 0.6;  // near detail blend amount
uniform float detail_scale : hint_range(2.0, 16.0) = 6.0;    // finer = tex_scale_m / this
uniform float detail_dist : hint_range(0.05, 1.0) = 0.1;     // fraction of ar_far_m over which detail fades out (0.1*2500 ≈ 250m near band; "near" detail should be genuinely near)
uniform float detail_nrm_amp : hint_range(0.0, 2.0) = 0.8;   // derived detail-normal strength
```

- [ ] **Step 2: Build + import.**
  Run: `dotnet build WG16.csproj` then `"<console exe>" --headless --path . --import`
  Expected: 0 errors; no `terrain_lab.gdshader` parse error reported by `--import`.

- [ ] **Step 3: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 2: distance-detail uniform set (detail_on/strength/scale/dist/nrm_amp)"
```

---

## Task 2: Add the detail-weight helper + a detail-aware albedo sampler

**Files:** Modify `shaders/terrain_lab.gdshader`.

The detail crossfade weight is a near-biased shaping of the shared `distanceWeight`: full near, gone by `detail_dist * ar_far_m`. Adding it as a named helper keeps the seam explicit and reusable (Unit 3 parallax will gate off the same near band).

- [ ] **Step 1: Add `detailWeight()`** right after `distanceWeight()` (~line 234), so it sits with the shared LOD factor:

```glsl
// Near-detail blend factor: 1 at the camera, 0 by detail_dist*ar_far_m. Shares
// distanceWeight's normalization so the detail band scales with ar_far_m.
float detailWeight(vec3 wp){
    float far = distanceWeight(wp);                 // 0 near .. 1 far
    float k = 1.0 - smoothstep(0.0, max(detail_dist, 1e-3), far);
    return clamp(k, 0.0, 1.0);
}
```

- [ ] **Step 2: Add `detail_alb()`** — a macro+near-detail albedo fetch that goes THROUGH `ar_sample_wp` at both scales — placed right after `tp_alb` (~line 425). It returns the composited albedo for one sampler:

```glsl
// Macro (existing scale) (+) near detail (finer scale), both via ar_sample_wp so
// each inherits Unit-1 bombing. The detail is added as the detail tap's deviation
// from ITS OWN luma (not a constant mid-grey) — so it layers high-freq contrast
// WITHOUT shifting the macro's brightness regardless of how dark/light the material
// is (audit M1: `det - 0.5` lifts dark textures / crushes light ones; subtracting
// the detail's own luma is the correct value-preserving form). Crossfaded by detailWeight.
vec3 detail_alb(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw = tri_w(nr);
    vec2 ux = wp.zy/tex_scale_m, uy = wp.xz/tex_scale_m, uz = wp.xy/tex_scale_m;
    vec3 macro = ar_sample_wp(t,ux,wp).rgb*bw.x
               + ar_sample_wp(t,uy,wp).rgb*bw.y
               + ar_sample_wp(t,uz,wp).rgb*bw.z;
    if (!detail_on) return macro;
    float dw = detailWeight(wp);
    if (dw < 0.001) return macro;                   // far: skip the extra taps
    float ds = tex_scale_m / max(detail_scale, 1e-3);  // finer world->uv scale
    vec2 dux = wp.zy/ds, duy = wp.xz/ds, duz = wp.xy/ds;
    vec3 det = ar_sample_wp(t,dux,wp).rgb*bw.x
             + ar_sample_wp(t,duy,wp).rgb*bw.y
             + ar_sample_wp(t,duz,wp).rgb*bw.z;
    // value-preserving: add detail's deviation from its OWN luma (mean stays = macro)
    float detLuma = dot(det, vec3(0.299, 0.587, 0.114));
    vec3 over = macro + (det - vec3(detLuma)) * (detail_strength * dw);
    return clamp(over, 0.0, 1.0);
}
```

- [ ] **Step 3: Build + import.** `dotnet build WG16.csproj` → `"<console exe>" --headless --path . --import`.
  Expected: 0 errors (`tri_w`, `ar_sample_wp`, `tex_scale_m`, `distanceWeight` all defined above this point — no forward reference).

- [ ] **Step 4: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 2: detailWeight() + detail_alb() (near-detail overlay through ar_sample_wp)"
```

---

## Task 3: Derived high-freq detail-normal + detail-aware normal sampler

**Files:** Modify `shaders/terrain_lab.gdshader`.

No-new-asset detail normal: derive a tangent-space high-freq bump from the **detail albedo's luminance gradient**, but compute that gradient from **explicit neighbouring texture taps** (finite differences via `textureGrad`), **NOT** from `dFdx`/`dFdy` of an `ar_sample_wp` result. (Audit H1: `ar_sample_wp` does per-tile rotation/jitter, so its output luminance is *discontinuous at tile-cell boundaries*; taking screen-space derivatives of it spikes at those seams → grid-aligned crawl in motion — the exact artifact we fear. Plain offset taps of the texture have no such seams.) The real material normal map still drives the macro normal via the existing path; the derived bump is added on top, near only.

The macro normal must also match the current path's behaviour **exactly** when detail is off. The current path is `tp_alb(z*_nrm,…)` → raw `ar_sample_wp(...).rgb` (0..1) → caller does `*2-1`, and the fragment only consumes `nrm.x`/`nrm.y` (`vec3 wn=normalize(nr + vec3(nrm.x,0.0,nrm.y)*0.8);` — `nrm.z` is discarded). So: **do NOT `normalize()`** (that shrinks `.xy` and would change the look even with detail off, breaking the off==current guarantee); just add the bump to the raw macro normal's `.xy` and pass `.z` through unchanged.

- [ ] **Step 1: Add `detail_nrm()`** right after `detail_alb()` (~line 425+). It returns the tangent-space normal (`-1..1`) — the macro normal-map sample with a derived high-freq bump added to `.xy`, near only:

```glsl
// Macro normal-map (-1..1) + a derived high-freq bump near the camera. The bump's
// slope = luminance gradient of the finer albedo tap, taken from EXPLICIT offset
// taps (textureGrad), NOT dFdx of an ar_sample_wp result — the latter crawls at
// the bombing's tile seams (audit H1). Un-normalized: matches the existing raw
// path so detail-off == current, and fragment() only uses .xy anyway.
vec3 detail_nrm(sampler2D albT, sampler2D nrmT, vec3 wp, vec3 nr){
    vec3 bw = tri_w(nr);
    vec2 ux = wp.zy/tex_scale_m, uy = wp.xz/tex_scale_m, uz = wp.xy/tex_scale_m;
    // macro normal exactly as the current trip_nrm_by path (raw, *2-1, NOT normalized)
    vec3 macroN = (ar_sample_wp(nrmT,ux,wp).rgb*bw.x
                 + ar_sample_wp(nrmT,uy,wp).rgb*bw.y
                 + ar_sample_wp(nrmT,uz,wp).rgb*bw.z) * 2.0 - 1.0;
    if (!detail_on) return macroN;
    float dw = detailWeight(wp);
    if (dw < 0.001) return macroN;
    float ds = tex_scale_m / max(detail_scale, 1e-3);
    // dominant triplanar plane drives the bump (cheap, avoids 3x derivative work)
    vec2 duv = (bw.x >= bw.y && bw.x >= bw.z) ? wp.zy/ds
             : (bw.y >= bw.z)                 ? wp.xz/ds
                                              : wp.xy/ds;
    // luminance gradient via EXPLICIT finite differences (seam-free, unlike dFdx of
    // the bombed tap). One texel-ish offset at the detail scale; plain textureGrad.
    vec2 dx = dFdx(duv), dy = dFdy(duv);
    float e = 1.0 / 512.0;                      // small uv offset for the difference
    float lC = dot(textureGrad(albT, duv,                dx, dy).rgb, vec3(0.299,0.587,0.114));
    float lX = dot(textureGrad(albT, duv + vec2(e,0.0),  dx, dy).rgb, vec3(0.299,0.587,0.114));
    float lY = dot(textureGrad(albT, duv + vec2(0.0,e),  dx, dy).rgb, vec3(0.299,0.587,0.114));
    vec2 g = vec2(lX - lC, lY - lC) / e;        // surface luminance slope
    // add the bump's xy slope to the macro normal; keep macro .z (fragment uses .xy)
    return vec3(macroN.xy - g * detail_nrm_amp * dw, macroN.z);
}
```

- [ ] **Step 2: Build + import.** Expected: 0 errors.

- [ ] **Step 3: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 2: detail_nrm() derived high-freq normal (luminance-gradient, no asset)"
```

---

## Task 4: Route the splat path through the detail samplers

**Files:** Modify `shaders/terrain_lab.gdshader`.

This is the change that makes detail visible. The splat path resolves albedo/normal via `trip_alb_by`/`trip_nrm_by`, which dispatch per-zone to `tp_alb`. We point the albedo/normal dispatch at the new detail-aware fetches. Roughness stays on `tp_rgh` (no detail roughness this unit — keep cost down; roughness detail is Unit 3/4 territory).

- [ ] **Step 1: Re-point `trip_alb_by`** (~line 433) to `detail_alb` (signature matches `tp_alb`: `(sampler2D, vec3, vec3)`):

```glsl
vec3 trip_alb_by(int z, vec3 wp, vec3 nr){
    if(z==0) return detail_alb(z0_alb,wp,nr); if(z==1) return detail_alb(z1_alb,wp,nr);
    if(z==2) return detail_alb(z2_alb,wp,nr); if(z==3) return detail_alb(z3_alb,wp,nr);
    if(z==4) return detail_alb(z4_alb,wp,nr); if(z==5) return detail_alb(z5_alb,wp,nr);
    return detail_alb(z6_alb,wp,nr);
}
```

- [ ] **Step 2: Re-point `trip_nrm_by`** (~line 439) to `detail_nrm`. NOTE the current code returns `tp_alb(z*_nrm,...)` (raw 0..1, the caller does `*2-1` at line ~488). `detail_nrm` returns the already `-1..1` tangent normal, so it must be re-encoded to 0..1 here to keep the caller's `*2-1` valid:

```glsl
vec3 trip_nrm_by(int z, vec3 wp, vec3 nr){
    // detail_nrm returns -1..1; re-encode to 0..1 so fragment()'s *2-1 is correct.
    if(z==0) return detail_nrm(z0_alb,z0_nrm,wp,nr)*0.5+0.5; if(z==1) return detail_nrm(z1_alb,z1_nrm,wp,nr)*0.5+0.5;
    if(z==2) return detail_nrm(z2_alb,z2_nrm,wp,nr)*0.5+0.5; if(z==3) return detail_nrm(z3_alb,z3_nrm,wp,nr)*0.5+0.5;
    if(z==4) return detail_nrm(z4_alb,z4_nrm,wp,nr)*0.5+0.5; if(z==5) return detail_nrm(z5_alb,z5_nrm,wp,nr)*0.5+0.5;
    return detail_nrm(z6_alb,z6_nrm,wp,nr)*0.5+0.5;
}
```

- [ ] **Step 3:** Leave `trip_rgh_by`/`tp_rgh` UNCHANGED (no detail roughness this unit). Confirm `fragment()`'s normal decode at ~line 488 (`nrm = mix(trip_nrm_by(dom,..), trip_nrm_by(sec,..), m)*2.0-1.0;`) is untouched — the `*0.5+0.5` in Step 2 makes the round-trip identity, so the math stays correct.

- [ ] **Step 4: Build + import + A/B captures (clouds off, close range, the `--detail=` flag from Task 6 not yet wired — so A/B via the toggle default).**
  For this task, A/B by temporarily flipping the `detail_on` default in the shader (or wait for Task 6's `--detail=` flag). Minimum capture:
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0" "--clouds=0" "--auto-shot=C:/tmp/u2_close_on.png"
```
  Expected: the close shot shows visibly MORE fine surface detail vs the Unit-1 baseline; overall brightness unchanged (overlay is value-preserving); no swimming yet to confirm (still). Read the PNG to confirm detail present and no obvious banding/blow-out.

- [ ] **Step 5: Commit.**
```bash
git add shaders/terrain_lab.gdshader
git commit -m "Ground unit 2: route splat trip_alb_by/trip_nrm_by through detail samplers"
```

---

## Task 5: Expose toggle + knobs in the registry (Detail tab)

**Files:** Modify `data/lab_controls.json`.

- [ ] **Step 1: Add the rows** to the **Detail** tab (the contact-shade group already lives there). Insert after the `contact_on` row (the existing Detail block):

```json
    { "id": "detail_on", "label": "distance detail", "tab": "Detail", "type": "toggle",
      "param": "detail_on", "default": true, "rand": false },
    { "id": "detail_strength", "label": "detail amt", "tab": "Detail", "type": "slider",
      "param": "detail_strength", "min": 0, "max": 1, "default": 0.6, "rand": true },
    { "id": "detail_scale", "label": "detail scale", "tab": "Detail", "type": "slider",
      "param": "detail_scale", "min": 2, "max": 16, "default": 6, "rand": true },
    { "id": "detail_dist", "label": "detail fade dist", "tab": "Detail", "type": "slider",
      "param": "detail_dist", "min": 0.05, "max": 1, "default": 0.1, "rand": false },
    { "id": "detail_nrm_amp", "label": "detail normal", "tab": "Detail", "type": "slider",
      "param": "detail_nrm_amp", "min": 0, "max": 2, "default": 0.8, "rand": true },
```

- [ ] **Step 2: Validate JSON.**
  Run: `python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"`
  Expected: `ok`.

- [ ] **Step 3: Build + launch windowed; confirm the five Detail-tab widgets appear and move live** (the registry builds the UI; `param` rows need no C#). Kill strays first.
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```
  Expected: Detail tab shows `distance detail` / `detail amt` / `detail scale` / `detail fade dist` / `detail normal`; toggling `distance detail` visibly changes the ground close up.

- [ ] **Step 4: Commit.**
```bash
git add data/lab_controls.json
git commit -m "Ground unit 2: distance-detail toggle + 4 knobs (Detail tab)"
```

---

## Task 6: `--detail=0/1` CLI override (A/B harness) + profile

**Files:** Modify `scripts/lab/TerrainLabUI.cs`.

Mirror the existing `--ar=` override exactly so `--auto-shot` A/B captures can flip detail without editing defaults.

- [ ] **Step 1: Add the field** beside `_terrainArCli` (~line 130):
```csharp
    private int _terrainArCli = -1;
    private int _terrainDetailCli = -1;
```

- [ ] **Step 2: Parse the flag** in the cmdline loop, right after the `--ar=` line (~line 165):
```csharp
            else if (a.StartsWith("--detail=")) { _terrainDetailCli = a.Substring("--detail=".Length) == "1" ? 1 : 0; }
```

- [ ] **Step 3: Apply it** in `ApplyCliOverrides()`, right after the `_terrainArCli` apply (~line 1024):
```csharp
            if (_terrainDetailCli >= 0) { _terrain.SetBool("detail_on", _terrainDetailCli == 1); }
```

- [ ] **Step 4: Build + import + full A/B + profile at three ranges.**
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<console exe>" --headless --path . --import
# A/B at three ranges, clouds off (isolate ground):
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0"    "--clouds=0" "--detail=0" "--auto-shot=C:/tmp/u2_close_off.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0"    "--clouds=0" "--detail=1" "--auto-shot=C:/tmp/u2_close_on.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,150,500,-25,0"  "--clouds=0" "--detail=1" "--auto-shot=C:/tmp/u2_mid_on.png"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,400,1800,-22,0" "--clouds=0" "--detail=1" "--auto-shot=C:/tmp/u2_far_on.png"
# Profile cost of the extra near taps:
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0" "--clouds=0" "--detail=0" "--profile=3"
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0" "--clouds=0" "--detail=1" "--profile=3"
```
  Expected:
  - `u2_close_off.png` vs `u2_close_on.png`: ON has visibly finer/crisper surface detail; same overall brightness.
  - `u2_far_on.png`: indistinguishable from detail-off far (detail faded out by `detail_dist`) — confirms the near taps are skipped at distance (no far cost, no far swimming).
  - `--profile` delta: detail-on costs more ONLY near (extra `ar_sample_wp` taps on the near band); record the close-range ms delta. Levers if heavy: `detail_dist` down (shrinks the near band), `detail_scale`, or `detail_strength`.

- [ ] **Step 5: Commit.**
```bash
git add scripts/lab/TerrainLabUI.cs
git commit -m "Ground unit 2: --detail=0/1 CLI override (A/B harness) mirroring --ar="
```

---

## Task 7: Live judging handoff (THE gate)

- [ ] **Step 1:** Kill strays, build, launch windowed:
```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed exe>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn
```
- [ ] **Step 2: User judges in MOTION at three ranges (close / mid / far).** Questions: (a) Close — does the ground now read as having real micro-detail instead of flat? (b) Far — does large-scale variation survive (macro intact, detail gone)? (c) The crossfade band (mid) — is there any visible "pop"/seam where detail fades, or any **swimming/crawling** of the derived normal in motion? Toggle `distance detail` (Detail tab) on/off to A/B; sweep `detail amt`, `detail scale`, `detail fade dist`, `detail normal`. Confirm it composes with anti-repeat (toggle `anti-repeat` too — both on should look best). Watch the FPS HUD.
- [ ] **Step 3:** On approval → proceed to Unit 3 (surface depth / POM) plan. If the derived normal swims or the fade pops: it is isolated to `detail_nrm`/`detailWeight` — soften the `smoothstep` band in `detailWeight`, lower `detail_nrm_amp`, or clamp the gradient. If detail looks like flat contrast-boost (no relief), raise `detail_nrm_amp` / lower `detail_scale`. Document the upgrade path (below) if a dedicated detail-normal is wanted.

---

## Upgrade path (noted, NOT built now — per pillars, asset-free first)

The no-new-asset approach (resample existing material finer + derive a normal from its luminance gradient) is the recommended first build: zero VRAM, zero authoring, composes with anti-repetition for free. If live judging shows the derived normal isn't convincing enough (lacks true high-freq relief that a hand-authored detail map has), the clean upgrade is: add ONE shared tiling **detail-normal** sampler uniform (e.g. `detail_nrm_tex`, a generic stone/grain normal), sampled in `detail_nrm()` via `ar_sample_wp` at the finer scale and blended by `detail_nrm_amp * detailWeight` — a localized change to `detail_nrm()` only, with a `TerrainLab.SetTexture("detail_nrm_tex", ...)` bind and one registry `material`-style row. The crossfade, weight helper, and routing all stay identical. Per-zone detail maps are a further (heavier) step, deferred unless judged necessary.

---

## Self-Review notes

- **Spec coverage:** Implements arc-spec Unit 2 (DISTANCE DETAIL LAYERING): near detail albedo (`detail_alb`) ⊕ far macro, cross-faded by the shared `distanceWeight` (via `detailWeight`), plus a derived high-freq detail-normal (`detail_nrm`) — fixing both "flat up close" and "samey at distance". Asset-free per pillars; dedicated-detail-texture upgrade path documented. Detail layering sits INSIDE the `ar_sample_wp` seam (every detail tap goes through it), so it composes with Unit 1 and does not bypass anti-repetition. `groundData` (Unit 4) and POM (Unit 3) seams untouched. Knob set: `detail_strength` + `detail_scale` + `detail_dist` + `detail_nrm_amp` + `detail_on`, all live on the Detail tab. Build order respected (Unit 2 after Unit 1).
- **Placeholder scan:** No TODOs / "add appropriate X" / stubs. Every shader and C# step is concrete, copy-pasteable code. No new compute added (justified in Environment). No new assets.
- **Type / interface consistency with the locked seams:**
  - `ar_sample_wp(sampler2D, vec2, vec3) -> vec4` — called identically in `detail_alb` and `detail_nrm` (Tasks 2–3); never bypassed.
  - `distanceWeight(vec3) -> float` — consumed by `detailWeight(vec3) -> float` (Task 2); not redefined.
  - `detail_alb(sampler2D, vec3, vec3) -> vec3` matches `tp_alb`'s signature, so `trip_alb_by` re-points with no caller change (Task 4 Step 1).
  - `detail_nrm(sampler2D albT, sampler2D nrmT, vec3, vec3) -> vec3` returns raw `-1..1` (UN-normalized, matching the existing path — audit H2); Task 4 Step 2 re-encodes `*0.5+0.5` so `fragment()`'s existing `*2.0-1.0` (line ~488) round-trips. Detail-off returns exactly `macroN` ⇒ pixel-identical to current (the normalize() that broke this is removed).
  - **Audit fixes applied:** H1 — detail-normal gradient now from explicit `textureGrad` offset taps, not `dFdx` of a bombed `ar_sample_wp` (no tile-seam crawl). H2 — dropped `normalize()`; off==current preserved. M1 — value-preserving overlay subtracts the detail tap's OWN luma (not constant 0.5), so dark/light materials don't shift brightness. M2 — `detail_dist` default 0.45→0.1 (≈250m near band, genuinely near).
  - `cam_world` uniform reused (no new camera plumbing); already pushed each frame by `TerrainLabUI._Process` (Unit 1).
  - Registry `param` ids (`detail_on`/`detail_strength`/`detail_scale`/`detail_dist`/`detail_nrm_amp`) match the shader uniform names exactly; `--detail=` mirrors `--ar=` (field + parse + apply), verified against the existing `_terrainArCli` pattern.
- **Forward-reference check:** `detailWeight` defined after `distanceWeight` (~234); `detail_alb`/`detail_nrm` defined after `tp_alb` (~425) and use `tri_w`/`ar_sample_wp`/`tex_scale_m` all defined above — no forward references. `trip_alb_by`/`trip_nrm_by` (~433–443) sit below the detail samplers, so re-pointing them compiles.
- **Composition / off-path safety:** `detail_on=false` ⇒ `detail_alb` returns exactly the macro (same as old `tp_alb`) and `detail_nrm` returns exactly `macroN` ⇒ pixel-identical to current. Far away (`detailWeight < 0.001`) the extra taps are skipped ⇒ no far-range cost or swimming. Roughness path left unchanged.
