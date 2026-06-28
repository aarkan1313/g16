# WG16 Ground-Surfacing Audit — Independent Report

**Date:** 2026-06-28
**Scope:** Ground *surface look* only (geometry/CDLOD/lighting/sky considered done).
**Method:** Read the real code; opened the actual texture PNGs; read the `.import` pipeline and
`project.godot`; cross-checked every load-bearing claim with an adversarial second pass (14 agents,
~270 tool calls) plus the author's own firsthand verification. Every claim cites `file:line`.

> **Headline:** The ground looks flat/thin/dark not because of one bug but because of a *stack* of them
> that all push the same direction. The biggest single one is **organizational, not graphical**: the
> renderer is hardwired to 5 textures via a hardcoded array, **two of which the owner personally rated
> `fail`/`dropped`** — including a **near-black rock** that gets force-painted onto every slope. The
> ~108 owner-approved materials and the whole palette/library/verdicts UI are **completely disconnected
> from what renders.** On top of that, the shader **fades surface relief out as a function of view angle**
> (always on), and **two view-dependent darkening systems** (shader AO + screen-space SSAO) stack on the
> dark slope rock. Fix the asset wiring + the relief-fade + the slope rock and the look jumps immediately
> — the existing assets *can* look good.

---

## 1. The real data flow (how a material reaches the GPU)

It's a **single hardcoded path; everything else is dead data.**

```
TerrainLab.LoadGroundMaterials()  ──► mat0..mat4 shader uniforms ──► ground.gdshader fragment()
        ▲
   _matRoles[5]  (HARDCODED string array)
```

- **The only feed:** `scripts/lab/TerrainLab.cs:271-280` defines `_matRoles` (5 names);
  `LoadGroundMaterials()` (`TerrainLab.cs:281-296`) `GD.Load`s each `albedo/normal/roughness/ao.png`
  and binds `mat{i}_alb/_nrm/_rgh/_ao`. Called unconditionally at `TerrainLab.cs:79` in `Build()`.
