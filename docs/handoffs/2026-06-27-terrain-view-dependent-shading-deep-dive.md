# Handoff — Terrain shading changes when the camera ROTATES in place ("shadows move when I turn")

**Date:** 2026-06-27
**Status:** OPEN — root cause NOT yet found. Methodology reset in progress.
**Severity:** high (blocks the "shadows look real / world-locked like erosion-lab" goal)

---

## The symptom (user's words)

> "when i turn around the shadows move around and change. its static and ground bound in the erosion lab."

When the camera **rotates in place** (no translation), the terrain's bright/dark pattern
**changes**. In `C:\Wg16\erosion-lab\ErosionLab` — a plain matte material on a single static
2 m mesh with default Godot lighting — it does **not**: the shading is static and ground-bound,
which is the correct behavior. WG16 does it even in `--clean` (env post stripped to erosion-lab
parity). So the cause is **inside WG16's terrain render**, not the environment post stack.

**Key geometry fact:** `scripts/workbench/FlyCamera.cs` mouse-look only writes `RotationDegrees`
(pure rotation); WASD translates separately. A correct renderer changes NOTHING in the world when
you only rotate — only the viewport pans. So whatever changes must be a **view-direction-dependent
shading term**. That is a short, specific list (below).

---

## METHODOLOGY RESET — why this has slipped across many chats

We kept trusting **indirect signals** instead of pixels:
- Console prints (`[isolate] (N) Sun CSM shadow = False`) prove a C# property was *set* — NOT that
  the render changed. The toggle may have had no visible effect, or been immediately overwritten.
- The user's in-motion eyeball (real, but we couldn't reproduce/measure it).
- Hand-rolled assumptions ("the toggles worked", "—clean stripped it").

**New iron rule for this bug:** nothing is "ruled out" until a **before/after screenshot, examined
directly, proves the lever does what it claims AND whether it removes the view-dependence.** Every
strip stage is verified by an **orbit pair** (same terrain target, two camera azimuths) that I look
at myself. The cause is whatever lever — when stripped — makes the same hills shade identically from
both angles.

---

## What was "tested" so far (ALL needs visual re-verification — do not trust)

| Lever | Reported result | Trust? |
|---|---|---|
| `N` → Sun CSM shadow OFF | "did nothing" | ❌ never visually confirmed the toggle removes a shadow |
| `M` → SSAO OFF | "did nothing" | ❌ never visually confirmed the toggle removes AO |
| `--clean` (env post off) | crawl persists | ✅ persists, but only strips ENV — terrain material untouched |
| ground.gdshader detail-fade | n/a | ✅ code: distance-driven + default OFF (`detail_fade_on=0`) — not it |

The N/M "did nothing" is itself suspicious: in the clean shot there ARE big dark areas on the dunes.
If turning the cast shadow fully off changed nothing, EITHER (a) the toggle is non-functional, OR
(b) those dark areas are NOT cast shadows — they're the **shaded (N·L) faces** of dunes facing away
from the low sun, which are world-locked and unaffected by the shadow toggle. (b) is very plausible
and would mean we've been chasing "shadows" that were never cast shadows. **Re-verify both toggles
with screenshots first.**

---

## Established from the real code

- `shaders/ground.gdshader` textured path (dm==0): `ROUGHNESS = max(clamp(rgh,0.04,1.0), tk)` —
  roughness can drop to **0.04 (glossy)**; `SPECULAR` is left at Godot's default **0.5**. ⇒ the
  terrain reflects the sky/sun specularly, a **view-dependent** highlight that moves as you rotate.
  erosion-lab uses flat roughness 0.92–0.95 (matte) ⇒ essentially no moving specular.
- Non-textured fallback path: `ROUGHNESS = ground_rough = 0.92` (matte, erosion-lab-like).
- `SPECULAR`/`ROUGHNESS=1` are forced ONLY by `dbg_fullrough` (line 447) and `dbg_unlit` (line 451).
- No SSR / reflection probe in `scenes/terrain_lab.tscn` (grep clean). Specular reflects the SKY
  cubemap radiance (IBL), which is view-dependent on a curved/rough surface.
- Strip levers that exist: `SetBool("dbg_fullrough")`, `SetBool("dbg_normalmap")`,
  `SetBool("dbg_unlit")`, `SetTexturesOn("use_textures")`, `SetCdlod(bool)`.

---

## FULL candidate list (ordered for a PURE-ROTATION symptom) + the lever to strip each

1. **Specular sky/sun reflection (BRDF), glossy roughness floor 0.04** — *prime suspect.*
   Lever: `dbg_fullrough=true` (ROUGHNESS=1, SPECULAR=0). View-dependent by definition; survives
   shadow-off + SSAO-off; absent in matte erosion-lab.
2. **Normal maps perturbing the specular + adding detail** — Lever: `dbg_normalmap=false`.
3. **Texture roughness maps creating the glossy patches** — Lever: `use_textures=false`
   (flat albedo + `ground_rough` matte). Subsumes #1/#3 if it’s the texture path.
4. **Sky IBL / ambient specular** (the reflected sky changes with view) — tied to #1; also test by
   swapping to a flat constant sky or `dbg_fullrough`.
5. **CDLOD geomorph + LOD band transitions** — only bites under TRANSLATION, but list it.
   Lever: `--cdlod=0` (single static mesh, erosion-lab-like).
6. **Floating-origin renderOrigin snap** — only under big translation. Lever: `--pinorigin`.
7. **SSAO (re-verify)** — Lever: `env.SsaoEnabled` / key `M`. Screen-space ⇒ view-dependent; but
   erosion-lab has it on too, so suspect low.
