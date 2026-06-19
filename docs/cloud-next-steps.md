# WG16 Clouds — Next Steps (definitive)

Date: 2026-06-18. The cloud roadmap (#1–#6) is BUILT, each behind a toggle defaulting to the
validated look. This doc is the prioritized "what now": the user's feature-by-feature review,
the deferred view-space/texture decision, and the remaining eye-gated items — with the concrete
tuning levers an implementor needs. Companion: `docs/cloud-system-overview.md` (architecture +
toggle table), memory `cloud-look-real-rootcauses` (why the clouds read good now).

---

## A. Feature-by-feature review (DO THIS FIRST — user's eye, in motion)

Each feature ships behind a toggle that defaults to the validated baseline, so review one at a
time. For each: launch, flip the toggle, judge in motion (never a still). Likely tuning levers
listed so adjustments are quick.

1. **Per-deck lighting** — `--perdeck=0/1` (or `cloud_perdeck` 0..1). A=global, B=per-deck.
   - Verify with `--lightcheck` (prints cumulus-vs-cirrus luminance/silver/tint deltas) and
     `--deckdbg=1` (deck-ID overlay: cumulus=red, cirrus=cyan — confirms they occupy distinct sky).
   - Tuning: per-deck `phase_g/phase_iso/albedo/sun_absorb/tint_*` in `data/cloud_layers.json`
     (cirrus) and `CloudLayers.WithCumulusLighting` (cumulus, layer 0 — single source of truth).
2. **Presets → layer stack** — preset picker / `--preset=0..4`. Overcast authors a 3-deck stack
   (logs "layer stack set — 3 deck(s)"). Verify each preset reads as a distinct sky.
   - Tuning: `data/cloud_presets.json` (knob values + optional `layers` block per preset).
3. **Coherent randomize / ranged presets** — Clouds-tab Randomize. Should ALWAYS give a believable
   sky (preset + seeded jitter), never garbage. Tuning: jitter amount in `RandomizeClouds()` (0.18).
4. **God rays** — `--godrays=1` + `cloud_godray_strength`. Look toward the sun through gaps for
   shafts; confirm NO black wedges (the old FogVolume bug). Tuning: the `0.03` coefficient + the
   `exp(-od*3.0)` shadow sharpness + `pow(cosA,3)` gate in `cloud_raymarch.glsl`.
5. **Temporal amortization** — `--temporal=N` (or `cloud_temporal`). Perf lever. Watch for drift
   SHIMMER/stripes (stale texels). If acceptable, it buys cheaper frames / headroom for hi-res.
   Tuning: blend factor `mix(history,result,0.6)` and stride cap (16) in `cloud_raymarch.glsl`.
   If shimmer is unacceptable → that's the signal to do the view-space rewrite (§B) instead.
6. **Dome resolution** — `--cloudtex=256` (1024×256) vs default 512×128. Judge horizon blockiness
   vs the +2.6 ms cost (pair with temporal). If 512 is "blocky at the horizon" and 1024 is "too
   expensive" → view-space rewrite (§B).

**STOP clause (pillars):** if a feature can't be made to look good after a fair effort, surface
it — don't grind. Per-feature toggles make it cheap to ship one and shelve another.

---

## B. The texture / view-space decision (the deferred BIG item)

**Current:** the march writes a **lat-long dome texture** (az×el, 512×128 default), and
`cloud_sky.gdshader` samples it by EYEDIR. Decouples cloud cost from screen res; compresses
detail at the horizon (→ blockiness) and the whole dome is origin-ish anchored.

**`--cloudtex` (DONE) is the stopgap** — bump the dome res for a sharper horizon at 4× cost.

**The real long-term fix (NOT done — needs the user's decision + eye):** drop the lat-long dome
and **march in VIEW SPACE at half-resolution + TAA**. Why it's better: cloud detail tracks the
screen (no horizon compression), and half-res + temporal reconstruction keeps it cheap. Why it's
deferred: it's a real architectural change (new pass shape, TAA history/motion-vectors, integrate
with the existing premultiplied composite + the shadow map which stays world-XZ), and its
correctness is **artifact-driven** (ghosting, disocclusion, TAA smearing) — only judgeable in
motion. The original cloud-polish prompt explicitly said "discuss before that big a change."

**Decision gate:** do this ONLY if, after §A, the user finds (a) horizon blockiness at 512 is a
real problem AND (b) 1024×256 is too expensive for the mid-range budget AND (c) temporal
amortization on the dome shimmers too much to lean on. If all three → view-space is justified;
**brainstorm → spec → plan** it (it's its own arc, not a tweak). If not → `--cloudtex` + temporal
is sufficient and view-space is YAGNI for now.

What a view-space implementation touches (for scoping): a half-res screen-space raymarch target;
reprojection + TAA resolve (history buffer, motion from camera + cloud drift, disocclusion reject);
upscale/composite; keep `cloud_shadow.glsl` world-XZ (the ground shadow is unaffected); the
density field + lighting stay identical (reuse). Coupling guarantee unchanged (shadow is separate).

---

## C. Remaining eye-gated cloud items (after §A, smaller than §B)

- **Horizon / distant-sky handling** — the cloud band can read cut-off at the horizon (hard
  `rd.y` cutoffs in both the march and the sky shader; no density fade-to-haze, no convincing
  clear-sky falloff). Wants: soften the horizon cutoff + a believable non-cloud sky gradient so a
  sparse sky doesn't look truncated. Surfaced in the cloud refactor review; do as a focused pass.
- **Sun — FULL PASS (user-flagged 2026-06-18, wants it "look better + a cool shader").** Today
  `cloud_sky.gdshader` draws a flat bright circle + a tight `cos^220` glow, and (now) at the base
  un-dimmed brightness so coverage doesn't dim it. That's a stopgap; the sun deserves its own pass.
  Candidate "cool sun" features (its own brainstorm → spec): soft limb/disc with slight limb
  darkening; a proper multi-falloff atmospheric glow/corona (Mie-ish, wider warm halo near horizon);
  HDR bloom tuned so only the disc blooms (not the whole sky); sun-position-driven SKY tint
  (warm horizon at low sun = golden hour); optional lens flare/ghosts on a toggle; god-ray anchor
  (the in-march shafts should emanate from it); must keep occluding correctly behind clouds (dome
  composite) AND stay decoupled from the overcast dimming. Pairs with the future time-of-day system.
- **Snapshot cloud settings into the MOOD / time-of-day presets** — so the curated moods carry a
  matching cloud look (the moods already drive cloud sky-color; this would bind the full knob set).
- **Cloud-presence coherence** — overcast dim + aerial tint + reflections + mood cloud color, now
  that the look polish has landed (re-judge the whole atmosphere together).

---

## E. Deck vertical structure / meteorological realism (RESEARCH item — user-flagged 2026-06-18)

Observation (user, during review): real clouds within a type are NOT at one fixed altitude — they
have a vertical DISTRIBUTION; "each deck should have a range of elevations the clouds can be in."
Currently a deck is a flat slab: `altitude` (base) + `thickness`, so cloud BASES are a perfect
plane and the deck sits in one rigid band. The realism gap is real; this is a research + design
pass (not blocking — per-deck lighting itself works).

Meteorological grounding (to inform it):
- Altitude tiers: LOW <2 km (cumulus/stratus/stratocumulus), MID 2–7 km (alto-), HIGH 5–13 km
  (cirrus). Our decks ≈ these tiers — keep that mapping.
- Cumulus BASES are ~flat in a region (form at the lifting-condensation level ≈ constant) — so a
  flat base is partly correct; the missing realism is in the TOPS and the across-region variation.
- TOPS vary hugely: flat fair-weather cumulus → towering congestus → cumulonimbus spanning low→high.

Candidate levers (cheap; mostly shape, empty-skip absorbs the wider span — NOT a big perf cost):
1. **Base undulation** — modulate a deck's base by a low-freq noise (±100–300 m) so it's not a plane.
2. **Per-clump top/height variation** within the deck (towering vs flat clumps side by side).
3. **Weather-field-driven base offset per region** (add a base-height channel, like coverage/type
   already drive density/kind) → different regions sit at different heights.
4. **Vertical-development clouds** — a deck with large thickness + strong type→height so towers punch up.
5. Possibly a richer taxonomy: the 3 tiers × types as authored deck presets.
Approach when picked up: a short research pass (deep-research skill or meteorology refs) → brainstorm
→ spec, since it touches the density model in all 3 coupled shaders (keep `--shadowcheck` PASS).

## D. Health / regression guards (run after any density-affecting change)

- `--shadowcheck --coverage=0.5` → must PASS (Pearson r>0.6); proves the 3 density shaders stay
  byte-identical (the coupling guarantee). SATURATED at very high coverage is expected, not a fail.
- `--cloudstats` → dome coverage/alpha/luminance sanity (dome-averaged — divide by coverage for
  per-cloud values).
- `--lightcheck` → per-deck lighting still distinct after deck-param edits.
- `--profile=4` → frame budget (clouds ~1.3 ms default on the dev 5090; scale for mid-range).
- Build: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj`. Launch ALWAYS with the absolute
  `--path /c/Wg16/wg-16-project` (memory `wg16-launch-absolute-path`). One Godot at a time.
