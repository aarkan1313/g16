# GROUND Phase G-1 — Compositing Core (float weights + fragment-resolution blend)

Date: 2026-06-20. **Phase G-1 of the master design** (`2026-06-20-ground-rendering-system-master-
design.md`): the *first* phase, because everything renders through the compositing core — harden it
before layering on it. Covers master components **#2 (texture sampling — verify)** and **#3
(compositing/blending — rebuild)**.

## Problem (confirmed live, 2026-06-20)

Driving key-3 close-up with the user, the ground reads **blocky, stair-stepped, and smeary** at
material transitions. Isolated decisively: `tile mode = none` → artifact persists (NOT textures /
histogram); `splat debug = zones` → smooth (NOT placement selection); `splat debug = mix amt` →
**blocky stair-stepped smeary** = the **blend weight** is the culprit. Root cause in code:
- The weightmaps (`splat_wa`/`splat_wb`, baked by `SplatCompute`) are **8-bit `Rgba8` at 2048² over
  8192 m = 4 m/texel**.
- The fragment computes `t = m1/(m0+m1)` from those weights, and `interlock_blend` uses `t` as the
  **threshold** the material heights cross. So the boundary *position* inherits t's **8-bit
  quantization** (→ stair-steps) and **4 m resolution + bilinear** (→ blocky/smear), even though the
  boundary *shape* already has nice full-res height detail.

Same class as the failed Unit-4 4 m masks. "Approved at distance, broken up close."

## North star (G-1)

Material boundaries are generated at **fragment resolution** with **no quantization and no visible
texel grid** — the low-res weight only provides a smooth low-frequency *bias* for where the boundary
sits; the full-res material height + noise generate its actual shape, anti-aliased. The core every
other phase stacks on is solid. Quality bar: Skyrim/NMS — believable, no obvious blend artifacts at
close/mid/far, game-agnostic (works for any material data). Hold 8 ms in-motion.

## Approach (user-approved: "A — float weights + fragment-resolution height boundary")

### Component #2 — Texture sampling: VERIFY (not rebuild)
Confirm the material samplers use **mipmaps + anisotropic filtering** (the documented "fuzz = textures
imported without mipmaps" gotcha) and that the histogram path stays mip-correct (it uses `textureGrad`
with real derivatives, so it should). This is a check with a small fix only if fuzz is found — keep
the histogram anti-tiling as-is.

### Component #3 — Compositing/blending: REBUILD
1. **De-quantize the weight field.** `SplatCompute` bakes `splat_wa`/`splat_wb` as **`Rgbah` (RGBA
   half-float)** instead of packed `Rgba8`. Removes the 256-step quantization (the stair-stepping) at
   the source. VRAM for the two maps ≈ 32 MB → 64 MB at 2048² (half is ample for 0–1 weights;
   full-float `Rgbaf` = 128 MB and isn't needed). The fragment `splat_weights7` sampler reads them
   unchanged — it just gets continuous values.
2. **Fragment-resolution boundary.** Rework the weightmap-path interlock so the **sharp boundary is
   driven by the full-res `material_height(d0/d1)` + multi-octave breakup noise**, with the
   now-smooth `t` demoted to a gentle low-frequency **bias** on the boundary position (not the
   threshold itself). Add an **AA-aware transition width** (tie the blend band to `fwidth` of the
   boundary signal) so there is no pixel-level stair-step at grazing angles.
3. **Dissolve the 4 m grid.** Strengthen/refine the existing multi-octave **domain warp**
   (`splat_warp_m`/`splat_warp_wl`) of the weightmap lookup so any residual 4 m structure becomes
   organic before the interlock sees it.
4. **Toggle.** All of the rebuild behind an **`hq_blend` toggle** (Surface or Splat tab), defaulting
   to the current path until the eye-gate passes, then flipped — fully reversible, A/B against now.
   The existing `interlock sharpness / breakup / boundary warp` knobs stay live for tuning.

## Architecture & data flow
- `SplatCompute.Bake` (+ `shaders/splat_weights.glsl`): emit float role weights; build `splat_wa`/`wb`
  as `Rgbah`. `splat_tex` (legacy index) + breakup map unchanged. `TerrainLab.RebakeSplat` unchanged.
- `shaders/terrain_lab.gdshader` weightmap branch: `splat_weights7` (now half-float) → top-2 → smooth
  `t` → **new** `hq_interlock(hD, hS, t)` (full-res height + noise boundary, `fwidth` AA, t as bias),
  behind `hq_blend`. `splat debug = mix amt` reflects the new `m` for verification.
- `data/lab_controls.json`: add `hq_blend` toggle (small, additive — coordinate, the file is shared).

## Scope
**In:** half-float weight bake; the fragment-resolution AA interlock; stronger warp; the toggle; the
sampling/mip verify. **Out:** weightmap *resolution* bump (deferred — try float+warp first); changing
material *placement/zones* (smooth + approved — only the transition *quality* changes); anything in
later phases (data model, surface depth, scatter, lighting).

## Perf
+32 MB VRAM (half-float weights), a few fragment noise/`fwidth` ALU. Gate on `--profmove` vs 8 ms; the
interlock work is per-fragment but cheap. No extra texture taps in the hot path.

## Risks / unknowns (verify early in the plan)
- **Float→half packing** in `SplatCompute` (compute writes float to the SSBO; C# builds the `Rgbah`
  image — confirm Godot accepts the half byte layout, else use `Rgbaf` and accept the VRAM).
- **Tuning the t-bias vs height-detail balance:** too much height-dominance shifts the *placement*
  (must NOT move the approved zones); too little leaves the boundary low-res. Knob-controlled.
- **`fwidth` at grazing angles** can over-widen the band → confirm it reads crisp, not mushy.
- Must stay **byte-stable for the approved look when `hq_blend` is OFF** (verify A/B).

## Gate (user's live eye)
**Shift+4** A/B (ground-lane review bank): current blend ↔ `hq_blend`, fly close/mid to a material
transition. PASS = `splat debug = mix amt` reads **smooth — no stair-step, no 4 m grid, organic
boundary**, the final surface reads clean at close/mid/far, perf within budget. On PASS: flip
`hq_blend` default on; record; proceed to Phase G-2 (material data + surface depth).

## Self-review notes
- **Scope:** one implementation plan's worth — one bake-format change + one shader interlock + a
  toggle + a sampling check. Resolution bump + later phases explicitly deferred.
- **Placeholders:** none — each change names its file + mechanism + the knob that controls it.
- **Consistency:** half-float (not full) stated identically in Approach/Architecture/Perf; the toggle
  (`hq_blend`, default-off-until-gated) stated once and honored; "don't move placement" repeated as
  both a scope line and a risk.
- **Ambiguity:** "fragment-resolution boundary" pinned to the concrete mechanism (full-res height +
  noise + `fwidth` AA, t as bias), not left abstract.
