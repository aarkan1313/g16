# WG16 Cloud System — Overview (architecture · features · how to test)

Status: 2026-06-18, after the cloud-polish session. This is the map for reviewing the cloud
system **feature by feature**. Every feature is behind a toggle defaulting to the validated look.

## Modules (separation of concerns — one job each, one-direction data flow)

```
DATA / BAKE (pure, no scene/march knowledge)
  CloudParams.cs      — flat knob defaults + JSON load (data only)
  CloudLayers.cs      — per-deck data: struct, parse (file + preset), pack→GPU buffer,
                        WithCumulusLighting (single source of truth for layer-0 lighting)
  CloudWeather.cs     — 2D weather field bake (R=coverage, G=type, B=density, + macro systems)
  CloudNoiseCompute.cs— 3D noise volume bake (shape RGBA + detail R), drives cloud_noise_3d.glsl
  Std430Writer.cs     — alignment-correct std430 buffer writer (the anti-scramble guarantee)
        │
        ▼
ORCHESTRATION
  CloudVolume.cs      — owns RD resources + render-thread dispatch (CallOnRenderThread),
                        builds param buffers (Std430Writer), the public knob interface
                        (UI → here ONLY), the active layer stack (SetLayers/SetLayerWeight),
                        the sky + shadow Texture2Drd, the overcast CPU proxy
        │
        ▼
SHADERS (consume buffers; produce textures)
  cloud_noise_3d.glsl — bakes the volumes (value + gradient noise, Worley FBM)
  cloud_raymarch.glsl — camera-anchored curved-shell march → lat-long dome texture
  cloud_shadow.glsl   — same density field, top-down sun march → 2D ground shadow map
  cloud_sky.gdshader  — samples the dome by EYEDIR, premultiplied over-composite, sun disc
        │
        ▼
CONSUMERS  — sky composite (dome) · terrain light() (shadow map) · overcast dim/aerial

DIAGNOSTICS (numeric, windowed — prove with math, not eyeballing)
  CloudShadowCheck.cs (--shadowcheck) · CloudLightCheck.cs (--lightcheck) · cloudstats readback
```

**The coupling guarantee:** `cloud_raymarch.glsl`, `cloud_shadow.glsl`, and `cloud_shadow_check.glsl`
must compute the per-deck density **byte-identically** (same `layer_density`, same SHAPE_SCALE /
cellScale / DETAIL_SCALE consts, same `LF(i,f)` stride). The shadow on the ground only matches the
visible cloud if these stay in lock-step. Any density-affecting edit MUST keep all three identical
and re-run `--shadowcheck` (PASS = Pearson r > 0.6 of vertical-density vs ground-darkness).
The lighting fields (per-deck phase/albedo/tint, layer fields 12–18) are raymarch-only — the shadow
shader ignores them, so lighting changes never touch coupling.

## Feature inventory + how to test each

All toggles default to the validated look (so the base is stable); flip one at a time.

| Feature | Toggle / control | Default | How to verify |
|---|---|---|---|
| Clouds on/off | `cloud_enabled` / `--clouds=0/1` | on | sky has/has-no clouds |
| Coverage (sky fill) | `cloud_coverage` / `--coverage=` | 0.55 | `--cloudstats` skyCovered% ≈ knob |
| Per-deck lighting | `cloud_perdeck` / `--perdeck=0/1` | 1 (on) | `--lightcheck` (cumulus vs cirrus Δ); `--deckdbg` overlay |
| Deck-ID overlay (debug) | `cloud_deckdbg` / `--deckdbg=1` | off | clouds flat-colored by deck (cumulus=red, cirrus=cyan) |
| Presets / layer stack | preset picker / `--preset=0..4` | — | console logs "layer stack set — N deck(s)"; Overcast=3 |
| Coherent randomize | Clouds-tab Randomize | — | logs "applied preset '…' (jittered)" — always a sane sky |
| God rays | `cloud_godrays` + `cloud_godray_strength` / `--godrays=1` | off | brightening/shafts toward sun through gaps; no black wedges |
| Temporal amortization | `cloud_temporal` / `--temporal=N` | 1 (off) | cheaper frame (`--profile`); watch for drift shimmer |
| Dome resolution | `--cloudtex=H` (launch only) | 512×128 | sharper horizon at 1024×256 (`--cloudtex=256`) |
| Raymarch steps | `cloud_steps` / `--cloudsteps=` | 128 | perf vs quality |

Diagnostics: `--shadowcheck` (coupling), `--lightcheck` (per-deck lighting delta),
`--cloudstats` (dome readback: skyCovered%, meanAlpha, meanCloudLuma — NOTE dome-averaged, so
divide by coverage for per-cloud values), `--profile=secs` (avg/worst fps), `--cam=x,y,z,pitch,yaw`
+ `--auto-shot=PATH` (capture a frame).

## What makes the clouds read "good" now (the root-cause fixes)

The earlier audit claimed lighting/macro/stepping were done; they had real bugs. Fixed:
1. **Premultiplied composite** — sky was `mix(bg,rgb,a)` on premultiplied radiance → ~2× too dim.
2. **Sun extinction** — sun-march const 0.02 → od~10–30 → no direct sun → ambient-only grey.
3. **Weather** — dead macro octave (Fbm baseFreq 1 = constant) + mean crushed to 0.32 → sparse,
   non-intuitive coverage. Now mean ~0.56, real weather systems, coverage ≈ sky-fill.
4. **Scale** — SHAPE_SCALE 1/9000 + cellScale 0.35 → giant blobs. Now 1/6000 + 0.7 → many clouds.
5. Gradient (Perlin) base noise; multi-scatter direct+fill (dark cores = form); 2-octave erosion.

## Performance (RTX 5090; relative cost scales to mid-range)

| config | frame | cloud cost |
|---|---|---|
| clouds off | 9.4 ms | — |
| default (512×128) | 10.7 ms | ~1.3 ms (cheap, ship-ready) |
| + god rays | 11.7 ms | +1.0 ms |
| + temporal stride 4 | 10.2 ms | amortizes (helps most at hi-res) |
| 1024×256 | 13.3 ms | +2.6 ms (pair with temporal) |

## Not done (eye-gated — needs the user's review in motion)

- View-space half-res march + TAA (the "real" #5; `--cloudtex` is the stopgap). Big architectural
  change — brainstorm → spec before building.
- Horizon / distant-sky handling (cloud band cutoff at the horizon).
- Sun-disc shader polish (flat bright circle today).
- Snapshot cloud settings into the mood/time-of-day presets.
