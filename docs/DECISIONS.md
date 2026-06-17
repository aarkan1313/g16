# WG16 — Decisions Log

One short entry per decision, newest first. The point is to not re-litigate
settled choices and to give "low plans" a memory. Link a spec only when a feature
was big enough to warrant one. Date · what · why.

---

**2026-06-17 — GROUND TEXTURING rebuild started (it's "functional but bad").** User
verdict (live): the ground reads repetitive + flat + muddy-blended + drab at ALL ranges,
and swapping/randomizing materials doesn't help → the **application/presentation layer was
never built out or validated** (the materials are fine — usage is the problem). Root cause
confirmed in code: the DEFAULT splat path (`tp_alb/tp_nrm/tp_rgh`) used plain `textureGrad`
with **zero anti-tiling** (the IQ/hex `tiled()` only fed the off-by-default non-splat path).
Decided (per pillars: everything an AAA terrain has, done right) to rebuild the whole
ground-presentation layer as a **6-unit arc** with shared shader seams — spec:
`docs/superpowers/specs/2026-06-17-ground-presentation-arc-design.md`. Units, build order
by impact: **(1) anti-repetition** (stochastic texture bombing — BUILT, `ar_sample_wp`, the
single material-fetch seam; awaiting live verify — it's the gate for 2-6) → **(2) distance
detail** (near detail ⊕ far macro by `distanceWeight`) → **(3) surface depth** (parallax-
occlusion, no tessellation in Godot; + bind AO, make normal/rough drive BRDF) → **(4)
procedural breakup** (GPU-compute bake of slope/curv/cavity/aspect masks → vary material per
context; the prime GPU-compute unit) → **(5) color/value** (replace washed macro tint, tuned
to survive GI+AgX) → **(6) "and more"** (scatter hooks/wetness/snow-by-aspect/hi-Q triplanar,
last). Units 2-6 are PLANNED (one plan each in docs/superpowers/plans/), not built. Shared
seams locked: `ar_sample_wp` (fetch), `distanceWeight` (LOD), `groundData`/`breakup_tex`
(baked masks, Unit 4). C# + GPU-compute focus throughout.

**2026-06-17 — Cloud system AUDITED + fixed (read-only audit → 8 real findings fixed).**
A skeptical read-only audit of the cloud + presence work found 8 valid issues (verified
against code, not taken on faith). Fixed per pillars: **H1** the custom terrain `light()`
was a cheap Lambert+Blinn that REPLACED Godot's built-in BRDF unconditionally (a custom
`light()` has no fall-through) → rewrote it as a faithful Burley-diffuse + GGX (D/V/F)
replica so clouds-off ≈ the approved built-in look (still wants an eye-confirm vs `main`).
**H3** mood/sun were pushed in `_Ready` before the deferred `AttachClouds`, so they
no-opped → clouds used defaults at spawn; now re-applied after attach. **L1** cloud
footprint hardcoded 8192 → from `FieldParams.RegionSizeM`. **M1** `UpdateOvercast` rewrote
sun/ambient every frame (sun slider non-1:1) → dirty-flag, only on overcast change. **M4**
shadow sun-march started at sea level → terrain mid-elevation. **M3/L2/L5/L3** exit cleanup,
defer `cloud_shadow_on` until the shadow RID is live, dead line removed, temporal stride
clamped to 1 (no reconstruction yet → stride>1 would smear; future upgrade). **H4** (god-ray
fog halo at terrain edges) deferred to the god-ray live-tuning pass (default-off). Audit
also confirmed the honest claims (CallOnRenderThread, ~2ms, match-by-construction shadows).

**2026-06-17 — CLOUD PRESENCE suite (clouds affect the scene, not just the sky).** After
the volumetric clouds shipped, added the AAA "presence" effects so clouds influence the
whole render — all driven from the cloud field, all ~free (CPU scalars + existing-pipeline
tweaks; full suite measured 140 fps vs 139 clouds-only). **(1) Mood/time-of-day cloud
color:** mood `sky_top/horizon` colors feed the raymarch ambient + sky-shader background
(the old `is ProceduralSkyMaterial` check skipped our cloud ShaderMaterial) → golden-hour
warm clouds, storm grey, A/B verified. **(2) Coverage scalar:** `CloudVolume.Overcast()` =
CPU proxy from the live coverage knob + `CloudWeather.Mean` (no GPU readback). **(3)
Overcast dims ambient/sun:** `UpdateOvercast` scales sun energy down + sky-fill up as
coverage rises, from the mood base → heavy cover reads as flat overcast (A/B verified).
NOTE: this is an OPEN-LOOP proxy — it tracks the coverage *slider* + weather mean, not the
actual rendered density (HG/edge/type knobs don't feed it), so very dense-but-low-coverage
clouds won't dim. Honest fidelity caveat, not a bug.
**(4) Aerial perspective:** `FogLightColor` blended toward the cloud horizon color, stronger
under overcast. **(5) Reflections/GI:** the cloud Sky already feeds Godot's sky-radiance →
ambient/SDFGI/reflections (that's why overcast works); set `roughness_layers=7`. **(6) God
rays:** research chose the user-picked "gap-aligned" path — a `FogVolume`
(`cloud_godray_fog.gdshader`) whose density is gated by the cloud-shadow map so sun
scatters through cloud GAPS; coverage drives the sun's volumetric scatter energy (bell
curve, peaks at broken cloud). **Built + wired but DEFAULT OFF** (Clouds-tab toggle + env
volumetric fog only enabled when on): first-pass fog density/energy darkens the scene and
volumetric-fog look can't be tuned from stills — needs live judgment, so it's gated rather
than risk the approved look. New CLI: `--coverage`, `--godrays`, `--profile`. Outstanding:
user live-judge of the coordinated look + enable/tune god rays.

**2026-06-17 — VOLUMETRIC CLOUDS + matched ground shadows (the fresh rebuild).** After
the ground-only cloud-shadow retry still read wrong ("a waste without a cloud up there"),
rebuilt as real raymarched volumetric clouds in the sky that cast their own matching
shadows. Spec: `docs/superpowers/specs/2026-06-17-volumetric-clouds-design.md`; plan:
`docs/superpowers/plans/2026-06-17-volumetric-clouds.md`. Technique = HZD/Nubis stack,
referenced from clayjohn's Godot demo, our own impl. Pipeline: **(1)** `cloud_noise_3d.glsl`
GPU-bakes tileable Perlin-Worley shape (96³) + Worley detail (32³) volumes + a 2D weather
field (`CloudWeather`), once at load. **(2)** `cloud_raymarch.glsl` raymarches a spherical-
shell density field (Beer + Henyey-Greenstein + powder + 6-sample light cone) into a
lat-long hemisphere texture. **(3)** `cloud_shadow.glsl` marches the SAME field top-down
toward the sun into a 2D transmittance map. **(4)** `cloud_sky.gdshader` samples the cloud
texture by EYEDIR; `terrain_lab.gdshader`'s re-introduced custom `light()` samples the
shadow map in world XZ to attenuate ONLY the direct sun (ambient/GI untouched, inert when
off). Because clouds + shadows come from one field, they match by construction.
**Perf (the hard part):** the per-frame compute MUST run on the render thread via
`RenderingServer.CallOnRenderThread` driven from a plain node (clayjohn pattern) — NOT a
CompositorEffect (that path raced the `Texture2Drd` RID, "binding not valid", unfixable in
the attempts made). Output texture RID assigned ONCE before any dispatch (Godot #118292).
Full system measured ~2 ms/frame (139 vs 192 fps, vsync-off mixed view) at 128 steps +
512² shadow map — amortization (`temporal_frames`) available but not needed, so default
temporal=1 (no smearing). Full Clouds tab: coverage/density/type/size/edge/detail/opacity/
brightness/ambient/altitude/thickness/drift/HG/powder/sun-absorption + ground-shadow +
perf knobs, 5 presets (Clear→Stormy), per-tab Randomize/Lock. Backup of the interim full-
res sky-shader path: tag `backup-clouds-skyshader-2026-06-17`. Lessons banked to memory:
local-RD compute can't run headless; CallOnRenderThread is the compute-to-material bridge.

**2026-06-16 — Cloud shadows CUT (to be rebuilt fresh).** Built a procedural cloud-
shadow system (drifting world-space FBM attenuating the sun via a custom `light()`),
but it persistently read as **square artifacts** to the user across 3 fix rounds
(wavelength too large → uniform-not-patchy; value-noise low contrast; albedo-darken
washed out by GI → moved to `light()`). Couldn't reproduce the square reliably in
headless captures. Per "spike cheap, judge live; the eye is the gate" + the 3-failed-
fixes rule, we removed the whole system rather than keep patching, and reverted to
Godot's default lighting (no custom `light()`). **The full impl is preserved on tag
`backup-before-cloud-removal-2026-06-16` and branch `backup/clouds-system-2026-06-16`**
for a fresh rebuild later. Everything else from the lighting arc stays.

**2026-06-16 — LIGHTING/ATMOSPHERE pass + curated MOOD presets = the big "good→great"
lever.** After the user judged randomizing material/mask knobs never produced a
standout ("30 randoms, nothing amazing"), deep research converged: greatness lives in
LIGHTING/ATMOSPHERE + a structured climate field + erosion masks + COMPOSITION — not
more material knobs; and curated rules beat raw randomization. Did the cheapest-highest-
impact piece first (scene-only, zero shader risk): soft sun shadows, SDFGI+SSIL GI,
large-radius SSAO, aerial-perspective + height fog, **AgX tonemap** (handles bright
outdoor highlights gracefully, fixed the "too bright/blown-out sun" — better than
Filmic/ACES), built-in color-grade adjustments. Then **6 curated lighting MOOD presets**
(`data/lighting_moods.json` → golden hour / overcast / midday / blue dawn / storm /
alpine), each a complete coordinated look (sun+sky+fog+exposure+grade) — user picks a
*vibe*, not numbers. **User verdict: "really good."** Key fixes along the way: tamed an
overbearing sun (shrink sky sun disc + decouple soft-shadow from disc size via pinned
`light_angular_distance`, raise glow HDR threshold so only the sun blooms not the whole
sky); over-applied atmosphere once ("can't see anything" — pulled volumetric fog OFF by
default, fog density ~4× down); fixed "spawn ≠ preset" (apply a default mood on startup;
startup was using raw .tscn env). New live **Light tab** + **Debug tab** (FLAT BASELINE
bisection) + **hero-shot** camera save/load (composition lever). The look lab is now an
art-direction tool, not a slider farm. NOT done yet (the remaining "great" levers):
climate/moisture field, erosion masks, real silhouette geometry, composition tooling.

**2026-06-16 — FUZZINESS ROOT-CAUSED & FIXED: material textures imported WITHOUT
mipmaps.** The long-running "fuzziness/speckle/grain" was NOT shader, lighting, splat,
hex-tiling, height-blend, or specular — it was all 738 material textures (2,930 files
incl. normal/rough/ao) imported with `mipmaps/generate=false`. No mips → minification
aliasing → high-freq texture detail CRAWLS in motion (invisible in a still frame).
This is why: it survived flat-baseline (raw texture read), survived splat on/off (both
sample the same textures), was immune to every shader fix, and never showed in
screenshots (it's a TEMPORAL artifact — needs motion). **Process lesson (again, the
hard way): do not debug a motion artifact from stills.** Several wrong fixes were tried
chasing a "splat off = smooth" conclusion drawn from stationary screenshots; the user
flying it ("still there at flat baseline") forced the diagnosis to texture import,
where `grep mipmaps/generate=false */*.import` found it instantly. Fix: flipped all
import files to `mipmaps/generate=true` + re-imported (44s); user confirmed gone in
motion. Made durable: `tools/copy_materials.py` now writes `.import` sidecars with
mipmaps on (the texture lib is gitignored, so the fix lives in re-derivable import
files — the tool is the source of truth). Also built a **Debug tab + FLAT BASELINE
button** in the look lab (every visual contributor as an independent toggle) — the
bisection harness that cracked this; keep it.

**2026-06-16 — Look lab: added hex-tiling anti-tiling + sharpness controls; FUZZINESS
not fully resolved (NOTED, deferred).** Root-caused the "fuzzy everywhere" as (a) the
old 50/50 `mix(base,big,0.5)` anti-tile HALVING texture contrast (variance loss — see
Heitz-Neyret) and (b) over-minification at `tex_scale_m=9`. Built fragment-shader-local
fixes behind live toggles: a `tile_mode` selector (none / IQ-2tap / hex-tiling, default
hex), and sliders for tex_scale (default now 28, was secretly 9), tri_sharpness (4→8),
hex_rot, hex_contrast. Also fixed a real UI bug: sliders set `.Value` in code but that
doesn't fire `ValueChanged`, so the shader silently kept its own defaults (incl. the
mush scale-9) while the panel DISPLAYED other values — `AddSlider` now pushes on init.
**Status: user flew it, fuzziness improved but not gone to his eye — DEFERRED, will
revisit.** Likely the same root issue as "make textures not look repeated/boring"
(active next). Headless A/B verified the techniques render correctly; the remaining
gap is a look-quality judgment, not a compile/wiring error. Surface-only, base field
untouched, git is the undo.

**2026-06-16 — Terrain LOOK LAB built (terrain_lab.tscn) — the texturing tool.**
After the splat/ground-texture attempts kept missing, switched to a tool-first
approach: a lab on the same base field with an on-screen panel to swap, live: 7
zone materials (valley→peak), 6 mask modes (height / height+slope / noise-broken /
curvature / steep-cliff / noise-biome), 5 blend modes (flat → full-stack PBR), and
preset save/load. Presets = the embryo of the biome system. Built but NOT yet
flown — user explores combos next. Compiles clean. Room to add more masks/shaders
(each is one case in the swapper).

**2026-06-16 — Material library judged down from 738 → 108 accepted, then deduped.**
Discovered the real texture library is huge (~1,015 PBR material folders system-wide
under D:\assets, 738 distinct after collapsing seed/variant copies — a biome-organized
`world 4/candidates` tree + `world3/textures/wgv3` + AmbientCG packs). Built a
**material judging loop** (board scene): one material at a time, full-stack PBR,
**1=pass / 3=fail**, verdicts persisted to data/material_verdicts.json (resumes
across sittings). User judged all 738 → **113 pass**. Conservative dedupe dropped 5
(1 pixel-dupe rock035=rock_dark + 4 non-materials) → **108 clean accepted**, heavily
alpine/volcanic/tundra/rock (fits the mountainous terrain). Verdicts + the derived
material_library.json are committed; the 2.5 GB of texture files are gitignored
(re-derivable from D:\assets). **Lesson:** most of the library is AI-generated with
stamped flora (leaves/ferns) that tiles badly — surface-only materials are the keepers;
flora is a future procedural-decoration pass, never baked into ground textures.

**2026-06-15 — Texturing arc: ported WG15 splat → many problems → reverted.**
The WG15 4-texture splat was tuned for altitude; at eye level it showed blocky
border-jitter (raw floor-hash) and screen-space grain. Tried smooth-noise jitter,
lower detail-bump, disabling FXAA/SSAO; user still rejected → reverted to the M1
ramp. Key process lesson that led to the lab + judging approach: don't port a
tuned-for-one-distance shader; build fresh, judge at eye level, give every layer a
live toggle, and make iteration cheap (git + sandbox scenes). Added MSAA (kept).

**2026-06-15 — M3 splat REVERTED; back to the M1 minimal height/slope ramp.**
The ported splat showed an artifact up close that the user judged bad: a
grain/pattern that "appears when you stop, fine when moving." We chased the likely
causes (blocky border-jitter from a raw floor()-hash → fixed with smooth value
noise; over-strong detail bump → reduced; FXAA, then SSAO → disabled as suspects)
but the user still saw it and called the revert. **Lesson:** the WG15 splat was
tuned for one viewing distance/altitude; at eye level it has a screen-space
artifact we didn't root-cause. Reverted the shader, removed texture wiring + splat
knobs from LabTerrain/PresentationParams. Kept MSAA (a pure win, splat-independent)
and the lighter fog default in JSON; restored SSAO + scene fog to M1 values. Build
clean, M1 confirmed clean at the close angle where the splat broke. Texture PNGs
left in `assets/` (unreferenced) so a future, properly-debugged splat is easy to
re-add — but next time we build the material FRESH and judge it at eye level
early, not port WG15's tuned-for-altitude one. The base field itself is unaffected
(presentation-only churn — the modular boundary held).

**2026-06-15 — M3 texture splat landed (presentation lane).** Ported WG15's
proven 4-texture height/slope splat (grass/rock/snow/gravel, border jitter,
biplanar rock, distance-faded detail bump) into `lab_terrain.gdshader`, trimmed of
the erosion delta map and water/flow mask. Wired the 4 textures + 13 splat knobs
through `PresentationParams` → `LabTerrain.ApplyPresentation`. Presenter-only, zero
field risk. Build clean, renders clean (no shader-compile errors). AWAITING the
user's fly-verdict on the textured look. Chose splat over lighting-polish/reseed
because it's what makes the terrain actually judgeable (was a flat color ramp).

**2026-06-15 — Make a small fixed set of orienting docs (README, TECH_STACK,
DECISIONS), then build out slowly.** "No plans doesn't work, lots of plans
doesn't work, maybe low plans will work." WG15 generated 19 specs + 20 plans in 6
days and the process outran the results. So: a thin durable doc set + one-line
decision entries, not a plan per lane.

**2026-06-15 — Tech-stack policy locked: C# default, GPU compute for parallel
per-cell math, Rust only for measured serial hot paths.** Right tool for
performance + quality, nothing speculative. Everything modular (swap a unit
without rewriting neighbors). See [TECH_STACK.md](TECH_STACK.md). WG15 lesson
baked in: profile the parallel fraction before promising a Rust speedup (its port
hoped 10×, got 2.6× — serial routing was the real ceiling).

**2026-06-15 — Render the base field with a minimal height/slope color ramp
first (M1); texture splat deferred to M3.** "Build piece by piece." See the raw
field read clean before layering presentation on top. **VERDICT: user flew it,
"looks good."** Base field confirmed in the no-bake project.

**2026-06-15 — No bake stage. Base field generated live on the GPU.** The bake
existed in WG15 only to hold erosion deltas; we dropped erosion, so the bake has
no reason to exist. The field was always a fast pure GPU function (~390 ms for
2048²). User: "try base field without bake, lets see what happens" → it works.

**2026-06-15 — Erosion content dropped; architecture kept.** WG15's base field
passed across 8 seeds; the erosion pipeline (valley_carve → stream_power →
hydraulic → thermal → alluvial) + 5 water systems churned across ~19 versions and
were judged bad. We keep the proven field + clean unit boundaries; if erosion
ever returns it's a fresh, judged-early single pass, NOT a port of the old
pipeline.

**2026-06-15 — Fresh project at `C:\Wg16\wg-16-project`, not an in-place strip of
WG15.** Cleanest separation; WG15 stays untouched as reference. Ported the Field
unit ~verbatim, trimmed the precision-ladder ABI (a gated no-op with no consumer;
params block 144B → 128B).
