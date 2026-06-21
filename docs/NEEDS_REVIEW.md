# WG16 — NEEDS REVIEW (the live eye-gate queue)

One place for everything **built but awaiting the user's live eye** — because the gate for look/feel
is always the user flying it, never a still or a mechanical check (project rule). Work the list when
you're back at a screen. Each item: **how to see it · what to judge · what it unblocks.**

How to run (windowed, one Godot at a time):
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`
Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. Judge **in motion**, and at
**close / mid / far** for ground items. `FLAT BASELINE` (top of panel) + per-item toggles isolate things.

### ▶ FAST PATH — the review scene (`scenes/review.tscn`)
A copy of the terrain lab wired with **number-key presets** that jump straight to each gate item from
the approved baseline, with an on-screen "what to judge" banner (the full lab UI is still there for
manual tuning). Press a key, fly, judge, move on:

| Key | Item | NEEDS_REVIEW § |
|----|------|----|
| **1** | Sun disc (Stage 1) | 3b |
| **2** | Time-of-day / daylight (Stage 2) | 3c |
| **3** | GM1 palette (press 3 again to A/B palettes) | 1c |
| **4** | GM2 real height + POM | 1c |
| **5** | GM3-A within-area variation | 1c |
| **6** | Clouds types — press 6 to cycle cumulus(profile off→on)→stratus→cirrus | 9 · 9b |
| **7** | Fantasy / exotic sky (press 7 to cycle presets) — ST4-2 | — |
| **8** | GPU atmosphere (AT-1) A/B — physical sky vs keyframed (Light-tab toggle + exposure). *Repurposed: GI A/B still via the Light-tab `GI (SDFGI)` toggle (0b RESOLVED).* | AT-1 |
| **9** | BRDF / approved baseline (clouds off) | 6 |

**Flow:** press a number → read the on-screen banner (what to judge) → fly **close / mid / far**, **in
motion** → form a verdict → record it (below + a `DECISIONS.md` line) → next number. The full lab UI is
still there for manual tuning, and `FLAT BASELINE` (Debug) isolates any contributor.

**Caveats (by design):** close-up presets (3/4/5/8) drop you at ~140 m looking down — **fly the last bit
to a real cliff/slope**, the preset only sets the toggles. Preset **8** (GI decision, now RESOLVED) sets the approved off+off
baseline — toggle `GI (SDFGI)` on the **Light** tab (NOT Debug) and `GI/shadow proxy` on the Debug tab to A/B. **AA-in-motion (item 7 in the gate order) is NOT
wired** in the lab — judge it separately (see `performance.md`). Presets are gated on `ReviewMode`, so
`terrain_lab.tscn` is unaffected.

Last updated: 2026-06-21 (added 1e ground "wrong-defaults" gate + 11 AT-1/AT-2 atmosphere — both from the
2026-06-21 audit + ground review; see `docs/AUDIT-2026-06-21.md` and ROADMAP).

---

## 🟥 P1 — blocks the active arc

### 0. Compositing core — eye-gated 2026-06-19 (plan `plans/2026-06-19-terrain-compositing-core.md`)
- **Phase A (Blend quality) ✅ APPROVED + SHIPPED (default).** User flying it: "weightmap is good! it
  actually looks good." Bake emits 7 smooth role weights → two linear Rgba8 weightmaps; fragment picks
  **top-2** roles, blends by a derived **material-height interlock** + organic breakup. `splat_blend_mode`
  **default now 1 (weightmap)**; legacy index path kept behind mode 0 for A/B. Knobs (Splat tab):
  `interlock sharpness/height drive/breakup`, `boundary warp (m)`. The de-block is the win; it does NOT add
  more material *variety* per area (that's placement/roles — G3/Unit 4, a separate lever).
- **AO ✅ kept.** Bound into the custom BRDF (`ao_on`, default on). User: "nice little detail, could do
  more" — fine as-is; revisit strength later.
- **Phase B POM ⏸ DEFERRED (built, default OFF).** User flying it: relief only "raises elevation a little"
  even tuned. Root cause: derived inverted-roughness height is nearly flat on this library (no height maps).
  POM is binary — it only reads with **real height maps** (spec ceiling seam #1). Decision (user): if we do
  height maps + POM later, **defer** now (seam kept, zero runtime cost). → see ROADMAP "height maps" arc.

### 0b. GI/SDFGI — ✅ RESOLVED 2026-06-20 (eye-gate, Sun/Light lane): default SDFGI OFF + GI proxy OFF
- **Verdict (user, live, review key 8):** SDFGI's camera-centered cascade renders a hard-edged bright
  box on the terrain that **re-centers on the camera as you fly** ("a light that gets brighter as you
  get closer"). Toggling SDFGI **off** removes it with no visible loss — confirms the 0b finding live.
  **Decision: default SDFGI off + GI proxy off** (sharp detail-mesh shadows, no cascade box, ~4.7 ms —
  also ~0.5 ms cheaper than the old sdfgi-on+proxy-on default). **Parked, not purged:** toggles kept
  (`GI (SDFGI)` on the **Light** tab; `GI/shadow proxy (perf)` on the Debug tab) — revive GI when flora
  / erosion canyons / night+moonlight add geometry that actually occludes & bounces. Defaults flipped in
  `lab_controls.json` (`sdfgi_on`/`gi_proxy` → false), `TerrainLab.cs` (`UseGiProxy=false`), both scenes
  (`sdfgi_enabled=false`). Follow-up: a **Shadow & Lighting roadmap stage** (see ROADMAP, Sun/Light lane)
  to take the current sharp-shadow setup to AAA (CSM tuning, contact/soft shadows, the proxy-on perf lever
  ~2.8 ms, SSIL re-check). NOTE: the SDFGI toggle is on the **Light** tab, not Debug (banner/table fixed).
- **(Investigation 2026-06-19, retained):** Another chat owned light/sun (scene WorldEnvironment) — this
  was the investigation result handed to this lane to act on (now acted on, above).
- **Finding (objective, `--sdfgi=`/`--giproxy=` A/B + pixel-diff, cam 0,120,0,-22,0, clouds off):**
  toggling SDFGI changes the rendered image by **0.002–0.003 / 255** (invisible) — confirmed the user's
  "does nothing." It is **NOT misconfigured/broken:** tested SDFGI on the full detail mesh (proxy off) →
  still ~0.003; and a custom `light()` does not block engine GI. It's **functioning but redundant on
  smooth open sky-lit terrain** (nothing to bounce/occlude; sky-ambient+SSAO already cover it).
- **Cost (in-motion `--profmove`, clouds off):** SDFGI on+proxy on **5.2 ms** · SDFGI **off**+proxy on
  **2.8 ms** (−2.4 ms) · SDFGI off+proxy **off** (sharp detail-mesh shadows) **4.7 ms**. So SDFGI costs
  ~2.4 ms for zero visible benefit *now*.
- **Recommendation (PARK, don't purge):** default SDFGI off + GI proxy off (sharp shadows, no blocky
  patches, ~4.7 ms, fully reversible toggles) → big step toward the 8 ms target with no visible loss.
  **Re-evaluate GI when flora / erosion canyons / day-night+moonlight land** — those add the occluding/
  enclosed geometry that makes GI earn its cost. Don't delete it.

### 1. Ground G1 — rule-based placement engine ✅ APPROVED 2026-06-19
- **Verdict (user, live):** "rule placement does work, it's basic, will want a lot more in the future but this proves the basics work." Gate PASSED.

### 1b. Ground Unit 4 — procedural breakup ❌ FAILED eye-gate 2026-06-20 → PARKED (not in the review queue)
- **Verdict (user, live):** hard square edges + "barely does anything." Built T1–T6, root-caused, **parked** (`breakup_on` default off, `3077abe`). NOT awaiting re-review — it's the wrong tool for "variety within an area" (masks are f(heightfield)). Infra kept for GM4 (revive after erosion). See DECISIONS 2026-06-20.
- **⮕ Active ground direction RE-SEQUENCED (2026-06-20):** material foundation first. Authoritative roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`.

