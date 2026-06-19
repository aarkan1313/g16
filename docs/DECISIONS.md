# WG16 — Decisions Log

One short entry per decision, newest first. The point is to not re-litigate
settled choices and to give "low plans" a memory. Link a spec only when a feature
was big enough to warrant one. Date · what · why.

---

**2026-06-19 — Terrain LOD roadmap SPEC'd (CDLOD), built around the WG1-15 clipmap post-mortem.**
The terrain is one 2048² PlaneMesh (4M verts, no LOD) = the ~3.8 ms perf floor + ×4 shadow redraw, and
the keystone streaming/erosion-E4/world-editing/flora need. PARKED for a roadmap because **clipmap
killed WG1-15** — user post-mortem: the killer was **elevation + quality POPS** (vertices snapping to
new heights + detail quality jumping at LOD transitions), i.e. missing *continuous* LOD, NOT the
topology. So the arc is anchored on POP-FREE CONTINUOUS LOD (geomorph + detail cross-fade), chosen
approach **CDLOD (quadtree + per-vertex geomorph)** — the AAA heightfield standard whose purpose is
killing pops; stable world-XZ tiles fit WG16's per-region systems (clipmap's moving rings don't).
Staged T1 (prove pop-free on the fixed region — THE gate) → T2 (tiles) → T3 (streaming), each
eye-gated; T1 gates the rest (anti-WG1-15: no infra before the core technique is proven). NOT
scheduled — spec only. `specs/2026-06-18-terrain-lod-roadmap-design.md`; memory `terrain-clipmap-killed-wg1-15`.

**2026-06-19 — Performance pass: code-efficiency wins, not quality cuts (user push).** Profiled +
decomposed the frame (added `--sdfgi` probe). Found anti-repetition ~3.4 ms, the un-LOD'd mesh ~3.8 ms
floor, shadows ~2 ms; SDFGI/SSAO surprisingly ~free. Landed, all look-preserving (verified): **branched
triplanar** (skip <0.4% triplanar planes), **AR on albedo-only** (normal/rough → plain triplanar),
`light()` **`pow→`5-muls**, **cloud GC** (cache compute uniform sets + alloc-free `Std430Writer` → kill
~400 byte[]/frame + per-frame UniformSetCreate), **16-bit half noise/weather volumes** (cloudstats
byte-identical), **MSAA 4×→2×**. Net **baseline ~9.7→5.6 ms, clouds-on ~12.4→7 ms**. **TRIED & REVERTED**
the cheap sun light-march (detail volume is cache-resident → ~0 gain, darkened clouds 6%). The mesh floor
is OUT of this pass (→ terrain LOD roadmap). ⚠ AA (off/FXAA/TAA) + temporal-default 1→3 flagged for the
user's in-motion review. Earlier this session: split the 1229-line `TerrainLabUI.cs` into responsibility
partials (zero behavior change). See `docs/performance.md`.

**2026-06-18 — Cloud feature-by-feature review pass (live, user-driven).** Outcomes:
(1) **Dome res / texel crawl** — 512×128 was pixelly looking up (zenith) + crawling squares on
moving edges. Fixed CHEAPLY (no view-space rewrite): a 5-tap softened dome sample in
`cloud_sky.gdshader` + default res → 1024×256. **The view-space half-res march rewrite is NO
LONGER needed** unless a future case demands it. (2) **Sun disc** decoupled from the overcast
dimming (`sun_disc_energy`) — coverage was dimming the visible sun even in a clear gap. (3)
**Overcast** is now its own tunable knob (`overcast_strength` / `cloud_overcast`) on a smooth
`pow(coverage,3)` curve (was a hard `smoothstep(0.55,0.9)`), and it greys the SKY + cloud ambient
(was terrain/fog only → invisible looking up). (4) **Presets reviewed** — distributions good;
brightened the Clear-Alpine mood sky (deep navy → luminous blue) and darkened Stormy.
(5) **Lighting-model clarification (don't re-litigate):** clouds make the GROUND *darker* (overcast
dim + shadow map), and make the SKY *look* brighter only because white cloud out-luminates blue sky
— so "Clear sky darker than Scattered" is correct/physical, NOT a bug. See `docs/cloud-next-steps.md`,
`docs/cloud-system-overview.md`, memory `cloud-lighting-model`.

**2026-06-18 — Cloud polish: the audit's "already fixed" lighting was BUGGY; fixed the
real root causes, then finished the roadmap behind toggles.** The 2026-06-18 external
audit said lighting/macro/stepping were done. They weren't: (1) `cloud_sky.gdshader`
composited PREMULTIPLIED radiance with `mix(bg,rgb,a)` → ×alpha twice → ~2× too dim;
(2) the sun light-march extinction (0.02) drove optical depth to ~10–30 → `exp(-od)≈0`
→ clouds lit by ambient ONLY (dim grey mush); (3) `CloudWeather` had a dead macro octave
(`Fbm(baseFreq=1)` wraps to a constant) and an asymmetric remap that crushed the field mean
to 0.32 → sparse, non-intuitive coverage; (4) cloud scale (SHAPE_SCALE 1/9000, cellScale 0.35)
made a few giant blobs. Fixing those — plus gradient base noise, direct+fill multi-scatter,
2-octave erosion — is what made the clouds read good. The breakthrough was **measuring, not
eyeballing**: built `--cloudstats` (dome readback), `--lightcheck`, and used `--auto-shot` to
view frames directly. Then shipped the cloud-follow-up roadmap (per-deck lighting, presets→layer
stack, coherent randomize, temporal amortization, configurable dome res, in-march god rays),
**each behind a toggle defaulting to the validated look** so they can be reviewed feature by
feature. NOT done (eye-gated): the view-space half-res march rewrite (`--cloudtex` is the
stopgap). See `docs/cloud-system-overview.md` and memory `cloud-look-real-rootcauses`.