- **The textured path is the live default** (so this *is* what you see): `use_textures = true`
  (`ground.gdshader:141`); fragment gate at `:488`. (Note the `TerrainLab.cs:79` comment "path itself
  off by default" is **wrong** — the uniform default is `true`.)
- **The palette/library/verdicts/Zone-UI are disconnected — doubly so:**
  1. `ApplyControl` (the *only* push-to-shader dispatcher) has **no `material`/`companion` case**
     (`scripts/lab/TerrainLabUI.Apply.cs:31-62`). Selecting a material could not rebind a uniform even
     if the widget existed.
  2. The widgets don't exist anyway: the `zone_names` array was removed in the 2026-06-21 strip, so
     `LoadRegistry` returns `Array.Empty` (`LabRegistryLoader.cs:67-70`), the zone-expansion loop runs
     **0 times** (`LabRegistryLoader.cs:79-87`), and `data/lab_controls.json` contains **zero**
     `material`/`companion` controls. So no Zone dropdowns are built.
  3. `data/ground_palette.json` (active `alpine_green`) is loaded into C# but only feeds
     `ZoneDefaultMaterialIndex` (`LabRegistryLoader.cs:141-155`), which is only called from the
     never-executed material `BuildRow` case (`TerrainLabUI.Registry.cs:211`). **Dead data.**

**Conclusion:** Selecting a material in the UI **cannot** change what renders. Proven by absence of any
code path from a selection to a `mat{i}_*` rebind. The fix surface area is *one file* (`TerrainLab.cs`)
— grep for `_matRoles`/`LoadGroundMaterials` hits only that file.

---

## 2. Coverage / inventory

| Quantity | Count | Source |
|---|---|---|
| Material dirs on disk | **738** | `ls assets/materials/` |
| Verdict entries | **738** (108 `pass` / 625 `fail` / 5 `dropped`) | `data/material_verdicts.json` |
| Registered in library | **108** (== exactly the `pass` set, verified set-equal) | `data/material_library.json` |
| Referenced by active palette | **7** (`alpine_green`, all `pass`, all in-library) | `data/ground_palette.json:9-12` |
| **Shader material slots** | **5** (`mat0..mat4`) | `ground.gdshader:151-165` |
| **Actually rendered** | **5**, of which **only 3 pass** | `TerrainLab.cs:271-280` |

**Orphaned good assets:** ~108 owner-approved materials are fully wired into library/palette data but
unreachable by the renderer.

### The smoking gun — the 5 rendered slots vs the owner's own ratings

| Slot | Role | Material | **Owner verdict** | In library? |
|---|---|---|---|---|
| mat0 | low/sand | `02_rippled_dunes` | `pass` (`:39`) | yes |
| mat1 | valley grass | **`biome_grassland`** | **`fail`** (`:504`) | **no** |
| mat2 | mid earth | `16_glacial_till` | `pass` (`:361`) | yes |
| mat3 | **slope rock** | **`rock035`** | **`dropped`** (`:600`) | **no** |
| mat4 | peak snow | `01_fresh_powder` | `pass` (`:9`) | yes |

The `TerrainLab.cs:273-277` "PROOF SWAP (2026-06-28)" comment claims it swapped to *"VERIFIED-AAA
isotropic sets… biome_grassland (kills the corduroy) + rock035 (photogrammetry-grade rock)."* **Both
claims are contradicted by the owner's own `material_verdicts.json`.** The author almost certainly meant
**`rock035_derived`** (rated `pass`, `:601`) — a genuinely different file from `rock035` (I confirmed
`cmp` shows the dark `rock_dark` and `rock035_derived` albedos **DIFFER**, so `rock035_derived` is a real
distinct asset, not a dup).

---

## 3. Channel usage (all opened/traced in `ground.gdshader`)

All four channels **are** sampled and applied in the live `dm==0` branch (`:603`):
`ALBEDO=alb`, `ROUGHNESS=rgh_aa`, `NORMAL=normalize(nrm_pert)`, `AO=ao_blend`, `AO_LIGHT_AFFECT=1.0`.

- **Albedo** — `tri_sample` for all 5 (`:530-533, :548`).
- **Roughness** — `tri_sample_r` for all 5 (`:534-537, :549`), then floored by Toksvig.
- **AO** — **now bound and applied** (the old "never used" gap is closed): `mat0_ao..mat4_ao`
  (`TerrainLab.cs:289-293`), blended `:554-559`, written `AO=ao_blend; AO_LIGHT_AFFECT=1.0`. *But:* the
  `ao.png` maps I opened (e.g. `biome_grassland/ao.png`, `rock035/ao.png`) are **near-white, very
  low-contrast** → AO adds diffuse dimming, **not** crisp crevice "definition." It's a weak contributor,
  not the win it was billed as.
- **Normals** — **all 5 applied** (the old mat0+mat1-only bug is fixed): `apply_nrm_tangent` × 5
  (`:573-577`), each scaled `wN/sum * nfade`. **But `nfade` is the problem** — see §5.

**Blend is plain linear** (`(w0·a0+…+w4·a4)/sum`, `:550-551, :559`) over fwidth-soft height bands ×
`(1-rock)` plus a slope-forced rock weight (`:496-506`). There is **no height/depth-aware blend** — soft
linear cross-fades mush adjacent materials together (a classic "clay" softener). A Bridson/height-blend
would keep transitions crisp.

---

## 4. Asset quality — what the textures ACTUALLY look like (images opened)

### Currently rendered (the ones that matter most)

- **`02_rippled_dunes` (sand, mat0)** — *strongly directional.* Diagonal wind-ripple bands; albedo is
  almost **featureless low-contrast tan**, all structure lives in a directional normal. → corduroy source
  on flat valley floors; contributes ~nothing but streaks.
- **`biome_grassland` (grass, mat1)** — *the best of the bunch, but flawed.* Genuine **top-down** grass
  rosettes over dark soil — so it does kill the old vertical-stalk corduroy. **However** it is
  **high-contrast and macro-blobby**: bright-green clumps, dark soil gaps, and recurring **brown circular
  dirt patches**. At a 12 m tile those distinctive blobs **visibly repeat** and the high contrast aliases
  at distance. Rated `fail` by the owner (debatable — it's better than that, but the tiling/contrast
  concern is real).
- **`16_glacial_till` (earth, mat2)** — *doubly flat.* Drab muddy grey-brown albedo (very low tonal
  variance) **and** a near-flat normal map (mostly the flat `128,128,255` baseline; only embedded pebbles
  poke out). Adds almost no perceived relief → prime "thin/flat" contributor.
- **`rock035` (slope rock, mat3)** — **★ the "too dark" culprit.** A **near-BLACK** dark-teal/charcoal
  veined marble (mean luma ≈ 0.09, min hits 0.0). The shader **force-paints this onto every slope**
  above `surf_slope_rock=0.42` (`:502-505`), and it's *always* triplanar (`:548-549`). Its normal map is
  actually the **best in the set** (deep AAA relief) — but that relief is **wasted under a near-black
  albedo**, and the high-frequency normal is a moiré source. Rated `dropped`.
- **`01_fresh_powder` (snow, mat4)** — *healthy.* Bright, isotropic, matched albedo+normal. Not a
  contributor to any complaint. `pass`.

### Best unused swap-ins (owner-rated `pass`, opened and confirmed isotropic + real relief)

| Role | **Recommended swap-in** | Why | Verdict / disk |
|---|---|---|---|
| Rock (mat3) | **`01_weathered_grey_bedrock`** | Isotropic, **correct mid-grey value** (no dark crush), **strongest genuine rock normal** of all candidates | `pass`, on disk |
| Rock alt | `rock035_derived` or `02_shattered_granite` | derived = the *passing* sibling the swap meant to use; granite = mid-value isotropic | `pass`, on disk |
| Dirt/earth (mat2) | **`13_sun_baked_clay`** | genuine-AAA isotropic **deep polygonal cracks** in albedo+normal — exactly the per-pixel shape that survives motion | `pass`, on disk |
| Grass (mat1) | `17_tundra_dwarf` (least-bad isotropic) or **keep `biome_grassland`** | grassland is genuinely the best *content*; the issue is tiling-break, not the asset | both on disk |

> ⚠️ Two `pass`-rated traps to avoid: **`scrub_dense`** has a **flat placeholder normal**
> (near-uniform blue, ~zero relief) — it would render dead-flat; and **`02_berry_shrubs`** is
> stone + bright orange flowers (wrong content, tiles as spots). "pass" is a coarse *usable* flag,
> not a *good-for-ground* endorsement — judge by image, not by verdict.

---

## 5. The owner's #1 pain: "too dark / definition disappears when I move camera from forward to DOWN, and changing view angle changes stuff"

This is **two distinct symptoms with different causes** — the adversarial pass surfaced that the team
had been conflating them. (Neither is the *already-settled* "toward vs away from the sun" directional
lighting, which is correct behavior.)

### Symptom A — "DEFINITION DISAPPEARS as I move/turn / looks as flat as toggling I (unlit)"

**Cause: the normal-map relief is faded out as a function of view angle — and it's ALWAYS ON.**

```glsl
vec2 uv_fp     = v_surf_xz / mat_tiling;                       // :521
float footprint = length(fwidth(uv_fp));                       // :522  ← grows at grazing & distance
float nrm_fp    = smoothstep(nrm_fade_lo, nrm_fade_hi, footprint); // :567  (0.012 → 0.10)
float nfade     = dbg_use_normalmap ? (1.0 - max(dis, nrm_fp)) : 0.0; // :568
... apply_nrm_tangent(..., wN/sum * nfade);                    // :573-577
```

`fwidth` is a **screen-space derivative** → it explodes when the surface is viewed at a **grazing
angle**. As `footprint` crosses `0.10`, `nfade → 0` and **all five normal maps collapse to the smooth
geometric normal** (`apply_nrm_tangent` with `strength=0` returns the bare geometric N, `:422-431`). With
`mat_tiling=12`, full kill happens at ~1.2 surface-m/pixel — reached quickly on any grazing slope or at
mid distance.

Crucially: this `nfade` is **decoupled** from the two "fade" toggles the dev believes turned this off.
With `footprint_fade=false` (`:125`) and `detail_fade_on=0.0` (`:120`), the **albedo stays sharp** but
the **relief still vanishes**. That asymmetry — *sharp color, zero 3D shape* — is exactly the "painted-on
/ like clay / as flat as lighting-off" read. The shader author's own `detail_demo` comment admits it:
*"The I-test proved the bare albedo is nearly flat → almost all definition is sun+geometry, the texture
carries ~nothing"* (`:446-449`). The Toksvig roughness floor (`:581-583`) piles on — it's also
footprint-driven (mip-filtered `texture()`), so roughness rises and specular dulls with the same view
angle, always on, no toggle.

### Symptom B — "everything goes TOO DARK when I look down / pitch"

**Cause: looking down fills the frame with more ground & slopes, and three things darken them:**

1. **The near-black slope rock.** `rock035` (mean luma ≈ 0.09) is force-blended onto every slope
   (`:502-505`). Tilt down → more slope area in frame → more near-black → scene crushes dark. This is a
   **base-color** effect, independent of lighting (it survives the `I` unlit toggle, which is *why*
   lit ≈ unlit looks "as bad").
2. **Shader AO at `AO_LIGHT_AFFECT=1.0`** (`:603`) folds AO into ambient/GI (there is **no custom
   `light()`**), lowering the ambient baseline everywhere.
3. **Screen-space SSAO** is enabled (radius 3, intensity 1) in `LightingComposer.cs:227-229`. SSAO is
   *inherently view-dependent* — its darkening pattern is computed in screen space and **shifts/crawls as
   the camera moves and tilts.** This is the "shadows/ground look too dark and *change* when I just move
   the camera" part. (The team previously pinned this kind of artifact on **SSIL**, which they disabled
   `:231-234` — but **SSAO is still on and is the remaining view-dependent darkener.**) Toggle it live
   with **`M`** (`TerrainLabUI.Process.cs:434-437`) for an instant A/B.

