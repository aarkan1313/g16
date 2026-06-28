# WG17 Terrain Surfacing (Design Spec)

**Date:** 2026-06-28
**Target repo:** `C:\Wg16\WG17\terrainengine-10k`
**Reference:** WG16 `C:\Wg16\wg-16-project` + surfacing post-mortem
`docs/handoffs/2026-06-28-terrain-surfacing-AUDIT-REPORT.md` (memory `ground-surfacing-audit-2026-06-28`).
**Depends on:** WG17 terrain slice (SHIPPED — Field+CDLOD, flying 4.2ms, `ground.gdshader` exists). Benefits
from Lighting (Slice A) for correct look, but does not block on the sky stack.
**Scope:** turn the height-color placeholder ground into real PBR materials, placed procedurally by terrain
attributes, blended + anti-tiled for an infinite world, with the two known WG16 look-bugs designed out.

---

## 1. Why This Slice Exists (and why WG16's failed)

WG16 terrain surfacing **was never done** — it was stripped to a height-color placeholder. The post-mortem
found the failure was **organizational, not graphical**:
- The renderer was fed by ONE hardcoded array `_matRoles` (5 named texture slots in `TerrainLab.cs`).
  The palette / library / verdicts / zone UI were ALL disconnected; `ground_palette.json` was dead data.
- Two of the 5 hardwired textures were owner-rated FAIL/DROPPED, incl. a **near-black slope rock force-painted
  on every slope** → the "too dark" look.
- **~108 owner-passing materials sat orphaned** on disk because only 5 slots existed.
- Two graphical bugs made even good materials look bad: an **always-on view-angle relief fade** ("definition
  vanishes when I move the camera") and a **renderer-imposed darkening** (AO_LIGHT_AFFECT=1 crush × near-black
  rock × SSAO).

So WG17 surfacing is **closer to a fresh design than a port.** We have 738 material dirs on disk (each a clean
4-map PBR set: albedo/normal/roughness/ao) with `material_verdicts.json` + `material_library.json` already
curating the ~108 winners. The assets were never the problem — the binding system was.

**Design principle (the spine):**
> Material placement is DATA, not code. The renderer reads a texture ARRAY indexed by rule-driven weights —
> never a hardcoded slot list. A material's darkness is the material's own; the renderer never imposes it.
> Surface relief never fades with camera angle.

---

## 2. Success Criteria

1. **Believable PBR ground** — grass in low/flat, rock on steep, snow up high — chosen procedurally from
   terrain attributes, with **soft blended transitions** (no hard bands).
2. **No obvious tiling** across the infinite world (macro-variation breakup), and **no stretched textures on
   cliffs** (triplanar on steep slopes).
3. **The two WG16 bugs are designed out:**
   - **Stable relief:** normal-map strength does NOT fade with view angle. Pitching down / moving the camera
     forward→down does NOT make surface definition vanish.
   - **No renderer darkening:** no AO_LIGHT_AFFECT crush, no near-black material force-painted on slopes.
     Slope darkness beyond correct sun-angle shading is a bug.
4. **Data-driven & fast to tune** — placement rules + palette live in JSON; eye-gate tuning is edit-JSON +
   relaunch, no recompile.
5. **In budget** — terrain is at 4.2ms; surfacing's per-fragment cost (top-2 blend × slope triplanar +
   macro-variation) stays well within frame budget.

