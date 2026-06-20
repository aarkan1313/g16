# WG16 God-Ray System — Overview (architecture · feature · how to test)

Status: 2026-06-19. Screen-space crepuscular beams (GPU Gems 3 Ch.13 radial light-scattering).
Behind a toggle, default OFF, mood/scene-independent once set. This is the map for reviewing the
feature and tuning it.

## What it is

Bright crepuscular **shafts** streaming from the sun through gaps in the clouds, drawn as a single
clip-space fullscreen quad that radial-blurs an occlusion buffer outward from the sun's screen
position and **adds** the beam light back. No second render pass, no CompositorEffect.

## Modules (one job each, one-direction data flow — same discipline as the cloud system)

```
REGISTRY / DATA (no scene knowledge)
  data/lab_controls.json   — the god-ray controls (Clouds tab): toggle, strength, length, decay,
                             cloud reach, cloud threshold, backlit. Edit JSON → control appears; no C# UI code.
  data/cloud_presets.json  — "God Ray Showcase / Subtle / Dramatic" presets set the knobs + sun in one pick.
        │
        ▼
ROUTING (registry → setter, the ONLY UI→effect path)
  TerrainLabUI.Clouds.cs   — ApplyCloudFloat / ApplyCloudBool map knob-id → GodRaysScreen.SetX().
        │
        ▼
ORCHESTRATION (per-frame, no math)
  GodRaysScreen.cs (Node3D) — owns the fullscreen quad + ShaderMaterial, the public setter interface,
                             and per-frame: projects the sun to screen UV (gated by IsPositionBehind +
                             view-align with a smooth edge fade), pushes inv_view_proj / cam_world /
                             aspect. Attach(camera, sun); SetEnabled(on). occ_mode 2 (luminance) default.
        │
        ▼
SHADER (the look)
  shaders/godray_screen.gdshader — clip-space quad; reconstructs sky vs geometry from depth; builds the
                             occlusion field; tangential-high-pass radial march → additive beam light.

DIAGNOSTICS (windowed)
  --godrays=0/1 · --godrayhp=0/1 (A/B the high-pass) · --godraydbg=1/2/3/5 (occlusion / sun marker /
  isSky gate / scatter viz) · --godrayab=<path> (drift-free on/off A/B, freezes the scene) · --glow=0/1.
```

