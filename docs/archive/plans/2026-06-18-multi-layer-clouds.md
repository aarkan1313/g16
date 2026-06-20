# Multi-Layer Clouds Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (author executes inline). Steps use `- [ ]`. GPU/visual work — the gate is the USER's eye in motion; mechanical checks (builds, shaders compile, `--shadowcheck` stays PASS, single-layer == current look) are the automatable part. FLAG each visual gate; never claim a look is good from a still.

**Goal:** Replace the single cloud deck with N data-driven layers (up to ~8), each with its own altitude/thickness/size/cell_scale/coverage_weight/density/opacity/type, marched in one pass via per-step per-layer density sum — so dense cover stops reading as one mass and clouds vary by height/size.

**Architecture:** A new `CloudLayers` unit parses a JSON layer array (layer 0 = the current flat knobs, for backward-compat + regression guard) and packs it into the EXISTING cloud param storage buffer (appended after the scalars — no new binding). `cloud_raymarch.glsl` and `cloud_shadow.glsl` loop the active layers per march step, evaluating the shared `sample_density` with each layer's params and summing, with density-based step acceleration over empty gaps. Master coverage scales all; per-layer `coverage_weight` sets the mix (the seam the future weather/biome system writes).

**Tech Stack:** Godot 4.6 mono; GLSL compute on the main RD via `CallOnRenderThread`; `Texture2Drd` consumers unchanged. Local-RD `--shadowcheck` numeric regression test (windowed).

## Global Constraints

- `sample_density(...)` per-layer body MUST stay byte-identical between `cloud_raymarch.glsl` and `cloud_shadow.glsl` (the shadow-coupling guarantee; `--shadowcheck` proves it, r>0.6 at mid coverage).
- Local-RD compute (noise bake, `--shadowcheck`) runs WINDOWED only — never `--headless` (returns null → NullRef).
- Launch windowed with the ABSOLUTE project path: `--path /c/Wg16/wg-16-project … res://scenes/terrain_lab.tscn` (shell cwd is the parent; `.` opens the launcher).
- Single Godot 4.6 process at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` before relaunch.
- std430 packing: every C# `BuildParams` write order must match the GLSL struct field-for-field; layer array is a fixed-stride run of floats appended to the param buffer.
- Layer 0 with default params MUST reproduce today's single-layer look (regression guard).
- All knobs are hot-reloadable data; no magic numbers in code.

---

### Task 1: CloudLayer model + CloudLayers unit (data only)

**Files:**
- Create: `scripts/lab/CloudLayers.cs`
- Create: `data/cloud_layers.json`

**Interfaces:**
- Produces: `record CloudLayer(float Altitude, float Thickness, float Size, float CellScale, float CoverageWeight, float Density, float Opacity, float Type, float Edge, float Detail, float DetailSize, int NoiseId, bool Enabled)`; `CloudLayers.Load() → List<CloudLayer>`; `CloudLayers.Pack(List<CloudLayer>, out int count) → float[]` (fixed stride per layer); `const int Stride`, `const int MaxLayers = 8`.

- [ ] **Step 1: Write CloudLayer record + the unit skeleton**

Create `scripts/lab/CloudLayers.cs`:
```csharp
using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// One cloud deck. Data only — no marching/scene knowledge (separation of concerns).
public readonly record struct CloudLayer(
    float Altitude, float Thickness, float Size, float CellScale,
    float CoverageWeight, float Density, float Opacity, float Type,
    float Edge, float Detail, float DetailSize, int NoiseId, bool Enabled);

/// Owns the cloud-layer array: load/validate from JSON, pack into a float[] run for the
/// GPU param buffer. One job. The march consumes the packed buffer; presets/weather write
/// the layers. Layer 0 mirrors the legacy flat knobs (set by CloudVolume at runtime).
public static class CloudLayers
{
    public const int MaxLayers = 8;
    public const int Stride = 12;   // floats per layer in the packed buffer (see Pack)
    public const string Path = "res://data/cloud_layers.json";