### ⚑ 1c. GROUND BATCH — GM1 + GM2 + GM3-A — ONE combined eye-gate (built 2026-06-20; ground lane PAUSED here)
**All three are BUILT + committed but NONE is user-approved** — three phases past the "approve before the next
phase" rule. **The ground lane is PAUSED for a project-wide re-roadmap once this batch is gated.** Defaults now
reproduce the **approved-era look** (palette `alpine_green` = prior set; GM3-A variation OFF; GM2 height OFF), so
every new feature is **opt-in** and you A/B from a known baseline. **Gate them TOGETHER** (they share the surface):
fly **close/mid** on a varied region (cliffs/peaks too, not just the warm basin) under a **midday/neutral mood**.

> **⚠ GATE IN PROGRESS 2026-06-20 — SURFACE BLOCKER FOUND → anti-tiling FIXED + PASSED.** Key 3 (GM1) judged live:
> **textures OK, the shared ground SURFACE fails** — hard-edged rectangular **tile-chunk seams** + abrupt
> color/value steps (worst close), visible **repetition**, **speckle**. Root-caused live: (1) **macro color**
> (fixed, default off) + (2) **anti-tiling seams** = the per-tile-rotation bombing on albedo. **FIX SHIPPED +
> PASSED (2026-06-20, live, Shift+3 A/B): AAA histogram-preserving tiling** (`tile_mode=3`, now default) — user:
> *"the new system is good!"* (spec/plan `2026-06-20-ground-anti-tiling-histogram-preserving*`). **NEXT: re-judge
> GM2 (key 4) + GM3-A (key 5) on the now-fixed surface.** Batch gate still open on those two. See DECISIONS 2026-06-20.

