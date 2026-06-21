# GPU Atmosphere AT-3 (Cloud-Lighting Integration) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended for this coupled GPU-seam + look-sensitive shader work, gated live by the user) or superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax. Spec: `specs/2026-06-21-gpu-atmosphere-at3-cloud-lighting-design.md`.

**Goal:** Light the volumetric clouds with the physical atmosphere LUTs — directional sky-view ambient (warm undersides / cool tops at sunset) + sun-transmittance reddening — sampled inside the cloud raymarch, behind a default-off toggle gated by the user's live eye.

**Architecture:** `AtmosphereCompute` already builds sky-view + transmittance LUTs on the shared render-thread RD. Expose their RIDs; `CloudVolume` binds them into the cloud raymarch compute uniform set (raymarch-only bindings 5/6; the shadow set is untouched) and carries an `atmo cloud-light strength` in the otherwise-unused `sun_color.a` param slot (0 = off → today's mood path, byte-identical; >0 = physical path at that gain). The raymarch branches its ambient + sun terms on `sun_color.a > 0`. A one-time uniform-set rebuild handshake binds the real LUT RIDs once the atmosphere is live; a `_Process` readiness gate pushes the RIDs + strength only when both nodes are ready.

**Tech Stack:** Godot 4.6 mono (C# + GLSL compute via `RenderingDevice`), the shared render-thread RD seam, `Std430Writer`.

> **BUILT 2026-06-21 — AS-BUILT DIFFERS FROM APPROACH A (see DECISIONS 2026-06-21).** Approach A (sampling the
> atmosphere LUTs *inside* the cloud raymarch, T1 Steps 2/3/5) **hard-crashes the render device** — CloudVolume's
> compute cannot safely sample AtmosphereCompute's textures cross-node (isolated empirically; the seam-law hazard).
> The 3 values needed (zenith sky, horizon-toward-sun sky, sun transmittance) are sun-dependent **per-frame
> constants**, so the as-built **reads them back on the CPU** in `AtmosphereCompute.RecomputeAll` and pushes them as
> 3 param `vec4`s appended to the raymarch `ParamsBuf` (after `layers[48]`); the shader uses `P.atmo_zenith/horizon/
> suntrans` instead of `texture(atmo_skyview/transmittance, …)`. No LUT bindings (5/6), no uniform-set rebuild
> handshake. Visually identical (A only sampled 2 fixed dirs + 1 transmittance per ray). Steps below are kept as the
> historical plan; the param-color path is the shipped one. Default-ON (gate PASSED). Commits abdaa0e + 6d0d590.

## Global Constraints

- **Pillars:** quality = AAA = long-term-best. User chose approach A (sample LUTs in the raymarch) over the cheaper C# color-handoff, for the directional-ambient win.
- **Default OFF** behind `physical cloud light (AT-3)` / `--cloudlight=1` until the live eye-gate passes. OFF path byte-identical to today's mood cloud lighting (the shader branch falls through to the existing code when `sun_color.a == 0`).
- **No double-count:** when on, the cloud uses the LUTs INSTEAD of the mood `sky_top`/`sky_horizon`/`sun_color.rgb` (handoff law, like AT-1/AT-2).
- **Seam law:** both nodes use `RenderingServer.GetRenderingDevice()` (verified). The atmosphere LUT RIDs are bound into the cloud raymarch set; an `atmo_light_on` readiness gate + placeholder binding avoid sampling an empty RID on frame 1.
- **Don't edit** `terrain_lab.gdshader` / `TerrainLab.cs` / `GodRays*` / `shaders/godray*`. **Keep the cloud shaders' shared param block byte-identical** — this plan changes NO shared field (it reuses `sun_color.a` and adds raymarch-only sampler bindings), so `cloud_shadow.glsl` / `cloud_shadow_check.glsl` are NOT touched.
- **No-TDD (GPU/visual):** per-task test = build → windowed `--import` → `--auto-shot` A/B + `--profmove` → the user's live eye. Never judge from a still.
- **Run (one Godot at a time; kill strays `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`):** `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`. Build `dotnet build WG16.csproj` from the project dir.
- **Coordination:** `git add` your paths explicitly, never `-A`. Commit-by-default; push only when asked.

## Reference points (read before building)

- `scripts/lab/AtmosphereCompute.cs` — `_skyTex` / `_transTex` (private `Rid`s, created in `InitCompute`); `_ready`.
- `shaders/cloud_raymarch.glsl` — bindings 0-4 (lines 14-19), `ParamsBuf` (19-47), the lighting block (318-364): `sunCol` (321), `skyAmbient` (323), `amb` (354), final `lum` (362).
- `shaders/cloud_sky.gdshader:90-96` — `atmo_dir_to_uv` (the LUT az/sqrt-el mapping) to copy verbatim.
- `scripts/lab/CloudVolume.cs` — `EnsureUniformSets` (276-293, raymarch set at 284), `BuildParams` (436-459, `sun_color` at 441), `SetSun` (465), `_computeReady`, `RenderProcess` (295).
- `scripts/lab/TerrainLabUI.cs` `AttachClouds` + `.Process.cs` readiness gates (AT-1 `_atmoMatActivated`, AT-2 `_aerialActivated`) + `.Clouds.cs` `ApplyCloudBool`/`ApplyCloudFloat` + `.Cli.cs`.

---

## Task 1 (AT3-1): Plumbing — expose LUTs, bind them, branch the raymarch (NO visual change)

Wires the LUTs into the raymarch behind `sun_color.a` (default 0 = off), so the scene is byte-identical to today. Deliverable: builds + runs, scene unchanged, `--cloudlight` path compiles; flipping a temporary strength shows the physical path exists.

**Files:**
- Modify: `scripts/lab/AtmosphereCompute.cs` (expose RIDs)
- Modify: `shaders/cloud_raymarch.glsl` (bindings 5/6 + `atmo_dir_to_uv` + branched lighting)
- Modify: `scripts/lab/CloudVolume.cs` (fields, setters, binding, param slot)

**Interfaces:**
- Produces: `AtmosphereCompute.SkyViewTexRid` (`Rid`), `AtmosphereCompute.TransTexRid` (`Rid`), `AtmosphereCompute.ReadyForCloudLight` (`bool`); `CloudVolume.SetAtmosphereLuts(Rid skyView, Rid trans)`, `CloudVolume.SetCloudAtmoLight(float strength)`.

- [ ] **Step 1: AtmosphereCompute — expose the LUT RIDs + readiness**

In `scripts/lab/AtmosphereCompute.cs`, add near the other public accessors (`AerialTexture`/`AerialReady`):
```csharp
    // AT-3: the cloud raymarch (CloudVolume, same render-thread RD) binds these LUT RIDs to light clouds physically.
    public Rid SkyViewTexRid => _skyTex;
    public Rid TransTexRid => _transTex;
    public bool ReadyForCloudLight => _ready;   // _skyTex/_transTex exist after InitCompute
```

- [ ] **Step 2: cloud_raymarch.glsl — add the LUT sampler bindings + the UV mapping**

After binding 4 (the param buffer, line 47 `} P;`), add the two raymarch-only sampler bindings (the shadow shaders do NOT get these):
```glsl
// AT-3 physical cloud lighting: the atmosphere LUTs (same render-thread RD). Sampled only when
// P.sun_color.a > 0 (the atmo cloud-light strength); a == 0 → the mood path below, unchanged.
layout(set = 0, binding = 5) uniform sampler2D atmo_skyview;        // Hillaire sky-view radiance (az, sqrt-el)
layout(set = 0, binding = 6) uniform sampler2D atmo_transmittance;  // sun transmittance (cosSunZenith, altitude)
```
Then add the LUT UV mapping (copy VERBATIM from `cloud_sky.gdshader:92-96`; `PI` is already defined at line 57). Place it after `remap` (line 59):
```glsl
// AT-3: MUST match atmosphere_skyview.glsl's per-texel ray build (az = u*2π, el = v²·π/2) — copied
// verbatim from cloud_sky.gdshader atmo_dir_to_uv. Keep in sync if either changes.
vec2 atmo_dir_to_uv(vec3 d){
    float az = atan(d.z, d.x); if (az < 0.0) az += 2.0 * PI;
    float el = asin(clamp(d.y, 0.0, 1.0));
    return vec2(az / (2.0 * PI), sqrt(el / (0.5 * PI)));
}
```

- [ ] **Step 3: cloud_raymarch.glsl — branch the sun + ambient terms on the atmo strength**

The atmo strength is `P.sun_color.a` (0 = off). Compute the physical inputs ONCE per ray (cheap), then use them per-sample. Replace the existing lines 318-323:
```glsl
        vec3 L = normalize(P.sun_dir.xyz);
        float cosA = dot(rd, L);
        float globalPhase = phase_dual(cosA);   // legacy uniform phase (P.perdeck blends away from it)
        vec3 sunCol = P.sun_color.rgb * P.sun_dir.w;
        // Mood sky fill; modulated per-voxel by height (base-occluded) below.
        vec3 skyAmbient = mix(P.sky_horizon.rgb, P.sky_top.rgb, clamp(rd.y, 0.0, 1.0));
```
with:
```glsl
        vec3 L = normalize(P.sun_dir.xyz);
        float cosA = dot(rd, L);
        float globalPhase = phase_dual(cosA);   // legacy uniform phase (P.perdeck blends away from it)

        // AT-3: physical cloud lighting when the atmo cloud-light strength (sun_color.a) > 0. Sun color =
        // neutral star × atmospheric transmittance toward the sun (Rayleigh reddening); ambient colors come
        // from the sky-view LUT at the zenith (top fill) and the horizon-toward-the-sun (underside fill).
        // a == 0 → the mood path (unchanged). Computed once per ray; the per-sample blend is below.
        float atmoStr = P.sun_color.a;
        vec3 sunCol = P.sun_color.rgb * P.sun_dir.w;        // mood sun (a == 0 path)
        vec3 skyTopC = P.sky_top.rgb, skyHorC = P.sky_horizon.rgb;   // mood ambient endpoints (a == 0 path)
        if (atmoStr > 0.0){
            vec3 sunTr = texture(atmo_transmittance, vec2(clamp(0.5 + 0.5 * L.y, 0.0, 1.0), 0.02)).rgb;
            sunCol = sunTr * P.sun_dir.w;                   // neutral white × transmittance × energy
            skyTopC = texture(atmo_skyview, atmo_dir_to_uv(vec3(0.0, 1.0, 0.0))).rgb * atmoStr;
            vec3 horizonToSun = normalize(vec3(L.x, 0.05, L.z));
            skyHorC = texture(atmo_skyview, atmo_dir_to_uv(horizonToSun)).rgb * atmoStr;
        }
        // ambient endpoints (mood or physical) blended by view elevation, same structure as before.
        vec3 skyAmbient = mix(skyHorC, skyTopC, clamp(rd.y, 0.0, 1.0));
```
Then replace the per-sample ambient (line 354) so cloud BASES pick up the horizon color and TOPS the zenith color (the directional underside win) when physical; identical to before when off:
```glsl
                // BASE-OCCLUDED ambient: base dark (sky-occluded), tops bright → 3D form. AT-3: when physical,
                // also tint bases toward the horizon color and tops toward the zenith color (warm undersides
                // at sunset). a == 0 → skyHorC/skyTopC are the mood endpoints, so this equals the old skyAmbient.
                float h = height_frac(p);
                vec3 ambEnd = (atmoStr > 0.0) ? mix(skyHorC, skyTopC, h) : skyAmbient;
                vec3 amb = ambEnd * P.ambient * mix(0.25, 1.0, h);
```
> Sanity: with `atmoStr == 0`, `ambEnd == skyAmbient` and `sunCol`/endpoints are the mood values → the math is byte-identical to today. The branch only diverges when strength > 0.

- [ ] **Step 4: CloudVolume — fields + setters for the LUT RIDs + strength**

In `scripts/lab/CloudVolume.cs`, add fields near the other compute RIDs + a default-off strength:
```csharp
    private Rid _atmoSkyRid, _atmoTransRid;   // AT-3: atmosphere LUTs (from AtmosphereCompute, same RD)
    private bool _atmoLutsDirty;              // rebuild the raymarch uniform set once the real RIDs arrive
    private float _atmoCloudStrength;         // 0 = off (mood path); >0 = physical cloud lighting at this gain
    public void SetAtmosphereLuts(Rid skyView, Rid trans) { _atmoSkyRid = skyView; _atmoTransRid = trans; _atmoLutsDirty = true; }
    public void SetCloudAtmoLight(float strength) { _atmoCloudStrength = Mathf.Max(0f, strength); }
```

- [ ] **Step 5: CloudVolume — bind the LUTs into the raymarch set (placeholder until live) + one-time rebuild**

Refactor the raymarch-set creation out of `EnsureUniformSets` so it can be rebuilt. Replace the raymarch-set block (lines 279-284) with a call:
```csharp
        BuildCloudSet();
```
and add the method (bindings 5/6 fall back to `_weatherTex` — a valid `sampler2D` — until the real atmosphere RIDs arrive; the shader never samples them while strength == 0):
```csharp
    private void BuildCloudSet()
    {
        var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 }; uOut.AddId(_outTex);
        var uShape = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 }; uShape.AddId(_sampler); uShape.AddId(_shapeTex);
        var uDetail = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 2 }; uDetail.AddId(_sampler); uDetail.AddId(_detailTex);
        var uWeather = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 }; uWeather.AddId(_sampler); uWeather.AddId(_weatherTex);
        var uParam = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 4 }; uParam.AddId(_paramBuf);
        Rid sky = _atmoSkyRid.IsValid ? _atmoSkyRid : _weatherTex;     // placeholder until the atmosphere is live
        Rid trn = _atmoTransRid.IsValid ? _atmoTransRid : _weatherTex;
        var uSky = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 5 }; uSky.AddId(_sampler); uSky.AddId(sky);
        var uTrn = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 6 }; uTrn.AddId(_sampler); uTrn.AddId(trn);
        if (_cloudSet.IsValid) { _rd.FreeRid(_cloudSet); }
        _cloudSet = _rd.UniformSetCreate(new Array<RDUniform> { uOut, uShape, uDetail, uWeather, uParam, uSky, uTrn }, _shader, 0);
    }
```
In `RenderProcess` (after the `if (!_computeReady) { return; }` guard, line 297), rebuild once when the real RIDs land:
```csharp
        if (_atmoLutsDirty && _atmoSkyRid.IsValid && _setsBuilt) { BuildCloudSet(); _atmoLutsDirty = false; }
```

- [ ] **Step 6: CloudVolume — write the strength into `sun_color.a`**

In `BuildParams` (line 441), replace `.Vec4(_sunColor)` with the strength in `.a`:
```csharp
            .Vec4(_sunColor.R, _sunColor.G, _sunColor.B, _atmoCloudStrength)   // sun_color.rgb mood; a = AT-3 strength (0 = off)
```

- [ ] **Step 7: Build + verify NO visual change (strength still 0 everywhere)**

`dotnet build WG16.csproj` → 0 errors. Then windowed:
`"<godot>" ... scenes/review.tscn -- --time=12 --auto-shot=/c/tmp/at3_off.png`
Expected: scene UNCHANGED vs a pre-AT-3 baseline (strength defaults 0 → mood path; new bindings unused). No new shader errors (watch for `cloud_raymarch.glsl` compile errors in the log). `--shadowcheck` still PASS.

- [ ] **Step 8: Commit**
```bash
git add scripts/lab/AtmosphereCompute.cs shaders/cloud_raymarch.glsl scripts/lab/CloudVolume.cs
git commit -m "atmosphere(AT-3): plumb atmosphere LUTs into cloud raymarch (gated off, no visual change)"
```

---

## Task 2 (AT3-2): Toggle + readiness gate + the live A/B gate

Adds the toggle/CLI/control, the readiness gate that pushes the RIDs + strength, the review-key banner, and drives the user's eye-gate. After this, physical cloud lighting is A/B-toggleable.

**Files:**
- Modify: `scripts/lab/TerrainLabUI.cs` (AttachClouds: nothing to add if the gate owns the push; see Step 2), `.Process.cs` (readiness gate), `.Clouds.cs` (fields + `ApplyCloudBool`/`ApplyCloudFloat`), `.Cli.cs` (`--cloudlight`, `--cloudlightstr`), `.Review.cs` (key-8 banner), `data/lab_controls.json`.

**Interfaces:**
- Consumes: `AtmosphereCompute.SkyViewTexRid/TransTexRid/ReadyForCloudLight`, `CloudVolume.SetAtmosphereLuts/SetCloudAtmoLight/ComputeReady`.

- [ ] **Step 1: Fields + toggle/strength routing (TerrainLabUI.Clouds.cs)**

Add fields near `_aerialOn`:
```csharp
    private bool _cloudLightOn;          // AT-3 physical cloud lighting; default OFF until the eye-gate passes
    private bool _cloudLightActivated;   // one-time RID+strength push once both nodes are ready (_Process gate)
    private float _cloudLightStr = 10f;  // atmo cloud-light gain (LUT radiance → ambient); gate-tunable
```
In `ApplyCloudBool` (after the `aerial_on` case):
```csharp
        if (knob == "cloud_light") { _cloudLightOn = on; if (!on) { _cloud?.SetCloudAtmoLight(0f); _cloudLightActivated = false; } else { _cloudLightActivated = false; } return; }   // AT-3: gate re-arms; _Process pushes when ready
```
In `ApplyCloudFloat` (the `switch`, after `aerial_strength`):
```csharp
            case "cloud_light_strength": _cloudLightStr = Mathf.Max(0f, v); if (_cloudLightOn && _cloudLightActivated) { _cloud?.SetCloudAtmoLight(v); } return;
```

- [ ] **Step 2: Readiness gate — push the RIDs + strength once both nodes ready (TerrainLabUI.Process.cs)**

After the AT-2 `_aerialActivated` gate, add (mirrors it; pushes the LUT RIDs — valid only now — then enables the strength):
```csharp
        // AT-3 default-gated: once the atmosphere LUTs are live AND the cloud compute is ready, bind the LUTs
        // into the cloud raymarch (one-time) and enable physical cloud lighting at the current strength.
        if (_cloudLightOn && !_cloudLightActivated && _atmosphere != null && _atmosphere.ReadyForCloudLight
            && _cloud != null && _cloud.ComputeReady)
        {
            _cloud.SetAtmosphereLuts(_atmosphere.SkyViewTexRid, _atmosphere.TransTexRid);
            _cloud.SetCloudAtmoLight(_cloudLightStr);
            _cloudLightActivated = true;
        }
```

- [ ] **Step 3: CLI (TerrainLabUI.Cli.cs)**

Fields near `_aerialCli`:
```csharp
    private int _cloudLightCli = -1;       // --cloudlight[=1] → AT-3 physical cloud lighting on at startup (default off)
    private float _cloudLightStrCli = -1f; // --cloudlightstr=N → AT-3 strength override
```
Parse (the specific match BEFORE the general, as with `--aerial`):
```csharp
            else if (a.StartsWith("--cloudlightstr=")) { float.TryParse(a.Substring("--cloudlightstr=".Length), out _cloudLightStrCli); }
            else if (a.StartsWith("--cloudlight")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cloudLightCli = (s == "1") ? 1 : 0; }
```
Apply in `TerrainLabUI.cs` AttachClouds (after the aerial CLI block):
```csharp
        if (_cloudLightStrCli >= 0f) { _cloudLightStr = _cloudLightStrCli; }
        if (_cloudLightCli == 1) { _cloudLightOn = true; _cloudLightActivated = false; }   // _Process pushes when ready
```

- [ ] **Step 4: Lab control (data/lab_controls.json, Light tab — after `aerial_strength`)**
```json
    { "id": "cloud_light", "label": "physical cloud light (AT-3)", "tab": "Light", "type": "cloud", "cloud": "cloud_light", "default": false, "rand": false },
    { "id": "cloud_light_strength", "label": "cloud light strength", "tab": "Light", "type": "cloudf", "cloud": "cloud_light_strength", "min": 0.0, "max": 30.0, "default": 10.0, "rand": false },
```

- [ ] **Step 5: Review key-8 banner (TerrainLabUI.Review.cs)**

In case 8, after `Set("aerial_on", ...)`, also set clouds on so they're judgeable, and extend the judge text. Replace `Set("cloud_enabled", false);` in case 8 with `Set("cloud_enabled", true); Set("cloud_coverage", 0.5f);` and add `Set("cloud_light", true);`. Append to the `judge` string:
```
 AT-3: toggle 'physical cloud light (AT-3)' on/off — clouds should be lit by the real sky (cool midday, warm-reddened undersides at golden/dusk), consistent with the sky; no over-bright/over-red double-count vs off. Tune 'cloud light strength'.
```
> Note: case 8 currently turns clouds OFF for a clean sky. AT-3 needs clouds visible — turning them on changes the AT-1/AT-2 framing slightly (clouds in view). Acceptable for the combined gate; the user can still A/B each layer via its toggle.

- [ ] **Step 6: Build + mechanical A/B + profmove**

`dotnet build` → 0 errors; then windowed:
1. **OFF == baseline:** `--time=12 --auto-shot=/c/tmp/at3_off.png` (cloud_light default off) vs the Task-1 baseline — identical.
2. **ON midday:** `--cloudlight=1 --time=12 --auto-shot=/c/tmp/at3_noon.png` — cloud ambient reads cool/sky-colored; clouds not washed.
3. **ON dusk:** `--cloudlight=1 --time=18 --auto-shot=/c/tmp/at3_dusk.png` — cloud undersides warm/reddened, tops cooler; consistent with the AT-1 sunset sky.
4. **Sweep strength** if washed/flat: `--cloudlight=1 --cloudlightstr=5/15 --time=18` — pick a sane default (update both default sites if needed).
5. `--profmove --cloudlight=1` vs `--profmove` — log the cost (2 LUT taps/ray + 1 transmittance tap; expect small). `--shadowcheck` still PASS.

- [ ] **Step 7: Commit**
```bash
git add scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Process.cs scripts/lab/TerrainLabUI.Clouds.cs scripts/lab/TerrainLabUI.Cli.cs scripts/lab/TerrainLabUI.Review.cs data/lab_controls.json
git commit -m "atmosphere(AT-3): physical cloud-light toggle + readiness gate + key-8 A/B (default off)"
```

- [ ] **Step 8: USER LIVE A/B EYE-GATE**

Drive `review.tscn` key 8 (atmosphere + aerial + clouds on; toggle `physical cloud light (AT-3)`). **Judge:** clouds lit consistently with the sky across the day; **sunset undersides redden** physically (the directional-ambient win); cool sky-colored ambient midday; **no double-count** (not over-bright/over-red vs off); ties with the AT-1 sky + AT-2 haze; perf OK. Tune `cloud light strength` live. Record in DECISIONS + NEEDS_REVIEW + ROADMAP. PASS → flip default-on; else iterate (strength, the horizon-toward-sun elevation, the zenith/horizon blend) or escalate (multi-scatter ambient, per-sample transmittance — both banked OUT in the spec).

---

## Self-Review

**Spec coverage** (`specs/2026-06-21-gpu-atmosphere-at3-cloud-lighting-design.md`):
- Expose LUT RIDs + readiness → T1 Step 1. ✓
- Bind LUTs into the raymarch set + one-time rebuild handshake → T1 Steps 5 (BuildCloudSet placeholder + RenderProcess rebuild). ✓
- Ambient from sky-view (zenith + horizon-to-sun), directional underside blend → T1 Step 3. ✓
- Sun transmittance reddening, neutral base → T1 Step 3. ✓
- No double-count (LUT instead of mood, gated by `sun_color.a`) → T1 Steps 3, 6. ✓
- No shared-block change (reuse `sun_color.a`, raymarch-only bindings) → T1 Steps 2/3/6 (shadow shaders untouched). ✓
- Toggle + `--cloudlight` + control, default OFF → T2 Steps 1/3/4. ✓
- Readiness gate (RIDs valid only when ready) → T2 Step 2. ✓
- Strength knob → T2 Steps 1/4. ✓
- Live eye-gate → T2 Step 8. ✓
- Risks: RID handshake (placeholder + one-time rebuild + readiness gate, T1-5/T2-2); look-sensitive (default-off + strength knob); atmo_dir_to_uv drift (verbatim copy, T1-2); shared-block (no shared field touched); perf (2-3 taps, T2-6 profmove). ✓

**Placeholder scan:** No TBD/“add error handling”. Every code step shows the exact code. The strength default (10) is an explicit start value, gate-tunable (T2-6 sweeps it).

**Type consistency:** `SkyViewTexRid`/`TransTexRid`/`ReadyForCloudLight` (T1-1) match the consumers in T2-2. `SetAtmosphereLuts(Rid,Rid)`/`SetCloudAtmoLight(float)` (T1-4) match T2-1/T2-2 call sites. `sun_color.a` written in C# (T1-6) == read in GLSL (T1-3, `P.sun_color.a`). Binding numbers 5/6 match between the shader (T1-2) and `BuildCloudSet` (T1-5). Control ids `cloud_light`/`cloud_light_strength` (T2-4) match `ApplyCloudBool`/`ApplyCloudFloat` cases (T2-1).
