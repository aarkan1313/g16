# WG16 — Roadmap

The single source of truth for what's done, what's being judged, and what's next. Thin by
design. Pairs with `NEEDS_REVIEW.md` (the eye-gate queue), `DECISIONS.md` (the why, per
decision), and `HANDOFF.md` (orientation). Superseded designs are frozen in `docs/archive/`.

Last updated: 2026-06-21 (full project audit + ground/texture iterate-vs-rebuild review reconciled in:
see `docs/AUDIT-2026-06-21.md`, the new **🛠 Debt & Remediation backlog** below, and the rewritten
**Ground / Texture** lane). Prior: 2026-06-20 full-scope re-roadmap.

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost. Lead
with the better option, not the cheap shortcut.

---

## 🧭 The discipline rule (read first — this is why we reset)

In five days WG16 grew 20 specs + 23 plans — the "process outran the results" sprawl it was
founded to avoid — and **both lanes built three phases past their last eye-gate** because the user
couldn't always be at a screen. The cure, encoded:

> **A lane builds at most ONE phase ahead of the last PASSED eye-gate.** When the gate queue holds
> work the user can't yet see, **STOP and bank — don't open new depth.** Build behind toggles
> defaulting to the approved look. **Designing ahead (spec/plan) is fine and encouraged; *building*
> ahead is the trap.** Thin docs: a roadmap line + a `DECISIONS.md` entry, not a plan-per-lane.

The eye is the only gate for look. Mechanical checks gate correctness/cost. Never judge a motion
artifact from a still. Always profile `--profmove`.

---

## The shape (user direction 2026-06-20)

Three phases, in order. **Finish the two lanes fully — especially ground texture — before moving
to the world.** "It doesn't matter if one mountain looks good — it's not a world" — so once the
region look is *done*, the priority pivots hard to scale/biomes/procedural, then the elements.

- **Phase A — Finish the two lanes (the region look, FULLY).** Sun & Light → full sky system;
  Ground/Texture → full material stack **incl. erosion + water hydrology + surface height**.
- **Phase B — Make it a WORLD.** Scale infra (CDLOD → chunks → streaming → infinite) + biomes +
  macro procedural variety + erosion at scale + flora/world-editing integration. **Hybrid build:**
  the chunk/streaming spine is the keystone (it's also the perf fix and the thing that makes it a
  world), but each content system is designed region-first + stream-aware so scaling is wiring.
- **Phase C — Climate & elements.** Visible water rendering, precipitation (rain/snowfall) +
  wetness, snow-on-ground — consuming Phase-A's hydrology model + the Weather axis.

> **⚠ Water + erosion are coupled and span phases.** WG15's erosion failed because erosion wasn't
> informed by water (channels didn't follow real drainage; it "didn't make sense when water was
> added"). So the **water FLOW / hydrology MODEL co-designs with erosion in Phase A** (one field:
> carved terrain + flow + sediment all derived together). Only the **visible water RENDERING**
> (rivers/lakes/ocean shaders) defers to Phase C. Erosion E1 (single-region coupled sim) is Phase A;
> E2–E4 (bake/stream/scale) are Phase B.

---

## ▶ NOW — finish Phase A · the combined eye-gate session

> **2026-06-21 — full project audit + ground/texture review done.** Detailed findings:
> `docs/AUDIT-2026-06-21.md`. Folded into this roadmap: (1) a new **🛠 Debt & Remediation backlog**
> (below) tracks every audit fix; (2) the **Ground / Texture lane is rewritten** to the review's
> verdict — **ITERATE, don't rebuild** (the from-scratch redesign already exists as the ground master
> design; execute it surgically — the WG15 graveyard was teardowns). **Honest status correction:** the
> sky lane was NOT "paused" after 2026-06-20 — at user direction it built a full burst (Stage 3 →
> CO-1..4 → Stage 4 → AT-1 → AT-2). **AT-1 atmosphere AND AT-2 aerial are now DEFAULT-ON but were never
> live-eye-gated** (and AT-2's frame cost was never measured) — both added to the gate below + NEEDS_REVIEW 11.

Most look work built so far is **awaiting the user's live eye** (the discipline rule is aspirational —
several pieces shipped default-on un-gated; this batch closes that gap). **Run `scenes/review.tscn` —
number keys 1-9 jump to each gate item** (guide + per-item judge criteria in `NEEDS_REVIEW.md`). **Run
the gates upstream → downstream so judgments aren't contaminated:**