> **Net:** Symptom A (flatten on turn) = the `nfade` relief rolloff. Symptom B (darken on pitch-down) =
> near-black slope rock × shader-AO × screen-space SSAO. They feel like one bug because both fire when
> you reorient the camera over sloped terrain.

---

## 6. Tiling, blend & in-shader band-aids

- **`mat_tiling = 12.0` — a single global**, never set from C#, no UI row (`ground.gdshader:142`). No
  per-material scale. At 1024 px that's ~85 texels/m (sharp enough), so the issue is **visible
  repetition**, not smear: the whole image repeats every 12 m and the bound albedos carry distinctive
  macro blobs (grassland dirt circles, till stone clusters) that read as a tiling grid.
- **No macro tiling-break anywhere** — no second-octave/detail texture, no variation map, no large-scale
  noise multiply on the sampled albedo (grep confirms the only "macro" uniforms are heightfield params).
- **Band-aid inventory (defaults):**
  | Mechanism | Default | Effect / trade |
  |---|---|---|
  | `detail_fade_on` (distance dissolve→mean) | **OFF** (`:120`) | would wash detail to flat avg color |
  | `footprint_fade` (fwidth dissolve→mean) | **OFF** (`:125`) | "washed ground DEFINITION to flat mean" (its own comment) |
  | `antitile_on` (stochastic rotation) | **OFF** (`:334`) | the *only* thing that'd break the 12 m repeat; expensive (≤4 fetches/plane) |
  | `tri_all` (triplanar all 5) | **ON** (`:186`) | kills slope stretch; but ~45 albedo+rough+ao fetches/px, and on flat ground the Y-plane dominates so it still tiles the top texture |
  | **`nfade` normal rolloff** | **ALWAYS ON** (`:567-568`) | **the active definition-killer** (§5A) |

  So the three dissolve-to-mean fades are *off* (good); the residual flattener is the always-on `nfade`,
  plus the asset/tiling problems. The shader is configured to **preserve** near detail — the missing
  ingredients are **real relief + tiling-break + a brighter slope rock**, not removal of an active fade.

