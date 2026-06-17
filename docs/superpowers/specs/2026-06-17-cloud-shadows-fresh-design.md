# Cloud Shadows (fresh rebuild) — Design Spec

Date: 2026-06-17 · Status: awaiting user review · Lane: lighting/atmosphere (Presenter + scene)

## Why

Cloud shadows are the immediate "good → great" lever after the lighting/mood pass. The
first attempt (cut 2026-06-16, preserved on `backup/clouds-system-2026-06-16`) failed
three times and was removed. The fresh build must not repeat its mistakes.

**What killed the old one (banked lessons — do not relearn):**
1. **Value-noise patches, low contrast** → read as uniform/square blobs, not clouds.
2. **Wavelength too large** → uniform, not patchy.
3. **Albedo-darkening** → washed out by SDFGI + SSIL ambient (GI refilled the darkening).
4. **Debugged from stills** → the "square" is a MOTION artifact; stationary captures
   couldn't reproduce it, so fixes chased the wrong thing.

## Decision (locked in brainstorming)

Build a **hybrid noise-warped cloud-coverage system** that **attenuates the direct sun
term only** (not albedo), judged **live in motion early**, behind a master toggle, with a
full control group so "idea wrong" is distinguishable from "tuning wrong".

- **Source:** a seamless tiling grayscale cloud-coverage texture, **GPU-compute-baked
  once at load** (mirrors `SplatCompute`/`FieldCompute`), seeded, re-derivable. Real
  cloud structure (high-contrast thresholded FBM), not flat value noise → no squares.
- **Anti-tiling:** the texture UVs are **domain-warped** by a cheap noise sample so a
  single tiling texture never reads as a repeating lattice.
- **Attenuation point:** a custom `light()` multiplies the **direct sun diffuse +
  specular** by the cloud factor. Ambient / GI / SSIL stay full → shadowed ground reads
  *lit-but-sunless* (physically what a cloud shadow is) and **GI cannot wash it out**.
- **Godot-4.6 reality:** the engine's directional-shadow term is not user-injectable
  without forking the renderer (rejected — off-policy, fragile). Custom `light()` gives
  the same visual result on the supported path. This re-introduces a `light()` (removed
  with the old clouds); when clouds are OFF it reproduces Godot's default Lambert/Schlick
  **bit-for-bit**, so nothing else regresses.

Rejected in brainstorming: pure-procedural-in-`light()` (reverts to the noise approach
that struggled; costs more/fragment), global `light_energy` modulation (one value, no
moving patches), engine fork (too big), volumetric cloud layer (much bigger system).

## Architecture (three small additive units — one job each)

```
seed ─> cloud_coverage.glsl (GPU compute, ONCE at load)
            └─ seamless tiling high-contrast FBM coverage  ─> float[]
                                                              │
                              CloudCompute.cs (dispatch + readback) ─> ImageTexture
                                                              │   (repeat_enable, mipmaps)
                                                              ▼
data/lab_controls.json  ──> TerrainLabUI ──> SetShaderParameter ──┐
  (new "Clouds" group)                                            ▼
                                       terrain_lab.gdshader  light()
                                         world-XZ uv, TIME drift, noise warp,
                                         sample coverage (mipmapped), shape by
                                         coverage/softness, attenuate SUN term only
```