1. **Light first** (it washes everything): GI/SDFGI default (0b ✅) → Sun disc Stage 1 (3b ✅) →
   time-of-day Stage 2 (3c ✅) → **AT-1 atmosphere + AT-2 aerial + AT-3 cloud-light (11) ✅ eye-gated
   2026-06-21 (default-on confirmed; AT-2 +0.7 ms measured) — re-confirmed good 2026-06-21.**
2. **Base shading:** H1 BRDF clouds-off regression (6 ✅).
3. **Ground under SETTLED light:** the GM1/GM2/GM3-A batch is **superseded by the ground redesign** —
   run the **Step-0 "wrong-defaults" ground gate (1e)** first (shows how much "drab" is just suppressed
   defaults), then iterate per the rewritten **Ground / Texture** lane below. *(Part of the "drab" is the
   default-on atmosphere warm-washing the surface — judge ground only after the sky lane settles.)*
4. **Sky:** Clouds feature review (5 ✅) → god rays (3 ✅) → Celestial C1 night-sky (galaxy/nebula REJECTED + v2 REDO KILLED 2026-06-21; night sky = moon+stars+meteors).
5. **Whole-scene last:** AA in motion (7) + GI-proxy fidelity (2).

## ⏸ Phase A — the two lanes (FULL scope; finish before Phase B)