**Coupling guarantee:** `GodRaysScreen` is the only place the UI touches the effect (mirrors
`CloudVolume`'s "UI → here ONLY" rule). Adding a tunable = shader uniform + one `SetX` + one
`ApplyCloud*` case + one `lab_controls.json` row. Nothing else changes.

## The four things that make it correct (and the bugs each fixes)

1. **`fog_disabled` in `render_mode`** — THE fix for the "filtered world." The quad is a 3D mesh at the
   far plane, so the Environment's depth fog (`fog_enabled`, blue-grey `fog_light_color`) was being
   applied to it and, via `blend_add`, added uniformly across the whole screen → a flat haze that no
   god-ray knob could change. `fog_disabled` makes the quad ignore fog. (A quad outputting nothing
   lifted the frame +7/+7/+7 with fog, **exactly 0.0** with `fog_disabled`.)
2. **`blend_add`** — the pass outputs ONLY `ray_tint * scatter` and adds it. Non-beam pixels (all
   terrain, open sky) get +0, so the world is left exactly as the scene rendered it. (The old
   `ALBEDO = scene + rays` opaque copy re-sampled and double-processed the whole frame.)
3. **`sky_depth_eps = 1e-6` sky gate** — under Reverse-Z the sky clears to depth *exactly 0.0* and all
   geometry is > 0, but distant terrain has a tiny depth (~1e-6..1e-4). The old `0.0005` cutoff was
   larger than that, so distant terrain was classified as sky and washed. 1e-6 excludes it. Beams are
   sky-only; terrain pays no march cost.
4. **`cloud_invert` occluder polarity (occ_mode 2)** — default `false` = DAYLIT: the **bright cloud is
   the occluder**, clear sky is open → shafts form in the gaps and uniform sky cancels under the
   high-pass (no veil). The old code had this inverted (treated the dim blue sky as the occluder), so
   shafts smeared through clouds and the sky's brightness gradient became a milky veil. Set
   `true` = BACKLIT for sun-behind-cloud scenes (cloud is the dark pixel). Exposed as the "god ray
   backlit" toggle.

## Occlusion modes (`occ_mode`)

- **2 = luminance (default, real scene):** sharp on-screen cloud silhouettes near the sun → crisp
  beams. Polarity set by `cloud_invert`. The chosen production path.
- **3 = cloud shadow map (dormant):** samples the mood-independent top-down transmittance the cloud
  system already bakes. No sky-glow veil, but the soft 512² map gives shafts too weak/soft to read,
  even with the `cloud_sharpen_lo/hi` contrast curve. Kept as a documented fallback, not default.
- **0 = depth only:** the synthetic `godray_test.tscn` harness.

## Beam shaping (the radial march)

`march_beam()` steps toward the sun and, at each step, subtracts the occlusion's **tangential local
mean** (perpendicular to the sun direction) and accumulates only the positive excess
`max(occ − tangential_mean, 0)`. Uniform sky → 0 (no veil); alongside a cloud band → a smooth shaft.
Because a smooth mean is subtracted (not an edge detected) the shafts have no cloud-edge halo. A
5-tap `near_structure()` pre-check skips the 64-step march on open sky far from any cloud → cost is
~+1 ms over god-rays-off (sky-only, gated). Tunables: `gate_width` (~half a shaft width), `gate_probe`
(early-out radius), `highpass` (1 = shafts, 0 = raw additive for A/B).

## Controls (Clouds tab, registry-driven)

| control | knob | does |
|---|---|---|
| god rays (screen-space) | `godrays` | enable the layer (default off) |
| god ray strength | `godray_strength` | beam intensity (exposure). Showcase preset = 14 |
| god ray length | `godray_length` | LOWER = longer beams (sample step) |
| god ray decay | `godray_decay` | shaft falloff (→1 = longer) |
| god ray cloud reach | `godray_cloud_radius` | how far from the sun clouds seed shafts |
| god ray cloud threshold | `godray_cloud_lum` | luminance split: cloud vs clear sky (occ_mode 2) |
| god ray backlit | `godray_backlit` | flip occluder polarity for sun-behind-cloud scenes |

## How to test

Windowed, absolute path (memory `wg16-launch-absolute-path`):

```
Godot ... --path C:/Wg16/wg-16-project res://scenes/terrain_lab.tscn -- \
  --preset=5 --godrays=1 --lookatsun
```

- The effect is **view-dependent** (screen-space) — point the camera toward the sun; it fades out
  smoothly as the sun leaves the screen.
- Judge ON vs OFF: the world (terrain + sky) must look **identical** off-vs-on except for the added
  shafts — no haze, no desaturation. Use `--godrayab=<path>` for a drift-free pair.
- `--godraydbg=3` (isSky: terrain must be red = excluded), `=5` (scatter viz: bright only in the sky
  shafts), `=1` (occlusion mask: clouds and clear sky correctly split).
- **Do NOT trust raw pixel diffs** between separate frames/runs here: SDFGI temporal jitter (~8–20)
  and the deterministic cloud-shadow drift swamp the god-ray signal. Use the debug views, the live
  UI toggle, or `--sdfgi=0 --clouds=0` for a deterministic read.

## Do-not-retry (failed approaches, see memory `godray-emission-vs-albedo-rootcause`)

Froxel/volumetric fog (3× — washy fog, not beams); occ_mode 3 as default (too soft); the angular-mean
high-pass (5× cost + residual fog); the structure-gate multiplier (rim-lit cloud edges); opaque
`scene + rays` blend (double-processes the frame).
