# "Make Clouds Good" — Fresh-Chat Handoff Prompt

Paste the block below into a NEW chat dedicated to cloud look-polish. It is self-contained.

---

You are continuing WG16, a procedural terrain generator in **Godot 4.6 mono (C# + GPU compute,
Vulkan)**. Your ONE job this chat: **take the volumetric cloud system from "doesn't look bad"
to genuinely GOOD/AAA.** The clouds work and are mechanically proven; the LOOK needs polish.
Do NOT work on terrain/ground/erosion/anything else — clouds only.

## Pillars (the standard for every choice)
quality = performance = AAA-ish = best-long-term, **regardless of time cost**. Lead with the
most-correct option, not the cheap shortcut. The user's EYE is the only gate for look — build
behind live toggles, put results in front of them early, judge IN MOTION (never from a still).

## How to run / verify (heed these — they cost days to learn)
- **Project dir:** `C:\Wg16\wg-16-project`. Build: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj`.
- **Launch (windowed, ALWAYS absolute path — the shell cwd is the PARENT `c:\Wg16`, so `--path .`
  opens the Godot launcher, not the scene):**
  `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan res://scenes/terrain_lab.tscn`
  Godot exe: `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`
- **ONE Godot at a time** (GPU contention → grey-screen). Kill first:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **local-RD compute CANNOT read back under `--headless`** (returns null → NullRef). The noise
  bake + the numeric shadow check run WINDOWED only.
- **Numeric shadow self-check** (proves shadow↔cloud coupling with MATH, not eyeballing — use it
  after any density change as a regression guard): launch with `-- --shadowcheck --coverage=0.5`;
  console prints PASS/FAIL + a correlation r (want >0.6 at mid coverage; it reports SATURATED at
  very high coverage where r isn't meaningful) + a decoded layer dump (layer0.altitude should be
  ~1800, layer_count 2 — proves the GPU buffer layout is right).
- CLI flags: `--coverage=`, `--clouds=0/1`, `--cloudsteps=`, `--clouddbg=1` (raw cloud tex),
  `--shadowcheck`, `--shadowdbg=1` (paint shadow factor on terrain), `--godrays=0/1`, `--profile`.
- Commit when work is verified. End commit messages with the project's Co-Authored-By line.