### GROUND / TEXTURE — the weak spot · VERDICT (2026-06-21 review): ITERATE, do not rebuild
> **⮕ SUPERSEDED later 2026-06-21 → RESET (user's call after flying it).** The G-0 probe put the levers in front of
> the live eye; at the **real-height** step the blend broke into blocky **4 m-grid facets** — proving the base problem
> is STRUCTURAL (the compositing core on the 4 m weight field), not surface tuning. New verdict: **reset the material
> RENDERING** (skin, not bones) to **per-pixel procedural placement + texture arrays** (no baked splat → no blockiness;
> scales to many materials/biomes). **Authoritative now:** `specs/2026-06-21-ground-material-system-reset-design.md`
> (+ plan `plans/2026-06-21-ground-material-system-core.md`, handoff `handoffs/2026-06-21-ground-v2-core-start-here.md`).
> The root-cause analysis below stays valid (it's *why* the reset); the iterate sequence under it is superseded by the
> reset's Units 1–6. The G-0 / fwidth / rough_floor work was the diagnostic and stays.

Authoritative design: `specs/2026-06-20-ground-rendering-system-master-design.md` (the from-scratch,
game-agnostic redesign — it ALREADY IS the "start over," to be executed surgically). Full review +
both adversarial briefs: `docs/AUDIT-2026-06-21.md` is the project audit; the ground iterate-vs-rebuild
review confirmed the code does NOT support a teardown — the two hardest pieces (half-float weight field
+ histogram anti-tiling, the one user-PASSED ground feature) are already built well; a rewrite re-risks
solved problems and re-enters the WG15 teardown graveyard. The "drab/flat" look traces to a small set of
concrete causes, ranked below by look-per-effort.

**Why it reads drab (code-confirmed root causes):** (1) `ROUGHNESS` is force-clamped `>=0.5` with NO UI
control (`terrain_lab.gdshader:103,987`) → whole ground dead-matte, zero specular/sheen. (2) NO real
surface height in the active look — height is the inverted-roughness proxy; GM2's genuine Poisson relief
defaults OFF; POM is UV-only (zero depth/silhouette); **the material library has zero height maps on disk**.
(3) active palette is the old drab `alpine_green`, not contrast-rich `alpine_stone`. (4) `tex_scale_m`
runs at 28 m vs the shader's 9 m → textures stretched ~3×. (5) macro-color AND within-area variation both
default OFF → a slope is unmitigated-uniform. (6) part of "drab" is the default-on atmosphere warm-washing
the surface — judge ground under settled light.

**Sequence (each behind a toggle defaulting to current, eye-gated):**
- **G-0 · the "wrong-defaults" gate (NEXT, cheap, reversible — NEEDS_REVIEW 1e).** Flip the suppressed
  cluster and fly the A/B: add a `rough_floor` slider → ~0.15 (⚠ it was set high to hide specular fuzz —
  watch for shimmer), bind GM2 real height (`height_from_maps=true`), switch palette to `alpine_stone`,
  turn on GM3-A variation, reconcile `tex_scale_m`. One session shows how much "drab" is just suppressed
  good-tech vs genuinely-missing capability — **before** building anything.
- **G-1 · compositing-core currency + honest-finish.** Half-float weights + histogram anti-tiling already
  shipped+PASSED (the G-1 spec is STALE — mark them DONE). Real remaining G-1: the missing `fwidth`-based
  transition AA + the promised reversible `hq_blend` A/B toggle (the half-float flip shipped un-A/B-able — a
  guardrail slip); single-source the **drifted duplicate `zone_weights`** (band_soft_mult — a real bake-vs-
  live correctness bug); the **tile_mode dropdown cleanup** (dead IQ/hex options + `hex_contrast` — one
  eye-verified change). Spec `specs/2026-06-20-ground-g1-compositing-core-design.md` (needs the currency pass).
- **G-2 · material data + surface depth (the real long pole).** Add a **first-class height channel** to the
  material schema; fix `MaterialBoard` (mis-binds AO→height, judges with a height-ignoring technique); source/
  author materials **with real height/displacement**; then POM-with-depth or heightblend that reads as 3D.
  The asset gap — not shader tuning — is the genuine prerequisite for the Skyrim/NMS bar. GM2/GM3-A built work
  reabsorbs here + G-3.
- **G-3 · placement + within-area variation.** Reabsorb GM3-A (correctly built world-pos noise) + re-tune;
  G3 aspect/moisture/flow rules. **The one genuine structural rebuild:** the active weight/placement field is
  baked at **4.0 m/texel** (8192 m / 2048 res) — the exact resolution the Unit-4 lesson declared fatal for
  close work, now governing the LIVE blend. Bake finer and/or move high-frequency placement fragment-side.
- **G-4/G-5 · detail scatter + lighting response** (master-design components 7-8), then the **big one —
  Terrain depth & hydrology (erosion + water-flow + surface height)** — a fresh brainstorm → spec → plan →
  review (`specs/2026-06-17-erosion-arc-design.md` is the base; geometry erosion is scoped OUT of the surface
  redesign and is its own arc). Build gated, one phase past the last pass.

### SUN & LIGHT — full sky system
Lane roadmap: `specs/2026-06-20-sun-light-system-architecture.md`.
- **GI/SDFGI decision ✅ RESOLVED 2026-06-20 (eye-gate key 8):** default **SDFGI off + GI proxy off**
  (cascade box artifact removed, sharp shadows, ~4.7 ms). Parked, not purged — revive GI with flora /
  canyons / night. See DECISIONS + NEEDS_REVIEW 0b.
- **Shadow & Lighting pass (NEW stage — user's call 2026-06-20, "with whatever we have"):** with SDFGI
  parked, take the current **sharp directional-shadow** setup to AAA before/alongside Stage 3. Scope to
  spec: directional **CSM / cascade tuning** (splits, bias, range — kill any remaining shadow shimmer/
  shifting), **contact + soft (PCSS-style) shadows**, the **proxy-on cheap-coarse-shadow perf lever**
  (~2.8 ms vs ~4.7 ms — quality/cost A/B), and an **SSIL re-check** (still on; another view-dependent
  GI term — decide keep/tune/off). Eye-gated like the rest; design-ahead OK, build one phase past the
  last gate. Pairs naturally with the moonlight shadow work in Stage 3.
- **Daylight ✅ GATED 2026-06-20 (live):** Stage 1 sun disc (3b), Stage 2 decouple + time-of-day (3c),
  sun **surface** shader + presets (default `realistic_midday`), BRDF regression (6), clouds review (5),
  god rays (3). Specs `2026-06-20-lighting-decouple-and-time-axis-design.md`,
  `2026-06-20-sun-surface-shader-and-presets-design.md`.

#### ☀️🌙 FINISH THE FULL SKY SYSTEM (user's call 2026-06-20: "plan it all, make a roadmap, lets do it")
> **SKY LANE 2026-06-20: Stage 3 (Night & Celestial) ✅ · #2 Clouds overhaul (CO-1..CO-4) ✅ · #4 Stage 4
> (auto cycle + fantasy) ✅ — all eye-gated/soft-gated live.** Moon now on its OWN arc (decoupled). **Galaxy/Milky
> Way reviewed live 2026-06-20 → NEEDS WORK** (fog band + uniform half-circle arc) → folded into the new
> **Celestial expansion (#6)** below. **STATUS 2026-06-21: #3 GPU atmosphere (AT-1/2/3) ✅ arc complete · #6 C1
> ✅ CLOSED (galaxy/nebula REJECTED; night sky = tuned moon+stars). C2 meteors ✅ PASS. **C1-v2 galaxy/nebula REDO
> ❌ KILLED 2026-06-21** (billboards: procedural + volumetric both failed the look within budget — feature removed).
> NEXT: C2 planets → #5 shadows → #7 perf.** Review keys: 2 = night sky (moon+stars+meteors) ·
> 6 = cloud types · 7 = fantasy skies. See `handoffs/2026-06-21-sky-lane-next-steps.md`.

Ordered, each its own spec → plan → eye-gate, built ONE phase past the last pass (discipline rule).
Master architecture: `specs/2026-06-20-sun-light-system-architecture.md`.
1. **Night & Celestial (Stage 3) ✅ DONE + GATED 2026-06-20** — 3a night sky (24h arc, sun-below-horizon,
   tunable darkness/floor) · 3b moon (textured/cratered, phases + terminator, cool halo, cloud-occluded) ·
   3c moonlight (2nd cool shadow-casting directional, gated night×moon-up×phase) · 3d stars + Milky Way
   (procedural 3D-noise, twinkle/rotation/fade). All on a **Night lab tab** (22 knobs) + **6 celestial
   presets** (`celestial_presets.json` + picker + `--celestial`). Also shipped: fog-wash night-grade,
   `L` inspection light, sun-disc limb polish. Plan `plans/2026-06-20-night-and-celestial.md`. Spec
   `specs/2026-06-20-night-and-celestial-design.md`. Owed (minor, await user eye): celestial-preset
   dropdown live-check; optional live moon/moonlight color pickers.
2. **Clouds overhaul — IN PROGRESS** (`specs/2026-06-20-clouds-overhaul-design.md`, plans
   `plans/2026-06-20-clouds-co1-vertical-realism.md` + `plans/2026-06-20-clouds-co2-types.md`). Phased CO-1
   vertical realism → CO-2 types (cirrus 2D layer + stratus shape-mode; cumulus preserved) → CO-3
   anti-repetition/horizon → CO-4 presets. **Weather-axis tie-in DEFERRED** (own later stage, user's call).
   - **CO-1 vertical realism — ✅ GATED 2026-06-20 (live).** Per-deck `height_profile` (flat base → faded/anvil
     top), layer fields 19-21, lockstep ×3 shaders; Clouds-tab toggle + knobs + `--cloudprofile`. Kept **opt-in
     (default-off)** — approved look unchanged; default-on is a later call.
   - **CO-2 types — ✅ GATED 2026-06-20 (live).** Stratus shape-mode (field 22, cellGate→sheet, **broken with
     gaps**, lockstep ×3) + **world-anchored** cirrus 2D layer (finite-altitude parallax + drift + per-region
     density/orientation/size variety, kept ~free via early-out + 3-octave noise). Clouds-tab `cumulus↔stratus`
     + `cirrus: coverage/density/wind/scale/sharpness/variety/speed/altitude`; `--stratus`/`--cirrus`; review
     key 6 cycles cumulus→stratus→cirrus. Stratus = selectable type, cirrus = opt-in layer (default off).
   - **CO-3 anti-repetition — 🔨 BUILT 2026-06-20, default-off/banked.** Macro coverage/size variety (field 23,
     lockstep ×3) clusters cumulus into varying-size groups; Clouds-tab `macro variety` + `--antirepeat`.
     On-chunk benefit marginal (user couldn't tell); the visible repetition is the far/down **"huge dome"**
     regime → **Phase-B (infinite world)**, banked. Horizon: no clear defect found from the live look — left.
     High-effort code review done (4 fixes incl. shadowcheck-active-config; 1 pre-existing detail-erosion
     divergence deferred). See DECISIONS.
   - **CO-4 presets — 🔨 BUILT 2026-06-20, awaiting the picker flip-through eye-gate.** 6 new Clouds-tab
     presets composing the new types (Fair Weather Cumulus, Cirrus Veil, Cumulus & Cirrus, Overcast Stratus,
     Stormy Anvil, Mackerel Sky); `--preset=N`. `ApplyCloudPreset` resets the type levers first → self-contained
     presets. Verified distinct via auto-shots. **#2 Clouds overhaul ~COMPLETE pending this gate** (weather-axis
     tie-in remains DEFERRED to its own stage).
3. **GPU-compute physical atmosphere — 🟢 AT-1 BUILT + day-gated + DEFAULT-ON 2026-06-20**
   (`specs/2026-06-20-gpu-atmosphere-design.md`, plan `plans/2026-06-20-gpu-atmosphere-at1-core-sky-color.md`).
   Hillaire LUTs (transmittance→multi-scatter→sky-view) on the CloudVolume **`Texture2Drd`/`CallOnRenderThread`
   seam** (`AtmosphereCompute` node). `cloud_sky.gdshader` `background()` samples the sky-view LUT. **AT-1 day sky
   soft-PASSED live ("looks good") → flipped DEFAULT-ON** (perf +0.2 ms; keyframed kept as the toggle-off
   fallback). Night hands off to the keyframed night cleanly (atmosphere bows out). Review **key 8** cycles the day
   presets. **Follow-ups:** confirm the horizon flash gone in motion · mood/fantasy sky-tint bypassed by day (the
   physical sky owns daytime color) — handle if the colored fantasy skies must persist · exposure default.
   **AT-2 aerial perspective — 🟢 BUILT + eye-gated → soft-PASS 2026-06-21 (default-on, subtle 0.4).** Froxel aerial
   LUT (32³, `Texture3Drd`) + screen-space composite (`AerialPerspective`, no terrain-shader edit), A/B vs the built-in
   fog via review key 8. In-scatter is additive → recalibrated strength to 0.4 (the planned ~2/10 washed); user "a
   little better than off"; cost +0.7 ms. Spec `…at2-aerial-perspective-design.md` · plan `…at2-aerial-perspective.md`
   (DECISIONS/NEEDS_REVIEW 11 2026-06-21). Supersedes the keyframed `day_script` for sky color.
   **AT-3 cloud-lighting — 🟢 BUILT + eye-gated → PASS 2026-06-21, default-ON.** Clouds lit by the physical sky:
   ambient = sky-view zenith (tops) + horizon-toward-sun (warm undersides at sunset); direct = sun-transmittance
   reddened. `physical cloud light (AT-3)` / `--cloudlight`. **Pivoted from approach A** (cross-node GPU LUT sampling
   in the cloud raymarch crashed the render device) to a CPU readback → param-color handoff (visually identical,
   stable; DECISIONS 2026-06-21; commit 6d0d590). User: "yep it works." **→ The GPU-atmosphere arc (#3) is COMPLETE
   (AT-1 sky · AT-2 aerial · AT-3 cloud-light, all default-on).** Owed: a readback throttle for the running cycle →
   ROADMAP #7 perf pass.
4. **Stage 4 — IN PROGRESS** (`specs/2026-06-20-stage4-cycle-and-fantasy-design.md`).
   - **ST4-1 auto day/night cycle — ✅ GATED 2026-06-20 (live, "looks pretty good").** Clock in `_Process`
     (play toggle + `cycle speed` + `--autotime`); manual scrub preserved. Follow-ups from the gate: **moon
     arc decoupled from the sun** (own phase-lagged arc — FIXED) · **galaxy/Milky Way needs review** (banked,
     NEEDS_REVIEW 10).
   - **ST4-2 fantasy/exotic — ✅ GATED 2026-06-20 (soft-pass, "pretty good, good enough for now").**
     Cross-system presets (`fantasy_presets.json` + `FantasyPresets.cs` + Night-tab picker + `--fantasy=N`,
     **review key 7 cycles them**) composing a sun preset + celestial preset + persistent `sky_tint` +
     moon/moonlight colors: blood_moon, alien_green, violet_night, harvest, ember_dusk.
   **Multiple suns/moons → UN-DEFERRED as #6 C3** (user's call 2026-06-20; see below). **#4 Stage 4 COMPLETE.**
5. **Shadow & Lighting pass — 🟡 PARTIAL 2026-06-21 (code-side done; terrain-mesh part deferred to CDLOD).**
   ✅ Directional CSM under-resolution fixed: 8192 atlas + SoftHigh PCF + blend_splits + flatter splits (0.1/0.28/
   0.6) + max 6000 (all via `RenderingServer`/Sun props in `TerrainLabUI.Lighting.cs`, no scene/project edits).
   ✅ The "blocky shadow blob" was **SSAO @2.0** on the faceted 4 m mesh, not a shadow → dialed to 0.6. See
   DECISIONS 2026-06-21. **Deferred (terrain-coupled, ride CDLOD):** diffuse-terminator faceting (mesh res),
   per-chunk caster AABBs (CDLOD already does), cloud→terrain shadow receive (re-add in the new terrain shader),
   proxy-on cheap-shadow lever, and cascade split-*distance* retuning once terrain scale settles. Godot 4 has no
   native contact shadows. Night moonlight→clouds shipped (#5 Layer 1). 8192 atlas = perf lever to dial down later.
6. **Celestial / Night-Sky expansion — 🆕 ROADMAPPED 2026-06-20** (user scope-up during the galaxy review).
   Built **AFTER #3 atmosphere** (the physical sky is the backdrop the celestial work sits on; and C3 feeds it).
   Each piece its own spec → plan → eye-gate, built one phase past the last pass (discipline rule).
   - **C1 — Procedural night-sky. ✅ CLOSED 2026-06-21: galaxy + nebulae SHELVED, night sky = MOON + STARS.**
     Built the full baked galaxy/nebula/starfield system (T1-T5, baked==proc verified) + iterated it live over many
     rounds (localized the galaxy, killed the bloom glow, ridged nebulae, made the galaxy body read). **User verdict:
     the galaxy/nebula direction "doesn't work" → killed it.** Final night sky = the **tuned starfield** (−60% count,
     −70% slower blink — user PASS) + the **moon** (Stage 3, brought back). Galaxy `mw_brightness` default 0, nebulae
     `neb_count` 0; the bake/shader machinery is **dormant + zero per-frame cost** (early-out skips the sample at
     brightness 0), fully tunable back via the Night tab if ever wanted. Review **key 2** = "night sky — stars + moon"
     (drives night directly + aims at the moon; robust to the ground-strip Apply.cs breakage). Commits a43515a..db0bebe
     (pushed). **The galaxy idea is parked, not deleted** — revisit only on a fresh, different concept (this one was
     rejected). Net C1 deliverable: a cleaner, tuned moon+stars night.
   - **C2 — Celestial bodies. 🔨 IN PROGRESS.**
     - **Meteors / shooting stars — ✅ PASS 2026-06-21 (eye-gate, live, review key 2), default-on.** Procedural
       in-shader (`cloud_sky.gdshader` `meteors()` — 2 TIME-hashed channels, occasional ~1 s streaks with a glowing
       head + trail + per-meteor color variety, gated night×horizon, ~free when idle). 7 Night-tab tunables
       (`meteors on`/`rate`/`brightness`/`length`/`speed`/`color`/`color variety`) + `--meteordebug`. Spec/plan
       `2026-06-21-celestial-c2-meteors*`; commits 29c52e4 · 14ec581 · 371332d. Optional follow-up: meteor presets.
     - **Planets + named stars + north star — ✅ PASS 2026-06-21 (eye-gate, review key 2), default-on.** Curated
       in-shader (`cloud_sky.gdshader` `planets()`/`bright_stars()`/`north_star()`): ~5 bright steady FANTASY-colored
       planet points (cyan/magenta/emerald/amber/violet) + glow, ~7 landmark stars (slow twinkle), one prominent
       cool-white pole star; all ride the celestial sphere, night-gated. Night-tab knobs: `planets`/`planet
       brightness`, `bright stars`/`bright star brightness`. Also dimmed the moon default + glow. Spec/plan
       `2026-06-21-celestial-c2-planets*`; commits a480a3e · 198e10d.

   - **C1-v2 — Galaxy / Nebula. ❌ KILLED 2026-06-21 (feature dropped).** Rebuilt as billboards: structured
     non-noise generator → flocculent/domain-warped → **volumetric raymarch** (Beer + color ramp) → **lit
     self-shadowed** volumetric. All read "fake/not good." User's call: two volumetric shots within ~1 ms, else
     kill — both failed, so the lane was **removed completely** (commit 62a3835; shader + C# + controls + presets).
     **Do not re-attempt procedurally** — the only paths research suggests would clear the bar are authored/offline
     **textures** or expensive (>1 ms) lit volumetrics, neither in scope for a night-sky accent. See DECISIONS +
     `docs/superpowers/specs|plans/2026-06-21-celestial-galaxy-nebula-billboards*`. Night sky = moon + stars + meteors.
   - **C3 — N suns + N moons.** Generalize the single-sun + single-moon architecture to **arbitrary counts** — each
     with its own arc / color / size (phase for moons) + lighting contribution (extends `ComposeLighting`'s one-writer
     and the per-luminary disc render in `cloud_sky.gdshader`). **Dependency:** feeds the atmosphere scattering (sky
     color is computed from the sun direction[s]) → **extends AT-1's LUTs.** Heaviest/architectural — last.
7. **⚙️ END-OF-ARC CODE-EFFICIENCY PASS — 🟣 LAST, after all sky/light/cloud/atmosphere work above is gated (user request 2026-06-21).**
   A dedicated pass over the *whole* lighting + cloud + sun + atmosphere lane for **code/compute efficiency — NOT visual
   tuning** (the user's words: "i dont want to tune, i want to look at code efficiency"). Scope: profile the real GPU/CPU
   costs (atmosphere LUTs + per-frame aerial 32³ recompute + AT-3 cloud-lighting + cloud raymarch + god rays + shadow
   maps), then attack redundant per-frame work, recompute-cadence (recompute only on real change; amortize/temporal where
   safe), dead/duplicated shader math, oversized textures/dispatches, the `Std430` buffer churn, and the CallOnRenderThread
   seams. Output: measured before/after ms per subsystem against the frame budget. No look changes — same image, fewer ms.
Tracked so nothing is lost. Each is a fix, not a feature; sequenced cheap → structural. Most are
default-safe and land alongside the lane work. Verdicts were adversarially verified in the audit.

**Done / now (cheap correctness + doc reconciles):**
- ✅ **Pushed** `experiment/presentation` + 6 backup tags to origin (2026-06-21) — the day's sky lane is now off-machine.
- ✅ **Doc reconcile** (2026-06-21): this ROADMAP, HANDOFF §6, NEEDS_REVIEW, performance.md, the ground master
  design + DECISIONS updated to match reality (the audit found HANDOFF "PAUSED" + performance.md proxy-defaults stale).
- ✅ **Sky-color ownership** (RESOLVED 2026-06-21 — was filed "BAD", investigation downgraded it to a *documentation*
  trap, not a bug). Tracing `background()` in `cloud_sky.gdshader`: the keyframed gradient is `mix(atmo, grad,
  night_factor)`, so `SetSkyColors` is **load-bearing for the NIGHT sky** (atmosphere bows out as night falls) and the
  atmosphere-off fallback — NOT redundant; "stop pushing it" would break night. The `ProceduralSkyMaterial` write is the
  **startup / no-CloudVolume fallback** (the cloud sky only installs once `_computeReady`), not dead. So no code behavior
  change: documented the ownership law in `ComposeLighting` (atmosphere LUT owns the DAYTIME sky/cloud-ambient; day_script
  daytime palette is by-design superseded — grade the day sky via `sky_tint`/atmo exposure). Day_script-as-grade-over-LUT
  is a *future look feature* (needs its own eye-gate), not a debt fix.
- **Measure AT-2 + re-decompose the frame:** performance.md's 9.6 ms predates AT-1/AT-2 and assumed proxy/SDFGI
  ON (they're OFF since 0b). Profile AT-2 in motion (`--profmove`) and write a current "perf state of record."
- **`copy_materials.py`** regenerates all 738 (not the 108) + hardcoded `D:\assets`, silent no-op if absent —
  filter to the library, parameterize the root, fail loud.
- **`FieldParams.Load`** throws on any missing/renamed key (unlike the graceful loaders) — guard it.

**Structural (before / alongside Phase B):**
- **Extract `LightingComposer` + `SkySubsystems` facade out of `TerrainLabUI`** (2599-line god-object, ~245
  fields, 18 partials) and a **`SkyMaterial` facade out of `CloudVolume`** (691 lines, ~40% sky pass-through).
  The unit that will otherwise resist the Phase-B "make it a world" refactor.
- **N-suns / N-moons (Celestial C3) needs a luminary-abstraction refactor** of `ComposeLighting` +
  `cloud_sky.gdshader` (single-luminary is baked deep) — budget it as a refactor, not an additive unit.
- **Dead-code sweep:** `project.godot` boots the frozen `lab.tscn` not the active scene; retire
  `godray_test.tscn` / `lab_experiment.tscn` / the dormant occ_mode-3 god-ray path; cache the ~37 per-frame
  `GetNode("/root/...")` string-walks.
- **Cloud detail-erosion divergence** (raymarch erodes edges harder than the shadow/check shaders; `--shadowcheck`
  is blind to it) — owed its own gate; unify or make the check see it.
- **No headless correctness gate** (`FieldCompute` NullRefs under `--headless`) — guard it; add a CI lint for
  registry `param`↔shader-uniform + scene/cloud target↔C#-case so a dead control fails fast.

## 🌍 Phase B — make it a WORLD (after Phase A is done)
Gated by the scale infra; hybrid build (spine first, content designed region-first + stream-aware).

### 🏔️ TERRAIN LOD (T1–T3) — the foundational spine
**Status 2026-06-21:** T1 = pop-free continuous LOD on the fixed 8 km region. **S1 (quadtree skeleton) ✅
DONE; S2a (chunks render) ✅ DONE (5.6 ms, 4.4× under budget). S2b (geomorph + pop-free verification) 🔨
IN PROGRESS.** Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`; current design
`specs/2026-06-21-s2b-geomorph-design.md`; implementation plan `plans/2026-06-21-s2b-geomorph.md`.

**T1 arc (pop-free proof):** The graveyard gate — WG1–15 all died at terrain LOD pops. S2b adds per-vertex
geomorph (each vertex morphs its grid position toward the coarser LOD via its own camera-distance factor,
sampling the SAME field at the morphed XZ → elevation pop is structurally impossible) + a reusable
LOD-crossing test harness (3 scripted camera paths: low-fast horizontal, vertical altitude drop, slow
boundary isolation; keys + `--testpath=N`; per-path perf + invariant-checked-along-path). **The eye-gate:**
the user flies the 3 paths in motion and confirms ZERO elevation pops, ZERO cracks, ZERO quality pops
across LOD bands. **That is T1's definition of done.** Chunking, streaming, tiles are T2–T3; they cannot
proceed until T1 proves pop-free.

**T2 & T3 (after T1 eye-gate):**
- **T2 — stable world tiles.** Chunk the 8 km fixed region into world-XZ tiles (the unit splat/breakup/
  erosion need), per-tile LOD select, edge stitch, frustum culling. Re-point per-region bakes to per-tile.
- **T3 — streaming.** Load/unload tiles around the camera → true-infinite. Foundation for erosion E4,
  world-editing, flora LOD.

**Other Phase-B systems (dependent on T1–T3 spine):**
- **Biomes** — climate field (moisture/temperature) selects per-region material palettes + rules +
  flora; the ground palette/rule system is already data-driven for this.
- **Macro procedural variety** — regions genuinely differ (not one mountain tiled).
- **Erosion at scale** — E2 drainage-skeleton bake → E3 per-chunk procedural detail → E4 coarse
  global pre-solve; consumes the Phase-A hydrology model. `specs/2026-06-17-erosion-arc-design.md`.
- **Flora integration** (parallel chat returns grass/trees/shrubs) + **world-editing integration**
  (brush + height-delta + undo) — wire providers; edits invalidate splat/breakup/scatter → re-bake.

- **Banked (user-flagged 2026-06-20, Sun/Light lane):** the cloud field reads as a **camera-anchored
    "huge dome"** at far/down extreme-distance views (clouds line up / repeat far out). Fine on-chunk;
    revisit for the infinite world (the cloud shell + cirrus altitude-plane both anchor to the camera).
- **Biomes** — climate field (moisture/temperature) selects per-region material palettes + rules +
  flora; the ground palette/rule system is already data-driven for this.
- **Macro procedural variety** — regions genuinely differ (not one mountain tiled).
- **Erosion at scale** — E2 drainage-skeleton bake → E3 per-chunk procedural detail → E4 coarse
  global pre-solve; consumes the Phase-A hydrology model. `specs/2026-06-17-erosion-arc-design.md`.
- **Flora integration** (parallel chat returns grass/trees/shrubs) + **world-editing integration**
  (brush + height-delta + undo) — wire providers; edits invalidate splat/breakup/scatter → re-bake.

## 🌧 Phase C — climate & elements
- **Visible water** — rivers/lakes/ocean rendering, consuming the Phase-A hydrology flow field.
- **Precipitation** — rain/snowfall on the Weather axis (currently a "future hook" only) + the
  ground **wetness** response (Unit 6 hooks) + dynamic weather.
- **Snow-on-ground** — aspect/temperature placement (partly in ground G3).

## ✅ Done & settled (approved — don't redo)
- **Base field** — WG15 5-layer GPU heightfield, no bake. PROVEN; don't touch its math. (Design:
  `archive/specs/2026-06-15-wg16-base-field-design.md`.)
- **Lighting base + 6 moods** — "really good" (now decoupled, re-gating as Stage 2).
- **Materials** — 738 → 108 accepted.
- **Ground** — Unit 1 anti-repetition; compositing-core Phase A weight-blend + AO; G1 placement. All APPROVED.
- **Clouds** — volumetric + multi-layer, accepted (broad review owed, gate 5).
- **Perf — GI/shadow proxy** (512²), default on, user-verified. Details: `performance.md`.
- **God rays** — screen-space radial scatter, shipped (final pass owed, gate 3).
- **Infra** — `Std430Writer`, `CallOnRenderThread` compute→material bridge, the lab + `FLAT
  BASELINE` harness, `--auto-shot`/`--profmove`/`--shadowcheck`.

## 📌 Conventions
- `main` = clean baseline · `experiment/presentation` = HEAD (work here). Git is the undo. Restore
  tag: `backup-doc-reset-2026-06-20`.
- Verify: build → headless `--import` → `--auto-shot` A/B + `--profmove`, then the USER's live eye
  (no TDD; GPU/visual). Never debug a motion artifact from a still.
- Launch (one Godot at a time): `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan
  scenes/terrain_lab.tscn`. Kill strays first.
- Gotchas (full list in `HANDOFF.md` + memory): ONE Godot at a time; always `--rendering-driver
  vulkan`; local-RD compute can't run `--headless`; per-frame compute→material via
  `CallOnRenderThread` (not CompositorEffect), assign `Texture2Drd` RID once; hand-packed std430
  drifts → use `Std430Writer`.