**Non-goals (YAGNI):** biomes (rules are biome-READY via an optional input, but no biome system this slice);
splat/painted maps; stochastic hex-tile anti-tiling; water-wetness / snow-accumulation dynamics; rebuilding
the old verdicts/library judging UI (we READ the curated JSON, we don't rebuild the UI).

---

## 3. Architecture & File Layout

```
src/surfacing/
├── SurfacePalette.cs    # loads the curated material set into Texture2DArrays (albedo/normal/roughness/ao,
│                        #   one array each, sampled by layer index). Each entry = {dir, name, tiling_m,
│                        #   tint}. NOT a hardcoded slot list. Sourced from data/surface_palette.json.
├── SurfaceRules.cs      # the placement rule table: (height, slope[, biome]) → per-layer blend weights.
│                        #   Pure data/logic; thresholds + blend widths from data/surface_rules.json.
└── TerrainSurfacer.cs   # the ONE class that binds the texture arrays + rule/tiling/macro uniforms to
                         #   ground.gdshader. Replaces the role of WG16's LoadGroundMaterials().

data/
├── surface_palette.json # curated starter set (drawn from the ~108 passing materials; cite verdicts)
└── surface_rules.json   # height/slope → material thresholds + blend widths + macro/triplanar knobs

shaders/
└── ground.gdshader (MODIFY)  # the surfacing fragment path replacing the height-color placeholder

src/surfacing/checks/
└── SurfaceCheck.cs      # asserts palette loaded N>placeholder materials + relief-view-fade is OFF
```

**Layering rules:**
- `SurfacePalette` / `SurfaceRules` are data/logic — no scene writes beyond building the texture arrays.
- `TerrainSurfacer` is the only class that sets material uniforms on `ground.gdshader`.
- Placement is JSON-driven; adding/swapping a material edits JSON, never code.

**The texture-array approach (the anti-WG16):** N palette materials load into 4 `Texture2DArray`s
(albedo/normal/roughness/ao), N layers each. The shader samples `texture(albedo_arr, vec3(uv, layer))` for the
chosen layers and blends — so the material count is data, not 5 hardwired `sampler2D`s. Start with N≈8–12
curated layers (covers grass/dirt/clay/scree/rock/bedrock/snow/sand); the array can grow without code change.

---

## 4. Material Placement (SurfaceRules)

A small rule table maps terrain attributes → blend weights. Per fragment, evaluate height + slope, pick the
top-2 (optionally top-3) layers by weight, and blend. Example rule shape in `surface_rules.json`:

```jsonc
{
  "rules": [
    { "layer": "grass",   "height": [0, 400],     "slope": [0, 25],  "weight": 1.0 },
    { "layer": "dirt",    "height": [0, 600],     "slope": [15, 40], "weight": 0.8 },
    { "layer": "rock",    "height": [0, 9000],    "slope": [35, 90], "weight": 1.0 },  // steep = rock anywhere
    { "layer": "scree",   "height": [400, 1500],  "slope": [30, 55], "weight": 0.7 },
    { "layer": "snow",    "height": [1400, 9000], "slope": [0, 45],  "weight": 1.0 }
  ],
  "blend_width_m": 80,        // soft height transition band
  "slope_blend_deg": 8,       // soft slope transition band
  "macro": { "scale_m": 1200, "strength": 0.35 },   // anti-tiling macro variation
  "triplanar": { "slope_on_deg": 38, "sharpness": 6 } // slope-gated triplanar
}
```

- **Height + slope** come from the fragment's world Y and the geometric normal (already available in
  `ground.gdshader`). **Biome** is an OPTIONAL future input the rule evaluator accepts but ignores this slice.
- **Soft transitions:** weights ramp across `blend_width_m` / `slope_blend_deg` so there are no hard lines.
- Rules are evaluated cheaply; the shader only samples the chosen top-2 layers (not all N).

---

## 5. Shader Path (ground.gdshader surfacing — correctness baked in)

The fragment path replacing the height-color placeholder:
1. **Compute weights** from height+slope per §4; select top-2 layers + their blend factor.
2. **Sample** albedo/normal/roughness/ao for the 2 layers from the texture arrays at the layer's `tiling_m`.
3. **Triplanar on slopes:** where slope > `slope_on_deg`, sample triplanar (3-axis projection blended by
   normal) instead of top-down UV, so cliffs don't stretch. Slope-gated so flat ground keeps the cheap path.
4. **Macro-variation anti-tiling (always on):** modulate albedo brightness/tint by a low-frequency world-space
   noise (`scale_m` ~1200 m) at `strength`, and optionally blend two UV scales — so the tile repeat dissolves
   at distance. NOT view-dependent.
5. **Blend** the 2 layers by weight → final ALBEDO / NORMAL / ROUGHNESS / AO.

