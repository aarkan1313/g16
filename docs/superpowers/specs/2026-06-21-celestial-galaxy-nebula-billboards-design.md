# Celestial Galaxy/Nebula v2 — "Billboards" (design, 2026-06-21)

Fresh design for the WG16 night sky's galaxy + nebula layer, replacing the **REJECTED** C1
procedural-noise system. User's call: "start over on the nebulae and galaxy"; direction chosen:
**billboards via a performant procedural lane.**

Lane context: `handoffs/2026-06-21-sky-handoff-START-HERE.md`,
`handoffs/2026-06-21-sky-lane-next-steps.md`. Authoritative logs: `docs/ROADMAP.md` (#6 Celestial),
`docs/DECISIONS.md`, `docs/NEEDS_REVIEW.md`. Memory: `sun-light-arc`.

## Why C1 was rejected (the lesson this design is built around)

C1 (`galaxy_color` / `nebula_color` in `cloud_sky.gdshader`, baked via `night_sky_bake.glsl`) built
the galaxy and nebulae from **fbm/ridged 3D value noise — the same noise basis the clouds use.** It
therefore always read as **clouds, not space.** Sharpening, finer frequencies, ridged filaments, and
localizing to a patch all helped but never escaped the cloud feel. A great-circle band read as "a fog
band across the whole world"; localizing made it "all in one place"; a `pow(cd)` core bulge bloomed
into a soft glow the user disliked.

**The fix:** abandon the 3D-noise basis. Build each object from **structure** (spiral arms, elliptical
cores, hard dust lanes, embedded stars) in a **bounded local 2D disc**, not volume noise on the sphere.

## Goals / non-goals

**Goals**
- A night-sky galaxy + nebula layer that reads as *objects in space*, not clouds.
- Small/subtle by default, but **per-object tunable up to medium / hero** prominence.
- Performant procedural lane — no authored image assets, near-zero cost when idle.
- Reuse the knob/slot-array pattern + preset-picker infra the user liked; throw out the bad generator.
- Live tuning (no re-bake on shape tweaks).

**Non-goals**
- No photorealism target; fantasy color freedom is fine and wanted.
- No new mesh/billboard *geometry* — "billboard" here means a direction-anchored 2D disc rendered in
  the existing `cloud_sky.gdshader` sky shader (the sky is drawn on `EYEDIR`, there is no scene mesh).
- Not touching `star_field` or `meteors` (both PASSED and stay as-is).
- No baked texture for this lane (see Decision D1).

## Decisions

- **D1 — Live, not baked.** C1 baked *only because* 3×5-octave fbm everywhere was too expensive.
  Billboards early-out with one dot product per object; only the few pixels inside a small disc run the
  structured eval; the whole lane is gated by `night_factor` and above-horizon. Live therefore gives
  instant tuning, removes the byte-identical GLSL-mirror maintenance burden, and lets us delete the bake
  path. `night_sky_brightness` stays a live global multiplier.
- **D2 — Both generators** (spiral/elliptical galaxy + nebula), shipped behind one unified slot model.
  Implementation is **phased**: spiral/elliptical galaxies are the first build + eye-gate; nebulae are
  the second pass. (Phasing lives in the implementation plan, not here.)
- **D3 — Hybrid placement.** Seeded auto-scatter for small background accents + a couple of explicit
  hand-placed/tuned hero slots, merged into one array.
- **D4 — Placement computed in C#, once per change.** No per-pixel seeding and no per-frame array
  rebuilds; the shader only loops a pre-filled array.

## Architecture

Two units with a single well-defined interface (the packed slot array uniform).

### Unit A — `NightBillboards` placement (C#, in the lab/lighting layer)
**What it does:** owns the billboard configuration and produces the packed uniform array.
**How it's used:** called when config changes (seed, count, hero edits, preset load) — *not* per frame.
**Depends on:** the existing lab registry / lighting state plumbing; the night-sky preset picker.

- State: `scatter_seed`, `scatter_count` (small accents), plus a fixed number of explicit `hero` slots.
- On change: hash `scatter_count` accent objects from `scatter_seed` (direction biased to the upper
  hemisphere, small angular size, type/color/seed per object), append active hero slots, and write the
  merged list into the shader's slot uniforms (capped at `MAX_BILLBOARDS`, ~8).
- Each slot packs into existing-style vec4s, e.g.:
  - `bb_dir_size[i]` = `vec4(dir.xyz, angular_size_radians)`
  - `bb_color_type[i]` = `vec4(color.rgb, type_and_flags)` (type encoded in the w channel)
  - `bb_params[i]` = `vec4(brightness, tilt, rotation, seed)`
  - plus a secondary/arm color channel as needed (`bb_color2[i]`).
  (Exact packing finalized in the plan; must respect std430/array-stride rules — see memory
  `std430-packing-helper`.)
- `uniform int bb_count` = number of active slots.

### Unit B — billboard rendering (GLSL, in `cloud_sky.gdshader`)
**What it does:** given the view ray, accumulates the color of every active billboard.
**How it's used:** called from `stars_layer()` in place of the deleted `galaxy_color + nebula_color`.
**Depends on:** the slot uniforms from Unit A; `vnoise3` (kept) for optional fine nebula gas texture.

```
vec3 billboards(vec3 sr):           // sr = rotated celestial direction (same as stars/galaxy used)
  acc = 0
  for i in 0..bb_count:
    dir = bb_dir_size[i].xyz; reach = bb_dir_size[i].w
    cd = dot(sr, dir)
    if cd < cos(reach): continue                 // EARLY-OUT: one dot product, skip distant objects
    // local tangent-plane disc coords centered on dir, oriented by rotation, scaled by reach
    uv = project_to_disc(sr, dir, rotation, reach)   // |uv| ~ 0 at center, ~1 at edge
    if type == GALAXY:  acc += galaxy_billboard(uv, ...)
    else:               acc += nebula_billboard(uv, ...)
  return acc
```

**`galaxy_billboard(uv)`** — polar coords `(r, θ)` in the disc:
- radial falloff (e.g. `exp(-k r²)`), bright Gaussian core,
- log-spiral arms: `arms = smoothstep(edge, 0, |frac(arms_count·θ/2π − k·ln(r) + phase) − 0.5|)`
  modulated by radius (no arms in the core, fade at the rim),
- elliptical type = arms term off (smooth bulge only),
- color = `mix(core_color, arm_color, by radius)`; optional tilt squashes one disc axis.

**`nebula_billboard(uv)`** — structure-dominated, not fbm-dominated:
- 2–3 overlapping soft emission lobes (offset Gaussians) for an irregular body,
- a few **hard-edged dust lanes** (sharp signed-line masks across the disc) that carve dark cuts,
- a sprinkle of embedded bright point-stars (hashed within the disc),
- optional faint `vnoise3` (sampled in-plane) for fine gas grain — kept low-weight so it cannot
  collapse the look back into cloud-feel.

Inside `billboards()` each object is multiplied by its **per-slot** `brightness` only. The **global**
`night_sky_brightness` and the existing `fade = night_factor * smoothstep(-0.02, 0.12, rd.y)` are
applied **once at the call site** (see Integration), so brightness is never double-applied.

### Integration point
`stars_layer()` currently returns `(star + meteors(rd) + mw) * fade`, where `mw` came from
`galaxy_color + nebula_color` (baked). Replace the `mw` block with `billboards(sr) * night_sky_brightness`
(keeping the `night_sky_brightness > 0` early-out so the lane is truly free when the global knob is 0).

## Delete vs keep

**Delete:**
- `cloud_sky.gdshader`: `galaxy_color`, `nebula_color`, `ridge`, `fbm3` (each becomes unused once the
  galaxy/nebula go — verified: `fbm3` used only by `galaxy_color`; `ridge` only by `nebula_color`).
- `cloud_sky.gdshader` uniforms: `gx_*` (galaxy), `neb_count`/`neb_dir_scale`/`neb_color_dens`,
  `night_sky_baked`, `night_sky_tex`.
- `night_sky_bake.glsl`: the galaxy/nebula bake math; `AtmosphereCompute` `_mw*` bake plumbing for it.

**Keep:**
- `star_field` (PASSED), `meteors` (PASSED), `night_sky_brightness` (live global knob), `vnoise3`.
- The night-sky **preset picker** infra (`TerrainLabUI.NightSkyPresets.cs` + `data/night_sky_presets.json`
  + `--nspreset`) — repurposed to billboard arrangements (e.g. Calm Night / Galaxy-rich / Nebula-rich /
  Hero). Existing galaxy presets get replaced.

## Lab controls & review

- Night-tab knobs (registered via the existing lab registry pattern — each non-shader control needs
  field/setter/scene/id + a C# case, per memory `lab-registry-param-gotcha`):
  global `night_sky_brightness`, `scatter_seed`, `scatter_count`, and per-hero-slot
  dir / angular_size / type / color / brightness / tilt.
- Defaults: small accents on, heroes off (size 0 / disabled) — matches "small by default."
- **Eye-gate via key `2`** (existing night review; robust to the ground-strip `Apply.cs` change).
  `--review=2` for headless capture; crop to the sky region (lab panel + terrain fill the frame).
  Agree the look at each phase before calling it done — do NOT iterate blind like C1.

## Performance

- Idle (day, or `night_sky_brightness == 0`): early-out, zero cost.
- Night: per pixel, `bb_count` dot products + the structured eval only for pixels inside a disc. With
  small default sizes this is a tiny fraction of the frame and far cheaper than C1's full-screen 3× fbm.
- Placement math runs in C# on change only; never per-frame, never per-pixel.

## Risks

- **R1 — nebula re-collapsing into cloud-feel.** Mitigation: structure-dominated (lobes + hard dust
  lanes + embedded stars); `vnoise3` grain kept low-weight; eye-gate the nebula phase separately.
- **R2 — disc projection seam/distortion near an object's rim or at the pole.** Mitigation: tangent-plane
  projection with a clean radial falloff to 0 at the rim; test objects near zenith and horizon.
- **R3 — std430 array packing drift** (vec2/vec4 alignment). Mitigation: pack only vec4s; follow memory
  `std430-packing-helper`.
- **R4 — shared branch churn.** `experiment/presentation` is shared with the ground chat (they stripped
  the material system). Stay in sky/light + cloud files; `git add` explicit paths, never `-A`.

## Out of scope (later, per roadmap)
Meteor presets, planets (C2 cont.), #5 shadow & lighting pass, #7 end-of-arc perf pass.
