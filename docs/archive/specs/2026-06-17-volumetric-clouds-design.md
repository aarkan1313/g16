# Volumetric Clouds + Matched Terrain Shadows — Design Spec

Date: 2026-06-17 · Status: awaiting user review · Lane: lighting/atmosphere (sky + scene + Presenter)

## Why

The fresh ground-only cloud shadows (2026-06-17) read better than the cut version but the
user's verdict surfaced the real requirement: **shadows on the ground with an empty sky
are uncanny** ("a waste without a cloud up there to create the shadow"), and coverage was
too dense (real cloud shadows are sparse — mostly-lit ground, occasional drifting dark
patches). The fix is real **volumetric clouds in the sky that cast the shadows you see**,
as one coupled system, not a ground-only trick.

Deep research (2026-06-17, see memory `volumetric-clouds-research`) confirms this is proven
in Godot and identifies the cheapest-good path.

## Decision (locked in brainstorming)

Build **raymarched volumetric clouds** following the HZD/Nubis stack, using the
**clayjohn Godot pattern** (compute raymarch → texture → sky shader, temporally amortized),
and produce terrain shadows from a **2D cloud-shadow map** sampled from the *same* density
field — so overhead cloud and ground shadow **match by construction**.

**Implementation posture (user directive): reference, don't copy.** clayjohn's demo and the
HZD/Nubis talks are studied for the *technique and structure* (how to amortize, how the
density+lighting math works, how `TextureRD` flows compute→sky), but we write our OWN AAA
implementation that fits WG16's existing patterns (FieldCompute/SplatCompute local-RD bake,
the data-driven Clouds tab, the inert-when-off `light()`). No code is lifted.

**Grounded in verified research:**
- **Reference:** `clayjohn/godot-volumetric-cloud-demo-v2` — compute-shader raymarch writes a
  `TextureRD`, read in the **sky shader**, raymarch **amortized over ~64 frames** (the
  dominant affordability trick; ~20× faster via Godot 4.2+ `TextureRD`). Works on 4.6.
- **Density field:** **Perlin-Worley** 3D noise (low-freq shape) + Worley detail (erode
  edges), modulated by a 2D **weather/coverage** map (controls where clouds are + how much).
- **Lighting:** **Beer's law** transmittance + **Henyey-Greenstein** phase + **powder** term
  (the silver-lining look), a short light-cone march toward the sun per dense sample.
- **Affordability:** low-res raymarch + temporal reconstruction is THE lever (full-res
  without temporal was refuted). Quarter-res + temporal ≈ 2.4 ms @ 1080p in one cited impl.
- **Shadow coupling:** bake a 2D cloud-shadow map by sampling the density field downward
  along the sun direction; sample it in WG16's existing custom `light()` sun-attenuation.

## Architecture (additive units — clouds are a new subsystem; ground plumbing is REUSED)

```
noise bake (once):  CloudNoiseCompute.cs + cloud_noise_3d.glsl
   3D Perlin-Worley shape tex (R) + 3D Worley detail tex (R)   ─┐
                                                                 │
weather/coverage (2D, cheap, tweakable: coverage/type/drift) ──┤
                                                                 ▼
per-frame (amortized over N frames, reduced res):
   cloud_raymarch.glsl (COMPUTE)  ── raymarch density+light ─> cloud_tex (RGBA: color+transmittance)
        │                                                        │
        │  (also, cheaply, downward-from-sun march)              ▼
        └─> cloud_shadow_map (2D, R = sun transmittance)   sky.gdshader reads cloud_tex
                         │                                   (composites clouds into sky)
                         ▼
   terrain_lab.gdshader light(): sample cloud_shadow_map in WORLD xz  →  attenuate SUN
        (REUSES the existing light() + Clouds tab + sun-only attenuation from 2026-06-17)
```

| Unit | File | Job | Status |
|------|------|-----|--------|
| 3D noise bake (GPU) | `shaders/cloud_noise_3d.glsl` + `scripts/lab/CloudNoiseCompute.cs` | Bake Perlin-Worley shape + Worley detail 3D textures once. | NEW |
| Raymarch (GPU) | `shaders/cloud_raymarch.glsl` + `scripts/lab/CloudVolume.cs` | Per-frame (amortized, reduced-res) raymarch → cloud color/transmittance texture + sun shadow map. | NEW |
| Sky composite | `shaders/cloud_sky.gdshader` (sky shader) on the scene's Sky | Read cloud texture, composite clouds into the sky behind terrain. | NEW |
| Shadow coupling | `shaders/terrain_lab.gdshader` `light()` | Sample the cloud-shadow map (world xz) to attenuate the sun. | REUSE/EDIT existing light() |
| Controls | `data/lab_controls.json` Clouds tab | Coverage/density/type/drift/lighting knobs. | REUSE/EXTEND existing tab |
| Removed | standalone `cloud_coverage.glsl` ground-only path | Superseded by the real shadow map. | RETIRE (keep in git history) |

