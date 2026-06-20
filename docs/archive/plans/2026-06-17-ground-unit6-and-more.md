# Ground Unit 6 — "And More" (Ground Extras) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) tracking. Each task-GROUP below is independent — build/judge/ship one at a time; do NOT batch them.

**Goal:** Land the deferred AAA "and more" ground extras as a MENU of independent, toggleable layers on top of the now-good Units 1-5 ground: (A) a detail-mesh SCATTER INTERFACE + a minimal pebble/debris MultiMesh demo (the hook the flora subsystem plugs into — built here, but flora itself is NOT), (B) wetness/puddles, (C) snow-by-aspect, (D) higher-quality triplanar. Each is optional, off-able, and revertable on its own.

**Architecture:** Per-fragment effects (B wetness, C snow, D triplanar) live INSIDE `shaders/terrain_lab.gdshader`'s `fragment()`, run after the material is resolved (post-`alb`/`rgh`/`nrm`), and CONSUME Unit 4's baked breakup masks via `groundData(uv)` (cavity/flow drive puddles; aspect drives snow) — masks are baked once, so per-frame cost is just a few extra ALU ops + the existing data-texture read. The scatter layer (A) is a separate C# subsystem: a `IScatterProvider` interface (surface pos + normal + scatter-density sampled from `groundData`) feeding a `MultiMeshInstance3D` sibling of `TerrainLab`; placement is a GPU-compute candidate but is left CPU-side in the demo and explicitly handed to the flora subsystem to own.

**Tech Stack:** Godot 4.6 mono, GLSL spatial shader (`shaders/terrain_lab.gdshader`), C# (`scripts/lab/*`), JSON control registry (`data/lab_controls.json`). Compute (optional, deferred): `scripts/lab/SplatCompute.cs`-style local-RD bake / render-thread dispatch.

---

## This is a MENU, built LAST

Unit 6 is the deferred polish/extras layer of the 6-unit ground-presentation arc (see `docs/superpowers/specs/2026-06-17-ground-presentation-arc-design.md`). It is built **only after Units 1-5 are judged good live** (anti-repetition → distance detail → surface depth → procedural breakup → color/value). The four groups below are mutually independent — none depends on another, so they can be built, judged, and shipped in any order and individually reverted. The user mandate (pillars): *"an AAA game has everything possible — this is going to be the best world generator if we do it right."* Treat each group as an optional AAA ground extra, each behind its own live toggle.

**Dependency on Unit 4:** Groups **B (wetness)** and **C (snow)** CONSUME Unit 4's baked breakup masks via `groundData(uv)` — B reads `cavity` + `flow` (water pools in concave / low-flow areas), C reads `aspect` (snow on up-facing + pole-facing slopes). **These two groups MUST NOT start until Unit 4 has landed `groundData()` returning those masks.** Group **A (scatter)** can read `groundData`'s scatter-density if present but degrades gracefully to a slope/height heuristic if Unit 4's density channel isn't baked yet. Group **D (triplanar)** has NO Unit 4 dependency — it only touches `tri_w`/`ar_sample_wp`.

**Flora-overlap boundary (READ before Group A):** A separate flora subsystem is being built elsewhere and may OWN scatter placement. Unit 6 does NOT build flora. Group A builds only (1) the `IScatterProvider` C# interface (the clean seam flora consumes) and (2) a minimal pebble/debris MultiMesh demo that proves the interface. If/when flora ships, it implements `IScatterProvider` (or consumes the same density source) and the pebble demo can be deleted — they must not duplicate placement logic. Coordination point is called out explicitly in Group A steps.

---

## Environment (gotchas — Godot 4.6 mono, GPU/visual)

- Project root: `C:\Wg16\wg-16-project`.
- **ONE Godot at a time.** Before any launch: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null`.
- **ALWAYS** pass `--rendering-driver vulkan`.
- After C#/shader changes: `dotnet build WG16.csproj`, then headless import with the **console** exe:
  `"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --headless --path . --import`
  (`--import` catches GLSL parse errors in `terrain_lab.gdshader`.)