**The two bug-fix rules, structural (not just tuned):**
- **NO view-angle relief fade.** WG16 rolled normal-map strength off by `footprint=length(fwidth(uv))` (a
  view-angle term) → relief vanished as you turned. WG17 does NOT do this. Normal strength may roll off by
  *distance* (mip/LOD) but NEVER by view angle. `SurfaceCheck` asserts this term is absent/disabled.
- **NO renderer darkening.** `AO_LIGHT_AFFECT` stays at a sane low value (not 1.0); the palette excludes
  near-black "rock" mis-assets; a material's albedo darkness is its own. The renderer never multiplies the
  surface toward black based on slope.

---

## 6. The Starter Palette (data/surface_palette.json)

Curated from the ~108 owner-passing materials (cross-reference `material_verdicts.json`). ~8–12 layers
covering the common surface classes, e.g.:
- low/flat: a grassland, a dirt/soil, a sun-baked clay
- mid/slope: a fine scree, a weathered grey bedrock (NOT the near-black rock)
- high: a fresh powder / firn snow
- arid/sand: a fine sand
Each entry lists its asset dir + a `tiling_m` (world metres per tile) + an optional `tint`. The exact picks
are an eye-gate tuning decision (edit JSON); the spec fixes the STRUCTURE, not the final list. **Explicitly
avoid the WG16 fail/dropped assets** (biome_grassland=fail, rock035=dropped/near-black).

---

## 7. Testing & Eye-Gate

- **`SurfaceCheck`** (`--surfacecheck`): asserts the palette loaded N (>placeholder) materials into the texture
  arrays (not a hardcoded slot fallback); asserts the shader's **view-angle relief-fade term is OFF** (a
  uniform/flag check — structurally disabled, not just low). Prints `SURFACE PASS materials=N relief-view-fade=OFF`.
- **Eye-gate (manual, in motion) — the primary gate:**
  - Fly: grass low/flat, rock steep, snow high; transitions soft, not banded.
  - Distance: no obvious tiling (macro-variation); cliffs: no stretched textures (triplanar).
  - **Bug test 1 (relief):** pitch down / move forward→down at a fixed spot → surface definition STAYS. The
    exact WG16 "definition vanishes when I move" check.
  - **Bug test 2 (darkening):** slopes are not unexplainably dark beyond correct sun-angle shading.
- **Tuning loop:** edit `surface_rules.json` / `surface_palette.json`, relaunch — no recompile.
- **Profile:** frame ms surfacing-on vs placeholder; confirm in budget (top-2 blend × slope triplanar).

---

## 8. Dependencies, Risks, Constraints

- **Depends on:** the shipped terrain slice (`ground.gdshader`, height/normal available per fragment). Lighting
  (Slice A) makes the look correct but isn't a build blocker — surfacing renders under whatever lighting exists.
- **Risk — re-introducing the relief fade:** the single most likely regression. Mitigated by the structural
  rule (distance-only rolloff) + `SurfaceCheck` asserting the view-angle term is off.
- **Risk — palette picks look bad:** mitigated by sourcing only from owner-passing assets + JSON tuning loop.
- **Risk — triplanar cost on slopes:** slope-gated so only steep fragments pay it; profile confirms budget.
- **Build/launch gotchas:** `dotnet build Terrainengine10k.csproj` after every `.cs` edit; absolute `--path`;
  bare `--` for flags; texture-array import needs mipmaps ON (avoid the WG16 mipmap-fuzz gotcha).
- **Asset import:** the 738 dirs are WG16-side; copy the curated subset into WG17 `assets/materials/` with
  Godot `.import` regenerated (mipmaps on, sRGB for albedo, linear for normal/roughness/ao).

---

## 9. Definition of Done

- Real PBR materials on the WG17 terrain, placed by height/slope rules, soft-blended, macro-variation
  anti-tiled, triplanar on slopes.
- The two WG16 bugs designed out: relief stable under camera motion; no renderer darkening. Eye-gate PASS on
  both explicit bug tests.
- Placement + palette are JSON-driven (no hardcoded slot array); `SurfaceCheck` PASS
  `materials=N relief-view-fade=OFF`.
- Frame ms recorded (surfacing on vs placeholder), in budget.
- Clean focused commits; spec/plan ~500-line files. Migration record updated with the surfacing outcome.
