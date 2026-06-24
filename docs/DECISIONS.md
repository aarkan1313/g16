# WG16 — Decisions Log

One short entry per decision, newest first. The point is to not re-litigate
settled choices and to give "low plans" a memory. Link a spec only when a feature
was big enough to warrant one. Date · what · why.

---

**2026-06-24 — CDLOD is now DEFAULT-ON (perf arc step 0; ARC B precondition).** Flipped `_cdlodCli` default
`-1 → 1` in `TerrainLabUI.Cli.cs` so a bare launch shows the **infinite quadtree**, not the finite single mesh.
Why: the shipping path is CDLOD (25 ms finite mesh → 6.5 ms avg streaming); the old off-default was why the user
found "it wasn't infinite." `--cdlod=0` still forces the single mesh for A/B. NOTE: `TerrainLabUI` backs BOTH
`terrain_lab.tscn` and `review.tscn`, so the review scene now streams too — more representative (CDLOD is
shipped) and it doesn't touch the look levers the 1-9 gates judge; re-verify the review presets in the sweep.
Confirmed at launch (log: "CDLOD ON (quadtree)", terrain renders, infinite horizon). This unblocks ARC B, whose
plan assumes "CDLOD is ON".

**2026-06-24 — Infinite-streaming pop-in fix: spec'd + planned (queued, not yet built).** The "regions visibly
load/pop in" = `SelectRoaming` loads a cell-aligned 3×3 block of 8192 m root cells, so crossing a cell boundary
(every 8192 m) shifts the block and pops an 8192 m strip. Four modular+tunable fixes: bigger load ring (R,
default 2), window hysteresis, **fog COUPLED to the load radius** (one view-distance drives ring + fog far-plane,
tunable scale on top — fogged-far = coarser = perf synergy), velocity-predictive lookahead. Decisions per user
"couple, modular, tunable, pillars". Spec `docs/superpowers/specs/2026-06-24-infinite-streaming-popfix-design.md`
+ plan. NOTE it grows the (shadow-dominated) perf cost → coordinate with the profile/optimize arc.

**2026-06-24 — Look-lab god-class decomposed (−40%) + audit hardening + perf reframe.** A big cleanup arc on
`experiment/presentation` (all pushed, regression-validated):
- **`TerrainLabUI` 3,214 → 1,943 LOC (−40%)** into **12 standalone classes** behind a narrow `ILabControls`
  façade (the linchpin), via the **migration-shim pattern** (logic → real class, thin partial keeps the old
  names so CLI/Registry/Review/`_Ready` compile unchanged — same pattern `LightingComposer` used). Why: it was
  the audit's "central maintainability risk." What stays (coordinator core, extracting moves coupling without
  reducing it): the panel-builder wiring hub, the `_Process` frame loop, the CLI appliers, the apply dispatch.
- **Audit hardening — validated each, fixed only the real ones.** #7 infinite-world chunk-key cap (28-bit pack
  COLLIDED past ±134,000 km → opaque 64-bit hash, no cap, ~1e-13 collision; key is never decoded so it's a
  drop-in). #11 `--atmoscheck`/`--aerialcheck` now exit-code (were print-only → unscriptable). #13 CLI
  parse-order hazard (`MatchFlag` order-independent matcher). #15 double-alloc. **#8 (unfenced AABB readback)
  was a FALSE POSITIVE** — it's fenced + render-thread + throttled, i.e. the async path the audit said to use;
  validating first saved wasted/risky work. One NRE regression (deferred-quit on an early-`_Ready` return)
  caught by independent code review + fixed with a `_cliSeq == null` guard.
- **Perf REFRAME (the big one).** The "~27.5 ms terrain-mesh floor" that drove the perf narrative was the
  **CDLOD-OFF single mesh** — not the shipping path. Coordinated whole-frame `--profmove` run: **CDLOD-on in
  motion = 6.5 ms avg / 20 ms worst**, and the worst-case spike is **shadow-map-dominated** (shadows-off drops
  worst 20→10 ms). avg is already under the 8 ms budget. **Next arc = profile+optimize: CDLOD default-on (it's
  off by default → bare launch is the slow finite mesh), then the shadow spike (8192 atlas dial-down).** Full
  data: `docs/performance.md` 2026-06-24; plan: `docs/handoffs/2026-06-24-profile-optimize-start-here.md`.

**2026-06-21 — #5 Shadow pass: findings + fixes (code-side; terrain-mesh part deferred to CDLOD).** Reviewed the
shadow system in motion. Two defects + a process bug:
- **Defect A — shadow quality jumps as the camera moves (CSM cascades).** Cause: default 4096 directional atlas
  + a split distribution that starved the far cascades (~0.6 m/texel near → ~7.8 m far over an 8 km range).
  **Fixed (code-side, no scene/project edits → no conflict with the terrain chat):** in `TerrainLabUI.Lighting.cs`
  `RenderingServer.DirectionalShadowAtlasSetSize(8192,true)` + `DirectionalSoftShadowFilterSetQuality(SoftHigh)`,
  and on the Sun `DirectionalShadowBlendSplits=true`, `MaxDistance=6000`, splits 0.10/0.28/0.60.