Untouched: Field, base field math, Workbench, splat, material system.

## Affordability plan (the knobs that keep it real-time)

- **Reduced-res raymarch:** raymarch at 1/4 res (or half), reconstruct/upsample. Tunable.
- **Temporal amortization:** spread the hemisphere update over N frames (clayjohn: ~64),
  interpolate to hide updates. Camera here moves slowly (look-lab fly) → cheap & stable.
- **Adaptive steps:** big empty-space steps until density, then fine steps; early-out when
  transmittance ≈ 0. Light-cone march short (few samples, decreasing LOD).
- **Shadow map cheap:** the downward sun-transmittance map is low-res 2D, baked alongside
  the main march, updated at the same amortized cadence.
- All step counts / res / N exposed as knobs so cost vs quality is dialed live.

## Controls (Clouds tab — extends the existing group)

Keep `cloud_enabled`, `cloud_strength` (shadow darkness), `cloud_drift_dir`. Add/repurpose:
`coverage` (sparse↔overcast — default LOW per the user's note), `cloud_density`,
`cloud_type` (wispy↔towering), `cloud_altitude`, `cloud_thickness`, `drift_speed`, lighting
knobs (HG anisotropy, powder, sun absorption), and perf knobs (raymarch steps, update res,
temporal frames). Sparse coverage is the DEFAULT.

## Testing / judging

- **Mechanical:** dotnet build, headless `--import` (shaders compile), `--auto-shot` (sky
  shows clouds, terrain shadows present, no crash, frame time printed). Stills only catch
  gross breakage.
- **THE gate — user flies it live in MOTION** (banked lesson; clouds are a motion artifact):
  do the clouds read as clouds? Are shadows sparse + matched to clouds overhead? Drift
  coherent? Frame time acceptable on the user's GPU? Toggle off = exactly today's look.
- **Perf is a first-class judgment here** (unlike pure-look features): if it's too heavy on
  the user's machine, lower res / steps / temporal-frames knobs are the dial.

## Risk / undo

Largest feature since the base field — new sky subsystem + per-frame compute. Mitigations:
behind `cloud_enabled` (off == today's look exactly, via the existing inert `light()`);
built piece-by-piece with live judgment at each (noise → sky clouds visible → shadow
coupling → perf tune); `git checkout .` / branch reverts; base field untouched. Per the
3-failed-fixes rule, if the look or perf can't satisfy the user's eye after fair tuning, we
stop and reconsider (e.g. fall back to the cheaper 2D sky-cloud layer) rather than thrash.

## Build order (incremental, eye-gated — each step judged before the next)

1. **3D noise bake** (Perlin-Worley shape + Worley detail) → verify textures look right
   (debug view / slice).
2. **Raymarch + sky composite, full-res, no temporal yet** → get clouds VISIBLE in the sky;
   user judges the cloud LOOK live (shape, lighting). Perf bad here is expected.
3. **Add reduced-res + temporal amortization** → user judges perf + temporal stability live.
4. **Bake the sun-transmittance shadow map; wire into `light()`** → user judges that ground
   shadows are sparse, match the clouds overhead, drift coherently.
5. **Tune via the Clouds tab** (coverage low, density, lighting). Retire the old
   ground-only `cloud_coverage` path once the real shadow map replaces it.
6. On approval: DECISIONS entry, HANDOFF §6 refresh, consider snapshotting cloud settings
   into mood presets.

## NOT doing (YAGNI)

- No multiple-scattering / fully physical atmosphere coupling beyond aerial perspective.
- No cloud self-shadowing beyond the short light-cone march.
- No weather *simulation* (just an artist-tweakable coverage/type field).
- No engine fork. No tessellation (Godot lacks it; irrelevant here — clouds are volumetric).
- Cheaper 2D sky-cloud layer kept only as the documented FALLBACK if volumetric can't hit
  the user's perf/look bar.