---

## 7. Import pipeline (correctness issues found)

- **All 738 normal maps imported as plain color (`compress/normal_map=0`)** — none flagged as normal
  maps (`rock035/normal.png.import:24`, systemic). Root cause: the generator `tools/copy_materials.py`
  writes one normal-agnostic `.import` template for albedo/normal/roughness/ao alike
  (`copy_materials.py:48-55, 87-88`). **Currently dormant** (textures are still `compress/mode=0`
  lossless, `vram_texture=false`, and the shader reads `.xyz` directly so they decode fine today) — but
  `detect_3d/compress_to=1` on all imports is a **time bomb**: first 3D re-import would VRAM-compress
  normals down the *color* path (not BC5/RG), degrading relief. Fix the flag proactively.
- **Anisotropic filtering is unset in `project.godot`** (no `default_filters/anisotropic_filtering_level`)
  → runs at Godot's weak **2×** default, despite samplers requesting `..._anisotropic`. Raising to
  **8×–16×** is a cheap grazing-angle win against the corduroy/shimmer.
- **Not broken:** mipmaps are on everywhere (2930/2930) — the old "mipmap fuzz" is *not* the current
  problem; sources are lossless WebP, no lossy downscaling. (Minor: sizes are mixed — ~640 @1024,
  75 @512, 13 @2048, 5 @4096, plus a few non-square oddballs like 3072×512 that would tile poorly.)