    public static List<CloudLayer> Load()
    {
        string abs = ProjectSettings.GlobalizePath(Path);
        if (!System.IO.File.Exists(abs)) { return new List<CloudLayer>(); }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        var list = new List<CloudLayer>();
        if (doc.RootElement.TryGetProperty("layers", out var arr))
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= MaxLayers) { break; }
                float F(string k, float d) => e.TryGetProperty(k, out var v) ? v.GetSingle() : d;
                int I(string k, int d) => e.TryGetProperty(k, out var v) ? v.GetInt32() : d;
                bool B(string k, bool d) => e.TryGetProperty(k, out var v) ? v.GetBoolean() : d;
                list.Add(new CloudLayer(
                    F("altitude", 1800f), F("thickness", 1400f), F("size", 1f), F("cell_scale", 1.6f),
                    F("coverage_weight", 1f), F("density", 1f), F("opacity", 1f), F("type", 0.6f),
                    F("edge", 0.5f), F("detail", 0.4f), F("detail_size", 1f), I("noise_id", 0), B("enabled", true)));
            }
        }
        return list;
    }

    /// Pack enabled layers into Stride floats each. Returns the float[] and active count.
    public static float[] Pack(List<CloudLayer> layers, out int count)
    {
        var packed = new float[MaxLayers * Stride];
        count = 0;
        foreach (var L in layers)
        {
            if (!L.Enabled || count >= MaxLayers) { continue; }
            int o = count * Stride;
            packed[o + 0] = L.Altitude;       packed[o + 1] = L.Thickness;
            packed[o + 2] = L.Size;           packed[o + 3] = L.CellScale;
            packed[o + 4] = L.CoverageWeight; packed[o + 5] = L.Density;
            packed[o + 6] = L.Opacity;        packed[o + 7] = L.Type;
            packed[o + 8] = L.Edge;           packed[o + 9] = L.Detail;
            packed[o + 10] = L.DetailSize;    packed[o + 11] = L.NoiseId;
            count++;
        }
        return packed;
    }
}
```

- [ ] **Step 2: Seed cloud_layers.json with a 2-deck example (low cumulus + high cirrus)**

Create `data/cloud_layers.json`:
```json
{
  "_comment": "Cloud decks. The march sums all enabled layers per step. Layer 0's params are OVERRIDDEN at runtime by the legacy flat knobs (CloudVolume) for backward-compat; layers 1+ are pure data here. coverage_weight = this deck's share of the master coverage knob.",
  "layers": [
    { "altitude": 1500, "thickness": 1200, "size": 1.0, "cell_scale": 1.6, "coverage_weight": 1.0, "density": 1.0, "opacity": 1.0, "type": 0.65, "edge": 0.55, "detail": 0.5, "detail_size": 1.0, "noise_id": 0, "enabled": true },
    { "altitude": 5500, "thickness": 600,  "size": 2.4, "cell_scale": 0.8, "coverage_weight": 0.6, "density": 0.5, "opacity": 0.7, "type": 0.12, "edge": 0.35, "detail": 0.7, "detail_size": 1.6, "noise_id": 0, "enabled": true }
  ]
}
```

- [ ] **Step 3: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj`
Expected: 0 errors (new file compiles; nothing consumes it yet).

- [ ] **Step 4: Commit**
```bash
git -C /c/Wg16/wg-16-project add scripts/lab/CloudLayers.cs data/cloud_layers.json
git -C /c/Wg16/wg-16-project commit -m "Multi-layer clouds T1: CloudLayer model + CloudLayers data unit + seed 2-deck JSON"
```

---

### Task 2: Upload the layer buffer + layer-0-from-legacy-knobs (CloudVolume)

**Files:**
- Modify: `scripts/lab/CloudVolume.cs`

**Interfaces:**
- Consumes: `CloudLayers.Load/Pack/Stride/MaxLayers`, the existing `_p` (CloudParams) flat knobs.
- Produces: layer data appended into the param buffer; `_layerCount` int passed to the shader.

- [ ] **Step 1: Load layers at Attach; hold them**

In `CloudVolume.cs`, add fields near `_p`:
```csharp
private System.Collections.Generic.List<CloudLayer> _layers = new();
private int _layerCount;
```
In `Attach(...)`, after `_p = CloudParams.Load();`:
```csharp
_layers = CloudLayers.Load();
if (_layers.Count == 0) { _layers.Add(default); }   // ensure at least layer 0 (filled from knobs)
```

- [ ] **Step 2: Build the packed layer array each frame with layer 0 = legacy knobs**

Add a helper in CloudVolume:
```csharp
// Layer 0's deck params come from the legacy flat knobs (_p / _cellScale) so the single-
// layer look + the existing UI keep working; layers 1+ are the JSON data verbatim.
private float[] PackLayers(out int count)
{
    var eff = new System.Collections.Generic.List<CloudLayer>(_layers);
    var l0 = eff[0];
    eff[0] = l0 with {
        Altitude = _p.AltitudeM, Thickness = _p.ThicknessM, Size = _p.Size, CellScale = _cellScale,
        CoverageWeight = 1f, Density = _p.Density, Opacity = _p.Opacity, Type = _p.CloudType,
        Edge = _p.Edge, Detail = _p.Detail, DetailSize = _p.DetailSize, NoiseId = 0, Enabled = true };
    return CloudLayers.Pack(eff, out count);
}
```