**2026-06-18 — CLOUD system refactored from scratch + multi-layer + audit-driven look fixes.**
The earlier volumetric clouds rendered but read "procedural/flat/uniform" through ~5 review
rounds. Path taken: (1) full from-scratch refactor to a **world-space camera-anchored
volumetric march** (spec `2026-06-17-cloud-atmosphere-refactor-design.md`) replacing the
origin-dome (which couldn't match shadows or stop jittering); (2) **multi-layer clouds** —
N data-driven decks (`CloudLayers` + `data/cloud_layers.json`, up to 8, layer 0 = legacy flat
knobs), summed in one full-span per-step march (spec `2026-06-18-multi-layer-clouds-design.md`);
(3) when the look still read procedural, got an **external shader audit** (`docs/cloud-look-
audit-prompt.md` + memory `cloud-look-audit-findings`) which correctly re-ranked the causes:
NOT texture res (a red herring I'd over-weighted) but **lighting** (added multiple-scattering
octaves + dual-lobe phase + base-occluded ambient — fog→form), **macro coverage variety**
(high-contrast multi-octave weather + cloud-system mask — kill the uniform "sampled noise"
look), and a **stepping bug** (uniform dt over the whole multi-deck span + 16-step floor →
~3 samples/deck = mush → fixed to empty-space-skip + ~64 fine samples/deck), plus secondary
fixes (stronger edge erosion, per-scale wind vs lockstep, high-coverage cellularity, 2-octave
Worley FBM noise). User verdict after the fixes: **"doesn't look bad overall"** — accepted;
moving on. **Two hard-won infra lessons (banked to memory):** hand-packing std430 param
buffers drifts on vec2/vec4 alignment (caused "no clouds" 3× → built `Std430Writer`); and
local-RD compute can't read back headless, so a windowed numeric self-check (`--shadowcheck`,
correlates shadow vs cloud-overhead density) is how shadow coupling is PROVEN (r≈0.79) rather
than eyeballed. Remaining cloud polish (per-deck phase/albedo, presets→layers, ranged presets,
god-ray rebuild as in-march in-scatter, temporal reconstruction, texture-res bump) is queued at
the front of ROADMAP "Cloud follow-ups". God-ray FogVolume was REMOVED (it collided with the
sun's volumetric shadows → black wedges).

**2026-06-17 — EROSION re-introduced as a fresh arc (WG15's graveyard, done differently).**
WG15 shipped ~19 erosion versions + 5 water systems, all judged bad; WG16 was *defined* by
dropping the whole bake/erosion/water stack. User asked to bring erosion back — "AAA + best
long-term; must work procedurally/infinitely whether that's pure perf or bake/presolve;
bakes can't take crazy room," and wants the "really cool" coupled erosion+water sim as a
reconciled-via-precompute toggle. **Diagnosed WG15 root cause** (from its post-mortem + the
user's live symptom — valleys that grew then shrank, elevation reversals, dips that didn't
drain): a STACK of independently-tuned solvers (valley_carve→stream_power→hydraulic→thermal→
alluvial) baked at an arbitrary iteration balance, with **no unifying drainage model** — the
solvers fought each other and froze incoherent. **Decided:** redo erosion as ONE coherent
coupled drainage-driven model (NOT a solver stack), as a transform DOWNSTREAM of the settled
base field (behind a pristine↔eroded toggle — base-field math untouched). Reconcile coupled
sim + infinite + small bakes by **baking the low-freq drainage SKELETON (MBs) and
synthesizing high-freq detail procedurally per-chunk**, mirroring the clouds DNA (heavy
compute → field → cheap consumers). **Build order (user's call): prove the sim GREAT on the
current single region FIRST**; bake/stream/chunk layers are explicitly LATER and depend on a
chunk system WG16 doesn't have. Arc = E1 sim core+live lab (NOW) → E2 skeleton bake → E3
semi-procedural detail → E4 coarse global + streaming. **Honest flag:** E2 is a scoped,
deliberate reversal of WG16's "no bake stage" stance, for erosion only, justified by the
infinite-world requirement. Spec: `docs/superpowers/specs/2026-06-17-erosion-arc-design.md`;
E1 plan: `docs/superpowers/plans/2026-06-17-erosion-unit1-sim-core.md`. **STOP clause:** if
E1 can't reach "great" after a fair effort, that's a real stop point — surface it, don't
grind 19 versions like WG15.

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