## Current cloud architecture (all working)
Heavy compute produces fields; cheap consumers sample them (WG16's DNA). Files:
- `scripts/lab/CloudNoiseCompute.cs` + `shaders/cloud_noise_3d.glsl` — bake-once tileable 3D
  volumes: 96³ shape (R=Perlin-Worley, G/B/A = 2-octave Worley FBM bands) + 32³ detail (2-octave
  Worley). Local-RD bake at load.
- `scripts/lab/CloudWeather.cs` — 256² 2D weather field: R = high-contrast multi-octave coverage
  × a large-scale "cloud-system" mask (cloudy regions + clear lanes), G = type, B = density bias.
- `scripts/lab/CloudLayers.cs` + `data/cloud_layers.json` — **N cloud decks** (up to 8; each:
  altitude/thickness/size/cell_scale/coverage_weight/density/opacity/type/edge/detail/noise_id).
  Layer 0 = the legacy flat knobs at runtime (regression-safe). Seed config = a low cumulus deck
  (1500m) + a high cirrus deck (5500m).
- `shaders/cloud_raymarch.glsl` — the march: camera-anchored curved shell over the FULL span of
  all decks; per step sums all decks (`density_all` → `layer_density`); empty-space skip + ~64
  fine samples/deck. Lighting = 3-octave multiple-scattering + dual-lobe phase + base-occluded
  ambient + view-gated powder. Writes a **512×128 lat-long hemisphere texture** (Texture2Drd).
- `shaders/cloud_shadow.glsl` — same `layer_density` (BYTE-IDENTICAL — the coupling guarantee),
  top-down sun-march → 2D shadow map the terrain `light()` samples in world XZ.
- `shaders/cloud_sky.gdshader` — samples the cloud texture by EYEDIR; draws the sun disc+glow.
- `scripts/lab/CloudVolume.cs` — owns it all; drives the per-frame raymarch + shadow compute on
  the RENDER THREAD via `RenderingServer.CallOnRenderThread` (NOT a CompositorEffect — that races
  the Texture2Drd RID). Public knob setters are the only UI surface.
- `scripts/lab/Std430Writer.cs` — **alignment-correct std430 param-buffer writer. USE IT for any
  param-buffer change.** Hand-packing these buffers as a flat float run drifts offsets on every
  vec2/vec4 (vec2→8B, vec4/array→16B align) → scrambled GPU data → "no clouds". This bit us 3×.
  Layers are packed as `vec4[24]` (3 vec4/layer); a `float[]` array has a 16B stride in std430.
- `data/cloud_params.json` (flat knobs) + `data/cloud_presets.json` (Clear…Stormy) +
  `data/lab_controls.json` (the Clouds-tab registry: `"type":"cloudf"`+`"cloud":"<knob>"` →
  `CloudVolume.SetKnob`; a `param` naming a missing uniform SILENTLY no-ops).

## What was already fixed (an external shader audit drove these — don't redo)
Multiple-scattering lighting, base-occluded ambient (dark base/bright tops), dual-lobe phase,
empty-skip/~64-samples-per-deck stepping, high-contrast multi-octave weather + cloud-system mask,
stronger edge-biased erosion, per-scale wind (no lockstep), 2-octave Worley FBM noise. After all
that the user's verdict was **"doesn't look bad overall"** — i.e. acceptable but not yet "good".

## Your work list (ordered; the user wants ALL of it, then god rays last)
1. **Per-deck phase/albedo** — currently all decks light identically, so cumulus vs cirrus don't
   read as different cloud KINDS (and the user can't see the height/deck difference). Carry
   per-deck density at each march sample and accumulate per-deck luminance sharing one
   transmittance T: cumulus = forward-scattering, brighter-cored; cirrus = near-isotropic, thin,
   bright. This is the top "looks real" + "makes layers visible" lever.
2. **Presets → layers** — presets only drive layer 0 today, so the multi-deck system is invisible
   through the preset picker. Let presets define/select a layer stack (e.g. a `layers` block in
   `cloud_presets.json`) so Overcast/Stormy actually stack the decks. Build the seam so a future
   weather/biome system can drive per-layer weights (already designed for).
3. **Ranged presets + "surprise me"** — center+spread per knob/layer so a preset isn't identical
   every load (seeded), plus a coherent global "randomize sky".
4. **Temporal reconstruction** — `temporal_frames` is clamped to 1 (no reconstruction). Turn on
   amortization (reproject prior frame + blend strided new samples + disocclusion reject) so you
   can afford higher step counts / sharper detail at the mid-range budget. This is the perf lever.
5. **Texture-res bump** — 512×128 → 1024×256 as a stopgap for horizon blockiness. The real
   long-term answer (raised by the audit) is to drop the lat-long texture and march in VIEW SPACE
   at half-res + TAA — the lat-long buffer decouples cloud res from screen and compresses badly at
   the horizon. Treat the view-space rewrite as the deeper option if res-bump + the above aren't
   enough to hit "good"; discuss with the user before that big a change.
6. **God rays LAST** — rebuild as **in-march in-scatter** (accumulate sun in-scatter toward the
   eye in the existing raymarch → crepuscular rays through cloud gaps, matching the real clouds).
   NOT a FogVolume (the old one collided with the sun's volumetric shadows → black wedges; it was
   removed). Optional screen-space radial-shaft boost on a toggle.

Performance target: a **mid-range GPU** (dev machine is an RTX 5090, so headroom exists for the
look lab, but ship-budget is mid-range — lean on temporal amortization). Must stay **tunable**
across photoreal / stylized / sparse via knobs + presets.

## Process
Use brainstorming→spec→plan for anything structural (per-deck lighting rework, temporal recon,
view-space rewrite); just-do-it for tuning. After each density-affecting change run `--shadowcheck`
(coupling regression) + launch for the user's eye. There's a `STOP` ethic: if a lever can't reach
"good" after a fair effort, say so — don't grind. Reference material lives in the repo:
`docs/cloud-look-audit-prompt.md` (the audit), `docs/superpowers/specs/2026-06-17-cloud-atmosphere-
refactor-design.md`, `docs/superpowers/specs/2026-06-18-multi-layer-clouds-design.md`, and the
user's `~/.claude` memory notes (`cloud-look-audit-findings`, `std430-packing-helper`,
`cloud-shadow-dome-mismatch`, `volumetric-clouds-research`, `wg16-launch-absolute-path`,
`compute-to-material-callonrenderthread`, `headless-no-local-rendering-device`).

Start by reading those memory notes + the audit, launch the current scene to see the baseline,
then begin with #1 (per-deck phase/albedo).
```