- [ ] **Step 3: Append the layer run + count to the param buffer**

`ParamFloats` is currently 52. Layer data = `MaxLayers(8) * Stride(12) = 96` floats, plus 1 for `layer_count` (pad to keep 16B alignment → +4: count + 3 pad). New `ParamFloats = 52 + 96 + 4 = 152`. In `BuildParams`, after the existing `F(_cellScale);` line, append:
```csharp
float[] layerData = PackLayers(out _layerCount);
F(_layerCount); F(0f); F(0f); F(0f);            // layer_count + pad (16B)
for (int i = 0; i < layerData.Length; i++) { F(layerData[i]); }
```
Set `private const int ParamFloats = 152;`.

- [ ] **Step 4: Same for the shadow param buffer**

`ShadowParamFloats` is 28. Append the same `layer_count(+3 pad) + 96 layer floats` → `28 + 4 + 96 = 128`. In `BuildShadowParams`, after `F(_cellScale);`:
```csharp
float[] layerData = PackLayers(out _);
F(_layerCount); F(0f); F(0f); F(0f);
for (int i = 0; i < layerData.Length; i++) { F(layerData[i]); }
```
Set `private const int ShadowParamFloats = 128;`.

- [ ] **Step 5: Build**

Run: `dotnet build WG16.csproj`
Expected: 0 errors. (Shaders don't read the new floats yet — buffer just carries them.)

- [ ] **Step 6: Commit**
```bash
git -C /c/Wg16/wg-16-project add scripts/lab/CloudVolume.cs
git -C /c/Wg16/wg-16-project commit -m "Multi-layer clouds T2: pack layer array into the cloud param buffers (layer 0 = legacy knobs)"
```

---

### Task 3: Raymarch — per-step per-layer density sum + step acceleration

**Files:**
- Modify: `shaders/cloud_raymarch.glsl`

**Interfaces:**
- Consumes: the appended `layer_count` + layer array in `ParamsBuf`.
- Produces: clouds that sum all active decks across a full-span shell.

- [ ] **Step 1: Add the layer array to the GLSL ParamsBuf**

After `float cell_scale;` in `ParamsBuf`:
```glsl
    float layer_count; float _lpad0, _lpad1, _lpad2;
    // 8 layers × 12 floats, std430 (each float is 4B; this is a flat run matching CloudLayers.Pack)
    float layers[96];
} P;
```
Helper to read a layer field: `#define LF(i, f) P.layers[(i)*12 + (f)]` (f: 0 alt,1 thick,2 size,3 cell,4 covW,5 dens,6 opac,7 type,8 edge,9 detail,10 detailSize,11 noiseId).

- [ ] **Step 2: Refactor sample_density to take explicit per-layer params**

Change `sample_density` to accept the layer's params instead of reading `P.*` globals:
```glsl
// per-LAYER density at world point p. baseR/topR = this layer's shell radii. All shape
// params come from the layer (so each deck differs). MUST match cloud_shadow.glsl.
float layer_density(vec3 p, float baseR, float topR, vec2 windOff,
                    float lsize, float lcell, float ldens, float lopac, float ltype,
                    float ledge, float ldetail, float ldetsize, float covW){
    float r = length(p);
    float h = clamp((r - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
    vec3 lp = vec3(p.x, r - baseR, p.z);
    vec2 wuv = lp.xz * WEATHER_SCALE + windOff * WEATHER_SCALE;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(P.coverage * covW + (w.r - 0.5) * 0.7, 0.0, 1.0);
    float type     = clamp(ltype + (w.g - 0.5) * 0.4, 0.0, 1.0);
    float densBias = mix(0.7, 1.3, w.b);
    vec3 warp = (vec3(texture(detail_tex, lp * (DETAIL_SCALE * 0.25)).r) - 0.5) * WARP_AMOUNT;
    vec3 lpw = lp + warp;
    float sScale = SHAPE_SCALE / max(lsize, 0.01);
    vec3 suv = lpw * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g*0.625 + sh.b*0.25 + sh.a*0.125;
    float base = remap(sh.r, fbm*0.3, 1.0, 0.0, 1.0);
    float thresh = mix(0.92, 0.02, coverage);
    float soft = min(thresh + mix(0.30, 0.10, ledge), 1.0);
    float shape = smoothstep(thresh, soft, base);
    float cellScale = sScale * 0.35 * max(lcell, 0.05);
    float cell = texture(shape_tex, lpw * cellScale + vec3(windOff.x, h, windOff.y) * cellScale).g;
    float cellGate = smoothstep(mix(0.78, 0.35, coverage), mix(1.0, 0.6, coverage), cell);
    shape *= cellGate;
    shape *= type_gradient(h, type);
    if (shape <= 0.0) return 0.0;
    if (ldetail > 0.0){
        float dScale = DETAIL_SCALE / max(ldetsize, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x*1.7, h, windOff.y*1.7) * dScale;
        float det = texture(detail_tex, duv).r;
        float erodeAmt = mix(0.25, 0.6, h) * ldetail;
        shape = clamp(remap(shape, det*erodeAmt, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * ldens * densBias;   // opacity applied by caller (sigma)
}
```

- [ ] **Step 3: Sum all layers at a world point**

Add a combiner that, for a world point, sums every active layer whose band contains it, returning total density + a blended opacity:
```glsl
float density_all(vec3 p, vec2 windOff, out float opacOut){
    int n = clamp(int(P.layer_count), 1, 8);
    float total = 0.0; float opAccum = 0.0;
    for (int i = 0; i < n; i++){
        float alt = LF(i,0), thick = LF(i,1);
        float baseR = PLANET_R + alt, topR = baseR + thick;
        float r = length(p);
        if (r < baseR || r > topR) continue;          // outside this deck's band
        float d = layer_density(p, baseR, topR, windOff,
            LF(i,2), LF(i,3), LF(i,5), LF(i,6), LF(i,7), LF(i,8), LF(i,9), LF(i,10), LF(i,4));
        total += d; opAccum += d * LF(i,6);
    }
    opacOut = (total > 1e-5) ? opAccum / total : 1.0;  // density-weighted opacity
    return total;
}
```

- [ ] **Step 4: Full-span shell + step-accelerated march in main()**

Replace the single-layer shell setup + march loop. Compute the span over active layers, march from the camera, big steps in empty air:
```glsl
// full span across active decks
float minBase = 1e9, maxTop = -1e9;
int n = clamp(int(P.layer_count), 1, 8);
for (int i = 0; i < n; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
float belowBase = min(P.cam_world.y, minBase - 1.0);
vec3 ro = vec3(P.cam_world.x, PLANET_R + belowBase, P.cam_world.z);
float spanBaseR = PLANET_R + minBase, spanTopR = PLANET_R + maxTop;
vec2 hitB = ray_sphere(ro, rd, spanBaseR);
vec2 hitT = ray_sphere(ro, rd, spanTopR);
float tStart = max(hitB.y, 0.0), tEnd = max(hitT.y, 0.0);
...
int steps = clamp(int(P.steps), 16, 160);
float dt = (tEnd - tStart) / float(steps);
float t = tStart; float emptyRun = 0.0;
for (int i = 0; i < steps; i++){
    vec3 p = ro + rd * t;
    float opac; float dens = density_all(p, windOff, opac);
    if (dens > 0.001){
        emptyRun = 0.0;
        float lightT = light_march_all(p, L, windOff);   // see Step 5
        float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * P.powder), 0.5);
        float sigma = dens * 0.02 * opac;
        float beer = exp(-sigma * dt);
        float sun = lightT * (phase + 0.4);
        vec3 lum = (sunCol * sun + skyAmbient * P.ambient) * P.brightness;
        scattered += T * lum * powder * (1.0 - beer);
        T *= beer; if (T < 0.01) break;
        t += dt;
    } else {
        // step acceleration: take a bigger stride through empty air
        emptyRun += 1.0;
        t += dt * (1.0 + min(emptyRun, 4.0));
    }
}
```

- [ ] **Step 5: Per-layer light march (sum toward the sun)**

Add `light_march_all` mirroring the old `light_march` but using `density_all`:
```glsl
float light_march_all(vec3 p, vec3 L, vec2 windOff){
    const int LSTEPS = 6;
    float lss = 250.0;   // fixed metres per light step (decoupled from any one layer thickness)
    float d = 0.0; vec3 q = p; float op;
    for (int i = 0; i < LSTEPS; i++){ q += L * lss; d += density_all(q, windOff, op) * lss; }
    return exp(-d * P.sun_absorb * 0.02);
}
```
Remove the now-unused single-layer `sample_density`/`light_march`/single-shell code.

- [ ] **Step 6: Build + run windowed; verify two decks + single-layer regression**

Run: `dotnet build WG16.csproj`; launch windowed (absolute path). Then disable layer 1 in `cloud_layers.json` (`"enabled": false`) and relaunch.
Expected (MECHANICAL): builds, "compute initialized" prints, no shader errors. With layer 1 off, the sky ≈ the pre-multi-layer single-deck look (regression).
Expected (VISUAL GATE — user): with both decks, a low cumulus deck + a distinct high cirrus deck at different heights/sizes. FLAG for user.

- [ ] **Step 7: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_raymarch.glsl
git -C /c/Wg16/wg-16-project commit -m "Multi-layer clouds T3: full-span per-step per-layer density sum + step acceleration"
```

---

### Task 4: Shadow shader — identical per-layer sum (keep coupling) + re-prove

**Files:**
- Modify: `shaders/cloud_shadow.glsl`

**Interfaces:**
- Consumes: the same layer array (appended to the shadow param buffer in T2).
- Produces: a shadow map summing all decks along the sun ray; `--shadowcheck` stays PASS.

- [ ] **Step 1: Mirror the layer array in the shadow ParamsBuf**

After `float cell_scale;` in `cloud_shadow.glsl` `ParamsBuf`:
```glsl
    float layer_count; float _lpad0, _lpad1, _lpad2;
    float layers[96];
} P;
```
Same `#define LF(i,f) P.layers[(i)*12 + (f)]`.