- **Defect B — the "blocky shadow blob with a jagged edge" was NOT a shadow at all — it was SSAO.** The active
  `ground.gdshader` (placeholder) doesn't even sample `cloud_shadow_tex` (only the god-ray pass does), and turning
  the directional shadow off didn't remove the blob. It was **SSAO at intensity 2.0** raking the faceted **4 m**
  mesh → jagged dark patches reading as shadows. **Fixed:** SSAO 2.0 → **0.6** (subtle valley AO) — default set in
  `data/lab_controls.json` `ssao_i` + a code guarantee in the shadow block. Revisit/raise once the higher-res CDLOD
  mesh lands (it'll be smooth then).
- **Process bug (cost us several blind iterations):** CLI flags need a **`--` separator** (`OS.GetCmdlineUserArgs()`
  is empty without it) — `--shadow=0/--ssao=0/--time` were silently dropping, so the isolation tests were no-ops
  until corrected. (Already in memory `wg16-launch-absolute-path` — follow it.)
- **Deferred (terrain-coupled):** the residual hard *diffuse terminator* is the 4 m mesh faceting itself → CDLOD's
  domain; the full-height single-mesh caster-AABB depth-precision amplifier (CDLOD already does per-chunk tight
  AABBs); re-adding cloud→terrain shadow receive (belongs in the new terrain/CDLOD shader, sampled `filter_linear`).
  Cloud shadow map kept at 512 (only feeds god rays today). 8192 atlas is a perf lever to dial down later.

**2026-06-21 — Night variation direction (planned) + night-cloud lighting (build now).** User: every night/weather
shouldn't look the same. Two layers. **(1) Enabling lighting — BUILD NOW as the lead of #5:** clouds currently
take only sun + sky-ambient (`cloud_raymarch.glsl` ~L384), so at dusk/night they get no fill and read as "big black
blobs"; add **moonlight into the cloud raymarch** (weak directional → silver edges/undersides) + a clean night
ambient/darkness floor. Without this, varying the moon does nothing visible. **(2) Night variation system — PLANNED
(not a new future lane):** a seeded "night composer" that draws each axis from ranges/pools so nights differ —
moon phase/brightness/**presence** (some nights no moon → very dark, stars dominate), star density/twinkle +
**different star/planet setups** (the curated planet + named-star tables become sampled/rotated pools, or a seeded
scatter), and mood. Builds on the decoupled `ComposeLighting` axes. **WEATHER-side night variation (overcast hides
stars / fog / clear) folds into the WEATHER roadmap lane — NOT a separate item.** Order: Layer 1 now → variation later.

**2026-06-21 — Galaxy / Nebula v2: KILLED (feature dropped; night sky = moon + stars + meteors).** After the
C1 procedural-noise rejection we rebuilt from scratch as **billboards**: a structured non-noise generator
(log-spiral arms + bulge + dust + HII knots), then a richer flocculent/domain-warped version, then — after
research showed *flat noise on a billboard is the documented wrong primitive* — a **volumetric raymarch**
(Beer-law extinction + domain-warped density + photo-derived color ramp), and finally a **lit, self-shadowed**
volumetric (Shot 2). User verdict at each: "not good / looks fake." User's call: **two volumetric shots within
a ~1 ms budget, else kill it.** Both shots failed the look bar, so the whole galaxy/nebula lane was **removed
completely** (commit 62a3835): all billboard shader code + `night_sky_brightness`, `CloudVolume.SetBillboards`,
`BuildBillboards`, `NightBillboards.cs`, the dead Night-tab galaxy/nebula controls + Apply cases + `StarsState`
fields, and the `NightSkyPresets` subsystem (`.cs` + `night_sky_presets.json` + `--nspreset` + picker).
**Lesson:** procedural galaxies/nebulae (flat OR cheap-volumetric) did not reach the quality bar; the research
path that might (authored/offline-generated **textures**, or expensive lit volumetrics > 1 ms) was out of scope
for a night-sky accent. **Do not re-attempt procedurally without an authored-asset plan.** Spec/plan/research
under `docs/superpowers/specs|plans` (2026-06-21 galaxy-nebula-billboards). Moon + stars + meteors stand.

**2026-06-21 — Galaxy / Nebula: START OVER (fresh concept). Meteors: PASS.** User after the meteor pass: "start
over on the nebulae and galaxy." The C1 procedural-noise galaxy/nebula is **rejected** — its root failure was using
**fbm/ridged 3D noise (the same noise the clouds use)**, so it always read as clouds, not space; localizing/
sharpening/ridging never escaped that. **Galaxy/Nebula v2 = a fresh spec from scratch, a genuinely different visual
direction** (candidates: a painted/authored galaxy-nebula texture sampled as a sky layer · a structured non-noise
generator [spiral arms / sharp emission + embedded stars] · scattered distant-galaxy billboards · or drop it) —
**agree a reference look first** so we don't iterate blind like C1. The C1 code is parked (off at brightness 0,
zero cost), deletable when v2 lands. Next-steps + failure analysis: `handoffs/2026-06-21-sky-lane-next-steps.md`.
**Meteors (C2 first piece): user "pretty good" → PASS, default-on.** (after the glowing-head + color-variety pass,
commit 371332d.) Optional follow-up: meteor presets.

**2026-06-21 — Celestial C2 first piece: meteors / shooting stars BUILT (look-gate owed → PASSED, see above).** After C1's galaxy/nebula
was rejected and the night sky settled to moon+stars, started C2 with **occasional, subtle shooting stars** (user feel:
"subtle & occasional", not a shower). Procedural **in-shader** (`cloud_sky.gdshader` `meteors()` — 2 TIME-hashed
channels, each cycles on a long period and only occasionally fires a ~1 s head-sweep drawing a thin head-bright/
tail-fading streak), composited into `stars_layer`, gated night×above-horizon. **Why in-shader not CPU:** stateless,
no per-frame CPU/uniform churn, ~free when idle (two early-outs bail before the streak math), fits the existing
procedural night sky. 5 Night-tab tunables + a `--meteordebug` uniform/CLI (forces a streak so headless capture lands
on one). Perf 5.4 ms night unchanged. Spec/plan `2026-06-21-celestial-c2-meteors*`; commits 29c52e4 + 14ec581. Owed:
the user's live eye-gate (review key 2). Out of scope (later C2): planets, meteor showers, persistent trails, fireballs.

**2026-06-21 — Celestial C1 (procedural fantasy night-sky) BUILT T1-T5, look-gate owed.** Replaced the uniform Milky
Way fog band (NEEDS_REVIEW 10, user: "just a fog band, a half-circle all the way across") with a procedural fantasy
**galaxy** (core bulge that kills the uniform arc + dust lanes + star-cloud knots + 2-color gradient) + up to **4
nebulae** + a reworked **magnitude/size/color-temp starfield**. Spec/plan `2026-06-21-celestial-c1-*`. **Key decisions:**
(1) **Baked, evolving the MW-bake seam** — `milkyway_bake.glsl`→`night_sky_bake.glsl` bakes galaxy+nebula COLOR to an
rgba16f lat-long texture (1 runtime tap, cheaper than the old 3 fbm3); the procedural path stays as the `night_sky_baked`
toggle-off reference, pixel-diff-verified == baked (≤0.0005, the star-twinkle floor). (2) **Why baked, not cross-node
sampling:** the AT-3 lesson — cross-node GPU texture sampling HARD-CRASHES the render device — so C1 reuses the
render-thread-RD bake + `Texture2Drd`/param seam, copying the noise block verbatim so baked==proc. (3) **Brightness stays
a LIVE shader multiplier** (not baked) so dragging it never re-bakes; structure/color re-bake on change (coalesced via
`_mwDirty` → one bake/frame). (4) **Tunable + 4 presets** (Subtle / Crimson Rift / Aurora Veil / Deep Field), `--nspreset=N`;
presets bake distinct skies (bright-region max ~1.0 → proves the knob→re-bake path). Commits a43515a..a09672a. **Owed:**
the user's live look-gate (T6) — perf/correctness are mechanically verified; the fantasy "is it cool" + the dim default
brightness are the user's call. On PASS: default-on (already wired) + record.

**2026-06-21 — Sky perf pass (no-visuals window): profiled + shipped the verifiable on-par wins; 2-3× needs a motion gate.**
Goal was 2-3× sky perf without quality loss. **Profiled** (`--profmove --profile=3`, subsystem toggles): the sky =
**clouds ~1.8 ms** (day) + **night stars/galaxy/moon ~1.3 ms**; **the whole atmosphere stack (AT-1/2/3) is ~free**
(LUTs recompute on sun-change only; aerial/cloud-light negligible — the earlier "+0.7 ms" was measurement noise).
**Shipped (each mechanically verified look-neutral — pixel-diff vs the original, since the user can't eye-gate now):**
(1) **Milky Way bake** — 3 per-pixel 5-octave fbm3 → one prebaked texture tap (`milkyway_bake.glsl` on AtmosphereCompute's
seam, exact-copied noise); pixel-diff bake==procedural (the diff WAS the animated stars); **default-on**; ~0.3 ms +
worst-case 14.8→8.5 ms. (2) **Cloud temporal stride 1→2** — converged image pixel-identical (static diff 0.030);
**default-on**; ~0.5 ms + stabler frames; the residual is a 2-frame dome staleness in fast motion (imperceptible on
slow far clouds). (3) earlier: AT-3 readback throttle + static-camera aerial skip. **Honest outcome:** the
on-par-*verifiable* wins total ~0.5-0.8 ms (~10-12 %) + much steadier frame times — NOT 2-3×. **A true 2-3× needs the
clouds**, and the bulk lever is aggressive temporal amortization (stride 4-8 → another ~0.8 ms) which trades *motion*
smear a static pixel-diff can't certify → **staged for the user's motion gate** (Clouds-tab `temporal frames` /
`--temporal=N`, already wired). Commits 82d5900/6019c9f/918e8ab/193a4eb (+ the earlier efficiency commit). Banked for
the formal end-of-arc perf pass (ROADMAP #7): the deeper texel-only LUT readback + a cloud-amortization motion gate.

---

**2026-06-21 — GROUND lane: verdict flipped ITERATE → RESET after the live G-0 gate; reset spec + plan + handoff written.**
The G-0 "wrong-defaults" probe (review key 3, stepped lever isolation) put the material defaults in front of the
user's live eye. At the **real-height** step the blend broke into hard **4 m-grid rectangular facets** — confirming
the base problem is STRUCTURAL (the audit-flagged 4 m weight field × the interlock hard-threshold), not surface
tuning. User's call: **start over on the ground, follow pillars, get it to parity with the sky.** New architecture:
reset the material RENDERING (skin, not bones — base-field geometry untouched) to **per-pixel procedural placement +
texture arrays** (no baked splat → no blockiness by construction; scales to many materials/biomes). Keepers
re-hosted: histogram anti-tiling + the BRDF. Built ALONGSIDE the old path behind a toggle, default-old, retire only
on a passed parity eye-gate (no big-bang teardown — the WG15 graveyard). Spec
`specs/2026-06-21-ground-material-system-reset-design.md`, plan `plans/2026-06-21-ground-material-system-core.md`,
handoff `handoffs/2026-06-21-ground-v2-core-start-here.md`. Supersedes the 06-20 master design's iterate stance
(keeps its north star + keepers). Brainstorm→spec→plan done via superpowers; NEXT: a fresh chat executes the plan
from the handoff. (Banked: the fwidth AA on the old interlock_blend + the rough_floor slider + the G-0 review-key-3
harness stay — they were the diagnostic that proved the reset.)

**2026-06-21 — AT-3 physical cloud lighting BUILT + live eye-gated → PASS, default-ON. Pivoted A→param-handoff (cross-node GPU sampling crashed).**
Light the volumetric clouds with the physical atmosphere (spec `…at3-cloud-lighting-design.md`, plan `…at3-cloud-lighting.md`):
cloud ambient endpoints = sky-view radiance at the **zenith** (cloud tops) + the **horizon toward the sun** (cloud
undersides → warm at sunset); direct sun = **sun-transmittance reddened**. Behind `physical cloud light (AT-3)` /
`--cloudlight`, carried in the unused `sun_color.a` param slot (0 = off → mood path, no shared-block change). User
eye-gate (review key 8 A/B, clouds tilted up at the deck): **"yep it works"** → PASS, **default-ON** (completes the
AT-1/AT-2/AT-3 stack consistently). **PIVOT from the approved approach A** (sample the LUTs inside the cloud raymarch):
CloudVolume's compute **HARD-CRASHES the render device** sampling AtmosphereCompute's textures cross-node (isolated:
sampling CloudVolume's OWN texture is stable; the atmosphere's faults — a render-device sync/ownership hazard, exactly
what the `compute-to-material-callonrenderthread` seam-law warns against). The three values are sun-dependent **per-frame
constants**, not per-pixel — so AtmosphereCompute **reads them back on the CPU** (sun-change cadence, in `RecomputeAll`)
and they're pushed as params. **Visually identical** to approach A (which only sampled 2 fixed sky dirs + 1 transmittance
per ray) and stable. Verified: 0 errors, 3/3 stable, ON-vs-OFF cloud-band diff 1.66 vs 0.07 drift control, clouds warm at
golden (R−B −10.1→−8.9). Commits abdaa0e (T1) + 6d0d590 (pivot+wiring). Strength default 10 (gate-tunable, subtle at
golden, strongest at dusk). **Atmosphere arc (#3) COMPLETE.** Next per the user: a CPU readback throttle is owed for the
running day/night cycle (per-frame readback) — folded into the end-of-arc code-efficiency pass (ROADMAP #7).

**2026-06-21 — AT-2 aerial perspective BUILT + live eye-gated → soft-PASS, default-ON at subtle strength 0.4.**
Built the full AT-2 stack from the approved plan (`plans/2026-06-20-gpu-atmosphere-at2-aerial-perspective.md`): a 4th
Hillaire LUT in `AtmosphereCompute` — a 32³ camera-frustum aerial froxel (rgb in-scatter, a transmittance, `Texture3Drd`
seam, cheap camera-only recompute path) + a screen-space composite (`AerialPerspective` + `aerial_screen.gdshader`,
mirrors GodRaysScreen, RenderPriority 120 < godray 127) that reads depth, reconstructs distance, samples the froxel, and
composites `color·T + inscatter` on geometry only (sky depth-gated out — proven no double-count). Built-in `FogAerial`
handed to 0 when on (no double-fog; off restores it exactly). Commits af07b5d (T1), 505ba07 (T2), d7474e6 (recal).
**Two plan-snippet fixes forced by the live engine:** spatial shader writes `ALBEDO/ALPHA` not `COLOR`; froxel altitude
clamped to ≥ sea level (terrain rays dive below the ground radius at large t → `exp()` density explodes → whole-frame
wash; sky LUTs never hit this). **The gate (user, review key 8 A/B):** the audit's predicted gain mismatch was real —
in-scatter is additive and at the planned strength (~2, let alone the sky-matched 10) it WASHED the frame. Swept it:
**~0.4** reads as gentle physical distance haze (warm sunset / blue noon), near terrain stays crisp; >1 washes. User
verdict at 0.4: "a little better than off" → **soft-pass, keep default-ON subtle.** Cost (`--profmove`): **+0.7 ms**
(114 vs 124 fps), within budget. Self-checks PASS (`--aerialcheck` inscatter[0..0.13] T[0.466..0.999], `--atmoscheck`,
`--shadowcheck`). **AT-1 horizon-flash re-confirmed CLEAN in motion 2026-06-21** (user flew key 8's horizon framing —
"didnt see it, looked good"; the fog/seam fix holds in motion, closing the last owed AT-1 item). AT-3 (cloud-lighting)
NOT started (disciplined — one sub-phase). Lane stays paused for the re-roadmap.

**2026-06-21 — Full project audit + ground/texture iterate-vs-rebuild review; docs reconciled; sky lane pushed to origin.**
Ran a 7-subsystem project audit (`AUDIT-2026-06-21.md`, committed) + a focused ground review (both multi-agent,
adversarially verified). **(1)** Pushed `experiment/presentation` (was 179 commits ahead) + 6 backup tags to origin —
the post-reset sky lane finally has an off-machine copy. **(2) Ground verdict: ITERATE, do NOT rebuild.** The
"drab/flat" look is a cluster of suppressed-good-tech defaults (roughness force-clamped ≥0.5 with no UI control →
dead-matte; real height + macro + within-area variation all default-off; palette is the drab `alpine_green`;
`tex_scale_m` 28 vs 9; + the default-on atmosphere warm-washing the surface), NOT a rotten foundation — the two hardest
pieces (half-float weights + histogram anti-tiling) are already built/PASSED, so a teardown re-risks solved work and
re-enters the WG15 graveyard. The from-scratch redesign already exists (the ground master design); execute it surgically.
Next ground step = a **G-0 "wrong-defaults" eye-gate** (flip the cluster behind toggles, A/B live) before any building.
**(3)** Honest correction: the sky lane was NOT paused after 06-20 — it built a burst to AT-2 at user direction, and
**AT-1 + AT-2 are default-on but never live-eye-gated** (AT-2's cost never measured) → added to NEEDS_REVIEW 11; also a
real sky-color-ownership bug (the keyframed `day_script` is a no-op when atmosphere is on, the default). **(4)** Reconciled
stale docs: ROADMAP gained a Debt & Remediation backlog + a rewritten Ground lane; HANDOFF §6, NEEDS_REVIEW (1e + 11),
performance.md (proxy/SDFGI default + AT-2-unmeasured correction), and the ground master design (currency note) all
updated to match the code. Why: the audit found the narrative was ahead of reality in specific ways and the fixes
weren't tracked anywhere — now they are. **No code changed — docs + planning only.**

**2026-06-20 — Sun/Light #3 AT-2 (aerial perspective) DESIGNED + PLANNED + handoff written; build deferred to a NEW session (user's call). Fantasy sky-tint resolved.**
Two things. **(1) Fantasy/mood sky tint now recolors the PHYSICAL sky** (closes the AT-1 default-on caveat): `sky_tint`
is applied to the atmosphere as a luminance-preserving recolor (white = no-op; saturated = recolor to that hue,
keeping the physical gradient/halo). alien_green → green sky, normal day unchanged. Commit `98b6da4`. (Deeper
"alien-air" via per-channel Rayleigh params = a future option.) **(2) AT-2 approach = B (froxel aerial LUT +
screen-space composite)** — user chose the AAA route ("pillars") over the cheaper "drive the built-in fog." A 3D
camera-aligned froxel LUT in AtmosphereCompute (rgb in-scatter + a transmittance, recomputed per-frame) + a NEW
clip-space fullscreen-quad pass (`AerialPerspective.cs` + `aerial_screen.gdshader`, mirrors GodRaysScreen) that
reads frame+depth and composites `color·T + inscatter` on geometry (sky skipped, no double-count); built-in aerial
fog drops when AT-2 owns it. **No terrain/godray shader edits** (coordination). Spec
`specs/2026-06-20-gpu-atmosphere-at2-aerial-perspective-design.md`, plan
`plans/2026-06-20-gpu-atmosphere-at2-aerial-perspective.md`, build handoff
`handoffs/2026-06-20-at2-aerial-build-start-here.md`. **User: build in a new session** → spec+plan+handoff written,
NOT built this session. NEXT (new session): execute the plan (T1 LUT → T2 composite → live gate). Owed: AT-1
horizon-flash live re-confirm (fold into the AT-2 gate).

**2026-06-20 — Sun/Light #3 AT-1: flipped to DEFAULT-ON (perf verified).**
User: "if perf is good leave the thing on." Perf measured (sun moving every frame = worst-case LUT recompute):
atmosphere ON **5.6 ms** vs OFF **5.4 ms** = **+0.2 ms** (180 vs 184 fps) — negligible, well under the 8 ms target.
So `atmosphere_on` default → **TRUE**: the physical Hillaire sky is the standard daytime look; the keyframed sky is
kept as the toggle-off fallback (Light tab / `--atmosphere=0`). Robust startup: the material flips to the LUT only
once it's computed (one-time `_Process` gate on `_atmosphere.Ready`) so frame-1 shows the keyframed sky, never an
unbound Texture2Drd (no black flash; the cloud "binding 1/34" spam is the pre-existing benign one). **CAVEAT
(banked follow-up):** with atmosphere on the daytime sky is physical everywhere → the 6 moods' + ST4-2 fantasy
presets' keyframed SKY TINTS are bypassed by day (night + fantasy moon colors still apply). If the colored fantasy
skies (review key 7) must persist, fantasy presets should flip atmosphere off OR tint the LUT — not yet handled.
**Still owed:** live confirm the horizon flash is gone in motion (user hasn't re-checked). NEXT: AT-2 aerial OR the
Celestial expansion (galaxy→bodies→N suns/moons).

**2026-06-20 — Sun/Light #3 GPU atmosphere AT-1 (core sky color) BUILT + day look soft-PASSED (live); fog-wash + horizon-seam fixed.**
AT-1 Hillaire LUTs (transmittance→multi-scatter→sky-view) on the CloudVolume `Texture2Drd`/`CallOnRenderThread`
seam (new `AtmosphereCompute` node, mirrors CloudVolume; RIDs assigned once; LUTs recompute on sun-change, not
per-frame). `cloud_sky.gdshader` `background()` samples the sky-view LUT when `atmosphere_on`, **default OFF** =
the approved keyframed look (A/B). **User verdict (live, review key 8 cycling dawn→noon→golden→dusk): "just the
sky? yeah i see it, looks good"** — physical DAYTIME sky soft-PASSED. Two gate fixes found live: **(1)** daytime
depth fog (`FogSkyAffect=1.0`) washed the whole sky dome → masked the atmosphere ("not much going on"); fixed by
dropping `FogSkyAffect` when `atmosphere_on` (atmosphere owns the sky haze, per the existing design note; terrain
aerial fog unchanged). **(2)** a hard horizon line the finite world revealed = a shading seam at `dir.y=0`
(physical above, keyframed below); fixed by making `background()` continuous across the horizon (sample the horizon
row below, fade to ground) — **world-size-independent** (it was never a finite-sky edge, so it needn't "adapt to
world size"). **NIGHT:** atmosphere correctly bows out to the keyframed night (night A/B identical, clean handoff)
— accepted (user: "fine if night just doesnt use it"). Review **key 8** (repurposed from the resolved GI gate; SDFGI
A/B still on the Light tab) cycles the 4 daytime presets; **camera reframes only on the first press** (no teleport
while cycling, per user); `--review=N` drives a preset headlessly; `--atmosphere/--atmoscheck/--atmoexp` CLIs.
Mechanically: `--atmoscheck` PASS (transmittance∈[0,1], skyview horizon>zenith, finite), `--shadowcheck` PASS
(no cloud regression), perf negligible (tiny LUTs). **Default stays OFF** (no flip called yet). Open: confirm the
horizon flash is gone in motion; exposure default. Commits 0fdcfc5/367388d/5bb24ab + this. NEXT (user's call):
flip-to-default · AT-2 aerial · or the Celestial expansion (galaxy→bodies→N suns/moons).

**2026-06-20 — Galaxy/Milky Way review: NEEDS WORK → scope expanded into a Celestial sub-lane; atmosphere (AT-1) sequenced FIRST.**
Drove the live galaxy gate (`review.tscn`, `--time=23 --celestial=1` new_moon_dark, MW boosted). **User verdict (live):
needs work** — "not terrible but just a fog band, and a band that goes all the way across in a half-circle." Root cause
(code, `stars_layer()` in `cloud_sky.gdshader`): the MW is a **uniform great-circle band** (`abs(dot(dir,planeN))<width`,
no along-band brightness variation → the uniform arc) textured by **smooth low-freq fbm with a flat blue-white color and
no embedded stars** (→ reads as fog). Missing: bright galactic **core/bulge**, sharp dark **dust lanes** (Great Rift),
**resolved star clouds** in the band, subtle color. **User expanded scope** (brainstorm) into a **Celestial / Night-Sky
sub-lane:** **C1** galaxy redesign (better + **pulled back** to a smaller apparent scale, realistic-cinematic, tunable/
modular + presets) · **C2** celestial bodies (planets/meteors/named-star realism/optional nebulae) · **C3** **N suns +
N moons** (un-deferred — generalize the single-luminary architecture). **Sequencing decision (user): GPU atmosphere
(AT-1) FIRST, then the Celestial expansion (C1→C2→C3).** Dependency banked: **C3 N-suns feeds the atmosphere scattering**
(sky color is computed from the sun direction[s]) → atmosphere foundation first, extend its LUTs for N-suns later.
Roadmapped thin (no full C1/C2/C3 specs yet — spec each at its turn, per the doc-sprawl discipline). NEEDS_REVIEW 10
updated (reviewed → needs work → C1). NEXT: **AT-1** (spec `specs/2026-06-20-gpu-atmosphere-design.md`, already approved) → writing-plans.

**2026-06-20 — Sun/Light #4 ST4-2 (fantasy presets) GATED (soft) + review key 7; #4 Stage 4 COMPLETE.**
ST4-2 soft-passed live (user: "pretty good, good enough for now") + wired to review key 7 (cycles the 5 fantasy
presets). **Stage 4 done; the core sky system is now built end-to-end: Stage 3 Night/Celestial ✅ · #2 Clouds
overhaul CO-1..CO-4 ✅ · #4 auto cycle + fantasy ✅ · moon decoupled to its own arc.** REMAINING sky work:
**#3 GPU atmosphere (AT-1)** (the last big item — physical Hillaire sky color on the Texture2Drd seam,
default-off A/B; now unblocked since #2 landed) and the **galaxy/Milky Way review** (NEEDS_REVIEW 10). Several
soft-accepts owe fuller judgment once weather + surrounding systems exist (CO-3 macro variety default-off,
CO-4 cloud presets, ST4-2). Detail-erosion raymarch↔shadow divergence still deferred (own gate).

**2026-06-20 — Sun/Light #4 ST4-2 (fantasy/exotic presets) BUILT, awaiting the picker gate → #4 ~COMPLETE.**
Cross-system preset layer (`FantasyPresets.cs` + `data/fantasy_presets.json`, mirrors sun/celestial): each
preset names a sun preset + celestial preset and layers a sky tint + moon/moonlight colors + cloud/exposure —
fanning out to ApplySunPreset/ApplyCelestialPreset + scene appliers (data only). 5 presets: blood_moon,
alien_green, violet_night, harvest, ember_dusk. Night-tab picker + `--fantasy=N`. Deliberately set NO
time_of_day so the LOOK holds as the ST4-1 cycle runs. **New lever:** a persistent `_skyTint` (Color, default
white) multiplied into the sky gradient in ComposeLighting (so it survives DriveTime re-running each frame) +
a Light-tab `sky tint` scenecolor — needed because sky color is time-driven with no direct override; this is
the fantasy looks' main sky-recolor. Verified: alien_green (green sky/cast) + blood_moon (red night) compose +
render distinct. Commit `5ea58d4`. **#4 Stage 4 ~complete** (ST4-1 gated, ST4-2 awaiting picker gate; multiple
suns/moons deferred). Sky lane #2 + #4 now done; **#3 GPU atmosphere (AT-1)** is the remaining big item.

**2026-06-20 — Sun/Light #4 ST4-1 (auto day/night cycle) PASS (live) + moon arc decoupled from the sun.**
ST4-1 auto cycle: a clock in `_Process` advances `_time.TimeOfDay` by `_timeSpeed` h/s and re-`DriveTime()`s
(wrap 24→0); time-of-day slider synced so manual scrub still works. Light-tab `play day/night` + `cycle speed`,
`--autotime=<speed>`. **User verdict (live): "looks pretty good"** — cycle smooth, no wrap/seam pops. Two
follow-ups flagged: (1) **moon shared the sun's arc** (it was placed exactly anti-solar) — FIXED: the moon now
follows its OWN arc, lagging the sun by `phase*12` hours (full = anti-solar/rises-at-dusk as before so the gated
Stage-3 full-moon look is preserved; new = rides with the sun; quarters 90° apart), evaluated with the same arc
function at the lagged hour. Also makes night visibility phase-correct (new moon up by day, not night).
Elev/Az offsets still tune. (2) **galaxy/Milky Way needs review** — banked (NEEDS_REVIEW), not changed.
CO-4 cloud presets soft-accepted ("not crazy good but hard to judge without weather/other systems") → #2 clouds
overhaul DONE (CO-1..CO-4). Commits ST4-1 + moon-arc. NEXT: ST4-2 fantasy presets (finish #4), or #3 atmosphere.

**2026-06-20 — Sun/Light #2 Clouds · CO-4 (preset library) BUILT, awaiting the picker flip-through gate.**
6 new Clouds-tab presets composing the CO-1/2/3 levers into distinct believable skies: Fair Weather Cumulus
(3D profile + macro variety), Cirrus Veil, Cumulus & Cirrus (layered), Overcast Stratus (shape-mode sheet),
Stormy Anvil (anvil profile), Mackerel Sky (broken cumulus + cirrus). Pure DATA (`cloud_presets.json`) — the
preset infra already existed; the new control ids route via the registry, bool toggles via numeric 1/0 (like
`cloud_godrays`). **One code change:** `ApplyCloudPreset` now RESETS the cloud-type levers (profile_on/shape_mode/
cirrus_on/anti_repeat) before applying, so every preset is **self-contained** (selecting a preset that omits
cirrus no longer inherits a leftover cirrus from the prior pick) — the right-depth fix vs adding off-keys to every
preset. Verified: presets apply (`--preset=N`) + render distinct (Cirrus Veil/Overcast Stratus/Stormy Anvil
auto-shots). Presets are opt-in selections → startup default look unchanged. Commit `acd90d8`. **This ~completes
#2 Clouds overhaul** (CO-1..CO-4); weather-axis tie-in stays DEFERRED to its own stage. NEXT (user's pick):
#3 GPU atmosphere (AT-1, now unblocked since #2 landed) · #4 Stage-4 auto-cycle · or revisit CO-3 default-on.

**2026-06-20 — Sun/Light #2 Clouds · CO-3 (macro variety) built default-off + high-effort code review of CO-1/2/3.**
CO-3 anti-repetition: per-layer field 23 (`AntiRepeat`, reserved in CO-1 → no stride growth) — a mid-scale
weather tap clusters cumulus into varying-size groups with gaps; byte-identical ×3 shaders; default 0 = cumulus
unchanged; Clouds-tab `macro variety` knob + `--antirepeat`. **Kept default-off/banked:** user flew it and the
on-chunk benefit is marginal — the repetition they actually see is the **far/down "huge dome" regime**, which is
a **Phase-B (infinite world) concern, not a region-look bug** (banked, not tuned). **Code review (8 finder angles
+ verify) findings, ranked + acted on:** (1) FIXED — `CloudShadowCheck` was rebuilding layer-0 with the new
fields neutral (+ hardcoded CellScale=1.6), so `--shadowcheck` silently validated cumulus even under `--stratus`;
now fed production's `PackedLayers` (single source of truth) — verified stratus now reads SATURATED (overcast
sheet), cumulus/antirepeat PASS. (2) FIXED — cirrus `t<=0` guard (camera at/above the cirrus altitude rendered
the sheet on the wrong side at low-altitude settings). (3) FIXED — CLI `--stratus/--cirrus/--antirepeat`
parse-then-assign (bad value no longer overwrites the -1 sentinel → silent enable-at-0). (4) FIXED — stale
`layers[40]/[24]` (5 vec4) comments → `[48]` (6 vec4). **DEFERRED (reported, not changed):** pre-existing
**detail-erosion divergence** between the raymarch (2-octave, erodeAmt 0.45-0.95, edgeBoost 2.3-0.5) and the
shadow/check shaders (1-octave, 0.35-0.85, 1.6-0.7) — shadows cast from a slightly fuller cloud EDGE than
rendered; long-standing, within accepted tolerance (shadow gates passed), and fixing it changes the approved
shadow look + adds shadow-pass cost → needs its own eye-gate. Cirrus envelope/streak two-rate drift shear
(approved look). Commit `93a0dfa`. NEXT: CO-4 presets (or revisit CO-3 default-on if wanted).

**2026-06-20 — Sun/Light #2 Clouds · CO-1 + CO-2 PASSED the live eye-gate. CO-3 next.**
Drove the gate via `review.tscn` (key 6 cycles cumulus→stratus→cirrus). User verdicts (live): CO-1 cumulus
vertical profile + CO-2 both **"fine / good"**. Iterations done at the gate: **stratus** now leaves occasional
**gaps** (broken stratus; coverage drives solidity) — user "there are now gaps"; **cirrus** was a view-locked
dome (didn't parallax when moving) → **world-anchored** to a finite altitude (real parallax + drift), user
"looks good"; then a **realism pass** (per-region density/orientation/size variety via cheap macro fields +
early-out + 3-octave noise) — user "good". **Perf caught + fixed at the gate:** the naive variety pass (2 bands
× 5-octave fbm3) tanked to **10 fps**; reworked to ~free (**133 vs 136 fps** cirrus on/off). **Defaults:** CO-1
profile stays **opt-in (toggle default-off)** — approved cloud look unchanged; not flipped on without an explicit
call (user: "if not we can come back later"). Stratus = a selectable type (knob/`--stratus`), cirrus = opt-in
layer (default off); tuned param defaults baked (cirrus variety 0.7 / speed 0.04 / alt 8000 / cov 0.6 / den 0.7).
Commits `aba7c25`,`a50ea79`,`17be53a`,`0011615`. NEXT: **CO-3 anti-repetition + horizon** (one phase past the pass).

**2026-06-20 — Sun/Light #2 Clouds overhaul · CO-2 (types: stratus + cirrus) BUILT + banked, AWAITING the live eye-gate.**
Built CO-2 per `plans/2026-06-20-clouds-co2-types.md`, **ahead of CO-1's gate at the user's explicit direction**
("keep going… if modular and tunable it'll be fine") — so everything ships default-off/neutral, cumulus
byte-unchanged. Two new cloud TYPES: **(1) Stratus shape-mode** — `ShapeMode` per-layer field **22** (the slot
reserved in CO-1, so NO stride growth); `layer_density` blends `cellGate→1` + cuts detail erosion for a
connected overcast sheet, **byte-identical across raymarch+shadow+check**; default 0 = cumulus (verified
terrain pixel-identical). Clouds-tab `type: cumulus↔stratus` knob + `--stratus`. **(2) Cirrus** — a cheap 2D
`cirrus_layer(rd)` in `cloud_sky.gdshader`: anisotropic wind-stretched `fbm3` filaments, elevation-gated,
sun-tinted, night-faded; composited OVER sun/moon and UNDER the cumulus dome (cumulus correctly occludes it).
`CloudVolume.SetCirrusOn/SetCirrus` push uniforms; Clouds-tab toggle+5 knobs + `--cirrus`; default off →
sky byte-unchanged. **Review key 6** now cycles cumulus(profile off→on)→stratus→cirrus. **Mechanically
verified:** cumulus no-op; `--shadowcheck` PASS r=0.811 (stratus render, correct field-22 decode); stratus
reads as an overcast sheet; cirrus reads as wind-streaked filaments (sky diff 2.0 vs 0.08 drift) and fades at
night. **NOT eye-gated.** Owed: CO-1 + CO-2 live eye-checks (NEEDS_REVIEW 9). NEXT after they pass: CO-3
anti-repetition/horizon. CO-2 OUT-of-scope kept deferred: weather-axis tie-in, unified volumetric cirrus.

**2026-06-20 — Sun/Light #2 Clouds overhaul · CO-1 (vertical realism) BUILT + banked, AWAITING the live eye-gate (user can't view rn).**
Built CO-1 per `plans/2026-06-20-clouds-co1-vertical-realism.md`: a per-deck vertical density profile so decks
read as 3D volumes (flat-ish base → faded/anvil top) instead of flat slabs. **New per-layer fields**
`ProfileBottom/ProfileTop/Anvil` (indices 19-21) on the packed cloud-layer buffer — `CloudLayers.Stride`
20→24, `layers[40]→[48]`, `LF` stride `*5→*6` **in lockstep across all THREE shaders** (raymarch + shadow +
the `cloud_shadow_check.glsl` diagnostic the plan initially missed). `height_profile(h, bottom, top, anvil)`
multiplies `layer_density`'s shape byte-identically in raymarch + shadow (so ground shadows track the reshaped
clouds). **Tunable + modular:** Clouds-tab `vertical profile (CO-1)` toggle (**default OFF = approved slab
look, opt-in**) + `base round`/`top fade`/`anvil (Cb)` knobs driving layer 0; `--cloudprofile=b,t,a` CLI;
per-deck JSON fields (`profile_bottom/top`, `anvil`) for future preset authoring (CO-4). **Mechanically
verified (not eye-gated):** neutral (0,1,0) is a true no-op (terrain pixel-identical to baseline, sky differs
only by cloud drift); `--shadowcheck` PASS r=0.835 with correct layout decode (alt=1800, count=2); profile-on
visibly reshapes clouds (sky diff ~0.9 vs ~0.08 drift) AND shifts ground cloud-shadow (lockstep holds in the
real render); **no perf cost** (3 smoothsteps/tap; profile-on fps ≥ off). **Known tunable behavior to judge:**
height_profile ≤ 1 so enabling it REDUCES integrated density (thinner clouds) — raise the density knob to
compensate; an optional density-preserving normalize can be added if the user wants shape-without-thinning.
**DISCIPLINE: stopped at the CO-1 gate** — last PASSED gate is still Stage 3, so CO-2 (cirrus/stratus types)
is NOT started (building it would be 2 sub-phases past the last pass = the WG1-15-reset trap). Owed: the live
CO-1 eye-check (NEEDS_REVIEW). NEXT after PASS: bake approved profile values as defaults → CO-2.

**2026-06-20 — GROUND lane reset to a from-scratch, game-agnostic rendering SYSTEM redesign (master spec).**
After the anti-tiling win, the GM batch re-judge surfaced more close-up artifacting; user stepped back: *"are
we building more and more on a bad foundation?"* Honest read: base-field geometry is solid, but the ground
material/**rendering** stack was built one bolted-on Unit at a time, never designed as a whole → we keep
discovering its quality debt one eye-gate at a time (IQ tiling, 8-bit weights). **Decision: give ground the
zero-to-100 treatment.** North star = a **game-agnostic, data-driven ground rendering SYSTEM** at a
**Skyrim/No-Man's-Sky** quality bar (not photoreal-locked) — the tech is the product. **Keepers:** base-field
geometry · histogram anti-tiling · the zone-placement *concept*. **Out of scope:** erosion/geometry +
scale/CDLOD (separate arcs). Master design `specs/2026-06-20-ground-rendering-system-master-design.md` — 8
components with keep/rebuild calls + a **foundation-first** build order (G-1 compositing core → data+depth →
placement+variation → detail → lighting). Guardrail: holistic design, **incremental gated execution, no
teardown**. Supersedes the GM1-6 sequence (old roadmap kept for its layer inventory + Unit-4 lesson).

**2026-06-20 — GROUND blocky-blend ROOT-CAUSED + G-1 Task 1 built (half-float weightmaps), AWAITING eye-gate.**
The close-up "blocky/stair-stepped/smeary" artifact = the **splat BLEND**, isolated decisively live (`tile
mode=none` → persists, so NOT textures/histogram; `splat debug=zones` → smooth, so NOT placement; `splat
debug=mix amt` → blocky = the **blend weight**). Cause: weightmaps are **8-bit `Rgba8` at 2048²/8192 m = 4 m
/texel**; `interlock_blend` uses the quantized low-res `t` as the boundary threshold → stair-steps (8-bit) +
blocky/smear (4 m). Phase **G-1** (`specs/2026-06-20-ground-g1-compositing-core-design.md`, plan
`plans/2026-06-20-ground-g1-compositing-core.md`): **T1 DONE** — weightmaps baked **half-float (`Rgbah`)**
(`packHalf2x16`, no CPU convert; `eb4733d`), placement byte-stable (0.13/255), de-quantizes the blend in the
**default** path (no toggle). **T2 DONE** — material samplers confirmed `mipmap_anisotropic` (no fuzz, no
change). **T3 WRITTEN-not-built** — `hq_blend` fragment-resolution AA interlock (boundary from full-res height
+ noise + `fwidth`, weight as bias) for the residual **4 m smear** — build ONLY if T1 isn't enough at the gate.
**NEXT (owed): user flies key 3 close to a transition + `splat debug=mix amt` → did de-quantizing kill the
stair-stepping?** Then T3-or-not → Phase G-2.

**2026-06-20 — Sun/Light Stage 3 (Night & Celestial) COMPLETE: 3d PASSED + celestial presets shipped.**
3d (stars + Milky Way) eye-gated live ("give it a pass for now"); Milky Way fixed from blocky → smooth
(was 2D value-noise projected on a sphere → switched to real 3D fbm `vnoise3/fbm3` sampled on the view
direction; wispy clumps + dust lanes + wobbling width). Sun-disc polish folded in (limb 0.55→0.70 =
spherical, not flat). Then, on the user's "do we have good presets/knobs/modularity?" audit, found the one
real gap: night/celestial had 22 live knobs but **no preset system** while sun/clouds/grade/moods all do.
Built it: `data/celestial_presets.json` (full_moon_clear, new_moon_dark, crescent, bright_moonlit,
deep_scary, exotic) + `TerrainLabUI.CelestialPresets` + Night-tab picker + `--celestial=N`, mirroring the
sun pattern (registry-routed; moon_color/moonlight_color rgb overrides → exotic amber moon). **Stage 3 done
end-to-end, all eye-gated** (3d self-checked via auto-shots; celestial-preset dropdown awaits the user's
live eye — user "can't visually check rn"). Minor owed: optional live moon/moonlight color pickers.
NEXT (user picks): #2 Clouds overhaul · #3 GPU atmosphere · #4 Stage-4 auto day/night. ROADMAP updated.

**2026-06-20 — Sun/Light Stage 3b (moon disc) + 3c (moonlight) eye-gates PASSED (live) → 3d (stars) next.**
Drove both via `review.tscn --nightgate=1`. **3b PASS** ("looks good"): textured phased moon (disc + limb +
reused-sun_fbm maria/craters + phase terminator new→half→full + cool halo), per-pixel cloud occlusion,
anti-solar. Iterated on feedback: halo was a phase-independent bright ring → scaled by illuminated fraction
+ lowered core mult (killed the "crazy luminosity" bloom); surface reworked (large maria + crater pits);
added a **Night lab tab** (night darkness/floor + all moon knobs) since the user wanted a dedicated
moon/night tab. **3c PASS** ("looks good"): moonlight = a 2nd cool shadow-casting DirectionalLight, anti-
solar via RotationDegrees, gated night×moon-up×phase, cross-fading with the sun. Two bugs caught with a
debug print: the light was never added to the scene tree (EnsureMoonLight didn't retry the AddChild →
identity transform, moon at horizon, no light) and an earlier LookAt gave wrong NoL — both fixed.
**Also shipped (user asks):** `L` = a toggleable studio inspection DirectionalLight (was an omni that
"didn't work" from altitude; default energy 2.0→1.0 + a Night-tab slider after "too bright"); and the
**banked fog-wash fix pulled forward** — depth fog/aerial is night-graded (FogSkyAffect 1→0.05 by
nightFactor) so the night sky/scene stops washing grey. NEXT: 3d stars + Milky Way, folding in the banked
Stage-1 sun-disc polish. Plan `plans/2026-06-20-night-and-celestial.md`.

**2026-06-20 — AAA anti-tiling PASSED the live eye-gate → histogram-preserving is the new default.** Built
the Deliot–Heitz histogram-preserving tiling-and-blending (spec/plan `2026-06-20-ground-anti-tiling-histogram-
preserving*`): T1 `HistogramCompute` (pure-C# forward/inverse LUT bake, `--histcheck` round-trip PASS on
clay/basalt/snow/sand, meanErr ~0.01/255), T2 `TerrainLab` `tile_lut` atlas (256×42 Rf, baked per zone-material,
+1 sampler), T3 shader `tile_mode=3` — **re-targeted mid-build** to the ACTIVE albedo path (`detail_alb`→
`histo_sample_wp`, a no-rotation triangle grid blending in gaussian space via the per-zone LUT), since the
review renders the splat-weightmap path (NOT `s_alb`/IQ, which is inactive); normal/rough left plain (their
tiling is imperceptible, documented). Verdict (user, live, Shift+3 IQ↔histogram A/B): **"the new system is
good!"** PASS, ~6.5 ms (within budget). **Decision: flip `tile_mode` default → 3 (histogram)** in
`lab_controls.json` + the shader uniform; the surface no longer seams. NEXT: retire the superseded IQ/hex/legacy
anti-tile paths (user "kill dead code" steer) as a verified follow-up, then re-judge GM1/2/3-A on the fixed
surface. Also added a **Shift+1–9 ground-lane review bank** (`TerrainLabUI.GroundReview.cs`, own partial via
`_ShortcutInput`) so ground toggles don't collide with the shared 1–9; Shift+3 = the anti-tiling A/B.

**2026-06-20 — Sun/Light Stage 3a (night sky) eye-gate PASSED (conditional, live) → 3b (moon) unblocked.**
Drove the night gate via `review.tscn --nightgate=1` (data/review_night.json: keys 1-9 = noon→dusk→deep
night→pre-dawn + dark-scary/moonlit-bright). User verdict: **"ok for now."** Sun arc 0-24, sun-below-
horizon vanish, dusk→night→dawn, and the strengthened `night_darkness` lever (real range after the
black-ambient-color fix) all approved. **BANKED follow-up (user-flagged):** the **depth fog / aerial
perspective is NOT night-graded**, so it adds bright world-ambient at night and washes out the darkness
range (8 vs 9 hard to read). Fix later (best folded into 3c moonlight / night-brightness tuning): night-
grade `FogLightColor`/`FogDensity`/aerial by `nightFactor`. Clean session (only the 3 benign startup
rebake lines, zero exceptions). Unblocks **3b — moon disc** as the next single gated phase.

**2026-06-20 — Sun/Light Stage 3a (night sky) BUILT; banked key-2 RD errors root-caused as benign.**
Resumed the Sun & Light lane at Stage 3 (Night & Celestial). Built 3a (behind defaults): `DriveTime` is
now a continuous 24 h sun arc (removing the day-clamp lets elevation go negative at night), a master
`_nightFactor` (0 day → 1 deep night, smoothstep of sun depth below horizon), night color anchors in
`time_presets.json` `day_script` (20/22/0/2/4 h), `night_darkness`/`night_ambient_floor` Light-tab knobs,
`time_of_day` range → 0-24, and a sun horizon-gate in `cloud_sky.gdshader` so the disc/corona/halo vanish
below the horizon. Self-check shots confirm believable dusk→night→dawn, dark night, no ghost sun, daylight
unchanged. **Banked 3a.4 RD errors investigated (systematic-debugging): they are benign radiance-rebake log
spam** — `ComposeLighting` re-writes the sun DirectionalLight → realtime sky-radiance re-bake → uniform set
briefly references `cloud_rd_tex`/dependent material (binding 1/34) → "not valid texture / us is null", 3
lines per discrete lighting change then quiet, clean exit, no crash, no visual impact. Same class as two
already-documented cases (CloudVolume frame-1 + the `63c59ff` env.Sky-swap fix). `--time` adds ZERO errors
over baseline. **Decision: accept + document** (broadened the CloudVolume comment) — a real fix touches the
fragile realtime-radiance↔Texture2Drd seam (cross-chat cloud lane) for cosmetic spam; not worth the risk.
Plan: `plans/2026-06-20-night-and-celestial.md`. Awaiting the live 3a eye-gate before building 3b (moon).

**2026-06-20 — Ground GM batch eye-gate (live, review key 3 / GM1 palette): textures OK, SURFACE COMPOSITING
FAILS → lane redirects to fixing the surface.** Drove key 3 with the user; cycled palettes
alpine_green→alpine_stone→arid under midday-neutral light (mood 2, POM/height/variation OFF = the approved
compositing core). Verdict (user, live, flying close/mid): "textures are OK, implementation/everything around them
sucks — artifacting, clear tile-to-tile chunk lines, weird color differences." So GM1's deliverable (a curated,
contrast-rich palette) is acceptable — the textures aren't drab — but the **shared ground SURFACE fails**:
hard-edged large rectangular **tile-chunk seams** with abrupt color/value steps (worst looking straight down,
close), visible **texture tiling/repetition** (cracked-clay), **salt-pepper speckle** on mid slopes. Confirmed
**lighting-independent** (mood 2 = midday-neutral) → genuinely in the compositing/placement implementation, not the
palette or the light. **ROOT-CAUSED live** (user-driven `splat debug` + per-toggle isolation, then confirmed in
shader code): **NOT the zones** (zone map renders smooth/organic) and **NOT the weightmap resolution** (my first
hypothesis — WRONG; legacy↔weightmap blend toggle changed nothing). **Two separable culprits:** (1) **IQ 2-tap
anti-tiling** (`tile_mode=1`, `iq_sample` in terrain_lab.gdshader): the blend factor `f` is **constant per
`floor(uv)` cell** at `tex_scale_m≈28 m`, so it jumps at every tile-cell boundary → **blocky stair-step seams**
aligned to the 28 m tile grid (worst up close); it feeds both albedo and `material_height→interlock`, which is why
`splat debug=mix amt` exposed the blocks. `tile mode=none(sharp)` removes them but reintroduces visible repetition.
(2) **macro color** (`macro_on`, Color tab): the big soft "weird color differences" blotches — user: "macro color
fixed a lot of artifacting" when toggled off. Residual after both = the true material **transition borders**
(interlock width, separately tunable). GM2/GM3-A (keys 4/5) ride this surface → deferred until it holds. Batch gate
1c stays **OPEN**. NEXT (design first, build behind toggles, eye-gated): a **non-blocking anti-tiling** technique
(smooth the IQ per-cell blend, or a better method) + **rework/tune macro** variation. No fixing on a guess — both
now confirmed.

**2026-06-20 — Sun SURFACE shader + sun presets: SHIPPED, default ON (realistic_midday).** User asked
for a procedural sun surface + a sun-preset pattern (spec/plan `2026-06-20-sun-surface-shader-and-presets*`).
v1 FAILED the eye-gate ("weird suns don't look good, didn't carry"); root-caused via auto-shot iteration:
sub-degree disc (surface too small), nuclear core clipped texture to white, full-color override = flat
blob, plus my cloud fix had stopped cloud_sky installing when clouds start off. Reworked: sphere-shaded
textured ball (foreshorten + de-foreshortened UVs, rim-faded granulation, carving sunspots, lower core
multiplier so texture survives tonemap, warm chroma, strong-tint+dim color override); cloud_sky installs
even clouds-off; sun_size cap 4→8. v2 PASSED ("looks good enough"). **Default ON = `realistic_midday`**
(perf effectively free — surface math gated to disc pixels, per-pixel cloud fetch replaced the centre
fetch 1:1); the `active` preset applies at startup, `neutral` = old flat disc. Sun-preset system
(`data/sun_presets.json` + Light-tab picker + `--sunpreset` + review key-1 cycle) establishes the
per-feature preset pattern. Also fixed: large discs were fully hidden when a cloud touched their centre →
**per-pixel cloud occlusion**. Colored fantasy (blood/alien) bleach at the bright core — opt-in, dim-able
later. Commits `346a1cd`,`b666bd3`,`c406e12`,`04a3f62`,`756ab7e`.

**2026-06-20 — Cloud error-burst fix: toggle clouds via uniform, never swap env.Sky.** Preset switches
re-set cloud_enabled=true each press; the old SetKnobBool re-assigned env.Sky every time, queuing async
radiance rebuilds that built uniform sets against a not-yet-valid Texture2Drd (binding 1 cloud_rd_tex,
binding 34 dependent material) → the error burst. Fix (systematic-debugging, evidence via render-thread
markers): toggle clouds via the cloud_enabled uniform only; cloud sky installs once and is never swapped
(clouds-off renders the clear-sky gradient + sun via cloud_sky). Verified 0 mid-session errors across
125+ live switches; a benign teardown-race burst remains at app-quit only (documented). Commit `63c59ff`.

**2026-06-20 — Sun/Light daylight eye-gate PASSED (live): Stage 1 sun disc + Stage 2 time-of-day both
approved → Stage 3 (night + moon) unblocked.** Drove review keys 1 + 2 with the user. **Sun disc (3b):
PASS** — "pretty good, maybe a little bit more; basically just a circle still but it does other stuff":
the halo/corona/horizon-redden read well, the disc itself reads flat. Owes a **small Stage-1 polish**
(limb-darkening gradient + a touch more size/corona presence; live-tunable Light-tab `sun *` knobs, then
bake defaults). **Time-of-day (3c): PASS** — "time of day is good; we just need the missing hours and a
moon, that comes later." Below-horizon = ~dark (exact darkness is a Stage 3 call). Both daylight stages
approved → builds the discipline green-light for **Stage 3 (night + moon/phases/cool moonlight + stars)**
as the next single gated phase. Minor correctness follow-up banked: key-2 re-apply threw transient
RenderingDevice uniform-set "invalid texture" errors (clean exit, not a crash) — investigate the Stage-2
cloud/compute re-bind. The GPU-compute physical atmosphere stays deferred (build only after night, and on
the CloudVolume `Texture2Drd`/CallOnRenderThread seam, not FieldCompute).

**2026-06-20 — GI/SDFGI eye-gate RESOLVED (Sun/Light lane): default SDFGI OFF + GI proxy OFF; new
Shadow & Lighting roadmap stage opened.** Drove review key 8 with the user (live). SDFGI's
camera-centered cascade renders a hard-edged bright box on the terrain that re-centers on the camera
as you fly ("a light that gets brighter as you get closer") — toggling SDFGI off removes it with no
visible loss, confirming the 0b investigation in motion. **Decision: default SDFGI off + GI proxy
off** — sharp detail-mesh shadows, no cascade box, ~4.7 ms (also ~0.5 ms cheaper than the old
sdfgi-on+proxy-on default). **Parked, not purged** (pillars + 0b "don't delete it"): both toggles
kept, revive GI when flora / erosion canyons / night+moonlight add real occluding/bouncing geometry.
Flipped defaults in `lab_controls.json` (`sdfgi_on`,`gi_proxy`→false), `TerrainLab.cs`
(`UseGiProxy=false`), `scenes/{review,terrain_lab}.tscn` (`sdfgi_enabled=false`). Also fixed a
misleading test: the SDFGI toggle is on the **Light** tab labeled `GI (SDFGI)`, not the Debug tab —
corrected the review banner (key 8) + NEEDS_REVIEW. **Opened a Shadow & Lighting roadmap stage**
(user's call: "add a new part of roadmap for shadow/lighting with whatever we have") to take the
current sharp-shadow setup to AAA — CSM/cascade tuning, contact/soft shadows, the proxy-on cheap-shadow
perf lever (~2.8 ms), and an SSIL re-check (still on; another view-dependent term). See ROADMAP Sun/Light lane.

**2026-06-20 — FULL-SCOPE RE-ROADMAP: finish both lanes → make it a world → climate/elements; water
co-designs with erosion (user direction).** After the doc-set reset, set the forward shape in three
phases. **(A) Finish the two lanes FULLY before moving on — especially ground texture:** Sun & Light
→ the *full* sky system (Stage 1 disc + Stage 2 daylight, built; then Stage 3 night + moon/stars, the
GPU-compute atmosphere, Stage 4 auto-cycle/fantasy — all gated, designed-ahead OK but built one phase
past the last gate); Ground/Texture → the *full* material stack **including erosion + surface height**.
**(B) Make it a WORLD** — the pivot the user called for ("it doesn't matter if one mountain looks good,
it's not a world"): scale infra (CDLOD → chunks → streaming → infinite) + biomes + macro procedural
variety + erosion-at-scale + flora/world-editing integration. **Hybrid build** (user's call): the
chunk/streaming spine is the keystone and the perf fix, but each content system is designed
region-first + stream-aware so scaling is wiring, not a rewrite. **(C) Climate & elements** — visible
water, precipitation (rain/snowfall), wetness, snow-on-ground.
**Key coupling (user's erosion insight, the crux):** WG15's erosion failed because *erosion wasn't
informed by water* — channels didn't follow real drainage, so it "didn't make sense when water was
added." Therefore the **water FLOW / hydrology MODEL co-designs with erosion in Phase A** (one field:
carved terrain + flow + sediment derived together; visible water *rendering* defers to Phase C but
consumes that same field). **Erosion + the terrain-depth/height work get a fresh brainstorm → spec →
plan → review** (the archived 2026-06-17 erosion spec — which already diagnoses the water-coupling root
cause — is the base; the new spec elevates visible water to a co-designed output). The separate
**material surface-height / POM** ("we didn't have height setup" — GM2, built fast) owes its own proper
write-up + its eye-gate. **Brought erosion + terrain-LOD specs back to the active set**
(`docs/superpowers/specs/`) from the archive. Build stays gated behind the current eye-gate batch.

**2026-06-20 — DOC-SET RESET + both active lanes PAUSED at a combined eye-gate (user direction).**
An audit found WG16 had grown **20 specs + 23 plans + 4 handoffs in five days** (~100k words vs
~7.4k lines of code) — the exact "process outran the results" sprawl the project was founded to
avoid (cf. 2026-06-15 "low plans") — and that **both active lanes had built three phases past their
last passed eye-gate** while the user couldn't be at a screen: GROUND built GM1+GM2+GM3-A unjudged;
SUN & LIGHT built Stage 1 + the Stage 2 decouple/time-of-day unjudged (and had spec'd a GPU-compute
Hillaire atmosphere + night/celestial/fantasy stages on top). **Decision: finish the in-flight work
to a clean, default-off/approved-look stopping point, then PAUSE both lanes for one combined
eye-gate, then re-roadmap from the results.** Concretely: (1) set every new GM + sun feature
default-off / approved-era look so it's opt-in and reversible; (2) **archive the sprawl** —
`docs/archive/` now holds all 23 plans, 14 superseded/not-scheduled specs (incl. the erosion and
terrain-LOD future-arc designs, pointer-linked), the 4 handoffs, and transient prompt docs (git mv,
history intact); (3) keep a **thin active set** — the 2 lane roadmaps + the 4 built-but-unapproved
specs + `NEEDS_REVIEW`/`DECISIONS`/`HANDOFF`/`TECH_STACK`/`performance` + the cloud/godray reference
overviews; (4) rewrite `ROADMAP.md` fresh around the NOW eye-gate session and a re-stated
**discipline rule: a lane builds at most ONE phase ahead of the last PASSED eye-gate; thin docs, not
a plan-per-lane.** The deferred GPU atmosphere must be re-spec'd around the CloudVolume render-thread
`Texture2Drd` seam (a local-RD texture can't be sampled by a material), not the FieldCompute pattern.
Gate order = light → base shading → ground → sky → whole-scene AA (upstream first so judgments aren't
contaminated). Nothing proven was dropped; nothing deleted.

**2026-06-20 — RE-SEQUENCE the GROUND arc: material foundation first; Unit 4 breakup PARKED (user, live).**
Unit 4 (procedural breakup) was built (T1–T6) and failed its live eye-gate: hard square edges + "barely
does anything." Deep root-cause (systematic-debugging): three compounding causes, one wrong premise —
(1) the masks are `f(heightfield)`, so within a *uniform* area they're ~constant → they add *between*-area
variety, NOT the *within*-area variety the user asked for; (2) baked at 4 m/texel (8192 m region / 2048
res) and stretched across the region → thresholding reveals the texel grid as faint squares; (3) the
near-monochrome palette gave the material swaps no contrast. **Context-driven breakup is the wrong tool
for "variety within an area."** Decision (user was open to a full reset; argued against — resetting proven
work is the WG15 trap): **re-sequence, don't reset.** Build the material FOUNDATION first — **GM1 curated
palette → GM2 real height maps → GM3 within-area procedural patches → GM4 placement+erosion → GM5 detail
layers → GM6 scale/perf.** Unit 4's bake infra is kept (parked, `breakup_on` default off, `3077abe`) for
revival as a SECONDARY context bias in GM4 once erosion provides real steep faces/gullies. Authoritative
roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md` (consolidates the scattered ground items).

**2026-06-19 — Build the TERRAIN MATERIAL SYSTEM properly (weight-blended pipeline), not tweaks (user,
live).** Reviewing the ground live, the user judged it "only frameworked, no real work done" and "small
tunes instead of building a system." Diagnosis (close-up shots): it reads **blocky** (splat bakes
per-texel dom/sec INDICES sampled filter_nearest on the 4 m grid + a roughness-proxy heightblend hack →
stair-stepped patches) and **flat/painted** (no parallax relief, weak normals). Key finding: the roadmap
planned most layers as units (anti-repeat, distance-detail, surface-depth, breakup, color) but **never
had a plan for the blend/compositing QUALITY layer** — it assumed the splat framework blends fine. That
hole is why it feels frameworked. **Decision: build the standard AAA weight-blended material pipeline as
a real system; build order = compositing-core first** (Blend quality = smooth weights + height-map
interlocking + organic breakup; + Surface relief = parallax-occlusion + real normals), then breakup
(Unit 4), then color (Unit 5); placement (G1, approved) feeds it. **Validated against pillars** as the
proper AAA/performant/quality/best-long-term setup, with TWO ceiling upgrades designed-for but deferred:
real **height maps** (derive from albedo-luma/roughness first) and **RVT/virtual-texture caching** (the
infinite-world 8 ms answer; caches this same pipeline; needs chunks → build pipeline first). Perf rules:
top-2 blend, POM LOD-gated near, masks baked once, profile in-motion. Spec
`specs/2026-06-19-terrain-material-system-design.md`; handoff
`handoffs/2026-06-19-terrain-material-system.md`. Continuing in a NEW chat. G1 approved live; palette +
splat-warp this session were stopgaps (the blend LAYER is the real fix).

**2026-06-19 — Perf target set = 8 ms total (world generator, long-term); GI/shadow proxy DEFAULT ON;
god rays moved to a parallel chat.** User set the long-term budget: **8 ms (~125 fps) for the WHOLE
generator**, with flora/water/erosion/biomes still to fit. Flying frame now ≈9.6 ms (clouds on, proxy
on): mesh floor ~4 ms, SDFGI-on-proxy ~2.9 ms (intrinsic cascade cost — proxy already removed the
geometry part), clouds ~2 ms. Defaulted the GI/shadow proxy ON (user's call; fidelity eye-check still
owed). Remaining levers to 8 ms — **CDLOD terrain LOD (#1, also cuts SDFGI+shadow), SDFGI config (#2),
cloud cost (#3) — are ALL eye-gated and PARKED** because the user can't do visual checks right now.
**God rays are being refactored in another chat** → don't touch `GodRays.cs`/`shaders/godray*` here.

**2026-06-19 — Perf: in-motion profiling correction + GI/shadow PROXY mesh (user push, "code not
settings").** The user reported 30–120 fps flying at 1440p; static `--profile` showed 130–230 and
hid it. Root cause: **static profiling lets SDFGI converge** — added `--profmove` (orbit during
profile) and found **SDFGI costs ~12 ms IN MOTION** (≈0.2 ms static) because it re-voxelizes the
4M-vert un-LOD'd mesh every frame the camera moves; shadows add ~3 ms the same way. Frame is
geometry-bound (4K≈1440p), not pixel/fragment-bound. **Fix (the AAA/structural one, user's call):
a coarse GI/shadow PROXY** — a 256² (~65k-vert) copy of the same heightfield feeds SDFGI
(`GIMode Static`) + casts shadows (`ShadowsOnly`, invisible in colour) while the detail mesh renders
the view (`GIMode Disabled`/`CastShadow Off`). GI/shadows are low-freq → need shape not fine verts.
In-motion 1440p: clouds-off 55→131 fps, clouds-on 49→101 fps; GI retained (~1.7 ms vs ~12 ms).
Behind `--giproxy`/Debug toggle, **DEFAULT OFF** (GI/shadow fidelity is the user's eye-gate; flip to
default-on once approved). git restore point: tag `backup-pre-gi-proxy-2026-06-19`. Next mesh lever =
the CDLOD arc. See `docs/performance.md`.

**2026-06-19 — Ground-presentation arc REORDERED: build the placement+palette FOUNDATION first
(user direction, live).** Flying the lab, the user judged the ground "doesn't look good enough to
even judge detail" — "it's just random ground, never had real setup/masks/shaders other than the
anti-tiling." Diagnosis confirmed in code: the splat machinery exists but does ARBITRARY things —
the companion material is `clamp(dominant−1,0,6)` (previous zone *by array index*, not by meaning),
the 7-zone palette is a near-monochrome grey subset, and placement is altitude+slope bands only (no
aspect/flow/curvature). So the arc's *order* was wrong: it deferred material PLACEMENT (Unit 4) and
PALETTE (part of Unit 5) to the end as "polish," when they are the FOUNDATION. **Decision: pull them
forward as a rule-based splatting foundation** — GPU-bake a real signal set (altitude, slope, signed
curvature, aspect→sun-exposure, moisture/flow proxy, cavity) and assign materials by MEANING via
placement rules, with a curated contrast-rich palette, all baked-once so the fragment cost stays flat
(protects the 160+ fps target the user set). Reuse the 7 slots as ROLES (no sampler churn); data-driven
palette/rules (`data/ground_palette.json`). **Scope (user's roadmap): make THIS region really good /
basically done FIRST, then chunks → infinite/procedural → more biomes last.** Build order G1 (signals+
rules, current palette → prove placement coherent) → G2 (curated palette → photoreal) → G3 (aspect+
moisture rules → follows water/exposure); THEN resume detail Units 2/3/5 on top. **Unit 2 (distance
detail) is BUILT this session, default OFF, shelved** until the foundation reads good (it only added a
near-band albedo tweak on a base that wasn't ready). Couplings: erosion E2 drainage later feeds the
moisture proxy; biome field later selects the palette/ruleset. Spec:
`specs/2026-06-19-ground-foundation-splatting-design.md` (supersedes the build ORDER of the
2026-06-17 ground-presentation arc spec). Perf baseline recorded: lab window clouds-off 227 fps/4.4 ms,
clouds-on 156 fps/6.4 ms (real-res lower; fragment-bound; mesh floor is the terrain-LOD arc's job).

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