> **⮕ SUPERSEDED 2026-06-20 — ground lane RESET to a from-scratch redesign.** The GM2/GM3-A re-judge surfaced
> *more* close-up artifacting; user called it: stop patching a never-holistically-designed core. New authority:
> **`specs/2026-06-20-ground-rendering-system-master-design.md`** (game-agnostic rendering SYSTEM, Skyrim/NMS bar;
> keepers = base field + anti-tiling + placement concept; foundation-first). The GM2/GM3-A built work is
> **reabsorbed** into the redesign's Phase G-2/G-3 (re-judged/rebuilt there), NOT separately gated here. See §1d.

### ⚑ 1d. GROUND G-1 — Compositing core (half-float weights) — AWAITING eye-gate (built 2026-06-20)
First phase of the ground redesign — harden the core everything renders through. **The close-up
blocky/stair-stepped/smeary blend was ROOT-CAUSED** (live isolation): the splat **blend weight**, because the
weightmaps were **8-bit at 4 m/texel** and the interlock used that quantized low-res weight as the boundary
threshold. **T1 BUILT (`eb4733d`):** weightmaps now baked **half-float (`Rgbah`)** → de-quantizes the blend in
the **default** path (no toggle; placement byte-stable). Spec/plan `2026-06-20-ground-g1-compositing-core*`.
- **How to see:** press `3`, fly **close to a material transition** (where the blocky was); **Splat tab →
  `splat debug = mix amt`**.
- **Judge:** is the **stair-stepping gone** (that was the 8-bit quantization)? Placement unchanged?
- **Unblocks / next:** if the residual **4 m softness/smear** still reads, build **T3 = `hq_blend`**
  (fragment-resolution AA interlock — boundary from full-res height + noise, weight as bias only; written in the
  plan, NOT built). PASS → record + **Phase G-2 (material data + surface depth / real height → 3D surfaces)**.
- **T2 (sampling mip/aniso) ✅ verified clean** (all material samplers `mipmap_anisotropic`, no change).

- **GM1 — data-driven palette** (`61dd605..90c39ef`, `4108ad2`+fix): `data/ground_palette.json` loads on startup,
  warn-on-miss, per-role **Zones-tab dropdowns**. Default `active=alpine_green` (prior look). **Judge:** switch
  `active` to **`alpine_stone`** (the curated candidate) or `arid`, and/or tune per-role dropdowns at close/mid
  under neutral light — isolate whether "drab" is lighting vs material saturation vs placement. Plan
  `plans/2026-06-20-ground-gm1-curated-palette.md` T4.
  - **⊘ Verdict (2026-06-20, user, live, review key 3, cycled alpine_green→alpine_stone→arid, midday-neutral):**
    "textures are OK, implementation/everything around them sucks — artifacting, clear tile-to-tile chunk lines,
    weird color differences." **Palette deliverable acceptable (not drab); the surface compositing is the blocker**
    (tile-chunk seams, color steps, repetition, speckle — see the SURFACE BLOCKER banner above). Leading unconfirmed
    **ROOT-CAUSED** (live isolation + code): two culprits — (1) **IQ 2-tap anti-tiling** (`tile_mode=1`) blend factor
    is constant per ~28 m tile cell → **blocky stair-step seams** (feeds albedo + interlock `mix amt`); `tile mode=none`
    removes them but brings back repetition. (2) **macro color** (`macro_on`) = the soft "weird color differences"
    blotches (user: off "fixed a lot of artifacting"). Residual = true material transition borders. **Fix = non-blocking
    anti-tiling + macro rework, design-first/eye-gated.** GM1 not separately approvable until the surface holds.
- **GM2 — real per-material height** (`39058ab`,`b907b77`): Poisson normal→height bake (verified: genuine
  basalt-column / talus-cobble relief), behind **`real height (GM2)`** (Detail tab, default OFF). **Judge:**
  toggle it (interlock uses real height), then flip **`surface depth (POM)`** — POM's revival gate (was "too flat";
  now has real height). Real crevice/relief depth, no swimming/artifacts? Forced-on 5.1 ms (in budget). Tunables
  (TerrainLab): `HeightIters`/`HeightAmp`/`HeightFlipY`/`HeightInvert`. Spec `specs/2026-06-20-ground-gm2-real-height-maps-design.md`.