---

## 8. Root causes, RANKED (symptom → cause → evidence)

1. **Wrong/dark/failing materials are hardwired into the renderer.** `rock035` (near-black, `dropped`)
   on every slope = "too dark"; `02_rippled_dunes` (directional) = corduroy; `16_glacial_till`
   (doubly-flat) = thin. The 108 good materials are unreachable.
   *Evidence:* `TerrainLab.cs:271-280` + `material_verdicts.json:39/361/504/600/9` + opened images.
   **(asset + system)**
2. **`nfade` fades all surface relief out by view angle, always on.** The "definition disappears when I
   turn / flat as unlit" mechanism. *Evidence:* `ground.gdshader:521-522, 567-568, 573-577`. **(shader)**
3. **Two view-dependent darkeners stack on the dark slope rock:** shader `AO_LIGHT_AFFECT=1.0`
   (`ground.gdshader:603`) + screen-space **SSAO r3/i1** (`LightingComposer.cs:227-229`, view-dependent,
   `M` to A/B). The "darker & shifting when I move the camera" part. **(shader + system)**
4. **No macro tiling-break + single 12 m global tile** over high-contrast blob textures → visible repeat
   & distance aliasing. *Evidence:* `ground.gdshader:142`; no variation term (grep). **(shader)**
5. **Plain linear blend** (no height/depth-aware) softens material seams toward mush.
   *Evidence:* `ground.gdshader:550-551, 559`. **(shader)**
6. **Import/config:** normal-map import flag off (latent) + weak default anisotropy. **(pipeline)**

---

## 9. Ranked fixes (impact per unit effort)