- [ ] **Step 2: Copy layer_density + density_all (byte-identical to the raymarch)**

Replace the shadow `sample_density` with the EXACT `layer_density` from T3 Step 2 and the `density_all` from T3 Step 3 (copy verbatim — the coupling guarantee). Note `P.coverage` exists in the shadow params already; `WEATHER_SCALE` etc. consts are already present.

- [ ] **Step 3: Sun-march summing all decks**

In `main()`, replace the single-shell sun march with a full-span one using `density_all`:
```glsl
float minBase = 1e9, maxTop = -1e9;
int n = clamp(int(P.layer_count), 1, 8);
for (int i = 0; i < n; i++){ float a = LF(i,0); minBase = min(minBase, a); maxTop = max(maxTop, a + LF(i,1)); }
vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
float spanBaseR = PLANET_R + minBase, spanTopR = PLANET_R + maxTop;
vec2 hitB = ray_sphere(ro, L, spanBaseR);
vec2 hitT = ray_sphere(ro, L, spanTopR);
float tStart = max(hitB.y, 0.0), tEnd = max(hitT.y, 0.0);
float vis = 1.0;
if (L.y > 0.05 && tEnd > tStart){
    float pathLen = min(tEnd - tStart, (maxTop - minBase) / max(L.y, 0.2));
    int steps = clamp(int(P.region.y), 4, 32);
    float dt = pathLen / float(steps);
    float d = 0.0; float t = tStart; float op;
    for (int i = 0; i < steps; i++){ vec3 p = ro + L * t; d += density_all(p, P.wind_offset, op) * dt; t += dt; }
    float trans = exp(-d * 0.02 * L.y);
    float shadowed = smoothstep(0.02, 0.5, 1.0 - trans);
    vis = mix(1.0, trans, P.strength * shadowed);
}
imageStore(shadow_tex, px, vec4(vis, 0.0, 0.0, 1.0));
```