8. **CSM cast shadow (re-verify)** — Lever: `sun.ShadowEnabled` / key `N` / `--sunshadow`.
9. **MSAA / temporal** — `project.godot` msaa_3d=1, no TAA seen. Unlikely. Lever: project setting.
10. **Tonemap / glow / grade** — already off in `--clean`; re-confirm in the strip baseline.

---

## The strip-down plan (one lever at a time, each VERIFIED by an orbit pair)

**Stage 0 — BAREST terrain ("WG16 erosion-lab-equivalent"):**
`--clean` + `dbg_fullrough=true` + `use_textures=false` + `--cdlod=0` + flat/constant sky.
Capture orbit pair (target T, az=0 and az=+30). EXPECT: identical shading on the same hills
(no view-dependence). If it's STILL view-dependent here → the cause is engine-level
(IBL/MSAA/exposure), escalate. If it's GONE → bisect upward:

**Stage 1..N — add ONE lever back, re-capture the orbit pair:**
- +specular/roughness (`dbg_fullrough=false`) → does the crawl return? (tests #1)
- +textures (`use_textures=true`) → ? (tests #3)
- +normal maps (`dbg_normalmap=true`) → ? (tests #2)
- +CDLOD (`--cdlod=1`) and now TRANSLATE → ? (tests #5/#6)

The first lever whose re-introduction brings back the view-dependent change **is the cause.**

---

## Instruments to add (so the bisection is headless + reproducible)

- `--orbit=cx,cy,cz,dist,elevDeg,azDeg` — place the camera at (dist,elev,az) around target
  (cx,cy,cz) and `LookAt` it. Enables same-patch two-azimuth capture (the view-dependence probe).
- `--fullrough[=1]` → `dbg_fullrough`; `--normalmap[=0]` → `dbg_normalmap`; `--unlit[=1]` →
  `dbg_unlit`. (`--textures`, `--cdlod`, `--sunshadow`, `--clean` already exist.)

## Launch reference
```
C:\Godot\v4.6.2\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe \
  --path C:\Wg16\wg-16-project -- --clean --orbit=0,150,0,900,22,0  --auto-shot=<abs>.png
```
(windowed — compute bakes NullRef under --headless; see memory headless-no-local-rendering-device)

## Reference
- erosion-lab: `C:\Wg16\erosion-lab\ErosionLab\{Main.tscn,Main.cs,TerrainMesh.cs}` — the matte,
  single-mesh, default-lighting control that does NOT exhibit the bug.

---

## FINDINGS — verified by screenshot bisection (2026-06-27)

Captured at a FIXED camera (`--cam=0,608,846,-20,<yaw>`) and an orbit, time≈noon, `--clean`.
Shots in the session scratchpad: A/E (full), F (yaw+12), G (specular off), H (normal-map off),
I (textures off), C/D (full strip).

1. **Pure camera ROTATION does NOT change the shading.** E (yaw 0) vs F (yaw 12): the dark near
   dune + snow patch simply pan across the frame; brightness is unchanged. ⇒ the symptom is **not**
   a view-dependent specular/IBL term, and **not** cast-shadow crawl. Terrain shading is world-locked.
2. **Specular ruled OUT.** G (`dbg_fullrough`, SPECULAR=0) is identical to E. The prime suspect
   (glossy 0.04 roughness sky reflection) is NOT visibly contributing at this sun/camera.
3. **The dark "shadowy" slopes are TEXTURE ALBEDO, not shadows.** I (`use_textures=false`) turns the
   dark brown near dune PALE/matte (height-colour path) — erosion-lab-like. The dark slope is the
   rock/soil material the slope-blend puts on steep faces, not a cast or N·L shadow.
4. **The shimmering striations/combing are NORMAL MAPS.** H (`dbg_normalmap=false`, textures still on)
   keeps the dark albedo but the diagonal striations VANISH.
5. **Cause of "shadows move when I turn" = the TEXTURED SURFACING in MOTION**, i.e. (a) dark slope
   albedo + (b) normal-map striations that shimmer as you fly. This matches the existing memory
   `wg16-mipmap-fuzz-gotcha` ("terrain motion artifacts come from textures; show live, not in stills")
   and `ground-texture-feedback`. It is NOT shadows / SSAO / specular / CSM — all ruled out above.

### What this means
The whole "real shadows / world-locked" arc was chasing the wrong layer. The shadows ARE world-locked
(Phase 1 CSM is fine). The thing that reads as "moving shadows" and "doesn't look like erosion-lab" is
the **terrain surfacing**: too-dark slope material + aliased/striated normal maps that shimmer in
motion. erosion-lab looks clean because it is **matte vertex-colour with no normal maps**.

### Remaining verification
- [ ] Confirm in MOTION (the symptom only lives there): fly with textures ON (shimmer) vs OFF
  (key `U`) — does the "moving shadows" vanish with `U` off? (live toggle added 2026-06-27)
- [ ] If yes, the fix is a SURFACING decision (not lighting): options below.

### Fix options (surfacing, not lighting)
- **A. Matte/erosion-lab parity:** drop normal maps + lighten slope albedo (closest to the look the
  user likes). Cheapest, most erosion-lab-like.
- **B. Fix the normal maps:** they read as aliased combing — check mipmaps on import
  (`wg16-mipmap-fuzz-gotcha`), reduce perturbation strength, or fix the distance/footprint fade so
  they don't shimmer. Keeps detail, more work.
- **C. Lighten/retune the slope material** so steep faces aren't near-black (the "shadow" look).