| Unit | File | Job | Depends on |
|------|------|-----|-----------|
| Coverage bake (GPU) | `shaders/cloud_coverage.glsl` *(new)* | Compute a seamless tiling high-contrast cloud-coverage texture from a seeded FBM. Pure function of texel position. | RenderingDevice only |
| Bake dispatch (C#) | `scripts/lab/CloudCompute.cs` *(new)* | Dispatch once, read back → `ImageTexture` (repeat + mipmaps). Mirrors `SplatCompute`. | RenderingDevice |
| Sun attenuation | `shaders/terrain_lab.gdshader` *(edit)* | Custom `light()`: attenuate direct sun by the cloud factor; exact default when off. | coverage texture + uniforms |
| Wiring | `scripts/lab/TerrainLab.cs` *(edit)* | Own the bake (one line, like splat); bind texture; pass cloud uniforms through existing `SetFloat/SetBool/SetInt`. | CloudCompute |
| Controls | `data/lab_controls.json` *(edit)* | New **Clouds** tab + group, pure data (no per-control C#). | — |

Untouched: Field unit, base field math, Workbench, other scenes, SplatCompute.

## Cloud factor (in `light()`, world-space)

1. `uv = WORLD.xz / cloud_scale_m` — patch size in meters (world-space → shadows sit on
   the ground, not the screen; that was a prior failure class).
2. `uv += TIME * dir * cloud_drift_speed`, where `dir = vec2(cos,sin)` from
   `cloud_drift_dir` (degrees). `TIME` is the shader built-in (advances automatically —
   no per-frame C# push).
3. **Domain warp:** sample the coverage texture at a large scale, offset `uv` by
   `cloud_warp * (warpSample - 0.5)`. Breaks the tiling lattice.
4. **Coverage sample:** `c = texture(cloud_tex, uv).r` — mipmapped (the fuzz lesson; no
   minification crawl in motion).
5. **Shape:** `shadow = smoothstep(cov - soft, cov + soft, c)` →
   `cloud_coverage` slides how much sky is clouded, `cloud_softness` the edge width.
6. **Attenuate sun only:** `sun_mul = 1.0 - cloud_strength * (1.0 - shadow)`; multiply the
   direct sun diffuse + specular by `sun_mul`. Ambient/GI untouched.

`cloud_enabled == false` → `light()` runs the stock Lambert/Schlick path with
`sun_mul = 1.0`. Verified A/B that off == today's look.

## Clouds control group (`data/lab_controls.json`, new "Clouds" tab)

All `type: slider`/`toggle`, `param` straight to the uniform (no C# per control). Default
ON so it is judged immediately.

| id | label | type | param | range | default |
|----|-------|------|-------|-------|---------|
| `cloud_enabled` | clouds | toggle | `cloud_enabled` | — | true |
| `cloud_strength` | strength | slider | `cloud_strength` | 0–1 | 0.6 |
| `cloud_coverage` | coverage | slider | `cloud_coverage` | 0–1 | 0.5 |
| `cloud_softness` | softness | slider | `cloud_softness` | 0.01–0.5 | 0.15 |
| `cloud_scale_m` | scale m | slider | `cloud_scale_m` | 200–4000 | 1200 |
| `cloud_drift_speed` | drift speed | slider | `cloud_drift_speed` | 0–0.02 | 0.004 |
| `cloud_drift_dir` | drift dir | slider | `cloud_drift_dir` | 0–360 | 45 |
| `cloud_warp` | warp | slider | `cloud_warp` | 0–1 | 0.4 |

`rand: false` on the cloud group initially (it's a lighting/atmosphere knob, not a
material roll — keep it off the randomizer until the look is dialed; revisit later).

## Testing / judging

- **Mechanical (self-check):** `dotnet build`, headless `--import`, then `--auto-shot` A/B
  (`cloud_enabled` true vs false) — confirms it compiles, the bake runs, the scene draws
  clean, and OFF matches the no-cloud render. This only proves it RENDERS.
- **The real gate is the user flying it in motion** — early, before any polish: drifting
  patches, no squares, no lattice, GI not washed out. Toggle isolates it instantly. If the
  square returns, it's isolated to this system and we judge live, never from a still.

## Risk / undo

Presenter + scene only; base field untouched. Custom `light()` re-introduced but inert
when clouds off (verified == default). `git checkout .` reverts. Per the 3-failed-fixes
rule, if the fresh build reads wrong after a fair live look, we stop and reconsider the
approach rather than patch repeatedly.

## Build order (incremental, eye-gated)

1. `cloud_coverage.glsl` + `CloudCompute.cs`; bake at load, bind texture. Verify the
   texture is seamless + high-contrast (debug view or direct inspect).
2. Add the `light()` sun attenuation + cloud uniforms to `terrain_lab.gdshader`; verify
   OFF == default (A/B headless).
3. Add the Clouds tab/group to `lab_controls.json`; wire bake into `TerrainLab.Build`.
4. Headless A/B render (enabled vs disabled) — compiles, draws clean.
5. **User flies it in motion** — judge shape, drift, no squares, GI intact. Tune via the
   group. Only then consider adding it to moods/presets.

## NOT doing (YAGNI)

- No engine fork / true shadow-map injection (off-policy).
- No volumetric cloud layer or god-rays (bigger system; only if asked later).
- No mood-preset integration yet (clouds judged standalone first; snapshot into moods
  only once the look is approved).
- No reuse of the old `backup/clouds-system` code — reference only.