- **GM3-A — within-area variation** (`82f60fb`,`4108ad2`): fragment-side multi-scale world-pos noise →
  roughness/relief/value of the surface (Unit-4 done right; free). Behind **`within-area variation`** (Color tab,
  default OFF). **Judge (uniform slope, close/mid):** toggle on — does it stop reading uniform (drier-lighter-rougher
  vs damper-darker-smoother patches, organic, no squares, no shimmer)? Tune `var roughness` first. **Verdict gates
  GM3 Approach B/C** (true different-material patches via texture arrays). Plan `plans/2026-06-20-ground-gm3a-within-area-variation.md` T3.

### ⚑ 1e. GROUND G-0 — the "wrong-defaults" eye-gate (NEXT for ground; from the 2026-06-21 review) — ✅ WIRED 2026-06-21
The 2026-06-21 ground review found the "drab/flat" look is largely a cluster of **suppressed-good-tech defaults**,
not a rotten foundation (verdict: **ITERATE, don't rebuild** — ROADMAP "Ground / Texture"). The cluster is now flippable
as ONE before/after, defaulting to the current look — A/B it live to see how much "drab" is recoverable today.
- **See it:** `review.tscn` → **press ⇧1 (Shift+1)** to toggle the whole cluster CURRENT↔FIXED (banner shows which);
  fly **close/mid in motion**, toggle ⇧1 to A/B. (`--greview=1` drives the FIXED state at startup for an auto-shot.)
  Mechanical check 2026-06-21: FIXED renders distinctly (warmer alpine_stone palette + finer texture + variation), no no-op.
- **The flips (all default-current):** `rough_floor` 0.5→0.15 (**new Surface-tab slider** — was pinned 0.5 with no UI
  control, forcing the ground dead-matte: `terrain_lab.gdshader:103,987`); `tex_scale_m` 28→11 (un-stretch ~3×);
  `height_from_maps` on (GM2 real Poisson relief); `within-area variation` on (GM3-A); palette → `alpine_stone`.
- **Judge (close/mid, in motion, under SETTLED light — gate the atmosphere first):** does the ground gain specular/
  sheen + relief + color variety + crisper texture? Or does unclamping roughness reintroduce specular shimmer/fuzz
  (the floor was hiding it)? If shimmer appears, that's the bisect target (`dbg_fullrough`/`dbg_use_normalmap`), not a
  reason to re-clamp.
- **Unblocks:** the G-1 currency/finish + G-2 asset/height work (ROADMAP). **Supersedes the §1c/§1d GM-batch framing.**

## 🟧 P2 — perf default needs a fidelity confirm

### 2. GI/shadow proxy — ✅ RESOLVED 2026-06-19 (default 512²)
- **What happened:** the original 256² proxy produced a big SDFGI dark-blob on steep ground (its ~32 m cells deviated from the real terrain → false GI occlusion). Bumped resolution → **512² (~260k verts) is blob-free (user-verified)** at ~8.4 ms in-motion; 1024² also clean but ~2 ms costlier; 256² blobbed. Default set to **512²**, tunable via `--proxyres=N`.
- **Net:** in-motion 1440p clouds-off 55→119 fps. Toggle `GI/shadow proxy (perf)` (Debug) / `--giproxy=0/1`.
- **Still inherent (NOT the proxy):** SDFGI's camera-centered cascades make a faint lighting shift "follow the camera" — that's SDFGI itself, on the perf-lever list (SDFGI config / cheaper GI), separate from this.

## 🟨 P3 — built earlier, eye-gate still owed

### 3. God rays — ✅ PASS 2026-06-20 (eye-gate, live, review key 7)
- **Verdict (user, live):** "all good." Screen-space sun-through-cloud shafts read believable. Gate PASSED.
- **Status (orig):** mostly finished (screen-space rework, in another chat). Needs a final visual pass.
- **See it:** Clouds tab → **`god rays`** toggle (or `--godrays=1`), clouds on, sun toward camera through cloud gaps.
- **Judge:** Do the shafts read as believable sun-through-cloud light (crisp where wanted, not uniform fog, not washing the scene)? Tune to taste with the god-ray chat's knobs.
- **Note:** god-ray files are owned by the other chat — coordinate; don't edit `GodRays*`/`shaders/godray*` from this thread.

### 3b-surface. Sun SURFACE shader + presets — ✅ PASS (reworked) 2026-06-20 (eye-gate, live)
- **v1 FAILED** (user: "the weird suns don't look good, didn't carry"). Root-caused via auto-shot
  iteration: disc sub-degree (surface too small), nuclear core clipped texture to white, full-color
  override = flat blob, and my cloud fix had stopped cloud_sky installing when clouds start off.
- **Rework (`c406e12`):** sphere foreshorten + de-foreshortened UVs, rim-faded granulation, carving
  sunspots, lower core multiplier in surface mode (texture survives tonemap), warm chroma, color
  override = strong-tint+dim; cloud_sky now installs even clouds-off; `sun_size` cap 4→8; presets
  re-sized visible. **Verdict (user, live, full cycle): "looks good enough to me."** PASS.
- **Believable presets (realistic_midday/golden_hour/living_star) read as textured warm suns.** Colored
  fantasy (blood_sun/alien) tint but bleach pale at the bright core (engine glow/AgX) — accepted as-is;
  can be dimmed for deep saturation if wanted later.
- **Default (user):** **ON = `realistic_midday`** (subtle granulation + faint spots), since perf is
  effectively free (surface math gated to disc pixels; the per-pixel cloud fetch replaced the old centre
  fetch 1:1). The JSON `active` preset now applies at startup; pick `neutral` for the old flat disc.
  Commits `c406e12`,`04a3f62`,`756ab7e`.
- **Also fixed during gate:** large discs were fully hidden when a cloud touched their centre →
  cloud occlusion is now **per-pixel** (`04a3f62`). Cloud error-burst fix confirmed holding (0
  mid-session errors). Colored fantasy (blood/alien) still bleach at the bright core — opt-in, can be
  dimmed for deep saturation later if wanted.

### 3b. Sun disc polish (Stage 1) — ✅ PASS 2026-06-20 (eye-gate, live), polish note owed
- **Verdict (user, live, review key 1):** "pretty good… maybe a little bit more — it's basically just a
  circle still, but it does other stuff." The halo / corona / horizon-reddening read well; the **disc
  itself reads flat** (a bright circle). PASS — unblocks Stage 2 (already passed) + Stage 3. **Open polish
  (small, Stage-1):** give the disc more presence (limb-darkening gradient so it's not a flat sticker,
  slightly larger / stronger corona bleed) — live-tunable via the Light-tab `sun *` knobs; bake the
  approved values as defaults.
- **Status (orig):** built + cherry-picked onto `experiment/presentation` (`b9cf52d..6a08f93`), builds clean. The flat
  white sun dot is now `sun_layers(rd, aSun)` in `cloud_sky.gdshader`: limb-darkened disc + corona + warm halo
  + horizon reddening/growth + soft optical-depth cloud occlusion. Spec/plan `docs/superpowers/{specs,plans}/2026-06-19-sun-disc-polish*`.
- **See it:** `--preset=8` ("Golden Hour Sun") `--godrays=0 --lookatsun` for the warm low sun; or any mood +
  raise/lower **Light tab → `sun height`** to swing high(neutral white)↔low(warm/red + grown). Drift a cumulus
  across the sun (or raise coverage) for the dim+redden cloud occlusion + halo bleed.
- **Judge:** Does it read as a believable glowing sun (limb-darkened disc, tight corona, soft warm halo) vs a
  flat circle? Does it redden + grow at the horizon and stay neutral-white high? Does cloud dim AND warm it
  softly (halo bleeding around the edge)? No banding / hard ring at the disc/halo edge.
- **Knobs (Light tab):** `sun size (deg)` (the VISIBLE disc — the old `sun size` is now "shadow penumbra"),
  `sun limb darken`, `sun corona size/energy`, `sun halo size/energy`, `sun horizon redden`, `redden onset`,
  `sun horizon grow`, `sun cloud redden`.
- **Built via:** subagent-driven-development (6 tasks, each spec+quality reviewed; opus whole-branch review = "ready
  to merge, no Critical/Important"). All rendering lives in `sun_layers()` inside `sky()`; `sun_disc_energy` stays
  decoupled from `LIGHT0_ENERGY` (overcast dims the directional light, not the visible disc); the halo is kept LOCAL
  to the sun cone (no broad-sky recolor — that's deliberately Stage 2). Elevation sign verified `+LIGHT0_DIRECTION.y`.
- **Pre-checked (controller, stills only — NOT a substitute for your live eye):** high sun (mood 2) = neutral-white
  soft disc + gentle halo, not over-bloomed; low sun (mood 0) = warm orange halo + reddening; Golden Hour preset =
  warm low sun with a lit cloud band. Build clean.
- **Watch for (known soft spots to judge):** (1) the default `sun size (deg)` = 0.6 renders a SMALL disc (limb
  darkening is sub-pixel at default) — decide if the default disc should be bigger. (2) The Golden Hour preset sun
  sits very low (8°) so its disc can read subtle / partly behind the cloud band — also swing a clear-sky low mood to
  see the bare warm disc. (3) `sun horizon redden` > 1 intentionally pushes the tint past the target color (a
  strength boost, not a bug). (4) cloud occlusion needs a cumulus actually crossing the sun — raise coverage (~0.8)
  if the sun sits in a gap.
- **Unblocks:** Stage 2 (time-of-day driver) — being designed now in parallel (`specs/2026-06-20-sun-light-system-architecture.md`).

### 3c. Lighting decouple + time-of-day driver (Stage 2 daylight) — ✅ PASS 2026-06-20 (eye-gate, live)
- **Verdict (user, live, review key 2):** "time of day is good — we just need the hours missing, and a
  moon, but that all comes later." Daylight arc + cohesive sky/light shift PASS. The **missing night
  hours + moon = Stage 3** (below-horizon goes ~dark; exact darkness is a Stage 3 design choice). Unblocks
  Stage 3. **Correctness follow-up (not a look-gate):** pressing key 2 once threw a burst of
  `Texture ... is not a valid texture` / `Parameter "us" is null` (RenderingDevice uniform-set) errors —
  likely cloud/compute re-bake on mood+time re-apply; process exited clean (not a crash). Investigate
  whether the Stage-2 re-apply path transiently unbinds a compute texture.
- **Status (orig):** on `experiment/presentation` (`fcb0b43..12cbf3a`), builds clean. The bundled "mood" is split into
  Time/Weather/Grade(+SunDisc) state behind one writer (`ComposeLighting`, which also absorbed the per-frame
  `UpdateOvercast` — no more competing writers). A `time_of_day` knob drives the sun along an analytic arc +
  a keyframed daytime color script (sky/sun-color/ambient). **GPU atmosphere deferred** to a future stage.
- **See it:** **Light tab → `time of day (h)`** slider (or `--time=7/12/17`). Also `sunrise/sunset/sun noon
  height`. Watch the sun travel low-E → high → low-W and the palette shift **cool dawn → bright noon → warm
  dusk**. The 6 moods (mood dropdown) must still look like before (they reproduce via `MoodToStates`).
- **Judge:** Does scrubbing time read as a believable day (sun arc + cohesive sky/light/ambient shift, no
  pops)? Do the 6 moods still match their old looks? Does overcast (raise cloud coverage) still dim the sun
  correctly? It's a **keyframed** color script, not physical atmosphere — judge "good enough day," not Rayleigh.
- **Unblocks:** the project-wide re-roadmap (user pausing the sun/light lane here) + the future GPU-atmosphere
  stage. Spec/plan `2026-06-20-lighting-decouple-and-time-axis*`.

### 4. Ground Unit 2 — distance detail (BUILT, default OFF, SHELVED)
- **See it:** Detail tab → **`distance detail`** + `detail amt/scale/normal/fade dist` (or `--detail=1`). **Fly down CLOSE** — it's a ~250 m near band; does nothing from altitude.
- **Judge:** Up close, real micro-detail vs flat? Any **swimming/crawling** of the derived normal in motion, or a fade **pop** at mid range? Far should be unchanged.
- **Unblocks:** only worth judging **after G1+G2 make the base good** — it's polish on the foundation. Plan `plans/2026-06-17-ground-unit2-distance-detail.md`.

### 5. Clouds — ✅ PASS 2026-06-20 (eye-gate, live, review key 6)
- **Verdict (user, live):** "all good." Cloud shape/lighting/motion read good across the Clouds-tab features. Gate PASSED (cloud system signed off).
- **See it:** Clouds tab (coverage/density/type/decks/presets/temporal/etc.), `--clouds=1`, `--preset=N`.
- **Judge:** the per-feature checklist in `docs/cloud-next-steps.md` (+ `cloud-system-overview.md`). Distant-sky/horizon handling + sun-disc polish are the known soft spots.
- **Unblocks:** signs off the cloud system as "reviewed good."

### 6. H1 — BRDF regression check — ✅ PASS 2026-06-20 (eye-gate, live, review key 9)
- **Verdict (user, live):** "all good." Custom `light()` (Burley+GGX + cloud-shadow hook) shows no regression on clouds-off terrain. Gate PASSED.
- **See it:** clouds OFF terrain. Compare against the approved look.
- **Judge:** the custom `light()` (Burley+GGX replica + cloud-shadow hook) shows no regression vs the engine default it replaced (the perf pass touched this shader — all bit-near-identical, but an eye-confirm is owed).

### 7. AA in motion
- **See it:** currently MSAA 2×. Compare MSAA off / FXAA / TAA.
- **Judge:** crawl/shimmer on terrain edges + cloud ghosting (TAA) vs the perf cost. Pick the AA. See `docs/performance.md`.

### 8. Stage 3 (Night & Celestial) — built+gated, two UI bits await your eye 2026-06-20
Stage 3 is COMPLETE and 3a–3d all PASSED live. Two later additions were built + self-checked (auto-shots)
but haven't had your live eye — confirm them whenever:
- **See it:** `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --nightgate=1 --time=23` → open the **`Night` tab**.
- **Celestial preset dropdown** ("CELESTIAL PRESET" at the top of the Night tab): try `full_moon_clear`,
  `new_moon_dark`, `crescent`, `bright_moonlit`, `deep_scary`, `exotic` (amber moon). Each should give a
  cohesive night look; `--celestial=N` is the CLI equivalent. **Judge:** do the 6 presets read distinct + believable?
- **Live color pickers** (`moon color`, `moonlight color` on the Night tab): drag them — moon disc tint +
  cool/warm ground moonlight should change live. **Judge:** colors apply cleanly, no lag/glitch.
- Unblocks: nothing (Stage 3 already gated) — this is just confirming the preset/color UX. Self-checked
  `exotic` renders (amber moon); startup is clean (no add_child spam after the MoonLight deferred-add fix).

### 9. Clouds #2 · CO-1 vertical realism — ✅ PASS 2026-06-20 (eye-gate, live)
- **Verdict (user, live, review key 6):** cumulus vertical profile "fine". Gate PASSED. Kept **opt-in (toggle
  default-off)** so the approved cloud look is unchanged; default-on is a later call. Knobs/`--cloudprofile` live.

### 9-orig. Clouds #2 · CO-1 vertical realism (detail) — built default-off
The first sub-phase of the Clouds overhaul: a per-deck **vertical density profile** so cloud decks read as 3D
volumes (flat-ish base → faded/anvil top) instead of flat slabs. **Built + mechanically verified, NOT eye-gated**
(user couldn't view). **CO-2 (item 9b) was then built ahead at user direction** — both await your eye; everything
is default-off so the approved look is untouched.
- **See it:** `"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn -- --clouds=1 --coverage=0.5`
  → **Clouds tab**, toggle **`vertical profile (CO-1)`** for an instant A/B (OFF = current slab look, ON = profile).
  Or launch straight into a strong example: append `--cloudprofile=0.2,0.5,0.8` (bottom,top,anvil). **Fly UNDER and
  up at a deck** — the slab-vs-volume read is clearest looking toward the cloud base, not down from altitude.
- **Knobs (Clouds tab):** `profile: base round` (flat-base round-up height), `profile: top fade` (where the top
  tapers), `profile: anvil (Cb)` (0 cumulus taper → spreading cumulonimbus top). Defaults 0.15 / 0.6 / 0.
- **Judge:** (1) ON → decks read as 3D volumes (flat base, rounded/anvil top), not slabs? (2) OFF → the approved
  cumulus look reproduces exactly? (3) any motion shimmer or horizon/grazing-angle artifact? (4) cost OK in motion.
- **Note:** enabling the profile thins clouds (it only ever removes density) — raise the **density** knob to
  compensate; an optional density-preserving normalize can be added if you want shape-without-thinning.
- **On PASS:** I bake the approved profile values as defaults (toggle preserved) → CO-3. Mechanically: neutral
  no-op verified (terrain pixel-identical), `--shadowcheck` PASS r=0.835, no perf cost. Plan
  `plans/2026-06-20-clouds-co1-vertical-realism.md`.

### 9b. Clouds #2 · CO-2 types (stratus + cirrus) — ✅ PASS 2026-06-20 (eye-gate, live)
- **Verdict (user, live):** stratus gaps "good", cirrus parallax "looks good", cirrus variety "good". Gate PASSED.
  Iterations at the gate: broken-stratus gaps, world-anchored cirrus (parallax+drift), per-region variety; perf
  bug caught (10 fps naive → 133 fps reworked). Stratus = selectable type, cirrus = opt-in layer (default off).
- (orig build notes) Two new cloud TYPES, cumulus left byte-unchanged. Built ahead of CO-1's gate at your direction.
- **Stratus (overcast sheet):** `--clouds=1 --coverage=0.6 --stratus=1`, or Clouds tab **`type: cumulus↔stratus`**
  (0=cumulus, 1=stratus). **Judge:** reads as a flat connected overcast SHEET (not cumulus clumps)? Tune coverage/density.
- **Cirrus (high wind-streaked layer):** `--clouds=1 --coverage=0.3 --cirrus=0.6`, or Clouds tab **`cirrus layer (CO-2)`**
  toggle + `cirrus: coverage/density/wind dir/scale/sharpness`. **Judge:** believable high WIND-STREAKED filaments,
  thin/semi-transparent, fading at the horizon, warm near the sun, faded at night? Cumulus still composites over it.
- **Fast path:** **review key 6** now cycles cumulus(profile off→on) → stratus → cirrus — press 6 repeatedly.
- **Judge cumulus is untouched:** at the default (shape_mode 0, cirrus off) the approved cumulus look must reproduce.
- Mechanically: cumulus no-op (terrain pixel-identical); `--shadowcheck` PASS r=0.811 (stratus); cirrus appears
  (sky diff 2.0) + night-fades. Plan `plans/2026-06-20-clouds-co2-types.md`. **On PASS → CO-3 anti-repetition/horizon.**

### 10. Stars + Milky Way (galaxy) — ✅ REVIEWED 2026-06-20 (live) → ❌ NEEDS WORK → folded into Celestial #6 C1
- **Verdict (user, live, `--time=23 --celestial=1` = moonless dark, MW boosted):** needs work — "not terrible but
  just a fog band, and a band that goes all the way across in a half-circle." Reviewed → **no longer "owed."** Now
  **C1 of the Celestial expansion** (ROADMAP #6), **sequenced AFTER #3 GPU atmosphere (AT-1)** per the user.
- **Root cause (code):** `stars_layer()` in `cloud_sky.gdshader` — a **uniform great-circle band** (no along-band
  core → the uniform arc) + **smooth low-freq fbm + flat blue-white color + no embedded stars** (→ fog). C1 fix:
  galactic **core/bulge** + Great-Rift **dust lanes** + **resolved star clouds** + subtle **color** + **pulled-back**
  apparent scale; tunable + presets. (DECISIONS 2026-06-20.)
- **See it (for C1 when it's built):** `--time=23 --celestial=1` → Night tab `mw *` / `star *` knobs.

---

### 11. GPU atmosphere AT-1 + AT-2 aerial + AT-3 cloud-light — all eye-gated 2026-06-21 (arc COMPLETE)
- **AT-3 (physical cloud lighting):** ✅ **PASS 2026-06-21** (review **key 8** A/B, clouds tilted up at the deck). Clouds
  lit by the physical sky — warm undersides at dusk (horizon radiance), cooler tops (zenith), sun-transmittance reddened
  direct. User: "yep it works" → **default-ON**. **Pivoted A→param-handoff** (cross-node GPU LUT sampling crashed the RD;
  the 3 colors are per-frame constants → CPU readback + params). Effect real (cloud-band diff 1.66 vs 0.07 drift), warms
  at golden. Strength default 10 (gate-tunable). (DECISIONS 2026-06-21; commit 6d0d590.) **Owed:** a readback throttle
  for the running day/night cycle (per-frame CPU readback) → folded into ROADMAP #7 end-of-arc code-efficiency pass.
- **AT-2 (screen-space aerial perspective):** ✅ **soft-PASS 2026-06-21** (review **key 8** A/B, live). The audit's
  predicted gain mismatch was real — in-scatter is additive, so the planned strength (~2) WASHED the frame; recalibrated
  to **0.4** (gentle distance haze, warm sunset / blue noon, near stays crisp). User: "a little better than off" →
  default-ON subtle. Cost **+0.7 ms** (`--profmove`, 114 vs 124 fps). Self-checks PASS. (DECISIONS 2026-06-21; commits
  af07b5d/505ba07/d7474e6.) *Note: it's a marginal gain over the built-in fog — revisit if you later want it punchier
  (soften extinction so distant contrast survives) or default-off.*
- **AT-1 horizon flash — ✅ RESOLVED 2026-06-21 (in-motion re-confirm):** user flew the review key-8 horizon-framed view
  and confirmed the `dir.y≈0` line is clean ("didnt see it, looked good") — the flash/seam fix holds in motion, not just
  on stills. (AT-1 sky itself soft-passed off cycling presets 2026-06-20.)
- **Item 11 status:** both sub-items gated → effectively RESOLVED; left here as the record. **Owed:** update
  performance.md with the AT-2 +0.7 ms. **Unblocks:** Celestial C1 (the roadmap's next sky item).

---

## ⏸ Not "review" — build-when-you-can-see (eye-gated, parked)
These need your eye to *build*, not just approve — listed so they're not forgotten:
- **CDLOD terrain LOD (T1)** — the dominant remaining perf lever toward the **8 ms** target (mesh floor ~4 ms; also cuts SDFGI+shadow). Gate is pop-free-in-motion. Spec `specs/2026-06-18-terrain-lod-roadmap-design.md`.
- **SDFGI config / cheaper GI** (~2.9 ms intrinsic in motion) and **cloud cost** (~2 ms) — the #2/#3 perf levers; quality/eye calls.
- **Erosion E1** — coupled sim core, watched cutting live (the "is erosion good" gate). Spec `specs/2026-06-17-erosion-arc-design.md`.
