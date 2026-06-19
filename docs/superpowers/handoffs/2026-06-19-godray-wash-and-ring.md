# Handoff: God-Ray "filter look" + "ring" — root cause found, fix NOT done

Date: 2026-06-19. Status: **INCOMPLETE — handed off.** Branch: `experiment/presentation`.
Companion: plan `docs/superpowers/plans/2026-06-19-godray-cloud-field-occlusion.md`,
spec `docs/superpowers/specs/2026-06-19-godray-cloud-field-occlusion-design.md`,
memory `godray-emission-vs-albedo-rootcause`.

## TL;DR

The original task (swap god-ray cloud occlusion from luminance → cloud shadow map for
mood-independence) was implemented and committed (`254422a`), then **rejected on the eye-gate**:
the shadow-map occlusion reads as a soft **filter, not beams**. We reverted the default to the
luminance path (which gives real beams) and chased two remaining complaints — a milky **"filter"
wash over the sky** and a **"ring" glow around the view**. Those two are **inherent to the
single-pass radial-scatter technique** and are **still unsolved**. The user called stop.

**Do NOT re-try:** the shadow-map occlusion as default (occ_mode 3 = filter), or froxel
volumetrics (failed 3× per memory — "washy fog").

## What the user wants

Crisp crepuscular **beams** through cloud gaps, that:
- do NOT wash the sky to milky white (the "filter look"),
- do NOT show a circular "ring/lens-flare" glow around the player's view,
- do NOT fog the terrain,
- ramp on **gradually** as you turn toward the sun (this one is FIXED — see below),
- work across moods without per-mood re-tuning,
- AAA quality. The user explicitly wants this **developed and grown**, not shelved.

## Root cause (established with evidence — read before touching anything)

Screen-space radial scatter (GPU Gems 3) adds light **additively** along each pixel's ray toward
the sun. The defining fact:

> **A "beam" is NOT brighter than open sky — it's open sky with dark cloud-shadows carved into
> it.** The bright shaft and the surrounding "wash" are the *same* scatter value; beam definition
> comes entirely from the *dark carvings* (rays that cross a cloud).

Consequences, each verified this session:

1. **Sky wash ("filter look")** — additive scatter inevitably brightens *open* sky toward white.
   Higher strength = more wash. Evidence: `C:/tmp/godray_verify/crop_off.png` vs `crop_on.png`
   (god rays off vs on, same view) — the blue sky goes milky. This is in the SKY and **cannot be
   masked**. Lowering strength (6.0 → 2.5) reduces the wash (sky stays bluer) but makes beams
   subtler — a fundamental trade-off, not a bug. `crop_str25.png` shows the strength-2.5 result.