- Runs use the windowed exe (no `_console`): `Godot_v4.6.2-stable_mono_win64.exe --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- <cli>`.
- **local-RD compute can't run `--headless`** — any compute-scatter path (Group A optional step) must be judged in a windowed run, not headless.
- **MultiMesh scatter + any per-frame compute→material** need the render-thread pattern: `RenderingServer.CallOnRenderThread`, assign the `Texture2Drd` RID ONCE then never reassign (the Texture2Drd race, Godot #118292), and **NOT** a `CompositorEffect`. See `scripts/lab/CloudVolume.cs` for the proven pattern. The pebble demo here does CPU placement (no per-frame compute) to stay simple — compute is the deferred upgrade flora may own.
- **Never judge from stills.** All effects (wetness sparkle, snow read, scatter density, triplanar seams) are motion/range artifacts — THE gate is the user flying it live.
- Lab CLIs: `--cam=x,y,z,pitch,yaw`, `--clouds=0/1`, `--mood=<id>`, `--profile[=secs]`, `--auto-shot=<abs.png>`.
- Controls live in `data/lab_controls.json`. Add a new **"Ground+"** tab (Unit 6's controls group). After editing JSON, validate it.

**Locked shader anchors (verified against current code):**
- `ar_sample_wp(sampler2D, vec2, vec3)` ~line 243 — the single material-fetch seam (Unit 1).
- `distanceWeight(vec3 wp)` ~line 231 — shared near/far LOD factor.
- `tri_w(vec3 nr)` ~line 236 — `pow(abs(nr), vec3(tri_sharpness))` 3-plane blend (Group D upgrades this).
- `fragment()` ~line 459; `ALBEDO` ~line 525, `ROUGHNESS` ~line 526, `NORMAL` ~lines 529-534.
- Contact block ~lines 508-517 holds the existing primitive `snow_dust_amp`/`snow_dust_h` dusting — **Group C's snow-by-aspect SUPERSEDES it** (remove/replace, don't stack).
- **`groundData(uv)` does NOT exist yet** — it is Unit 4's deliverable. Groups B/C call it as a locked interface; this plan defines the consumption contract but assumes Unit 4 added the function + the baked data texture(s).

**Locked `groundData` contract (Unit 4 provides; Unit 6 consumes):**
```glsl
struct GroundData {
    int   domZone, secZone;
    float mix;
    float slope, curv, cavity, aspect, flow;   // breakup masks (0..1 except aspect)
    vec3  macroTint;
    float scatterDensity;                       // 0..1 placement hint (Group A may read)
};
GroundData groundData(vec2 uv);   // reads the baked data texture(s), one fetch
```
> Convention used below: `cavity` 1 = deeply concave (pools collect), `flow` 1 = high downhill flow (drains, stays dry), `aspect` is the slope-facing direction as a 0..1 azimuth where `aspect_pole` (a uniform, the pole-facing azimuth) marks max snow retention. If Unit 4's exact field names differ, alias them in one place at the top of each consuming block and note it — do not scatter renames.

---

## Group A — Detail-mesh scatter INTERFACE + pebble/debris MultiMesh demo

Builds the clean seam the flora subsystem plugs into, plus a minimal pebble demo proving it. **Flora is NOT built here.**

**Files:**
- Create `scripts/lab/IScatterProvider.cs`
- Create `scripts/lab/PebbleScatter.cs`
- Modify `scripts/lab/TerrainLab.cs` (expose heightfield/region accessors + a `ScatterSurface()` query)
- Modify `scripts/lab/TerrainLabUI.cs` (instantiate + toggle the demo)
- Modify `data/lab_controls.json` (Ground+ tab toggle/knobs)

- [ ] **Step 1: Define the scatter interface** — the ONE seam flora or any scatterer consumes. Create `scripts/lab/IScatterProvider.cs`:

```csharp
using Godot;

namespace WG16.Lab;

/// One scattered placement: where to drop an instance + how it sits + how dense the
/// surface "wants" detail there (drives per-instance accept/reject + scale).
public readonly struct ScatterSample
{
    public readonly Vector3 Position;   // world-space surface point
    public readonly Vector3 Normal;     // surface normal at Position
    public readonly float   Density;    // 0..1 scatter-density from groundData (or heuristic)
    public ScatterSample(Vector3 pos, Vector3 nrm, float density)
    { Position = pos; Normal = nrm; Density = density; }
}

/// The CLEAN SEAM between the ground surface and anything that scatters detail meshes
/// on it (pebbles/debris here; the flora subsystem elsewhere). A surface implements
/// this; a scatterer (PebbleScatter, or the flora system) consumes it. The scatterer
/// owns its own MultiMesh + placement strategy — this interface only answers
/// "what is the surface doing at world XZ?". DO NOT put flora/pebble logic in here.
public interface IScatterProvider
{
    /// Surface query at a world XZ. Returns false if XZ is outside the region.
    bool TryScatterSurface(float worldX, float worldZ, out ScatterSample sample);

    /// Region half-extent + a deterministic seed so independent scatterers that share
    /// this provider place against the same surface/space without coordinating.
    float RegionHalfExtentM { get; }
    int   ScatterSeed { get; }
}
```

- [ ] **Step 2: Make `TerrainLab` the scatter provider.** In `scripts/lab/TerrainLab.cs`, add `IScatterProvider` to the class and implement the query against the already-stored `_heights`/`_res`/`_regionSize`/`_spacing` (bilinear height + central-difference normal — mirrors the `vertex()` normal). Add near the other public setters (~line 100):

```csharp
public float RegionHalfExtentM => _regionSize * 0.5f;
public int   ScatterSeed => 1606;   // arc date; deterministic across scatterers

// Bilinear surface query + central-difference normal, in TerrainLab world space
// (plane is centered on origin; uv = (xz + region/2)/region, matching vertex()).
public bool TryScatterSurface(float worldX, float worldZ, out ScatterSample sample)
{
    sample = default;
    if (_heights == null) return false;
    float half = _regionSize * 0.5f;
    if (worldX < -half || worldX > half || worldZ < -half || worldZ > half) return false;

    float fx = (worldX + half) / _spacing;
    float fz = (worldZ + half) / _spacing;
    int x0 = Mathf.Clamp((int)fx, 0, _res - 2);
    int z0 = Mathf.Clamp((int)fz, 0, _res - 2);
    float tx = fx - x0, tz = fz - z0;
    float h00 = H(x0, z0), h10 = H(x0 + 1, z0), h01 = H(x0, z0 + 1), h11 = H(x0 + 1, z0 + 1);
    float h = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);

    // central-difference normal (same form as the shader vertex() normal)
    float hl = H(x0 - 1 < 0 ? 0 : x0 - 1, z0), hr = H(x0 + 1, z0);
    float hd = H(x0, z0 - 1 < 0 ? 0 : z0 - 1), hu = H(x0, z0 + 1);
    var n = new Vector3(hl - hr, 2f * _spacing, hd - hu).Normalized();

    // density: slope/height heuristic until Unit 4 bakes a scatterDensity channel.
    // pebbles like gentle, low-ish ground; reject steep faces.
    float slope = 1f - n.Y;
    float density = Mathf.Clamp(1f - slope * 2.2f, 0f, 1f);
    sample = new ScatterSample(new Vector3(worldX, h, worldZ), n, density);
    return true;
}

private float H(int x, int z)
{
    x = Mathf.Clamp(x, 0, _res - 1); z = Mathf.Clamp(z, 0, _res - 1);
    return _heights![z * _res + x];
}
```
> **Coordination-with-flora:** This heuristic `density` is the FALLBACK. When Unit 4 bakes `scatterDensity` AND the flora subsystem wants the real value, route the density through the same baked source (read the data texture CPU-side, or have flora sample `groundData` on the GPU). Keep the *shape* of `ScatterSample` identical so flora and pebbles place against one contract. Mark this in code with a `// FLORA-COORD:` comment.

- [ ] **Step 3: Minimal pebble/debris MultiMesh demo** consuming the interface. Create `scripts/lab/PebbleScatter.cs`:

```csharp
using Godot;

namespace WG16.Lab;

/// Minimal pebble/debris scatter — PROVES IScatterProvider. CPU placement (no per-frame
/// compute) on purpose: it's a demo, not the flora system. The flora subsystem (built
/// elsewhere) is the real consumer; it may own a GPU-compute placement pass instead.
/// FLORA-COORD: if flora ships its own scatter, delete this node — do not run both.
public partial class PebbleScatter : MultiMeshInstance3D
{
    private IScatterProvider _provider = null!;
    private bool _on = true;
    private int _count = 4000;
    private float _scale = 0.6f;

    public void Init(IScatterProvider provider) { _provider = provider; Rebuild(); }
    public void SetEnabled(bool on) { _on = on; Visible = on; }
    public void SetCount(int c) { _count = Mathf.Clamp(c, 0, 60000); Rebuild(); }
    public void SetScale(float s) { _scale = s; Rebuild(); }

    private void Rebuild()
    {
        if (_provider == null) return;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = MakePebbleMesh(),
        };

        // Deterministic Poisson-ish dart-throwing against the provider's density.
        var rng = new RandomNumberGenerator { Seed = (ulong)_provider.ScatterSeed };
        float half = _provider.RegionHalfExtentM;
        var xforms = new System.Collections.Generic.List<Transform3D>(_count);
        int attempts = _count * 4, made = 0;
        for (int i = 0; i < attempts && made < _count; i++)
        {
            float x = rng.RandfRange(-half, half), z = rng.RandfRange(-half, half);
            if (!_provider.TryScatterSurface(x, z, out var s)) continue;
            if (rng.Randf() > s.Density) continue;                 // density-weighted accept
            // align pebble's up to the surface normal, random yaw + size jitter
            var basis = AlignUp(s.Normal) * new Basis(Vector3.Up, rng.RandfRange(0f, Mathf.Tau));
            float sc = _scale * rng.RandfRange(0.5f, 1.4f);
            xforms.Add(new Transform3D(basis.Scaled(new Vector3(sc, sc, sc)), s.Position));
            made++;
        }

        mm.InstanceCount = made;
        for (int i = 0; i < made; i++) mm.SetInstanceTransform(i, xforms[i]);
        Multimesh = mm;
        GD.Print($"PebbleScatter: placed {made}/{_count} pebbles");
    }

    private static Basis AlignUp(Vector3 up)
    {
        up = up.Normalized();
        Vector3 t = Mathf.Abs(up.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        Vector3 x = t.Cross(up).Normalized();
        Vector3 z = up.Cross(x).Normalized();
        return new Basis(x, up, z);
    }

    private static Mesh MakePebbleMesh()
    {
        // squashed low-poly sphere stand-in for a pebble (debris asset swaps in later).
        var s = new SphereMesh { Radius = 0.5f, Height = 0.6f, RadialSegments = 6, Rings = 3 };
        var m = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.40f, 0.37f), Roughness = 0.95f };
        s.Material = m;
        return s;
    }
}
```

- [ ] **Step 4: Wire the demo into the lab** as a sibling of `TerrainLab`. In `scripts/lab/TerrainLabUI.cs`, after the terrain is built (where `_terrain` is available), add:

```csharp
private PebbleScatter? _pebbles;

private void EnsurePebbleScatter()
{
    if (_pebbles != null) return;
    _pebbles = new PebbleScatter { Name = "PebbleScatter" };
    _terrain.GetParent().AddChild(_pebbles);    // sibling of TerrainLab (shares world space)
    _pebbles.Init(_terrain);                     // TerrainLab implements IScatterProvider
}
```
Call `EnsurePebbleScatter()` from the same place the splat bake/apply runs after `Build()`. Hook the registry toggle (Step 6) to `_pebbles?.SetEnabled(...)`, count → `SetCount`, scale → `SetScale`.

- [ ] **Step 5: Build + import.**
  `dotnet build WG16.csproj` → 0 errors. Then headless `--import` → no GLSL errors (shader untouched in Group A; this is a C#-only group, but run import to confirm the project loads).

- [ ] **Step 6: Registry — Ground+ tab.** In `data/lab_controls.json`, add `"Ground+"` to the `tabs` array, then add rows:
```json
    { "id": "pebble_on", "label": "pebbles (demo)", "tab": "Ground+", "type": "toggle", "default": false, "rand": false },
    { "id": "pebble_count", "label": "pebble count", "tab": "Ground+", "type": "slider", "min": 0, "max": 30000, "default": 4000, "rand": false },
    { "id": "pebble_scale", "label": "pebble scale", "tab": "Ground+", "type": "slider", "min": 0.1, "max": 2.0, "default": 0.6, "rand": false },
```
> **Audit H1 — these rows DELIBERATELY have NO `"param"`** (they're not shader uniforms). A `toggle`/`slider` with a `param` would route to `SetShaderParameter("pebble_*")` and **silently no-op** (no such uniform); with no `param` the generic case does nothing either. So they MUST be special-cased by `id` in `ApplyControl` (TerrainLabUI.cs) BEFORE the generic routing — this is mandatory wiring, not optional:
> ```csharp
>     // Unit 6A pebble demo controls (id-routed, not shader uniforms):
>     if (c.Id == "pebble_on")    { _pebbles?.SetEnabled(c.Value.AsBool());  return; }
>     if (c.Id == "pebble_count") { _pebbles?.SetCount(c.Value.AsInt32());   return; }
>     if (c.Id == "pebble_scale") { _pebbles?.SetScale(c.Value.AsSingle());  return; }
> ```
> (`_pebbles` is the `PebbleScatter` instance created in Group A. Put these checks at the top of `ApplyControl`, mirroring how cloud `cloud`-typed ids are handled.) Validate JSON: `python -c "import json;json.load(open(r'data/lab_controls.json'));print('ok')"`.

- [ ] **Step 7: Build + A/B + profile.**
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed>" --path . --rendering-driver vulkan scenes/terrain_lab.tscn -- "--cam=0,40,80,-20,0" "--clouds=0" --profile=3
```
Toggle `pebbles (demo)` on; confirm pebbles sit ON the surface, follow normals, thin out on steep slopes (density working), and re-`--profile`. MultiMesh draw is one draw call — cost should be tiny.

- [ ] **Step 8: Commit.**
```bash
git add scripts/lab/IScatterProvider.cs scripts/lab/PebbleScatter.cs scripts/lab/TerrainLab.cs scripts/lab/TerrainLabUI.cs data/lab_controls.json
git commit -m "Ground unit 6A: IScatterProvider seam + pebble/debris MultiMesh demo (flora plugs in here)"
```

- [ ] **Step 9 (DEFERRED, optional, flora-owned): GPU-compute placement.** Per-instance placement from a density map is embarrassingly parallel — a compute pass (SplatCompute-style local-RD bake of instance transforms, or render-thread dispatch writing a `MultiMesh` buffer) is the AAA upgrade. **Do NOT build it speculatively here.** It belongs to whoever owns scatter at scale (likely flora). If pebble count needs to reach 100k+ live, lift placement into compute then, keeping `IScatterProvider` as the density source.

---

## Group B — Wetness / puddles (consumes Unit 4 cavity + flow masks)

**DEPENDS ON UNIT 4.** Water pools in concave/low-flow areas: lower ROUGHNESS, darken ALBEDO, raise specular response; optional flat puddle planes via a raised roughness floor.

**Files:**
- Modify `shaders/terrain_lab.gdshader`
- Modify `data/lab_controls.json` (Ground+ tab)

- [ ] **Step 1: Uniforms** after the AR uniforms (~line 36):
```glsl
// --- Unit 6B: wetness / puddles (consumes Unit 4 cavity+flow masks) ----------
uniform bool  wet_on = false;
uniform float wet_amount   : hint_range(0.0, 1.0) = 0.6;   // global wetness master
uniform float wet_cavity_k : hint_range(0.0, 4.0) = 2.0;   // how strongly cavity pools water
uniform float wet_flow_dry : hint_range(0.0, 1.0) = 0.7;   // high flow drains (stays dry)
uniform float wet_darken   : hint_range(0.0, 1.0) = 0.45;  // albedo darken in puddles
uniform float wet_rough    : hint_range(0.0, 0.4) = 0.06;  // wet roughness floor (mirror-ish)
uniform float puddle_level : hint_range(0.0, 1.0) = 0.0;   // 0=off; >0 = flat puddle threshold
```

- [ ] **Step 2: Wetness block** in `fragment()`, AFTER the contact block (~after line 517), BEFORE the `ALBEDO=`/`ROUGHNESS=` writes. It reads Unit 4's masks via `groundData`:
```glsl
    // --- Unit 6B: wetness / puddles -----------------------------------------
    // Water collects where the surface is concave (cavity) and not draining (low flow).
    if (wet_on){
        GroundData gd = groundData(v_uv);          // Unit 4 baked masks (one fetch)
        float pool = clamp(gd.cavity * wet_cavity_k, 0.0, 1.0);
        pool *= (1.0 - gd.flow * wet_flow_dry);      // fast flow stays dry
        float wet = clamp(pool * wet_amount, 0.0, 1.0);
        // puddle "plane": above a cavity threshold, force full wet (flat water look).
        if (puddle_level > 0.0){
            float puddle = smoothstep(puddle_level, puddle_level + 0.06, gd.cavity);
            wet = max(wet, puddle * wet_amount);
        }
        // wet ground: darker albedo, much lower roughness (sharper specular), slight
        // specular lift comes for free from the lower roughness under Godot's GGX.
        alb *= mix(1.0, 1.0 - wet_darken, wet);
        rgh  = mix(rgh, wet_rough, wet);
    }
```
> If Unit 4 named the fields differently, alias once here (e.g. `float cav = gd.cavity;`) and note it; nothing else in this block changes.

- [ ] **Step 3: Build + import.** `dotnet build` + headless `--import`. Expected: compiles clean (requires Unit 4's `groundData`/`GroundData` already in the file — if `--import` errors with "groundData not declared", Unit 4 has not landed; STOP and do not stub it).

- [ ] **Step 4: Registry (Ground+ tab).**
```json
    { "id": "wet_on", "label": "wetness", "tab": "Ground+", "type": "toggle", "param": "wet_on", "default": false, "rand": false },
    { "id": "wet_amount", "label": "wetness amt", "tab": "Ground+", "type": "slider", "param": "wet_amount", "min": 0, "max": 1, "default": 0.6, "rand": false },
    { "id": "wet_cavity_k", "label": "wet pool k", "tab": "Ground+", "type": "slider", "param": "wet_cavity_k", "min": 0, "max": 4, "default": 2.0, "rand": false },
    { "id": "wet_flow_dry", "label": "wet flow dry", "tab": "Ground+", "type": "slider", "param": "wet_flow_dry", "min": 0, "max": 1, "default": 0.7, "rand": false },
    { "id": "wet_darken", "label": "wet darken", "tab": "Ground+", "type": "slider", "param": "wet_darken", "min": 0, "max": 1, "default": 0.45, "rand": false },
    { "id": "wet_rough", "label": "wet rough floor", "tab": "Ground+", "type": "slider", "param": "wet_rough", "min": 0, "max": 0.4, "default": 0.06, "rand": false },
    { "id": "puddle_level", "label": "puddle plane", "tab": "Ground+", "type": "slider", "param": "puddle_level", "min": 0, "max": 1, "default": 0.0, "rand": false },
```
Validate JSON.

- [ ] **Step 5: Build + A/B + profile** (clouds off, low sun-grazing cam shows specular best):
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed>" … scenes/terrain_lab.tscn -- "--cam=0,30,120,-8,0" "--clouds=1" --auto-shot=C:/tmp/wet_off.png
```
Then `wet_on` true, `--auto-shot=C:/tmp/wet_on.png`. Confirm puddles read in concave/low areas (not on ridges/steep faces), specular highlight appears, no full-screen darken. `--profile` delta should be ~1 extra texture fetch + ALU.

- [ ] **Step 6: Commit.**
```bash
git add shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Ground unit 6B: wetness/puddles from Unit 4 cavity+flow (darken+low-rough, optional puddle plane)"
```

---

## Group C — Snow accumulation by aspect (SUPERSEDES the primitive snow_dust)

**DEPENDS ON UNIT 4 (aspect mask).** Snow accumulates on up-facing + pole-facing slopes, melts on steep + sun-facing slopes. Replaces the existing `snow_dust_amp`/`snow_dust_h` dusting (~lines 512-516).

**Files:**
- Modify `shaders/terrain_lab.gdshader`
- Modify `data/lab_controls.json` (Ground+ tab)

- [ ] **Step 1: Uniforms** after the wetness uniforms:
```glsl
// --- Unit 6C: snow by aspect (supersedes snow_dust) --------------------------
uniform bool  snow_on = false;
uniform float snow_amount   : hint_range(0.0, 1.0) = 0.5;
uniform float snow_height_m : hint_range(0.0, 2000.0) = 600.0;  // snowline base
uniform float snow_height_soft : hint_range(20.0, 600.0) = 200.0;
uniform float snow_slope_max : hint_range(0.05, 0.7) = 0.35;    // steeper than this sheds snow
uniform float snow_aspect_pole : hint_range(0.0, 1.0) = 0.5;    // pole-facing azimuth (max retain)
uniform float snow_aspect_k  : hint_range(0.0, 1.0) = 0.4;      // aspect influence (0=ignore)
uniform vec3  snow_color = vec3(0.92, 0.95, 1.0);
```

- [ ] **Step 2: REMOVE whatever snow-dust logic survives Unit 4** (audit H2 — do NOT trust the line numbers). Unit 4 reworks the `contact_on` block before this unit runs (it demotes contact shading to a `!breakup_on` fallback), so the `snow_dust` lines may have moved, been re-indented, or been gated. **Find them by content** (`grep -n "snow_dust" shaders/terrain_lab.gdshader`) and delete the dusting block wherever it now lives — originally these lines (and any `breakup_on`-gated copy Unit 4 left):
```glsl
        // snow dusting on near-flat upward faces above a height.
        float dust = snow_dust_amp * smoothstep(snow_dust_h, snow_dust_h+120.0, v_h)
                                   * (1.0 - smoothstep(0.18, 0.42, v_slope));
        alb = mix(alb, vec3(0.92,0.94,0.97), dust);
        rgh = mix(rgh, 0.6, dust);
```
Delete those 5 lines (and, if `snow_dust_amp`/`snow_dust_h` uniforms are now unused, remove them too — grep first). The contact block keeps fold-darken + slope-wear.

- [ ] **Step 3: Snow-by-aspect block** in `fragment()`, after the wetness block, before the writes:
```glsl
    // --- Unit 6C: snow accumulation by aspect -------------------------------
    // Accumulates above a snowline, on gentle (up-facing) slopes, biased toward the
    // pole-facing aspect; sheds on steep + sun/equator-facing faces.
    if (snow_on){
        GroundData gd = groundData(v_uv);                  // aspect/slope from Unit 4
        float hgate  = smoothstep(snow_height_m, snow_height_m + snow_height_soft, v_h);
        float upface = 1.0 - smoothstep(0.0, snow_slope_max, gd.slope);   // flat=1, steep=0
        // aspect: 1 when facing the pole azimuth, falling off to 0 at the opposite side.
        float adir   = 1.0 - abs(gd.aspect - snow_aspect_pole) * 2.0;     // [-1..1] → fold
        float aspectw = mix(1.0, clamp(adir, 0.0, 1.0), snow_aspect_k);
        float snow = clamp(snow_amount * hgate * upface * aspectw, 0.0, 1.0);
        alb = mix(alb, snow_color, snow);
        rgh = mix(rgh, 0.55, snow);                        // snow ~ matte-ish, not mirror
        // (optional) flatten normal slightly under deep snow — left out to keep it cheap.
    }
```

- [ ] **Step 4: Build + import.** `dotnet build` + headless `--import`. Clean compile (needs Unit 4 `groundData`). If old `snow_dust_*` references remain elsewhere, fix or `--import` will error.

- [ ] **Step 5: Registry (Ground+ tab).**
```json
    { "id": "snow_on", "label": "snow (aspect)", "tab": "Ground+", "type": "toggle", "param": "snow_on", "default": false, "rand": false },
    { "id": "snow_amount", "label": "snow amt", "tab": "Ground+", "type": "slider", "param": "snow_amount", "min": 0, "max": 1, "default": 0.5, "rand": false },
    { "id": "snow_height_m", "label": "snowline m", "tab": "Ground+", "type": "slider", "param": "snow_height_m", "min": 0, "max": 2000, "default": 600, "rand": false },
    { "id": "snow_slope_max", "label": "snow slope max", "tab": "Ground+", "type": "slider", "param": "snow_slope_max", "min": 0.05, "max": 0.7, "default": 0.35, "rand": false },
    { "id": "snow_aspect_pole", "label": "snow pole azim", "tab": "Ground+", "type": "slider", "param": "snow_aspect_pole", "min": 0, "max": 1, "default": 0.5, "rand": false },
    { "id": "snow_aspect_k", "label": "snow aspect k", "tab": "Ground+", "type": "slider", "param": "snow_aspect_k", "min": 0, "max": 1, "default": 0.4, "rand": false },
```
Validate JSON. (Remove any leftover `snow_dust_*` registry rows from the old dusting.)

- [ ] **Step 6: Build + A/B + profile** (cam looking at varied slopes/heights):
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed>" … scenes/terrain_lab.tscn -- "--cam=0,200,400,-25,0" "--clouds=1" --auto-shot=C:/tmp/snow_off.png
```
Then `snow_on` true → `C:/tmp/snow_on.png`. Confirm snow sits on gentle high ground + the pole-facing side, sheds on steep faces, respects the snowline (set `snow_aspect_k=0` to verify the aspect term by isolating it). `--profile` delta ~1 fetch + ALU. Verify it reads BETTER than the old dust (it should, with directional bias).

- [ ] **Step 7: Commit.**
```bash
git add shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Ground unit 6C: snow-by-aspect (height+slope+pole-facing), supersedes snow_dust"
```

---

## Group D — Higher-quality triplanar (no Unit 4 dependency)

Upgrade the plain 3-plane `tri_w` blend with sharper, energy-correct weights and an optional biplanar (2-tap) mode for cost. Minor, self-contained — touches `tri_w` / the triplanar accumulation only.

**Files:**
- Modify `shaders/terrain_lab.gdshader`
- Modify `data/lab_controls.json` (Ground+ tab)

- [ ] **Step 1: Uniforms** (near `tri_sharpness`):
```glsl
// --- Unit 6D: higher-quality triplanar ---------------------------------------
uniform int   tri_quality = 0;   // 0 = current 3-plane, 1 = sharpened weights, 2 = biplanar (2-tap)
uniform float tri_bias : hint_range(1.0, 16.0) = 4.0;  // extra weight sharpening for mode 1/2
```

- [ ] **Step 2: Upgrade `tri_w`** (~line 236) to a quality-switched form. Mode 0 keeps current behavior exactly (zero regression when off); mode 1 sharpens + normalizes harder (less muddy seams); mode 2 drops the smallest plane (biplanar — 2 taps instead of 3, the cost win):
```glsl
vec3 tri_w(vec3 nr){
    vec3 bw = pow(abs(nr), vec3(tri_sharpness));
    if (tri_quality == 0) return bw / (bw.x + bw.y + bw.z);   // unchanged baseline
    // sharpen toward the dominant plane (cleaner seams, less cross-bleed)
    bw = pow(bw, vec3(tri_bias));
    if (tri_quality == 2){
        // biplanar: zero the smallest contributor → 2 effective taps where it matters
        float mn = min(bw.x, min(bw.y, bw.z));
        bw -= vec3(mn);                       // smallest → 0, keep two
    }
    float s = bw.x + bw.y + bw.z;
    return bw / max(s, 1e-5);
}
```
> Because the smallest weight goes to ~0 in mode 2, its `ar_sample_wp` tap contributes ~0 and the existing per-plane fetches are wasted but harmless. To actually SAVE the fetch you'd branch per-plane in `tp_*`/`s_*`; that's a larger change — only do it if `--profile` shows triplanar fetches are the bottleneck. Default `tri_quality=0` so nothing changes until chosen.

- [ ] **Step 3: Build + import.** `dotnet build` + headless `--import` → clean compile.

- [ ] **Step 4: Registry (Ground+ tab).**
```json
    { "id": "tri_quality", "label": "triplanar q", "tab": "Ground+", "type": "enum", "param": "tri_quality", "options": ["3-plane (base)", "sharpened", "biplanar (2-tap)"], "default": 0, "rand": false },
    { "id": "tri_bias", "label": "triplanar bias", "tab": "Ground+", "type": "slider", "param": "tri_bias", "min": 1, "max": 16, "default": 4, "rand": false },
```
Validate JSON.

- [ ] **Step 5: Build + A/B + profile.** Capture on a cliff/steep area (triplanar seams show on near-vertical faces):
```
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null
dotnet build WG16.csproj
"<windowed>" … scenes/terrain_lab.tscn -- "--cam=0,60,60,-10,0" "--clouds=0" --auto-shot=C:/tmp/tri0.png
```
Then cycle `triplanar q` 0→1→2 (`tri1.png`/`tri2.png`). Confirm mode 1 tightens seams on cliffs without new banding; mode 2 looks ~same with equal-or-better `--profile`. THE gate: do cliffs read cleaner in motion?

- [ ] **Step 6: Commit.**
```bash
git add shaders/terrain_lab.gdshader data/lab_controls.json
git commit -m "Ground unit 6D: higher-quality triplanar (sharpened weights + biplanar mode, default off)"
```

---

## Self-Review notes

- **Spec coverage:** Implements arc-spec Unit 6 in full — (A) detail-mesh scatter hooks via a clean interface + pebble/debris demo, (B) wetness/puddles, (C) snow-by-aspect, (D) higher-quality triplanar. Built LAST, only after Units 1-5 are judged good (stated up top). Each is its own self-contained, independently shippable task-group with its own commit + toggle, matching the "menu of optional AAA extras" mandate.
- **Placeholder scan:** No TBDs. The only non-shader-uniform registry ids (`pebble_on/count/scale`) are explicitly flagged as UI-only (routed to `PebbleScatter`, not `SetShaderParameter`). The compute-scatter upgrade (A Step 9) is explicitly DEFERRED + flora-owned, not stubbed — no speculative compute added. The old `snow_dust_*` is explicitly REMOVED (C Step 2), not left dangling.
- **Dependency notes (Unit 4 masks):** **B and C HARD-DEPEND on Unit 4's `groundData()`** — B reads `cavity`+`flow`, C reads `aspect`+`slope`. Both steps say STOP if `--import` reports `groundData` undeclared (Unit 4 not landed). **A degrades gracefully** (slope/height heuristic density; consumes Unit 4 `scatterDensity` when available). **D has NO Unit 4 dependency** (touches `tri_w` only). So B/C are gated on Unit 4; A and D can ship even if Unit 4 slips.
- **Flora-overlap boundary:** Group A builds ONLY the `IScatterProvider` seam + a throwaway pebble demo. Flora is NOT built here. Two `// FLORA-COORD:` markers call out where flora plugs in (density source + delete-the-demo-if-flora-ships). The interface (`ScatterSample` = pos+normal+density) is the single contract both consume — no duplicated placement logic.
- **Interface consistency:** `groundData(uv) → GroundData{...cavity, flow, aspect, slope, scatterDensity}` consumed identically in B and C (one fetch each, field names aliased-once if Unit 4 differs). `IScatterProvider`/`ScatterSample` defined in A Step 1, implemented by `TerrainLab` (A Step 2), consumed by `PebbleScatter` (A Step 3) — same struct shape throughout. `tri_w(vec3)` signature unchanged (D upgrades the body, mode 0 = exact current behavior, zero regression). All registry `param` ids match shader uniforms exactly except the flagged UI-only `pebble_*`. New controls all land on one new **"Ground+"** tab.
- **Audit fixes applied:** H1 — pebble rows now have NO `"param"` and are id-routed in `ApplyControl` to `_pebbles.SetEnabled/SetCount/SetScale` with explicit code (a `param` would silently no-op; the prose-only note was insufficient). H2 — Group C Step 2 reframed from a verbatim line-512-516 delete to "grep `snow_dust` and remove whatever survives Unit 4" (Unit 4 reworks that contact block first, so the lines move). M3 — when both B and C are on they each fetch `groundData(v_uv)`; acceptable (cheap, and they're independent task-groups), but if profiled hot, hoist one shared `GroundData gd = groundData(v_uv);` guarded by `wet_on || snow_on`.
- **Cross-cutting (all ground units):** find seam functions by NAME/content not line number; `param` rows silently no-op if the uniform is missing — Unit 6's non-uniform controls (`pebble_*`) are correctly id-routed instead. Note `_spacing = region/res` (not `region/(res-1)`) in the scatter lookup mirrors the shader's own normal convention — intentional, fine for a density heuristic.
- **Watch-points:** (1) `groundData`/`GroundData` MUST exist (Unit 4) before B/C compile — enforced by the import-error STOP. (2) `tri_quality=2` zeroes a tap's weight but still issues the fetch; real fetch-saving needs per-plane branching in `tp_*`/`s_*` — only pursue if profiled as the bottleneck (noted in D Step 2). (3) `pebble_*` controls are id-routed in the UI apply switch (not `SetShaderParameter`) — code in A Step 6 / Step 6 registry note. (4) PebbleScatter must be a SIBLING of `TerrainLab` (shared centered world space), not a child of the displaced mesh.