- [ ] **Step 4: Update the --shadowcheck shader to the layer model too**

`shaders/cloud_shadow_check.glsl` + `CloudShadowCheck.cs` use the OLD single-layer `sample_density`/params. To keep the test valid, give the check shader the same `layer_density`+`density_all` and append the layer array to its param buffer (mirror T2's pack into `CloudShadowCheck.BuildParams`, and add `layers[96]` + `layer_count` to the check struct). Sample the SAME cloud_layers.json via `CloudLayers.Load/Pack` in the C# checker.

- [ ] **Step 5: Build + RE-PROVE shadows (mechanical, no eyeball)**

Run: `dotnet build WG16.csproj`; launch windowed with `-- --shadowcheck --coverage=0.45`.
Expected (MECHANICAL): console prints `[shadowcheck] PASS — r=… > 0.6`. If FAIL (and not SATURATED), the per-layer sum decoupled the shadow — fix before proceeding. This is the regression guard for the coupling guarantee.

- [ ] **Step 6: Commit**
```bash
git -C /c/Wg16/wg-16-project add shaders/cloud_shadow.glsl shaders/cloud_shadow_check.glsl scripts/lab/CloudShadowCheck.cs
git -C /c/Wg16/wg-16-project commit -m "Multi-layer clouds T4: shadow + shadowcheck use identical per-layer sum (re-proven PASS)"
```

---

### Task 5: Authoring (presets/registry) + docs/state update

**Files:**
- Modify: `data/cloud_presets.json`, `docs/DECISIONS.md`, `docs/HANDOFF.md`, `docs/ROADMAP.md`, `docs/TECH_STACK.md`

**Interfaces:**
- Consumes: the working multi-layer system.
- Produces: a multi-deck preset; current docs.

- [ ] **Step 1: Add a multi-deck preset note + keep existing presets working**

Existing presets set the flat knobs (= layer 0), which still works. Add a top-of-file comment in `data/cloud_presets.json` noting that layer 1+ decks live in `cloud_layers.json` and that preset-driven per-layer stacks are a future authoring step (per the spec's weather/biome seam). (Full per-preset layer stacks = a later enhancement; not built now — YAGNI until presets need it.)

- [ ] **Step 2: TECH_STACK.md — add the CloudLayers unit**

Under the Cloud unit inventory, add: `scripts/lab/CloudLayers.cs` + `data/cloud_layers.json` — N-deck layer data, packed into the cloud param buffers; layer 0 = legacy flat knobs; march sums all decks; per-layer noise is a future seam.

- [ ] **Step 3: DECISIONS.md (newest-first) — multi-layer clouds**

Entry: single deck → N data-driven decks (full-span per-step sum, step-accel, layer 0 = legacy, coverage_weight mix + weather/biome write-seam); fixes "dense = one mass / same size". Reference the spec + this plan; note `--shadowcheck` re-proven PASS.

- [ ] **Step 4: HANDOFF §6 + ROADMAP — current state**

HANDOFF: cloud system is now multi-layer; review backlog notes it. ROADMAP: mark multi-layer done-pending-review; the existing cloud follow-ups (per-preset layer stacks, per-layer noise) stay as future items.

- [ ] **Step 5: Build, launch, present for review**

Launch windowed (absolute path). Hand the user the checklist: distinct decks at different heights/sizes; dense no longer one mass; tunable via cloud_layers.json; single-layer regression intact; `--shadowcheck` PASS (already verified mechanically).

- [ ] **Step 6: Commit**
```bash
git -C /c/Wg16/wg-16-project add data/cloud_presets.json docs/
git -C /c/Wg16/wg-16-project commit -m "Multi-layer clouds T5: authoring note + docs/state update (TECH_STACK/DECISIONS/HANDOFF/ROADMAP)"
```

---

## Self-Review

- **Spec coverage:** layer model + CloudLayers unit (T1) · layer buffer + layer 0 = legacy (T2) · full-span per-step sum + step-accel (T3) · shadow identical sum + re-prove (T4) · coverage_weight mix + authoring + weather/biome seam noted + docs (T5). All spec sections have a task. Per-layer noise seam = `NoiseId` carried but unused (future), per spec.
- **Placeholder scan:** all code steps concrete. The only deferred items are explicitly-future (per-preset layer stacks, per-layer noise) per spec YAGNI — not hidden TODOs.
- **Type consistency:** `CloudLayer` record fields ↔ `Pack` order ↔ GLSL `LF(i,f)` indices ↔ `layer_density` arg order all match (alt,thick,size,cell,covW,dens,opac,type,edge,detail,detailSize,noiseId = indices 0-11). `layer_density`+`density_all` identical in raymarch (T3), shadow (T4 Step 2), and check (T4 Step 4) — the coupling guarantee, called out. `ParamFloats=152`, `ShadowParamFloats=128` — both = base + 4 (count+pad) + 96 (layers). Stride=12, MaxLayers=8.
- **Risk:** the std430 layer-run packing (C# order ↔ GLSL `layers[]`) is the main footgun — `--shadowcheck` (T4 Step 5) is the mechanical guard that the data lines up AND the coupling holds. Layer-0-from-knobs (T2) + "disable layer 1 → regression" (T3 Step 6) guards the single-layer look. `git checkout` / backup tag reverts.
- **Gate:** user's eye for the multi-deck LOOK; `--shadowcheck` PASS + single-layer regression are the mechanical gates.