2. **Terrain fog — SOLVED.** The same scatter smeared over terrain had no contrast → uniform veil.
   Fixed with a depth-based **sky-mask** (`terrain_ray_strength`, default 0 = god rays only on sky
   pixels). Evidence: `iso_off.png` vs `iso_on.png` — with the sky-mask, terrain is ~identical
   off-vs-on; the residual sun-facing terrain haze is the **scene's own aerial perspective**, NOT
   god rays (it's present with god rays OFF too). Don't chase it as a god-ray bug.

3. **"Ring" glow** — the radial scatter's soft circular concentration centered on the sun/screen
   (the DC peak near the sun), possibly accentuated by the gate's falloff. **Not solved.**

4. **occ_mode 3 (shadow map) = filter, occ_mode 2 (luminance) = beams.** The cloud shadow map is a
   smooth top-down transmittance projected to the deck → no sharp edges → soft filter. On-screen
   luminance follows real cloud silhouettes → crisp beams. **The committed `254422a` swap was a
   visual regression.** The working tree already reverts the default to occ_mode 2.

## Current code state (working tree, UNCOMMITTED on top of `254422a`)

Modified (god-ray files only):
- `shaders/godray_screen.gdshader` — rewritten `occlusion()` keeps both paths (occ_mode 2
  luminance = default-worthy; occ_mode 3 shadow-map = dormant). Added: `sun_cloud_gate()` (occ_mode
  3 only), **sky-mask** (`terrain_ray_strength`, draws beams on sky only), shadow-map uniforms.
- `scripts/lab/GodRaysScreen.cs` — default `occ_mode = 2` (luminance/beams). **Gradual gate
  onset** in `_Process` (wider edge fade 0.15→0.32 + eased align smoothstep) — this FIXED the
  "lens flare comes on too quick" complaint. Plus dormant shadow-map setters.
- `data/cloud_presets.json` — "God Ray Showcase" `cloud_godray_strength` 6.0 → **2.5** (de-wash).

Committed at `254422a` (the shadow-map wiring, now mostly dormant for occ_mode 2):
- `scripts/lab/TerrainLabUI.cs` / `TerrainLabUI.Process.cs` — binds the cloud shadow map to the
  god-ray material + flips `cloud_shadow_on` at ComputeReady. Harmless when occ_mode 2; only used
  by the dormant occ_mode 3 path.

**What's GOOD and should be kept:** occ_mode 2 default (beams), the sky-mask (clean terrain), the
gradual onset. **What's still wrong:** the sky wash ("filter") and the ring.

## Recommended next steps (in priority order)

The crux is: **kill the DC (uniform wash + ring), keep the AC (beam carvings).** Options:

1. **Local-contrast / variance gate (recommended).** Modulate the per-pixel added scatter by the
   *local variation* of the occlusion buffer near the sun direction: suppress where occlusion is
   locally uniform (open sky → pure wash, OR solid cloud → nothing), keep where it varies (cloud
   edges = actual shafts). This directly removes the open-sky wash and the ring while preserving
   beams. Costs extra occlusion taps; single-pass-ish.
2. **High-pass / unsharp the scatter.** Subtract a blurred copy of the scatter from itself → drops
   the low-frequency wash + ring, keeps the high-frequency beam carvings. Needs a 2nd pass/blur.
3. **Accept subtle (current working-tree state, strength 2.5).** Tasteful floor; wash minimal,
   beams subtle. Ship-able if the user accepts "gentle" over "dramatic."

Do NOT: re-enable occ_mode 3 as default (filter), or build froxel volumetrics (failed 3×).

## How to verify (so you can self-review without bugging the user)

- Launch WINDOWED, absolute path, `--` before user args (memory `wg16-launch-absolute-path`):
  ```
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
    --path C:/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn \
    -- --preset=5 --godrays=1 --lookatsun --auto-shot=C:/tmp/godray_verify/x.png
  ```
- **The auto-shot saves at WINDOW resolution and the image viewer downscales it — so thumbnails
  LIE.** Crop the region of interest to native pixels before judging (this is how the wash was
  finally seen). PowerShell:
  ```
  powershell -NoProfile -Command "Add-Type -AssemblyName System.Drawing; \
    $s=[System.Drawing.Image]::FromFile('C:\tmp\godray_verify\x.png'); \
    $c=New-Object System.Drawing.Bitmap(760,548); $g=[System.Drawing.Graphics]::FromImage($c); \
    $g.DrawImage($s,(New-Object System.Drawing.Rectangle(0,0,760,548)),(New-Object System.Drawing.Rectangle(392,100,760,548)),[System.Drawing.GraphicsUnit]::Pixel); \
    $c.Save('C:\tmp\godray_verify\x_crop.png')"
  ```
- A/B the contribution by toggling `--godrays=0` vs `=1` at the same view — the diff is the
  god-ray term (this is how terrain haze was proven to be scene aerial perspective, not god rays).
- Judge IN MOTION, never from a still (memory `wg16-mipmap-fuzz-gotcha`).
- `--godraydbg=1` = occlusion mask, `=2` = sun marker. `--mood=N` to test moods.

## Reference captures left in `C:/tmp/godray_verify/`

`crop_off.png` / `crop_on.png` (the wash, strength 6), `crop_str25.png` (strength 2.5, bluer),
`iso_off.png` / `iso_on.png` (sky-mask proof), `diag_rays_off.png` (clean scene baseline).