| # | Fix | Where | Impact | Effort |
|---|---|---|---|---|
| **1** | **Swap the 5 hardcoded slots to bright, isotropic, high-relief `pass` sets** — esp. mat3 → `01_weathered_grey_bedrock` (kills the dark slopes), mat2 → `13_sun_baked_clay` (real relief), mat0 → an isotropic dirt/sand instead of directional dunes. | `TerrainLab.cs:271-280` | **Very high** (directly fixes too-dark + corduroy + thin) | **5 min** (edit one array) |
| **2** | **Stop fading relief by view angle.** Raise `nrm_fade_lo/hi` far higher (or gate `nfade` like the albedo fade) so near/mid relief stays. | `ground.gdshader:132-133, 568` | **Very high** (fixes "definition disappears on turn") | 5 min + eye-gate |
| **3** | **A/B the darkeners:** turn SSAO down (intensity ~0.4–0.6 or radius down) and/or cut shader AO strength; test with `M`. | `LightingComposer.cs:227-229`; `ground.gdshader:603` | High (fixes pitch-down darkening & crawl) | 15 min |
| **4** | **Wire materials to the palette/verdicts** so the 108 good assets are reachable and you can tune live (add an `ApplyControl` `material` case + restore zone rows, or simplest: load `_matRoles` from `ground_palette.json`). | `TerrainLab.cs`, `TerrainLabUI.Apply.cs`, `LabRegistryLoader.cs` | High (unlocks iteration; the real long-term fix) | 1–2 h |
| **5** | **Add macro tiling-break** (multiply albedo by a low-freq variation, or enable+tune `antitile_on`). | `ground.gdshader` (`:334` exists) | Medium-high (kills the 12 m grid) | 30–60 min |
| **6** | **Height/depth-aware blend** instead of linear. | `ground.gdshader:550-559` | Medium (crisper seams) | 1 h |
| **7** | **Per-material tiling scale** (array instead of one `mat_tiling`). | `ground.gdshader:142` | Medium | 1 h |
| **8** | **Set anisotropy 8×–16× + fix normal-map import flag** (and `detect_3d/compress_to=2`). | `project.godot`; `tools/copy_materials.py` + re-import | Medium (grazing sharpness; pre-empt the VRAM-compress time bomb) | 30 min |

---

## 10. The single best one-line proof that the ground CAN look good with existing assets

**Edit one array.** In `scripts/lab/TerrainLab.cs:271-280`, replace `_matRoles` with all-`pass`,
bright-isotropic sets — most importantly get the **near-black `rock035` off the slopes**:

```csharp
private static readonly string[] _matRoles =
{
    "dirt", "biome_grassland", "13_sun_baked_clay",
    "01_weathered_grey_bedrock", "01_fresh_powder",
};
```

(All five exist on disk and are `pass`-rated; `01_weathered_grey_bedrock` is mid-grey with the strongest
rock normal, `13_sun_baked_clay` has deep crack relief.) **Then `dotnet build WG16.csproj`** before
launching — the player does not recompile C#. This alone should remove the "too dark on slopes" and the
worst corduroy. Pair it with fix #2 (raise `nrm_fade_lo/hi`) to also stop the relief from fading as you
turn, and you've addressed the owner's top three complaints with two tiny edits.

---

## Appendix — corrections to the prior internal hypotheses

| Prior hypothesis | Verdict |
|---|---|
| Shader fed by hardcoded 5-array; palette/UI not connected | **CONFIRMED** (§1) |
| 3 of 5 hardcoded mats were `fail` | **STALE** — already swapped 2026-06-28; now **2 of 5** are `fail`/`dropped` (`biome_grassland`, `rock035`), and the swap *comment misdescribes them as AAA* (§2) |
| AO never applied | **STALE/FIXED** — AO is now bound & applied, but the maps are low-contrast so it's weak (§3) |
| Only 2 of 5 normals applied | **STALE/FIXED** — all 5 now applied; the real issue is `nfade` fading them by view angle (§5A) |
| `01_tussock_grass` directional stalks = corduroy | **STALE** — swapped out; current corduroy source is `02_rippled_dunes` + the dark rock's diagonal veins (§4) |
| Global `mat_tiling=12`, linear blend | **CONFIRMED** (§6) |

*Also corrected during this audit:* the claim that `rock_dark` and `rock035_derived` are byte-identical
duplicates is **false** (`cmp` shows they differ) — `rock035_derived` is a genuine distinct `pass` asset.
