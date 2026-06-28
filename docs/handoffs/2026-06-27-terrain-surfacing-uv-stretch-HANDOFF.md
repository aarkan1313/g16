# HANDOFF — The "moving shadows when I turn" is TERRAIN SURFACING (texture UV stretch), not shadows

**Date:** 2026-06-27
**For:** a fresh chat to fix it
**Status:** ROOT CAUSE FOUND (grounded in code + close-up screenshots). Fix NOT yet applied.

---

## TL;DR

Months of "shadows look wrong / they move when I turn / not world-locked like erosion-lab" were
**NEVER a shadow or lighting bug.** Verified by screenshot bisection this session:

- Cast shadow OFF, SSAO OFF, specular OFF, TAA ON/OFF, pure camera rotation — **none** of them
  change or fix the symptom.
- The symptom is the **textured terrain surfacing**: the material textures are mapped with a
  **top-down planar UV** that **stretches into vertical streaks/strands on slopes**, plus visibly
  **tiles** (the knit/diamond pattern on snow). In motion this smeared detail shifts and reads as
  "moving shadows / shredding."
- erosion-lab looks clean because it's **matte vertex-colour, no textures, no normal maps** — so it
  has no UV to stretch.

**The fix is a SURFACING shader rework (triplanar/biplanar + anti-tiling), not a lighting change.**

---

## The smoking gun (real code)

`shaders/ground.gdshader`, textured branch (`use_textures == true`, the DEFAULT):

- **Line 352:** `vec2 uv = v_surf_xz / mat_tiling;` — UV is the **world XZ** position only → a
  **planar / top-down projection.**
- **Lines 382–392:** mat0 (sand), mat1 (grass), mat2 (mud), mat4 (snow) all sample `texture(matN_*, uv)`
  with that planar UV. **Only mat3 (rock/bedrock) uses `tri_sample(...)` (triplanar, line 385).**
- A top-down UV on a steep slope maps a tiny XZ footprint over a large vertical extent → the texture
  **smears vertically** → the streaks/strands. Worst on grass (`01_tussock_grass`) because its content
  is already vertical-ish; sand smears into the diagonal "combing."
- `v_surf_xz` = true-world XZ (line 125), `mat_tiling` is the world tiling scale.

That is the entire mechanism. It is geometric/spatial (baked into the mapping), which is why **TAA did
nothing** (TAA only fixes temporal/sub-pixel sparkle) and why every lighting lever was a dead end.

### Close-up evidence (user screenshots, 2026-06-27)
- Flat-ish ground: dense **vertical yellow-green strands** (stretched grass texture) carpet everything.
- A dune: dark body with **vertical streaking**; snow cap shows a **diamond/knit tiling weave**
  (`01_fresh_powder` tiling visibly).
- These do NOT appear in high-altitude auto-shots (detail too small) — they dominate up close and in
  motion. (Matches memory `wg16-mipmap-fuzz-gotcha` + `ground-texture-feedback`: judge surfacing
  close-up and in motion, never from downscaled stills.)

---

## What was RULED OUT this session (each VERIFIED, not assumed)

Methodology reset mid-session: console prints ≠ pixels. Everything below was confirmed by a
screenshot or a live in-motion toggle, not by a property-set log.

| Suspect | How tested | Result |
|---|---|---|
| Cast shadow (CSM) | key `N` live toggle + `--sunshadow=0` | no effect on symptom |
| SSAO | key `M` live toggle | no effect |
| Specular (glossy 0.04 roughness sky reflection) | `--fullrough=1` (SPECULAR=0); shot **G == E** | not it |
| Pure camera ROTATION | `--cam` two-yaw pair (**E vs F**) | shading world-locked; rotation alone barely changes it |
| TAA (specular shimmer fix) | `use_taa=true` + key `Y`; user flew it | **"didn't really do anything"** ⇒ spatial, not temporal |
| Normal maps (striations) | `--normalmap=0` shot **H** | removes the fine striations but the **streak/stretch + dark slopes remain** ⇒ normal maps are a *secondary* layer; the stretch is the albedo UV |
| Textures wholesale | `--textures=0` shot **I**, key `U` live | symptom GONE → confirms it's the textured surfacing |

So: shadows/SSAO/specular/CSM/TAA/rotation all eliminated. Remaining cause = **planar-UV texture
stretch + visible tiling** in the textured surfacing.

---

## Diagnostic instruments added this session (reuse them)

**Live keys (work in BOTH scenes, gated only by `_ready`):**
- `N` Sun CSM shadow toggle · `M` SSAO toggle · `U` terrain textures toggle · `Y` TAA toggle
  (`scripts/lab/TerrainLabUI.Process.cs`, the "Debug isolation bank")

**CLI flags (`scripts/lab/TerrainLabUI.Cli.cs` parse + `TerrainLabUI.cs` apply, all win over review/clean):**
- `--clean` — strip env to erosion-lab parity (filmic tonemap + SSAO only; no grade/glow/fog/aerial/
  atmosphere/cloud/overcast). Owned by `LightingComposer.CleanParity`.
- `--orbit=cx,cy,cz,dist,elevDeg,azDeg` — LookAt camera around a target (same-patch two-angle probe).
- `--fullrough[=1]` (dbg_fullrough: ROUGHNESS=1, SPECULAR=0), `--normalmap[=0]` (dbg_normalmap),
  `--unlit[=1]` (dbg_unlit), `--textures=0/1` (use_textures), `--cdlod=0/1`.
- `--sunshadow=0/1`, `--shadowdist=N`, `--shadowatlas=N`.

**Repro launch (windowed — compute bakes NullRef under --headless):**
```
C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe \
  --path C:\Wg16\wg-16-project -- --clean --time=14
```
Fly low over the dunes; press `U` to A/B textured vs matte. The streaks are the bug.

---

## THE FIX (AAA surfacing pass — this is the next chat's job)

Primary (the streaks):
1. **Triplanar (or biplanar/stochastic) for ALL materials, not just rock.** Replace the planar
   `texture(matN_*, uv)` for mat0/1/2/4 with `tri_sample`-style world-projection blends (mat3 already
   does this). Biplanar (Lagarde) is cheaper than full triplanar and usually enough. This removes the
   slope stretch entirely.
2. **Anti-tiling break-up** for the visible repeat (snow knit, sand): hex/stochastic tiling
   (Heitz–Neyret) or at least a macro-variation multiply + larger `mat_tiling`. WG16 has anti-tiling
   history — check `tile_mode` / the dropdown work (memory `tile-mode-dropdown-cleanup-entangled`).

Secondary (polish, after the stretch is gone):
3. Re-check normal-map strength + the disabled distance roll-off (`detail_fade_on=0`,
   `nfade`/`detail_dissolve`) — only matters once the albedo stretch is fixed.
4. Lighten the steep-slope material if dark rock/soil still reads as "shadow."

Interim / fallback:
- **Matte path is the known-good look** (`use_textures=false`, key `U`) — height/slope vertex colour,
  erosion-lab-like. If the surfacing rework is deferred, shipping matte (flip the `use_textures=true`
  default at `shaders/ground.gdshader:128`) is a clean placeholder. (This is literally what the ground
  was stripped to once — memory `ground-v2-core-built`.)

**Verification protocol (don't repeat our mistakes):** judge CLOSE-UP and IN MOTION (key `U` A/B),
never from high-altitude downscaled stills. Triplanar success = no vertical streak on the dune slopes
+ no strands on the grass at ground level.

---

## Key files
- `shaders/ground.gdshader` — line 352 (planar UV), 382–392 (per-material sampling), 302–310
  (`tri_sample` triplanar, the template to extend), 128 (`use_textures` default), 158–160 (dbg_*).
- `scripts/lab/TerrainLab.cs:288–301` `LoadGroundMaterials()` — the 5 `_matRoles` + `assets/materials/<role>/{albedo,normal,roughness}.png`.
- `scripts/lab/TerrainLabUI.Process.cs` — live N/M/U/Y toggles + the J ring-hunt diag stepper.
- `scripts/lab/TerrainLabUI.Cli.cs` / `TerrainLabUI.cs` — the `--clean/--orbit/--textures/...` flags.
- Earlier handoff (superseded by this one): `docs/handoffs/2026-06-27-terrain-view-dependent-shading-deep-dive.md`.

## Reference
- erosion-lab control (no bug): `C:\Wg16\erosion-lab\ErosionLab\{Main.tscn,Main.cs,TerrainMesh.cs}` —
  matte vertex-colour, single mesh, default lighting. The look the user wants.

## Standard-lighting-stack note
The Phase 0–2 standard Godot lighting work (Sun CSM + SSAO + filmic, commits up to `c0d379b`) is sound
and stays. It was never the problem. The remaining standard-stack phases (3 fog / 4 SDFGI / 5 presets)
are independent of this surfacing fix.
